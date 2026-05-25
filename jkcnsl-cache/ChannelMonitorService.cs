namespace jkcnsl_cache;

public class ChannelMonitorService : IHostedService
{
    private readonly ChannelManager _channelManager;
    private readonly IConfiguration _config;
    private readonly NicovideoSearchService _searchService;
    private readonly ILogger<ChannelMonitorService> _logger;

    public ChannelMonitorService(ChannelManager channelManager, IConfiguration config,
        NicovideoSearchService searchService, ILogger<ChannelMonitorService> logger)
    {
        _channelManager = channelManager;
        _config = config;
        _searchService = searchService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ = RunWithLoggingAsync("SearchLoop", () => _searchService.RunAsync(ct), ct);
        _ = RunWithLoggingAsync("ChannelStartup", () => StartChannelsAsync(ct), ct);
        return Task.CompletedTask;
    }

    private async Task RunWithLoggingAsync(string name, Func<Task> action, CancellationToken ct)
    {
        try { await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChannelMonitor] {Name} で未処理の例外が発生しました", name);
        }
    }

    // localstream は外部上流へ接続しないため即時開始する。
    // その後、外部上流だけ3秒ずつずらして順次接続（上流への一斉アクセスを防ぐ）。
    private async Task StartChannelsAsync(CancellationToken ct)
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
    }

    public Task StopAsync(CancellationToken ct) => _channelManager.StopMonitoredAsync();

    private static bool IsMonitorTarget(IConfigurationSection ch) =>
        ch.Key.StartsWith("jk", StringComparison.Ordinal) ||
        ch.Key.StartsWith("co", StringComparison.Ordinal);

    private static bool IsLocalStreamValue(string? upstreamValue) =>
        upstreamValue?.StartsWith("localstream:", StringComparison.OrdinalIgnoreCase) == true;
}
