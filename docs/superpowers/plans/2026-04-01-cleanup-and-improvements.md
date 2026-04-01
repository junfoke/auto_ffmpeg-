# Auto FFmpeg Muxer — Cleanup & Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stabilize and clean up the Auto FFmpeg Muxer tool — fix bugs, improve code structure, and add UX improvements (cancel, drag & drop, real progress).

**Architecture:** Extract logic from monolithic Program.cs into focused service classes (FfmpegService, DriveDownloader). MainForm handles only UI. All async operations accept CancellationToken for cancellation support.

**Tech Stack:** .NET 8, WinForms, FFmpeg/FFprobe (external), yt-dlp (external)

---

### Task 1: Delete Unused Files & Create PairInfo.cs

**Files:**
- Delete: `auto_ffmpeg/Form1.cs`
- Delete: `auto_ffmpeg/Form1.Designer.cs`
- Create: `auto_ffmpeg/PairInfo.cs`

- [ ] **Step 1: Delete Form1.cs and Form1.Designer.cs**

```bash
cd "D:/MyProject/c#/auto_ffmpeg"
rm auto_ffmpeg/Form1.cs auto_ffmpeg/Form1.Designer.cs
```

- [ ] **Step 2: Create PairInfo.cs**

Write `auto_ffmpeg/PairInfo.cs`:

```csharp
namespace auto_ffmpeg;

public class PairInfo
{
    public string? Basename { get; set; }
    public string? Video { get; set; }
    public string? Audio { get; set; }
    public string? Output { get; set; }
}
```

- [ ] **Step 3: Verify project builds**

```bash
cd "D:/MyProject/c#/auto_ffmpeg"
dotnet build auto_ffmpeg/auto_ffmpeg.csproj
```

Expected: Build succeeds (Form1 was never referenced by Program.cs/MainForm).

- [ ] **Step 4: Commit**

```bash
git add -A auto_ffmpeg/Form1.cs auto_ffmpeg/Form1.Designer.cs auto_ffmpeg/PairInfo.cs
git commit -m "chore: delete unused Form1 files, extract PairInfo class"
```

---

### Task 2: Create FfmpegService.cs

**Files:**
- Create: `auto_ffmpeg/FfmpegService.cs`

- [ ] **Step 1: Create FfmpegService.cs with ResolveFfmpegPath and BuildArgs**

Write `auto_ffmpeg/FfmpegService.cs`:

```csharp
namespace auto_ffmpeg;

using System.Diagnostics;
using System.Globalization;

public static class FfmpegService
{
    private static readonly string[] VideoExts = [".mp4"];
    private static readonly string[] AudioExts = [".m4a", ".mp3", ".aac", ".wav", ".flac", ".opus", ".ogg"];

    public static bool IsVideoExt(string ext) => VideoExts.Contains(ext.ToLower());
    public static bool IsAudioExt(string ext) => AudioExts.Contains(ext.ToLower());
    public static string[] GetAudioExts() => AudioExts;

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

    public static string? ResolveFfmpegPath()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

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
                return line;
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
            await Task.Run(() => p.WaitForExit(), ct);
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
```

- [ ] **Step 2: Verify project builds**

```bash
cd "D:/MyProject/c#/auto_ffmpeg"
dotnet build auto_ffmpeg/auto_ffmpeg.csproj
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add auto_ffmpeg/FfmpegService.cs
git commit -m "feat: add FfmpegService with merge, progress parsing, and cancellation"
```

---

### Task 3: Create DriveDownloader.cs

**Files:**
- Create: `auto_ffmpeg/DriveDownloader.cs`

- [ ] **Step 1: Create DriveDownloader.cs**

Write `auto_ffmpeg/DriveDownloader.cs`:

```csharp
namespace auto_ffmpeg;

using System.Diagnostics;

public static class DriveDownloader
{
    /// <summary>
    /// Download from Google Drive using yt-dlp.
    /// Pass the raw URL — yt-dlp handles Drive URLs natively.
    /// </summary>
    public static async Task<bool> DownloadAsync(
        string url,
        bool useCookies, string chromeProfile,
        CancellationToken ct,
        Action<string>? onLog = null)
    {
        var cookieArg = useCookies
            ? $"--cookies-from-browser \"chrome:profile={chromeProfile}\" "
            : "";

        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp.exe",
            Arguments = $"{cookieArg}\"{url}\" " +
                        "-f \"136+140/best\" --merge-output-format mp4 " +
                        "-o \"%(title)s_merged.%(ext)s\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await Task.Run(() => proc.WaitForExit(), ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return proc.ExitCode == 0;
    }
}
```

