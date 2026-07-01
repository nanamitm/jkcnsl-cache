using Microsoft.Data.Sqlite;

namespace jkcnsl_cache;

public sealed class EpgStorageService : BackgroundService
{
    private readonly ILogger<EpgStorageService> _logger;
    private readonly string _dbPath;
    private readonly int _retentionDays;
    private readonly ChannelCatalog _channelCatalog;

    public EpgStorageService(IConfiguration config, ILogger<EpgStorageService> logger, ChannelCatalog channelCatalog)
    {
        _logger = logger;
        _dbPath = config["EpgStorage:DbPath"] ?? "local/epg.db";
        _retentionDays = Math.Max(1, config.GetValue("EpgStorage:RetentionDays", 60));
        _channelCatalog = channelCatalog;
    }

    public void SavePrograms(string channel, IReadOnlyList<EpgProgram> programs)
    {
        if (programs.Count == 0) return;
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO epg_programs (channel, original_network_id, transport_stream_id, service_id, title, start_at, end_at, source, genre_code, genre_name) " +
                "VALUES ($ch, $onid, $tsid, $sid, $title, $start, $end, $source, $genre_code, $genre_name)";
            var pCh = cmd.Parameters.Add("$ch", SqliteType.Text);
            var pOnid = cmd.Parameters.Add("$onid", SqliteType.Integer);
            var pTsid = cmd.Parameters.Add("$tsid", SqliteType.Integer);
            var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pStart = cmd.Parameters.Add("$start", SqliteType.Integer);
            var pEnd = cmd.Parameters.Add("$end", SqliteType.Integer);
            var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
            var pGenreCode = cmd.Parameters.Add("$genre_code", SqliteType.Text);
            var pGenreName = cmd.Parameters.Add("$genre_name", SqliteType.Text);

            pCh.Value = channel;
            var channelInfo = ResolveChannel(channel);
            foreach (var p in programs)
            {
                BindServiceKeyParameters(pOnid, pTsid, pSid, ResolveServiceKey(p, channelInfo));
                pTitle.Value = p.Title;
                pStart.Value = p.StartAt.ToUnixTimeSeconds();
                pEnd.Value = p.EndAt.ToUnixTimeSeconds();
                pSource.Value = p.Source;
                pGenreCode.Value = (object?)p.GenreCode ?? DBNull.Value;
                pGenreName.Value = (object?)p.GenreName ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EpgStorage] EPGデータの保存に失敗しました: channel={Channel}", channel);
        }
    }

    public void ReplaceImportedPrograms(string channel, IReadOnlyList<EpgProgram> programs)
    {
        if (programs.Count == 0) return;

        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            var rangeStart = programs.Min(p => p.StartAt).ToUnixTimeSeconds();
            var rangeEnd = programs.Max(p => p.EndAt).ToUnixTimeSeconds();

            using (var delete = conn.CreateCommand())
            {
                delete.CommandText =
                    "DELETE FROM epg_import_programs " +
                    "WHERE channel = $ch AND end_at > $from AND start_at < $to";
                delete.Parameters.AddWithValue("$ch", channel);
                delete.Parameters.AddWithValue("$from", rangeStart);
                delete.Parameters.AddWithValue("$to", rangeEnd);
                delete.ExecuteNonQuery();
            }

            using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT OR REPLACE INTO epg_import_programs (channel, original_network_id, transport_stream_id, service_id, title, start_at, end_at, source, genre_code, genre_name) " +
                "VALUES ($ch, $onid, $tsid, $sid, $title, $start, $end, $source, $genre_code, $genre_name)";
            var pCh = insert.Parameters.Add("$ch", SqliteType.Text);
            var pOnid = insert.Parameters.Add("$onid", SqliteType.Integer);
            var pTsid = insert.Parameters.Add("$tsid", SqliteType.Integer);
            var pSid = insert.Parameters.Add("$sid", SqliteType.Integer);
            var pTitle = insert.Parameters.Add("$title", SqliteType.Text);
            var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
            var pEnd = insert.Parameters.Add("$end", SqliteType.Integer);
            var pSource = insert.Parameters.Add("$source", SqliteType.Text);
            var pGenreCode = insert.Parameters.Add("$genre_code", SqliteType.Text);
            var pGenreName = insert.Parameters.Add("$genre_name", SqliteType.Text);

            pCh.Value = channel;
            var channelInfo = ResolveChannel(channel);
            foreach (var p in programs)
            {
                BindServiceKeyParameters(pOnid, pTsid, pSid, ResolveServiceKey(p, channelInfo));
                pTitle.Value = p.Title;
                pStart.Value = p.StartAt.ToUnixTimeSeconds();
                pEnd.Value = p.EndAt.ToUnixTimeSeconds();
                pSource.Value = p.Source;
                pGenreCode.Value = (object?)p.GenreCode ?? DBNull.Value;
                pGenreName.Value = (object?)p.GenreName ?? DBNull.Value;
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EpgStorage] インポートEPGデータの保存に失敗しました: channel={Channel}", channel);
        }
    }

    public (DateTimeOffset? Earliest, DateTimeOffset? Latest) GetDateRange()
    {
        if (!File.Exists(_dbPath)) return (null, null);
        try
        {
            using var conn = OpenConnection(readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MIN(start_at), MAX(start_at) FROM epg_programs";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return (null, null);
            return (
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))
            );
        }
        catch { return (null, null); }
    }

    public Dictionary<string, List<EpgProgram>> QueryPrograms(DateTimeOffset from, DateTimeOffset to)
    {
        return QueryProgramsCore("epg_programs", from, to);
    }

    public Dictionary<string, List<EpgProgram>> QueryImportedPrograms(DateTimeOffset from, DateTimeOffset to)
    {
        return QueryProgramsCore("epg_import_programs", from, to);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using (var conn = OpenConnection())
        {
            InitSchema(conn);
            CleanupOld(conn);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
            try
            {
                using var conn = OpenConnection();
                CleanupOld(conn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EpgStorage] EPGクリーンアップに失敗しました");
            }
        }
    }

    private void CleanupOld(SqliteConnection conn)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays).ToUnixTimeSeconds();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM epg_programs WHERE end_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
            _logger.LogInformation("[EpgStorage] 古いEPGを削除しました count={Count}", deleted);
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
            CREATE TABLE IF NOT EXISTS epg_programs (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                channel              TEXT    NOT NULL,
                original_network_id  INTEGER,
                transport_stream_id  INTEGER,
                service_id           INTEGER,
                title                TEXT    NOT NULL,
                start_at             INTEGER NOT NULL,
                end_at               INTEGER NOT NULL,
                source               TEXT    NOT NULL,
                genre_code           TEXT,
                genre_name           TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_epg_channel_start ON epg_programs(channel, start_at);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_epg_service_start ON epg_programs(original_network_id, transport_stream_id, service_id, start_at)
                WHERE original_network_id IS NOT NULL AND transport_stream_id IS NOT NULL AND service_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_epg_end ON epg_programs(end_at);

            CREATE TABLE IF NOT EXISTS epg_import_programs (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                channel              TEXT    NOT NULL,
                original_network_id  INTEGER,
                transport_stream_id  INTEGER,
                service_id           INTEGER,
                title                TEXT    NOT NULL,
                start_at             INTEGER NOT NULL,
                end_at               INTEGER NOT NULL,
                source               TEXT    NOT NULL,
                genre_code           TEXT,
                genre_name           TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_epg_import_channel_start ON epg_import_programs(channel, start_at);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_epg_import_service_start ON epg_import_programs(original_network_id, transport_stream_id, service_id, start_at)
                WHERE original_network_id IS NOT NULL AND transport_stream_id IS NOT NULL AND service_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_epg_import_end ON epg_import_programs(end_at);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn(conn, "epg_programs", "original_network_id", "INTEGER");
        EnsureColumn(conn, "epg_programs", "transport_stream_id", "INTEGER");
        EnsureColumn(conn, "epg_programs", "service_id", "INTEGER");
        EnsureColumn(conn, "epg_import_programs", "original_network_id", "INTEGER");
        EnsureColumn(conn, "epg_import_programs", "transport_stream_id", "INTEGER");
        EnsureColumn(conn, "epg_import_programs", "service_id", "INTEGER");
    }

    private static void EnsureColumn(SqliteConnection conn, string tableName, string columnName, string typeName)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {typeName}";
        alter.ExecuteNonQuery();
    }

    private Dictionary<string, List<EpgProgram>> QueryProgramsCore(string tableName, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new Dictionary<string, List<EpgProgram>>(StringComparer.Ordinal);
        if (!File.Exists(_dbPath)) return result;

        try
        {
            using var conn = OpenConnection(readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT channel, original_network_id, transport_stream_id, service_id, title, start_at, end_at, source, genre_code, genre_name " +
                $"FROM {tableName} WHERE end_at > $from AND start_at < $to " +
                $"ORDER BY channel, start_at";
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var storedChannel = reader.GetString(0);
                var originalNetworkId = reader.IsDBNull(1) ? (ushort)0 : checked((ushort)reader.GetInt32(1));
                var transportStreamId = reader.IsDBNull(2) ? (ushort)0 : checked((ushort)reader.GetInt32(2));
                var serviceId = reader.IsDBNull(3) ? (ushort)0 : checked((ushort)reader.GetInt32(3));
                var serviceKey = new ServiceKey(originalNetworkId, transportStreamId, serviceId);
                var channel = ResolveStoredChannel(storedChannel, serviceKey);
                var title = reader.GetString(4);
                var startAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5));
                var endAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6));
                var source = reader.GetString(7);
                var genreCode = reader.IsDBNull(8) ? null : reader.GetString(8);
                var genreName = reader.IsDBNull(9) ? null : reader.GetString(9);

                if (!result.TryGetValue(channel, out var list))
                    result[channel] = list = new List<EpgProgram>();
                list.Add(new EpgProgram(title, startAt, endAt, source, genreCode, genreName,
                    originalNetworkId, transportStreamId, serviceId, storedChannel));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EpgStorage] EPGデータの取得に失敗しました: table={Table}", tableName);
        }

        return result;
    }

    private ChannelInfo? ResolveChannel(string channel) =>
        _channelCatalog.All.FirstOrDefault(info =>
            string.Equals(info.Video, channel, StringComparison.Ordinal) ||
            string.Equals(info.LegacyJkId, channel, StringComparison.Ordinal));

    private static ServiceKey ResolveServiceKey(EpgProgram program, ChannelInfo? channelInfo) =>
        program.HasServiceKey ? program.ServiceKey : channelInfo?.ServiceKey ?? default;

    private string ResolveStoredChannel(string storedChannel, ServiceKey serviceKey)
    {
        if (!serviceKey.IsEmpty)
        {
            var mappedChannel = _channelCatalog.All.FirstOrDefault(info => info.HasServiceKey && info.ServiceKey == serviceKey);
            if (mappedChannel != null)
                return mappedChannel.Video;

            if (NetworkServiceIdTable.ByServiceKey.TryGetValue(serviceKey, out var mapping))
                return mapping.JkId;
        }

        return ResolveChannel(storedChannel)?.Video ?? storedChannel;
    }

    private static void BindServiceKeyParameters(SqliteParameter onid, SqliteParameter tsid, SqliteParameter sid, ServiceKey serviceKey)
    {
        onid.Value = serviceKey.OriginalNetworkId == 0 ? DBNull.Value : serviceKey.OriginalNetworkId;
        tsid.Value = serviceKey.TransportStreamId == 0 ? DBNull.Value : serviceKey.TransportStreamId;
        sid.Value = serviceKey.ServiceId == 0 ? DBNull.Value : serviceKey.ServiceId;
    }
}
