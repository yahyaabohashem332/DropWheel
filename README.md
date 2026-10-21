# Petal

**The zero-click file converter for Windows.** Start dragging a file in File Explorer, tap a key, and
drop it on a petal of the wheel that appears under your cursor. The result is saved next to the original.

| Tap while dragging | Wheel | What's on it |
|---|---|---|
| **Shift** | Convert | Your starred favourites first, then every format the file can become (342 conversion paths) |
| **Ctrl** | Tools | Compress, metadata, edit, remove or erase, resize, watermark, crop, redact, copy text or QR, searchable PDF, read aloud, transcribe, remove noise, separate vocals, trim, split, PDF password, page numbers, sign, export Photoshop layers, file info, collect project files, colour palettes… (48 tools), your recipes and your own petals |
| **Alt** | Actions | Ask Petal, Share, Send to Phone, Clean for Sharing, Float, Copy, Move, Rename, ZIP, Copy Path, Terminal, Find Duplicates |

The keys can be changed in Settings. Let go of the key — the wheel stays open until you drop. Tap
another key to switch wheels in place. Drop on the centre (or press Esc) to cancel. The centre names
the petal under the cursor in full: a long name goes onto up to three lines, set in a disc that fits
the hub, instead of being cut short.

Everything runs locally. Nothing is uploaded. Pictures and sounds dropped together are converted side
by side, one per processor core (up to four); a PDF's pages are drawn from one reading of the file and
saved together.

No drag needed: right-click files in File Explorer → **Convert with Petal** opens the same wheel at
the mouse; click a petal, or use the arrow keys and Enter (Shift, Ctrl and Alt switch wheels).

