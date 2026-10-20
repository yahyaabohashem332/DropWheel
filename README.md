# Petal

**The zero-click file converter for Windows.** Start dragging a file in File Explorer, tap a key, and
drop it on a petal of the wheel that appears under your cursor. The result is saved next to the original.

| Tap while dragging | Wheel | What's on it |
|---|---|---|
| **Shift** | Convert | Every format the file can become (290 conversion paths) |
| **Ctrl** | Tools | Compress, metadata, edit, resize, watermark, crop, redact, copy text, trim, split, PDF password, page numbers, sign… (34 tools) |
| **Alt** | Actions | Share, Float, Copy, Move, Rename, ZIP, Copy Path, Terminal |

The keys can be changed in Settings. Let go of the key — the wheel stays open until you drop. Tap
another key to switch wheels in place. Drop on the centre (or press Esc) to cancel.

Everything runs locally. Nothing is uploaded.

No drag needed: right-click files in File Explorer → **Convert with Petal** opens the same wheel at
the mouse; click a petal, or use the arrow keys and Enter (Shift, Ctrl and Alt switch wheels).

**Keyboard shortcut — Ctrl + Win + Z.** With files selected in the File Explorer window in front (or
on the desktop), it opens the wheel for them; with nothing selected, it opens Petal. Press it again
to close the wheel. Change it in Settings › Keys: click the button and press the new keys. Petal asks
Windows first and says so when another program already owns a combination. (Ctrl + Win + Enter is
Narrator's, which is why the default is different.)

**Everyday file chores, also on the wheels:**

* **Resize & Rotate** — by percentage, width, height or a box; quarter turns and flips; one photo with a
  live preview or a whole selection.
* **Add Watermark** — text (Arabic included) or a logo, placed in one of nine spots or repeated
  across; on photos, on every frame of a video, or on every PDF page. The last watermark is remembered.
* **Copy Text** — reads the text in a photo or screenshot with Windows' own OCR (every installed
  language is tried; the cleanest reading wins) and puts it on the clipboard.
* **PDF tools** — password protection with AES-256 (or removing a password you know), page numbers as
  real text, a drawn, typed or scanned signature placed on a page (a picture, not a certificate
  signature), and extracting every picture (JPEGs saved as they are, each picture once).
* **Rename** — a pattern with the name, a running number, the date a photo was taken and the folder,
  plus find & replace and letter case; previewed with clashes flagged before anything changes, and
  undone from the confirmation card.
* **Compress to ZIP** — files and folders into one archive, with an optional AES-256 password.
* **Create QR Code** (notification-area menu or the main window) — text, links or a Wi-Fi network,
  saved as PNG or SVG or copied.

**English and Arabic.** Petal follows the Windows display language, and Settings › Appearance ›
Language switches it on the spot (Automatic · English · العربية). In Arabic every window is laid out
right to left, with proper Arabic plurals ("ملفان", "3 ملفات", "11 ملفًا"); pictures, timelines,
waveforms and playback controls stay left to right, and digits stay Western so numbers read the same
everywhere. The right-click menu entry and the notification-area menu follow the language too.

## Install

Run `dist\Petal-Setup-1.0.0.exe` (per-user, no admin rights; English or Arabic setup). It includes
.NET, FFmpeg and ExifTool, so nothing else is needed. A portable ZIP is built next to it.

Microsoft Office or LibreOffice, when installed, is used for slides and exact-layout Word/Excel output;
everything else is built in. On this development PC all 290 conversion paths are available.

## Tests

634 tests: core and app parts (96, including a check that every interface string has Arabic), media
engine (191), image engine (178), documents and PDF (169).
`tests/Petal.DragHarness` performs real shell drags with key taps for end-to-end checks.

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

Add `--lang ar` (or `--lang en`) to either command to render a specific language.

Interface text is written in English in the code and translated through `Localization/L.cs`
(`L.T`, `L.F` for formats, `L.P` for counts); the Arabic lives in `Localization/Arabic.cs`. The
localization tests fail when a new English string has no Arabic entry or a translation drops a `{0}`.

## Credits

Icons: [Lucide](https://lucide.dev) (ISC). FFmpeg (GPL build, run as a separate program),
ExifTool by Phil Harvey (Artistic/GPL), ImageMagick (Apache 2.0 style), NAudio (MIT),
SharpCompress (MIT), SharpZipLib (MIT, AES-encrypted ZIP), ZXing.Net (Apache 2.0, QR codes). Inspired by the Mac app Tangerine; Petal shares no code, art or assets with it.
