using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EpgTimer;

var jsonOptions = CreateJsonOptions();
var appDir = AppContext.BaseDirectory;
var configPath = Path.Combine(appDir, "appsettings.json");
var localConfigPath = FindConfigUpward(appDir, Path.Combine("local", "appsettings.json"));
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"appsettings.json が見つかりません: {configPath}");
    return 1;
}

var config = LoadConfig(configPath, localConfigPath, jsonOptions);

var listServices = args.Any(arg => string.Equals(arg, "--list-services", StringComparison.OrdinalIgnoreCase));
var dryRun = args.Any(arg => string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase));
var channelFilter = GetOptionValue(args, "--channel");

try
{
    var client = new EdcbEpgClient(config.Edcb);

    if (listServices)
    {
        var services = client.GetServicesSnapshot();
        foreach (var service in services.OrderBy(s => s.RemoteControlKeyId).ThenBy(s => s.Onid).ThenBy(s => s.Sid))
        {
            Console.WriteLine($"{service.Onid}:{service.Tsid}:{service.Sid}\t{service.Name}\t{service.NetworkName}\t{service.TransportStreamName}");
        }
        return 0;
    }

    if (config.ServiceMappings.Count == 0)
    {
        Console.Error.WriteLine("ServiceMappings が未設定です。--list-services で確認してから appsettings.json を埋めてください。");
        return 2;
    }

    var now = DateTimeOffset.Now;
    var from = now.AddHours(config.Window.StartOffsetHours);
    var to = from.AddHours(config.Window.DurationHours);
    if (to <= from)
    {
        Console.Error.WriteLine("Window 設定が不正です。DurationHours は 0 より大きくしてください。");
        return 2;
    }

    var servicesSnapshot = client.GetEpgSnapshot(from.LocalDateTime, to.LocalDateTime);
    var serviceByKey = servicesSnapshot.ToDictionary(
        s => ServiceId.Format(s.Service.Onid, s.Service.Tsid, s.Service.Sid),
        StringComparer.Ordinal);

    using var http = new HttpClient
    {
        BaseAddress = new Uri(EnsureTrailingSlash(config.ImportApi.BaseUrl)),
        Timeout = TimeSpan.FromSeconds(Math.Max(5, config.ImportApi.TimeoutSeconds))
    };
    if (!string.IsNullOrWhiteSpace(config.ImportApi.ApiKey))
        http.DefaultRequestHeaders.Add("X-API-Key", config.ImportApi.ApiKey);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    var uploaded = 0;
    foreach (var mapping in config.ServiceMappings.Where(m => channelFilter is null || string.Equals(m.Video, channelFilter, StringComparison.OrdinalIgnoreCase)))
    {
        var key = ServiceId.Format(mapping.Onid, mapping.Tsid, mapping.Sid);
        if (!serviceByKey.TryGetValue(key, out var service))
        {
            Console.Error.WriteLine($"サービスが見つかりません: {mapping.Video} ({key})");
            continue;
        }

        var programs = service.Events
            .Where(ev => ev.StartTime is not null && ev.DurationSeconds is > 0)
            .Select(ev =>
            {
                var startAt = ToDateTimeOffset(ev.StartTime!.Value);
                var endAt = startAt.AddSeconds(ev.DurationSeconds!.Value);
                return new ImportProgram(
                    ev.Title,
                    startAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    endAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    null,
                    null);
            })
            .Where(program => !string.IsNullOrWhiteSpace(program.Title))
            .OrderBy(program => program.StartAt)
            .ToList();

        if (programs.Count == 0)
        {
            Console.WriteLine($"{mapping.Video}: 対象番組なし");
            continue;
        }

        Console.WriteLine($"{mapping.Video}: {programs.Count}件 ({service.Service.Name})");
        if (dryRun)
            continue;

        var payload = new ImportRequest(
            mapping.Video,
            string.IsNullOrWhiteSpace(config.ImportApi.Source) ? "airwave" : config.ImportApi.Source,
            now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            programs);

        using var response = await http.PostAsync(
            "api/admin/epg/import",
            new StringContent(JsonSerializer.Serialize(payload, jsonOptions), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{mapping.Video}: 送信失敗 {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.Error.WriteLine(body);
            return 3;
        }

        uploaded++;
    }

    Console.WriteLine(dryRun
        ? "dry-run 完了"
        : $"送信完了: {uploaded} チャンネル");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

static string? GetOptionValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string EnsureTrailingSlash(string url) => url.EndsWith("/") ? url : url + "/";

static DateTimeOffset ToDateTimeOffset(DateTime dateTime)
{
    var local = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
    return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
}

static JsonSerializerOptions CreateJsonOptions() => new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

static AppConfig LoadConfig(string configPath, string localConfigPath, JsonSerializerOptions jsonOptions)
{
    var rootNode = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
        ?? throw new InvalidOperationException($"設定ファイルの読み込みに失敗しました: {configPath}");

    if (File.Exists(localConfigPath))
    {
        var localNode = JsonNode.Parse(File.ReadAllText(localConfigPath)) as JsonObject
            ?? throw new InvalidOperationException($"設定ファイルの読み込みに失敗しました: {localConfigPath}");
        MergeObjects(rootNode, localNode);
    }

    return rootNode.Deserialize<AppConfig>(jsonOptions) ?? new AppConfig();
}

static string FindConfigUpward(string startDirectory, string relativePath)
{
    var directory = new DirectoryInfo(startDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
            return candidate;
        directory = directory.Parent;
    }

    return Path.Combine(startDirectory, relativePath);
}

static void MergeObjects(JsonObject target, JsonObject source)
{
    foreach (var pair in source)
    {
        if (pair.Value is JsonObject sourceObject &&
            target[pair.Key] is JsonObject targetObject)
        {
            MergeObjects(targetObject, sourceObject);
            continue;
        }

        target[pair.Key] = pair.Value?.DeepClone();
    }
}

sealed class EdcbEpgClient(EdcbOptions options)
{
    public IReadOnlyList<EdcbService> GetServicesSnapshot()
    {
        var cmd = CreateCommand();
        var services = new List<EpgServiceInfo>();
        EnsureSuccess(cmd.SendEnumService(ref services));
        return services.Select(service => new EdcbService(
            service.ONID,
            service.TSID,
            service.SID,
            service.service_name ?? "",
            service.network_name ?? "",
            service.ts_name ?? "",
            service.remote_control_key_id)).ToList();
    }

    public IReadOnlyList<EdcbServiceEvents> GetEpgSnapshot(DateTime start, DateTime end)
    {
        if (end <= start)
            throw new ArgumentException("end must be later than start", nameof(end));

        var cmd = CreateCommand();
        var services = new List<EpgServiceInfo>();
        EnsureSuccess(cmd.SendEnumService(ref services));

        var epg = new List<EpgServiceEventInfo>();
        EnsureSuccess(cmd.SendEnumPgAll(ref epg));

        var eventMap = epg.ToDictionary(
            info => ServiceId.Format(info.serviceInfo.ONID, info.serviceInfo.TSID, info.serviceInfo.SID),
            info => info.eventList
                .Where(e => Overlaps(e, start, end))
                .OrderBy(e => e.StartTimeFlag != 0 ? e.start_time : DateTime.MaxValue)
                .Select(ToEvent)
                .ToList(),
            StringComparer.Ordinal);

        return services
            .OrderBy(s => s.remote_control_key_id == 0 ? byte.MaxValue : s.remote_control_key_id)
            .ThenBy(s => s.ONID)
            .ThenBy(s => s.TSID)
            .ThenBy(s => s.SID)
            .Select(service =>
            {
                var key = ServiceId.Format(service.ONID, service.TSID, service.SID);
                return new EdcbServiceEvents(
                    new EdcbService(
                        service.ONID,
                        service.TSID,
                        service.SID,
                        service.service_name ?? "",
                        service.network_name ?? "",
                        service.ts_name ?? "",
                        service.remote_control_key_id),
                    eventMap.GetValueOrDefault(key) ?? []);
            })
            .ToList();
    }

    private CtrlCmdUtil CreateCommand()
    {
        var cmd = new CtrlCmdUtil();
        cmd.SetConnectTimeOut(options.ConnectTimeoutMilliseconds);
        cmd.SetSendMode(options.UseTcp);
        if (options.UseTcp)
            cmd.SetNWSetting(IPAddress.Parse(options.Host), options.Port);
        else
            cmd.SetPipeSetting(options.EventName, options.PipeName);
        return cmd;
    }

    private static void EnsureSuccess(ErrCode code)
    {
        if (code != ErrCode.CMD_SUCCESS)
            throw new InvalidOperationException($"EDCB command failed: {code}");
    }

    private static bool Overlaps(EpgEventInfo info, DateTime start, DateTime end)
    {
        if (info.StartTimeFlag == 0 || info.DurationFlag == 0)
            return false;

        var eventStart = info.start_time;
        var eventEnd = eventStart.AddSeconds(info.durationSec);
        return eventStart < end && eventEnd > start;
    }

    private static EdcbEvent ToEvent(EpgEventInfo info) => new(
        info.StartTimeFlag != 0 ? info.start_time : null,
        info.DurationFlag != 0 ? info.durationSec : null,
        info.ShortInfo?.event_name ?? "",
        info.ShortInfo?.text_char ?? "",
        info.ExtInfo?.text_char ?? "");
}

static class ServiceId
{
    public static string Format(ushort onid, ushort tsid, ushort sid) => $"{onid}:{tsid}:{sid}";
}

sealed record EdcbService(ushort Onid, ushort Tsid, ushort Sid, string Name, string NetworkName, string TransportStreamName, byte RemoteControlKeyId);
sealed record EdcbEvent(DateTime? StartTime, uint? DurationSeconds, string Title, string Description, string Detail);
sealed record EdcbServiceEvents(EdcbService Service, List<EdcbEvent> Events);

sealed class AppConfig
{
    public EdcbOptions Edcb { get; set; } = new();
    public ImportApiOptions ImportApi { get; set; } = new();
    public WindowOptions Window { get; set; } = new();
    public List<ServiceMapping> ServiceMappings { get; set; } = [];
}

sealed class EdcbOptions
{
    public bool UseTcp { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public uint Port { get; set; } = 5678;
    public string EventName { get; set; } = "Global\\EpgTimerSrvConnect";
    public string PipeName { get; set; } = "EpgTimerSrvPipe";
    public int ConnectTimeoutMilliseconds { get; set; } = 15000;
    public string RootPath { get; set; } = "C:\\Free Soft Ware\\EDCB-work-plus-s";
}

sealed class ImportApiOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5000/";
    public string ApiKey { get; set; } = "";
    public string Source { get; set; } = "airwave";
    public int TimeoutSeconds { get; set; } = 15;
}

sealed class WindowOptions
{
    public int StartOffsetHours { get; set; } = -6;
    public int DurationHours { get; set; } = 72;
}

sealed class ServiceMapping
{
    public string Video { get; set; } = "";
    public ushort Onid { get; set; }
    public ushort Tsid { get; set; }
    public ushort Sid { get; set; }
}

sealed record ImportRequest(
    string Channel,
    string Source,
    string CapturedAt,
    List<ImportProgram> Programs);

sealed record ImportProgram(
    string Title,
    string StartAt,
    string EndAt,
    string? GenreCode,
    string? GenreName);
