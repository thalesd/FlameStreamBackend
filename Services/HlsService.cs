using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlameStreamBackend.Helpers;

namespace FlameStreamBackend.Services;

public class HlsService
{
    private readonly TranscodeRegistry _registry;
    private readonly FFprobeService _ffprobe;
    private readonly ServerSettings _settings;

    internal readonly ConcurrentDictionary<string, DateTime> LastAccess = new();
    private readonly ConcurrentDictionary<string, string> _activeSeekJobs = new();
    private readonly ConcurrentDictionary<string, string> _jobPaths = new();

    public HlsService(TranscodeRegistry registry, FFprobeService ffprobe, ServerSettings settings)
    {
        _registry = registry;
        _ffprobe  = ffprobe;
        _settings = settings;
    }

    public void TouchJob(string key) => LastAccess[key] = DateTime.UtcNow;

    public string MainDir(string baseHash) => Path.Combine(_settings.CacheRoot, baseHash);
    public string TempDir(string baseHash, string seekHash) => Path.Combine(_settings.CacheRoot, baseHash, "temp", seekHash);

    public int CountEncodedSegments(string playlistPath)
    {
        if (!File.Exists(playlistPath)) return 0;
        try { return File.ReadAllLines(playlistPath).Count(l => l.TrimEnd().EndsWith(".ts", StringComparison.OrdinalIgnoreCase)); }
        catch { return 0; }
    }

    public bool IsPlaylistComplete(string playlistPath)
    {
        if (!File.Exists(playlistPath)) return false;
        try { return File.ReadAllText(playlistPath).Contains("#EXT-X-ENDLIST"); }
        catch { return false; }
    }

