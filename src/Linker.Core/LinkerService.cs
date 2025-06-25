﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Timers;
using EventStore.PositionRepository.Gprc;
using KurrentDB.Client;
using Microsoft.Extensions.Logging;
using EventTypeFilter = KurrentDB.Client.EventTypeFilter;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Linker.Core;

public class LinkerService : ILinkerService, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IPositionRepository _positionRepository;
    private readonly IStreamPositionFlusher _flusherForStreamPositions;
    private readonly IFilterService? _filterService;
    private readonly Settings _settings;
    private Position _lastPosition;
    private Position _originCurrentEnd = Position.Start;
    public string Name { get; }

    private readonly ILinkerConnectionBuilder _originConnectionBuilder;
    private readonly ILinkerConnectionBuilder _destinationConnectionBuilder;
    private KurrentDBClient? _originConnection;
    private KurrentDBClient? _destinationConnection;

    private Channel<BufferedEvent> _channel;
    private Task? _processingTask;

    private readonly System.Timers.Timer _timerForStats;

    private readonly LinkerHelper _replicaHelper;
    private CancellationTokenSource _cts = new();
    private Task? _subscriptionTask;
    private bool _started;

    private long _replicatedSinceLastStats;
    private long _replicatedTotal;

    private readonly double _currentBackpressureThreshold = 0.8;
    private readonly List<long> _latencySamples = [];
    private readonly List<long> _replicationSamples = [];
    private readonly Lock _adaptiveLock = new();
    private int _bufferSize;
    private int _adaptiveIntervalCounter;
    private readonly decimal _allowedIncreaseOrDecreaseAmount = 0.15m;
    private readonly double _significantRegressionToTriggerDecrease = -0.10;
    private readonly double _significantIncreaseToTriggerIncrease = -0.10;
    public const int MaxAllowedBuffer = 1000;
    public const int MinAllowedBuffer = 1;
    private const int StatsIntervalMs = 3000;

    private readonly ConcurrentDictionary<string, SortedDictionary<ulong, BufferedEvent>> _perStreamBuffers = new();
    private readonly ConcurrentDictionary<string, ulong?> _lastWrittenPerStream = new();
    private readonly HashSet<string> _streamsToBeExcluded;
    private readonly HashSet<string> _adjustedStartStreams = new();
    private readonly IAdjustedStreamRepository _adjustedStreamRepository;

    public LinkerService(ILinkerConnectionBuilder originBuilder, ILinkerConnectionBuilder destinationBuilder,
        IPositionRepository positionRepository, IFilterService? filterService, Settings settings, IAdjustedStreamRepository adjustedStreamRepository,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(LinkerService));
        Name = $"From-{originBuilder.ConnectionName}-To-{destinationBuilder.ConnectionName}";

        _originConnectionBuilder = originBuilder;
        _destinationConnectionBuilder = destinationBuilder;

        _positionRepository = positionRepository;
        _filterService = filterService;
        _settings = settings;
        _bufferSize = Math.Clamp(settings.BufferSize, MinAllowedBuffer, MaxAllowedBuffer);

        var streamPositionsPath = Path.Combine(settings.DataFolder, "positions", $"stream_positions_{Name}.json");
        _flusherForStreamPositions =
            new PeriodicStreamPositionFlusher(streamPositionsPath, loggerFactory.CreateLogger("Flusher"));
        _adjustedStreamRepository = adjustedStreamRepository;
        //_adjustedStreamsPath = Path.Combine(settings.DataFolder, "positions", $"adjusted_streams_{Name}.json");

        _replicaHelper = new LinkerHelper();

        _streamsToBeExcluded =
        [
            $"PositionStream-{_originConnectionBuilder.ConnectionName}",
            $"$$PositionStream-{_originConnectionBuilder.ConnectionName}"
        ];

        _timerForStats = new System.Timers.Timer(StatsIntervalMs);
        _timerForStats.Elapsed += TimerForStats_Elapsed;
    }

    private Channel<BufferedEvent> CreateNewChannel(int size)
    {
        return Channel.CreateBounded<BufferedEvent>(new BoundedChannelOptions(size)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });
    }

    private async Task ResizeChannelAsync(int newSize)
    {
        _logger.LogInformation($"{Name}: Resizing buffer from {_bufferSize} to {newSize}");

        await _cts.CancelAsync();
        _channel.Writer.TryComplete();

        if (_subscriptionTask is not null)
        {
            try { await _subscriptionTask; } catch (OperationCanceledException) { }
        }

        if (_processingTask is not null)
        {
            try { await _processingTask; } catch (OperationCanceledException) { }
        }

        var buffered = new List<BufferedEvent>();
        while (_channel.Reader.TryRead(out var evt))
            buffered.Add(evt);

        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _bufferSize = newSize;
        _channel = CreateNewChannel(_bufferSize);

        foreach (var evt in buffered)
            await _channel.Writer.WriteAsync(evt);

        StartWorkerTasks();
    }

    private void StartWorkerTasks()
    {
        _logger.LogDebug($"{Name}: Starting background tasks...");
        _processingTask = Task.Run(ProcessChannelEventsAsync);
        _subscriptionTask = SubscribeMeGrpc(_cts.Token);
    }

    public async Task<bool> StartAsync()
    {
        if (_started) return true;

        await StopAsync();

        if (!_positionRepository.TryGet(out _lastPosition))
            _lastPosition = Position.Start;

        _adjustedStartStreams.Clear();
        var loaded = await _adjustedStreamRepository.LoadAsync();
        foreach (var s in loaded)
            _adjustedStartStreams.Add(s);
        _logger.LogInformation($"{Name}: Loaded {_adjustedStartStreams.Count} adjusted start streams from disk");

        if (_lastPosition.Equals(Position.Start))
            _logger.LogInformation("No last position found. Starting from Position.Start");

        _destinationConnection = _destinationConnectionBuilder.Build();
        _originConnection = _originConnectionBuilder.Build();

        await UpdateOriginCurrentEndAsync();

        await _flusherForStreamPositions.StartAsync();

        if (_settings.EnableReconciliation)
            await ReconcileStreamPositions();
        else
            _logger.LogWarning($"{Name}: Reconciliation is disabled — startup will skip verifying stream write positions.");

        _started = true;

        _timerForStats.Start();

        _channel = CreateNewChannel(_bufferSize);

        StartWorkerTasks();

        _logger.LogInformation($"{Name} started.");

        return true;
    }

    private async Task ReconcileStreamPositions()
    {
        var streamPositions = await _flusherForStreamPositions.LoadAsync();
        int total = streamPositions.Count;
        int current = 0;

        var stopwatch = Stopwatch.StartNew();
        var lastLogTime = TimeSpan.Zero;

        foreach (var kvp in streamPositions)
        {
            current++;
            var streamId = kvp.Key;
            ulong recoveredPosition;

            try
            {
                var last = await GetLastEventFromAStreamAsync(streamId);
                if (last is { OriginalEvent: not null })
                {
                    recoveredPosition = last.Value.OriginalEventNumber.ToUInt64();
                }
                else
                {
                    recoveredPosition = 0;
                }
            }
            catch (Exception ex)
            {
                recoveredPosition = kvp.Value;

                var last = await GetLastEventFromAStreamAsync($"$${streamId}");
                if (last.HasValue)
                    continue; // stream has been deleted

                _logger.LogWarning(ex, $"{Name}: Could not read {streamId} from destination. Using fallback value {kvp.Value}");
            }

            _lastWrittenPerStream[streamId] = recoveredPosition;
            _flusherForStreamPositions.Update(streamId, recoveredPosition);

            if (recoveredPosition > 0)
                await AddToAdjustedStreams(streamId);

            if (stopwatch.Elapsed - lastLogTime >= TimeSpan.FromSeconds(5))
            {
                lastLogTime = stopwatch.Elapsed;
                _logger.LogInformation($"{Name}: Reconciliation progress {current}/{total} streams ({(current * 100 / total)}%)");
            }
        }

        _logger.LogInformation($"{Name}: ReconcileStreamPositions completed. Total streams reconciliations processed: {total}");
    }

    public async Task<bool> StopAsync()
    {
        if (!_started)
            return true;

        await _cts.CancelAsync();
        _timerForStats.Stop();
        _channel.Writer.TryComplete();

        if (_subscriptionTask is not null)
        {
            try { await _subscriptionTask; } catch (OperationCanceledException) { }
        }

        if (_processingTask is not null)
        {
            try { await _processingTask; } catch (OperationCanceledException) { }
            _processingTask = null;
        }

        await _flusherForStreamPositions.StopAsync();
        
        await DisposeConnectionsAsync();

        _started = false;
        _logger.LogInformation($"{Name} stopped.");
        return true;
    }

    private async Task DisposeConnectionsAsync()
    {
        if (_originConnection != null)
            await _originConnection.DisposeAsync();
        if (_destinationConnection != null)
            await _destinationConnection.DisposeAsync();
    }

    private async Task UpdateOriginCurrentEndAsync()
    {
        if (_originConnection == null)
            return;

        try
        {
            var result = _originConnection.ReadAllAsync(Direction.Backwards, Position.End, maxCount: 1, resolveLinkTos: false, cancellationToken: _cts.Token);
            await foreach (var evt in result.WithCancellation(_cts.Token))
            {
                if (evt.OriginalPosition.HasValue)
                {
                    _originCurrentEnd = evt.OriginalPosition.Value;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"{Name}: Failed to update origin current end position.");
        }
    }

    private async Task ProcessChannelEventsAsync()
    {
        _logger.LogDebug($"{Name}: Channel processing started");
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                _logger.LogTrace($"{Name}: Dequeued {evt.EventNumber}@{evt.StreamId}");
                await ProcessBufferedEvent(evt);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // "Specified argument was out of the range of valid values. (Parameter 'eventStreamId')"
            _logger.LogError(ex, $"{Name}: Channel processing error: {ex.GetBaseException().Message}");
            await RestartServiceAsync();
        }
    }

    private async Task ProcessBufferedEvent(BufferedEvent evt)
    {
        var streamId = evt.StreamId;
        var eventNum = evt.EventNumber.ToUInt64();

        var buffer = _perStreamBuffers.GetOrAdd(streamId, _ => new SortedDictionary<ulong, BufferedEvent>());
        buffer[eventNum] = evt;

        ulong expected;
        if (_lastWrittenPerStream.TryGetValue(streamId, out var lastWritten))
            expected = (lastWritten.HasValue ? lastWritten.Value + 1 : 0);
        else
            expected = 0;

        _logger.LogTrace($"{Name}: LastWritten={(lastWritten.HasValue ? lastWritten.Value.ToString() : "null")}, Expected={expected}, BufferCount={buffer.Count}");
        _logger.LogTrace($"{Name}: Buffer keys: {string.Join(",", buffer.Keys)}");
        
        if (!buffer.ContainsKey(expected))
            _logger.LogDebug($"{Name}: Missing expected event {expected}@{streamId}, can't append");

        while (buffer.TryGetValue(expected, out var next))
        {
            await AppendEventAsync(next);
            _lastWrittenPerStream[streamId] = expected;
            _flusherForStreamPositions.Update(streamId, expected);
            if (expected == 0)
                await _flusherForStreamPositions.FlushAsync();
            buffer.Remove(expected);
            expected++;
        }

        if (buffer.Count == 0)
            _perStreamBuffers.TryRemove(streamId, out _);
    }

    private async Task AddToAdjustedStreams(string streamId)
    {
        var updated = _adjustedStartStreams.Add(streamId) ||
                      !streamId.StartsWith("$$") && _adjustedStartStreams.Add("$$" + streamId);

        if (updated)
            await _adjustedStreamRepository.SaveAsync(_adjustedStartStreams);
    }

    private async Task SubscribeMeGrpc(CancellationToken ctsToken)
    {
        if (_originConnection == null)
            throw new Exception("Origin connection is not initialized");
        await using var subscription = _originConnection.SubscribeToAll(start: FromAll.After(_lastPosition), cancellationToken: ctsToken,
            resolveLinkTos: _settings.ResolveLinkTos, filterOptions: new SubscriptionFilterOptions(EventTypeFilter.RegularExpression(@"^(\$metadata|[^\$].*)")));
        await foreach (var message in subscription.Messages.WithCancellation(ctsToken))
        {
            var currentBuffer = _channel.Reader.Count;
            var threshold = _currentBackpressureThreshold;

            if (currentBuffer >= _bufferSize * threshold)
            {
                _logger.LogDebug($"{Name}: Backpressure active. Buffer={currentBuffer}/{_bufferSize}, Threshold={threshold:P0}");
                await Task.Delay(100, ctsToken);
            }

            if (message is StreamMessage.Event(var evnt))
                await HandleEventAsync(evnt);
        }
    }

    private async Task HandleEventAsync(ResolvedEvent evt)
    {
        if (evt.Event == null || !evt.OriginalPosition.HasValue)
            return;

        var metadata = _replicaHelper.DeserializeObject(evt.Event.Metadata);

        if (!_replicaHelper.IsValidForReplica(evt.Event.EventType, evt.Event.EventStreamId, evt.OriginalPosition.Value,
                _positionRepository.PositionEventType, _filterService, _streamsToBeExcluded, metadata,
                _destinationConnectionBuilder.ConnectionName))
        {
            _logger.LogDebug(
                "{Name}: Skipping event {EventNumber}@{StreamId} - Not valid for replication",
                Name, evt.OriginalEventNumber, evt.OriginalStreamId);
            return;
        }

        if (!_replicaHelper.TryProcessMetadata(evt.Event.EventStreamId, evt.Event.EventNumber, evt.Event.Created,
                _originConnectionBuilder.ConnectionName, metadata, out var enrichedMetadata))
        {
            _logger.LogDebug($"{Name}: Updated global last position to {_lastPosition}");
            return;
        }

        var streamId = evt.Event.EventStreamId;
        var eventNumber = evt.Event.EventNumber.ToUInt64();

        await EnsureStreamTrackingAsync(evt, streamId,
            eventNumber);

        if (_lastWrittenPerStream.TryGetValue(streamId, out var lastWritten) && lastWritten.HasValue && eventNumber <= lastWritten.Value)
        {
            _logger.LogDebug("{Name}: Skipping already replicated event {EventNumber}@{StreamId}",
                Name, evt.Event.EventNumber, streamId);
            return;
        }

        var bufferedEvent = new BufferedEvent(
            streamId,
            evt.Event.EventNumber,
            evt.OriginalPosition.Value,
            new EventData(
                evt.Event.EventId,
                evt.Event.EventType,
                evt.Event.Data,
                _replicaHelper.SerializeObject(enrichedMetadata)),
            evt.Event.Created);

        await _channel.Writer.WriteAsync(bufferedEvent, _cts.Token);
        _logger.LogDebug("{Name} Received event {EventNumber}@{StreamId}", Name, bufferedEvent.EventNumber, streamId);
    }

    private async Task EnsureStreamTrackingAsync(ResolvedEvent evt, string streamId, ulong eventNumber)
    {
        if (_lastWrittenPerStream.ContainsKey(streamId))
        {
            _logger.LogDebug("{Name}: Stream {StreamId} already tracked. Skipping initialization.", Name, streamId);
            return;
        }

        var lastInDest = await GetLastEventFromAStreamAsync(streamId);
        if (lastInDest != null)
        {
            var lastEventNumber = lastInDest.Value.OriginalEventNumber.ToUInt64();
            _lastWrittenPerStream[streamId] = lastEventNumber;
            _flusherForStreamPositions.Update(streamId, lastEventNumber);
            _logger.LogDebug("{Name}: Initialized stream {StreamId} with existing event #{LastEventNumber}",
                Name, streamId, lastEventNumber);
        }
        else if (eventNumber == 0)
        {
            _lastWrittenPerStream[streamId] = null; // Mark as ready but not written yet
            _logger.LogDebug("{Name}: Initialized empty stream {StreamId}, ready to replicate event 0", Name, streamId);
        }
        else if (_adjustedStartStreams.Contains(streamId))
        {
            _lastWrittenPerStream[streamId] = eventNumber - 1;
            _flusherForStreamPositions.Update(streamId, eventNumber - 1);
            _logger.LogDebug("{Name}: Adjusted start for stream {StreamId}, tracking starts at {EventNumber}",
                Name, streamId, eventNumber - 1);
        }
        else
        {
            _logger.LogTrace("{Name}: Skipping event {EventNumber}@{StreamId} - not initialized and no adjusted start",
                Name, eventNumber, streamId);
        }
    }

    private async Task AppendEventAsync(BufferedEvent evt)
    {
        if (_destinationConnection == null)
            throw new InvalidOperationException("Destination connection is not initialized");

        var sw = Stopwatch.StartNew();
        ulong eventNumber = evt.EventNumber.ToUInt64();
        StreamState expectedRevision;

        try
        {
            var readResult = _destinationConnection.ReadStreamAsync(Direction.Backwards, evt.StreamId, StreamPosition.End, 1);
            ResolvedEvent? lastEvent = null;
            await foreach (var e in readResult)
            {
                lastEvent = e;
                break;
            }

            if (lastEvent is not null)
            {
                var lastEventNumber = lastEvent.Value.OriginalEventNumber.ToUInt64();
                if (eventNumber == lastEventNumber + 1)
                {
                    expectedRevision = StreamState.StreamRevision(lastEventNumber);
                }
                else if (_adjustedStartStreams.Contains(evt.StreamId))
                {
                    _logger.LogWarning($"{Name}: Skipping gap in adjusted stream {evt.StreamId}. Got {eventNumber}, expected {lastEventNumber + 1}");
                    await UpdateStreamTrackingAsync(evt);
                    return;
                }
                else if (evt.StreamId.StartsWith("$$"))
                {
                    _logger.LogInformation($"{Name}: Skipping out-of-order system event {eventNumber}@{evt.StreamId} (last was {lastEventNumber})");
                    await UpdateStreamTrackingAsync(evt);
                    return;
                }
                else
                {
                    throw new InvalidOperationException($"Out-of-order event detected in {evt.StreamId}. Expected {lastEventNumber + 1}, got {eventNumber}.");
                }
            }
            else if (_adjustedStartStreams.Contains(evt.StreamId))
            {
                _logger.LogInformation($"{Name}: Stream {evt.StreamId} is empty but adjusted start is enabled — using StreamState.NoStream for event {eventNumber}");
                expectedRevision = StreamState.NoStream;
            }
            else
            {
                if (await IsMaxAgeOrMaxCountStream(evt.StreamId))
                {
                    _logger.LogInformation($"{Name}: Using StreamState.Any for {evt.StreamId} due to $maxCount/$maxAge settings");
                    expectedRevision = StreamState.Any;
                }
                else
                {
                    var isAdjusted = _adjustedStartStreams.Contains(evt.StreamId);
                    _lastWrittenPerStream.TryGetValue(evt.StreamId, out var lastWritten);

                    _logger.LogWarning($"{Name}: Debug — about to append event {eventNumber}@{evt.StreamId}, adjusted={isAdjusted}, lastWritten={lastWritten}");
                    _logger.LogError($"{Name}: Cannot append event {eventNumber} to empty stream {evt.StreamId}. Adjusted? {isAdjusted}");
                    throw new InvalidOperationException($"Cannot append event {eventNumber} to empty stream.");
                }
            }
        }
        catch (StreamNotFoundException)
        {
            if (eventNumber == 0)
            {
                expectedRevision = StreamState.NoStream;
            }
            else if (_adjustedStartStreams.Contains(evt.StreamId))
            {
                _logger.LogDebug($"{Name}: Stream {evt.StreamId} missing but start adjusted — using StreamState.NoStream for event {eventNumber}");
                expectedRevision = StreamState.NoStream;
            }
            else if (await IsTombstonedStream(evt.StreamId))
            {
                _logger.LogWarning($"{Name}: Stream {evt.StreamId} is soft deleted. Using NoStream for re-append.");
                await AddToAdjustedStreams(evt.StreamId); 
                expectedRevision = StreamState.NoStream;
            }
            else
            {
                throw new InvalidOperationException($"Event {eventNumber} cannot be appended to missing stream.");
            }
        }
        if (expectedRevision == StreamState.Any)
        {
            _logger.LogError($"{Name}: Refusing to append with {nameof(StreamState.Any)} to stream {evt.StreamId} (event #{evt.EventNumber}) — this is unsafe for replication.");
            throw new InvalidOperationException("Unsafe replication state: StreamState.Any should not be used during replication.");
        }

        try
        {
            await _destinationConnection.AppendToStreamAsync(evt.StreamId, expectedRevision, [evt.EventData]);
            _lastPosition = evt.OriginalPosition;
            _positionRepository.Set(_lastPosition);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("eventStreamId"))
        {
            _logger.LogError(ex, $"{Name}: Skipping event due to invalid stream ID. Stream={evt.StreamId}, EventNumber={evt.EventNumber}");
            await UpdateStreamTrackingAsync(evt);
            return;
        }
        catch (WrongExpectedVersionException ex)
        {
            if (!_settings.HandleConflicts || !await HandleConflictAsync(evt, ex))
            {
                _logger.LogError($"Conflict handling failed: Stream: {evt.StreamId}, EventNumber: {evt.EventNumber}, Error: {ex.Message}");
                throw new InvalidOperationException("Replication halted due to unrecoverable conflict error.");
            }

            await UpdateStreamTrackingAsync(evt);
            return;
        }

        sw.Stop();

        lock (_latencySamples)
        {
            _latencySamples.Add(sw.ElapsedMilliseconds);
            if (_latencySamples.Count > 100) _latencySamples.RemoveAt(0);
        }

        await UpdateStreamTrackingAsync(evt);
    }

    private async Task<bool> IsTombstonedStream(string streamId)
    {
        try
        {
            var original = await _destinationConnection.GetStreamMetadataAsync(streamId);
            // soft deleted
            var deletedStream = await _destinationConnection.GetStreamMetadataAsync($"$${streamId}");
            return true;
        }
        catch (StreamDeletedException) 
        {
            // TODO test if we end up here for hard deleted streams
            // and eventually return handle it differently to keep the hard deleted behaviour in the destination
            return true;
        }
        catch (StreamNotFoundException)
        {
            return false;
        }
    }

    private async Task<bool> IsMaxAgeOrMaxCountStream(string streamId)
    {
        if (_originConnection == null)
            return false;

        try
        {
            var metadataResult = await _originConnection.GetStreamMetadataAsync(streamId, null, null, _cts.Token);
            var metadata = metadataResult.Metadata;
            if (metadata.MaxCount.HasValue || metadata.MaxAge.HasValue)
            {
                return true;
            }
        }
        catch (StreamNotFoundException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"{Name}: Failed to check stream metadata for {streamId}");
        }

        return false;
    }

    private async Task UpdateStreamTrackingAsync(BufferedEvent evt)
    {
        var streamId = evt.StreamId;
        var eventNumber = evt.EventNumber.ToUInt64();

        _lastWrittenPerStream[streamId] = eventNumber;
        _flusherForStreamPositions.Update(streamId, eventNumber);

        Interlocked.Increment(ref _replicatedSinceLastStats);
        Interlocked.Increment(ref _replicatedTotal);

        if (_adjustedStartStreams.Remove(streamId))
            await _adjustedStreamRepository.SaveAsync(_adjustedStartStreams);
    }

    private async Task<ResolvedEvent?> GetLastEventFromAStreamAsync(string streamId)
    {
        if (_destinationConnection == null)
            throw new InvalidOperationException("Destination connection not initialised yet.");
        try
        {
            var result = _destinationConnection.ReadStreamAsync(
                Direction.Backwards,
                streamId,
                StreamPosition.End,
                maxCount: 1,
                resolveLinkTos: false,
                cancellationToken: _cts.Token
            );

            await foreach (var evt in result)
                return evt;

            return null;
        }
        catch (StreamNotFoundException)
        {
            _logger.LogDebug($"{Name}: Stream {streamId} not found in destination - assuming it doesn't exist yet.");
            return null;
        }
    }

    private async Task<bool> HandleConflictAsync(BufferedEvent evt, WrongExpectedVersionException ex)
    {
        if (_destinationConnection == null)
            throw new Exception("Destination connection is not initialized");

        var conflictStreamId = _settings.HandleConflicts
            ? $"$conflicts-from-{_originConnectionBuilder.ConnectionName}-to-{_destinationConnectionBuilder.ConnectionName}"
            : evt.StreamId;

        var metadata = _replicaHelper.DeserializeObject(evt.EventData.Metadata);
        metadata["$error"] = ex.GetBaseException().Message;

        try
        {
            await _destinationConnection.AppendToStreamAsync(conflictStreamId, StreamState.Any, new[]
            {
                new EventData(evt.EventData.EventId, evt.EventData.Type, evt.EventData.Data,
                    _replicaHelper.SerializeObject(metadata))
            });
            Interlocked.Increment(ref _replicatedSinceLastStats);
            Interlocked.Increment(ref _replicatedTotal);
            _lastPosition = evt.OriginalPosition;
            _positionRepository.Set(_lastPosition);
            return true;
        }
        catch (Exception innerEx)
        {
            _logger.LogError(innerEx, $"Conflict append failed: {innerEx.Message}");
            return false;
        }
    }

    private void TimerForStats_Elapsed(object? _, ElapsedEventArgs e)
    {
        var replicatedThisInterval = Interlocked.Exchange(ref _replicatedSinceLastStats, 0);
        var totalReplicated = Interlocked.Read(ref _replicatedTotal);
        var bufferSize = _channel.Reader.Count;
        var bufferRatio = (double)bufferSize / _bufferSize;

        long avgLatency;
        lock (_latencySamples)
        {
            avgLatency = _latencySamples.Count > 0 ? (long)_latencySamples.Average() : 0;
        }

        double progressPercent = 0;
        if (_originCurrentEnd > Position.Start && _lastPosition > Position.Start)
        {
            var origin = _originCurrentEnd.CommitPosition + _originCurrentEnd.PreparePosition;
            var current = _lastPosition.CommitPosition + _lastPosition.PreparePosition;
            if (origin > 0)
                progressPercent = Math.Min(100, (double)current / origin * 100);
        }

        lock (_adaptiveLock)
        {
            _replicationSamples.Add(replicatedThisInterval);
            if (_replicationSamples.Count > 5)
                _replicationSamples.RemoveAt(0);

            _adaptiveIntervalCounter++;

            if (_adaptiveIntervalCounter >= 5)
            {
                _adaptiveIntervalCounter = 0;
                var average = _replicationSamples.Average();
                var previous = _replicationSamples.Take(4).Average();

                if (average == 0 && previous == 0)
                {
                    _logger.LogInformation($"{Name} adaptive tuning skipped: no replication activity. Current BufferSize={_bufferSize}");
                    return;
                }

                var percentChange = previous == 0 ? 1 : (average - previous) / previous;
                var proposed = _bufferSize;

                if (_settings.AutomaticTuning)
                {
                    var delta = Math.Max(1, (int)Math.Round(_bufferSize * _allowedIncreaseOrDecreaseAmount));

                    if (percentChange >= _significantIncreaseToTriggerIncrease)
                    {
                        // Steady or improving: increase buffer
                        proposed = Math.Min(MaxAllowedBuffer, _bufferSize + delta);
                    }
                    else if (percentChange < _significantRegressionToTriggerDecrease)
                    {
                        // Significant regression: decrease buffer
                        proposed = Math.Max(MinAllowedBuffer, _bufferSize - delta);
                    }

                    if (proposed != _bufferSize)
                        _ = ResizeChannelAsync(proposed);

                    _logger.LogInformation($"{Name} adaptive tuning: prevAvg={previous:F1}, currentAvg={average:F1}, proposed bufferSize={proposed}, change={percentChange:P1}");
                }
                else
                    _logger.LogInformation($"{Name} prevAvg={previous:F1}, currentAvg={average:F1}, bufferSize={proposed}, change={percentChange:P1}");
            }
        }

        _logger.LogInformation(
            $"{Name} stats: replicated {replicatedThisInterval} events, total: {totalReplicated}, buffer: {bufferSize}/{_bufferSize} ({bufferRatio:P0}), latency: {avgLatency}ms, progress: {progressPercent:F1}%");
    }

    private async Task RestartServiceAsync()
    {
        _logger.LogWarning($"{Name} restarting after error...");
        await Task.Delay(1000);
        await StopAsync();
        await StartAsync();
    }

    public IDictionary<string, dynamic> GetStats() => new Dictionary<string, dynamic>
    {
        ["serviceType"] = "crossReplica",
        ["from"] = _originConnectionBuilder.ConnectionName,
        ["to"] = _destinationConnectionBuilder.ConnectionName,
        ["isRunning"] = _started,
        ["lastPosition"] = _lastPosition,
        ["bufferedEvents"] = _channel.Reader.Count,
        ["replicatedTotal"] = Interlocked.Read(ref _replicatedTotal)
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _timerForStats.Dispose();
    }
}