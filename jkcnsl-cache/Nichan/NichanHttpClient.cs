using System.Collections.Concurrent;
using System.Net;

namespace jkcnsl_cache.Nichan;

public class NichanException : Exception
{
    public NichanException(string message, Exception? inner = null) : base(message, inner) { }
}

// 401/403 や長い Retry-After でホストへのアクセスを止めている間に投げる例外
public sealed class NichanHostBlockedException : NichanException
{
    public string Host { get; }
    public DateTimeOffset Until { get; }

    public NichanHostBlockedException(string host, DateTimeOffset until)
        : base($"{host} へのアクセスを {until.ToLocalTime():HH:mm:ss} まで停止しています")
    {
        Host = host;
        Until = until;
    }
}

// 5ch へのリクエストをすべてここに通し、全チャンネル合計でアクセス頻度を制限する。
// - 同一ホストへのリクエスト間隔を HostMinInterval 以上空ける
// - 同時リクエスト数を MaxConcurrentRequests 以下にする
// - 401/403 はホスト単位で BlockedWait の間止める、429/503 の Retry-After もホスト単位で守る
public sealed class NichanHttpClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly NichanOptions _options;
    private readonly ILogger<NichanHttpClient> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private long _requests;
    private long _errors;
    private long _rateLimited;

    public NichanOptions Options => _options;
    public long TotalRequests => Interlocked.Read(ref _requests);
    public long TotalErrors => Interlocked.Read(ref _errors);
    public long TotalRateLimited => Interlocked.Read(ref _rateLimited);

    public NichanHttpClient(IConfiguration config, ILogger<NichanHttpClient> logger)
    {
        _options = NichanOptions.FromConfiguration(config);
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentRequests);
        // dat の差分取得（Range）はバイト位置で行うため、自動展開はしない
        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.UserAgent);
    }

    public DateTimeOffset? GetBlockedUntil(string host) =>
        _hosts.TryGetValue(host, out var state) && state.BlockedUntil > DateTimeOffset.UtcNow
            ? state.BlockedUntil
            : null;

    // 呼び出し側でレスポンスを Dispose すること
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        var state = _hosts.GetOrAdd(host, _ => new HostState());

        await _concurrency.WaitAsync(ct);
        try
        {
            await state.Gate.WaitAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (state.BlockedUntil > now)
                    throw new NichanHostBlockedException(host, state.BlockedUntil);
                var wait = state.LastRequest + _options.HostMinInterval - now;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
                state.LastRequest = DateTimeOffset.UtcNow;
            }
            finally
            {
                state.Gate.Release();
            }

            Interlocked.Increment(ref _requests);
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            }
            catch (HttpRequestException ex)
            {
                Interlocked.Increment(ref _errors);
                throw new NichanException($"5ch との通信に失敗しました: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _errors);
                throw new NichanException("5ch との通信がタイムアウトしました", ex);
            }

            var code = (int)response.StatusCode;
            if (code is 401 or 403)
            {
                Interlocked.Increment(ref _errors);
                var until = DateTimeOffset.UtcNow + _options.BlockedWait;
                state.BlockedUntil = until;
                _logger.LogWarning("5ch {Host} から {Code} が返りました。{Until} までこのホストへのアクセスを停止します: {Uri}",
                    host, code, until.ToLocalTime().ToString("HH:mm:ss"), request.RequestUri);
                response.Dispose();
                throw new NichanHostBlockedException(host, until);
            }
            if (code is 429 or 503)
            {
                Interlocked.Increment(ref _rateLimited);
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(1);
                if (delay < TimeSpan.FromSeconds(10)) delay = TimeSpan.FromSeconds(10);
                if (delay > TimeSpan.FromHours(1)) delay = TimeSpan.FromHours(1);
                var until = DateTimeOffset.UtcNow + delay;
                state.BlockedUntil = until;
                _logger.LogWarning("5ch {Host} から {Code} が返りました。{Seconds} 秒間このホストへのアクセスを停止します",
                    host, code, (int)delay.TotalSeconds);
                response.Dispose();
                throw new NichanHostBlockedException(host, until);
            }
            if (code >= 500)
                Interlocked.Increment(ref _errors);
            return response;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose() => _client.Dispose();

    private sealed class HostState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset LastRequest = DateTimeOffset.MinValue;
        public DateTimeOffset BlockedUntil = DateTimeOffset.MinValue;
    }
}
