namespace auto_ffmpeg;

using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

public sealed record CaptureResult(
    string VideoUrl, string VideoExt,
    string AudioUrl, string AudioExt,
    string Title);

/// <summary>
/// Hidden Form hosting a WebView2; opens the Drive player and captures the
/// videoplayback requests (video + audio) via the CDP Network domain.
/// </summary>
public sealed class GoogleVideoCapturer : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, (string url, long clen, bool isVideo)> _streams = new();
    private Action<string>? _log;

    public GoogleVideoCapturer()
    {
        Text = "Dang lay link video tu Google Drive...";
        Width = 900; Height = 600;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Opacity = 0d;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        Controls.Add(_web);
        ShowIcon = false;
    }

    private static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "auto_ffmpeg", "WebView2");

    /// <summary>Opens the player for fileId and captures the best video+audio stream URLs. Null on timeout.</summary>
    public async Task<CaptureResult?> CaptureAsync(string fileId, string? browser, CancellationToken ct, Action<string>? onLog)
    {
        _log = onLog;

        Show();   // off-screen; creates the real window handle so WebView2 can init and BeginInvoke works

        try
        {
            var initTask = InitWebViewAsync();
            var done = await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(30), ct));
            if (done != initTask) { _log?.Invoke("[WARN] WebView2 khoi tao qua lau/that bai."); return null; }
            await initTask; // observe exceptions
        }
        catch (Exception ex) { _log?.Invoke($"[WARN] Khong khoi tao duoc WebView2: {ex.Message}"); return null; }

        var core = _web.CoreWebView2;
        await TryReuseCookiesAsync(browser);
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        var receiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        receiver.DevToolsProtocolEventReceived += OnRequest;
        core.NavigationStarting += OnNavigationStarting;

        core.Navigate($"https://drive.google.com/file/d/{fileId}/preview");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime? firstSeen = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(300, ct);
                try { await core.ExecuteScriptAsync("document.querySelector('video')?.play?.();"); } catch { }

                bool hasV = _streams.Values.Any(s => s.isVideo);
                bool hasA = _streams.Values.Any(s => !s.isVideo);
                if (hasV && hasA)
                {
                    firstSeen ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - firstSeen.Value).TotalSeconds >= 3) break;
                }
                if (sw.Elapsed.TotalSeconds > 40) break;
            }
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= OnRequest;
            core.NavigationStarting -= OnNavigationStarting;
            try { await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { }
        }

        var video = _streams.Values.Where(s => s.isVideo).OrderByDescending(s => s.clen).FirstOrDefault();
        var audio = _streams.Values.Where(s => !s.isVideo).OrderByDescending(s => s.clen).FirstOrDefault();
        if (video.url == null || audio.url == null)
        {
            _log?.Invoke("[WARN] Khong bat duoc du video+audio stream.");
            return null;
        }

        string title = SanitizeFileName(core.DocumentTitle);
        return new CaptureResult(
            DashStream.StripRange(video.url), DashStream.ExtFromMime(DashStream.GetQueryParam(video.url, "mime")),
            DashStream.StripRange(audio.url), DashStream.ExtFromMime(DashStream.GetQueryParam(audio.url, "mime")),
            title);
    }

    private async Task InitWebViewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        await _web.EnsureCoreWebView2Async(env);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.Contains("accounts.google.com"))
        {
            _log?.Invoke("[INFO] Can dang nhap Google - hien cua so dang nhap.");
            BeginInvoke(() =>
            {
                Opacity = 1d;
                StartPosition = FormStartPosition.CenterScreen;
                Location = new Point(
                    (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2,
                    (Screen.PrimaryScreen!.WorkingArea.Height - Height) / 2);
                WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                Activate();
            });
        }
    }

    private void OnRequest(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var url = doc.RootElement.GetProperty("request").GetProperty("url").GetString();
            if (url == null || !url.Contains("videoplayback")) return;

            var mime = DashStream.GetQueryParam(url, "mime");
            bool isVideo = DashStream.IsVideoMime(mime);
            bool isAudio = DashStream.IsAudioMime(mime);
            if (!isVideo && !isAudio) return;

            var itag = DashStream.GetQueryParam(url, "itag") ?? url.GetHashCode().ToString();
            long.TryParse(DashStream.GetQueryParam(url, "clen"), out var clen);
            _streams[itag] = (url, clen, isVideo);
        }
        catch { }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "drive_video";
        int idx = name.IndexOf(" - Google", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) name = name[..idx];
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>
    /// Try injecting Google cookies from a source browser into WebView2 to avoid login.
    /// browser: "firefox"/"chrome"/... or null/empty to skip.
    /// </summary>
    private async Task TryReuseCookiesAsync(string? browser)
    {
        if (string.IsNullOrEmpty(browser)) return;
        var ytdlp = DriveDownloader.ResolveYtDlpPath();
        if (ytdlp is null) { _log?.Invoke("[INFO] Khong co yt-dlp de xuat cookie, se dang nhap WebView2 neu can."); return; }

        var tmp = Path.Combine(Path.GetTempPath(), $"af_cookies_{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ytdlp,
                Arguments = $"--cookies-from-browser \"{browser}\" --cookies \"{tmp}\" --skip-download --no-warnings \"https://drive.google.com/\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var err = await stderrTask;
            if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                _log?.Invoke($"[INFO] Khong xuat duoc cookie (exit {p.ExitCode}), se dang nhap WebView2 neu can. {err}".Trim());
                return;
            }

            var cm = _web.CoreWebView2.CookieManager;
            int n = 0;
            foreach (var line in await File.ReadAllLinesAsync(tmp))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                bool httpOnly = false;
                var raw = line;
                if (raw.StartsWith("#HttpOnly_", StringComparison.Ordinal))
                {
                    httpOnly = true;
                    raw = raw.Substring("#HttpOnly_".Length); // strip prefix -> real domain
                }
                else if (raw.StartsWith('#'))
                {
                    continue; // genuine comment line
                }

                // Netscape cookies.txt: domain \t flag \t path \t secure \t expiry \t name \t value
                var f = raw.Split('\t');
                if (f.Length < 7) continue;
                var domain = f[0];
                if (!domain.Contains("google.com") && !domain.Contains("youtube.com")) continue;
                var cookie = cm.CreateCookie(f[5], f[6], domain, f[2]);
                cookie.IsSecure = f[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                cookie.IsHttpOnly = httpOnly;
                if (long.TryParse(f[4], out var exp) && exp > 0)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                cm.AddOrUpdateCookie(cookie);
                n++;
            }
            _log?.Invoke($"[INFO] Da nap {n} cookie tu {browser} vao WebView2.");
        }
        catch (Exception ex) { _log?.Invoke($"[INFO] Tai dung cookie that bai: {ex.Message}"); }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
