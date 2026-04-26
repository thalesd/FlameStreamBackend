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

                // Delete incomplete encodings; keep completed videos
                var cacheDir = Path.Combine(cacheRoot, kv.Key);
                try
                {
                    if (Directory.Exists(cacheDir))
                    {
                        var playlistFile = Path.Combine(cacheDir, "stream.m3u8");
                        if (File.Exists(playlistFile))
                        {
                            var content = File.ReadAllText(playlistFile);
                            // If encoding is incomplete (no ENDLIST), delete the cache
                            if (!content.Contains("#EXT-X-ENDLIST"))
                            {
                                Console.WriteLine($"[Idle] Deleting incomplete cache: {kv.Key}");
                                Directory.Delete(cacheDir, true);
                            }
                            else
                            {
                                Console.WriteLine($"[Idle] Keeping completed video cache: {kv.Key}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Idle] Error cleaning cache {kv.Key}: {ex.Message}");
                }
            }
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

    var clean = path[..^5]; // strip ".m3u8"

    ctx.Request.Query.TryGetValue("start", out var startStr);
    double.TryParse(startStr, System.Globalization.NumberStyles.Any,
        System.Globalization.CultureInfo.InvariantCulture, out var startSeconds);
    var startSegment = (int)Math.Floor(startSeconds / hlsSegmentDuration);

    try
    {
        var src = SafeUnder(libraryRoot, clean);
        if (!File.Exists(src)) return Results.NotFound();

        // Determine if we need pre-segmentation (seeking to a specific time)
        var baseHash = HashId(src);
        var hash = baseHash;
        var seekOffset = 0;

        var useOriginalForSeek = false;

        if (startSeconds > 0)
        {
            // If the original encoding has already produced the needed segment, use it directly
            var originalSegFile = Path.Combine(cacheRoot, baseHash, $"seg_{startSegment:D5}.ts");
            if (File.Exists(originalSegFile))
            {
                hash = baseHash;
                useOriginalForSeek = true;
                Console.WriteLine($"[HLS] Original has segment {startSegment}, serving from main stream");

                // Clean up the stale seek job now that the original covers this position
                if (activeSeekJobs.TryRemove(baseHash, out var obsoleteHash))
                {
                    if (!IsPlaylistComplete(Path.Combine(cacheRoot, baseHash, "stream.m3u8")))
                    {
                        Console.WriteLine($"[HLS] Cleaning up seek job {obsoleteHash} (original caught up)");
                        CleanupSeekJob(obsoleteHash);
                    }
                }
            }
            else
            {
                // Original hasn't reached this point yet — use a separate encoding job
                hash = HashId($"{src}_{startSeconds}");
                seekOffset = startSegment;
                Console.WriteLine($"[HLS] Pre-segmentation: user requested start at {startSeconds}s");

                // User seeked to a different position: stop the previous seek job while original is still processing
                if (activeSeekJobs.TryGetValue(baseHash, out var prevSeekHash) && prevSeekHash != hash)
                {
                    if (!IsPlaylistComplete(Path.Combine(cacheRoot, baseHash, "stream.m3u8")))
                    {
                        Console.WriteLine($"[HLS] New seek position, stopping previous seek job {prevSeekHash}");
                        activeSeekJobs.TryRemove(baseHash, out _);
                        CleanupSeekJob(prevSeekHash);
                    }
                }

                // Register this as the active seek job for this file
                activeSeekJobs[baseHash] = hash;
            }
        }

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

            // If pre-segmenting, seek to start time before encoding (much faster)
            if (startSeconds > 0)
            {
                args.Add("-ss");
                args.Add(startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Console.WriteLine($"[HLS] FFmpeg will seek to {startSeconds}s before encoding");
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

            // Shift output PTS to match the original file's timeline so video.currentTime
            // stays accurate even though FFmpeg restarted encoding from t=0 internally.
            if (startSeconds > 0)
            {
                args.Add("-output_ts_offset");
                args.Add(startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            args.AddRange(new List<string>
            {
                "-map", "0:v:0", "-map", "0:a:0",
                "-hls_time", hlsSegmentDuration.ToString(),
                "-hls_flags", "independent_segments",
                "-hls_segment_filename", segPattern,
                "-force_key_frames",$"expr:gte(t,n_forced*{hlsSegmentDuration})",
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

            Console.WriteLine($"[HLS] ffmpeg start\n  cmd: ffmpeg {string.Join(" ", args)}\n  outDir: {outDir}");

            _ = Task.Run(async () =>
            {
                try
                {
                    var proc = await ctx.RequestServices
                        .GetRequiredService<TranscodeRegistry>()
                        .StartAsync(hash, psi);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HLS] Failed to start ffmpeg: {ex.Message}");
                }
            });

            // Wait briefly so index.m3u8 exists before replying
            var sw = Stopwatch.StartNew();
            while (!System.IO.File.Exists(playlist) && sw.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(200);

            if (!File.Exists(playlist))
            {
                Console.Error.WriteLine("[HLS] Playlist not created. See ffmpeg log in: " + outDir);
                return Results.StatusCode(500);
            }
        }

        // No seek — serve playlist dynamically so the Chromecast (and other strict
        // HLS clients) always get segments that are fully written. Redirecting to the
        // static file caused failures: clients received a playlist before FFmpeg had
        // finished writing the first segments, then got partial .ts files and gave up.
        if (startSegment == 0 && startSeconds <= 0)
        {
            // Wait until at least 2 segments exist so the client can start buffering
            // immediately after receiving the playlist.
            var seg1 = Path.Combine(outDir, "seg_00001.ts");
            if (!File.Exists(seg1))
            {
                var segSw = Stopwatch.StartNew();
                while (!File.Exists(seg1) && segSw.Elapsed < TimeSpan.FromSeconds(60))
                    await Task.Delay(500);
                Console.WriteLine($"[HLS] Waited {segSw.Elapsed.TotalSeconds:F1}s for initial segments");
            }
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
            var pl = BuildSubPlaylist(playlist, hash, 0, null);
            Console.WriteLine($"[HLS] Serving dynamic playlist from segment 0");
            return Results.Content(pl, "application/vnd.apple.mpegurl");
        }

        // For pre-segmented seeks the new job always starts encoding from 0 (FFmpeg seeked ahead).
        // When using the original stream for a seek, target the actual segment index.
        var targetSegment = (startSeconds > 0 && !useOriginalForSeek) ? 0 : startSegment;

        // Seek — wait until FFmpeg has encoded past the target segment
        var targetSegFile = Path.Combine(outDir, $"seg_{targetSegment:D5}.ts");
        var seekSw = Stopwatch.StartNew();
        const int maxWaitSeconds = 600; // 10 minutes
        
        while (!File.Exists(targetSegFile) && seekSw.Elapsed < TimeSpan.FromSeconds(maxWaitSeconds))
        {
            // Check playlist to see if it's marked as complete
            var playlistLines = File.ReadAllLines(playlist);
            bool isComplete = Array.Exists(playlistLines, line => line == "#EXT-X-ENDLIST");
            
            if (isComplete)
            {
                // File is complete, if segment doesn't exist, it's beyond the file
                Console.WriteLine($"[HLS] File encoding complete, but segment {targetSegment} not found.");
                break;
            }
            
            // Still encoding, wait a bit and retry
            await Task.Delay(500);
        }

        if (seekSw.Elapsed > TimeSpan.FromSeconds(5))
            Console.WriteLine($"[HLS] Segment wait completed in {seekSw.Elapsed.TotalSeconds:F1}s");

        if (!File.Exists(targetSegFile))
        {
            Console.Error.WriteLine($"[HLS] Seek target seg_{targetSegment:D5}.ts not available after {seekSw.Elapsed.TotalSeconds:F1}s timeout.");
            
            // Check if file is still being encoded
            var playlistLines = File.ReadAllLines(playlist);
            bool isComplete = Array.Exists(playlistLines, line => line == "#EXT-X-ENDLIST");
            
            if (isComplete)
            {
                Console.Error.WriteLine($"[HLS] File is complete. Requested segment {targetSegment} is beyond file duration.");
                return Results.StatusCode(416); // Range Not Satisfiable
            }
            else
            {
                Console.Error.WriteLine($"[HLS] File is still being encoded. Serving from last available segment.");
                // Fall through to BuildSubPlaylist which will serve from last available segment
            }
        }

        // For seek jobs the sub-playlist must declare the correct timeline position via
        // MEDIA-SEQUENCE so HLS.js knows the stream starts at startSeconds, not at 0.
        int? mediaSeq = (startSeconds > 0 && !useOriginalForSeek) ? startSegment : null;
        var subPlaylist = BuildSubPlaylist(playlist, hash, targetSegment, mediaSeq);
        Console.WriteLine($"[HLS] Serving sub-playlist from segment {targetSegment}");
        return Results.Content(subPlaylist, "application/vnd.apple.mpegurl");
    }
    catch (Exception ex)
    {
        Console.WriteLine("[HLS] Exception: " + ex);
        return Results.BadRequest(new { error = ex.Message });
    }
});

string BuildSubPlaylist(string playlistPath, string hash, int startSegment, int? mediaSequenceOverride = null)
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
        sb.AppendLine($"/hls/{hash}/{segments[i].file}");
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

void CleanupSeekJob(string seekHash)
{
    registry.Stop(seekHash);
    lastAccessUtc.TryRemove(seekHash, out _);
    var dir = Path.Combine(cacheRoot, seekHash);
    if (!Directory.Exists(dir)) return;
    if (!IsPlaylistComplete(Path.Combine(dir, "stream.m3u8")))
    {
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { Console.Error.WriteLine($"[Seek] Failed to delete seek cache {seekHash}: {ex.Message}"); }
    }
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

app.MapGet("/subs/{**path}", async (string path) =>
{
    try
    {
        var src = SafeUnder(libraryRoot, path);
        var basePath = Path.ChangeExtension(src, null); // remove extensão

        // Procura legenda: .vtt tem prioridade, senão .srt
        var vttPath = basePath + ".vtt";
        var srtPath = basePath + ".srt";

        // Se já existe .vtt, serve direto
        if (File.Exists(vttPath))
            return Results.File(vttPath, "text/vtt");

        // Se existe .srt, converte para .vtt e faz cache
        if (File.Exists(srtPath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(srtPath);
            psi.ArgumentList.Add(vttPath);

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && File.Exists(vttPath))
                return Results.File(vttPath, "text/vtt");

            return Results.StatusCode(500);
        }

        return Results.NotFound();
    }
    catch (UnauthorizedAccessException)
    {
        return Results.BadRequest();
    }
    catch (Exception ex)
    {
        Console.WriteLine("[SUBS] Exception: " + ex);
        return Results.StatusCode(500);
    }
});

app.Run();
