namespace auto_ffmpeg;

using System.Net;
using System.Text.RegularExpressions;

/// <summary>
/// Parallel downloader for Google Drive <c>videoplayback</c> streams via <c>&amp;range=START-END</c>
/// query-param segments, using the authenticated browser session's Cookie + User-Agent (+ the exact
/// browser request headers) so a download-restricted file returns 200 instead of 403.
///
/// A single full GET is throttled by googlevideo to ~playback bitrate; the player gets full speed by
/// firing many bounded &amp;range= requests in parallel — that's what this replicates. Returns false
/// (cleaning up any partial file) if the server rejects the request, so the caller can fall back to
/// the single-connection browser download.
/// </summary>
public static class DriveSegmentDownloader
{
    private const int SegmentSize = 4 * 1024 * 1024; // 4 MiB per &range= request
    private const int Parallelism = 16;              // concurrent segments (like IDM/aria2c -x16)
    private const int MaxRetries = 3;

    /// <summary>Number of concurrent segment requests (for log/UI).</summary>
    public const int SegmentParallelism = Parallelism;

    public static async Task<bool> DownloadAsync(
        string url, string outputPath, long totalSize,
        string? cookieHeader, string? userAgent,
        CancellationToken ct,
        Action<string>? onLog = null, Action<int>? onProgress = null)
    {
        if (totalSize <= 0) { onLog?.Invoke("[DEBUG] Segment DL: thieu clen, bo qua."); return false; }

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,                              // we set Cookie manually, no container
            AutomaticDecompression = DecompressionMethods.None, // raw bytes -> Content-Length == real size
            MaxConnectionsPerServer = Parallelism * 2,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // Replicate EXACTLY the browser navigation headers we logged (the request Google answers 200):
        // Accept, Upgrade-Insecure-Requests, User-Agent, sec-ch-ua family, Cookie. No Referer (the
        // browser navigation had none). sec-ch-ua version is derived from the UA so it stays in sync.
        var h = http.DefaultRequestHeaders;
        if (!string.IsNullOrEmpty(userAgent)) h.TryAddWithoutValidation("User-Agent", userAgent);
        if (!string.IsNullOrEmpty(cookieHeader)) h.TryAddWithoutValidation("Cookie", cookieHeader);
        h.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        h.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        var ver = Regex.Match(userAgent ?? "", @"Chrome/(\d+)").Groups[1].Value;
        if (ver.Length == 0) ver = "149";
        h.TryAddWithoutValidation("sec-ch-ua", $"\"Google Chrome\";v=\"{ver}\", \"Chromium\";v=\"{ver}\", \"Not)A;Brand\";v=\"24\"");
        h.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        h.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");

        var sep = url.Contains('?') ? "&" : "?";

        // DECISIVE PROBE: full GET vs range GET. Tells us whether 403 is client-identity (both fail)
        // or range-rejection (full ok, range fails). ResponseHeadersRead + dispose = no body downloaded.
        async Task<int> ProbeAsync(string u)
        {
            try
            {
                using var rq = new HttpRequestMessage(HttpMethod.Get, u);
                using var rp = await http.SendAsync(rq, HttpCompletionOption.ResponseHeadersRead, ct);
                return (int)rp.StatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { onLog?.Invoke($"[DEBUG] Probe loi: {ex.Message}"); return -1; }
        }

        long firstEnd = Math.Min(SegmentSize, totalSize) - 1;
        int sFull = await ProbeAsync(url);
        int sRange = await ProbeAsync($"{url}{sep}range=0-{firstEnd}");
        onLog?.Invoke($"[DEBUG] Probe HttpClient: full-GET={sFull}, range-GET={sRange}");
        if (sRange is not (200 or 206))
        {
            onLog?.Invoke(sFull is 200 or 206
                ? "[DEBUG] Identity OK nhung RANGE bi tu choi -> can chia range qua browser."
                : "[DEBUG] HttpClient bi tu choi (identity) -> dung browser.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var ranges = new List<(long start, long end)>();
        for (long s = 0; s < totalSize; s += SegmentSize)
            ranges.Add((s, Math.Min(s + SegmentSize, totalSize) - 1));

        long downloaded = 0;
        int lastPct = -1;
        bool ok = false;

        var fh = File.OpenHandle(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        try
        {
            RandomAccess.SetLength(fh, totalSize);
            using var sem = new SemaphoreSlim(Parallelism);

            async Task DoSegment((long start, long end) r, int index)
            {
                await sem.WaitAsync(ct);
                try
                {
                    var segUrl = $"{url}{sep}range={r.start}-{r.end}&rn={index}&rbuf=0";
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, segUrl);
                            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                            int status = (int)resp.StatusCode;
                            if (status is not (200 or 206)) throw new HttpRequestException($"status={status}");

                            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                            var buffer = new byte[81920];
                            long offset = r.start;
                            int read;
                            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                            {
                                await RandomAccess.WriteAsync(fh, buffer.AsMemory(0, read), offset, ct);
                                offset += read;
                                long now = Interlocked.Add(ref downloaded, read);
                                int pct = (int)(now * 100 / totalSize);
                                if (pct != lastPct) { lastPct = pct; onProgress?.Invoke(pct); }
                            }
                            return;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
                        { await Task.Delay(400 * (attempt + 1), ct); }
                    }
                }
                finally { sem.Release(); }
            }

            await Task.WhenAll(ranges.Select((r, i) => DoSegment(r, i)));
            ok = downloaded >= totalSize;
            return ok;
        }
        catch (OperationCanceledException) { throw; } // genuine user cancel
        catch (Exception ex)
        {
            onLog?.Invoke($"[WARN] Tai song song theo range that bai: {ex.Message}");
            return false;
        }
        finally
        {
            fh.Dispose();
            if (!ok) { try { File.Delete(outputPath); } catch { } } // don't leave a partial/sparse file
        }
    }
}
