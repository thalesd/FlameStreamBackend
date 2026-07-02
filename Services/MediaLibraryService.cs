using FlameStreamBackend.Helpers;

namespace FlameStreamBackend.Services;

public class MediaLibraryService
{
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi" };

    private readonly FFprobeService _ffprobe;
    private readonly HlsService _hls;
    private readonly string _libraryRoot;

    public MediaLibraryService(FFprobeService ffprobe, HlsService hls, ServerSettings settings)
    {
        _ffprobe     = ffprobe;
        _hls         = hls;
        _libraryRoot = settings.LibraryRoot;
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
            var embedded = _ffprobe.GetEmbeddedSubtitles(f);
            var (duration, width, height) = _ffprobe.GetMediaInfo(f);

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
                subUrl = (File.Exists(Path.ChangeExtension(f, ".vtt")) || File.Exists(Path.ChangeExtension(f, ".srt")))
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
                height,
                ready,
                cachedBytes
            });
        }

        return entries;
    }
}
