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
    private int _reqSeen;     // diagnostics: total Network.requestWillBeSent observed
    private int _gvSeen;      // diagnostics: requests to googlevideo
    private readonly HashSet<string> _hosts = new();  // diagnostics: distinct request hosts seen

    public GoogleVideoCapturer()
    {
        Text = "Dang lay link video tu Google Drive...";
        Width = 900; Height = 600;
        // Visible on-screen: Chromium throttles/occludes hidden (Opacity=0) windows, which can
        // suspend the player; showing it also lets the user click play if the synthetic click misses.
        StartPosition = FormStartPosition.CenterScreen;
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
        await TryReuseCookiesAsync(browser, fileId);

        CoreWebView2DevToolsProtocolEventReceiver? receiver = null;
        CoreWebView2DevToolsProtocolEventReceiver? attachRecv = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime? firstSeen = null;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            // The preview player is a cross-origin iframe = a separate CDP target (OOPIF); the top
            // target's Network domain won't see its videoplayback requests. Auto-attach (flatten) to
            // child targets and enable Network per session so their requests reach the same receiver.
            // waitForDebuggerOnStart=true pauses each child target on creation so we can enable
            // Network BEFORE it fires any request (otherwise early media requests race past us).
            await core.CallDevToolsProtocolMethodAsync("Target.setAutoAttach",
                "{\"autoAttach\":true,\"waitForDebuggerOnStart\":true,\"flatten\":true}");
            receiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            receiver.DevToolsProtocolEventReceived += OnRequest;
            attachRecv = core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget");
            attachRecv.DevToolsProtocolEventReceived += OnAttached;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NavigationStarting += OnNavigationStarting;
            core.Navigate($"https://drive.google.com/file/d/{fileId}/preview");

            var lastClick = TimeSpan.FromSeconds(-10);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(300, ct);

                // The Drive preview player lives in a cross-origin iframe, so top-frame
                // play() can't reach it. Until a stream is seen, synthesize a click at the
                // center via CDP (origin-agnostic) to press the player's play button.
                if (_streams.Count == 0 && sw.Elapsed.TotalSeconds < 8 && sw.Elapsed - lastClick > TimeSpan.FromSeconds(2))
                {
                    lastClick = sw.Elapsed;
                    await ClickCenterAsync(core);
                }
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
        catch (Exception ex) { _log?.Invoke($"[WARN] Loi khi bat link: {ex.Message}"); return null; }
        finally
        {
            if (receiver != null) receiver.DevToolsProtocolEventReceived -= OnRequest;
            if (attachRecv != null) attachRecv.DevToolsProtocolEventReceived -= OnAttached;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NavigationStarting -= OnNavigationStarting;
            try { await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { }
        }

        var video = PickBest(_streams.Values.Where(s => s.isVideo));
        var audio = PickBest(_streams.Values.Where(s => !s.isVideo));
        if (video.url == null || audio.url == null)
        {
            _log?.Invoke($"[WARN] Khong bat duoc du video+audio stream. (tong request={_reqSeen}, googlevideo={_gvSeen})");
            // If googlevideo=0, the media frame's requests still aren't visible (or media moved hosts):
            // dump the distinct hosts seen so we can tell WHERE the player actually fetched from.
            _log?.Invoke("[DEBUG] Hosts: " + string.Join(", ", _hosts.OrderBy(h => h)));
            return null;
        }

        string title = SanitizeFileName(core.DocumentTitle);
        return new CaptureResult(
            DashStream.StripRange(video.url), DashStream.ExtFromMime(DashStream.GetQueryParam(video.url, "mime")),
            DashStream.StripRange(audio.url), DashStream.ExtFromMime(DashStream.GetQueryParam(audio.url, "mime")),
            title);
    }

    // Prefer mp4/m4a streams so video+audio mux into .mp4 with stream-copy; fall back to any (e.g. webm) by largest clen.
    private static (string url, long clen, bool isVideo) PickBest(IEnumerable<(string url, long clen, bool isVideo)> streams)
    {
        var list = streams.ToList();
        var mp4 = list.Where(s => DashStream.ExtFromMime(DashStream.GetQueryParam(s.url, "mime")) is "mp4" or "m4a")
                      .OrderByDescending(s => s.clen).FirstOrDefault();
        return mp4.url != null ? mp4 : list.OrderByDescending(s => s.clen).FirstOrDefault();
    }

    // Synthesize a left click at the player's center via CDP (works across the cross-origin
    // preview iframe and even when the host window is off-screen).
    private static async Task ClickCenterAsync(CoreWebView2 core)
    {
        const string press = "{\"type\":\"mousePressed\",\"x\":442,\"y\":280,\"button\":\"left\",\"clickCount\":1}";
        const string release = "{\"type\":\"mouseReleased\",\"x\":442,\"y\":280,\"button\":\"left\",\"clickCount\":1}";
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", press);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", release);
        }
        catch { }
    }

    /// <summary>
    /// Build the (Cookie header, User-Agent) the authenticated browser session would send to <paramref name="url"/>,
    /// so an external parallel downloader (aria2c, 16 connections) can replicate the exact request Google
    /// accepts — single-connection downloads get throttled by googlevideo, parallel range requests don't.
    /// </summary>
    public async Task<(string cookieHeader, string userAgent)> GetSessionContextAsync(string url)
    {
        var core = _web.CoreWebView2;
        string ua = core.Settings.UserAgent;
        var cookies = await core.CookieManager.GetCookiesAsync(url);
        var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";
        _log?.Invoke($"[DEBUG] Session context: host={host} aria2c cookie count={cookies.Count} names={string.Join(",", cookies.Select(c => c.Name))}");
        return (header, ua);
    }

    private async Task InitWebViewAsync()
    {
        // Disable site isolation so the cross-origin preview-player iframe (and its nested media
        // frame) run IN-PROCESS, sharing the top target's CDP session. Without this, those OOPIFs
        // get their own renderer process + CDP session, and WebView2's event receiver — which is
        // bound to the ROOT session only — never sees their videoplayback/googlevideo requests
        // (nor the child-session attachedToTarget events the recursive auto-attach relied on).
        var opts = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--disable-features=IsolateOrigins,site-per-process --disable-site-isolation-trials"
        };
        var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, opts);
        await _web.EnsureCoreWebView2Async(env);
    }

    /// <summary>
    /// Download a (cleaned) stream URL THROUGH the authenticated browser session, so it uses the
    /// browser's stack/cookies/fingerprint that Google accepts (aria2c/curl get 403 on these).
    /// Forces a download (vs inline playback) by injecting Content-Disposition via CDP Fetch.
    /// Reuses this instance's CoreWebView2 (already authenticated by CaptureAsync). Returns false on failure.
    /// </summary>
    public async Task<bool> DownloadViaBrowserAsync(string url, string outputPath, CancellationToken ct, Action<int>? onProgress)
    {
        var core = _web.CoreWebView2;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        bool reqLogged = false;

        void OnFetchPaused(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var root = doc.RootElement;
                var requestId = root.GetProperty("requestId").GetString();

                // Request stage: log the browser's exact headers once (to compare vs aria2c), then continue.
                if (!root.TryGetProperty("responseStatusCode", out var sc))
                {
                    // Log the browser's EXACT headers (the request Google answers 200) so we can match them
                    // in aria2c. Cookie VALUES are redacted (only names+count) — safe to share, no secrets.
                    if (!reqLogged && root.TryGetProperty("request", out var req) && req.TryGetProperty("headers", out var hh))
                    {
                        reqLogged = true;
                        var names = new List<string>();
                        string? cookie = null;
                        foreach (var p in hh.EnumerateObject())
                        {
                            if (p.Name.Equals("cookie", StringComparison.OrdinalIgnoreCase)) cookie = p.Value.GetString();
                            else names.Add($"{p.Name}: {p.Value.GetString()}");
                        }
                        _log?.Invoke("[DEBUG] Browser req headers (non-cookie): " + string.Join(" | ", names));
                        if (cookie != null)
                        {
                            var cn = cookie.Split("; ").Select(c => c.Split('=')[0]).ToArray();
                            _log?.Invoke($"[DEBUG] Browser cookie count={cn.Length} names={string.Join(",", cn)}");
                        }
                    }
                    _ = core.CallDevToolsProtocolMethodAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId }));
                    return;
                }

                int status = sc.GetInt32();
                _log?.Invoke($"[DEBUG] Browser GET status={status}");

                // Redirect: follow unchanged.
                if (status is >= 300 and < 400)
                {
                    _ = core.CallDevToolsProtocolMethodAsync("Fetch.continueResponse", JsonSerializer.Serialize(new { requestId }));
                    return;
                }

                // 2xx: force a download (not inline playback) by replacing Content-Type with
                // octet-stream and adding Content-Disposition: attachment.
                var headers = new List<object>();
                if (root.TryGetProperty("responseHeaders", out var rh) && rh.ValueKind == JsonValueKind.Array)
                    foreach (var h in rh.EnumerateArray())
                    {
                        var n = h.GetProperty("name").GetString();
                        if (n != null && (n.Equals("content-type", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("content-disposition", StringComparison.OrdinalIgnoreCase))) continue;
                        headers.Add(new { name = n, value = h.GetProperty("value").GetString() });
                    }
                headers.Add(new { name = "Content-Type", value = "application/octet-stream" });
                headers.Add(new { name = "Content-Disposition", value = "attachment" });

                _ = core.CallDevToolsProtocolMethodAsync("Fetch.continueResponse",
                    JsonSerializer.Serialize(new { requestId, responseCode = status, responseHeaders = headers }));
            }
            catch (Exception ex) { _log?.Invoke($"[DEBUG] Fetch paused loi: {ex.Message}"); }
        }

        void OnDownloadStarting(object? s, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                started = true;
                e.Handled = true;                 // suppress default download UI
                e.ResultFilePath = outputPath;
                var op = e.DownloadOperation;
                op.StateChanged += (_, _) =>
                {
                    if (op.State == CoreWebView2DownloadState.Completed) tcs.TrySetResult(true);
                    else if (op.State == CoreWebView2DownloadState.Interrupted)
                    { _log?.Invoke($"[WARN] Tai gian doan: {op.InterruptReason}"); tcs.TrySetResult(false); }
                };
                op.BytesReceivedChanged += (_, _) =>
                {
                    if (op.TotalBytesToReceive is ulong tot && tot > 0)
                        onProgress?.Invoke((int)Math.Min(100, (double)op.BytesReceived / tot * 100));
                };
            }
            catch (Exception ex) { _log?.Invoke($"[DEBUG] DownloadStarting loi: {ex.Message}"); tcs.TrySetResult(false); }
        }

        var fetchRecv = core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
        fetchRecv.DevToolsProtocolEventReceived += OnFetchPaused;
        core.DownloadStarting += OnDownloadStarting;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Fetch.enable",
                "{\"patterns\":[{\"urlPattern\":\"*videoplayback*\",\"requestStage\":\"Request\"},{\"urlPattern\":\"*videoplayback*\",\"requestStage\":\"Response\"}]}");
            core.Navigate(url);
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                // If the download never STARTS within 25s, bail (avoids hanging on inline playback).
                var gate = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(25), ct));
                if (gate != tcs.Task && !started) { _log?.Invoke("[WARN] Browser download khong bat dau (timeout)."); return false; }
                return await tcs.Task;
            }
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            fetchRecv.DevToolsProtocolEventReceived -= OnFetchPaused;
            core.DownloadStarting -= OnDownloadStarting;
            try { await core.CallDevToolsProtocolMethodAsync("Fetch.disable", "{}"); } catch { }
        }
    }

    /// <summary>
    /// Download <paramref name="url"/> FAST via parallel <c>&amp;range=</c> segments routed THROUGH the
    /// browser's own network stack (CDP Network.loadNetworkResource + IO.read). These protected Drive
    /// URLs validate client identity (TLS/HTTP-2 fingerprint), so external clients (HttpClient/aria2c)
    /// get 403 — only the real Chromium stack returns 200. A single full GET is throttled by googlevideo
    /// to ~playback rate; many bounded range requests each burst at full speed (what the player does).
    /// Probes one segment first; returns false (caller falls back to single-stream) if unsupported.
    /// </summary>
    public async Task<bool> DownloadViaBrowserRangedAsync(string url, string outputPath, long totalSize, CancellationToken ct, Action<int>? onProgress)
    {
        if (totalSize <= 0) { _log?.Invoke("[DEBUG] Browser-ranged: thieu clen, bo qua."); return false; }
        var core = _web.CoreWebView2;
        const int SegmentSize = 4 * 1024 * 1024;
        const int Parallelism = 8;   // CDP calls marshal to the UI thread; 8 in-flight is plenty
        const int ReadChunk = 1024 * 1024;
        var sep = url.Contains('?') ? "&" : "?";

        string? frameId = null;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            var ft = await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}");
            using var d = JsonDocument.Parse(ft);
            frameId = d.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        }
        catch (Exception ex) { _log?.Invoke($"[DEBUG] getFrameTree loi: {ex.Message}"); }

        // Open one range segment through the browser stack; returns its IO stream handle (or null) + status.
        async Task<(string? handle, int status)> OpenAsync(string segUrl)
        {
            var p = JsonSerializer.Serialize(new { frameId, url = segUrl, options = new { disableCache = true, includeCredentials = true } });
            var json = await core.CallDevToolsProtocolMethodAsync("Network.loadNetworkResource", p);
            using var d = JsonDocument.Parse(json);
            var res = d.RootElement.GetProperty("resource");
            int status = res.TryGetProperty("httpStatusCode", out var sc) ? sc.GetInt32() : 0;
            bool success = res.TryGetProperty("success", out var su) && su.GetBoolean();
            string? handle = res.TryGetProperty("stream", out var st) ? st.GetString() : null;
            return (success ? handle : null, status);
        }

        async Task CloseAsync(string handle)
        { try { await core.CallDevToolsProtocolMethodAsync("IO.close", JsonSerializer.Serialize(new { handle })); } catch { } }

        // Probe segment 0: confirm the browser stack accepts a range request before building the machinery.
        long firstEnd = Math.Min(SegmentSize, totalSize) - 1;
        var (probeHandle, probeStatus) = await OpenAsync($"{url}{sep}range=0-{firstEnd}");
        _log?.Invoke($"[DEBUG] Browser-ranged probe: status={probeStatus} stream={(probeHandle != null)}");
        if (probeHandle == null)
        {
            try { await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { }
            return false;
        }
        await CloseAsync(probeHandle);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var ranges = new List<(long start, long end)>();
        for (long s = 0; s < totalSize; s += SegmentSize)
            ranges.Add((s, Math.Min(s + SegmentSize, totalSize) - 1));

        long downloaded = 0;
        int lastPct = -1;
        bool ok = false;
        var fh = File.OpenHandle(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        using var sem = new SemaphoreSlim(Parallelism);

        async Task Seg((long start, long end) r, int idx)
        {
            await sem.WaitAsync(ct);
            try
            {
                var segUrl = $"{url}{sep}range={r.start}-{r.end}&rn={idx}&rbuf=0";
                var (handle, status) = await OpenAsync(segUrl);
                if (handle == null) throw new Exception($"loadNetworkResource status={status}");
                try
                {
                    long offset = r.start;
                    while (true)
                    {
                        var readJson = await core.CallDevToolsProtocolMethodAsync("IO.read",
                            JsonSerializer.Serialize(new { handle, size = ReadChunk }));
                        using var rd = JsonDocument.Parse(readJson);
                        var root = rd.RootElement;
                        if (root.TryGetProperty("data", out var dataEl))
                        {
                            var data = dataEl.GetString() ?? "";
                            bool b64 = root.TryGetProperty("base64Encoded", out var be) && be.GetBoolean();
                            var bytes = b64 ? Convert.FromBase64String(data) : System.Text.Encoding.UTF8.GetBytes(data);
                            if (bytes.Length > 0)
                            {
                                await RandomAccess.WriteAsync(fh, bytes, offset, ct);
                                offset += bytes.Length;
                                long now = Interlocked.Add(ref downloaded, bytes.Length);
                                int pct = (int)(now * 100 / totalSize);
                                if (pct != lastPct) { lastPct = pct; onProgress?.Invoke(pct); }
                            }
                        }
                        if (root.TryGetProperty("eof", out var e) && e.GetBoolean()) break;
                    }
                }
                finally { await CloseAsync(handle); }
            }
            finally { sem.Release(); }
        }

        try
        {
            await Task.WhenAll(ranges.Select((r, i) => Seg(r, i)));
            ok = downloaded >= totalSize;
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log?.Invoke($"[WARN] Browser-ranged that bai: {ex.Message}"); return false; }
        finally
        {
            fh.Dispose();
            if (!ok) { try { File.Delete(outputPath); } catch { } }
            try { await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { }
        }
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

    // Auto-attached child target (e.g. the cross-origin player iframe): enable Network on its
    // session so its videoplayback requests flow to OnRequest (flatten routes them to one receiver).
    private void OnAttached(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            var sessionId = root.GetProperty("sessionId").GetString();
            var ti = root.GetProperty("targetInfo");
            var type = ti.GetProperty("type").GetString();
            var url = ti.GetProperty("url").GetString();
            _log?.Invoke($"[DEBUG] Attached target type={type} url={Trunc(url)}");
            if (!string.IsNullOrEmpty(sessionId))
                _ = InitChildSessionAsync(sessionId!);
        }
        catch (Exception ex) { _log?.Invoke($"[DEBUG] OnAttached loi: {ex.Message}"); }
    }

    // Enable Network on the child session AND recurse auto-attach into ITS children, so media
    // requests from deeply nested OOPIFs / workers (where the player actually fetches) are seen.
    private async Task InitChildSessionAsync(string sessionId)
    {
        var core = _web.CoreWebView2;
        try { await core.CallDevToolsProtocolMethodForSessionAsync(sessionId, "Network.enable", "{}"); }
        catch (Exception ex) { _log?.Invoke($"[DEBUG] Network.enable(session) loi: {ex.Message}"); }
        try { await core.CallDevToolsProtocolMethodForSessionAsync(sessionId, "Target.setAutoAttach",
            "{\"autoAttach\":true,\"waitForDebuggerOnStart\":true,\"flatten\":true}"); }
        catch (Exception ex) { _log?.Invoke($"[DEBUG] setAutoAttach(session) loi: {ex.Message}"); }
        // Resume the paused child now that Network is enabled (must always run or the page hangs).
        try { await core.CallDevToolsProtocolMethodForSessionAsync(sessionId, "Runtime.runIfWaitingForDebugger", "{}"); }
        catch (Exception ex) { _log?.Invoke($"[DEBUG] runIfWaitingForDebugger loi: {ex.Message}"); }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => _log?.Invoke($"[DEBUG] Navigation xong success={e.IsSuccess} url={Trunc(_web.CoreWebView2.Source)}");

    private static string Trunc(string? s) => s == null ? "" : (s.Length > 120 ? s[..120] + "..." : s);

    private void OnRequest(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var url = doc.RootElement.GetProperty("request").GetProperty("url").GetString();
            if (url == null) return;
            _reqSeen++;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) _hosts.Add(u.Host);
            if (url.Contains("googlevideo"))
            {
                _gvSeen++;
                if (_gvSeen <= 4) _log?.Invoke($"[DEBUG] googlevideo req: {Trunc(url)}");
            }
            if (!url.Contains("videoplayback")) return;

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
    private async Task TryReuseCookiesAsync(string? browser, string fileId)
    {
        if (string.IsNullOrEmpty(browser)) return;
        var ytdlp = DriveDownloader.ResolveYtDlpPath();
        if (ytdlp is null) { _log?.Invoke("[INFO] Khong co yt-dlp de xuat cookie, se dang nhap WebView2 neu can."); return; }

        var tmp = Path.Combine(Path.GetTempPath(), $"af_cookies_{Guid.NewGuid():N}.txt");
        try
        {
            // Use the real file URL so yt-dlp extracts successfully and writes the cookie jar
            // (a generic drive.google.com/ URL is "Unsupported URL" and yt-dlp exits without writing cookies).
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ytdlp,
                Arguments = $"--cookies-from-browser \"{browser}\" --cookies \"{tmp}\" --skip-download --no-warnings \"https://drive.google.com/file/d/{fileId}/view\"",
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
