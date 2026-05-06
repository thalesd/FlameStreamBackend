using System.Security.Cryptography;
using System.Text;

namespace FlameStreamBackend.Helpers;

public static class PathHelper
{
    public static string HashId(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string SafeUnder(string root, string? rel)
    {
        rel ??= "";
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException();
        return full;
    }

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4"  => "video/mp4",
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts"   => "video/MP2T",
        ".m4s"  => "video/iso.segment",
        ".vtt"  => "text/vtt",
        _       => "application/octet-stream"
    };
}
