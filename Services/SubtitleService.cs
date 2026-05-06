using System.Diagnostics;
using System.Text;
using FlameStreamBackend.Helpers;

namespace FlameStreamBackend.Services;

public class SubtitleService
{
    private readonly FFprobeService _ffprobe;
    private readonly string _libraryRoot;
    private readonly string _cacheRoot;

    public SubtitleService(FFprobeService ffprobe, ServerSettings settings)
    {
        _ffprobe     = ffprobe;
        _libraryRoot = settings.LibraryRoot;
        _cacheRoot   = settings.CacheRoot;
    }

    public async Task<IResult> GetSubtitlesAsync(HttpContext ctx, string path)
    {
        try
        {
            var src = PathHelper.SafeUnder(_libraryRoot, path);

            if (ctx.Request.Query.TryGetValue("track", out var trackStr) &&
                int.TryParse(trackStr, out var trackIndex))
                return await ServeEmbeddedTrackAsync(src, trackIndex);

            return await ServeExternalSubtitleAsync(src, path);
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
    }

    private async Task<IResult> ServeEmbeddedTrackAsync(string src, int trackIndex)
    {
        var subtitles = _ffprobe.GetEmbeddedSubtitles(src);
        var trackInfo = subtitles.FirstOrDefault(s => s.index == trackIndex);

        if (trackInfo == default)
        {
            Console.Error.WriteLine($"[SUBS] Track {trackIndex} not found in {src}");
            return Results.NotFound();
        }

        Console.WriteLine($"[SUBS] Extracting embedded track {trackIndex}: codec={trackInfo.codec}, lang={trackInfo.language}");

        if (trackInfo.codec.Equals("ass", StringComparison.OrdinalIgnoreCase) ||
            trackInfo.codec.Equals("ssa", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"[SUBS] WARNING: {trackInfo.codec.ToUpper()} subtitles with styling will lose formatting in WebVTT conversion");

        var cacheVtt = Path.Combine(_cacheRoot, "subs", $"{PathHelper.HashId($"{src}_s{trackIndex}")}.vtt");
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
            psi.ArgumentList.Add("-i");   psi.ArgumentList.Add(src);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"0:s:{trackIndex}");
            psi.ArgumentList.Add("-c:s"); psi.ArgumentList.Add("webvtt");
            psi.ArgumentList.Add(cacheVtt);

            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || !File.Exists(cacheVtt))
            {
                Console.Error.WriteLine($"[SUBS] Failed to convert track {trackIndex}: {stderr}");
                return Results.StatusCode(500);
            }
            Console.WriteLine($"[SUBS] Converted track {trackIndex} to WebVTT");
        }

        return Results.File(cacheVtt, "text/vtt");
    }

    private async Task<IResult> ServeExternalSubtitleAsync(string src, string path)
    {
        var basePath = Path.ChangeExtension(src, null);

        var vttPath = basePath + ".vtt";
        if (File.Exists(vttPath))
        {
            Console.WriteLine($"[SUBS] Serving external VTT: {vttPath}");
            return Results.File(vttPath, "text/vtt");
        }

        var srtPath = basePath + ".srt";
        if (File.Exists(srtPath))
        {
            Console.WriteLine($"[SUBS] Converting external SRT to VTT: {srtPath}");
            var vttContent = ConvertSrtToVtt(await ReadTextWithFallback(srtPath));
            return Results.Content(vttContent, "text/vtt; charset=utf-8");
        }

        Console.WriteLine($"[SUBS] No subtitle file found for {path}");
        return Results.NotFound();
    }

    private static async Task<string> ReadTextWithFallback(string path)
    {
        // Try UTF-8 first (strict). If bytes are invalid, fall back to Latin-1
        // (covers Windows-1252 encoding common in Portuguese SRT files).
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

    private static string ConvertSrtToVtt(string srt)
    {
        srt = srt.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder();
        sb.Append("WEBVTT\n\n");

        foreach (var block in srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Trim().Split('\n');
            if (lines.Length < 2) continue;

            int i = int.TryParse(lines[0].Trim(), out _) ? 1 : 0;
            if (i >= lines.Length || !lines[i].Contains("-->")) continue;

            // SRT uses comma as millisecond separator; VTT requires a period
            sb.Append(lines[i].Replace(',', '.')).Append('\n');
            for (int j = i + 1; j < lines.Length; j++)
                sb.Append(lines[j]).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
