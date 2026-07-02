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

builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
builder.Services.AddOpenApi();

builder.Services.AddSingleton(settings);

var registry = new TranscodeRegistry(maxConcurrent: 5);
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton<IHostedService>(registry);

builder.Services.AddSingleton<FFprobeService>();
builder.Services.AddSingleton<HlsService>();
builder.Services.AddSingleton<MediaLibraryService>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<WatchHistoryService>();
builder.Services.AddHostedService<IdleCleanupService>();

var app = builder.Build();

Directory.CreateDirectory(settings.LibraryRoot);
Directory.CreateDirectory(settings.CacheRoot);

await app.Services.GetRequiredService<WatchHistoryService>().EnsureSchemaAsync();

AppDomain.CurrentDomain.ProcessExit += (_, __) => registry.StopAll();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();

// Serves wwwroot/ — includes receiver.html for the Chromecast Custom Receiver
app.UseStaticFiles();

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
    Console.WriteLine($"Request received for /stream/{path}");

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

app.MapGet("/api/media", (MediaLibraryService lib) =>
    Results.Ok(lib.BuildTree(settings.LibraryRoot)));

app.MapGet("/subs/{**path}", async (HttpContext ctx, string path, SubtitleService subs) =>
    await subs.GetSubtitlesAsync(ctx, path));

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
