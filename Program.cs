using System.Text;
using FlameStreamBackend;
using FlameStreamBackend.Helpers;
using FlameStreamBackend.Services;
using Microsoft.AspNetCore.StaticFiles;

// Required for Encoding.GetEncoding(1252) to work on Linux/Docker
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
var serverConfig = builder.Configuration.GetSection("Server");

var settings = new ServerSettings(
    LibraryRoot:           Path.GetFullPath(serverConfig.GetValue<string>("LibraryRoot") ?? "D:/Media"),
    CacheRoot:             Path.GetFullPath(serverConfig.GetValue<string>("CacheRoot")   ?? "D:/Cache"),
    HlsSegmentSeconds:     serverConfig.GetValue<int?>("Hls:SegmentSeconds") ?? 10,
    HardwareAcceleration:  serverConfig.GetValue<string>("Hls:HardwareAcceleration") ?? "None"
);

// Self-hosted native-app release channel (APK, OTA web-bundle zip, Windows installer +
// electron-updater feed). Served under /api/app; manifest is version.json in this folder.
var appReleasesRoot = Path.GetFullPath(
    serverConfig.GetValue<string>("AppReleasesRoot")
    ?? Path.Combine(builder.Environment.ContentRootPath, "AppReleases"));

builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
        // Exposed so the browser player can read the true stream start offset (see
        // HandleStreamRequestAsync) and align its clock + subtitle cue timing to it.
        .WithExposedHeaders("X-Hls-Start-Offset")));
builder.Services.AddOpenApi();

builder.Services.AddSingleton(settings);

var registry = new TranscodeRegistry(maxConcurrent: 5);
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton<IHostedService>(registry);

builder.Services.AddSingleton<FFprobeService>();
builder.Services.AddSingleton<MediaInfoCache>();
builder.Services.AddSingleton<HlsService>();
builder.Services.AddSingleton<MediaLibraryService>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddSingleton<WatchHistoryService>();
builder.Services.AddSingleton<ListService>();
builder.Services.AddHostedService<IdleCleanupService>();

var app = builder.Build();

// Ring buffer behind /api/castlog — receiver-side dlog() lines plus the server-side
// request markers below, so one endpoint shows the whole cast story in order.
var castLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
void CastLog(string s)
{
    var entry = $"{DateTime.Now:HH:mm:ss.fff} {s}";
    castLog.Enqueue(entry);
    while (castLog.Count > 300) castLog.TryDequeue(out _);
    Console.WriteLine("[CASTLOG] " + entry);
}

Directory.CreateDirectory(settings.LibraryRoot);
Directory.CreateDirectory(settings.CacheRoot);
Directory.CreateDirectory(appReleasesRoot);
Directory.CreateDirectory(Path.Combine(appReleasesRoot, "android"));
Directory.CreateDirectory(Path.Combine(appReleasesRoot, "windows"));

await app.Services.GetRequiredService<WatchHistoryService>().EnsureSchemaAsync();
await app.Services.GetRequiredService<ListService>().EnsureSchemaAsync();

AppDomain.CurrentDomain.ProcessExit += (_, __) => registry.StopAll();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();

// Serves wwwroot/ — includes receiver.html for the Chromecast Custom Receiver.
// Chromecast caches the receiver aggressively; send no-store so each cast pulls the
// current receiver.html/js (otherwise edits to the custom receiver logic won't take
// effect on the device without a reboot).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        if (name.StartsWith("receiver.", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    }
});

var hlsContentTypes = new FileExtensionContentTypeProvider();
hlsContentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
hlsContentTypes.Mappings[".ts"]   = "video/MP2T";
hlsContentTypes.Mappings[".m4s"]  = "video/iso.segment";
hlsContentTypes.Mappings[".mp4"]  = "video/mp4";
hlsContentTypes.Mappings[".vtt"]  = "text/vtt";

// Update last-access timestamp before static files handle /hls/**
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/hls", out var remainder))
    {
        // Log segment fetches from non-local callers (i.e. the Chromecast) into the cast
        // relay, so /api/castlog shows whether the TV ever actually pulls media.
        if (!System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback)
            && !Equals(ctx.Connection.RemoteIpAddress?.ToString(), "192.168.0.50"))
            CastLog($"[HLS-REQ from {ctx.Connection.RemoteIpAddress}] {ctx.Request.Path}");

        var segs = remainder.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs is { Length: >= 1 })
        {
            var hls = ctx.RequestServices.GetRequiredService<HlsService>();
            hls.TouchJob(segs[0]);
            if (segs.Length >= 3 && segs[1] == "temp")
                hls.TouchJob(segs[2]);
        }
    }
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(settings.CacheRoot),
    RequestPath  = "/hls",
    ContentTypeProvider = hlsContentTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// Native-app update artifacts under /api/app/**: android/bundle.zip (capgo OTA),
// android/latest.apk (full reinstall), windows/latest.yml + *-Setup.exe (electron-updater).
// no-store so every launch sees the freshly published release, not a cached one.
var appContentTypes = new FileExtensionContentTypeProvider();
appContentTypes.Mappings[".apk"] = "application/vnd.android.package-archive";
appContentTypes.Mappings[".zip"] = "application/zip";
appContentTypes.Mappings[".yml"] = "text/yaml";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(appReleasesRoot),
    RequestPath  = "/api/app",
    ContentTypeProvider = appContentTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate"
});

app.MapGet("/media/{**path}", (string path) =>
{
    try
    {
        var full = PathHelper.SafeUnder(settings.LibraryRoot, path);
        if (!File.Exists(full)) return Results.NotFound();
        return Results.File(full, PathHelper.Mime(full), enableRangeProcessing: true);
    }
    catch { return Results.BadRequest(); }
});

