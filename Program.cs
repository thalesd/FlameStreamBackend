using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlameStreamBackend.Services;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

var serverConfig = builder.Configuration.GetSection("Server");

var libraryRoot = Path.GetFullPath(serverConfig.GetValue<string>("LibraryRoot") ?? "D:/Media");
var cacheRoot = Path.GetFullPath(serverConfig.GetValue<string>("CacheRoot") ?? "D:/Cache");
var hlsSegmentDuration = serverConfig.GetValue<int?>("Hls:SegmentSeconds") ?? 10;
var hwType = serverConfig.GetValue<string>("Hls:HardwareAcceleration") ?? "None";

// idle-stop settings
var idleCheckInterval = TimeSpan.FromSeconds(30);
var idleTimeout = TimeSpan.FromSeconds(90);

// Add services to the container.
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var registry = new TranscodeRegistry(maxConcurrent: 5);
builder.Services.AddSingleton(registry);

var app = builder.Build();

// Ensure dirs
Directory.CreateDirectory(libraryRoot);
Directory.CreateDirectory(cacheRoot);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() => registry.StopAll());
AppDomain.CurrentDomain.ProcessExit += (_, __) => registry.StopAll();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

var hlsContentTypes = new FileExtensionContentTypeProvider();
hlsContentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
hlsContentTypes.Mappings[".ts"] = "video/MP2T";
hlsContentTypes.Mappings[".m4s"] = "video/iso.segment";
hlsContentTypes.Mappings[".mp4"] = "video/mp4"; // in case you drop in init.mp4 for CMAF later
hlsContentTypes.Mappings[".vtt"] = "text/vtt";

// Track last access time per HLS job/hash
var lastAccessUtc = new ConcurrentDictionary<string, DateTime>();

// Update last - access before static files handle /hls/**
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/hls", out var remainder))
    {
        // Expect /hls/{hash}/...
        var segments = remainder.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is { Length: >= 1 })
        {
            var hash = segments[0];
            lastAccessUtc[hash] = DateTime.UtcNow;
        }
    }
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(cacheRoot),
    RequestPath = "/hls",
    ContentTypeProvider = hlsContentTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// Background idle killer
_ = Task.Run(async () =>
{
    var timer = new PeriodicTimer(idleCheckInterval);
    while (await timer.WaitForNextTickAsync())
    {
        var now = DateTime.UtcNow;
        foreach (var kv in lastAccessUtc.ToArray())
        {
            if (now - kv.Value > idleTimeout)
            {
                // kv.Key is the hash = our registry key
                registry.Stop(kv.Key);
                lastAccessUtc.TryRemove(kv.Key, out _);
            }
        }
    }
});

//Helpers
string HashId(string s) => Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
string SafeUnder(string root, string? rel)
{
    rel ??= "";
    var full = Path.GetFullPath(Path.Combine(root, rel));
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException();
    return full;
}
static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".mp4" => "video/mp4",
    ".m3u8" => "application/vnd.apple.mpegurl",
    ".ts" => "video/MP2T",
    ".m4s" => "video/iso.segment",
    ".vtt" => "text/vtt",
    _ => "application/octet-stream"
};

// ---------- Progressive file (keep for testing) ----------
app.MapGet("/media/{**path}", (string path) =>
{
    try
    {
        var full = SafeUnder(libraryRoot, path);
        if (!File.Exists(full)) return Results.NotFound();
        return Results.File(full, Mime(full), enableRangeProcessing: true);
    }
    catch { return Results.BadRequest(); }
});

app.MapGet("/stopTranscoding", (HttpContext ctx) =>
{
    ctx.RequestServices.GetRequiredService<TranscodeRegistry>().StopAll();
});

