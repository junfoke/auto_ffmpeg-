namespace auto_ffmpeg
{
	using System;
	using System.Diagnostics;
	using System.IO;
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
		TextBox txtVideo = new() { ReadOnly = true, Width = 360 };
		TextBox txtAudio = new() { ReadOnly = true, Width = 360 };
		TextBox txtOutput = new() { ReadOnly = true, Width = 360 };
		TextBox txtLog = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 520, Height = 180 };
		Button btnPickVideo = new() { Text = "Chọn Video..." };
		Button btnPickAudio = new() { Text = "Chọn Audio..." };
		Button btnPickOutput = new() { Text = "Nơi lưu..." };
		Button btnRun = new() { Text = "Ghép (Mux)", Width = 120 };
		Label lblStatus = new() { AutoSize = true, Text = "Sẵn sàng." };

		public MainForm()
		{
			Text = "FFmpeg Muxer (ghép video + audio) – đơn giản";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			StartPosition = FormStartPosition.CenterScreen;
			Width = 600;
			Height = 450;

			var table = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 3,
				RowCount = 6,
				Padding = new Padding(10),
				AutoSize = true
			};
			table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

			// Hàng Video
			table.Controls.Add(new Label { Text = "File Video:", AutoSize = true }, 0, 0);
			table.Controls.Add(txtVideo, 1, 0);
			table.Controls.Add(btnPickVideo, 2, 0);

			// Hàng Audio
			table.Controls.Add(new Label { Text = "File Audio:", AutoSize = true }, 0, 1);
			table.Controls.Add(txtAudio, 1, 1);
			table.Controls.Add(btnPickAudio, 2, 1);

			// Hàng Output
			table.Controls.Add(new Label { Text = "File xuất (.mp4):", AutoSize = true }, 0, 2);
			table.Controls.Add(txtOutput, 1, 2);
			table.Controls.Add(btnPickOutput, 2, 2);

			// Log
			table.Controls.Add(new Label { Text = "Nhật ký:", AutoSize = true }, 0, 3);
			table.SetColumnSpan(txtLog, 3);
			table.Controls.Add(txtLog, 0, 4);

			// Nút chạy + status
			var panelBottom = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
			panelBottom.Controls.Add(btnRun);
			panelBottom.Controls.Add(lblStatus);
			table.SetColumnSpan(panelBottom, 3);
			table.Controls.Add(panelBottom, 0, 5);

			Controls.Add(table);

			// Sự kiện
			btnPickVideo.Click += (_, __) => PickFile(txtVideo, "Chọn video", "Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v|Tất cả|*.*");
			btnPickAudio.Click += (_, __) => PickFile(txtAudio, "Chọn audio", "Audio|*.mp3;*.aac;*.m4a;*.wav;*.flac;*.opus;*.ogg|Tất cả|*.*");
			btnPickOutput.Click += (_, __) => PickSaveFile(txtOutput, "Nơi lưu video đã ghép", "MP4|*.mp4");
			btnRun.Click += async (_, __) => await RunMuxAsync();
		}

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

		async Task RunMuxAsync()
		{
			txtLog.Clear();

			var video = txtVideo.Text.Trim();
			var audio = txtAudio.Text.Trim();
			var output = txtOutput.Text.Trim();

			if (!File.Exists(video))
			{
				MessageBox.Show(this, "Chưa chọn hoặc không tìm thấy file Video.", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
			if (!File.Exists(audio))
			{
				MessageBox.Show(this, "Chưa chọn hoặc không tìm thấy file Audio.", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
			if (string.IsNullOrWhiteSpace(output))
			{
				MessageBox.Show(this, "Chưa chọn nơi lưu file xuất (.mp4).", "Thiếu dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			string? ffmpegPath = ResolveFfmpegPath();
			if (ffmpegPath is null)
			{
				MessageBox.Show(this,
					"Không tìm thấy ffmpeg.\n\nCách khắc phục:\n- Đặt ffmpeg.exe cùng thư mục .exe của ứng dụng, hoặc\n- Thêm ffmpeg vào PATH (cài bản full từ gyan.dev/BtbN, v.v.).",
					"Thiếu ffmpeg", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}

			// Lệnh: lấy video stream từ file video, audio stream từ file audio.
			// Giữ nguyên video (-c:v copy), audio mã hoá ra AAC cho tương thích, cắt ngắn theo file ngắn hơn (-shortest).
			string args = $"-i \"{video}\" -i \"{audio}\" -c copy -y \"{output}\"";
			btnRun.Enabled = false;
			lblStatus.Text = "Đang ghép...";
			try
			{
				await RunProcessAsync(ffmpegPath, args, AppendLog);
				lblStatus.Text = "Hoàn tất!";
				AppendLog("\r\n==> Xong. Mở thư mục chứa file xuất...");
				try
				{
					Process.Start("explorer.exe", $"/select,\"{output}\"");
				}
				catch { /* ignore */ }
			}
			catch (Exception ex)
			{
				lblStatus.Text = "Lỗi!";
				AppendLog("\r\n[ERROR] " + ex.Message);
				MessageBox.Show(this, ex.Message, "Lỗi khi chạy ffmpeg", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			finally
			{
				btnRun.Enabled = true;
			}
		}

		static string? ResolveFfmpegPath()
		{
			// 1) ffmpeg.exe cùng thư mục ứng dụng
			string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
			if (File.Exists(local)) return local;

			// 2) Trong PATH
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
			catch { /* ignore */ }

			return null;
		}

		async Task RunProcessAsync(string fileName, string arguments, Action<string> onOutput)
		{
			var psi = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardError = true,   // ffmpeg ghi tiến trình ở stderr
				RedirectStandardOutput = true,
				CreateNoWindow = true
			};

			using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

			var stderrSb = new StringBuilder();
			p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\r\n"); };
			p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\r\n"); };

			p.Start();
			p.BeginOutputReadLine();
			p.BeginErrorReadLine();

			await Task.Run(() => p.WaitForExit());
			if (p.ExitCode != 0)
				throw new Exception($"ffmpeg trả mã lỗi {p.ExitCode}. Xem log để biết chi tiết.");
		}

		void AppendLog(string s)
		{
			if (txtLog.InvokeRequired)
			{
				txtLog.Invoke(new Action<string>(AppendLog), s);
				return;
			}
			txtLog.AppendText(s);
		}
	}
}