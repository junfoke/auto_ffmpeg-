namespace auto_ffmpeg;

using System.Diagnostics;
using System.Text.RegularExpressions;

public static class Aria2Downloader
{
    // aria2c console progress: "[#abc 12MiB/360MiB(3%) CN:16 DL:1.1MiB ETA:..]"
    private static readonly Regex PctRegex = new(@"\((\d+)%\)", RegexOptions.Compiled);

    /// <summary>
    /// Download one URL with multi-connection aria2c into outputPath (full file path).
    /// Returns false if aria2c is missing or the download fails. On cancel, kills the
    /// process and deletes the partial file.
    /// </summary>
    public static async Task<bool> DownloadAsync(
        string url, string outputPath,
        CancellationToken ct,
        Action<string>? onLog = null,
        Action<int>? onProgress = null,
        string? cookieHeader = null,
        string? userAgent = null)
    {
        var aria2c = DriveDownloader.ResolveAria2cPath();
        if (aria2c is null)
        {
            onLog?.Invoke("[ERROR] Khong tim thay aria2c.exe.");
            return false;
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir)) { onLog?.Invoke("[ERROR] outputPath phai co duong dan thu muc."); return false; }
        var file = Path.GetFileName(outputPath);
        Directory.CreateDirectory(dir);

        var ua = string.IsNullOrEmpty(userAgent)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
            : userAgent;
        // Replicate the authenticated browser session so googlevideo returns 200 (not 403) for a
        // download-restricted file. The browser succeeds with a top-level NAVIGATION request: besides
        // the Cookie, it carries Sec-Fetch-* + Upgrade-Insecure-Requests headers that the server checks
        // (the URL is origin-bound: obr=..., source=webdrive). Replicate that exact shape.
        var cookieArg = "";
        if (!string.IsNullOrEmpty(cookieHeader))
            cookieArg = $"--header=\"Cookie: {cookieHeader}\" " +
                        "--header=\"Sec-Fetch-Site: none\" " +
                        "--header=\"Sec-Fetch-Mode: navigate\" " +
                        "--header=\"Sec-Fetch-User: ?1\" " +
                        "--header=\"Sec-Fetch-Dest: document\" " +
                        "--header=\"Upgrade-Insecure-Requests: 1\" " +
                        "--header=\"Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8\" ";

        var psi = new ProcessStartInfo
        {
            FileName = aria2c,
            Arguments = $"-x 16 -s 16 -k 1M --console-log-level=warn --summary-interval=1 " +
                        $"--allow-overwrite=true --auto-file-renaming=false " +
                        $"--user-agent=\"{ua}\" " +
                        $"--referer=\"https://drive.google.com/\" " +
                        cookieArg +
                        $"-d \"{dir}\" -o \"{file}\" \"{url}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void HandleLine(string? data)
        {
            if (data == null) return;
            onLog?.Invoke(data);
            var m = PctRegex.Match(data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) onProgress?.Invoke(pct);
        }
        proc.OutputDataReceived += (_, e) => HandleLine(e.Data);
        proc.ErrorDataReceived  += (_, e) => HandleLine(e.Data);

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
            await TryDeleteAsync(outputPath);
            await TryDeleteAsync(outputPath + ".aria2");
            throw;
        }

        return proc.ExitCode == 0;
    }

    private static async Task TryDeleteAsync(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (IOException) when (attempt < 2) { await Task.Delay(200); }
            catch { return; }
        }
    }
}
