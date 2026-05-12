using System.Diagnostics;

namespace jkcnsl_cache;

public sealed class MetricsService : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private const int MaxSamples = 180;

    private readonly object _lock = new();
    private readonly Queue<MetricsSample> _samples = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.UtcNow;
    private long _lastPosts;
    private long _lastPostOk;
    private long _lastPostFailed;
    private long _lastPostTooFast;
    private long _lastPostTooLong;
    private long _lastDelivered;
    private long _lastRejects;
    private long _lastChannelRejects;
    private long _lastTotalRejects;
    private long _lastQueueDrops;
    private long _lastQueueDisconnects;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;

    private long _commentConnections;
    private long _watchConnections;
    private long _posts;
    private long _postOk;
    private long _postFailed;
    private long _postTooFast;
    private long _postTooLong;
    private long _delivered;
    private long _channelRejects;
    private long _totalRejects;
    private long _queueDrops;
    private long _queueDisconnects;

    public long CommentConnections => Interlocked.Read(ref _commentConnections);
    public long WatchConnections => Interlocked.Read(ref _watchConnections);
    public long Posts => Interlocked.Read(ref _posts);
    public long PostOk => Interlocked.Read(ref _postOk);
    public long PostFailed => Interlocked.Read(ref _postFailed);
    public long Delivered => Interlocked.Read(ref _delivered);
    public long Rejects => Interlocked.Read(ref _channelRejects) + Interlocked.Read(ref _totalRejects);

    public void CommentConnected() => Interlocked.Increment(ref _commentConnections);
    public void CommentDisconnected() => Interlocked.Decrement(ref _commentConnections);
    public void WatchConnected() => Interlocked.Increment(ref _watchConnections);
    public void WatchDisconnected() => Interlocked.Decrement(ref _watchConnections);
    public void RecordDelivered(long count = 1) => Interlocked.Add(ref _delivered, count);
    public void RecordChannelLimitReject() => Interlocked.Increment(ref _channelRejects);
    public void RecordTotalLimitReject() => Interlocked.Increment(ref _totalRejects);
    public void RecordQueueDrop(int dropped) => Interlocked.Add(ref _queueDrops, Math.Max(0, dropped));
    public void RecordQueueDisconnect() => Interlocked.Increment(ref _queueDisconnects);

    public void RecordPostAttempt() => Interlocked.Increment(ref _posts);

    public void RecordPostResult(string? code)
    {
        if (string.IsNullOrEmpty(code) || string.Equals(code, "OK", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _postOk);
            return;
        }

        Interlocked.Increment(ref _postFailed);
        if (string.Equals(code, "POST_TOO_FAST", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _postTooFast);
        else if (string.Equals(code, "COMMENT_TOO_LONG", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _postTooLong);
    }

    public MetricsPayload CreatePayload()
    {
        lock (_lock)
        {
            var samples = _samples.ToArray();
            var latest = samples.LastOrDefault() ?? CreateCurrentOnlySample(DateTimeOffset.UtcNow);
            return new MetricsPayload(
                latest,
                samples,
                new MetricsTotals(
                    CommentConnections,
                    WatchConnections,
                    Posts,
                    PostOk,
                    PostFailed,
                    Interlocked.Read(ref _postTooFast),
                    Interlocked.Read(ref _postTooLong),
                    Delivered,
                    Interlocked.Read(ref _channelRejects),
                    Interlocked.Read(ref _totalRejects),
                    Interlocked.Read(ref _queueDrops),
                    Interlocked.Read(ref _queueDisconnects)));
        }
    }

    private MetricsSample CreateCurrentOnlySample(DateTimeOffset now)
    {
        _process.Refresh();
        return new MetricsSample(
            now,
            CommentConnections,
            WatchConnections,
            CommentConnections + WatchConnections,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Math.Round(_process.WorkingSet64 / 1024d / 1024d, 1),
            Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
            0,
            0,
            0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastCpuTime = _process.TotalProcessorTime;
        _lastGc0 = GC.CollectionCount(0);
        _lastGc1 = GC.CollectionCount(1);
        _lastGc2 = GC.CollectionCount(2);
        AddSample(CreateSample(DateTimeOffset.UtcNow));

        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                AddSample(CreateSample(DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) { }
    }

    private MetricsSample CreateSample(DateTimeOffset now)
    {
        _process.Refresh();
        var elapsed = Math.Max(0.001, (now - _lastSampleAt).TotalSeconds);
        var cpuTime = _process.TotalProcessorTime;
        var cpuPercent = Math.Clamp(
            (cpuTime - _lastCpuTime).TotalMilliseconds / (elapsed * 1000 * Environment.ProcessorCount) * 100,
            0,
            100);
        _lastCpuTime = cpuTime;
        _lastSampleAt = now;

        var posts = Posts;
        var postOk = PostOk;
        var postFailed = PostFailed;
        var postTooFast = Interlocked.Read(ref _postTooFast);
        var postTooLong = Interlocked.Read(ref _postTooLong);
        var delivered = Delivered;
        var channelRejects = Interlocked.Read(ref _channelRejects);
        var totalRejects = Interlocked.Read(ref _totalRejects);
        var rejects = channelRejects + totalRejects;
        var queueDrops = Interlocked.Read(ref _queueDrops);
        var queueDisconnects = Interlocked.Read(ref _queueDisconnects);
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);

        var sample = new MetricsSample(
            now,
            CommentConnections,
            WatchConnections,
            CommentConnections + WatchConnections,
            posts - _lastPosts,
            postOk - _lastPostOk,
            postFailed - _lastPostFailed,
            postTooFast - _lastPostTooFast,
            postTooLong - _lastPostTooLong,
            delivered - _lastDelivered,
            rejects - _lastRejects,
            channelRejects - _lastChannelRejects,
            totalRejects - _lastTotalRejects,
            queueDrops - _lastQueueDrops,
            queueDisconnects - _lastQueueDisconnects,
            Math.Round(cpuPercent, 2),
            Math.Round(_process.WorkingSet64 / 1024d / 1024d, 1),
            Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
            gc0 - _lastGc0,
            gc1 - _lastGc1,
            gc2 - _lastGc2);

        _lastPosts = posts;
        _lastPostOk = postOk;
        _lastPostFailed = postFailed;
        _lastPostTooFast = postTooFast;
        _lastPostTooLong = postTooLong;
        _lastDelivered = delivered;
        _lastRejects = rejects;
        _lastChannelRejects = channelRejects;
        _lastTotalRejects = totalRejects;
        _lastQueueDrops = queueDrops;
        _lastQueueDisconnects = queueDisconnects;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
        return sample;
    }

    private void AddSample(MetricsSample sample)
    {
        lock (_lock)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > MaxSamples)
                _samples.Dequeue();
        }
    }
}

public sealed record MetricsPayload(MetricsSample Now, MetricsSample[] Series, MetricsTotals Totals);

public sealed record MetricsTotals(
    long CommentConnections,
    long WatchConnections,
    long Posts,
    long PostOk,
    long PostFailed,
    long PostTooFast,
    long PostTooLong,
    long Delivered,
    long ChannelRejects,
    long TotalRejects,
    long QueueDrops,
    long QueueDisconnects);

public sealed record MetricsSample(
    DateTimeOffset Time,
    long CommentConnections,
    long WatchConnections,
    long TotalConnections,
    long Posts,
    long PostOk,
    long PostFailed,
    long PostTooFast,
    long PostTooLong,
    long Delivered,
    long Rejects,
    long ChannelRejects,
    long TotalRejects,
    long QueueDrops,
    long QueueDisconnects,
    double ProcessCpuPercent,
    double WorkingSetMB,
    double ManagedMemoryMB,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);