- [ ] **Step 2: Verify project builds**

```bash
cd "D:/MyProject/c#/auto_ffmpeg"
dotnet build auto_ffmpeg/auto_ffmpeg.csproj
```

- [ ] **Step 3: Commit**

```bash
git add auto_ffmpeg/DriveDownloader.cs
git commit -m "feat: add DriveDownloader with cookies option and cancellation"
```

---

### Task 4: Rewrite MainForm.cs — UI Layout

This is the largest task. It rewrites MainForm with: checkbox moved to bottom, cancel button, Drive tab cookies UI, drag & drop, and wiring to FfmpegService/DriveDownloader.

**Files:**
- Modify: `auto_ffmpeg/Program.cs` (trim to entry point only)
- Create: `auto_ffmpeg/MainForm.cs` (full UI)

- [ ] **Step 1: Rewrite Program.cs to entry point only**

Overwrite `auto_ffmpeg/Program.cs`:

```csharp
namespace auto_ffmpeg;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
```

- [ ] **Step 2: Create MainForm.cs with full UI and logic**

Write `auto_ffmpeg/MainForm.cs` — this is the complete file:

```csharp
namespace auto_ffmpeg;

using System.Text;

public class MainForm : Form
{
    // ====== Common (bottom area) ======
    readonly CheckBox chkCopy = new() { Text = "Dung stream copy (-c copy) - nhanh nhat", Checked = true, AutoSize = true };
    readonly CheckBox chkFallback = new() { Text = "Tu dong fallback AAC neu copy loi", Checked = true, AutoSize = true };
    readonly TextBox txtLog = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 760, Height = 180, ReadOnly = true };
    readonly Label lblStatus = new() { AutoSize = true, Text = "San sang." };
    readonly ProgressBar progress = new() { Style = ProgressBarStyle.Blocks, Width = 680, Minimum = 0, Maximum = 100 };
    readonly Button btnCancel = new() { Text = "Huy", Width = 70, Visible = false };
    readonly StringBuilder _logBuf = new();
    System.Windows.Forms.Timer _logTimer = null!;
    CancellationTokenSource? _cts;

    // ====== Single tab ======
    readonly TextBox txtVideo = new() { ReadOnly = true, Width = 480 };
    readonly TextBox txtAudio = new() { ReadOnly = true, Width = 480 };
    readonly TextBox txtOutput = new() { ReadOnly = true, Width = 480 };
    readonly Button btnPickVideo = new() { Text = "Chon Video..." };
    readonly Button btnPickAudio = new() { Text = "Chon Audio..." };
    readonly Button btnPickOutput = new() { Text = "Noi luu..." };
    readonly Button btnRun = new() { Text = "Ghep (1 cap)", Width = 140 };

    // ====== Batch tab ======
    readonly TextBox txtFolder = new() { ReadOnly = true, Width = 480 };
    readonly Button btnPickFolder = new() { Text = "Chon Thu muc..." };
    readonly Button btnScan = new() { Text = "Quet cap trung ten" };
    readonly Button btnBatchMerge = new() { Text = "Ghep tat ca", Width = 140 };
    readonly ListView lvPairs = new()
    {
        View = View.Details,
        CheckBoxes = true,
        Width = 760,
        Height = 220,
        FullRowSelect = true
    };

    // ====== Drive tab ======
    readonly TextBox txtDriveUrl = new() { Width = 480 };
    readonly Button btnDownloadDrive = new() { Text = "Tai tu Google Drive", Width = 180 };
    readonly CheckBox chkUseCookies = new() { Text = "Dung cookies Chrome", AutoSize = true };
    readonly TextBox txtChromeProfile = new() { Text = "Default", Width = 120, Enabled = false };

    // All action buttons for enable/disable
    Button[] ActionButtons => [btnRun, btnBatchMerge, btnDownloadDrive, btnScan,
                                btnPickVideo, btnPickAudio, btnPickOutput, btnPickFolder];

    public MainForm()
    {
        Text = "Auto FFmpeg Muxer - ghep video + audio";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 840;
        Height = 720;
        Font = new System.Drawing.Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        // ====== Tabs ======
        var tabs = new TabControl { Dock = DockStyle.Top, Height = 340 };
        var tabSingle = new TabPage("Ghep 1 cap") { AllowDrop = true };
        var tabBatch = new TabPage("Ghep theo thu muc") { AllowDrop = true };
        var tabDrive = new TabPage("Tai tu Google Drive");
        tabs.TabPages.Add(tabSingle);
        tabs.TabPages.Add(tabBatch);
        tabs.TabPages.Add(tabDrive);

        // ====== Single UI ======
        var sTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Padding = new Padding(12)
        };
        sTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        sTable.Controls.Add(MakeLabel("File Video (.mp4):"), 0, 0);
        sTable.Controls.Add(txtVideo, 1, 0);
        sTable.Controls.Add(btnPickVideo, 2, 0);

        sTable.Controls.Add(MakeLabel("File Audio:"), 0, 1);
        sTable.Controls.Add(txtAudio, 1, 1);
        sTable.Controls.Add(btnPickAudio, 2, 1);

        sTable.Controls.Add(MakeLabel("File xuat (.mp4):"), 0, 2);
        sTable.Controls.Add(txtOutput, 1, 2);
        sTable.Controls.Add(btnPickOutput, 2, 2);

        var sFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
        sFlow.Controls.Add(btnRun);
        sTable.SetColumnSpan(sFlow, 3);
        sTable.Controls.Add(sFlow, 0, 3);
        tabSingle.Controls.Add(sTable);

        // ====== Batch UI ======
        var bTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(12)
        };
        bTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        bTable.Controls.Add(MakeLabel("Thu muc nguon:"), 0, 0);
        bTable.Controls.Add(txtFolder, 1, 0);
        bTable.Controls.Add(btnPickFolder, 2, 0);

        var bFlowTop = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
        bFlowTop.Controls.Add(btnScan);
        bFlowTop.Controls.Add(btnBatchMerge);
        bTable.SetColumnSpan(bFlowTop, 3);
        bTable.Controls.Add(bFlowTop, 0, 1);

        lvPairs.Columns.Add("V", 40);
        lvPairs.Columns.Add("Ten co so (basename)", 280);
        lvPairs.Columns.Add("Video", 120);
        lvPairs.Columns.Add("Audio", 120);
        lvPairs.Columns.Add("Output", 200);
        lvPairs.Columns.Add("Trang thai", 160);
        bTable.SetColumnSpan(lvPairs, 3);
        bTable.Controls.Add(lvPairs, 0, 2);
        tabBatch.Controls.Add(bTable);

        // ====== Drive UI ======
        var dTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(12)
        };
        dTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        dTable.Controls.Add(MakeLabel("Link Google Drive:"), 0, 0);
        dTable.Controls.Add(txtDriveUrl, 1, 0);
        dTable.Controls.Add(btnDownloadDrive, 2, 0);

        var dCookieFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
        dCookieFlow.Controls.Add(chkUseCookies);
        dCookieFlow.Controls.Add(new Label { Text = "  Profile:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
        dCookieFlow.Controls.Add(txtChromeProfile);
        dTable.SetColumnSpan(dCookieFlow, 3);
        dTable.Controls.Add(dCookieFlow, 0, 1);
        tabDrive.Controls.Add(dTable);

        // ====== Bottom shared area ======
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // checkboxes
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // log label
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // log
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // progress + cancel
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status

        var chkFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
        chkFlow.Controls.Add(chkCopy);
        chkFlow.Controls.Add(chkFallback);
        bottom.Controls.Add(chkFlow, 0, 0);

        bottom.Controls.Add(new Label { Text = "Nhat ky:", AutoSize = true }, 0, 1);
        bottom.Controls.Add(txtLog, 0, 2);

        var progressFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
        progressFlow.Controls.Add(progress);
        progressFlow.Controls.Add(btnCancel);
        bottom.Controls.Add(progressFlow, 0, 3);

        bottom.Controls.Add(lblStatus, 0, 4);

        // ====== Root layout ======
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(bottom, 0, 1);
        Controls.Add(root);

        // ====== Events ======
        btnPickVideo.Click += (_, _) => PickFile(txtVideo, "Chon Video", "MP4 Video|*.mp4|Tat ca|*.*");
        btnPickAudio.Click += (_, _) => PickFile(txtAudio, "Chon Audio", "Am thanh|*.m4a;*.mp3;*.aac;*.wav;*.flac;*.opus;*.ogg|Tat ca|*.*");
        btnPickOutput.Click += (_, _) => PickSaveFile(txtOutput, "Noi luu", "MP4|*.mp4");
        btnRun.Click += async (_, _) => await RunSingleAsync();
        btnPickFolder.Click += (_, _) => PickFolder();
        btnScan.Click += (_, _) => ScanPairs();
        btnBatchMerge.Click += async (_, _) => await RunBatchAsync();
        btnDownloadDrive.Click += async (_, _) => await RunDriveDownloadAsync();
        btnCancel.Click += (_, _) => _cts?.Cancel();
        chkUseCookies.CheckedChanged += (_, _) => txtChromeProfile.Enabled = chkUseCookies.Checked;

        // Drag & drop — Single tab
        tabSingle.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        tabSingle.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLower();
                if (FfmpegService.IsVideoExt(ext))
                {
                    txtVideo.Text = f;
                    if (string.IsNullOrEmpty(txtOutput.Text))
                    {
                        var dir = Path.GetDirectoryName(f)!;
                        var name = Path.GetFileNameWithoutExtension(f);
                        txtOutput.Text = Path.Combine(dir, name + "_merged.mp4");
                    }
                }
                else if (FfmpegService.IsAudioExt(ext))
                {
                    txtAudio.Text = f;
                }
            }
        };

        // Drag & drop — Batch tab
        tabBatch.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        tabBatch.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            var folder = paths.FirstOrDefault(p => Directory.Exists(p));
            if (folder != null)
            {
                txtFolder.Text = folder;
                ScanPairs();
            }
        };

        // Log flush timer
        _logTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _logTimer.Tick += (_, _) =>
        {
            if (_logBuf.Length == 0) return;
            var s = _logBuf.ToString();
            _logBuf.Clear();
            txtLog.AppendText(s);
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        };
        _logTimer.Start();
    }

    // ====== Helpers ======

    static Label MakeLabel(string text) => new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    void PickFile(TextBox target, string title, string filter)
    {
        using var ofd = new OpenFileDialog { Title = title, Filter = filter };
        if (ofd.ShowDialog(this) == DialogResult.OK)
            target.Text = ofd.FileName;
    }

    void PickSaveFile(TextBox target, string title, string filter)
    {
        using var sfd = new SaveFileDialog { Title = title, Filter = filter, DefaultExt = "mp4" };
        if (sfd.ShowDialog(this) == DialogResult.OK)
            target.Text = sfd.FileName;
    }

    void PickFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Chon thu muc chua .mp4 va audio cung ten" };
        if (fbd.ShowDialog(this) == DialogResult.OK)
            txtFolder.Text = fbd.SelectedPath;
    }

    void SetProcessingState(bool isProcessing)
    {
        foreach (var btn in ActionButtons)
            btn.Enabled = !isProcessing;
        btnCancel.Visible = isProcessing;
        if (isProcessing)
        {
            _cts = new CancellationTokenSource();
            progress.Value = 0;
        }
        else
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    void UpdateProgress(int percent)
    {
        if (InvokeRequired)
            Invoke(() => progress.Value = Math.Clamp(percent, 0, 100));
        else
            progress.Value = Math.Clamp(percent, 0, 100);
    }

    void AppendLog(string line)
    {
        _logBuf.AppendLine(line);
    }

    // ====== Single merge ======

    async Task RunSingleAsync()
    {
        var video = txtVideo.Text.Trim();
        var audio = txtAudio.Text.Trim();
        var output = txtOutput.Text.Trim();

        if (!File.Exists(video)) { MessageBox.Show(this, "Chua chon hoac khong tim thay video .mp4", "Thieu du lieu", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!File.Exists(audio)) { MessageBox.Show(this, "Chua chon hoac khong tim thay file audio", "Thieu du lieu", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.IsNullOrWhiteSpace(output)) { MessageBox.Show(this, "Chua chon noi luu file xuat .mp4", "Thieu du lieu", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        if (FfmpegService.ResolveFfmpegPath() is null)
        {
            MessageBox.Show(this, "Khong tim thay ffmpeg.\n- Dat ffmpeg.exe cung thu muc .exe cua ung dung, hoac\n- Them ffmpeg vao PATH.", "Thieu ffmpeg", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        txtLog.Clear();
        SetProcessingState(true);
        lblStatus.Text = "Dang ghep 1 cap...";

        try
        {
            var ok = await FfmpegService.MergeAsync(video, audio, output,
                chkCopy.Checked, chkFallback.Checked, _cts!.Token,
                onProgress: UpdateProgress, onLog: AppendLog);

            progress.Value = 100;
            lblStatus.Text = ok ? "Hoan tat!" : "Co loi khi ghep.";
            if (ok)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Da huy.";
            AppendLog("[INFO] Nguoi dung da huy.");
        }
        finally
        {
            SetProcessingState(false);
        }
    }

    // ====== Batch scan ======

    void ScanPairs()
    {
        lvPairs.Items.Clear();
        var dir = txtFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show(this, "Chua chon thu muc hop le.", "Thong bao", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var files = Directory.EnumerateFiles(dir)
            .Where(p =>
            {
                var ext = Path.GetExtension(p).ToLower();
                return FfmpegService.IsVideoExt(ext) || FfmpegService.IsAudioExt(ext);
            })
            .ToList();

        var byBase = files.GroupBy(p => Path.GetFileNameWithoutExtension(p));
        int count = 0;
        foreach (var grp in byBase)
        {
            var vids = grp.Where(p => FfmpegService.IsVideoExt(Path.GetExtension(p).ToLower())).ToList();
            var auds = grp.Where(p => FfmpegService.IsAudioExt(Path.GetExtension(p).ToLower())).ToList();
            if (vids.Count == 0 || auds.Count == 0) continue;

            var audioPick = auds.OrderBy(p => FfmpegService.AudioExtPriority(Path.GetExtension(p))).First();
            var videoPick = vids.First();
            var output = Path.Combine(dir, grp.Key + "_merged.mp4");

            var item = new ListViewItem() { Checked = true };
            item.SubItems.Add(grp.Key);
            item.SubItems.Add(Path.GetExtension(videoPick).ToLower().TrimStart('.'));
            item.SubItems.Add(Path.GetExtension(audioPick).ToLower().TrimStart('.'));
            item.SubItems.Add(Path.GetFileName(output));
            item.SubItems.Add("Dang cho");
            item.Tag = new PairInfo { Video = videoPick, Audio = audioPick, Output = output, Basename = grp.Key };
            lvPairs.Items.Add(item);
            count++;
        }

        lblStatus.Text = count > 0 ? $"Da tim thay {count} cap trung ten." : "Khong tim thay cap nao.";
    }

    // ====== Batch merge ======

    async Task RunBatchAsync()
    {
        if (lvPairs.Items.Count == 0)
        {
            MessageBox.Show(this, "Chua co cap nao de ghep. Hay bam 'Quet cap trung ten' truoc.", "Thong bao", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selected = lvPairs.Items.Cast<ListViewItem>().Where(it => it.Checked).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Ban chua tick chon cap nao.", "Thong bao", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (FfmpegService.ResolveFfmpegPath() is null)
        {
            MessageBox.Show(this, "Khong tim thay ffmpeg.", "Thieu ffmpeg", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        txtLog.Clear();
        SetProcessingState(true);
        lblStatus.Text = $"Dang ghep {selected.Count} cap...";
        int totalPairs = selected.Count;
        int completedPairs = 0;

        try
        {
            foreach (var item in selected)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                var info = (PairInfo)item.Tag;
                item.SubItems[5].Text = "Dang xu ly...";

                int cp = completedPairs; // capture for closure
                bool ok = await FfmpegService.MergeAsync(
                    info.Video!, info.Audio!, info.Output!,
                    chkCopy.Checked, chkFallback.Checked, _cts!.Token,
                    onProgress: pct => UpdateProgress((cp * 100 + pct) / totalPairs),
                    onLog: AppendLog);

                item.SubItems[5].Text = ok ? "V Thanh cong" : "X Loi";
                completedPairs++;
                UpdateProgress(completedPairs * 100 / totalPairs);
            }

            lblStatus.Text = "Hoan tat batch.";
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Da huy batch.";
            AppendLog("[INFO] Nguoi dung da huy.");
        }
        finally
        {
            SetProcessingState(false);
        }
    }

    // ====== Drive download ======

    async Task RunDriveDownloadAsync()
    {
        string url = txtDriveUrl.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show(this, "Hay nhap link Google Drive truoc.", "Thieu du lieu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        txtLog.Clear();
        SetProcessingState(true);
        progress.Style = ProgressBarStyle.Marquee;
        lblStatus.Text = "Dang tai video tu Google Drive...";

        try
        {
            var ok = await DriveDownloader.DownloadAsync(
                url, chkUseCookies.Checked, txtChromeProfile.Text.Trim(),
                _cts!.Token, onLog: AppendLog);

            lblStatus.Text = ok ? "Tai hoan tat! File da duoc ghep." : "Loi khi tai.";
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Da huy tai.";
            AppendLog("[INFO] Nguoi dung da huy.");
        }
        finally
        {
            progress.Style = ProgressBarStyle.Blocks;
            SetProcessingState(false);
        }
    }
}
```

