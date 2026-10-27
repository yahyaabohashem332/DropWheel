# Third-party notices

Petal is built on open-source work. Each component below is licensed by its authors under its own
licence; nothing in Petal's terms limits the rights those licences give you. Full licence texts for the
bundled programs are in the `licenses` folder beside Petal.exe.

## Programs Petal runs

| Program | Licence | Source |
|---|---|---|
| FFmpeg (BtbN "gpl-shared" build: ffmpeg.exe, ffprobe.exe and their DLLs) | GPL-3.0-or-later | https://ffmpeg.org, build scripts at https://github.com/BtbN/FFmpeg-Builds |
| ExifTool by Phil Harvey | Perl's terms (Artistic License or GPL) | https://exiftool.org |
| Piper (Microsoft Store package) | MIT | https://github.com/rhasspy/piper |
| espeak-ng, used by Piper (Microsoft Store package) | GPL-3.0-or-later | https://github.com/rhasspy/espeak-ng (the fork Piper builds), from https://github.com/espeak-ng/espeak-ng |
| piper-phonemize and libtashkeel, used by Piper (Microsoft Store package) | MIT | https://github.com/rhasspy/piper-phonemize |
| llama.cpp (Microsoft Store package) | MIT; LLVM OpenMP: Apache-2.0 with LLVM exception | https://github.com/ggml-org/llama.cpp |
| DeepFilterNet (Microsoft Store package) | MIT or Apache-2.0 | https://github.com/Rikorose/DeepFilterNet |

Copies installed from Petal's own setup download Piper, llama.cpp and DeepFilterNet the first time a tool
needs them, from the addresses above.

### Source code for the GPL programs

FFmpeg and espeak-ng are distributed under the GNU General Public License version 3. Petal runs them as
separate programs and does not link to them. Their complete source code is available from the addresses
above, at the versions Petal ships: FFmpeg at the version in `licenses\FFmpeg-version.txt`, and
espeak-ng as included in Piper release 2023.11.14-2. For three years from the date you received Petal,
the developer will also send you, on request to the support address in the Microsoft Store listing, a
copy of that complete source code on a medium customarily used for software interchange, for no more
than the cost of making and sending it.

## Libraries inside Petal

| Library | Licence |
|---|---|
| .NET runtime and WPF (Microsoft) | MIT |
| Windows SDK projections for .NET (Microsoft) | MIT |
| ClosedXML, ClosedXML.Parser | MIT |
| DocumentFormat.OpenXml (Open XML SDK) | MIT |
| ExcelDataReader, ExcelNumberFormat | MIT |
| HtmlAgilityPack | MIT |
| Magick.NET and ImageMagick | Apache-2.0; ImageMagick License |
| Markdig | BSD-2-Clause |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw | Apache-2.0 |
| SQLite | Public domain |
| Microsoft.Extensions.* | MIT |
| ONNX Runtime with DirectML | MIT |
| DirectML (Microsoft.AI.DirectML) | Microsoft DirectML License (redistributable), in `licenses\DirectML.txt` |
| NAudio | MIT |
| PDFsharp and MigraDoc | MIT |
| PDFtoImage | MIT |
| PDFium (bblanchon/pdfium-binaries) | Apache-2.0 and BSD-3-Clause |
| SkiaSharp and Skia | MIT; BSD-3-Clause |
| PdfPig | Apache-2.0 |
| RBush | MIT |
| SharpCompress | MIT |
| SharpZipLib | MIT |
| SixLabors.Fonts 1.0.1 | Apache-2.0 |
| System.Numerics.Tensors, System.Security.Cryptography.Pkcs | MIT |
| Whisper.net and whisper.cpp | MIT |
| ZXing.Net | Apache-2.0 |
| Lucide icons | ISC |

## Models

Shipped with Petal:

| Model | Licence | Source |
|---|---|---|
| Real-ESRGAN general x4v3 (ONNX export by Qualcomm AI Hub) | BSD-3-Clause | https://github.com/xinntao/Real-ESRGAN |

Downloaded the first time a tool needs them, from their authors' public pages, with the licence shown
before the download:

| Model | Licence | Source |
|---|---|---|
| BiRefNet lite | MIT | https://huggingface.co/onnx-community/BiRefNet_lite-ONNX |
| Whisper base, small and large-v3 turbo (whisper.cpp builds) | MIT | https://huggingface.co/ggerganov/whisper.cpp |
| Piper voices: Kareem (Arabic) and Joe (English) | MIT | https://huggingface.co/rhasspy/piper-voices |
| Qwen2.5 1.5B Instruct | Apache-2.0 | https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF |
| CLIP ViT-B/32 | MIT | https://huggingface.co/Xenova/clip-vit-base-patch32 |
| Multilingual CLIP text model | Apache-2.0 | https://huggingface.co/sentence-transformers/clip-ViT-B-32-multilingual-v1 |
| HT Demucs | MIT | https://huggingface.co/timcsy/demucs-web-onnx |
| LaMa | Apache-2.0 | https://huggingface.co/Carve/LaMa-ONNX |
| Opus-MT English to Arabic (Helsinki-NLP) | Apache-2.0 (ONNX export); CC-BY-4.0 training data | https://huggingface.co/Xenova/opus-mt-en-ar |
| Opus-MT Arabic to English (Helsinki-NLP) | Apache-2.0 (ONNX export); CC-BY-4.0 training data | https://huggingface.co/Xenova/opus-mt-ar-en |
