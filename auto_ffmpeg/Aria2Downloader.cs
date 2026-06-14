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
        Action<int>? onProgress = null)
    {
        var aria2c = DriveDownloader.ResolveAria2cPath();
        if (aria2c is null)
        {
            onLog?.Invoke("[ERROR] Khong tim thay aria2c.exe.");
            return false;
        }

        var dir = Path.GetDirectoryName(outputPath)!;
        var file = Path.GetFileName(outputPath);
        Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = aria2c,
            Arguments = $"-x 16 -s 16 -k 1M --console-log-level=warn --summary-interval=1 " +
                        $"--allow-overwrite=true --auto-file-renaming=false " +
                        $"-d \"{dir}\" -o \"{file}\" \"{url}\"",
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
            var m = PctRegex.Match(e.Data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) onProgress?.Invoke(pct);
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
            TryDelete(outputPath);
            TryDelete(outputPath + ".aria2");
            throw;
        }

        return proc.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
