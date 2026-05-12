using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace jkcnsl_cache;

/// <summary>ログエントリをSSEクライアントへブロードキャストするサービス</summary>
public sealed class LogBroadcaster
{
    private readonly Queue<string> _buffer = new(200);
    private readonly List<ChannelWriter<string>> _subscribers = new();
    private readonly object _lock = new();
    private const int MaxBuffer = 200;

    public void Write(string entry)
    {
        lock (_lock)
        {
            _buffer.Enqueue(entry);
            if (_buffer.Count > MaxBuffer) _buffer.Dequeue();
            foreach (var w in _subscribers)
                w.TryWrite(entry);
        }
    }

    /// <summary>バックログを送ってから新着をストリーミング</summary>
    public async IAsyncEnumerable<string> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
            { FullMode = BoundedChannelFullMode.DropOldest });

        string[] backlog;
        lock (_lock)
        {
            backlog = _buffer.ToArray();
            _subscribers.Add(ch.Writer);
        }

        foreach (var e in backlog) yield return e;

        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct))
                yield return e;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(ch.Writer);
        }
    }
}

/// <summary>LogBroadcaster へログを転送する ILoggerProvider</summary>
public sealed class LogBroadcastProvider(LogBroadcaster broadcaster) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new LogBroadcastLogger(categoryName, broadcaster);
    public void Dispose() { }
}

file sealed class LogBroadcastLogger(string category, LogBroadcaster broadcaster) : ILogger
{
    // "jkcnsl_cache.ChannelManager" → "ChannelManager"
    private readonly string _short = category.Contains('.')
        ? category[(category.LastIndexOf('.') + 1)..] : category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

    public void Log<TState>(LogLevel level, EventId id, TState state,
        Exception? ex, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var lvl = level switch
        {
            LogLevel.Warning => "WARN",
            LogLevel.Error or LogLevel.Critical => "ERROR",
            _ => "INFO"
        };
        broadcaster.Write(
            $"{DateTime.Now:HH:mm:ss.fff} [{lvl}] {_short}: {formatter(state, ex)}");
    }
}
