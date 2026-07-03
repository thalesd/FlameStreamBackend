using System.Diagnostics;
using System.Text.Json;

namespace FlameStreamBackend.Services;

public class FFprobeService
{
    private static readonly HashSet<string> SupportedSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "ass", "ssa", "webvtt", "mov_text", "ttml", "sami", "microdvd", "text" };

    public (string codec, int channels) GetAudioInfo(string inputFile)
    {
        var psi = CreatePsi();
        psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add(inputFile);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (string.IsNullOrEmpty(output)) return ("", 0);
        using var doc = JsonDocument.Parse(output);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            return ("", 0);
        var codec = streams[0].GetProperty("codec_name").GetString() ?? "";
        var channels = streams[0].TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
        return (codec, channels);
    }

    public List<(int index, string language, string title, string codec)> GetEmbeddedSubtitles(string inputFile)
    {
        var psi = CreatePsi();
        psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("s");
        psi.ArgumentList.Add(inputFile);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        var result = new List<(int, string, string, string)>();
        if (string.IsNullOrEmpty(output)) return result;

        using var doc = JsonDocument.Parse(output);
        if (!doc.RootElement.TryGetProperty("streams", out var streams)) return result;

        int idx = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
            if (!SupportedSubtitleCodecs.Contains(codec)) { idx++; continue; }

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

    public (double duration, int width, int height) GetMediaInfo(string inputFile)
    {
        var psi = CreatePsi();
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add(inputFile);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
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

        if (doc.RootElement.TryGetProperty("streams", out var vstreams) && vstreams.GetArrayLength() > 0)
        {
            if (vstreams[0].TryGetProperty("width",  out var w)) width  = w.GetInt32();
            if (vstreams[0].TryGetProperty("height", out var h)) height = h.GetInt32();
        }

        return (duration, width, height);
    }

    private static ProcessStartInfo CreatePsi()
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
        return psi;
    }
}
