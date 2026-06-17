namespace auto_ffmpeg;

using System.Web;

/// <summary>Pure helpers for googlevideo videoplayback URLs and stream mime types.</summary>
public static class DashStream
{
    public static bool IsVideoMime(string? mime) =>
        mime != null && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    public static bool IsAudioMime(string? mime) =>
        mime != null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    public static string ExtFromMime(string? mime) => (mime?.ToLowerInvariant()) switch
    {
        "video/mp4" => "mp4",
        "video/webm" => "webm",
        "audio/mp4" => "m4a",
        "audio/webm" => "weba",
        _ => mime != null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "m4a" : "mp4"
    };

    /// <summary>Reads one query param (URL-decoded). Null if absent.</summary>
    public static string? GetQueryParam(string url, string key)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;
        var query = HttpUtility.ParseQueryString(url[(q + 1)..]);
        return query[key];
    }

    /// <summary>
    /// Removes range-binding params (range/rn/rbuf) and embedded-player context params
    /// (alr/cpn/c/cver/ump/srfvp) so the URL becomes a directly downloadable progressive file.
    /// Preserves original percent-encoding of all remaining params (e.g. mime=video%2Fmp4 stays literal).
    /// </summary>
    public static string StripRange(string url)
    {
        int q = url.IndexOf('?');
        if (q < 0) return url;

        var baseUrl = url[..q];
        var queryString = url[(q + 1)..];

        // Manual split to preserve original encoding of values (HttpUtility.ParseQueryString
        // decodes and then re-encodes with different casing, e.g. %2F -> %2f).
        static string? RawKey(string pair)
        {
            int eq = pair.IndexOf('=');
            return eq < 0 ? pair : pair[..eq];
        }

        var kept = queryString
            .Split('&')
            .Where(pair =>
            {
                var k = Uri.UnescapeDataString(RawKey(pair) ?? string.Empty);
                // Drop per-request range params AND embedded-player context params. The latter
                // (c=WEB_EMBEDDED_PLAYER, ump, cpn, alr, ...) make the server serve the UMP/SABR
                // stream and reject a plain download (403); removing them yields a directly
                // downloadable progressive URL. None of these are covered by the signature (sparams).
                return k is not ("range" or "rn" or "rbuf"
                    or "alr" or "cpn" or "c" or "cver" or "ump" or "srfvp");
            });

        var joined = string.Join("&", kept);
        return joined.Length == 0 ? baseUrl : baseUrl + "?" + joined;
    }
}
