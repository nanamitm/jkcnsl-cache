using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace jkcnsl_cache;

public sealed class CommentStorageService : BackgroundService
{
    private readonly ILogger<CommentStorageService> _logger;
    private readonly string _dbPath;
    private readonly int _retentionDays;
    private readonly Channel<CommentEntry> _queue =
        Channel.CreateBounded<CommentEntry>(new BoundedChannelOptions(10_000)
        { FullMode = BoundedChannelFullMode.DropOldest });

    private record struct CommentEntry(string Channel, byte[] Data, long ReceivedAt);

    private long _sessionInserted;
    private long _lastInsertAtTicks;

    public CommentStorageService(IConfiguration config, ILogger<CommentStorageService> logger)
    {
        _logger = logger;
        _dbPath = config["CommentStorage:DbPath"] ?? "local/comments.db";
        _retentionDays = Math.Max(1, config.GetValue("CommentStorage:RetentionDays", 2));
    }

    public bool TryEnqueue(string channel, ReadOnlyMemory<byte> data) =>
        _queue.Writer.TryWrite(new CommentEntry(
            channel, data.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public StorageStatus GetStatus()
    {
        var lastTicks = Interlocked.Read(ref _lastInsertAtTicks);
        long dbBytes = 0;
        try { if (File.Exists(_dbPath)) dbBytes = new FileInfo(_dbPath).Length; } catch { }
        return new StorageStatus(
            _queue.Reader.Count,
            Interlocked.Read(ref _sessionInserted),
            Math.Round(dbBytes / 1024d / 1024d, 1),
            lastTicks > 0 ? new DateTimeOffset(lastTicks, TimeSpan.Zero) : null);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = OpenConnection();
        InitSchema(conn);
        CleanupOld(conn);

        var lastCleanup = DateTimeOffset.UtcNow;
        var batch = new List<CommentEntry>(64);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                batchCts.CancelAfter(TimeSpan.FromSeconds(5));
                try { await _queue.Reader.WaitToReadAsync(batchCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                batch.Clear();
                while (batch.Count < 256 && _queue.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count > 0)
                    InsertBatch(conn, batch);

                if (DateTimeOffset.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    CleanupOld(conn);
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "コメント保存バッチ処理エラー");
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        // シャットダウン時に残りをフラッシュ
        batch.Clear();
        while (_queue.Reader.TryRead(out var item)) batch.Add(item);
        if (batch.Count > 0)
            try { InsertBatch(conn, batch); } catch { }
    }

    public async IAsyncEnumerable<CommentRow> ExportAsync(
        DateTimeOffset from, DateTimeOffset to, string? channel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) yield break;

        await using var conn = OpenConnection(readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = channel is null
            ? "SELECT id, received_at, channel, no, date, user_id, anonymity, mail, content " +
              "FROM comments WHERE received_at >= $from AND received_at < $to " +
              "ORDER BY received_at, id"
            : "SELECT id, received_at, channel, no, date, user_id, anonymity, mail, content " +
              "FROM comments WHERE received_at >= $from AND received_at < $to AND channel = $ch " +
              "ORDER BY received_at, id";
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        if (channel is not null)
            cmd.Parameters.AddWithValue("$ch", channel);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new CommentRow(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8));
        }
    }

    private void InsertBatch(SqliteConnection conn, List<CommentEntry> batch)
    {
        var inserted = 0;
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO comments (received_at, channel, no, date, user_id, anonymity, mail, content) " +
            "VALUES ($recv, $ch, $no, $dt, $uid, $anon, $mail, $content)";
        var pRecv = cmd.Parameters.Add("$recv", SqliteType.Integer);
        var pCh   = cmd.Parameters.Add("$ch",   SqliteType.Text);
        var pNo   = cmd.Parameters.Add("$no",   SqliteType.Integer);
        var pDt   = cmd.Parameters.Add("$dt",   SqliteType.Integer);
        var pUid  = cmd.Parameters.Add("$uid",  SqliteType.Text);
        var pAnon = cmd.Parameters.Add("$anon", SqliteType.Integer);
        var pMail = cmd.Parameters.Add("$mail", SqliteType.Text);
        var pCnt  = cmd.Parameters.Add("$content", SqliteType.Text);

        foreach (var entry in batch)
        {
            try
            {
                using var doc = JsonDocument.Parse(entry.Data);
                if (!doc.RootElement.TryGetProperty("chat", out var chat)) continue;

                var content = chat.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(content)) continue;

                pRecv.Value = entry.ReceivedAt;
                pCh.Value   = entry.Channel;
                pNo.Value   = chat.TryGetProperty("no",        out var no)  ? (object)no.GetInt64()  : DBNull.Value;
                pDt.Value   = chat.TryGetProperty("date",      out var dt)  ? (object)dt.GetInt64()  : DBNull.Value;
                pUid.Value  = chat.TryGetProperty("user_id",   out var uid) ? uid.GetString() ?? (object)DBNull.Value : DBNull.Value;
                pAnon.Value = chat.TryGetProperty("anonymity", out var anon) ? anon.GetInt32() : 0;
                pMail.Value = chat.TryGetProperty("mail",      out var mail) ? mail.GetString() ?? (object)DBNull.Value : DBNull.Value;
                pCnt.Value  = content;
                cmd.ExecuteNonQuery();
                inserted++;
            }
            catch { /* malformed JSON は無視 */ }
        }
        tx.Commit();
        if (inserted > 0)
        {
            Interlocked.Add(ref _sessionInserted, inserted);
            Interlocked.Exchange(ref _lastInsertAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }

    private void CleanupOld(SqliteConnection conn)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays).ToUnixTimeSeconds();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM comments WHERE received_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
            _logger.LogInformation("コメント保存: 古いレコードを削除しました count={Count}", deleted);
    }

    private SqliteConnection OpenConnection(bool readOnly = false)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        if (!readOnly)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA synchronous=NORMAL";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private static void InitSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS comments (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                received_at INTEGER NOT NULL,
                channel     TEXT    NOT NULL,
                no          INTEGER,
                date        INTEGER,
                user_id     TEXT,
                anonymity   INTEGER NOT NULL DEFAULT 0,
                mail        TEXT,
                content     TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_comments_recv    ON comments(received_at);
            CREATE INDEX IF NOT EXISTS idx_comments_ch_recv ON comments(channel, received_at);
            """;
        cmd.ExecuteNonQuery();
    }
}

public sealed record StorageStatus(
    int QueueDepth,
    long SessionInserted,
    double DbSizeMB,
    DateTimeOffset? LastInsertAt);

public record CommentRow(
    long Id,
    DateTimeOffset ReceivedAt,
    string Channel,
    long? No,
    long? Date,
    string? UserId,
    int Anonymity,
    string? Mail,
    string Content)
{
    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
}
