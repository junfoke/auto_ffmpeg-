namespace auto_ffmpeg;

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public static class DriveDownloader
{
    // Matches yt-dlp progress lines like: "[download]  12.3% of 45.6MiB ..."
    private static readonly Regex DownloadPctRegex =
        new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    private static string? _cachedYtDlp;

    public static string? ResolveYtDlpPath()
    {
        if (_cachedYtDlp != null && File.Exists(_cachedYtDlp)) return _cachedYtDlp;

        string local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(local)) return _cachedYtDlp = local;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "yt-dlp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string? line = p?.StandardOutput.ReadLine();
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                return _cachedYtDlp = line;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Download from Google Drive using yt-dlp.
    /// Drive serves a single progressive stream, so no DASH format selection / merging.
    /// </summary>
    public static async Task<bool> DownloadAsync(
        string url,
        bool useCookies, string chromeProfile,
        string? cookiesFile,
        string outputDir,
        CancellationToken ct,
        Action<string>? onLog = null,
        Action<int>? onProgress = null)
    {
        var ytdlp = ResolveYtDlpPath();
        if (ytdlp is null)
        {
            onLog?.Invoke("[ERROR] Khong tim thay yt-dlp.exe. Hay dat yt-dlp.exe cung thu muc ung dung hoac them vao PATH.");
            return false;
        }

        var cookieArg = !string.IsNullOrEmpty(cookiesFile) && File.Exists(cookiesFile)
            ? $"--cookies \"{cookiesFile}\" "
            : useCookies
                ? $"--cookies-from-browser \"chrome:{chromeProfile}\" "
                : "";

        var psi = new ProcessStartInfo
        {
            // --newline forces yt-dlp to emit progress on separate lines so we can parse %
            Arguments = $"{cookieArg}--newline \"{url}\" -o \"%(title)s.%(ext)s\"",
            FileName = ytdlp,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = outputDir
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
            // WaitForExitAsync genuinely observes the token, unlike Task.Run(() => WaitForExit(), ct)
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
