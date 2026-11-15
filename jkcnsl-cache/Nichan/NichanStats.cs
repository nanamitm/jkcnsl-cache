namespace jkcnsl_cache.Nichan;

// 5ch へのリクエストの集計（ステータスページの「5ch」タブ用）。
// 全体の累計・5 秒ごとの推移（MetricsService と同じ 15 分ぶん）・ホストごとの累計を持つ。
// 推移はバックグラウンドで標本化せず、記録時に該当する 5 秒枠へ加算し、読み出し時に空き枠を 0 で埋める。
public sealed class NichanStats
{
    public static readonly TimeSpan BucketSize = TimeSpan.FromSeconds(5);
    private const int MaxBuckets = 180;

    private readonly object _lock = new();
    private readonly Dictionary<string, HostCounters> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<Bucket> _buckets = new();
    private readonly Counters _totals = new();

    public void RecordResponse(string host, int statusCode, long bytes)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var hostCounters = GetHost(host);
            hostCounters.LastStatus = statusCode;
            hostCounters.LastRequestAt = now;
            foreach (var c in new[] { _totals, hostCounters.Counters, CurrentBucket(now).Counters })
            {
                c.Requests++;
                c.Bytes += Math.Max(0, bytes);
                if (statusCode == 304) c.NotModified++;
                if (statusCode is 429 or 503) c.RateLimited++;
                else if (statusCode >= 400) c.Errors++;
            }
        }
    }

    public void RecordNetworkError(string host)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var hostCounters = GetHost(host);
            hostCounters.LastStatus = 0;
            hostCounters.LastRequestAt = now;
            foreach (var c in new[] { _totals, hostCounters.Counters, CurrentBucket(now).Counters })
            {
                c.Requests++;
                c.Errors++;
            }
        }
    }

    // 停止中のホストへのリクエストを送らずに見送った回数
    public void RecordSkippedBlocked(string host)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            foreach (var c in new[] { _totals, GetHost(host).Counters, CurrentBucket(now).Counters })
                c.SkippedBlocked++;
        }
    }

    public (NichanCountersDto Totals, NichanSampleDto[] Series, NichanHostDto[] Hosts) Snapshot(
        Func<string, DateTimeOffset?> blockedUntil)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            // 最新の枠まで 0 で埋めて、グラフの右端が現在時刻になるようにする
            CurrentBucket(now);
            var series = _buckets.Select(b => new NichanSampleDto(b.Start, b.Counters.ToDto())).ToArray();
            var hosts = _hosts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new NichanHostDto(kv.Key, kv.Value.Counters.ToDto(),
                    kv.Value.LastStatus, kv.Value.LastRequestAt, blockedUntil(kv.Key)))
                .ToArray();
            return (_totals.ToDto(), series, hosts);
        }
    }

    private HostCounters GetHost(string host)
    {
        if (!_hosts.TryGetValue(host, out var counters))
            _hosts[host] = counters = new HostCounters();
        return counters;
    }

    private Bucket CurrentBucket(DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.UtcTicks - now.UtcTicks % BucketSize.Ticks, TimeSpan.Zero);
        if (_buckets.Count == 0)
        {
            var first = new Bucket(start);
            _buckets.Enqueue(first);
            return first;
        }
        var last = _buckets.Last();
        if (last.Start >= start)
            return last;
        // 間が空いた枠を 0 で埋める（長時間空いた場合は保持数ぶんだけ）
        var next = last.Start + BucketSize;
        if (start - next > BucketSize * MaxBuckets)
            next = start - BucketSize * (MaxBuckets - 1);
        var bucket = last;
        for (; next <= start; next += BucketSize)
        {
            bucket = new Bucket(next);
            _buckets.Enqueue(bucket);
        }
        while (_buckets.Count > MaxBuckets)
            _buckets.Dequeue();
        return bucket;
    }

    private sealed class Counters
    {
        public long Requests, Errors, NotModified, RateLimited, SkippedBlocked, Bytes;
        public NichanCountersDto ToDto() => new(Requests, Errors, NotModified, RateLimited, SkippedBlocked, Bytes);
    }

    private sealed class HostCounters
    {
        public readonly Counters Counters = new();
        public int LastStatus;
        public DateTimeOffset? LastRequestAt;
    }

    private sealed class Bucket(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;
        public Counters Counters { get; } = new();
    }
}

public sealed record NichanCountersDto(
    long Requests, long Errors, long NotModified, long RateLimited, long SkippedBlocked, long Bytes);

public sealed record NichanSampleDto(DateTimeOffset Time, NichanCountersDto Counters);

public sealed record NichanHostDto(
    string Host, NichanCountersDto Counters, int LastStatus, DateTimeOffset? LastRequestAt, DateTimeOffset? BlockedUntil);

public sealed record NichanThreadStatusDto(
    string Title, string Url, int ResCount, bool Finished, bool Dropping, string? Error,
    DateTimeOffset? RetryAt, DateTimeOffset? LastFetchAt, DateTimeOffset? LastNewResAt, long Queued);

public sealed record NichanChannelStatusDto(
    string Key, string Status, string? StatusText, bool Running, int Clients, long Delivered, int Force,
    int Pending, NichanThreadStatusDto[] Threads);

public sealed record NichanBoardStatusDto(string Board, string? MovedTo, int Threads, DateTimeOffset? FetchedAt);

public sealed record NichanStatusPayload(
    bool Enabled, object Options, NichanCountersDto Totals, NichanSampleDto[] Series,
    NichanHostDto[] Hosts, NichanBoardStatusDto[] Boards, NichanChannelStatusDto[] Channels);
