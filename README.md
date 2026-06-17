# Auto FFmpeg Muxer

Ứng dụng Windows (WinForms, .NET 8) để **ghép video + audio bằng ffmpeg** và **tải video từ Google Drive**. Giao diện tiếng Việt, 3 tab công việc, log thời gian thực và thanh tiến trình.

> Đây là một desktop app dùng cá nhân — không phải thư viện. Build ra `auto_ffmpeg.exe` rồi chạy trực tiếp.

## Tính năng

### 1. Ghép 1 cặp (`Ghep 1 cap`)
Chọn (hoặc kéo-thả) 1 file video `.mp4` + 1 file audio, ghép thành `.mp4`.
- Mặc định dùng `-c copy` (nhanh nhất, không re-encode).
- Tự động fallback sang re-encode AAC nếu copy lỗi (tùy chọn).
- Tự gợi ý tên file xuất `<video>_merged.mp4`.

### 2. Ghép theo thư mục (`Ghep theo thu muc`)
Quét một thư mục, tự ghép các file **trùng tên cơ sở** (ví dụ `bai1.mp4` + `bai1.m4a`).
- Hỗ trợ nhiều định dạng audio, ưu tiên theo thứ tự `.m4a > .aac > .mp3 > .wav > .flac > .opus > .ogg`.
- Tick chọn từng cặp, hiển thị trạng thái OK/Lỗi cho từng dòng.

### 3. Tải từ Google Drive (`Tai tu Google Drive`)
Dán link share Drive và tải về. Có 2 phương thức:

- **Nhanh (DASH qua trình duyệt)** — _mặc định._ Mở video trong WebView2 ẩn, bắt 2 stream
  `videoplayback` (video + audio) qua giao thức CDP, tải song song nhiều luồng rồi ghép bằng ffmpeg.
  Vượt qua việc Google Drive bóp tốc độ tải 1 luồng (~1 MiB/s).
- **Thường (yt-dlp)** — gọi `yt-dlp` trực tiếp, có thể tăng tốc bằng `aria2c`. Dùng làm fallback
  tự động khi phương thức nhanh thất bại.

Hỗ trợ tái sử dụng cookie từ trình duyệt (Chrome/Firefox/Edge/Brave) hoặc file `cookies.txt`
để tải file yêu cầu đăng nhập.

## Cách tải nhanh hoạt động (DASH qua trình duyệt)

Google Drive bóp tốc độ stream `videoplayback` xuống mức bitrate phát lại khi tải 1 luồng. Trình
duyệt đạt tốc độ cao bằng cách bắn nhiều request `&range=START-END` song song. App tái hiện điều đó:

1. `GoogleVideoCapturer` mở player Drive trong WebView2 ẩn, dùng CDP `Network` domain bắt 2 URL
   `videoplayback` (phân biệt video/audio bằng mime, chọn theo `clen` lớn nhất → ưu tiên chất lượng cao).
2. Tải stream theo thứ tự fallback (xem `MainForm.DownloadStreamFastAsync`):
   1. **HttpClient song song** theo `&range=` — nhanh nhất, cho file không bị chặn.
   2. **Browser-ranged song song** qua CDP `Network.loadNetworkResource` — cho file bị Google chặn ở
      mức client-identity (TLS/HTTP-2 fingerprint), buộc phải đi qua network stack của Chromium thật.
   3. **Tải 1 luồng qua trình duyệt** — bị throttle nhưng luôn chạy được (last resort).
3. Ghép video + audio bằng `ffmpeg` (`-c copy`, fallback AAC). Container `.mp4` nếu video mp4 + audio
   m4a, ngược lại xuất `.mkv`.

## Yêu cầu

- **Windows** + **.NET 8 SDK** (target `net8.0-windows`, WinForms).
- **WebView2 Runtime** (cho phương thức tải nhanh) — thường đã có sẵn trên Windows 10/11.
- Các công cụ ngoài, đặt **cùng thư mục `.exe`** hoặc **trên `PATH`**:
  - `ffmpeg.exe` / `ffprobe.exe` — bắt buộc để ghép.
  - `yt-dlp.exe` — cho phương thức tải "Thường" và fallback.
  - `aria2c.exe` — tùy chọn, tăng tốc yt-dlp.

App tự dò công cụ: ưu tiên file cạnh ứng dụng (`AppContext.BaseDirectory`), nếu không có thì
`where <name>` trên PATH (xem `DriveDownloader.ResolveExe`, `FfmpegService.ResolveFfmpegPath`).

## Build & chạy

```sh
dotnet build auto_ffmpeg.sln -c Release
dotnet run --project auto_ffmpeg
```

> Nếu build báo `MSB3027`/`MSB3021` (file `.exe` bị khóa), hãy **đóng app đang chạy** trước khi build.

## Test

Unit test (xUnit) cho các helper thuần (`DriveLink`, `DashStream`):

```sh
dotnet test
```

> Lưu ý: phần tải Drive end-to-end **chưa** được test tự động (cần link Drive thật + đăng nhập +
> WebView2 runtime, và URL stream bị khóa theo IP).

## Cấu trúc mã nguồn

| File | Vai trò |
|------|---------|
| `Program.cs`, `MainForm.cs` | Điểm vào + toàn bộ UI WinForms và luồng điều khiển |
| `FfmpegService.cs` | Dò ffmpeg/ffprobe, dựng tham số, chạy ffmpeg + parse tiến trình |
| `DriveDownloader.cs` | Tải Drive bằng yt-dlp (+ aria2c), dọn file dở khi hủy |
| `GoogleVideoCapturer.cs` | WebView2 + CDP: bắt URL `videoplayback`, tải qua browser network stack |
| `DriveSegmentDownloader.cs` | Tải song song theo `&range=` bằng HttpClient (16 luồng) |
| `Aria2Downloader.cs` | Wrapper gọi `aria2c` đa luồng |
| `DashStream.cs` | Helper thuần xử lý URL googlevideo (mime, strip params, query param) |
| `DriveLink.cs` | Trích `fileId` từ link share Drive |
| `PairInfo.cs`, `DashStream.cs` | Model dữ liệu |
| `auto_ffmpeg.Tests/` | Unit test xUnit cho `DriveLink`, `DashStream` |
</content>
</invoke>
