using jkcnsl_cache;
using System.Globalization;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("local/appsettings.json", optional: true, reloadOnChange: true);
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss.fff ");

// LogBroadcaster をシングルトンで登録し、カスタムロガープロバイダーを追加
var logBroadcaster = new LogBroadcaster();
builder.Services.AddSingleton(logBroadcaster);
builder.Logging.AddProvider(new LogBroadcastProvider(logBroadcaster));

builder.Services.AddSingleton<NicovideoSearchService>();
builder.Services.AddSingleton<ChannelCatalog>();
builder.Services.AddSingleton<ChannelManager>();
builder.Services.AddSingleton<ChannelsStreamBroadcaster>();
builder.Services.AddSingleton<LocalStreamConnectionLimiter>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<EpgStorageService>();
builder.Services.AddSingleton<ProgramInfoService>();
builder.Services.AddSingleton<CommentStorageService>();
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.AddHostedService<ChannelMonitorService>();
builder.Services.AddHostedService<ChannelsStatsBroadcastService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EpgStorageService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProgramInfoService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommentStorageService>());
builder.Services.AddOutputCache();

var bindAddress = builder.Configuration.GetValue<string>("CacheServer:BindAddress") ?? "0.0.0.0";
var mainPort    = builder.Configuration.GetValue<int>("CacheServer:MainPort",   5000);
var statusPort  = builder.Configuration.GetValue<int>("CacheServer:StatusPort", 5001);
var channelsStreamIntervalSec = Math.Max(1,
    builder.Configuration.GetValue<int>("CacheServer:ChannelsStreamIntervalSeconds", 2));

builder.WebHost.ConfigureKestrel((_, options) =>
{
    var addr = System.Net.IPAddress.Parse(bindAddress);
    options.Listen(addr, mainPort);
    if (mainPort != statusPort)
        options.Listen(addr, statusPort);
});

var startedAt = DateTimeOffset.UtcNow;

var app = builder.Build();
app.UseResponseCompression();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseWhen(ctx => mainPort == statusPort || ctx.Connection.LocalPort == mainPort, mainApp =>
{
    mainApp.UseDefaultFiles();
    mainApp.UseStaticFiles();
});
app.UseRouting();
app.UseOutputCache();

// ステータスポートのみ許可するフィルター（同一ポートなら制限なし）
RouteHandlerBuilder StatusOnly(RouteHandlerBuilder b) =>
    mainPort == statusPort ? b :
    b.AddEndpointFilter(async (ctx, next) =>
    {
        if (ctx.HttpContext.Connection.LocalPort != statusPort)
        {
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Empty;
        }
        return await next(ctx);
    });

// 視聴セッション（メインポートのみ）
app.Map("/watch/{channel}", async (HttpContext ctx, string channel, ChannelManager mgr,
    IHostApplicationLifetime lifetime) =>
{
    if (mainPort != statusPort && ctx.Connection.LocalPort != mainPort)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (!mgr.IsKnownChannel(channel))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);
    await mgr.HandleWatchSessionAsync(channel, ws, cts.Token);
});

// コメントセッション（メインポートのみ）
app.Map("/comment/{channel}", async (HttpContext ctx, string channel, ChannelManager mgr,
    IHostApplicationLifetime lifetime) =>
{
    if (mainPort != statusPort && ctx.Connection.LocalPort != mainPort)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!mgr.IsKnownChannel(channel))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync("msg.nicovideo.jp#json");
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);
    await mgr.HandleCommentSessionAsync(channel, ws, cts.Token);
});

// ステータスページ（ステータスポートのみ）
StatusOnly(app.MapGet("/", () => Results.Content(StatusPage.Html, "text/html; charset=utf-8")));

// ログ SSE ストリーム（ステータスポートのみ）
StatusOnly(app.MapGet("/api/logs",
    async (HttpContext ctx, LogBroadcaster broadcaster, IHostApplicationLifetime lifetime) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");
    // クライアント切断 OR アプリ停止どちらでも即キャンセル
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);
    try
    {
        await foreach (var entry in broadcaster.StreamAsync(cts.Token))
        {
            var data = System.Text.Json.JsonSerializer.Serialize(entry);
            await ctx.Response.WriteAsync($"data: {data}\n\n", cts.Token);
            await ctx.Response.Body.FlushAsync(cts.Token);
        }
    }
    catch (OperationCanceledException) { }
}));

// 管理画面メトリクス API（ステータスポートのみ）
StatusOnly(app.MapGet("/api/admin/metrics", (MetricsService metrics, CommentStorageService storage) =>
    Results.Json(metrics.CreatePayload(storage.GetStatus()))));

