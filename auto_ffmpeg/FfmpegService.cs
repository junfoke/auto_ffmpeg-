namespace auto_ffmpeg;

using System.Diagnostics;
using System.Globalization;

public static class FfmpegService
{
    private static readonly string[] VideoExts = [".mp4"];
    private static readonly string[] AudioExts = [".m4a", ".mp3", ".aac", ".wav", ".flac", ".opus", ".ogg"];

    public static bool IsVideoExt(string ext) => VideoExts.Contains(ext.ToLower());
    public static bool IsAudioExt(string ext) => AudioExts.Contains(ext.ToLower());

    public static int AudioExtPriority(string ext) => ext.ToLower() switch
    {
        ".m4a" => 0,
        ".aac" => 1,
        ".mp3" => 2,
        ".wav" => 3,
        ".flac" => 4,
        ".opus" => 5,
        ".ogg" => 6,
        _ => 9
    };

    // Cache only a successful resolution; never cache a null so that the user
    // can drop ffmpeg.exe next to the app (or add it to PATH) and retry.
    private static string? _cachedFfmpeg;

    public static string? ResolveFfmpegPath()
    {
        if (_cachedFfmpeg != null && File.Exists(_cachedFfmpeg)) return _cachedFfmpeg;

        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return _cachedFfmpeg = local;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string? line = p?.StandardOutput.ReadLine();
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                return _cachedFfmpeg = line;
        }
        catch { }
        return null;
    }

    public static string BuildArgs(string video, string audio, string output, bool useCopy)
    {
        var codec = useCopy
            ? "-c:v copy -c:a copy"
            : "-c:v copy -c:a aac -b:a 192k";

        return $"-hide_banner -loglevel warning -progress pipe:1 " +
               $"-i \"{video}\" -i \"{audio}\" " +
               $"-map 0:v:0 -map 1:a:0 {codec} " +
               $"-shortest -movflags +faststart -y \"{output}\"";
    }

    /// <summary>
    /// Run ffprobe to get duration in microseconds. Returns 0 on failure.
    /// </summary>
    public static async Task<long> GetDurationUsAsync(string filePath)
    {
        var ffprobePath = ResolveFfprobePath();
        if (ffprobePath is null) return 0;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{filePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return 0;
            string? line = await p.StandardOutput.ReadLineAsync();
            await p.WaitForExitAsync();
            if (double.TryParse(line?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                return (long)(secs * 1_000_000);
        }
        catch { }
        return 0;
    }

    private static string? ResolveFfprobePath()
    {
        // ffprobe is typically next to ffmpeg
        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg is null) return null;
        var dir = Path.GetDirectoryName(ffmpeg)!;
        var ffprobe = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(ffprobe) ? ffprobe : null;
    }

    /// <summary>
    /// Merge video + audio. Tries copy first if useCopy=true, then falls back to AAC if fallbackAac=true.
    /// onProgress receives 0-100. onLog receives log lines.
    /// </summary>
    public static async Task<bool> MergeAsync(
        string video, string audio, string output,
        bool useCopy, bool fallbackAac,
        CancellationToken ct,
        Action<int>? onProgress = null,
        Action<string>? onLog = null)
    {
        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        long durationUs = await GetDurationUsAsync(video);

        if (useCopy)
        {
            var args = BuildArgs(video, audio, output, useCopy: true);
            var ok = await RunFfmpegAsync(ffmpegPath, args, durationUs, ct, onProgress, onLog);
            if (ok) return true;

            onLog?.Invoke("[WARN] -c copy that bai, thu fallback AAC...");
            if (!fallbackAac) return false;
        }

        // Fallback or direct AAC
        var aacArgs = BuildArgs(video, audio, output, useCopy: false);
        onProgress?.Invoke(0); // reset progress for retry
        return await RunFfmpegAsync(ffmpegPath, aacArgs, durationUs, ct, onProgress, onLog);
    }

    private static async Task<bool> RunFfmpegAsync(
        string ffmpegPath, string arguments, long durationUs,
        CancellationToken ct,
        Action<int>? onProgress, Action<string>? onLog)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) onLog?.Invoke(e.Data);
        };

        // stdout will contain -progress output (key=value lines)
        p.Start();
        p.BeginErrorReadLine();

        // Parse progress from stdout
        var stdoutTask = Task.Run(async () =>
        {
            while (!p.StandardOutput.EndOfStream)
            {
                var line = await p.StandardOutput.ReadLineAsync();
                if (line == null) break;

                if (durationUs > 0 && line.StartsWith("out_time_us="))
                {
                    var val = line.Substring("out_time_us=".Length);
                    if (long.TryParse(val, out var currentUs) && currentUs >= 0)
                    {
                        int pct = (int)Math.Min(100, currentUs * 100 / durationUs);
                        onProgress?.Invoke(pct);
                    }
                }
            }
        }, ct);

        try
        {
            // WaitForExitAsync genuinely observes the token, unlike Task.Run(() => WaitForExit(), ct)
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        await stdoutTask;
        return p.ExitCode == 0;
    }
}
