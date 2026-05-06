using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
builder.Services.AddSingleton<IHostedService>(registry);

var app = builder.Build();

// Ensure dirs
Directory.CreateDirectory(libraryRoot);
Directory.CreateDirectory(cacheRoot);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
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

// Track the currently active seek job per base file: baseHash -> seekHash
var activeSeekJobs = new ConcurrentDictionary<string, string>();

// Map each job key (baseHash or seekHash) to its output directory
var jobPaths = new ConcurrentDictionary<string, string>();

// Update last-access before static files handle /hls/**
// Handles both /hls/{baseHash}/... and /hls/{baseHash}/temp/{seekHash}/...
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/hls", out var remainder))
    {
        var segs = remainder.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs is { Length: >= 1 })
        {
            lastAccessUtc[segs[0]] = DateTime.UtcNow;
            if (segs.Length >= 3 && segs[1] == "temp")
                lastAccessUtc[segs[2]] = DateTime.UtcNow;
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
var appStopping = lifetime.ApplicationStopping;
_ = Task.Run(async () =>
{
    var timer = new PeriodicTimer(idleCheckInterval);
    while (await timer.WaitForNextTickAsync(appStopping).ConfigureAwait(false))
    {
        var now = DateTime.UtcNow;
        foreach (var kv in lastAccessUtc.ToArray())
        {
            if (now - kv.Value > idleTimeout)
            {
                registry.Stop(kv.Key);
                lastAccessUtc.TryRemove(kv.Key, out _);

                // Resolve directory: prefer jobPaths entry, fall back to cacheRoot/{key} for legacy
                jobPaths.TryRemove(kv.Key, out var cacheDir);
                cacheDir ??= Path.Combine(cacheRoot, kv.Key);

                try
                {
                    if (Directory.Exists(cacheDir))
                    {
                        var playlistFile = Path.Combine(cacheDir, "stream.m3u8");
                        if (File.Exists(playlistFile) && !File.ReadAllText(playlistFile).Contains("#EXT-X-ENDLIST"))
                        {
                            Console.WriteLine($"[Idle] Deleting incomplete cache: {kv.Key}");
                            Directory.Delete(cacheDir, true);
                        }
                        else
                        {
                            Console.WriteLine($"[Idle] Keeping completed cache: {kv.Key}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Idle] Error cleaning cache {kv.Key}: {ex.Message}");
                }
            }
        }

        // Clean up temp seek dirs under any fully-completed main encoding
        try
        {
            foreach (var mainDir in Directory.GetDirectories(cacheRoot))
            {
                var tempDir = Path.Combine(mainDir, "temp");
                if (!Directory.Exists(tempDir)) continue;
                var mainPlaylist = Path.Combine(mainDir, "stream.m3u8");
                if (!File.Exists(mainPlaylist)) continue;
                if (!File.ReadAllText(mainPlaylist).Contains("#EXT-X-ENDLIST")) continue;
                Directory.Delete(tempDir, true);
                Console.WriteLine($"[Idle] Deleted temp dir for completed encoding: {Path.GetFileName(mainDir)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Idle] Error during temp cleanup: {ex.Message}");
        }
    }
}, appStopping);

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

string MainDir(string baseHash) => Path.Combine(cacheRoot, baseHash);
string TempDir(string baseHash, string seekHash) => Path.Combine(cacheRoot, baseHash, "temp", seekHash);

// Count how many segments FFmpeg has written to the playlist so far.
// More reliable than File.Exists: FFmpeg appends the segment to the playlist only
// after it is fully flushed to disk, so a count > N means segment N is readable.
int CountEncodedSegments(string playlistPath)
{
    if (!File.Exists(playlistPath)) return 0;
    try { return File.ReadAllLines(playlistPath).Count(l => l.TrimEnd().EndsWith(".ts", StringComparison.OrdinalIgnoreCase)); }
    catch { return 0; }
}

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

    if (!path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Stream endpoint requires .m3u8 suffix." });

    var clean = path[..^5];

    ctx.Request.Query.TryGetValue("start", out var startStr);
    double.TryParse(startStr, System.Globalization.NumberStyles.Any,
        System.Globalization.CultureInfo.InvariantCulture, out var startSeconds);
    var startSegment = (int)Math.Floor(startSeconds / hlsSegmentDuration);

    try
    {
        var src = SafeUnder(libraryRoot, clean);
        if (!File.Exists(src)) return Results.NotFound();

        var baseHash     = HashId(src);
        var mainDir      = MainDir(baseHash);
        var mainPlaylist = Path.Combine(mainDir, "stream.m3u8");

        // Always ensure the full-file encoding is running
        if (!IsPlaylistComplete(mainPlaylist) && !registry.IsRunning(baseHash))
        {
            Console.WriteLine($"[HLS] Starting main encoding for {baseHash}");
            StartFfmpegJob(baseHash, mainDir, src, 0.0);
        }

        // Determine which job/dir serves this request
        string hash, outDir, urlBase;
        bool useOriginalForSeek;

        if (startSeconds > 0)
        {
            // Check if main encoding has already produced the needed segment.
            // CountEncodedSegments reads the playlist; FFmpeg appends a segment entry only
            // after the .ts file is fully flushed, so this is race-free.
            if (CountEncodedSegments(mainPlaylist) > startSegment)
            {
                hash = baseHash; outDir = mainDir; urlBase = $"/hls/{baseHash}";
                useOriginalForSeek = true;
                Console.WriteLine($"[HLS] Main has segment {startSegment}, serving from main stream");

                if (activeSeekJobs.TryRemove(baseHash, out var obsoleteSeekHash))
                {
                    Console.WriteLine($"[HLS] Cleaning up seek job {obsoleteSeekHash} (main caught up)");
                    CleanupSeekJob(baseHash, obsoleteSeekHash);
                }
            }
            else
            {
                var seekHash = HashId($"{src}_{startSeconds}");
                hash = seekHash; outDir = TempDir(baseHash, seekHash); urlBase = $"/hls/{baseHash}/temp/{seekHash}";
                useOriginalForSeek = false;
                Console.WriteLine($"[HLS] Main doesn't have segment {startSegment} yet, using seek job {seekHash}");

                if (activeSeekJobs.TryGetValue(baseHash, out var prevSeekHash) && prevSeekHash != seekHash)
                {
                    Console.WriteLine($"[HLS] New seek position — stopping previous seek job {prevSeekHash}");
                    activeSeekJobs.TryRemove(baseHash, out _);
                    CleanupSeekJob(baseHash, prevSeekHash);
                }
                activeSeekJobs[baseHash] = seekHash;
            }
        }
        else
        {
            hash = baseHash; outDir = mainDir; urlBase = $"/hls/{baseHash}";
            useOriginalForSeek = false;
        }

        lastAccessUtc[hash] = DateTime.UtcNow;

        var playlist = Path.Combine(outDir, "stream.m3u8");

        if (!File.Exists(playlist))
        {
            if (startSeconds > 0 && !useOriginalForSeek)
            {
                Console.WriteLine($"[HLS] Starting seek encoding from {startSeconds}s");
                StartFfmpegJob(hash, outDir, src, startSeconds);
            }

            var sw = Stopwatch.StartNew();
            while (!File.Exists(playlist) && sw.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(200);

            if (!File.Exists(playlist))
            {
                Console.Error.WriteLine("[HLS] Playlist not created. See ffmpeg log in: " + outDir);
                return Results.StatusCode(500);
            }
        }

        // No seek — wait for at least 2 segments then serve dynamic playlist
        if (startSegment == 0 && startSeconds <= 0)
        {
            var seg1 = Path.Combine(outDir, "seg_00001.ts");
            if (!File.Exists(seg1))
            {
                var segSw = Stopwatch.StartNew();
                while (!File.Exists(seg1) && segSw.Elapsed < TimeSpan.FromSeconds(60))
                    await Task.Delay(500);
                Console.WriteLine($"[HLS] Waited {segSw.Elapsed.TotalSeconds:F1}s for initial segments");
            }
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
            var pl = BuildSubPlaylist(playlist, urlBase, 0, null);
            Console.WriteLine($"[HLS] Serving dynamic playlist from segment 0");
            return Results.Content(pl, "application/vnd.apple.mpegurl");
        }

        // For seek jobs FFmpeg internally starts from 0; when using the original stream,
        // target the actual segment index.
        var targetSegment = (startSeconds > 0 && !useOriginalForSeek) ? 0 : startSegment;

        // Wait until the playlist shows the target segment as fully written
        var seekSw = Stopwatch.StartNew();
        const int maxWaitSeconds = 600;

        while (CountEncodedSegments(playlist) <= targetSegment && seekSw.Elapsed < TimeSpan.FromSeconds(maxWaitSeconds))
        {
            if (IsPlaylistComplete(playlist))
            {
                Console.WriteLine($"[HLS] Encoding complete, segment {targetSegment} not found.");
                break;
            }
            await Task.Delay(500);
        }

        if (seekSw.Elapsed > TimeSpan.FromSeconds(5))
            Console.WriteLine($"[HLS] Segment wait completed in {seekSw.Elapsed.TotalSeconds:F1}s");

        if (CountEncodedSegments(playlist) <= targetSegment)
        {
            if (IsPlaylistComplete(playlist))
            {
                Console.Error.WriteLine($"[HLS] Segment {targetSegment} is beyond file duration.");
                return Results.StatusCode(416);
            }
            Console.Error.WriteLine($"[HLS] Still encoding; BuildSubPlaylist will serve from last available segment.");
        }

        int? mediaSeq = (startSeconds > 0 && !useOriginalForSeek) ? startSegment : null;
        var subPlaylist = BuildSubPlaylist(playlist, urlBase, targetSegment, mediaSeq);
        Console.WriteLine($"[HLS] Serving sub-playlist from segment {targetSegment}");
        return Results.Content(subPlaylist, "application/vnd.apple.mpegurl");
    }
    catch (Exception ex)
    {
        Console.WriteLine("[HLS] Exception: " + ex);
        return Results.BadRequest(new { error = ex.Message });
    }
});

string BuildSubPlaylist(string playlistPath, string urlBase, int startSegment, int? mediaSequenceOverride = null)
{
    var lines = File.ReadAllLines(playlistPath);
    var sb = new System.Text.StringBuilder();

    string targetDuration = $"#EXT-X-TARGETDURATION:{hlsSegmentDuration}";
    bool hasEndList = false;
    var segments = new List<(string extinf, string file)>();

    for (int i = 0; i < lines.Length; i++)
    {
        var line = lines[i].Trim();
        if (line.StartsWith("#EXT-X-TARGETDURATION:"))
            targetDuration = line;
        else if (line == "#EXT-X-ENDLIST")
            hasEndList = true;
        else if (line.StartsWith("#EXTINF:") && i + 1 < lines.Length)
        {
            segments.Add((line, lines[i + 1].Trim()));
            i++;
        }
    }

    // Start from the requested segment, or from the closest available segment
    // If seeking beyond available segments, still serve from the last one for continuity
    var from = startSegment;
    
    // If requested segment is beyond what's available
    if (from >= segments.Count)
    {
        // If the file is complete (has ENDLIST), clamp to the last segment
        if (hasEndList)
        {
            from = Math.Max(0, segments.Count - 1);
        }
        else
        {
            // File is still being encoded, start from the last available segment
            // This allows playback to continue while waiting for more segments
            from = Math.Max(0, segments.Count - 1);
        }
    }
    else
    {
        from = Math.Max(0, from);
    }

    sb.AppendLine("#EXTM3U");
    sb.AppendLine("#EXT-X-VERSION:3");
    sb.AppendLine(targetDuration);
    sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{mediaSequenceOverride ?? from}");
    if (!hasEndList)
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:EVENT");

    for (int i = from; i < segments.Count; i++)
    {
        sb.AppendLine(segments[i].extinf);
        sb.AppendLine($"{urlBase}/{segments[i].file}");
    }

    if (hasEndList)
        sb.AppendLine("#EXT-X-ENDLIST");

    return sb.ToString();
}

bool IsPlaylistComplete(string playlistPath)
{
    if (!File.Exists(playlistPath)) return false;
    try { return File.ReadAllText(playlistPath).Contains("#EXT-X-ENDLIST"); }
    catch { return false; }
}

void CleanupSeekJob(string baseHash, string seekHash)
{
    registry.Stop(seekHash);
    lastAccessUtc.TryRemove(seekHash, out _);
    jobPaths.TryRemove(seekHash, out _);
    var dir = TempDir(baseHash, seekHash);
    if (!Directory.Exists(dir)) return;
    if (!IsPlaylistComplete(Path.Combine(dir, "stream.m3u8")))
    {
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { Console.Error.WriteLine($"[Seek] Failed to delete seek cache {seekHash}: {ex.Message}"); }
    }
}

void StartFfmpegJob(string key, string outDir, string src, double startSeconds)
{
    Directory.CreateDirectory(outDir);

    var audioCodec = GetAudioCodec(src);
    var playlist   = Path.Combine(outDir, "stream.m3u8");
    var segPattern = Path.Combine(outDir, "seg_%05d.ts");

    var args = new List<string> { "-hide_banner", "-y", "-nostdin", "-loglevel", "info" };

    if (startSeconds > 0)
    {
        args.Add("-ss");
        args.Add(startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

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
        args.AddRange(new[] { "-i", src });
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-tune", "zerolatency" });
    }

    args.Add("-pix_fmt"); args.Add("yuv420p");

    if (audioCodec == "aac")
        args.AddRange(new[] { "-c:a", "copy" });
    else
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000" });

    // Shift output PTS so video.currentTime matches the original file's timeline
    if (startSeconds > 0)
    {
        args.Add("-output_ts_offset");
        args.Add(startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    args.AddRange(new[]
    {
        "-map", "0:v:0", "-map", "0:a:0",
        "-hls_time", hlsSegmentDuration.ToString(),
        "-hls_flags", "independent_segments",
        "-hls_segment_filename", segPattern,
        "-force_key_frames", $"expr:gte(t,n_forced*{hlsSegmentDuration})",
        "-start_number", "0",
        "-hls_list_size", "0",
        "-hls_playlist_type", "event",
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
    foreach (var a in args) psi.ArgumentList.Add(a);

    jobPaths[key]     = outDir;
    lastAccessUtc[key] = DateTime.UtcNow;

    Console.WriteLine($"[HLS] ffmpeg queued: {key}\n  cmd: ffmpeg {string.Join(" ", args)}\n  outDir: {outDir}");

    _ = Task.Run(async () =>
    {
        try { await registry.StartAsync(key, psi); }
        catch (Exception ex) { Console.Error.WriteLine($"[HLS] Failed to start ffmpeg for {key}: {ex.Message}"); }
    });
}

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


List<(int index, string language, string title, string codec)> GetEmbeddedSubtitles(string inputFile)
{
    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "subrip", "ass", "ssa", "webvtt", "mov_text", "ttml", "sami", "microdvd", "text" };

    var psi = new ProcessStartInfo
    {
        FileName = "ffprobe",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("quiet");
    psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
    psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("s");
    psi.ArgumentList.Add(inputFile);

    using var proc = Process.Start(psi)!;
    string output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    var result = new List<(int, string, string, string)>();
    if (string.IsNullOrEmpty(output)) return result;

    using var doc = JsonDocument.Parse(output);
    if (!doc.RootElement.TryGetProperty("streams", out var streams)) return result;

    int idx = 0;
    foreach (var stream in streams.EnumerateArray())
    {
        var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
        if (!supported.Contains(codec)) { idx++; continue; }

        string language = "", title = "";
        if (stream.TryGetProperty("tags", out var tags))
        {
            if (tags.TryGetProperty("language", out var lang)) language = lang.GetString() ?? "";
            if (tags.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
        }
        result.Add((idx++, language, title, codec));
    }

    return result;
}

(double duration, int width, int height) GetMediaInfo(string inputFile)
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
    psi.ArgumentList.Add("-show_format");
    psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("v:0");
    psi.ArgumentList.Add(inputFile);

    using var proc = Process.Start(psi)!;
    string output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    if (string.IsNullOrEmpty(output)) return (0, 0, 0);
    using var doc = JsonDocument.Parse(output);

    double duration = 0;
    int width = 0, height = 0;

    if (doc.RootElement.TryGetProperty("format", out var fmt) &&
        fmt.TryGetProperty("duration", out var dur) &&
        double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d))
        duration = d;

    if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
    {
        if (streams[0].TryGetProperty("width", out var w)) width = w.GetInt32();
        if (streams[0].TryGetProperty("height", out var h)) height = h.GetInt32();
    }

    return (duration, width, height);
}

var mediaExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi" };

List<object> BuildMediaTree(string dir, string relBase)
{
    var entries = new List<object>();

    foreach (var subDir in Directory.GetDirectories(dir).OrderBy(d => d))
    {
        var dirName = Path.GetFileName(subDir)!;
        var relPath = string.IsNullOrEmpty(relBase) ? dirName : $"{relBase}/{dirName}";
        entries.Add(new { type = "folder", name = dirName, path = relPath, children = BuildMediaTree(subDir, relPath) });
    }

    foreach (var f in Directory.GetFiles(dir).Where(f => mediaExts.Contains(Path.GetExtension(f))).OrderBy(f => f))
    {
        var fileName = Path.GetFileName(f);
        var relPath = string.IsNullOrEmpty(relBase) ? fileName : $"{relBase}/{fileName}";
        var escaped = string.Join("/", relPath.Split('/').Select(Uri.EscapeDataString));
        var subtitleSrt = Path.ChangeExtension(f, ".srt");
        var subtitleVtt = Path.ChangeExtension(f, ".vtt");
        var embedded = GetEmbeddedSubtitles(f);
        var (duration, width, height) = GetMediaInfo(f);

        entries.Add(new
        {
            type = "file",
            name = fileName,
            path = relPath,
            url = $"/stream/{escaped}.m3u8",
            directUrl = $"/media/{escaped}",
            subUrl = (File.Exists(subtitleVtt) || File.Exists(subtitleSrt))
                ? $"/subs/{escaped}"
                : null,
            embeddedSubtitles = embedded.Select(s => new
            {
                url = $"/subs/{escaped}?track={s.index}",
                s.language,
                s.title,
                s.codec
            }).ToArray(),
            duration,
            width,
            height
        });
    }

    return entries;
}

app.MapGet("/api/media", () => Results.Ok(BuildMediaTree(libraryRoot, "")));

app.MapGet("/subs/{**path}", async (HttpContext httpCtx, string path) =>
{
    try
    {
        var src = SafeUnder(libraryRoot, path);

        // ── Embedded subtitle extraction: /subs/video.mkv?track=N ────────────
        if (httpCtx.Request.Query.TryGetValue("track", out var trackStr) &&
            int.TryParse(trackStr, out var trackIndex))
        {
            // Get the codec type for this subtitle track
            var subtitles = GetEmbeddedSubtitles(src);
            var trackInfo = subtitles.FirstOrDefault(s => s.index == trackIndex);
            
            if (trackInfo == default)
            {
                Console.Error.WriteLine($"[SUBS] Track {trackIndex} not found in {src}");
                return Results.NotFound();
            }

            Console.WriteLine($"[SUBS] Extracting embedded track {trackIndex}: codec={trackInfo.codec}, lang={trackInfo.language}");

            var cacheVtt = Path.Combine(cacheRoot, "subs", $"{HashId($"{src}_s{trackIndex}")}.vtt");
            Directory.CreateDirectory(Path.GetDirectoryName(cacheVtt)!);

            if (!File.Exists(cacheVtt))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");            psi.ArgumentList.Add(src);
                psi.ArgumentList.Add("-map");          psi.ArgumentList.Add($"0:s:{trackIndex}");
                
                // For simple formats, output as-is. For complex formats, convert to WebVTT
                if (trackInfo.codec.Equals("subrip", StringComparison.OrdinalIgnoreCase))
                {
                    // SRT → SRT (no conversion needed)
                    psi.ArgumentList.Add("-c:s");          psi.ArgumentList.Add("copy");
                    var cacheSrt = Path.ChangeExtension(cacheVtt, ".srt");
                    psi.ArgumentList.Add(cacheSrt);
                    
                    using var proc = Process.Start(psi)!;
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0 || !File.Exists(cacheSrt))
                    {
                        Console.Error.WriteLine($"[SUBS] Failed to extract SRT track {trackIndex} from {src}");
                        return Results.StatusCode(500);
                    }
                    Console.WriteLine($"[SUBS] Serving SRT track {trackIndex} (no conversion)");
                    return Results.File(cacheSrt, "text/plain"); // SRT format
                }
                else
                {
                    // Complex formats (ASS/SSA/etc) → WebVTT
                    if (trackInfo.codec.Equals("ass", StringComparison.OrdinalIgnoreCase) ||
                        trackInfo.codec.Equals("ssa", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[SUBS] WARNING: {trackInfo.codec.ToUpper()} subtitles with styling will lose formatting in WebVTT conversion");
                    }
                    
                    psi.ArgumentList.Add("-c:s");          psi.ArgumentList.Add("webvtt");
                    psi.ArgumentList.Add(cacheVtt);
                    
                    using var proc = Process.Start(psi)!;
                    var stderr = proc.StandardError.ReadToEnd();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0 || !File.Exists(cacheVtt))
                    {
                        Console.Error.WriteLine($"[SUBS] Failed to convert {trackInfo.codec} track {trackIndex}: {stderr}");
                        return Results.StatusCode(500);
                    }
                    Console.WriteLine($"[SUBS] Converted {trackInfo.codec} track {trackIndex} to WebVTT");
                }
            }

            return Results.File(cacheVtt, "text/vtt");
        }

        // ── External subtitle file (.vtt or .srt next to the video) ──────────
        var basePath = Path.ChangeExtension(src, null);
        var vttPath  = basePath + ".vtt";
        var srtPath  = basePath + ".srt";

        if (File.Exists(vttPath))
        {
            Console.WriteLine($"[SUBS] Serving external VTT: {vttPath}");
            return Results.File(vttPath, "text/vtt");
        }

        if (File.Exists(srtPath))
        {
            Console.WriteLine($"[SUBS] Converting external SRT to VTT: {srtPath}");
            var vttContent = ConvertSrtToVtt(await ReadTextWithFallback(srtPath));
            return Results.Content(vttContent, "text/vtt; charset=utf-8");
        }

        Console.WriteLine($"[SUBS] No subtitle file found for {path}");
        return Results.NotFound();
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("[SUBS] Unauthorized access");
        return Results.BadRequest();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SUBS] Exception: {ex.Message}\n{ex.StackTrace}");
        return Results.StatusCode(500);
    }
});

app.Run();

// ── Subtitle helpers ──────────────────────────────────────────────────────────

static async Task<string> ReadTextWithFallback(string path)
{
    // Try UTF-8 first (strict). If it contains invalid sequences, fall back to
    // Windows-1252 (covers accented characters common in Portuguese SRT files).
    var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    try
    {
        return await File.ReadAllTextAsync(path, utf8Strict);
    }
    catch (DecoderFallbackException)
    {
        return await File.ReadAllTextAsync(path, Encoding.Latin1);
    }
}

static string ConvertSrtToVtt(string srt)
{
    // Normalise line endings
    srt = srt.Replace("\r\n", "\n").Replace("\r", "\n");

    var sb = new StringBuilder();
    sb.Append("WEBVTT\n\n");

    foreach (var block in srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
    {
        var lines = block.Trim().Split('\n');
        if (lines.Length < 2) continue;

        // Skip optional cue-index line (bare integer)
        int i = int.TryParse(lines[0].Trim(), out _) ? 1 : 0;
        if (i >= lines.Length) continue;

        // Timestamp line must contain "-->"
        if (!lines[i].Contains("-->")) continue;

        // SRT uses comma as decimal separator; VTT requires a period
        sb.Append(lines[i].Replace(',', '.')).Append('\n');

        for (int j = i + 1; j < lines.Length; j++)
            sb.Append(lines[j]).Append('\n');

        sb.Append('\n');
    }

    return sb.ToString();
}