**Keyboard shortcut — Ctrl + Win + Z.** With files selected in the File Explorer window in front (or
on the desktop), it opens the wheel for them; with nothing selected, it opens Petal. Press it again
to close the wheel. Change it in Settings › Keys: click the button and press the new keys. Petal asks
Windows first and says so when another program already owns a combination. (Ctrl + Win + Enter is
Narrator's, which is why the default is different.) With nothing selected, the shortcut can open the
wheel for what is on the clipboard instead: copied files, a screenshot, or text (a copied link
becomes a QR code).

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
  saved as PNG or SVG or copied. **Copy Text** also reads QR codes and barcodes in a picture.
* **Compress for an app** — WhatsApp, Telegram, email, Discord and other presets that keep a file
  under the app's limit.
* **Make Searchable** — scanned PDFs and photos of pages get an invisible text layer from Windows'
  OCR (Arabic included, in reading order), so they can be searched and copied; pictures become one PDF.
* **Clean for Sharing** — copies without the location, device and author details hidden in photos,
  videos, PDFs and Office files. Send to Phone offers it when a photo shows where it was taken.
* **Scan with Phone** (notification-area menu) — a QR code opens a camera page on a phone on the same
  Wi-Fi; Petal finds each page's corners, straightens it, cleans it up (colour, grey or black & white)
  and saves the pages as one searchable PDF on the PC.
* **Find Duplicates** (Actions wheel or notification-area menu) — identical files, and photos or videos
  copied at another size, in a selection or folders; extra copies go to the Recycle Bin and History
  can put them back.

**On-device AI** — nothing leaves the PC; every model is checked against a pinned SHA-256.

* **Remove Background** — BiRefNet-lite cuts out the subject (hair included) for a transparent PNG,
  a white or coloured background, optionally cropped to the subject. The 224 MB model downloads once,
  from the tool window.
* **Transcribe** — Whisper (Fast · Balanced · Accurate, the last one strong in Arabic) turns speech
  in audio or video into subtitles (.srt) and/or text, in the spoken language or translated into
  English, and can add them to the video as a track or burned into the picture (Arabic shaped and
  right to left). Uses the graphics card through Vulkan when it can.
* **AI enlargement** in Resize — Real-ESRGAN (bundled, 4.5 MB) adds detail when a picture is made
  larger, on the graphics card through DirectML, else on the processor.
* **Read Aloud** — PDFs, Word files, e-books and text become an MP3 audiobook, with Windows' voices
  or the natural Piper voices Kareem (Arabic, diacritised automatically) and Joe (English).
* **Ask Petal** (Actions wheel) — say what to do with the files in English or Arabic ("اضغطها
  للواتساب", "make them 1080 px WEBP and zip them"). Petal shows the steps before anything runs and
  can save them as a recipe. Common requests are understood without a model; Qwen2.5 1.5B through
  llama.cpp understands requests worded freely.
* **Search by Meaning** (notification-area menu, Settings › Search) — an index on this PC of the words
  in documents, screenshots and subtitles, and of what photos and videos show (multilingual CLIP), so
  "beach at sunset" or "فاتورة الكهرباء" finds files. It reads only the folders you choose, while
  the PC is idle.
* **Remove Noise** — DeepFilterNet 3 takes hum, hiss and room noise out of speech in recordings and
  videos (the picture is copied as it is).
* **Separate Vocals** — Hybrid Transformer Demucs splits a song into vocals and music, or four parts.
* **Erase Object** — brush over a passer-by or a cable and LaMa fills the area in.
* **Arabic subtitles** — Translate to Arabic turns English SRT/VTT subtitles into Arabic with Opus-MT;
  Transcribe can write Arabic subtitles for speech in any language.

Each model downloads once from its tool window (sizes: natural voices 85 MB each, Ask Petal 1.1 GB,
search 227 MB, noise 27 MB, vocals 181 MB, eraser 208 MB, translation 114 MB) and is checked against
a pinned SHA-256.

**Adobe files** — no Adobe application needed:

* **Photoshop (PSD, PSB)** — convert to PNG, JPG, WEBP, TIFF, PDF and more (files saved without
  "Maximize compatibility" are combined from their visible layers); **Export Layers** saves every
  layer as a PNG named after it (Unicode names, groups, hidden layers, cropped or on the full canvas);
  **Remove Photoshop History** drops the hidden list of every file ever pasted in, which can make a
  PSD many times larger; Clean for Sharing works on PSDs too.
* **Camera RAW** (DNG, CR2, CR3, NEF, ARW, RAF, ORF, RW2 and more) — developed with the camera's white
  balance into JPG, HEIC, TIFF, PNG, WEBP, AVIF or PDF, upright, keeping the date, camera, lens,
  exposure and place.
* **Illustrator (AI)** — drawn as pictures with a transparent background, or saved as a plain PDF
  without Illustrator's editing data (often a much smaller file).
* **EPS** — the picture stored inside for previews; full-quality drawing and vector PDFs when the free
  Ghostscript is installed.
* **File Info** — size, colour mode, layers, pages or artboards, colour plates, fonts (installed here or
  not), linked files (found or missing) for PSD, RAW, AI, EPS, InDesign (INDD, IDML) and XD files.
* **Collect Files** — a Premiere Pro, After Effects, InDesign, IDML or Illustrator project with every
  file and font it uses, in one folder with a report; Premiere, After Effects and IDML copies are
  pointed at the collected files.
* **IDML** to Word, ODT, text, Markdown or HTML, in reading order; **Save Preview** for InDesign, XD and
  EPS files.
* **Swatches** — ASE (Photoshop, Illustrator, InDesign) and ACO files shown, converted into each other,
  saved as CSS, JSON, a GIMP palette or a picture; **Extract Colours** makes swatches from any picture.

**Make Petal yours:**

* **Recipes** (Settings › Recipes) — several steps as one petal on the tools wheel: remove the
  background, resize, add a watermark, convert, make searchable, rename, combine into a PDF, zip…
  Each step works on what the one before made.
* **Your own petals** (Settings › Your Petals) — a small JSON file in `%APPDATA%\Petal\petals` adds a
  tool that runs a program of your choice on the files it accepts. Petal shows the exact command
  before a petal's first run, and again whenever its file changes.
* **Favourites** (Settings › Favourites) — a format plus a longest side and a quality, such as
  "WEBP 1600" or "MP4 720p". They sit first on the conversion wheel with a star, and also make
  smaller copies of files already in that format.
* **History** (notification-area menu or Settings) — what Petal made lately, newest first: show a
  result in File Explorer, or undo it (results go to the Recycle Bin; renames are put back).
* **Automations** (Settings › Automations) — watch a folder such as Downloads and convert each new
  file once it has finished arriving (pictures, videos, audio or documents), to a format or with a
  favourite or a recipe. Results go to a "Petal" folder inside it, so a rule never feeds itself. A
  rule can also sort a folder: screenshots, invoices and everything else by kind into their own
  folders, and photos renamed after the day they were taken; History can put them back.
* **Send to Phone** (Actions wheel) — a QR code for a phone on the same Wi-Fi: it opens a page with
  the files (folders, and everything at once, as ZIP) and, when allowed, lets the phone send files
  back to Downloads. A small web server on this PC's local address under a random secret path; it
  stops when the window closes or after 15 idle minutes.
* **Command line** — `petal convert *.heic --to jpg --max 2048`, `petal resize`, `petal compress`,
  `petal zip`, `petal qr`, `petal text`, `petal codes`, `petal info`, `petal formats`, `petal run
  <recipe>`, `petal searchable`, `petal speak`, `petal ask "…"`, `petal search`, `petal denoise`,
  `petal separate`, `petal translate`, `petal layers`, `petal slim`, `petal preview`, `petal collect`,
  `petal colours`, `petal palette`. Installed as
  `Petal.com` beside `Petal.exe` (setup can add it to PATH); `petal help` lists the options.
* **Updates** — set an update address (an `update.json` over https, or a shared folder) in
  Settings › Updates and Petal checks once a day; the installer is verified against the feed's
  SHA-256 and installs quietly. No address is set by default, so a plain install never phones home.

**English and Arabic.** Petal follows the Windows display language, and Settings › Appearance ›
Language switches it on the spot (Automatic · English · العربية). In Arabic every window is laid out
right to left, with proper Arabic plurals ("ملفان", "3 ملفات", "11 ملفًا"); pictures, timelines,
waveforms and playback controls stay left to right, and digits stay Western so numbers read the same
everywhere. The right-click menu entry and the notification-area menu follow the language too.

## Install

Run `dist\Petal-Setup-1.3.2.exe` (per-user, no admin rights; English or Arabic setup). It includes
.NET, FFmpeg, ExifTool and the enlargement model, so nothing else is needed; the other AI models
download from their tool windows when first used. Setup can also add the `petal`
command to PATH. A portable ZIP and `update.json` (for an update feed) are built next to it.

Microsoft Office or LibreOffice, when installed, is used for slides and exact-layout Word/Excel output;
everything else is built in. EPS (and Illustrator files from before version 9) need Ghostscript for full
quality: all 342 conversion paths have an engine, and the 9 from EPS run when Ghostscript is installed.

## Tests

896 tests: core, app and command line (242, including a check that every interface string has
Arabic), media engine (216, transcription, voices, noise removal and vocal separation included),
image engine (255, AI models, Photoshop, camera RAW and swatches included), documents and PDF (183).
Tests that need a downloaded model look for it in a folder named by an environment variable
(`PETAL_LLM`, `PETAL_VOICES`, `PETAL_SEARCH_MODELS`, `PETAL_DEEPFILTER`, `PETAL_DEMUCS`, `PETAL_LAMA`,
`PETAL_OPUS`) and say they were skipped otherwise; `PETAL_ADOBE_SAMPLES` adds structure checks on
real Photoshop, Illustrator and EPS files.
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
src/Petal.Cli               the command line, published as Petal.com
tests/Petal.Tests           unit and integration tests
tests/Petal.DragHarness     drives a real shell drag (SHDoDragDrop) + key taps for end-to-end tests
tools/scripts               icon generation, helper-program download, packaging
```

## Building

Requirements: Windows 10 1809+ (x64), .NET 10 SDK.

```powershell
pwsh tools\scripts\fetch-tools.ps1      # FFmpeg, ExifTool and Real-ESRGAN into tools\bin (checksums verified)
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
SharpCompress (MIT), SharpZipLib (MIT, AES-encrypted ZIP), ZXing.Net (Apache 2.0, QR codes),
ONNX Runtime (MIT) with DirectML (Microsoft redistributable licence), Whisper.net and whisper.cpp
(MIT) with OpenAI's Whisper models (MIT), BiRefNet (MIT), Real-ESRGAN (BSD-3-Clause, ONNX export by
Qualcomm AI Hub), Piper (MIT) with the Kareem and Joe voices, llama.cpp (MIT) with Qwen2.5 1.5B
Instruct (Apache 2.0), CLIP ViT-B/32 (MIT) and multilingual CLIP text (Apache 2.0), DeepFilterNet
(MIT), Demucs (MIT), LaMa (Apache 2.0), Opus-MT by Helsinki-NLP (CC-BY 4.0 data, Apache 2.0 ONNX
export), PDFsharp and PdfPig (MIT), Microsoft.Data.Sqlite (MIT). Inspired by the Mac app Tangerine; Petal shares no code, art or assets with it.
