# Third-party notices

DropWheel is built on open-source programs, libraries and AI models. Each one below is licensed by its
authors under its own licence; nothing in DropWheel's terms limits the rights those licences give you.
The full texts of the licences are at the end of this file and in the `licenses` folder in DropWheel's
install folder: `Apache-2.0.txt`, `GPL-3.0.txt`, `CC-BY-4.0.txt`, `FFmpeg.txt`, `ExifTool.txt`,
`DirectML.txt`, and in `packages`, each library's own licence and notice files as its authors wrote them.

All AI models run on this PC. DropWheel uses each one as it is published at the address given for it.


## Programs DropWheel runs

- FFmpeg (BtbN "gpl-shared" build: ffmpeg.exe, ffprobe.exe and their DLLs), by the FFmpeg developers.
  GPL-3.0-or-later. https://ffmpeg.org; build scripts at https://github.com/BtbN/FFmpeg-Builds.
  Its version is in `licenses\FFmpeg-version.txt`.
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

FFmpeg and espeak-ng are distributed under the GNU General Public License version 3, and some libraries
inside Magick.NET (see below) under the GNU Lesser General Public License. DropWheel runs FFmpeg and
espeak-ng as separate programs and does not link to them. Their complete source code is available from
the addresses in this file, at the versions DropWheel ships: FFmpeg at the version in
`licenses\FFmpeg-version.txt`; espeak-ng as included in Piper release 2023.11.14-2; Magick.NET's native
library at https://github.com/dlemstra/Magick.Native, at the Magick.NET version below. For three years
from the date you received DropWheel, the developer will also send you, on request to the support
address in the Microsoft Store listing, a copy of that complete source code on a medium customarily used
for software interchange, for no more than the cost of making and sending it.


## Libraries inside DropWheel

MIT:
- .NET runtime, WPF, Microsoft.Extensions.*, Microsoft.Data.Sqlite and System.* packages:
  Copyright (c) .NET Foundation and Contributors.
- Windows SDK projections for .NET (C#/WinRT), DocumentFormat.OpenXml (Open XML SDK), ONNX Runtime with
  DirectML: Copyright (c) Microsoft Corporation.
- ClosedXML and ClosedXML.Parser: Copyright (c) ClosedXML contributors.
- ExcelDataReader: Copyright (c) 2014 ExcelDataReader.
- ExcelNumberFormat: Copyright (c) 2017 andersnm.
- HtmlAgilityPack: Copyright (c) ZZZ Projects Inc.
- NAudio: Copyright (c) 2020 Mark Heath.
- PDFsharp and MigraDoc: Copyright (c) 2005-2025 empira Software GmbH.
- PDFtoImage: Copyright (c) 2019 David Sungaila.
- SkiaSharp: Copyright (c) 2015-2016 Xamarin, Inc. and Copyright (c) 2017-2025 Microsoft Corporation.
- RBush: Copyright (c) 2017 viceroypenguin.
- SharpCompress: Copyright (c) 2014 Adam Hathcock.
- SharpZipLib: Copyright (c) 2000-2018 SharpZipLib Contributors.
- Whisper.net: Copyright (c) 2023 Sandro Hanea. whisper.cpp and ggml: Copyright (c) 2023-2026 The ggml
  authors.

Apache-2.0:
- Magick.NET: Copyright Dirk Lemstra. Its native library holds ImageMagick (ImageMagick License,
  Copyright ImageMagick Studio LLC) and the libraries ImageMagick uses, each under its own licence: among
  them libheif, libde265, libraw, glib, pango, cairo, gdk-pixbuf, librsvg, libcroco and fribidi under the
  LGPL. Their notices and licence texts are in `packages\Magick.NET-Q8-AnyCPU - Notice.txt`. The
  native library is the file Magick.Native-Q8-x64.dll, which you may replace with one you build from its
  source.
- SQLitePCLRaw: Copyright (c) SourceGear, LLC. SQLite itself is in the public domain.
- PdfPig: Copyright (c) UglyToad and contributors.
- SixLabors.Fonts 1.0.1: Copyright (c) Six Labors.
- ZXing.Net: Copyright (c) Michael Jahn and the ZXing authors.
- PDFium (the bblanchon/pdfium-binaries build): Apache-2.0; PDFium itself Copyright 2014 The PDFium
  Authors, BSD-3-Clause.

Other licences:
- Markdig: Copyright (c) 2018-2019, Alexandre Mutel. BSD-2-Clause.
- Skia: Copyright (c) 2011 Google Inc. BSD-3-Clause.
- DirectML (Microsoft.AI.DirectML): the Microsoft DirectML License, which allows it to be redistributed
  with apps; in `licenses\DirectML.txt`.
- Lucide icons: ISC. Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of
  Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.


## AI models

Shipped with DropWheel:

- Real-ESRGAN general x4v3, Copyright (c) 2021, Xintao Wang. BSD-3-Clause.
  https://github.com/xinntao/Real-ESRGAN. ONNX export by Qualcomm AI Hub.

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
- Gemma 4 12B Instruct, Copyright (c) Google LLC. Apache-2.0.
  https://huggingface.co/unsloth/gemma-4-12b-it-GGUF (4-bit GGUF by Unsloth).
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
  https://github.com/facebookresearch/demucs. As one ONNX file by timcsy:
  https://huggingface.co/timcsy/demucs-web-onnx
- TIGER for Divide and Remaster, by Tsinghua University. Apache-2.0. https://github.com/JusperLee/TIGER.
  8-bit ONNX by audiojs: https://huggingface.co/audiojs/tiger-dnr
- LaMa (big-lama), Copyright (c) Samsung Research (advimman/lama). Apache-2.0.
  https://huggingface.co/Carve/LaMa-ONNX (ONNX export by Carve).
- Chatterbox Multilingual V3, Copyright (c) 2025 Resemble AI. MIT.
  https://github.com/resemble-ai/chatterbox. ONNX exports by Masterx
  (https://huggingface.co/Masterx/chatterbox-multilingual-v3-ONNX) and KitsuMate
  (https://huggingface.co/KitsuMate/chatterbox-multilingual-v3-onnx).
- DeepFilterNet 3 (inside the DeepFilterNet program above). MIT or Apache-2.0.

### Opus-MT (Creative Commons Attribution 4.0)

DropWheel's Dub and Translate tools use these two translation models:

- "opus-mt-en-ar" (English to Arabic) and "opus-mt-ar-en" (Arabic to English), by Jörg Tiedemann and the
  Language Technology Research Group at the University of Helsinki (Helsinki-NLP), from
  https://huggingface.co/Helsinki-NLP/opus-mt-en-ar and https://huggingface.co/Helsinki-NLP/opus-mt-ar-en.
- Licensed under the Creative Commons Attribution 4.0 International licence (CC BY 4.0):
  https://creativecommons.org/licenses/by/4.0/ (its text is in `licenses\CC-BY-4.0.txt`).
- Changed from the originals: converted to ONNX and quantized to 8 bits by Xenova
  (https://huggingface.co/Xenova/opus-mt-en-ar and https://huggingface.co/Xenova/opus-mt-ar-en).
  DropWheel uses those files as they are.
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

### Apache-2.0, GPL-3.0 and CC BY 4.0

Their full texts are in the `licenses` folder: `Apache-2.0.txt`, `GPL-3.0.txt` and `CC-BY-4.0.txt`.
