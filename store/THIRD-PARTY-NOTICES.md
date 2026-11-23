# Third-party notices

DropWheel is built on open-source programs, libraries and AI models. Each one below is licensed by its
authors under its own licence; nothing in DropWheel's terms limits the rights those licences give you.
The full texts of the licences are at the end of this file and in the `licenses` folder in DropWheel's
install folder: `Apache-2.0.txt`, `GPL-3.0.txt`, `CC-BY-4.0.txt`, `FFmpeg.txt` (the GNU LGPL version 2.1),
`ExifTool.txt`, `DirectML.txt`; in `ffmpeg\licenses`, the licence files of every library inside FFmpeg;
in `magick\licenses`, the licence files of every library inside Magick.Native; and in `packages`, each
library's own licence and notice files as its authors wrote them.

All AI models run on this PC. DropWheel uses each one as it is published at the address given for it.


## Programs DropWheel runs

- FFmpeg 9.0.2 (ffmpeg.exe, ffprobe.exe and their DLLs), by the FFmpeg developers. LGPL-2.1-or-later.
  https://ffmpeg.org. This software uses libraries from the FFmpeg project under the LGPLv2.1. DropWheel's own
  build: https://github.com/yahyaabohashem332/dropwheel-ffmpeg, with the libraries compiled from pinned sources
  by FFmpeg-Builds (https://github.com/BtbN/FFmpeg-Builds, MIT). It holds no decoder or encoder of any format
  still under patent; `licenses\ffmpeg\configuration.txt` gives its exact configuration, `stages.txt` the
  repository and commit of every library, and `BUILD.txt` how it was made. The libraries inside its DLLs, each
  with its licence files in `licenses\ffmpeg\licenses`:
  - dav1d, Copyright (c) VideoLAN and dav1d authors. BSD-2-Clause.
  - libaom, Copyright (c) the Alliance for Open Media. BSD-2-Clause, with the Alliance for Open Media Patent
    License 1.0. With VMAF, Copyright (c) Netflix, Inc. BSD-2-Clause-Patent.
  - SVT-AV1, Copyright (c) the Alliance for Open Media. BSD-3-Clause-Clear, with the Alliance for Open Media
    Patent License 1.0.
  - libvpx and libwebp, Copyright (c) the WebM project authors (Google). BSD-3-Clause, with Google's patent
    grant.
  - LAME, Copyright (c) the LAME project. LGPL-2.0-or-later.
  - Opus, Copyright (c) Xiph.Org Foundation, Skype, Microsoft and others. BSD-3-Clause, with the Opus patent
    licences.
  - Ogg and Vorbis, Copyright (c) Xiph.Org Foundation. BSD-3-Clause.
  - soxr, Copyright (c) Rob Sykes. LGPL-2.1-or-later.
  - zimg, Copyright (c) sekrit-twc. WTFPL.
  - libass, Copyright (c) the libass contributors. ISC.
  - FreeType, Copyright (c) The FreeType Project. The FreeType License: portions of this software are
    copyright (c) The FreeType Project (www.freetype.org). All rights reserved.
  - HarfBuzz, Copyright (c) the HarfBuzz authors. MIT ("Old MIT").
  - FriBidi, Copyright (c) the FriBidi authors. LGPL-2.1-or-later.
  - Fontconfig, Copyright (c) Keith Packard and others. MIT-style (HPND).
  - libunibreak, Copyright (c) Wu Yongwei and others. zlib.
  - libxml2, Copyright (c) Daniel Veillard. MIT.
  - zlib, Copyright (c) Jean-loup Gailly and Mark Adler. zlib.
  - liblzma (XZ Utils), by Lasse Collin and others. 0BSD.
  - libiconv, Copyright (c) Free Software Foundation, Inc. LGPL-2.1-or-later.
  - The mingw-w64 runtime and winpthreads, Copyright (c) the mingw-w64 project. ZPL-2.1, MIT and BSD-3-Clause;
    with the GCC runtime libraries under the GPL with the GCC Runtime Library Exception.
- Windows Media Foundation, part of Windows, with the HEIF Image Extensions and HEVC Video Extensions from the
  Microsoft Store where they are installed: Windows' own decoders and encoders, which read and write H.264,
  HEVC, AAC, Windows Media and HEIC for DropWheel. They are licensed to you by Microsoft with Windows and with
  those extensions; DropWheel carries no decoder or encoder for these formats.
- ExifTool, Copyright Phil Harvey, with the Perl that runs it. Perl's terms (the Artistic License or the
  GPL). https://exiftool.org
