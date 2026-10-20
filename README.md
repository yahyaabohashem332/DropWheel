# Petal

**The zero-click file converter for Windows.** Start dragging a file in File Explorer, tap a key, and
drop it on a petal of the wheel that appears under your cursor. The result is saved next to the original.

| Tap while dragging | Wheel | What's on it |
|---|---|---|
| **Shift** | Convert | Every format the file can become (290 conversion paths) |
| **Ctrl** | Tools | Compress, metadata, edit, annotate, crop, redact, trim, split, merge… (26 tools) |
| **Alt** | Actions | Share, Float, Copy, Move, Copy Path, Terminal |

The keys can be changed in Settings. Let go of the key — the wheel stays open until you drop. Tap
another key to switch wheels in place. Drop on the centre (or press Esc) to cancel.

Everything runs locally. Nothing is uploaded.

## How it works

* **Gesture detection** — low-level mouse and keyboard hooks run on their own thread and only
  observe input. A drag is a press followed by a move past the system drag threshold; tapping exactly
  one mapped modifier during the drag opens that wheel.
* **The wheel is a real drop target.** It is a transparent, never-activated, topmost window registered
  with OLE. When the key is tapped it slides under the cursor while still invisible, nudges Explorer's
  drag loop, and becomes visible the moment Explorer hands it the dragged files (≈ 15–50 ms). Dropping
  on a petal returns `DROPEFFECT_COPY`, so Explorer never moves or deletes the originals. Explorer's
  drag thumbnail and native drop captions ("Convert to PNG") keep working over the wheel.
* **Frosted glass** — the pixels behind the wheel are captured just before it appears, blurred and
  saturated on the CPU, so it looks the same on Windows 10 and 11.
* **Engines** — FFmpeg (audio, video, GIF), ImageMagick via Magick.NET (images incl. HEIC, AVIF, WebP,
  SVG), PDFium / PDFsharp / PdfPig (PDF), Open XML SDK, Markdig and friends (documents, spreadsheets,
  EPUB), Microsoft Office or LibreOffice when installed (slides, exact-layout office files),
  SharpCompress (RAR, 7z), ExifTool (metadata), and Windows' own OCR and face detection.

## Project layout

```
src/Petal.Core              formats, conversion matrix, tool registry, naming, process helpers
src/Petal.Engines.Imaging   image conversions and tools (Magick.NET, Windows.Media.Ocr/FaceAnalysis)
src/Petal.Engines.Media     audio/video conversions and tools (FFmpeg)
src/Petal.Engines.Documents PDF, documents, spreadsheets, slides, EPUB
src/Petal.Engines.Files     archives, subtitles, ExifTool metadata
src/Petal.App               WPF app: tray, gesture hooks, wheel, HUD, tool windows, settings
tests/Petal.Tests           unit and integration tests
tests/Petal.DragHarness     drives a real shell drag (SHDoDragDrop) + key taps for end-to-end tests
tools/scripts               icon generation, helper-program download, packaging
```

## Building

Requirements: Windows 10 1809+ (x64), .NET 10 SDK.

```powershell
pwsh tools\scripts\fetch-tools.ps1      # FFmpeg + ExifTool into tools\bin (checksums verified)
dotnet build Petal.sln
dotnet test tests\Petal.Tests
src\Petal.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Petal.exe
```

During development Petal also finds FFmpeg on the PATH or in winget's package folder.

Design snapshots without a screen:

```powershell
Petal.exe --snapshots <out-folder> <background.png> <file>      # the wheel in every mode, light and dark
Petal.exe --snapshot-tools <out-folder> <test-data-folder>       # every tool window
```

## Credits

Icons: [Lucide](https://lucide.dev) (ISC). FFmpeg (GPL build, run as a separate program),
ExifTool by Phil Harvey (Artistic/GPL), ImageMagick (Apache 2.0 style), NAudio (MIT),
SharpCompress (MIT). Inspired by the Mac app Tangerine; Petal shares no code, art or assets with it.
