namespace jkcnsl_cache;

public class ChannelMonitorService : IHostedService
{
    private readonly ChannelManager _channelManager;
    private readonly IConfiguration _config;
    private readonly NicovideoSearchService _searchService;

    public ChannelMonitorService(ChannelManager channelManager, IConfiguration config,
        NicovideoSearchService searchService)
    {
        _channelManager = channelManager;
        _config = config;
        _searchService = searchService;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // 統合検索ループを起動
        _ = Task.Run(() => _searchService.RunAsync(ct), ct);

        // localstream は外部上流へ接続しないため即時開始する。
        // その後、外部上流だけ3秒ずつずらして順次接続（上流への一斉アクセスを防ぐ）。
        _ = Task.Run(async () =>
        {
            var channels = _config.GetSection("CacheServer:Channels")
                .GetChildren()
                .Where(IsMonitorTarget)
                .ToArray();

            foreach (var ch in channels.Where(ch => IsLocalStreamValue(ch.Value)))
            {
                if (ct.IsCancellationRequested) break;
                _channelManager.StartMonitoring(ch.Key);
            }

            foreach (var ch in channels.Where(ch => !IsLocalStreamValue(ch.Value)))
            {
                if (ct.IsCancellationRequested) break;
                _channelManager.StartMonitoring(ch.Key);
                try { await Task.Delay(3_000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => _channelManager.StopMonitoredAsync();

    private static bool IsMonitorTarget(IConfigurationSection ch) =>
        ch.Key.StartsWith("jk", StringComparison.Ordinal) ||
        ch.Key.StartsWith("co", StringComparison.Ordinal);

    private static bool IsLocalStreamValue(string? upstreamValue) =>
        upstreamValue?.StartsWith("localstream:", StringComparison.OrdinalIgnoreCase) == true;
}
