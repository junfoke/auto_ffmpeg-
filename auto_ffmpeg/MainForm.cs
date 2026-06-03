namespace auto_ffmpeg;

using System.Drawing;
using System.Text;

public class MainForm : Form
{
    // ====== Theme ======
    static readonly Color BgColor      = Color.FromArgb(245, 246, 248);
    static readonly Color PanelColor   = Color.White;
    static readonly Color TextColor    = Color.FromArgb(31, 35, 40);
    static readonly Color MutedColor   = Color.FromArgb(107, 114, 128);
    static readonly Color AccentColor  = Color.FromArgb(37, 99, 235);
    static readonly Color AccentHover  = Color.FromArgb(29, 78, 216);
    static readonly Color BorderColor  = Color.FromArgb(229, 231, 235);
    static readonly Color DangerColor  = Color.FromArgb(220, 38, 38);
    static readonly Color LogBg        = Color.FromArgb(24, 26, 30);
    static readonly Color LogFg        = Color.FromArgb(220, 220, 220);
    static readonly Color ReadyColor   = Color.FromArgb(34, 197, 94);   // green
    static readonly Color BusyColor    = Color.FromArgb(245, 158, 11);  // amber

    // status column index in lvPairs (kept as a const so adding/removing columns is safe)
    const int ColStatus = 5;

