namespace auto_ffmpeg;

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public static class HlsDownloader
{
    // Matches yt-dlp progress lines like: "[download]  12.3% of 45.6MiB ..."
    private static readonly Regex DownloadPctRegex =
        new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    /// <summary>
    /// Download an HLS (.m3u8) URL to a specific output file via yt-dlp (auto-resumes
    /// on network drops). referer/userAgent are omitted when empty.
    /// </summary>
    public static async Task<bool> DownloadWithYtDlpAsync(
        string url, string output, string referer, string userAgent,
        CancellationToken ct,
        Action<string>? onLog = null,
        Action<int>? onProgress = null)
    {
        var ytdlp = DriveDownloader.ResolveYtDlpPath();
        if (ytdlp is null)
        {
            onLog?.Invoke("[ERROR] Khong tim thay yt-dlp.exe. Hay dat yt-dlp.exe cung thu muc ung dung hoac them vao PATH.");
            return false;
        }

        var refArg = string.IsNullOrWhiteSpace(referer) ? "" : $"--add-header \"Referer:{referer}\" ";
        var uaArg = string.IsNullOrWhiteSpace(userAgent) ? "" : $"--user-agent \"{userAgent}\" ";

        var psi = new ProcessStartInfo
        {
            // --newline forces yt-dlp to emit progress on separate lines so we can parse %
            Arguments = $"{refArg}{uaArg}--newline \"{url}\" -o \"{output}\"",
            FileName = ytdlp,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            onLog?.Invoke(e.Data);
            var m = DownloadPctRegex.Match(e.Data);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                onProgress?.Invoke((int)pct);
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return proc.ExitCode == 0;
    }
}
