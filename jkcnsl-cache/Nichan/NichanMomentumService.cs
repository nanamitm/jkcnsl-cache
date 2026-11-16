using System.Collections.Concurrent;

namespace jkcnsl_cache.Nichan;

// コメント受信者がいない 5ch ソースの勢いを、subject.txt のレス数の増え方から求める。
// 5ch ソースは受信者がいる間しか dat を取得しないため、それだけだと誰も見ていないチャンネルの
// 勢いが常に 0 になり、勢いリストから「いま 5ch で盛り上がっているチャンネル」がわからない。
// 取得するのは subject.txt だけで、板ごとに SubjectInterval に 1 回（NichanBoardService の共有キャッシュ経由）。
// dat は取得しない。スレの選び方はコメント配信（NichanUpstreamChannel）と同じ。
public sealed class NichanMomentumService : BackgroundService
{
    private const string NichanPrefix = "5ch:";

    private readonly IConfiguration _config;
    private readonly NichanOptions _options;
    private readonly NichanBoardService _boards;
    private readonly NichanBoardCatalog _catalog;
    private readonly ProgramInfoService _programs;
    private readonly ILogger<NichanMomentumService> _logger;
    private readonly ConcurrentDictionary<string, (int Force, DateTimeOffset At)> _forces = new(StringComparer.Ordinal);
    // ExecuteAsync のループからだけ触る
    private readonly Dictionary<string, NichanProgramTracker> _trackers = new(StringComparer.Ordinal);

    public NichanMomentumService(IConfiguration config, NichanHttpClient http, NichanBoardService boards,
        NichanBoardCatalog catalog, ProgramInfoService programs, ILogger<NichanMomentumService> logger)
    {
        _config = config;
        _options = http.Options;
        _boards = boards;
        _catalog = catalog;
        _programs = programs;
        _logger = logger;
    }

    // これより古い値は使わない（取得の失敗やホストの停止が続いたとき、古い勢いを出し続けないように）
    private TimeSpan StaleAfter => _options.SubjectInterval * 3;

    // 1 分あたりのレス数（他ソースの勢いと同じ単位）。わからないときは 0
    public int GetForce(string channel) =>
        _forces.TryGetValue(channel, out var entry) && DateTimeOffset.UtcNow - entry.At <= StaleAfter
            ? entry.Force
            : 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.BackgroundMomentum)
            return;

        try
        {
            // 番組表の読み込みを少し待つ
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var channels = _config.GetSection("CacheServer:Channels").GetChildren()
                    .Where(section => section.Value?.StartsWith(NichanPrefix, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(section => (section.Key, Target: section.Value![NichanPrefix.Length..].Trim()))
                    .ToArray();
                foreach (var (channel, target) in channels)
                {
                    try
                    {
                        if (await EstimateAsync(channel, target, stoppingToken) is { } force)
                            _forces[channel] = (force, DateTimeOffset.UtcNow);
                    }
                    catch (NichanException ex)
                    {
                        // 前回の値は StaleAfter まで残す
                        _logger.LogDebug("[{Channel}] 5ch 勢いの取得に失敗しました: {Message}", channel, ex.Message);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "[{Channel}] 5ch 勢いの取得に失敗しました", channel);
                    }
                }
                foreach (var key in _forces.Keys.Except(channels.Select(c => c.Key)).ToArray())
                    _forces.TryRemove(key, out _);

                await Task.Delay(_options.SubjectInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // 対象の板がない・スレ URL が不正なときは null
    private async Task<int?> EstimateAsync(string channel, string target, CancellationToken ct)
    {
        IEnumerable<NichanThreadInfo> threads;
        if (target.Length > 0)
        {
            // スレ固定: そのスレだけを見る（板が移転していてもよいよう、板名とキーで照合する）
            if (!NichanThreadAddress.TryParseThreadUrl(target, out var address) || address == null)
                return null;
            threads = (await _boards.GetThreadsAsync(address.BoardUri, ct))
                .Where(t => t.Address.Board == address.Board && t.Address.Key == address.Key);
        }
        else
        {
            var entries = _catalog.Get(channel);
            if (entries.Count == 0)
                return null;
            if (!_trackers.TryGetValue(channel, out var tracker))
                _trackers[channel] = tracker = new NichanProgramTracker(_programs,
                    UpstreamChannelBase.NormalizeJkThreadId(channel));
            var now = DateTimeOffset.UtcNow;
            tracker.Refresh(now);
            var (programKeys, programOnlyKeys) = tracker.GetKeys(now);
            threads = (await NichanThreadSelector.SelectAsync(_boards, entries, programKeys, programOnlyKeys,
                    _options.MaxThreadsPerChannel, ct))
                .Select(s => s.Thread);
        }
        // DisplayMomentum は直近約 2 分の増え方から求めた 1 時間あたりのレス数（スレ選びに使う RecentMomentum は
        // 10 分の平均で、表示には反応が遅い）。観測期間が足りないスレ（起動直後・新スレ）は数えない
        var perHour = threads.Sum(t => t.DisplayMomentum ?? 0);
        return (int)Math.Round(perHour / 60, MidpointRounding.AwayFromZero);
    }
}
