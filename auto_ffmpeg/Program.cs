namespace auto_ffmpeg
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using System.Windows.Forms;

	internal static class Program
	{
		[STAThread]
		static void Main()
		{
			ApplicationConfiguration.Initialize();
			Application.Run(new MainForm());
		}
	}

	public class MainForm : Form
	{
		// ====== Common ======
		CheckBox chkCopy = new() { Text = "Dùng stream copy (-c copy) • nhanh nhất", Checked = true, AutoSize = true };
		CheckBox chkFallback = new() { Text = "Tự động fallback AAC nếu copy lỗi", Checked = true, AutoSize = true };
		TextBox txtLog = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 760, Height = 180, ReadOnly = true };
		Label lblStatus = new() { AutoSize = true, Text = "Sẵn sàng." };
		ProgressBar progress = new() { Style = ProgressBarStyle.Blocks, Width = 760 };
		readonly StringBuilder _logBuf = new();
		System.Windows.Forms.Timer _logTimer;

		// ====== Single tab ======
		TextBox txtVideo = new() { ReadOnly = true, Width = 480 };
		TextBox txtAudio = new() { ReadOnly = true, Width = 480 };
		TextBox txtOutput = new() { ReadOnly = true, Width = 480 };
		Button btnPickVideo = new() { Text = "Chọn Video…" };
		Button btnPickAudio = new() { Text = "Chọn Audio…" };
		Button btnPickOutput = new() { Text = "Nơi lưu…" };
		Button btnRun = new() { Text = "Ghép (1 cặp)", Width = 140 };

		// ====== Batch tab ======
		TextBox txtFolder = new() { ReadOnly = true, Width = 480 };
		Button btnPickFolder = new() { Text = "Chọn Thư mục…" };
		Button btnScan = new() { Text = "Quét cặp trùng tên" };
		Button btnBatchMerge = new() { Text = "Ghép tất cả", Width = 140 };
		ListView lvPairs = new()
		{
			View = View.Details,
			CheckBoxes = true,
			Width = 760,
			Height = 220,
			FullRowSelect = true
		};
		readonly string[] VideoExts = new[] { ".mp4" };
		readonly string[] AudioExts = new[] { ".m4a", ".mp3", ".aac", ".wav", ".flac", ".opus", ".ogg" };

		// ====== Drive tab ======
		TextBox txtDriveUrl = new() { Width = 480 };
		Button btnDownloadDrive = new() { Text = "Tải từ Google Drive", Width = 180 };

		public MainForm()
		{
			Text = "Auto FFmpeg Muxer – ghép video + audio (Đẹp & Nhanh)";
			StartPosition = FormStartPosition.CenterScreen;
			Width = 840;
			Height = 700;
			Font = new System.Drawing.Font("Segoe UI", 9.5f);
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;

			// Tabs
			var tabs = new TabControl { Dock = DockStyle.Top, Height = 360 };
			var tabSingle = new TabPage("Ghép 1 cặp");
			var tabBatch = new TabPage("Ghép theo thư mục");
			var tabDrive = new TabPage("Tải từ Google Drive");
			tabs.TabPages.Add(tabSingle);
			tabs.TabPages.Add(tabBatch);
			tabs.TabPages.Add(tabDrive);

			// ====== Single UI ======
			var sTable = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 3,
				RowCount = 7,
				Padding = new Padding(12)
			};
			sTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			sTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			sTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

			sTable.Controls.Add(MakeLabel("File Video (.mp4):"), 0, 0);
			sTable.Controls.Add(txtVideo, 1, 0); sTable.Controls.Add(btnPickVideo, 2, 0);

			sTable.Controls.Add(MakeLabel("File Audio:"), 0, 1);
			sTable.Controls.Add(txtAudio, 1, 1); sTable.Controls.Add(btnPickAudio, 2, 1);

			sTable.Controls.Add(MakeLabel("File xuất (.mp4):"), 0, 2);
			sTable.Controls.Add(txtOutput, 1, 2); sTable.Controls.Add(btnPickOutput, 2, 2);

			var sFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
			sFlow.Controls.Add(chkCopy);
			sFlow.Controls.Add(chkFallback);
			sFlow.Controls.Add(new Label { AutoSize = true, Text = "  " });
			sFlow.Controls.Add(btnRun);
			sTable.SetColumnSpan(sFlow, 3);
			sTable.Controls.Add(sFlow, 0, 3);

			tabSingle.Controls.Add(sTable);

			// ====== Batch UI ======
			var bTable = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 3,
				RowCount = 6,
				Padding = new Padding(12)
			};
			bTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			bTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			bTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

			bTable.Controls.Add(MakeLabel("Thư mục nguồn:"), 0, 0);
			bTable.Controls.Add(txtFolder, 1, 0); bTable.Controls.Add(btnPickFolder, 2, 0);

			var bFlowTop = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
			bFlowTop.Controls.Add(chkCopy);
			bFlowTop.Controls.Add(chkFallback);
			bFlowTop.Controls.Add(new Label { AutoSize = true, Text = "  " });
			bFlowTop.Controls.Add(btnScan);
			bFlowTop.Controls.Add(btnBatchMerge);
			bTable.SetColumnSpan(bFlowTop, 3);
			bTable.Controls.Add(bFlowTop, 0, 1);

			lvPairs.Columns.Add("✓", 40);
			lvPairs.Columns.Add("Tên cơ sở (basename)", 280);
			lvPairs.Columns.Add("Video", 120);
			lvPairs.Columns.Add("Audio", 120);
			lvPairs.Columns.Add("Output", 200);
			lvPairs.Columns.Add("Trạng thái", 160);
			bTable.SetColumnSpan(lvPairs, 3);
			bTable.Controls.Add(lvPairs, 0, 2);
			tabBatch.Controls.Add(bTable);

			// ====== Drive UI ======
			var dTable = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 3,
				RowCount = 3,
				Padding = new Padding(12)
			};
			dTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			dTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			dTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

			dTable.Controls.Add(MakeLabel("Link Google Drive:"), 0, 0);
			dTable.Controls.Add(txtDriveUrl, 1, 0);
			dTable.Controls.Add(btnDownloadDrive, 2, 0);
			tabDrive.Controls.Add(dTable);

			// ====== Bottom shared ======
			var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
			bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));

			bottom.Controls.Add(new Label { Text = "Nhật ký:", AutoSize = true }, 0, 0);
			bottom.Controls.Add(txtLog, 0, 1);
			bottom.Controls.Add(progress, 0, 2);
			var statusFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
			statusFlow.Controls.Add(lblStatus);
			bottom.Controls.Add(statusFlow, 0, 3);

			var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			root.Controls.Add(tabs, 0, 0);
			root.Controls.Add(bottom, 0, 1);
			Controls.Add(root);

			// ====== Events ======
			btnPickVideo.Click += (_, __) => PickFile(txtVideo, "Chọn Video", "MP4 Video|*.mp4|Tất cả|*.*");
			btnPickAudio.Click += (_, __) => PickFile(txtAudio, "Chọn Audio", "Âm thanh|*.m4a;*.mp3;*.aac;*.wav;*.flac;*.opus;*.ogg|Tất cả|*.*");
			btnPickOutput.Click += (_, __) => PickSaveFile(txtOutput, "Nơi lưu", "MP4|*.mp4");
			btnRun.Click += async (_, __) => await RunSingleAsync();
			btnPickFolder.Click += (_, __) => PickFolder();
			btnScan.Click += (_, __) => ScanPairs();
			btnBatchMerge.Click += async (_, __) => await RunBatchAsync();
			btnDownloadDrive.Click += async (_, __) =>
			{
				string url = txtDriveUrl.Text.Trim();
				if (string.IsNullOrEmpty(url))
				{
					MessageBox.Show(this, "Hãy nhập link Google Drive trước.", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				await DownloadFromDriveAsync(url);
			};

			_logTimer = new System.Windows.Forms.Timer { Interval = 150 };
			_logTimer.Tick += (_, __) =>
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

		Label MakeLabel(string text) => new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

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
			using var fbd = new FolderBrowserDialog { Description = "Chọn thư mục chứa .mp4 và audio cùng tên" };
			if (fbd.ShowDialog(this) == DialogResult.OK)
				txtFolder.Text = fbd.SelectedPath;
		}

		// ====== DOWNLOAD DRIVE ======
		async Task DownloadFromDriveAsync(string url)
		{
			txtLog.Clear();
			progress.Style = ProgressBarStyle.Marquee;
			lblStatus.Text = "Đang tải video từ Google Drive…";

			var psi = new ProcessStartInfo
			{
				FileName = "yt-dlp.exe",
				Arguments =
	$"--cookies-from-browser \"chrome:profile=Profile 1:all\" " +
	$"\"{url.Replace("/view", "/uc?export=download")}\" " +
	"-f \"136+140/best\" --merge-output-format mp4 " +
	"-o \"%(title)s_merged.%(ext)s\"",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};

			var proc = new Process { StartInfo = psi };
			proc.OutputDataReceived += (_, e) => { if (e.Data != null) _logBuf.AppendLine(e.Data); };
			proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _logBuf.AppendLine(e.Data); };

			proc.Start();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();
			await Task.Run(() => proc.WaitForExit());
			progress.Style = ProgressBarStyle.Blocks;

			if (proc.ExitCode == 0)
				lblStatus.Text = "Tải hoàn tất! File đã được ghép.";
			else
				lblStatus.Text = $"Lỗi khi tải (mã {proc.ExitCode}).";

			txtLog.AppendText(_logBuf.ToString());
		}

		// ====== Single run ======
		async Task RunSingleAsync()
		{
			txtLog.Clear();
			progress.Style = ProgressBarStyle.Marquee;
			lblStatus.Text = "Đang ghép 1 cặp…";

			var video = txtVideo.Text.Trim();
			var audio = txtAudio.Text.Trim();
			var output = txtOutput.Text.Trim();
			if (!File.Exists(video)) { MessageBox.Show(this, "Chưa chọn hoặc không tìm thấy video .mp4", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning); progress.Style = ProgressBarStyle.Blocks; return; }
			if (!File.Exists(audio)) { MessageBox.Show(this, "Chưa chọn hoặc không tìm thấy file audio", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning); progress.Style = ProgressBarStyle.Blocks; return; }
			if (string.IsNullOrWhiteSpace(output)) { MessageBox.Show(this, "Chưa chọn nơi lưu file xuất .mp4", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning); progress.Style = ProgressBarStyle.Blocks; return; }

			var ok = await MergeOneAsync(video, audio, output);
			progress.Style = ProgressBarStyle.Blocks;
			lblStatus.Text = ok ? "Hoàn tất!" : "Có lỗi khi ghép.";
			if (ok)
			{
				try { Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
			}
		}

		// ====== Batch scan ======
		void ScanPairs()
		{
			lvPairs.Items.Clear();
			var dir = txtFolder.Text.Trim();
			if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
			{
				MessageBox.Show(this, "Chưa chọn thư mục hợp lệ.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			var files = Directory.EnumerateFiles(dir)
				.Where(p => VideoExts.Contains(Path.GetExtension(p).ToLower()) || AudioExts.Contains(Path.GetExtension(p).ToLower()))
				.ToList();

			// group by basename
			var byBase = files.GroupBy(p => Path.GetFileNameWithoutExtension(p));
			int count = 0;
			foreach (var grp in byBase)
			{
				var vids = grp.Where(p => VideoExts.Contains(Path.GetExtension(p).ToLower())).ToList();
				var auds = grp.Where(p => AudioExts.Contains(Path.GetExtension(p).ToLower())).ToList();
				if (vids.Count == 0 || auds.Count == 0) continue;

				// ưu tiên .m4a, sau đó mp3, aac...
				var audioPick = auds.OrderBy(p =>
				{
					var ext = Path.GetExtension(p).ToLower();
					return ext switch
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
				}).First();

				// video: chỉ mp4
				var videoPick = vids.First(); // đã lọc .mp4

				var output = Path.Combine(dir, grp.Key + "_merged.mp4");

				var item = new ListViewItem() { Checked = true }; // mặc định chọn
				item.SubItems.Add(grp.Key);
				item.SubItems.Add(Path.GetExtension(videoPick).ToLower().TrimStart('.'));
				item.SubItems.Add(Path.GetExtension(audioPick).ToLower().TrimStart('.'));
				item.SubItems.Add(Path.GetFileName(output));
				item.SubItems.Add("Đang chờ");
				item.Tag = new PairInfo { Video = videoPick, Audio = audioPick, Output = output, Basename = grp.Key };
				lvPairs.Items.Add(item);
				count++;
			}

			lblStatus.Text = count > 0 ? $"Đã tìm thấy {count} cặp trùng tên." : "Không tìm thấy cặp nào.";
		}

		// ====== Batch run ======
		async Task RunBatchAsync()
		{
			if (lvPairs.Items.Count == 0)
			{
				MessageBox.Show(this, "Chưa có cặp nào để ghép. Hãy bấm 'Quét cặp trùng tên' trước.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			var selected = lvPairs.Items.Cast<ListViewItem>().Where(it => it.Checked).ToList();
			if (selected.Count == 0)
			{
				MessageBox.Show(this, "Bạn chưa tick chọn cặp nào.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			txtLog.Clear();
			progress.Style = ProgressBarStyle.Blocks;
			progress.Minimum = 0;
			progress.Maximum = selected.Count;
			progress.Value = 0;

			lblStatus.Text = $"Đang ghép {selected.Count} cặp…";

			foreach (var item in selected)
			{
				var info = (PairInfo)item.Tag;
				item.SubItems[5].Text = "Đang xử lý…";

				bool ok = await MergeOneAsync(info.Video!, info.Audio!, info.Output!);
				item.SubItems[5].Text = ok ? "✓ Thành công" : "✗ Lỗi";

				progress.Value = Math.Min(progress.Value + 1, progress.Maximum);
			}

			lblStatus.Text = "Hoàn tất batch.";
		}

		// ====== Core merge ======
		async Task<bool> MergeOneAsync(string video, string audio, string output)
		{
			string? ffmpegPath = ResolveFfmpegPath();
			if (ffmpegPath is null)
			{
				MessageBox.Show(this,
					"Không tìm thấy ffmpeg.\n- Đặt ffmpeg.exe cùng thư mục .exe của ứng dụng, hoặc\n- Thêm ffmpeg vào PATH.",
					"Thiếu ffmpeg", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return false;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(output)!);

			// Try with -c copy (nếu user bật), rồi fallback sang AAC nếu thất bại (nếu bật).
			if (chkCopy.Checked)
			{
				var argsCopy = BuildArgs(video, audio, output, useCopy: true);
				var resCopy = await RunProcessAsync(ffmpegPath, argsCopy);
				if (resCopy.exitCode == 0) return true;

				_logBuf.AppendLine($"[WARN] -c copy thất bại (mã {resCopy.exitCode}).");
				if (!chkFallback.Checked) return false;
			}

			// Fallback AAC
			var argsAac = BuildArgs(video, audio, output, useCopy: false);
			var resAac = await RunProcessAsync(ffmpegPath, argsAac);
			return resAac.exitCode == 0;
		}

		string BuildArgs(string video, string audio, string output, bool useCopy)
		{
			// -shortest: dừng theo track ngắn hơn; -movflags +faststart tối ưu MP4 cho web.
			if (useCopy)
			{
				return $"-hide_banner -loglevel warning " +
					   $"-i \"{video}\" -i \"{audio}\" " +
					   $"-c copy -shortest -movflags +faststart -y \"{output}\"";
			}
			else
			{
				return $"-hide_banner -loglevel warning " +
					   $"-i \"{video}\" -i \"{audio}\" " +
					   $"-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k " +
					   $"-shortest -movflags +faststart -y \"{output}\"";
			}
		}

		static string? ResolveFfmpegPath()
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

		async Task<(int exitCode, string std)> RunProcessAsync(string fileName, string arguments)
		{
			var psi = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				CreateNoWindow = true
			};
			using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

			p.OutputDataReceived += (_, e) => { if (e.Data != null) _logBuf.AppendLine(e.Data); };
			p.ErrorDataReceived += (_, e) => { if (e.Data != null) _logBuf.AppendLine(e.Data); };

			p.Start();
			p.BeginOutputReadLine();
			p.BeginErrorReadLine();

			await Task.Run(() => p.WaitForExit());
			return (p.ExitCode, "");
		}

		class PairInfo
		{
			public string? Basename { get; set; }
			public string? Video { get; set; }
			public string? Audio { get; set; }
			public string? Output { get; set; }
		}
	}

}