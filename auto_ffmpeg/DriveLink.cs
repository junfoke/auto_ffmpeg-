namespace auto_ffmpeg;

using System.Text.RegularExpressions;

public static class DriveLink
{
    private static readonly Regex[] Patterns =
    [
        new(@"/file/d/([A-Za-z0-9_-]{10,})", RegexOptions.Compiled),
        new(@"[?&]id=([A-Za-z0-9_-]{10,})", RegexOptions.Compiled),
    ];

    /// <summary>Extracts the fileId from a Google Drive share link. Returns null if no match.</summary>
    public static string? ExtractFileId(string? shareUrl)
    {
        if (string.IsNullOrWhiteSpace(shareUrl)) return null;
        if (!shareUrl.Contains("google.com", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var p in Patterns)
        {
            var m = p.Match(shareUrl);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
}