app.MapGet("/stopTranscoding", (TranscodeRegistry reg) => reg.StopAll());

app.MapGet("/stream/{**path}", async (HttpContext ctx, string path, HlsService hls) =>
{
    CastLog($"[STREAM-REQ from {ctx.Connection.RemoteIpAddress}] /stream/{path}{ctx.Request.QueryString}");

    if (!path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Stream endpoint requires .m3u8 suffix." });

    var clean = path[..^5];
    ctx.Request.Query.TryGetValue("start", out var startStr);
    double.TryParse(startStr, System.Globalization.NumberStyles.Any,
        System.Globalization.CultureInfo.InvariantCulture, out var startSeconds);

    try
    {
        var src = PathHelper.SafeUnder(settings.LibraryRoot, clean);
        if (!File.Exists(src)) return Results.NotFound();
        return await hls.HandleStreamRequestAsync(ctx, src, startSeconds);
    }
    catch (Exception ex)
    {
        Console.WriteLine("[HLS] Exception: " + ex);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/media", async (MediaLibraryService lib) =>
    Results.Ok(await lib.BuildTreeAsync()));

app.MapGet("/subs/{**path}", async (HttpContext ctx, string path, SubtitleService subs) =>
    await subs.GetSubtitlesAsync(ctx, path));

// Scene preview frame for the seek-bar hover popup. ?t=<seconds> is snapped to a fixed
// grid and the extracted JPEG is cached under the title's HLS cache dir.
app.MapGet("/api/thumb/{**path}", async (HttpContext ctx, string path, double? t, ThumbnailService thumbs) =>
{
    try
    {
        var src = PathHelper.SafeUnder(settings.LibraryRoot, path);
        if (!File.Exists(src)) return Results.NotFound();
        var file = await thumbs.GetOrCreateAsync(src, t ?? 0, ctx.RequestAborted);
        if (file is null) return Results.StatusCode(500);
        // Immutable per (path, bucket): safe to cache hard in the browser.
        ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
        return Results.File(file, "image/jpeg");
    }
    catch (UnauthorizedAccessException) { return Results.BadRequest(); }
    catch (OperationCanceledException)  { return Results.Empty; }
});

app.MapPost("/api/preprocess", async (string path, HlsService hls) =>
{
    try
    {
        var src = PathHelper.SafeUnder(settings.LibraryRoot, path);
        if (!File.Exists(src)) return Results.NotFound();
        var started = await hls.EnsurePreprocessAsync(src);
        return Results.Ok(new { started });
    }
    catch (UnauthorizedAccessException) { return Results.BadRequest(); }
});

app.MapGet("/api/jobs", (HlsService hls) => Results.Ok(hls.GetActiveJobs()));

// ── Native-app auto-update manifest ──────────────────────────────────────────
// Single hand-maintained file the Android/Windows shells poll on launch to decide
// whether to OTA-swap the web bundle (capgo) or prompt a full reinstall. URLs inside
// are relative to BACKEND_BASE (see /api/app/** static serving above). Windows uses
// its own electron-updater latest.yml feed, not this manifest.
app.MapGet("/api/app/version", (HttpContext ctx) =>
{
    var manifest = Path.Combine(appReleasesRoot, "version.json");
    if (!File.Exists(manifest))
        return Results.NotFound(new { error = "no release published" });
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    return Results.Text(File.ReadAllText(manifest), "application/json");
});

// ── Cast receiver log relay ──────────────────────────────────────────────────
// The TV's remote-debug port isn't reachable on this device, so the custom receiver
// POSTs every dlog() line here instead; GET returns the ring buffer (which also
// carries the server-side /stream and /hls request markers logged above).
app.MapPost("/api/castlog", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var line = await reader.ReadToEndAsync();
    if (line.Length > 2000) line = line[..2000];
    CastLog(line);
    return Results.Ok();
});
app.MapGet("/api/castlog", () => Results.Text(string.Join("\n", castLog), "text/plain"));

app.MapPost("/api/jobs/{key}/cancel", (string key, HlsService hls) =>
    Results.Ok(new { cancelled = hls.CancelJob(key) }));

app.MapGet("/api/watch-history/{**path}", async (string path, WatchHistoryService history) =>
    Results.Ok(await history.GetAsync(path)));

app.MapPost("/api/watch-history", async (WatchHistoryRequest body, WatchHistoryService history) =>
{
    await history.UpsertAsync(body.Path, body.PositionSeconds, body.DurationSeconds);
    return Results.Ok();
});

app.MapGet("/api/continue-watching", async (WatchHistoryService history) =>
    Results.Ok(await history.GetContinueWatchingAsync()));

// ── My List ──────────────────────────────────────────────────────────────────
app.MapGet("/api/list", async (ListService list) =>
    Results.Ok(await list.GetAllAsync()));

app.MapPost("/api/list", async (ListRequest body, ListService list) =>
{
    await list.AddAsync(body.Path);
    return Results.Ok();
});

// POST (not DELETE) to stay within the credential-free wildcard-CORS pattern the frontend uses.
app.MapPost("/api/list/remove", async (ListRequest body, ListService list) =>
{
    await list.RemoveAsync(body.Path);
    return Results.Ok();
});

app.MapPost("/api/cache/delete", (string path, HlsService hls) =>
{
    try
    {
        var src = PathHelper.SafeUnder(settings.LibraryRoot, path);
        hls.DeleteCache(src);
        return Results.Ok();
    }
    catch (UnauthorizedAccessException) { return Results.BadRequest(); }
});

app.Run();

record WatchHistoryRequest(string Path, double PositionSeconds, double DurationSeconds);
record ListRequest(string Path);
