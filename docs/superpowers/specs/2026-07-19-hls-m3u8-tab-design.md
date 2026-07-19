# Tab "Tải m3u8 / HLS" — Design

Ngày: 2026-07-19
Trạng thái: Đã duyệt thiết kế, chờ viết plan.

## Mục tiêu

Thêm một tab thứ 4 vào `MainForm` cho phép người dùng dán một link `.m3u8` (HLS)
và tải xuống thành file `.mp4`, dùng đúng bộ tham số đã kiểm chứng cho CDN
Bunny/mediadelivery (User-Agent + Referer header + allowed extensions).

## Bối cảnh

Lệnh ffmpeg đã kiểm chứng chạy tốt:

```
ffmpeg -user_agent "<UA Chrome>" \
  -headers "Referer: https://iframe.mediadelivery.net/\r\n" \
  -extension_picky 0 \
  -allowed_extensions ts,mp4,m4s,aac,mpegts,dts \
  -allowed_segment_extensions ts,mp4,m4s,aac,mpegts,dts \
  -i "https://.../1080p/video.m3u8" \
  -c copy -bsf:a aac_adtstoasc "video_1080p.mp4"
```

Bản yt-dlp tương đương (tự resume khi đứt mạng):

```
yt-dlp --add-header "Referer:https://iframe.mediadelivery.net/" "<playlist.m3u8>" -o "video.mp4"
```

Codebase hiện tại: `MainForm` dựng mỗi tab bằng một `BuildXxxCard()` trả về
`TableLayoutPanel`, dùng chung khu vực log/progress/nút Huỷ ở dưới. ffmpeg được
gọi qua `FfmpegService` (pattern `-progress pipe:1`, parse `out_time_us`). yt-dlp
được gọi qua `DriveDownloader` (parse regex `[download] NN%`).

## Quyết định thiết kế (đã chốt với người dùng)

1. **Công cụ tải**: cả hai — 2 radio, mặc định ffmpeg, tùy chọn yt-dlp (tự resume).
2. **Header**: ô nhập Referer + User-Agent, điền sẵn giá trị mặc định, cho sửa để
   dùng cho nguồn m3u8 khác.
3. **Nơi lưu**: hộp thoại `SaveFileDialog` (Save As) mỗi lần tải.

## Giao diện

Thêm `TabPage tabHls = new("Tai m3u8 / HLS")` vào `TabControl`, và
`BuildHlsCard()` trả về grid (theo pattern `NewFormGrid`/`AddRow`):

| Dòng | Nhãn | Control |
|------|------|---------|
| 0 | `Link m3u8` | `txtHlsUrl` (TextBox, editable) |
| 1 | `Phuong thuc` | `rdoHlsFfmpeg` (Checked) + `rdoHlsYtdlp` trong FlowLayoutPanel |
| 2 | `Referer` | `txtReferer`, mặc định `https://iframe.mediadelivery.net/` |
| 3 | `User-Agent` | `txtUserAgent`, mặc định UA Chrome |
| 4 | (action) | nút `btnDownloadHls` = "TAI VE" (primary) |

- `btnDownloadHls` thêm vào mảng `ActionButtons` để bị disable khi đang xử lý.
- Style: `StylePrimary(btnDownloadHls)`. Các ô dùng `StyleInput`.
- Dùng lại khu vực dưới: `txtLog`, `progress`, `btnCancel`, `SetProcessingState`,
  `OnDriveProgress`, `AppendLog`, `SetStatus`, `ClearLog`.

## Luồng xử lý — `RunHlsDownloadAsync()` trong MainForm

1. Đọc `txtHlsUrl`. Rỗng hoặc không bắt đầu bằng `http` → MessageBox cảnh báo, dừng.
2. Đọc `referer = txtReferer.Text.Trim()`, `ua = txtUserAgent.Text.Trim()`.
3. Kiểm tra công cụ tương ứng có sẵn:
   - ffmpeg → `FfmpegService.ResolveFfmpegPath()`; null → MessageBox như hiện tại.
   - yt-dlp → `DriveDownloader.ResolveYtDlpPath()`; null → MessageBox như hiện tại.
4. Mở `SaveFileDialog` (Filter `MP4|*.mp4`, DefaultExt `mp4`, `FileName = "video.mp4"`).
   Nếu người dùng huỷ → thoát im lặng (không đổi trạng thái).
5. `ClearLog()`, `SetProcessingState(true)`, `progress.Style = Marquee`,
   `SetStatus("Dang tai m3u8...")`.
