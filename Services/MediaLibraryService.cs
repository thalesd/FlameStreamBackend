using FlameStreamBackend.Helpers;

namespace FlameStreamBackend.Services;

public class MediaLibraryService
{
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi" };

    private readonly MediaInfoCache _infoCache;
    private readonly HlsService _hls;
    private readonly string _libraryRoot;

    public MediaLibraryService(MediaInfoCache infoCache, HlsService hls, ServerSettings settings)
    {
        _infoCache   = infoCache;
        _hls         = hls;
        _libraryRoot = settings.LibraryRoot;
    }

    /// <summary>
    /// Warm the ffprobe metadata cache for the whole library (parallel, one-time per changed file),
    /// then build the tree — so the per-file work below is all cheap cache hits + disk checks.
    /// </summary>
    public async Task<List<object>> BuildTreeAsync()
    {
        var files = Directory
            .EnumerateFiles(_libraryRoot, "*", SearchOption.AllDirectories)
            .Where(f => MediaExts.Contains(Path.GetExtension(f)))
            .ToList();
        await _infoCache.WarmAsync(files);
        return BuildTree(_libraryRoot);
    }

    public List<object> BuildTree(string dir, string relBase = "")
    {
        var entries = new List<object>();

        foreach (var subDir in Directory.GetDirectories(dir).OrderBy(d => d))
        {
            var dirName = Path.GetFileName(subDir)!;
            var relPath = string.IsNullOrEmpty(relBase) ? dirName : $"{relBase}/{dirName}";
            entries.Add(new { type = "folder", name = dirName, path = relPath, children = BuildTree(subDir, relPath) });
        }

        foreach (var f in Directory.GetFiles(dir).Where(f => MediaExts.Contains(Path.GetExtension(f))).OrderBy(f => f))
        {
            var fileName = Path.GetFileName(f);
            var relPath  = string.IsNullOrEmpty(relBase) ? fileName : $"{relBase}/{fileName}";
            var escaped  = string.Join("/", relPath.Split('/').Select(Uri.EscapeDataString));
            var info     = _infoCache.Get(f);
            var (duration, width, height) = (info.Duration, info.Width, info.Height);

            var baseHash     = PathHelper.HashId(f);
            var mainPlaylist = Path.Combine(_hls.MainDir(baseHash), "stream.m3u8");
            var ready = _hls.IsPlaylistComplete(mainPlaylist);
            var cachedBytes = _hls.GetCacheSizeBytes(f);

            entries.Add(new
            {
                type = "file",
                name = fileName,
                path = relPath,
                url       = $"/stream/{escaped}.m3u8",
                directUrl = $"/media/{escaped}",
                thumbUrl  = $"/api/thumb/{escaped}",
                subUrl = (File.Exists(Path.ChangeExtension(f, ".vtt")) || File.Exists(Path.ChangeExtension(f, ".srt")))
                    ? $"/subs/{escaped}"
                    : null,
                embeddedSubtitles = info.Subtitles.Select(s => new
                {
                    url = $"/subs/{escaped}?track={s.Index}",
                    language = s.Language,
                    title = s.Title,
                    codec = s.Codec
                }).ToArray(),
                duration,
                width,
                height,
                ready,
                cachedBytes
            });
        }

        return entries;
    }
}
