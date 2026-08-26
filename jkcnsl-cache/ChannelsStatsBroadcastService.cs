namespace jkcnsl_cache;

// 勢いリスト WebSocket API（/api/channels/ws）の stats を、接続クライアントごとに
// 個別計算・個別送信するのではなく、一定間隔で1回だけ計算して全クライアントへ
// まとめて配信する。接続数が増えても stats 生成コストが接続数倍に増えないようにするため。
public sealed class ChannelsStatsBroadcastService(
    IConfiguration config,
    ILogger<ChannelsStatsBroadcastService> logger,
    ChannelManager channelManager,
    ChannelsStreamBroadcaster broadcaster,
    ChannelCatalog channelCatalog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSec = Math.Max(1,
            config.GetValue<int>("CacheServer:ChannelsStreamIntervalSeconds", 2));
        var interval = TimeSpan.FromSeconds(intervalSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await broadcaster.BroadcastAsync(
                    ChannelsStreamPayloads.CreateChannelsStats(channelManager, config, channelCatalog, intervalSec),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ChannelsStream] stats 配信に失敗しました");
            }
        }
    }
}