// コメントエクスポート API（ステータスポートのみ）
// GET /api/comments/export?date=2026-06-26[&channel=jk1]
StatusOnly(app.MapGet("/api/comments/export",
    async (HttpContext ctx, CommentStorageService storage, IHostApplicationLifetime lifetime) =>
{
    var dateText = ctx.Request.Query["date"].ToString();
    var fromText = ctx.Request.Query["from"].ToString();
    var toText   = ctx.Request.Query["to"].ToString();
    var channel  = ctx.Request.Query["channel"].ToString();
    if (string.IsNullOrWhiteSpace(channel)) channel = null;

    DateTimeOffset from, to;
    if (!string.IsNullOrWhiteSpace(fromText) || !string.IsNullOrWhiteSpace(toText))
    {
        if (!DateTimeOffset.TryParse(fromText, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out from) ||
            !DateTimeOffset.TryParse(toText, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out to))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(
                new { error = "from/to は ISO 8601 形式で指定してください (例: 2026-06-26T10:00:00+09:00)" });
            return;
        }
        if (from >= to)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "from は to より前の時刻を指定してください" });
            return;
        }
        if (to - from > TimeSpan.FromDays(31))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "指定できる範囲は最大31日です" });
            return;
        }
    }
    else if (!string.IsNullOrWhiteSpace(dateText))
    {
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "date は yyyy-MM-dd 形式で指定してください" });
            return;
        }
        from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        to   = from.AddDays(1);
    }
    else
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(
            new { error = "date または from/to を指定してください" });
        return;
    }

    ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
    ctx.Response.Headers.Append("Cache-Control", "no-cache");

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);
    try
    {
        await foreach (var row in storage.ExportAsync(from, to, channel, cts.Token))
        {
            await ctx.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(row, CommentRow.JsonOptions) + "\n",
                cts.Token);
        }
    }
    catch (OperationCanceledException) { }
}));

// ステータス JSON API（ステータスポートのみ、2秒キャッシュ）
app.MapGet("/api/status", (ChannelManager mgr, ChannelCatalog channelCatalog) =>
{
    var channels = channelCatalog.All.Select(info =>
    {
        var (running, _, currentTarget, force, viewers, totalComments, lastResNo) = mgr.GetChannelFullStats(info.Video);

        var coChannel = "co" + info.Id;
        var sources = new[]
        {
            ChannelsStreamPayloads.CreateSourceStatus(mgr, info.Video, "official"),
            ChannelsStreamPayloads.CreateSourceStatus(mgr, coChannel, "unofficial"),
            ChannelsStreamPayloads.CreateSourceStatus(mgr, info.Video + "r", "refuge"),
        };

        return new
        {
            id = info.Id,
            name = info.Name,
            video = info.Video,
            bs = info.Bs,
            running,
            currentTarget,
            force,
            viewers,
            totalComments,
            lastResNo,
            sources
        };
    });
    return Results.Json(new
    {
        uptimeSec = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
        channels
    });
}).CacheOutput(p => p.Expire(TimeSpan.FromSeconds(2)));

// 番組表保存期間 API（DBに蓄積された最古〜最新の放送日を返す）
app.MapGet("/api/programs/schedule/range", (EpgStorageService epgStorage, IConfiguration config) =>
{
    var (earliest, latest) = epgStorage.GetDateRange();
    if (earliest == null)
        return Results.Json(new { earliestDate = (string?)null, latestDate = (string?)null });

    var tzId = config["CacheServer:BroadcastTimeZone"] ?? "Asia/Tokyo";
    TimeZoneInfo tz;
    try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
    catch { tz = TimeZoneInfo.Local; }

    return Results.Json(new
    {
        earliestDate = ToBroadcastDate(earliest.Value, tz).ToString("yyyy-MM-dd"),
        latestDate   = ToBroadcastDate(latest!.Value,  tz).ToString("yyyy-MM-dd"),
    });
});

// 番組表 API（既存EPGキャッシュのみを返す。date は放送日 yyyy-MM-dd）
app.MapGet("/api/programs/schedule", (HttpContext ctx, ProgramInfoService programInfoService) =>
{
    DateOnly? broadcastDate = null;
    var dateText = ctx.Request.Query["date"].ToString();
    if (!string.IsNullOrWhiteSpace(dateText))
    {
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return Results.BadRequest(new { error = "date は yyyy-MM-dd 形式の放送日で指定してください" });
        broadcastDate = parsed;
    }

    return Results.Json(programInfoService.CreateSchedulePayload(broadcastDate));
}).CacheOutput(p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("date"));