- [ ] **Step 3: Delete old Program.cs content (already overwritten in step 1)**

Already handled — Program.cs was overwritten in Step 1.

- [ ] **Step 4: Verify project builds**

```bash
cd "D:/MyProject/c#/auto_ffmpeg"
dotnet build auto_ffmpeg/auto_ffmpeg.csproj
```

Expected: Build succeeds with no errors.

- [ ] **Step 5: Manual test checklist**

Run the app and verify:
1. App starts, 3 tabs visible
2. Checkboxes appear in bottom area (not inside tabs)
3. Single tab: pick video, audio, output → click "Ghep" → progress bar shows % → file created
4. Single tab: drag video + audio files → fields auto-fill
5. Batch tab: pick folder → scan → click "Ghep tat ca" → progress bar fills per pair
6. Batch tab: drag folder → auto-scans
7. Drive tab: cookies checkbox toggles profile textbox enabled/disabled
8. Click "Huy" during merge → process stops, status shows "Da huy"
9. Buttons are disabled during processing, re-enabled after

- [ ] **Step 6: Commit**

```bash
git add auto_ffmpeg/Program.cs auto_ffmpeg/MainForm.cs
git commit -m "feat: rewrite MainForm with improved layout, drag & drop, cancel, real progress"
```

---

### Task 5: Final Cleanup & Verification

**Files:**
- Verify all files in place

- [ ] **Step 1: Verify final file structure**

```bash
ls auto_ffmpeg/*.cs
```

Expected:
```
auto_ffmpeg/DriveDownloader.cs
auto_ffmpeg/FfmpegService.cs
auto_ffmpeg/MainForm.cs
auto_ffmpeg/PairInfo.cs
auto_ffmpeg/Program.cs
```

No Form1.cs or Form1.Designer.cs.

- [ ] **Step 2: Clean build**

```bash
cd "D:/MyProject/c#/auto_ffmpeg"
dotnet clean auto_ffmpeg/auto_ffmpeg.csproj
dotnet build auto_ffmpeg/auto_ffmpeg.csproj
```

Expected: Build succeeded, 0 errors, 0 warnings (or only minor warnings).

- [ ] **Step 3: Run app and do final smoke test**

Launch `auto_ffmpeg/bin/Debug/net8.0-windows/auto_ffmpeg.exe` and run through all tabs.

- [ ] **Step 4: Commit any final fixes if needed**

```bash
git add -A auto_ffmpeg/
git commit -m "chore: final cleanup and verification"
```
