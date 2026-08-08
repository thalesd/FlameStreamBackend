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

    /// <summary>
    /// Flat substring search over the library, for callers that want titles rather than a tree.
    /// </summary>
    /// <remarks>
    /// Deliberately does not warm the ffprobe cache: the MCP tools use this to answer "what do I
    /// have that matches X", and probing every file to answer that would turn a question into a
    /// minutes-long job. Duration and resolution come from <see cref="BuildTreeAsync"/>, which is
    /// what the player actually loads.
    /// </remarks>
    public List<MediaMatch> Search(string query, int limit = 50)
    {
        var matches = new List<MediaMatch>();

        foreach (var file in Directory.EnumerateFiles(_libraryRoot, "*", SearchOption.AllDirectories))
        {
            if (!MediaExts.Contains(Path.GetExtension(file))) continue;

            var relPath = Path.GetRelativePath(_libraryRoot, file).Replace('\\', '/');

            if (query.Length > 0 && relPath.Contains(query, StringComparison.OrdinalIgnoreCase) is false)
            {
                continue;
            }

            var escaped = string.Join("/", relPath.Split('/').Select(Uri.EscapeDataString));
            matches.Add(new MediaMatch(
                Path.GetFileName(file),
                relPath,
                $"/stream/{escaped}.m3u8",
                _hls.IsPlaylistComplete(Path.Combine(_hls.MainDir(PathHelper.HashId(file)), "stream.m3u8"))));

            if (matches.Count >= limit) break;
        }

        return matches;
    }
}

/// <param name="Ready">Whether the HLS playlist is already built — an unready title plays after a wait.</param>
public sealed record MediaMatch(string Name, string Path, string StreamUrl, bool Ready);