// 外部EPG取り込み API（メインポートのみ）
app.MapPost("/api/admin/epg/import", async (
    HttpContext ctx,
    ProgramInfoService programInfoService,
    IConfiguration config,
    ILogger<Program> logger) =>
{
    if (mainPort != statusPort && ctx.Connection.LocalPort != mainPort)
        return Results.NotFound();

    if (!config.GetValue("CacheServer:EpgImport:Enabled", true))
        return Results.NotFound();

    var configuredApiKey = config["CacheServer:EpgImport:ApiKey"];
    if (string.IsNullOrWhiteSpace(configuredApiKey))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    var requestApiKey = ctx.Request.Headers["X-API-Key"].ToString();
    if (!ApiKeysEqual(requestApiKey, configuredApiKey))
        return Results.Unauthorized();

    var body = await ctx.Request.ReadFromJsonAsync<EpgImportRequest>(cancellationToken: ctx.RequestAborted);
    if (body is null || string.IsNullOrWhiteSpace(body.Channel))
        return Results.BadRequest(new { error = "channel は必須です" });
    if (body.Programs is null || body.Programs.Count == 0)
        return Results.BadRequest(new { error = "programs は1件以上必要です" });

    var source = string.IsNullOrWhiteSpace(body.Source) ? "airwave" : body.Source.Trim();
    var programs = new List<EpgProgram>();
    var errors = new List<string>();

    for (var i = 0; i < body.Programs.Count; i++)
    {
        var program = body.Programs[i];
        if (program is null)
        {
            errors.Add($"programs[{i}] が null です");
            continue;
        }
        if (string.IsNullOrWhiteSpace(program.Title))
        {
            errors.Add($"programs[{i}].title は必須です");
            continue;
        }
        if (!DateTimeOffset.TryParse(program.StartAt, out var startAt))
        {
            errors.Add($"programs[{i}].startAt の形式が不正です");
            continue;
        }
        if (!DateTimeOffset.TryParse(program.EndAt, out var endAt))
        {
            errors.Add($"programs[{i}].endAt の形式が不正です");
            continue;
        }
        if (endAt <= startAt)
        {
            errors.Add($"programs[{i}] は endAt が startAt より後である必要があります");
            continue;
        }

        programs.Add(new EpgProgram(
            program.Title.Trim(),
            startAt,
            endAt,
            source,
            string.IsNullOrWhiteSpace(program.GenreCode) ? null : program.GenreCode.Trim(),
            string.IsNullOrWhiteSpace(program.GenreName) ? null : program.GenreName.Trim()));
    }

    if (errors.Count > 0)
        return Results.BadRequest(new { error = "入力に誤りがあります", details = errors });

    var (importedCount, currentChanged) = await programInfoService.ImportProgramsAsync(
        body.Channel.Trim(), programs, ctx.RequestAborted);

    logger.LogInformation("外部EPG APIで取り込みました: channel={Channel} source={Source} count={Count}",
        body.Channel.Trim(), source, importedCount);

    return Results.Json(new
    {
        ok = true,
        channel = body.Channel.Trim(),
        source,
        importedCount,
        currentChanged
    });
});

// 勢いリスト（getchannels 互換 XML、2秒キャッシュ）
app.MapGet("/api/channels", (ChannelManager mgr, ChannelCatalog channelCatalog) =>
{
    var sb = new StringBuilder();
    sb.Append("<channels status=\"ok\">");
    int threadId = 0;
    foreach (var info in channelCatalog.All)
    {
        var (force, viewers, totalComments, lastResNo) = mgr.GetAggregatedStats(ChannelsStreamPayloads.GetSourceKeys(info));
        var tag = info.Bs ? "bs_channel" : "channel";
        sb.Append($"<{tag}>");
        sb.Append($"<id>{info.Id}</id>");
        if (!info.Bs) sb.Append($"<no>{info.Id}</no>");
        sb.Append($"<name>{SecurityElement.Escape(info.Name)}</name>");
        sb.Append($"<video>{info.Video}</video>");
        sb.Append("<thread>");
        sb.Append($"<id>{++threadId}</id>");
        sb.Append(lastResNo > 0 ? $"<last_res>{lastResNo}</last_res>" : "<last_res />");
        sb.Append($"<force>{force}</force>");
        sb.Append($"<viewers>{viewers}</viewers>");
        sb.Append($"<comments>{totalComments}</comments>");
        sb.Append("</thread>");
        sb.Append($"</{tag}>");
    }
    sb.Append("</channels>");
    return Results.Content(sb.ToString(), "text/xml; charset=utf-8");
}).CacheOutput(p => p.Expire(TimeSpan.FromSeconds(2)));

