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

            double shift = 0;
            if (ctx.Request.Query.TryGetValue("shift", out var shiftStr))
                double.TryParse(shiftStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out shift);

            if (ctx.Request.Query.TryGetValue("track", out var trackStr) &&
                int.TryParse(trackStr, out var trackIndex))
                return await ServeEmbeddedTrackAsync(src, trackIndex, shift);

            return await ServeExternalSubtitleAsync(src, path, shift);
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

    // `shift` (seconds) is required because after a seek, the web/Cast player loads a
    // fresh HLS manifest whose local media time resets to ~0 regardless of where in the
    // original file it actually starts — but our VTT cues are always timed against the
    // ORIGINAL file's absolute timeline. Without shifting cues to match, native subtitle
    // rendering (which matches cues directly against the player's raw currentTime, with
    // no awareness of our own start-offset bookkeeping) never lines up after any seek.
    private async Task<IResult> ServeEmbeddedTrackAsync(string src, int trackIndex, double shift)
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

        if (shift > 0)
            return Results.Content(ShiftVttCues(await File.ReadAllTextAsync(cacheVtt), shift), "text/vtt; charset=utf-8");
        return Results.File(cacheVtt, "text/vtt");
    }

    private async Task<IResult> ServeExternalSubtitleAsync(string src, string path, double shift)
    {
        var basePath = Path.ChangeExtension(src, null);

        var vttPath = basePath + ".vtt";
        if (File.Exists(vttPath))
        {
            Console.WriteLine($"[SUBS] Serving external VTT: {vttPath}");
            if (shift > 0)
                return Results.Content(ShiftVttCues(await File.ReadAllTextAsync(vttPath), shift), "text/vtt; charset=utf-8");
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

            if (shift > 0)
                return Results.Content(ShiftVttCues(await File.ReadAllTextAsync(cacheVtt), shift), "text/vtt; charset=utf-8");
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

    // WebVTT timestamps come in two forms: HH:MM:SS.mmm and the short MM:SS.mmm (hours
    // omitted). ffmpeg's webvtt muxer emits the short form for every cue under one hour
    // and the long form only from 1:00:00 on — so a single embedded track mixes both.
    // The hours group is therefore optional here; a regex that required HH:MM:SS silently
    // dropped every first-hour cue on shift, making embedded subs vanish after any seek.
    private static readonly Regex VttTimingRegex = new(
        @"^((?:\d{1,3}:)?\d{1,2}:\d{2}\.\d{3})\s*-->\s*((?:\d{1,3}:)?\d{1,2}:\d{2}\.\d{3})(.*)$",
        RegexOptions.Compiled);

    /// <summary>Rewrites every cue's timing to be relative to <paramref name="shiftSeconds"/>, dropping cues
    /// that end entirely before it and clamping the start of any cue spanning it to 0.</summary>
    private static string ShiftVttCues(string vtt, double shiftSeconds)
    {
        var lines = vtt.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        sb.Append("WEBVTT\n\n");

        for (int i = 0; i < lines.Length; i++)
        {
            var m = VttTimingRegex.Match(lines[i]);
            if (!m.Success) continue;

            var start = ParseVttTime(m.Groups[1].Value) - shiftSeconds;
            var end   = ParseVttTime(m.Groups[2].Value) - shiftSeconds;
            if (end <= 0) continue; // entirely before the seek target
            start = Math.Max(0, start);

            sb.Append(FormatVttTime(start)).Append(" --> ").Append(FormatVttTime(end)).Append(m.Groups[3].Value).Append('\n');
            for (int j = i + 1; j < lines.Length && !string.IsNullOrWhiteSpace(lines[j]); j++)
                sb.Append(lines[j]).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    // Parses a WebVTT timestamp in either "[HH:]MM:SS.mmm" form.
    private static double ParseVttTime(string ts)
    {
        var dot   = ts.Split('.');
        var ms    = int.Parse(dot[1]);
        var parts = dot[0].Split(':');
        int h = 0, mm, ss;
        if (parts.Length == 3) { h = int.Parse(parts[0]); mm = int.Parse(parts[1]); ss = int.Parse(parts[2]); }
        else                   {                          mm = int.Parse(parts[0]); ss = int.Parse(parts[1]); }
        return h * 3600 + mm * 60 + ss + ms / 1000.0;
    }

    private static string FormatVttTime(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
