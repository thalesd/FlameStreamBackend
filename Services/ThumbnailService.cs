using System.Collections.Concurrent;
using System.Diagnostics;
using FlameStreamBackend.Helpers;

namespace FlameStreamBackend.Services;

/// <summary>
/// Generates and caches single-frame JPEG scene previews for the seek-bar hover popup.
/// Requested times snap to a fixed grid (<see cref="IntervalSeconds"/>) so scrubbing across
/// the bar reuses cached frames instead of spawning an ffmpeg per pixel; each grid frame is
/// extracted once via a fast input-seek and stored alongside the title's HLS cache, so it is
/// reclaimed together with that cache on delete / idle cleanup.
/// </summary>
public class ThumbnailService
{
    private readonly ServerSettings _settings;

    // Seconds between preview frames. The frontend snaps hover times to the same grid.
    public const int IntervalSeconds = 10;

    // One in-flight generation per cache file, so concurrent hovers on the same bucket wait
    // on a single ffmpeg run rather than racing to write (and half-read) the same jpg.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ThumbnailService(ServerSettings settings) => _settings = settings;

    private SemaphoreSlim GetLock(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>Returns the cached preview frame nearest <paramref name="timeSeconds"/>, generating it on first request. Null if extraction failed.</summary>
    public async Task<string?> GetOrCreateAsync(string src, double timeSeconds, CancellationToken ct = default)
    {
        var bucket   = Math.Max(0, (long)(timeSeconds / IntervalSeconds) * IntervalSeconds);
        var baseHash = PathHelper.HashId(src);
        var dir      = Path.Combine(_settings.CacheRoot, baseHash, "thumbs");
        var file     = Path.Combine(dir, $"thumb_{bucket}.jpg");

        if (File.Exists(file)) return file;

        var gate = GetLock(file);
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(dir);
            return await ExtractFrameAsync(src, bucket, file, ct) ? file : null;
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> ExtractFrameAsync(string src, long atSeconds, string outFile, CancellationToken ct)
    {
        var ic  = System.Globalization.CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // -ss before -i = fast input seek (snaps to the nearest keyframe, plenty accurate for
        // a scrub preview and far cheaper than decoding up to the exact frame). One frame,
        // scaled to 240px wide keeping aspect (-2 = even height, required by the jpeg encoder).
        foreach (var a in new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", atSeconds.ToString(ic),
            "-i", src,
            "-frames:v", "1",
            "-vf", "scale=240:-2",
            "-q:v", "5",
            outFile
        }) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            await using var reg = ct.Register(() => { try { proc.Kill(true); } catch { /* already gone */ } });
            _ = proc.StandardError.ReadToEndAsync(ct); // drain stderr so the pipe can't fill and stall ffmpeg
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 && File.Exists(outFile);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Thumb] ffmpeg failed for {src}@{atSeconds}s: {ex.Message}");
            return false;
        }
    }
}