    // ====== Common (bottom area) ======
    readonly CheckBox chkCopy     = new() { Text = "Dung stream copy (-c copy) - nhanh nhat", Checked = true, AutoSize = true };
    readonly CheckBox chkFallback = new() { Text = "Tu dong fallback AAC neu copy loi",        Checked = true, AutoSize = true };
    readonly TextBox  txtLog      = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle };
    readonly Label    lblStatus   = new() { AutoSize = true, Text = "San sang." };
    readonly Panel    statusDot   = new() { Width = 10, Height = 10, BackColor = ReadyColor };
    readonly ProgressBar progress = new() { Style = ProgressBarStyle.Blocks, Minimum = 0, Maximum = 100, Height = 18 };
    readonly Button   btnCancel   = new() { Text = "Huy", Width = 80, Height = 32, Visible = false };
    readonly StringBuilder _logBuf = new();
    System.Windows.Forms.Timer _logTimer = null!;
    CancellationTokenSource? _cts;
    bool _hadError;

    // ====== Single tab ======
    readonly TextBox txtVideo     = new() { ReadOnly = true };
    readonly TextBox txtAudio     = new() { ReadOnly = true };
    readonly TextBox txtOutput    = new() { ReadOnly = true };
    readonly Button  btnPickVideo  = new() { Text = "Video..." };
    readonly Button  btnPickAudio  = new() { Text = "Audio..." };
    readonly Button  btnPickOutput = new() { Text = "Noi luu..." };
    readonly Button  btnRun        = new() { Text = "GHEP 1 CAP", Width = 180, Height = 38 };

    // ====== Batch tab ======
    readonly TextBox txtFolder      = new() { ReadOnly = true };
    readonly Button  btnPickFolder  = new() { Text = "Thu muc..." };
    readonly Button  btnScan        = new() { Text = "Quet cap trung ten", Width = 180, Height = 34 };
    readonly Button  btnBatchMerge  = new() { Text = "GHEP TAT CA", Width = 180, Height = 38 };
    readonly ListView lvPairs = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = false,
        BorderStyle = BorderStyle.FixedSingle
    };

    // ====== Drive tab ======
    readonly TextBox txtDriveUrl       = new();
    readonly Button  btnDownloadDrive  = new() { Text = "TAI VE", Width = 180, Height = 38 };
    readonly CheckBox chkUseCookies    = new() { Text = "Dung cookies tu Chrome", AutoSize = true };
    readonly TextBox txtChromeProfile  = new() { Text = "Default", Width = 140, Enabled = false };
    readonly TextBox txtCookiesFile    = new() { ReadOnly = true };
    readonly Button  btnPickCookies    = new() { Text = "cookies.txt..." };
    readonly TextBox txtDriveOutDir    = new() { ReadOnly = true, Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
    readonly Button  btnPickDriveOutDir = new() { Text = "Thu muc..." };

    Button[] ActionButtons => [btnRun, btnBatchMerge, btnDownloadDrive, btnScan,
                                btnPickVideo, btnPickAudio, btnPickOutput, btnPickFolder, btnPickCookies, btnPickDriveOutDir];

    public MainForm()
    {
        Text = "Auto FFmpeg Muxer";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 940;
        Height = 780;
        Font = new Font("Segoe UI", 10f);
        BackColor = BgColor;
        ForeColor = TextColor;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        // Style primary action buttons
        StylePrimary(btnRun);
        StylePrimary(btnBatchMerge);
        StylePrimary(btnDownloadDrive);
        StyleSecondary(btnScan);
        StyleSecondary(btnPickVideo);
        StyleSecondary(btnPickAudio);
        StyleSecondary(btnPickOutput);
        StyleSecondary(btnPickFolder);
        StyleSecondary(btnPickCookies);
        StyleSecondary(btnPickDriveOutDir);
        StyleDanger(btnCancel);

        // Log styling
        txtLog.BackColor = LogBg;
        txtLog.ForeColor = LogFg;
        txtLog.Font = new Font("Consolas", 9f);

        // ====== Header ======
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = PanelColor };
        var headerTitle = new Label
        {
            Text = "Auto FFmpeg Muxer",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(20, 14)
        };
        var headerSub = new Label
        {
            Text = "Ghep video + audio  -  Tai video tu Google Drive",
            Font = new Font("Segoe UI", 9f),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(22, 50)
        };
        header.Controls.Add(headerTitle);
        header.Controls.Add(headerSub);
        var headerBorder = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = BorderColor };
        header.Controls.Add(headerBorder);

        // ====== Tabs ======
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 8),
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(190, 34)
        };
        var tabSingle = new TabPage("Ghep 1 cap") { AllowDrop = true, BackColor = BgColor, Padding = new Padding(14) };
        var tabBatch  = new TabPage("Ghep theo thu muc") { AllowDrop = true, BackColor = BgColor, Padding = new Padding(14) };
        var tabDrive  = new TabPage("Tai tu Google Drive") { BackColor = BgColor, Padding = new Padding(14) };
        tabs.TabPages.Add(tabSingle);
        tabs.TabPages.Add(tabBatch);
        tabs.TabPages.Add(tabDrive);

        // ====== Single tab content ======
        tabSingle.Controls.Add(BuildCard(BuildSingleCard()));

        // ====== Batch tab content ======
        tabBatch.Controls.Add(BuildCard(BuildBatchCard()));

        // ====== Drive tab content ======
        tabDrive.Controls.Add(BuildCard(BuildDriveCard()));

        // ====== Bottom shared area ======
        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = BgColor, Padding = new Padding(14, 6, 14, 14) };

        var bottomCard = new Panel { Dock = DockStyle.Fill, BackColor = PanelColor, Padding = new Padding(16) };
        bottomCard.Paint += (s, e) => DrawCardBorder((Panel)s!, e);

        var bottomTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = PanelColor
        };
        bottomTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottomTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var optionsFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        optionsFlow.Controls.Add(chkCopy);
        optionsFlow.Controls.Add(new Label { Width = 16 });
        optionsFlow.Controls.Add(chkFallback);
        bottomTable.Controls.Add(optionsFlow, 0, 0);

        bottomTable.Controls.Add(MakeSectionLabel("Nhat ky"), 0, 1);

        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 6) };
        txtLog.Dock = DockStyle.Fill;
        logHost.Controls.Add(txtLog);
        bottomTable.Controls.Add(logHost, 0, 2);

        var progressRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Height = 36, BackColor = PanelColor
        };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progress.Dock = DockStyle.Fill;
        progress.Margin = new Padding(0, 9, 8, 0);
        progressRow.Controls.Add(progress, 0, 0);
        progressRow.Controls.Add(btnCancel, 1, 0);
        bottomTable.Controls.Add(progressRow, 0, 3);

        var statusFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        statusDot.Margin = new Padding(0, 6, 8, 0);
        lblStatus.Font = new Font("Segoe UI", 9.5f);
        lblStatus.ForeColor = MutedColor;
        statusFlow.Controls.Add(statusDot);
        statusFlow.Controls.Add(lblStatus);
        bottomTable.Controls.Add(statusFlow, 0, 4);

        bottomCard.Controls.Add(bottomTable);
        bottom.Controls.Add(bottomCard);

        // ====== Root layout ======
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = BgColor };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 340));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Dock = DockStyle.Fill;
        bottom.Dock = DockStyle.Fill;
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);

        WireEvents(tabSingle, tabBatch);
        StartLogTimer();
    }

    // ====== UI builders ======

    Panel BuildSingleCard()
    {
        var grid = NewFormGrid(4);
        AddRow(grid, "File video",  txtVideo,  btnPickVideo);
        AddRow(grid, "File audio",  txtAudio,  btnPickAudio);
        AddRow(grid, "File xuat",   txtOutput, btnPickOutput);

        var actionFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 14, 0, 0), BackColor = PanelColor };
        actionFlow.Controls.Add(btnRun);
        grid.SetColumnSpan(actionFlow, 3);
        grid.Controls.Add(actionFlow, 0, 3);
        grid.RowStyles[3] = new RowStyle(SizeType.Absolute, 60);

        return grid;
    }

    Panel BuildBatchCard()
    {
        var grid = NewFormGrid(3);
        AddRow(grid, "Thu muc nguon", txtFolder, btnPickFolder);

        var actionFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 4, 0, 8) };
        actionFlow.Controls.Add(btnScan);
        actionFlow.Controls.Add(new Label { Width = 8 });
        actionFlow.Controls.Add(btnBatchMerge);
        grid.SetColumnSpan(actionFlow, 3);
        grid.Controls.Add(actionFlow, 0, 1);

        lvPairs.Columns.Clear();
        lvPairs.Columns.Add("",                  28);
        lvPairs.Columns.Add("Ten co so",        260);
        lvPairs.Columns.Add("Video",             80);
        lvPairs.Columns.Add("Audio",             80);
        lvPairs.Columns.Add("Output",           200);
        lvPairs.Columns.Add("Trang thai",       140);
        lvPairs.Dock = DockStyle.Fill;
        var listHost = new Panel { Dock = DockStyle.Fill, Height = 150, Padding = new Padding(0, 4, 0, 0) };
        listHost.Controls.Add(lvPairs);
        grid.SetColumnSpan(listHost, 3);
        grid.Controls.Add(listHost, 0, 2);
        grid.RowStyles[2] = new RowStyle(SizeType.Percent, 100);

        return grid;
    }

    Panel BuildDriveCard()
    {
        var grid = NewFormGrid(5);
        AddRow(grid, "Link Drive", txtDriveUrl, null, row: 0);

        // Cookies row
        var cookieFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, BackColor = PanelColor };
        cookieFlow.Controls.Add(chkUseCookies);
        cookieFlow.Controls.Add(new Label { Text = "  Profile:", AutoSize = true, ForeColor = MutedColor, Padding = new Padding(0, 6, 4, 0) });
        cookieFlow.Controls.Add(txtChromeProfile);
        grid.Controls.Add(MakeLabel("Cookies Chrome"), 0, 1);
        grid.SetColumnSpan(cookieFlow, 2);
        grid.Controls.Add(cookieFlow, 1, 1);

        // cookies.txt
        var fileFlow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = PanelColor };
        fileFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        StyleInput(txtCookiesFile);
        txtCookiesFile.Dock = DockStyle.Fill;
        txtCookiesFile.Margin = new Padding(0, 0, 8, 0);
        fileFlow.Controls.Add(txtCookiesFile, 0, 0);
        fileFlow.Controls.Add(btnPickCookies, 1, 0);
        grid.Controls.Add(MakeLabel("Hoac file cookies"), 0, 2);
        grid.SetColumnSpan(fileFlow, 2);
        grid.Controls.Add(fileFlow, 1, 2);

        // output dir
        AddRow(grid, "Thu muc luu", txtDriveOutDir, btnPickDriveOutDir, row: 3);

        // action row
        var actionFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 14, 0, 0), BackColor = PanelColor };
        actionFlow.Controls.Add(btnDownloadDrive);
        grid.SetColumnSpan(actionFlow, 3);
        grid.Controls.Add(actionFlow, 0, 4);
        grid.RowStyles[4] = new RowStyle(SizeType.Absolute, 60);

        return grid;
    }

    TableLayoutPanel NewFormGrid(int rows)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = rows,
            BackColor = PanelColor,
            Padding = new Padding(0)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < rows; i++)
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    int _autoRow;
    void AddRow(TableLayoutPanel grid, string label, TextBox input, Button? btn, int? row = null)
    {
        int r = row ?? _autoRow++;
        StyleInput(input);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 8, 8);
        grid.Controls.Add(MakeLabel(label), 0, r);
        grid.Controls.Add(input, 1, r);
        if (btn != null) grid.Controls.Add(btn, 2, r);
    }

    Panel BuildCard(Panel inner)
    {
        // resets the row counter for the next card built afterwards
        _autoRow = 0;
        var card = new Panel { Dock = DockStyle.Fill, BackColor = PanelColor, Padding = new Padding(18) };
        card.Paint += (s, e) => DrawCardBorder((Panel)s!, e);
        inner.Dock = DockStyle.Fill;
        card.Controls.Add(inner);
        return card;
    }

    static void DrawCardBorder(Panel p, PaintEventArgs e)
    {
        using var pen = new Pen(BorderColor);
        var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 120,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = MutedColor,
        Font = new Font("Segoe UI", 9.5f),
        Margin = new Padding(0, 6, 8, 8)
    };

    static Label MakeSectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = MutedColor,
        Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
        Margin = new Padding(0, 4, 0, 4)
    };

    static void StyleInput(TextBox tb)
    {
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.BackColor = Color.White;
        tb.Font = new Font("Segoe UI", 9.5f);
    }

    static void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.BackColor = AccentColor;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        if (b.Height < 34) b.Height = 38;
    }

    static void StyleSecondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = BorderColor;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 238, 242);
        b.BackColor = Color.White;
        b.ForeColor = TextColor;
        b.Font = new Font("Segoe UI", 9.5f);
        b.Cursor = Cursors.Hand;
        b.Height = 30;
        if (b.Width < 110) b.Width = 120;
    }

    static void StyleDanger(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 28, 28);
        b.BackColor = DangerColor;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
    }

    void StartLogTimer()
    {
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

    void WireEvents(TabPage tabSingle, TabPage tabBatch)
    {
        btnPickVideo.Click   += (_, _) => PickVideoSingle();
        btnPickAudio.Click   += (_, _) => PickFile(txtAudio, "Chon Audio", "Am thanh|*.m4a;*.mp3;*.aac;*.wav;*.flac;*.opus;*.ogg|Tat ca|*.*");
        btnPickOutput.Click  += (_, _) => PickSaveFile(txtOutput, "Noi luu", "MP4|*.mp4");
        btnRun.Click         += async (_, _) => await RunSingleAsync();
        btnPickFolder.Click  += (_, _) => PickFolder();
        btnScan.Click        += (_, _) => ScanPairs();
        btnBatchMerge.Click  += async (_, _) => await RunBatchAsync();
        btnDownloadDrive.Click += async (_, _) => await RunDriveDownloadAsync();
        btnCancel.Click      += (_, _) => _cts?.Cancel();
        chkUseCookies.CheckedChanged += (_, _) => txtChromeProfile.Enabled = chkUseCookies.Checked;
        btnPickCookies.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Title = "Chon file cookies", Filter = "Text|*.txt|Tat ca|*.*" };
            if (ofd.ShowDialog(this) == DialogResult.OK) txtCookiesFile.Text = ofd.FileName;
        };
        btnPickDriveOutDir.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Chon thu muc luu video tai tu Drive" };
            if (fbd.ShowDialog(this) == DialogResult.OK) txtDriveOutDir.Text = fbd.SelectedPath;
        };

        tabSingle.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        tabSingle.DragDrop  += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLower();
                if (FfmpegService.IsVideoExt(ext))
                {
                    txtVideo.Text = f;
                    SuggestOutputFor(f);
                }
                else if (FfmpegService.IsAudioExt(ext)) txtAudio.Text = f;
            }
        };

        tabBatch.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        tabBatch.DragDrop  += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            var folder = paths.FirstOrDefault(p => Directory.Exists(p));
            if (folder != null) { txtFolder.Text = folder; ScanPairs(); }
        };
    }

    // ====== Helpers ======

    void PickFile(TextBox target, string title, string filter)
    {
        using var ofd = new OpenFileDialog { Title = title, Filter = filter };
        if (ofd.ShowDialog(this) == DialogResult.OK) target.Text = ofd.FileName;
    }

    void PickVideoSingle()
    {
        using var ofd = new OpenFileDialog { Title = "Chon Video", Filter = "MP4 Video|*.mp4|Tat ca|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        txtVideo.Text = ofd.FileName;
        SuggestOutputFor(ofd.FileName);
    }

    // fills the output path with "<video>_merged.mp4" when it is still empty
    void SuggestOutputFor(string videoPath)
    {
        if (!string.IsNullOrEmpty(txtOutput.Text)) return;
        var dir = Path.GetDirectoryName(videoPath)!;
        var name = Path.GetFileNameWithoutExtension(videoPath);
        txtOutput.Text = Path.Combine(dir, name + "_merged.mp4");
    }

    void PickSaveFile(TextBox target, string title, string filter)
    {
        using var sfd = new SaveFileDialog { Title = title, Filter = filter, DefaultExt = "mp4" };
        if (sfd.ShowDialog(this) == DialogResult.OK) target.Text = sfd.FileName;
    }

    void PickFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Chon thu muc chua .mp4 va audio cung ten" };
        if (fbd.ShowDialog(this) == DialogResult.OK) txtFolder.Text = fbd.SelectedPath;
    }

    void SetProcessingState(bool isProcessing)
    {
        foreach (var btn in ActionButtons) btn.Enabled = !isProcessing;
        btnCancel.Visible = isProcessing;
        if (isProcessing)
        {
            _hadError = false;
            statusDot.BackColor = BusyColor;
            _cts = new CancellationTokenSource();
            progress.Value = 0;
        }
        else
        {
            // keep the red dot if the operation ended in error
            statusDot.BackColor = _hadError ? DangerColor : ReadyColor;
            _cts?.Dispose();
            _cts = null;
        }
    }

    void UpdateProgress(int percent)
    {
        if (InvokeRequired) Invoke(() => progress.Value = Math.Clamp(percent, 0, 100));
        else progress.Value = Math.Clamp(percent, 0, 100);
    }

    // first real % from yt-dlp flips the bar from marquee to a determinate value
    void OnDriveProgress(int percent)
    {
        if (InvokeRequired) { Invoke(() => OnDriveProgress(percent)); return; }
        if (progress.Style != ProgressBarStyle.Blocks) progress.Style = ProgressBarStyle.Blocks;
        progress.Value = Math.Clamp(percent, 0, 100);
    }

    void AppendLog(string line) => _logBuf.AppendLine(line);

    // clears both the visible log and the pending buffer so a stale flush
    // from a previous run can't leak into the next one
    void ClearLog()
    {
        _logBuf.Clear();
        txtLog.Clear();
    }

    void SetStatus(string text, bool error = false)
    {
        lblStatus.Text = text;
        lblStatus.ForeColor = error ? DangerColor : MutedColor;
        _hadError = error;
        if (error) statusDot.BackColor = DangerColor;
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

        ClearLog();
        SetProcessingState(true);
        SetStatus("Dang ghep 1 cap...");

        try
        {
            var ok = await FfmpegService.MergeAsync(video, audio, output,
                chkCopy.Checked, chkFallback.Checked, _cts!.Token,
                onProgress: UpdateProgress, onLog: AppendLog);

            progress.Value = 100;
            SetStatus(ok ? "Hoan tat!" : "Co loi khi ghep.", error: !ok);
            if (ok)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Da huy.");
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

        SetStatus(count > 0 ? $"Da tim thay {count} cap trung ten." : "Khong tim thay cap nao.");
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

        ClearLog();
        SetProcessingState(true);
        SetStatus($"Dang ghep {selected.Count} cap...");
        int totalPairs = selected.Count;
        int completedPairs = 0;

        try
        {
            foreach (var item in selected)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                var info = (PairInfo)item.Tag!;
                SetStatus($"Dang ghep {completedPairs + 1}/{totalPairs}: {info.Basename}");
                item.SubItems[ColStatus].Text = "Dang xu ly...";

                int cp = completedPairs;
                bool ok = await FfmpegService.MergeAsync(
                    info.Video!, info.Audio!, info.Output!,
                    chkCopy.Checked, chkFallback.Checked, _cts!.Token,
                    onProgress: pct => UpdateProgress((cp * 100 + pct) / totalPairs),
                    onLog: AppendLog);

                item.SubItems[ColStatus].Text = ok ? "OK" : "Loi";
                completedPairs++;
                UpdateProgress(completedPairs * 100 / totalPairs);
            }

            SetStatus("Hoan tat batch.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Da huy batch.");
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

        string outDir = txtDriveOutDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir))
        {
            MessageBox.Show(this, "Chua chon thu muc luu hop le.", "Thieu du lieu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (DriveDownloader.ResolveYtDlpPath() is null)
        {
            MessageBox.Show(this, "Khong tim thay yt-dlp.\n- Dat yt-dlp.exe cung thu muc .exe cua ung dung, hoac\n- Them yt-dlp vao PATH.", "Thieu yt-dlp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ClearLog();
        SetProcessingState(true);
        progress.Style = ProgressBarStyle.Marquee;
        SetStatus("Dang tai video tu Google Drive...");

        try
        {
            var ok = await DriveDownloader.DownloadAsync(
                url, chkUseCookies.Checked, txtChromeProfile.Text.Trim(),
                txtCookiesFile.Text.Trim(),
                outDir,
                _cts!.Token, onLog: AppendLog, onProgress: OnDriveProgress);

            SetStatus(ok ? "Tai hoan tat!" : "Loi khi tai.", error: !ok);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Da huy tai.");
            AppendLog("[INFO] Nguoi dung da huy.");
        }
        finally
        {
            progress.Style = ProgressBarStyle.Blocks;
            SetProcessingState(false);
        }
    }
}