// ---------- HLS generator endpoint ----------
app.MapGet("/stream/{**path}", async (HttpContext ctx, string path) =>
{
    Console.WriteLine($"Request received for /stream/{path}");

    // path might be "Movie.mp4.m3u8"
    if (!path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Stream endpoint requires .m3u8 suffix." });

    var clean = path[..^5]; // strip ".m3u8"

    try
    {
        var src = SafeUnder(libraryRoot, clean);
        if (!File.Exists(src)) return Results.NotFound();

        var hash = HashId(src);
        var outDir = Path.Combine(cacheRoot, hash);
        var playlist = Path.Combine(outDir, "stream.m3u8");
        var segPattern = Path.Combine(outDir, "seg_%05d.ts");

        // Touch last-access so idle killer doesn't nuke newly-started jobs
        lastAccessUtc[hash] = DateTime.UtcNow;

        if (!File.Exists(playlist))
        {
            Directory.CreateDirectory(outDir);

            var audioCodec = GetAudioCodec(src);
            var videoCodec = GetVideoCodec(src);
            var subtitleFile = GetVideoSubtitles(src);

            if (subtitleFile != null && subtitleFile.EndsWith(".srt"))
            {
                var vttPath = Path.ChangeExtension(subtitleFile, ".vtt");
                var convertArgs = new[] { "-i", subtitleFile, vttPath };
                var convertPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                foreach (var a in convertArgs) convertPsi.ArgumentList.Add(a);
                using var convertProc = Process.Start(convertPsi);
                convertProc.WaitForExit();
                subtitleFile = vttPath;
            }

            // Transcode to H.264/AAC HLS (works for any input)
            var args = new List<string>
            {
                "-hide_banner", "-y", "-nostdin", "-loglevel", "info"
            };

            if (hwType.Equals("Nvidia", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", src });
                args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-b:v", "5M" });
            }
            else if (hwType.Equals("Amd", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-hwaccel", "dxva2", "-i", src });
                args.AddRange(new[] { "-c:v", "h264_amf", "-b:v", "5M", "-profile:v", "high", "-level", "4.1" });
            }
            else
            {
                // Fallback to CPU
                args.AddRange(new[] { "-i", src });
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23" });
            }

            args.Add("-pix_fmt"); args.Add("yuv420p");

            if(audioCodec == "aac")
            {
                args.AddRange(new List<string>
                {
                    "-c:a", "copy"
                });
            }
            else
            {
                args.AddRange(new List<string>
                {
                    "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000"
                });
            }

            args.AddRange(new List<string>
            {
                "-map", "0:v:0", "-map", "0:a:0",
                "-tune", "zerolatency",
                "-hls_time", hlsSegmentDuration.ToString(),
                "-hls_flags", "independent_segments+append_list+temp_file",
                "-hls_segment_filename", segPattern,
                "-force_key_frames",$"expr:gte(t,n_forced*{hlsSegmentDuration})",
                "-start_number","0",
                "-hls_list_size", "0",
                "-hls_playlist_type", "vod",
                playlist
            });

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                WorkingDirectory = outDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            var reportPath = Path.Combine(outDir, "ffmpeg_report.log");

            // Use ArgumentList to avoid quoting issues
            foreach (var a in args) psi.ArgumentList.Add(a);

            if (subtitleFile != null)
            {
                var subArgs = new List<string>
                {
                    "-hide_banner", "-y", "-nostdin",
                    "-loglevel", "info", "-report",
                    "-c:s", "webvtt",
                    "-i", subtitleFile
                };
            }

            Console.WriteLine($"[HLS] ffmpeg start\n  src: {src}\n  outDir: {outDir}\n  segPattern: {segPattern}\n  playlist: {playlist}");

            _ = Task.Run(async () =>
            {
                try
                {
                    // Start & track under the hash key
                    var proc = await ctx.RequestServices
                        .GetRequiredService<TranscodeRegistry>()
                        .StartAsync(hash, psi);
                }
                catch { }
            });

            // Wait briefly so index.m3u8 exists before replying
            var sw = Stopwatch.StartNew();
            while (!System.IO.File.Exists(playlist) && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(200);

            if (!File.Exists(playlist))
            {
                Console.Error.WriteLine("[HLS] Playlist not created. See ffmpeg log in: " + outDir);
                return Results.StatusCode(500);
            }
        }

        // Redirect to the static path so segment relative URLs resolve
        var redirect = $"/hls/{hash}/stream.m3u8";
        Console.WriteLine($"[HLS] Redirect -> {redirect}");
        return Results.Redirect(redirect, false);
    }
    catch (Exception ex)
    {
        Console.WriteLine("[HLS] Exception: " + ex);
        return Results.BadRequest(new { error = ex.Message });
    }
});

string GetAudioCodec(string inputFile)
{
    var psi = new ProcessStartInfo
    {
        FileName = "ffprobe",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("quiet");
    psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
    psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("a:0");
    psi.ArgumentList.Add(inputFile);

    using var proc = Process.Start(psi);
    string output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    if (string.IsNullOrEmpty(output)) return "";
    using var doc = JsonDocument.Parse(output);
    if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        return "";

    var codec = streams[0].GetProperty("codec_name").GetString();

    return codec ?? "";
}

string GetVideoCodec(string inputFile)
{
    var psi = new ProcessStartInfo
    {
        FileName = "ffprobe",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("quiet");
    psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
    psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("v:0");
    psi.ArgumentList.Add(inputFile);

    using var proc = Process.Start(psi);
    string output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    if (string.IsNullOrEmpty(output)) return "";
    using var doc = JsonDocument.Parse(output);
    if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        return "";

    var codec = streams[0].GetProperty("codec_name").GetString();

    return codec ?? "";
}

string? GetVideoSubtitles(string inputFile)
{
    var subtitleSrt = Path.ChangeExtension(inputFile, ".srt");
    var subtitleVtt = Path.ChangeExtension(inputFile, ".vtt");
    string? subtitleFile = null;
    if (File.Exists(subtitleVtt)) subtitleFile = subtitleVtt;
    else if (File.Exists(subtitleSrt)) subtitleFile = subtitleSrt;

    return subtitleFile;
}

app.MapGet("/api/media", () =>
{
    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi" };
    var files = Directory.GetFiles(libraryRoot, "*.*", SearchOption.TopDirectoryOnly)
        .Where(f => exts.Contains(Path.GetExtension(f)))
        .Select(f =>
        {
            var relPath = Path.GetRelativePath(libraryRoot, f).Replace("\\", "/");
            var subtitleSrt = Path.ChangeExtension(f, ".srt");
            var subtitleVtt = Path.ChangeExtension(f, ".vtt");
            string? subtitle = null;
            if (File.Exists(subtitleVtt)) subtitle = Path.GetFileName(subtitleVtt);
            else if (File.Exists(subtitleSrt)) subtitle = Path.GetFileName(subtitleSrt);

            return new
            {
                name = Path.GetFileName(f),
                path = relPath,
                url = $"/stream/{Uri.EscapeDataString(relPath)}.m3u8",
                subUrl = $"/subs/{Uri.EscapeDataString(relPath)}_vtt.m3u8"
            };
        });

    return Results.Ok(files);
});

app.Run();