6. Gọi engine đã chọn (trong try/catch/finally như các Run\*Async khác):
   - ffmpeg → `FfmpegService.DownloadHlsAsync(url, output, referer, ua, ct, OnDriveProgress, AppendLog)`
   - yt-dlp → `HlsDownloader.DownloadWithYtDlpAsync(url, output, referer, ua, ct, AppendLog, OnDriveProgress)`
7. Kết quả: `SetStatus(ok ? "Tai hoan tat!" : "Loi khi tai.", error: !ok)`.
   Nếu ok, mở Explorer chọn file (`explorer.exe /select,...`) như tab Ghép.
8. `catch (OperationCanceledException)` → "Da huy tai." ; `finally` →
   `progress.Style = Blocks; SetProcessingState(false);`.

## Service

### `FfmpegService` (bổ sung, nhánh ffmpeg)

- `public static string BuildHlsArgs(string url, string output, string referer, string userAgent, bool withProgress)`
  — hàm **thuần**, dựng chuỗi tham số. Tách riêng để test được.
  - `-hide_banner -loglevel warning`
  - `-user_agent "<ua>"` (chỉ thêm khi ua không rỗng)
  - `-headers "Referer: <referer>\r\n"` (chỉ thêm khi referer không rỗng; header
    kết thúc bằng `\r\n` literal đúng như lệnh đã kiểm chứng)
  - `-extension_picky 0 -allowed_extensions ts,mp4,m4s,aac,mpegts,dts -allowed_segment_extensions ts,mp4,m4s,aac,mpegts,dts`
  - `-i "<url>"`
  - `-c copy -bsf:a aac_adtstoasc`
  - `withProgress` → thêm `-progress pipe:1`
  - `-y "<output>"`
- `public static async Task<long> GetHlsDurationUsAsync(string url, string referer, string userAgent)`
  — chạy `ffprobe` kèm `-user_agent`/`-headers` để lấy duration (giây → µs).
  Lỗi/không có ffprobe → trả 0.
- `public static async Task<bool> DownloadHlsAsync(url, output, referer, ua, ct, onProgress, onLog)`
  — resolve ffmpeg (null → false), lấy duration (nếu >0 thì `onProgress` chạy theo
  `out_time_us`; nếu 0 giữ marquee), dựng args bằng `BuildHlsArgs`, chạy qua runner
  ffmpeg dùng chung. Reuse `RunFfmpegAsync` hiện có (đổi sang `internal`/`public`
  nếu cần) thay vì nhân bản.

### `HlsDownloader` (file mới, nhánh yt-dlp)

- `public static async Task<bool> DownloadWithYtDlpAsync(url, output, referer, ua, ct, onLog, onProgress)`
  — reuse `DriveDownloader.ResolveYtDlpPath()`. Args:
  `--newline --add-header "Referer:<referer>" --user-agent "<ua>" "<url>" -o "<output>"`
  (bỏ `--add-header`/`--user-agent` khi tương ứng rỗng). `WorkingDirectory` = thư
  mục của `output`. Parse `%` bằng cùng regex `[download] NN%` như `DriveDownloader`
  (tách regex thành helper dùng chung hoặc nhân bản 1 dòng). Huỷ → kill cây tiến
  trình, xoá partial của chính lần tải này.

## Xử lý lỗi

- URL rỗng / không phải `http(s)` → MessageBox cảnh báo tiếng Việt, không throw.
- Thiếu ffmpeg/yt-dlp → MessageBox hướng dẫn (đặt .exe cạnh app hoặc thêm PATH),
  giống thông báo hiện có.
- Huỷ SaveFileDialog → return, không đổi trạng thái.
- Huỷ khi đang chạy → `_cts.Cancel()` → runner kill `entireProcessTree` (đã có).

## Kiểm thử

Thêm `auto_ffmpeg.Tests/HlsArgsTests.cs`:
- `BuildHlsArgs` chứa `-c copy`, `-bsf:a aac_adtstoasc`,
  `-allowed_extensions ts,mp4,m4s,aac,mpegts,dts`, và `Referer: <ref>\r\n`.
- Referer rỗng → không có `-headers`.
- UA rỗng → không có `-user_agent`.
- `withProgress=false` → không có `-progress pipe:1`; `true` → có.
- URL và output được bọc trong dấu ngoặc kép.

## Ngoài phạm vi (YAGNI)

- Chọn chất lượng/độ phân giải (dùng nguyên URL người dùng dán).
- Tải hàng loạt nhiều link m3u8.
- Cấu hình số luồng / tham số aria2c cho HLS.
- Ghi nhớ Referer/UA giữa các phiên.
