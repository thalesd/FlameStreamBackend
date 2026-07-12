using System.Collections.Concurrent;
using System.Text.Json;

namespace FlameStreamBackend.Services;

/// <summary>
/// Caches the immutable, expensive-to-compute ffprobe results (duration, dimensions, embedded
/// subtitle tracks) per source file, so <c>/api/media</c> doesn't re-spawn two ffprobe processes
/// for every file on every request. Entries are keyed by absolute path and validated against the
/// file's size + last-write time, so a re-encoded/replaced file is automatically re-probed.
///
/// Persisted to <c>{CacheRoot}/mediainfo.json</c> so the cache survives restarts (a library that
/// hasn't changed loads instantly even on the first request after a reboot).
///
/// NOTE: deliberately does NOT cache transcode-derived state (playlist "ready", cached bytes) —
/// that changes independently of the source file and is cheap to read live.
/// </summary>
public sealed class MediaInfoCache
{
    public sealed record SubInfo(int Index, string Language, string Title, string Codec);
    public sealed record Info(double Duration, int Width, int Height, SubInfo[] Subtitles);

    private sealed record Entry(
        long Size, long MTimeTicks, double Duration, int Width, int Height, SubInfo[] Subtitles);

    private readonly FFprobeService _ffprobe;
    private readonly string _cacheFile;
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();
    private volatile bool _dirty;

    public MediaInfoCache(FFprobeService ffprobe, ServerSettings settings)
    {
        _ffprobe   = ffprobe;
        _cacheFile = Path.Combine(settings.CacheRoot, "mediainfo.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cacheFile));
            if (dict is null) return;
            foreach (var (k, v) in dict) _entries[k] = v;
        }
        catch { /* corrupt/unreadable cache — start empty and rebuild */ }
    }

    private bool TryValid(string file, out Entry entry)
    {
        entry = null!;
        if (!_entries.TryGetValue(file, out var e)) return false;
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length != e.Size || fi.LastWriteTimeUtc.Ticks != e.MTimeTicks)
            return false;
        entry = e;
        return true;
    }

    /// <summary>Cache-first lookup; probes (and records) on a miss or when the file has changed.</summary>
    public Info Get(string file)
    {
        if (TryValid(file, out var e))
            return new Info(e.Duration, e.Width, e.Height, e.Subtitles);
        return Probe(file);
    }

    private Info Probe(string file)
    {
        var (duration, width, height) = _ffprobe.GetMediaInfo(file);
        var subs = _ffprobe.GetEmbeddedSubtitles(file)
            .Select(s => new SubInfo(s.index, s.language, s.title, s.codec))
            .ToArray();

        var fi = new FileInfo(file);
        _entries[file] = new Entry(fi.Length, fi.LastWriteTimeUtc.Ticks, duration, width, height, subs);
        _dirty = true;
        return new Info(duration, width, height, subs);
    }

    /// <summary>
    /// Probe every missing/stale file up front, in parallel, then persist once. Turns a cold
    /// first load from N serial ffprobe pairs into a bounded-concurrency batch; warm loads that
    /// find everything already cached return immediately.
    /// </summary>
    public async Task WarmAsync(IReadOnlyCollection<string> files)
    {
        var missing = files.Where(f => !TryValid(f, out _)).ToList();
        if (missing.Count == 0) return;

        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            (f, _) => { try { Probe(f); } catch { /* skip unreadable file */ } return ValueTask.CompletedTask; });

        Save();
    }

    private void Save()
    {
        if (!_dirty) return;
        lock (_saveLock)
        {
            if (!_dirty) return;
            try
            {
                var tmp = _cacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
                File.Move(tmp, _cacheFile, overwrite: true);
                _dirty = false;
            }
            catch { /* best-effort; an unwritten cache just means a re-probe next time */ }
        }
    }
}
