namespace jkcnsl_cache;

// 勢いリスト WebSocket API（/api/channels/ws）の snapshot/stats ペイロード生成と、
// /api/status で使うソース状態の組み立てをまとめたヘルパー。
// snapshot/stats は全クライアント共通の内容なので、ここで1回だけ計算して
// ChannelsStreamBroadcaster.BroadcastAsync で配る（クライアントごとに計算しない）。
public static class ChannelsStreamPayloads
{
    public static string FormatServerTime(IConfiguration config)
    {
        var timeZoneId = config["CacheServer:BroadcastTimeZone"] ?? "Asia/Tokyo";
        TimeZoneInfo timeZone;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch { timeZone = TimeZoneInfo.Local; }
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
    }

    public static IEnumerable<string> GetSourceKeys(ChannelInfo info)
    {
        yield return info.Video;
        yield return "co" + info.Id;
        yield return info.Video + "r";
    }

    public static ChannelSourceStatus CreateSourceStatus(ChannelManager mgr, string key, string defaultSourceType)
    {
        var sourceType = defaultSourceType;
        if (!mgr.IsConfiguredChannel(key))
            return new ChannelSourceStatus(key, sourceType, SourceLabel(sourceType), false, false, null,
                0, 0, 0, 0, false, null, "notConfigured", null, false, RequiresAuth(sourceType),
                $"/watch/{key}", $"/comment/{key}");

        var (running, type, currentTarget, force, viewers, totalComments, lastResNo) = mgr.GetChannelFullStats(key);
        var scheduledStartUtc = mgr.GetChannelScheduled(key);
        var isReserved = mgr.IsChannelScheduled(key);
        var (status, statusText) = mgr.GetChannelStatus(key);
        sourceType = SourceTypeFromDisplayType(type, defaultSourceType);
        var isLocalFallback = status == "fallbackLocal";
        return new ChannelSourceStatus(
            key,
            sourceType,
            SourceLabel(sourceType),
            true,
            running,
            currentTarget,
            force,
            viewers,
            totalComments,
            lastResNo,
            isReserved,
            scheduledStartUtc,
            status,
            statusText,
            Commentable(sourceType),
            !isLocalFallback && RequiresAuth(sourceType),
            $"/watch/{key}",
            $"/comment/{key}");
    }

    private static string SourceTypeFromDisplayType(string type, string defaultSourceType) => type switch
    {
        "公式" => "official",
        "非公式" => "unofficial",
        "避難所" => "refuge",
        "ローカル" => "local",
        "-" => defaultSourceType,
        _ => "unknown",
    };

    private static string SourceLabel(string sourceType) => sourceType switch
    {
        "official" => "公式",
        "unofficial" => "非公式",
        "refuge" => "避難所",
        "local" => "ローカル",
        _ => "-",
    };

    private static bool RequiresAuth(string sourceType) => sourceType is "official" or "unofficial";
    private static bool Commentable(string sourceType) => sourceType is "local" or "refuge" or "official" or "unofficial";

    public static object CreateChannelsSnapshot(ChannelManager mgr, IConfiguration config,
        ProgramInfoService programInfoService, ChannelCatalog channelCatalog, int intervalSec) => new
    {
        type = "snapshot",
        updatedAt = FormatServerTime(config),
        statsIntervalSec = intervalSec,
        channels = channelCatalog.All.Select(info =>
        {
            var (force, viewers, totalComments, lastResNo) = mgr.GetAggregatedStats(GetSourceKeys(info));
            var coChannel = "co" + info.Id;
            return new
            {
                id = info.Id,
                name = info.Name,
                video = info.Video,
                bs = info.Bs,
                force,
                viewers,
                comments = totalComments,
                lastResNo,
                program = ProgramInfoService.ToApiProgram(programInfoService.GetProgram(info.Video)),
                sources = new[]
                {
                    CreateSourceStatus(mgr, info.Video, "official"),
                    CreateSourceStatus(mgr, coChannel, "unofficial"),
                    CreateSourceStatus(mgr, info.Video + "r", "refuge"),
                },
            };
        }),
    };

    public static object CreateChannelsStats(ChannelManager mgr, IConfiguration config, ChannelCatalog channelCatalog, int intervalSec) => new
    {
        type = "stats",
        updatedAt = FormatServerTime(config),
        intervalSec,
        channels = channelCatalog.All.Select(info =>
        {
            var (force, viewers, totalComments, lastResNo) = mgr.GetAggregatedStats(GetSourceKeys(info));
            var coChannel = "co" + info.Id;
            return new
            {
                id = info.Id,
                video = info.Video,
                force,
                viewers,
                comments = totalComments,
                lastResNo,
                sources = new[]
                {
                    CreateSourceStatus(mgr, info.Video, "official"),
                    CreateSourceStatus(mgr, coChannel, "unofficial"),
                    CreateSourceStatus(mgr, info.Video + "r", "refuge"),
                },
            };
        }),
    };
}

public record ChannelSourceStatus(
    string Key,
    string SourceType,
    string Label,
    bool Configured,
    bool Running,
    string? CurrentTarget,
    int Force,
    int Viewers,
    long TotalComments,
    long LastResNo,
    bool IsReserved,
    DateTimeOffset? ScheduledStartUtc,
    string Status,
    string? StatusText,
    bool Commentable,
    bool RequiresAuth,
    string WatchUrl,
    string CommentUrl);
