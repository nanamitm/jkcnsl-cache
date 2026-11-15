namespace jkcnsl_cache.Nichan;

// appsettings.json の CacheServer:Nichan セクション。
// 5ch へのアクセス頻度に関わる値は、設定ミスで過剰アクセスにならないよう下限を設ける。
public sealed class NichanOptions
{
    public const string DefaultUserAgent = "Monazilla/1.00 (jkcnsl-cache)";

    public bool Enabled { get; init; }
    public string UserAgent { get; init; } = DefaultUserAgent;
    public TimeSpan SubjectInterval { get; init; }
    public TimeSpan DatInterval { get; init; }
    public int MaxThreadsPerChannel { get; init; }
    public TimeSpan HostMinInterval { get; init; }
    public int MaxConcurrentRequests { get; init; }
    public TimeSpan InitialBacklog { get; init; }
    public TimeSpan PlaybackDelay { get; init; }
    public int MaxCommentLength { get; init; }
    public int MaxLines { get; init; }
    public bool StoreComments { get; init; }
    public int HistoryLimit { get; init; }
    public TimeSpan HistoryDuration { get; init; }
    public TimeSpan BlockedWait { get; init; }

    public static NichanOptions FromConfiguration(IConfiguration config)
    {
        var s = config.GetSection("CacheServer:Nichan");
        var datIntervalSec = Math.Max(5, s.GetValue("DatIntervalSeconds", 10));
        var ua = s.GetValue<string>("UserAgent");
        return new NichanOptions
        {
            Enabled = s.GetValue("Enabled", false),
            UserAgent = string.IsNullOrWhiteSpace(ua) ? DefaultUserAgent : ua.Trim(),
            SubjectInterval = TimeSpan.FromSeconds(Math.Max(30, s.GetValue("SubjectIntervalSeconds", 60))),
            DatInterval = TimeSpan.FromSeconds(datIntervalSec),
            MaxThreadsPerChannel = Math.Clamp(s.GetValue("MaxThreadsPerChannel", 3), 1, 5),
            HostMinInterval = TimeSpan.FromMilliseconds(Math.Max(200, s.GetValue("HostMinIntervalMilliseconds", 1000))),
            MaxConcurrentRequests = Math.Clamp(s.GetValue("MaxConcurrentRequests", 2), 1, 8),
            InitialBacklog = TimeSpan.FromSeconds(Math.Max(0, s.GetValue("InitialBacklogSeconds", 30))),
            PlaybackDelay = TimeSpan.FromSeconds(Math.Max(0, s.GetValue("PlaybackDelaySeconds", datIntervalSec + 5))),
            MaxCommentLength = Math.Max(10, s.GetValue("MaxCommentLength", 120)),
            MaxLines = Math.Max(1, s.GetValue("MaxLines", 4)),
            StoreComments = s.GetValue("StoreComments", false),
            HistoryLimit = Math.Max(0, s.GetValue("HistoryLimit", 20)),
            HistoryDuration = TimeSpan.FromSeconds(Math.Max(0, s.GetValue("HistorySeconds", 30))),
            BlockedWait = TimeSpan.FromMinutes(Math.Max(10, s.GetValue("BlockedWaitMinutes", 60))),
        };
    }
}