    public string BuildSubPlaylist(string playlistPath, string urlBase, int startSegment, int? mediaSequenceOverride = null)
    {
        var lines = File.ReadAllLines(playlistPath);
        var sb = new StringBuilder();

        string targetDuration = $"#EXT-X-TARGETDURATION:{_settings.HlsSegmentSeconds}";
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

        var from = Math.Max(0, startSegment >= segments.Count ? segments.Count - 1 : startSegment);

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

    public void CleanupSeekJob(string baseHash, string seekHash)
    {
        _registry.Stop(seekHash);
        LastAccess.TryRemove(seekHash, out _);
        _jobPaths.TryRemove(seekHash, out _);
        var dir = TempDir(baseHash, seekHash);
        if (!Directory.Exists(dir)) return;
        if (!IsPlaylistComplete(Path.Combine(dir, "stream.m3u8")))
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) { Console.Error.WriteLine($"[Seek] Failed to delete seek cache {seekHash}: {ex.Message}"); }
        }
    }

    public void StartFfmpegJob(string key, string outDir, string src, double startSeconds)
    {
        Directory.CreateDirectory(outDir);

        var audioCodec  = _ffprobe.GetAudioCodec(src);
        var playlist    = Path.Combine(outDir, "stream.m3u8");
        var segPattern  = Path.Combine(outDir, "seg_%05d.ts");
        var ic          = System.Globalization.CultureInfo.InvariantCulture;

        var args = new List<string> { "-hide_banner", "-y", "-nostdin", "-loglevel", "info" };

        if (startSeconds > 0)
        {
            args.Add("-ss");
            args.Add(startSeconds.ToString(ic));
        }

        if (_settings.HardwareAcceleration.Equals("Nvidia", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", src]);
            args.AddRange(["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-b:v", "5M"]);
        }
        else if (_settings.HardwareAcceleration.Equals("Amd", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-hwaccel", "dxva2", "-i", src]);
            args.AddRange(["-c:v", "h264_amf", "-b:v", "5M", "-profile:v", "high", "-level", "4.1"]);
        }
        else
        {
            args.AddRange(["-i", src]);
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-tune", "zerolatency"]);
        }

        args.Add("-pix_fmt"); args.Add("yuv420p");

        args.AddRange(audioCodec == "aac"
            ? ["-c:a", "copy"]
            : ["-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000"]);

        if (startSeconds > 0)
        {
            args.Add("-output_ts_offset");
            args.Add(startSeconds.ToString(ic));
        }

        args.AddRange([
            "-map", "0:v:0", "-map", "0:a:0",
            "-hls_time", _settings.HlsSegmentSeconds.ToString(),
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", segPattern,
            "-force_key_frames", $"expr:gte(t,n_forced*{_settings.HlsSegmentSeconds})",
            "-start_number", "0",
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            playlist
        ]);

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

        _jobPaths[key]  = outDir;
        LastAccess[key] = DateTime.UtcNow;

        Console.WriteLine($"[HLS] ffmpeg queued: {key}\n  cmd: ffmpeg {string.Join(" ", args)}\n  outDir: {outDir}");

        _ = Task.Run(async () =>
        {
            try { await _registry.StartAsync(key, psi); }
            catch (Exception ex) { Console.Error.WriteLine($"[HLS] Failed to start ffmpeg for {key}: {ex.Message}"); }
        });
    }

    public async Task<IResult> HandleStreamRequestAsync(HttpContext ctx, string src, double startSeconds)
    {
        var baseHash     = PathHelper.HashId(src);
        var mainDir      = MainDir(baseHash);
        var mainPlaylist = Path.Combine(mainDir, "stream.m3u8");

        if (!IsPlaylistComplete(mainPlaylist) && !_registry.IsRunning(baseHash))
        {
            Console.WriteLine($"[HLS] Starting main encoding for {baseHash}");
            StartFfmpegJob(baseHash, mainDir, src, 0.0);
        }

        var startSegment = (int)Math.Floor(startSeconds / _settings.HlsSegmentSeconds);
        string hash, outDir, urlBase;
        bool useOriginalForSeek;

        if (startSeconds > 0)
        {
            if (CountEncodedSegments(mainPlaylist) > startSegment)
            {
                hash = baseHash; outDir = mainDir; urlBase = $"/hls/{baseHash}";
                useOriginalForSeek = true;
                Console.WriteLine($"[HLS] Main has segment {startSegment}, serving from main stream");

                if (_activeSeekJobs.TryRemove(baseHash, out var obsoleteSeekHash))
                {
                    Console.WriteLine($"[HLS] Cleaning up seek job {obsoleteSeekHash} (main caught up)");
                    CleanupSeekJob(baseHash, obsoleteSeekHash);
                }
            }
            else
            {
                var seekHash = PathHelper.HashId($"{src}_{startSeconds}");
                hash = seekHash; outDir = TempDir(baseHash, seekHash); urlBase = $"/hls/{baseHash}/temp/{seekHash}";
                useOriginalForSeek = false;
                Console.WriteLine($"[HLS] Main doesn't have segment {startSegment} yet, using seek job {seekHash}");

                if (_activeSeekJobs.TryGetValue(baseHash, out var prevSeekHash) && prevSeekHash != seekHash)
                {
                    Console.WriteLine($"[HLS] New seek position — stopping previous seek job {prevSeekHash}");
                    _activeSeekJobs.TryRemove(baseHash, out _);
                    CleanupSeekJob(baseHash, prevSeekHash);
                }
                _activeSeekJobs[baseHash] = seekHash;
            }
        }
        else
        {
            hash = baseHash; outDir = mainDir; urlBase = $"/hls/{baseHash}";
            useOriginalForSeek = false;
        }

        LastAccess[hash] = DateTime.UtcNow;
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
            Console.WriteLine("[HLS] Serving dynamic playlist from segment 0");
            return Results.Content(pl, "application/vnd.apple.mpegurl");
        }

        var targetSegment = (startSeconds > 0 && !useOriginalForSeek) ? 0 : startSegment;
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
            Console.Error.WriteLine("[HLS] Still encoding; BuildSubPlaylist will serve from last available segment.");
        }

        int? mediaSeq = (startSeconds > 0 && !useOriginalForSeek) ? startSegment : null;
        var subPlaylist = BuildSubPlaylist(playlist, urlBase, targetSegment, mediaSeq);
        Console.WriteLine($"[HLS] Serving sub-playlist from segment {targetSegment}");
        return Results.Content(subPlaylist, "application/vnd.apple.mpegurl");
    }

    public void CleanupIdleJobs(TimeSpan idleTimeout)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in LastAccess.ToArray())
        {
            if (now - kv.Value <= idleTimeout) continue;

            _registry.Stop(kv.Key);
            LastAccess.TryRemove(kv.Key, out _);
            _jobPaths.TryRemove(kv.Key, out var cacheDir);
            cacheDir ??= Path.Combine(_settings.CacheRoot, kv.Key);

            try
            {
                if (!Directory.Exists(cacheDir)) continue;
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
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Idle] Error cleaning cache {kv.Key}: {ex.Message}");
            }
        }

        try
        {
            foreach (var mainDir in Directory.GetDirectories(_settings.CacheRoot))
            {
                var tempDir = Path.Combine(mainDir, "temp");
                if (!Directory.Exists(tempDir)) continue;
                var mainPlaylist = Path.Combine(mainDir, "stream.m3u8");
                if (!File.Exists(mainPlaylist) || !File.ReadAllText(mainPlaylist).Contains("#EXT-X-ENDLIST")) continue;
                Directory.Delete(tempDir, true);
                Console.WriteLine($"[Idle] Deleted temp dir for completed encoding: {Path.GetFileName(mainDir)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Idle] Error during temp cleanup: {ex.Message}");
        }
    }
}
