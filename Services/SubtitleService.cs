using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
            var cacheVtt = Path.Combine(_cacheRoot, "subs", $"{PathHelper.HashId(srtPath)}.vtt");
            Directory.CreateDirectory(Path.GetDirectoryName(cacheVtt)!);

            if (!File.Exists(cacheVtt) || File.GetLastWriteTimeUtc(srtPath) > File.GetLastWriteTimeUtc(cacheVtt))
            {
                Console.WriteLine($"[SUBS] Converting external SRT to VTT: {srtPath}");
                var vttContent = ConvertSrtToVtt(await ReadTextWithFallback(srtPath));
                await File.WriteAllTextAsync(cacheVtt, vttContent, new UTF8Encoding(false));
            }

            return Results.File(cacheVtt, "text/vtt");
        }

        Console.WriteLine($"[SUBS] No subtitle file found for {path}");
        return Results.NotFound();
    }

    private static async Task<string> ReadTextWithFallback(string path)
    {
        // Try UTF-8 first (strict, so invalid sequences throw immediately).
        // Fall back to Windows-1252 (CP1252), NOT Latin-1: bytes 0x80-0x9F are
        // printable in CP1252 (en-dash, curly quotes, etc.) but control characters
        // in Latin-1, which causes them to render as squares in the browser.
        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return await File.ReadAllTextAsync(path, utf8Strict);
        }
        catch (DecoderFallbackException)
        {
            return await File.ReadAllTextAsync(path, Encoding.GetEncoding(1252));
        }
    }

    // Matches an SRT/VTT timing line anywhere, regardless of surrounding blank-line count
    // or a missing/garbled cue-index line — the previous parser relied on both of those
    // being well-formed and silently dropped the whole cue otherwise.
    private static readonly Regex TimingRegex = new(
        @"^\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{3})",
        RegexOptions.Compiled);

    private const char Bom = '﻿';

    private static string ConvertSrtToVtt(string srt)
    {
        srt = srt.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart(Bom);
        var lines = srt.Split('\n');

        var anchors = new List<(int line, Match match)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimingRegex.Match(lines[i]);
            if (m.Success) anchors.Add((i, m));
        }

        var sb = new StringBuilder();
        sb.Append("WEBVTT\n\n");

        int cuesEmitted = 0, emptyCues = 0;
        for (int a = 0; a < anchors.Count; a++)
        {
            var (timingLine, match) = anchors[a];
            int textStart = timingLine + 1;
            int textEnd = (a + 1 < anchors.Count ? anchors[a + 1].line : lines.Length) - 1;

            while (textEnd >= textStart && string.IsNullOrWhiteSpace(lines[textEnd]))
                textEnd--;

            // The line right before the next cue's timing line is normally that next
            // cue's index number (not part of this cue's text) — drop it if so.
            if (a + 1 < anchors.Count && textEnd >= textStart && int.TryParse(lines[textEnd].Trim(), out _))
            {
                textEnd--;
                while (textEnd >= textStart && string.IsNullOrWhiteSpace(lines[textEnd]))
                    textEnd--;
            }

            var timing = $"{match.Groups[1].Value.Replace(',', '.')} --> {match.Groups[2].Value.Replace(',', '.')}";

            if (textEnd < textStart)
            {
                Console.Error.WriteLine($"[SUBS] Empty cue at {timing}");
                emptyCues++;
                continue;
            }

            sb.Append(timing).Append('\n');
            for (int j = textStart; j <= textEnd; j++)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                sb.Append(lines[j]).Append('\n');
            }
            sb.Append('\n');
            cuesEmitted++;
        }

        Console.WriteLine($"[SUBS] SRT->VTT conversion: {cuesEmitted} cues converted, {emptyCues} empty/skipped");
        return sb.ToString();
    }
}