// 勢いリスト WebSocket API（接続直後 snapshot、その後 stats を全チャンネルまとめて push）
app.Map("/api/channels/ws", async (HttpContext ctx, ChannelManager mgr, IConfiguration config,
    ChannelsStreamBroadcaster streamBroadcaster, ProgramInfoService programInfoService,
    ChannelCatalog channelCatalog, IHostApplicationLifetime lifetime) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);
    await HandleChannelsStreamAsync(ws, mgr, config, streamBroadcaster, programInfoService, channelCatalog,
        channelsStreamIntervalSec, cts.Token);
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<ChannelsStreamBroadcaster>()
            .CloseAllAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    catch { }
});


// ニコニコログイン（メール+パスワード → user_session または 2FA トークンを返す）
app.MapPost("/api/login", async (HttpContext ctx, ILogger<Program> logger) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<NicovideoLoginRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
        return Results.Json(new { error = "メールアドレスとパスワードを入力してください" });

    logger.LogInformation("ニコニコログイン試行: {Email}", body.Email);
    var r = await NicovideoAuth.LoginAsync(body.Email, body.Password, body.MfaTrustedDeviceToken, ctx.RequestAborted);
    if (r.Error != null) return Results.Json(new { error = r.Error });
    if (r.MfaRequired) { logger.LogInformation("2FA required: {Email}", body.Email); return Results.Json(new { mfaRequired = true, mfaToken = r.MfaToken }); }
    logger.LogInformation("ニコニコログイン成功: {Email}", body.Email);
    return Results.Json(new { userSession = r.UserSession, mfaTrustedDeviceToken = r.MfaTrustedDeviceToken });
});

// 2FA ワンタイムパスワード送信
app.MapPost("/api/login/mfa", async (HttpContext ctx, ILogger<Program> logger) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<NicovideoMfaRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.MfaToken) || string.IsNullOrWhiteSpace(body.Otp))
        return Results.Json(new { error = "トークンと OTP が必要です" });

    var r = await NicovideoAuth.SubmitMfaAsync(body.MfaToken, body.Otp, body.TrustDevice, ctx.RequestAborted);
    if (r.Error != null) return Results.Json(new { error = r.Error });
    logger.LogInformation("2FA 認証成功");
    return Results.Json(new { userSession = r.UserSession, mfaTrustedDeviceToken = r.MfaTrustedDeviceToken });
});

app.Run();

static bool ApiKeysEqual(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length &&
        CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static DateOnly ToBroadcastDate(DateTimeOffset utc, TimeZoneInfo timeZone)
{
    var local = TimeZoneInfo.ConvertTime(utc, timeZone);
    var date = DateOnly.FromDateTime(local.DateTime);
    return local.Hour < 5 ? date.AddDays(-1) : date;
}

// CreateSourceStatus / GetSourceKeys / snapshot・stats ペイロード生成は
// ChannelsStreamPayloads に集約（複数クライアントで使い回すため）。

static async Task HandleChannelsStreamAsync(
    WebSocket ws,
    ChannelManager mgr,
    IConfiguration config,
    ChannelsStreamBroadcaster streamBroadcaster,
    ProgramInfoService programInfoService,
    ChannelCatalog channelCatalog,
    int intervalSec,
    CancellationToken ct)
{
    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var receiveTask = DrainWebSocketAsync(ws, receiveCts.Token);
    var clientId = streamBroadcaster.Add(ws);

    try
    {
        await streamBroadcaster.SendAsync(clientId,
            ChannelsStreamPayloads.CreateChannelsSnapshot(mgr, config, programInfoService, channelCatalog, intervalSec), ct);

        // 以後の stats 配信は ChannelsStatsBroadcastService が全クライアントへ一括配信するため、
        // ここでは接続維持（受信ドレイン）と切断検知だけを行う。
        await receiveTask;
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    catch (InvalidOperationException) { }
    finally
    {
        streamBroadcaster.Remove(clientId);
        await receiveCts.CancelAsync();
        try { await receiveTask; } catch { }
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
    }
}

static async Task DrainWebSocketAsync(WebSocket ws, CancellationToken ct)
{
    var buffer = new byte[256];
    try
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    catch (InvalidOperationException) { }
}

record NicovideoLoginRequest(string Email, string Password, string? MfaTrustedDeviceToken = null);
record NicovideoMfaRequest(string MfaToken, string Otp, bool TrustDevice = true);

sealed record EpgImportRequest(
    string Channel,
    string? Source,
    string? CapturedAt,
    List<EpgImportProgramRequest> Programs);

sealed record EpgImportProgramRequest(
    string Title,
    string StartAt,
    string EndAt,
    string? GenreCode,
    string? GenreName);
