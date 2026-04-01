# Auto FFmpeg Muxer — Cleanup & Improvements Design

## Overview

Auto FFmpeg Muxer is a personal WinForms tool (.NET 8) for merging video + audio using FFmpeg. This spec covers 8 improvements focused on stability, code quality, and UX.

## 1. File Structure Refactor

Split the current monolithic `Program.cs` (~500 lines) into focused files:

```
auto_ffmpeg/
├── Program.cs              — entry point only (~6 lines)
├── MainForm.cs             — UI layout, events, drag & drop
├── FfmpegService.cs        — ffmpeg/ffprobe process logic, args building, progress parsing
├── DriveDownloader.cs      — yt-dlp download logic for Google Drive
├── PairInfo.cs             — data class for batch mode pairs
```

Delete unused `Form1.cs` and `Form1.Designer.cs`.

### Responsibilities

- **MainForm.cs**: All UI construction, event wiring, drag & drop handlers. Calls into `FfmpegService` and `DriveDownloader` for actual work. Manages button enable/disable state and cancel flow.
- **FfmpegService.cs**: 
  - `ResolveFfmpegPath()` — find ffmpeg.exe
  - `GetDurationAsync(string filePath)` — run ffprobe to get video duration in ms
  - `MergeAsync(string video, string audio, string output, bool useCopy, bool fallbackAac, CancellationToken ct, Action<int>? onProgress)` — run ffmpeg with cancellation and progress callback (0-100)
  - `BuildArgs(...)` — build ffmpeg argument string
- **DriveDownloader.cs**:
  - `DownloadAsync(string url, bool useCookies, string chromeProfile, CancellationToken ct, Action<string>? onLog)` — run yt-dlp with cancellation and log callback
- **PairInfo.cs**: Simple data class with `Basename`, `Video`, `Audio`, `Output` properties.

## 2. Fix Shared Checkbox Bug

**Problem**: `chkCopy` and `chkFallback` are added to both Single and Batch tabs, but a WinForms control can only have one parent. The second `Controls.Add` silently removes it from the first tab.

**Solution**: Move both checkboxes to the shared bottom area, above the log. They apply to both Single and Batch modes, so this is semantically correct.

```
┌─ Tabs ──────────────────────────────┐
│  [Ghep 1 cap] [Batch] [Drive]      │
│  (tab content, NO checkboxes)       │
└─────────────────────────────────────┘
☑ Dung stream copy    ☑ Tu dong fallback AAC
┌─ Nhat ky: ──────────────────────────┐
│  ...log...                          │
└─────────────────────────────────────┘
[===== progress bar =====] [Huy]
Trang thai: San sang.
```

## 3. Disable Buttons During Processing

When a merge/download starts:
- Disable: `btnRun`, `btnBatchMerge`, `btnDownloadDrive`, `btnScan`, `btnPickVideo`, `btnPickAudio`, `btnPickOutput`, `btnPickFolder`
- Show cancel button

When processing completes or is cancelled:
- Re-enable all buttons
- Hide cancel button

Implementation: a helper method `SetProcessingState(bool isProcessing)` that toggles all relevant controls.

## 4. Cancel Button

- A `Button btnCancel` placed next to the progress bar, hidden by default (`Visible = false`)
- Uses a `CancellationTokenSource` field on `MainForm`
- When clicked: calls `CancellationTokenSource.Cancel()`
- `FfmpegService.MergeAsync` and `DriveDownloader.DownloadAsync` accept `CancellationToken` and kill the process when cancelled
- Process kill: call `Process.Kill(entireProcessTree: true)` then `WaitForExit()`

## 5. Fix Google Drive Download

**Remove**:
- Hardcoded `--cookies-from-browser "chrome:profile=Profile 1:all"`
- URL manipulation: `.Replace("/view", "/uc?export=download")`

**Add to Drive tab UI**:
- `CheckBox chkUseCookies` — "Dung cookies Chrome" (default unchecked)
- `TextBox txtChromeProfile` — profile name (default "Default"), enabled only when chkUseCookies is checked

**yt-dlp arguments**:
- Always: pass the raw URL directly to yt-dlp
- If cookies enabled: add `--cookies-from-browser "chrome:profile={txtChromeProfile.Text}"`
- Keep: `-f "136+140/best" --merge-output-format mp4 -o "%(title)s_merged.%(ext)s"`

## 6. Drag & Drop Support

### Tab Single
- Enable `AllowDrop = true` on the tab
- On `DragDrop`: inspect dropped file extensions
  - Video extension (.mp4) → fill `txtVideo`
  - Audio extension (.m4a, .mp3, .aac, .wav, .flac, .opus, .ogg) → fill `txtAudio`
- If 2 files dropped (1 video + 1 audio) → fill both
- Auto-generate output path: same directory as video, `{basename}_merged.mp4`

### Tab Batch
- Enable `AllowDrop = true` on the tab
- On `DragDrop`: if a directory is dropped → fill `txtFolder` and auto-run `ScanPairs()`

### Tab Drive
- No drag & drop needed

## 7. Real Progress Bar from FFmpeg

### Getting duration
- Before merging, run: `ffprobe -v error -show_entries format=duration -of csv=p=0 "{video}"`
- Parse the output as `double` seconds → convert to microseconds for comparison

### Getting progress
- Add `-progress pipe:1` to ffmpeg args
- ffmpeg outputs key=value pairs to stdout, including `out_time_us=...` (microseconds processed)
- Parse `out_time_us` / `total_duration_us` * 100 = progress %
- Call `onProgress(percent)` callback → MainForm updates progress bar via `Invoke`

### Single mode
- Progress bar 0-100% for the single file

### Batch mode
- Each pair gets an equal share of the bar (e.g., 5 pairs → each is 20%)
- Within each pair, progress updates fill that pair's portion
- Formula: `overallPercent = (completedPairs * 100 + currentPairPercent) / totalPairs`

## 8. Delete Unused Files

Delete:
- `auto_ffmpeg/Form1.cs`
- `auto_ffmpeg/Form1.Designer.cs`

These are the default WinForms template files. The app uses `MainForm` defined in `Program.cs` (to be moved to `MainForm.cs`).

## Non-Goals

- No architecture patterns (MVVM, DI, etc.) — this is a personal tool
- No unit tests — manual testing is sufficient for this scope
- No new features beyond the 8 items listed
- No localization — keep Vietnamese UI as-is