- Piper, Copyright (c) 2022 Michael Hansen. MIT. https://github.com/rhasspy/piper
  - espeak-ng, used by Piper. GPL-3.0-or-later. https://github.com/rhasspy/espeak-ng (the fork Piper
    builds), from https://github.com/espeak-ng/espeak-ng
  - piper-phonemize and libtashkeel, used by Piper. MIT. https://github.com/rhasspy/piper-phonemize
  - ONNX Runtime, used by Piper, Copyright (c) Microsoft Corporation. MIT.
- llama.cpp, Copyright (c) 2023-2026 The ggml authors. MIT. With LLVM OpenMP: Apache-2.0 with LLVM
  exception. https://github.com/ggml-org/llama.cpp
- DeepFilterNet 3, Copyright (c) Hendrik Schröter. MIT or Apache-2.0, at your choice.
  https://github.com/Rikorose/DeepFilterNet

FFmpeg and ExifTool come with DropWheel. Piper, llama.cpp and DeepFilterNet come with the Microsoft
Store edition; other copies download them from the addresses above the first time a tool needs them.

### Source code for the GPL and LGPL programs and libraries

FFmpeg and four of the libraries inside it (LAME, soxr, FriBidi and libiconv) are under the GNU Lesser General
Public License, version 2.1 or later; DropWheel runs FFmpeg as a separate program, and its DLLs are files you
may replace with your own build. Their exact source code, with the scripts that built them, comes with
DropWheel: `licenses\sources\ffmpeg-n9.0.2-dropwheel-win64-source.tar.gz`. DropWheel's **custom
Magick.Native-Q8-x64 build** contains libraries under the GNU Lesser General Public License (fribidi, libiconv,
libxml2, zlib, liblzma, bzip2, freetype, harfbuzz, fribidi, and others from ImageMagick's dependency tree);
their complete source code and build scripts come with DropWheel:
`licenses\sources\magick-native-q8-x64-dropwheel-source.tar.gz` (from the dropwheel-magick release). espeak-ng,
which Piper uses, is under the GNU General Public License version 3, and DropWheel runs Piper as a separate
program. Its complete source code is available from https://github.com/espeak-ng/espeak-ng at the version
included in Piper 2023.11.14-2. For three years from the date you received DropWheel, the developer will also
send you, on request to the support address in the Microsoft Store listing, a copy of any of that complete
source code on a medium customarily used for software interchange, for no more than the cost of making and
sending it.


## Libraries inside DropWheel

MIT:
- .NET runtime, WPF, Microsoft.Extensions.*, Microsoft.Data.Sqlite and System.* packages:
  Copyright (c) .NET Foundation and Contributors.
- Windows SDK projections for .NET (C#/WinRT), DocumentFormat.OpenXml (Open XML SDK), ONNX Runtime with
  DirectML: Copyright (c) Microsoft Corporation.
- ClosedXML and ClosedXML.Parser: Copyright (c) ClosedXML contributors.
- DiscUtils (LTRData.DiscUtils: disc images, ISO 9660 and UDF): Copyright (c) 2008-2011 Kenneth Bell,
  Copyright (c) 2014 Quamotion, Copyright (c) 2011-2026 Olof Lagerkvist, LTR Data. LTRData.Extensions:
  Copyright (c) 2011-2026 Olof Lagerkvist, LTR Data.
- ExcelDataReader: Copyright (c) 2014 ExcelDataReader.
- ExcelNumberFormat: Copyright (c) 2017 andersnm.
- ExCSS (used by Svg.NET in the File Explorer extension, `shell\`): Copyright (c) Tyler Brinks.
- HtmlAgilityPack: Copyright (c) ZZZ Projects Inc.
- Joveler.Compression.XZ and Joveler.DynLoader (writing .tar.xz through liblzma): Copyright (c) 2018-present
  Hajin Jang.
- NAudio: Copyright (c) 2020 Mark Heath.
- PDFsharp and MigraDoc: Copyright (c) 2005-2025 empira Software GmbH.
- PDFtoImage: Copyright (c) 2019 David Sungaila.
- SkiaSharp: Copyright (c) 2015-2016 Xamarin, Inc. and Copyright (c) 2017-2025 Microsoft Corporation.
- RBush: Copyright (c) 2017 viceroypenguin.
- SharpCompress: Copyright (c) 2014 Adam Hathcock.
- SharpGLTF (SharpGLTF.Core, SharpGLTF.Runtime and SharpGLTF.Toolkit; glTF and GLB 3D models): Copyright (c)
  2018-2026 Vicente Penades.
- SharpZipLib: Copyright (c) 2000-2018 SharpZipLib Contributors.
- Whisper.net: Copyright (c) 2023 Sandro Hanea. whisper.cpp and ggml: Copyright (c) 2023-2026 The ggml
  authors.
- YamlDotNet (YAML in the data conversions): Copyright (c) Antoine Aubry and contributors.

Apache-2.0:
- Magick.NET: Copyright Dirk Lemstra. DropWheel uses a **custom Magick.Native-Q8-x64 build** (Apache-2.0:
  ImageMagick License, Copyright ImageMagick Studio LLC) **without libheif, libde265, or openh264** — the
  patented HEIF/HEVC/H.264 libraries. HEIC is read by Windows WIC (WindowsHeif.cs) and written through
  FFmpeg's hevc_mf (HeifWriter.cs), under Microsoft's Windows licence. The native library is the file
  Magick.Native-Q8-x64.dll, fetched from https://github.com/yahyaabohashem332/dropwheel-magick at release
  v1.0.0-dropwheel-magick, built from dlemstra/Magick.Native sources with those libraries excluded. Its
  complete source, with build scripts, is available from that repository. The libraries inside it, each with
  its licence, are listed in `licenses\magick\licenses` in DropWheel's install folder.
- SQLitePCLRaw: Copyright (c) SourceGear, LLC. SQLite itself is in the public domain.
- PdfPig: Copyright (c) UglyToad and contributors.
- SixLabors.Fonts 1.0.1: Copyright (c) Six Labors.
- ZXing.Net: Copyright (c) Michael Jahn and the ZXing authors.
- PDFium (the bblanchon/pdfium-binaries build): Apache-2.0; PDFium itself Copyright 2014 The PDFium
  Authors, BSD-3-Clause.

Other licences:
- Markdig: Copyright (c) 2018-2019, Alexandre Mutel. BSD-2-Clause.
- Tomlyn (TOML in the data conversions): Copyright (c) Alexandre Mutel. BSD-2-Clause.
- Svg.NET (thumbnails and previews of SVG files in File Explorer, `shell\Svg.dll`): © Copyright 2009 Microsoft,
  © Copyright 2011 vvvv Group, © Copyright 2021 SVG.NET Contributors. Microsoft Public License (MS-PL); in
  `licenses\MS-PL.txt`.
- Skia: Copyright (c) 2011 Google Inc. BSD-3-Clause.
- DirectML (Microsoft.AI.DirectML): the Microsoft DirectML License, which allows it to be redistributed
  with apps; in `licenses\DirectML.txt`.
- XZ Utils' liblzma 5.6.4 (`liblzma.dll`, which writes .tar.xz files): Copyright (C) The XZ Utils authors and
  contributors. BSD Zero Clause License (0BSD), below.
- Lucide icons: ISC. Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of
  Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.


## AI models

Shipped with DropWheel:

- YuNet face detector (OpenCV Zoo, Shenzhen Institute of Artificial Intelligence and Robotics for Society), MIT, and LPD-YuNet number plate detector (OpenCV Zoo, Dong Xu), Apache-2.0: downloaded when Blur Faces and Plates is first used.
- Real-ESRGAN general x4v3, Copyright (c) 2021, Xintao Wang. BSD-3-Clause.
  https://github.com/xinntao/Real-ESRGAN. ONNX export by Qualcomm AI Hub.
- Silero VAD 6.2, Copyright (c) 2020-present Silero Team. MIT. https://github.com/snakers4/silero-vad.
  The whisper.cpp (ggml) file at https://huggingface.co/ggml-org/whisper-vad.

Downloaded the first time a tool needs them, from the addresses below, with the licence shown before the
download:

- BiRefNet lite, Copyright (c) 2024 ZhengPeng. MIT.
  https://huggingface.co/onnx-community/BiRefNet_lite-ONNX (ONNX export by onnx-community).
- Whisper base, small and large-v3 turbo, Copyright (c) 2022 OpenAI. MIT. The whisper.cpp builds at
  https://huggingface.co/ggerganov/whisper.cpp.
- The natural English voice "Linda": Piper's LJSpeech voice (rhasspy/piper-voices), MIT, trained on the
  LJ Speech recordings by Keith Ito, which are in the public domain.
  https://huggingface.co/rhasspy/piper-voices
- Qwen2.5 1.5B Instruct and Qwen3 4B Instruct 2507, Copyright (c) Alibaba Cloud (the Qwen team).
  Apache-2.0. https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF and
  https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF (4-bit GGUF by Unsloth).
- Gemma 4 12B and E4B Instruct, Copyright (c) Google LLC. Apache-2.0.
  https://huggingface.co/unsloth/gemma-4-12b-it-GGUF and
  https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF (4-bit GGUF by Unsloth).
- SmolVLM 500M Instruct, Copyright (c) Hugging Face. Apache-2.0.
  https://huggingface.co/ggml-org/SmolVLM-500M-Instruct-GGUF
- multilingual-e5-small, Copyright (c) Microsoft Corporation (intfloat). MIT.
  https://huggingface.co/Xenova/multilingual-e5-small (8-bit ONNX by Xenova).
- CLIP ViT-B/32, Copyright (c) 2021 OpenAI. MIT. https://huggingface.co/Xenova/clip-vit-base-patch32
  (8-bit ONNX by Xenova).
- Multilingual CLIP text model (clip-ViT-B-32-multilingual-v1), by Nils Reimers and the UKP Lab
  (sentence-transformers). Apache-2.0.
  https://huggingface.co/sentence-transformers/clip-ViT-B-32-multilingual-v1
- Hybrid Transformer Demucs, Copyright (c) Meta Platforms, Inc. and affiliates. MIT.
  https://github.com/facebookresearch/demucs. As one ONNX file by timcsy, made by the demucs-web project
  (MIT, https://github.com/timcsy/demucs-web): https://huggingface.co/timcsy/demucs-web-onnx
- TIGER for Divide and Remaster, by Tsinghua University. Apache-2.0. https://github.com/JusperLee/TIGER.
  8-bit ONNX by audiojs: https://huggingface.co/audiojs/tiger-dnr
- LaMa (big-lama), Copyright (c) Samsung Research (advimman/lama). Apache-2.0.
  https://huggingface.co/Carve/LaMa-ONNX (ONNX export by Carve).
- M2M100 418M, Copyright (c) Facebook, Inc. and its affiliates (Meta). MIT.
  https://huggingface.co/facebook/m2m100_418M; 8-bit ONNX by Xenova: https://huggingface.co/Xenova/m2m100_418M
- Chatterbox Multilingual V3, Copyright (c) 2025 Resemble AI. MIT.
  https://github.com/resemble-ai/chatterbox. ONNX exports by Masterx
  (https://huggingface.co/Masterx/chatterbox-multilingual-v3-ONNX) and KitsuMate
  (https://huggingface.co/KitsuMate/chatterbox-multilingual-v3-onnx; DropWheel uses its flow and vocoder
  graphs, under Chatterbox's MIT licence, as its THIRD_PARTY_NOTICES.md says).
- DeepFilterNet 3 (inside the DeepFilterNet program above). MIT or Apache-2.0.

### Opus-MT (Creative Commons Attribution 4.0)

DropWheel's Dub and Translate tools, and live captions on a PC without a graphics card, use these translation
models, each made for one pair of languages:

- "opus-mt-en-ar", "opus-mt-ar-en", "opus-mt-en-es", "opus-mt-es-en", "opus-mt-en-fr", "opus-mt-fr-en",
  "opus-mt-en-id", "opus-mt-id-en", "opus-mt-es-fr" and "opus-mt-fr-es", and "opus-mt-tc-big-en-tr" and
  "opus-mt-tc-big-tr-en" (English and Turkish), by Jörg Tiedemann and the Language Technology Research Group at
  the University of Helsinki (Helsinki-NLP), each from https://huggingface.co/Helsinki-NLP/ followed by its name
  (https://huggingface.co/Helsinki-NLP/opus-mt-en-ar, and so on).
- Licensed under the Creative Commons Attribution 4.0 International licence (CC BY 4.0):
  https://creativecommons.org/licenses/by/4.0/ (its text is in `licenses\CC-BY-4.0.txt`).
- Changed from the originals: converted to ONNX and quantized to 8 bits, by Xenova
  (https://huggingface.co/Xenova/ followed by the model's name) and, for the two English and Turkish models, by
  the ONNX Community (https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr and
  https://huggingface.co/onnx-community/opus-mt-tc-big-tr-en). DropWheel uses those files as they are.
- The licensors offer the models as-is and make no representations or warranties of any kind about them.

## Licence texts

### MIT

Applies to each component listed as MIT above, with the copyright notice given beside it.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### BSD-2-Clause

Applies to Markdig, with the copyright notice given beside it.

Redistribution and use in source and binary forms, with or without modification, are permitted provided
that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and
   the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and
   the following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.

### BSD-3-Clause

Applies to Real-ESRGAN, Skia and PDFium, with the copyright notices given beside them.

Redistribution and use in source and binary forms, with or without modification, are permitted provided
that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and
   the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and
   the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or
   promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.

### ISC

Applies to the Lucide icons, with the copyright notice given beside them.

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is
hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE
INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE
FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING
OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

### 0BSD

Applies to XZ Utils' liblzma.

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is
hereby granted.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE
INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE
FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING
OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

### Apache-2.0, GPL-3.0 and CC BY 4.0

Their full texts are in the `licenses` folder: `Apache-2.0.txt`, `GPL-3.0.txt` and `CC-BY-4.0.txt`.
