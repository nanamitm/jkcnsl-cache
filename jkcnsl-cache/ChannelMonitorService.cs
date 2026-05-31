namespace jkcnsl_cache;

public class ChannelMonitorService : IHostedService
{
    private static readonly TimeSpan RestartInitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestartMaxDelay = TimeSpan.FromMinutes(30);

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
        _ = RunWithRestartAsync("SearchLoop", () => _searchService.RunAsync(ct), ct);
        _ = RunWithRestartAsync("ChannelStartup", () => StartChannelsAsync(ct), ct);
        return Task.CompletedTask;
    }

    private async Task RunWithRestartAsync(string name, Func<Task> action, CancellationToken ct)
    {
        var delay = RestartInitialDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await action();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[ChannelMonitor] {Name} で未処理の例外が発生しました。{Delay}秒後に再起動します",
                    name, (int)delay.TotalSeconds);
            }

            try { await Task.Delay(AddJitter(delay), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, RestartMaxDelay.TotalSeconds));
        }
    }

    private static TimeSpan AddJitter(TimeSpan delay)
    {
        var seconds = (int)delay.TotalSeconds;
        var jitter = Random.Shared.Next(-seconds / 4, seconds / 4 + 1);
        return TimeSpan.FromSeconds(Math.Max(5, seconds + jitter));
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
