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
    private static string? _cachedAria2c;

    public static string? ResolveYtDlpPath() => ResolveExe("yt-dlp", ref _cachedYtDlp);

    /// <summary>Locate aria2c.exe (bundled next to the app or on PATH). Null if unavailable.</summary>
    public static string? ResolveAria2cPath() => ResolveExe("aria2c", ref _cachedAria2c);

    private static string? ResolveExe(string name, ref string? cache)
    {
        if (cache != null && File.Exists(cache)) return cache;

        string local = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        if (File.Exists(local)) return cache = local;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string? line = p?.StandardOutput.ReadLine();
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                return cache = line;
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
        string? browser, string profile,
        string? cookiesFile,
        bool useAria2c,
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

        var browserSpec = string.IsNullOrEmpty(browser)
            ? browser
            : string.IsNullOrEmpty(profile) ? browser : $"{browser}:{profile}";

        var cookieArg = !string.IsNullOrEmpty(cookiesFile) && File.Exists(cookiesFile)
            ? $"--cookies \"{cookiesFile}\" "
            : !string.IsNullOrEmpty(browserSpec)
                ? $"--cookies-from-browser \"{browserSpec}\" "
                : "";

        // aria2c downloads in parallel segments (like IDM), bypassing Drive's single-connection throttle
        var aria2cArg = "";
        if (useAria2c)
        {
            var aria2c = ResolveAria2cPath();
            if (aria2c != null)
                aria2cArg = $"--downloader \"{aria2c}\" --downloader-args \"aria2c:-x 16 -s 16 -k 1M\" ";
            else
                onLog?.Invoke("[WARN] Khong tim thay aria2c.exe, tai bang che do thuong (cham hon). Dat aria2c.exe cung thu muc ung dung de tang toc.");
        }

        var psi = new ProcessStartInfo
        {
            // --newline forces yt-dlp to emit progress on separate lines so we can parse %
            Arguments = $"{cookieArg}{aria2cArg}--newline \"{url}\" -o \"%(title)s.%(ext)s\"",
            FileName = ytdlp,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = outputDir
        };

        // snapshot existing files so a cancel only deletes partials we created
        HashSet<string> filesBefore;
        try { filesBefore = Directory.EnumerateFiles(outputDir).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { filesBefore = new(StringComparer.OrdinalIgnoreCase); }

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
            await CleanupPartialsAsync(outputDir, filesBefore, onLog);
            throw;
        }

        return proc.ExitCode == 0;
    }

    private static bool IsPartialFile(string name) =>
        name.Contains(".part", StringComparison.OrdinalIgnoreCase)   // .part and .part-FragN
        || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Delete yt-dlp partial/temp files created during this download (those not present in filesBefore).
    /// Retries briefly since the killed process may still hold the file handle.
    /// </summary>
    private static async Task CleanupPartialsAsync(string dir, HashSet<string> filesBefore, Action<string>? onLog)
    {
        List<string> targets;
        try
        {
            targets = Directory.EnumerateFiles(dir)
                .Where(f => !filesBefore.Contains(f) && IsPartialFile(Path.GetFileName(f)))
                .ToList();
        }
        catch { return; }

        foreach (var f in targets)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Delete(f);
                    onLog?.Invoke($"[INFO] Da xoa file tai do: {Path.GetFileName(f)}");
                    break;
                }
                catch (IOException) when (attempt < 2) { await Task.Delay(200); }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[WARN] Khong xoa duoc {Path.GetFileName(f)}: {ex.Message}");
                    break;
                }
            }
        }
    }
}
