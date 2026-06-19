namespace auto_ffmpeg;

using System.Text.Json;

public sealed record Transcode(string Itag, string Url);

/// <summary>
/// Parses the Google Drive "workspacevideo" metadata JSON for progressive
/// (muxed, single-file) transcode URLs. The reference extension reads
/// mediaStreamingData.formatStreamingData.progressiveTranscodes and takes the
/// last (highest-quality) entry. Pure/static so it is unit-testable.
/// </summary>
public static class ProgressiveTranscodes
{
    public static IReadOnlyList<Transcode> Parse(string json)
    {
        var result = new List<Transcode>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mediaStreamingData", out var msd)) return result;
            if (!msd.TryGetProperty("formatStreamingData", out var fsd)) return result;
            if (!fsd.TryGetProperty("progressiveTranscodes", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var t in arr.EnumerateArray())
            {
                if (!t.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) continue;
                var url = urlEl.GetString();
                if (string.IsNullOrEmpty(url)) continue;

                string itag = "?";
                if (t.TryGetProperty("itag", out var itagEl))
                    itag = itagEl.ValueKind == JsonValueKind.Number
                        ? itagEl.GetRawText()
                        : (itagEl.GetString() ?? "?");

                result.Add(new Transcode(itag, url));
            }
        }
        catch (JsonException) { /* malformed -> empty */ }
        return result;
    }

    public static Transcode? Highest(IReadOnlyList<Transcode> list)
        => list.Count == 0 ? null : list[^1];

    public static string? Title(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mediaMetadata", out var mm)
                && mm.TryGetProperty("title", out var t)
                && t.ValueKind == JsonValueKind.String)
                return t.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
