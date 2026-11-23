# DropWheel — complete handoff for moving the project to another computer

Written 2026-11-03 (10:40 UTC) at the end of a long session on the old PC, whose project drive (D:) is failing.
Read all of it before touching anything. The user writes in Arabic. Write your final summaries to them in Arabic.

> للمستخدم: هذا الملف كُتب لـ Claude على الحاسوب الجديد. افتح المشروع هناك ثم قل له: «اقرأ DropWheel-HANDOFF.md وتابع».
> انسخ معه أيضاً مفتاح توقيع التحديثات (القسم 4). من دونه لن تقبل النسخ المثبتة أي تحديث.

---

## 1. What the project is

- **DropWheel** (named **Petal** until 1.9.0): a Windows desktop app written in C#, .NET 10 and WPF.
  - It is the Windows equivalent of the Mac app Tangerine. **Never use Tangerine's name or assets.**
  - It is a zero-click file converter: start dragging a file, tap Shift (conversions), Ctrl (tools) or Alt (actions), and a wheel of "petals" opens under the pointer.
  - It also has hundreds of tools: media, images, PDF and office files, screen recording, captions, on-device AI (Whisper, local LLMs, voice cloning) and more, in 8 languages.
- **Owner:** the user, GitHub `yahyaabohashem332`, email yahya.abohashem3322@gmail.com.
  - They grant full autonomy and say "time does not matter, only the result".
  - They demand precision and verification, and want the app to make other tools unnecessary.
- **Goal now:** sell DropWheel on the **Microsoft Store**. Everything it ships or downloads must be free of any licence or patent conflict with commercial use.
  - The user's words: «راجع كل ما نستخدمه في التطبيق للتأكد من أن لا شيء يتعارض أبدا مع الاستخدام التجاري»
  - and, about FFmpeg: «غير ما يجب تغييره بدقة لا نريد الوقوع في أي مأزق أو مطالبة في ما بعد … طبق هذه ولا يهم الوقت أو الجهد … المهم المنتج الخالي تماما من أي مشكلة».
- **Internal names that still say Petal** (only what people see was renamed):
  - Petal.exe, Petal.com and the `petal` command;
  - the %APPDATA%\Petal and %LOCALAPPDATA%\Petal folders and PETAL_* environment variables;
  - the .petalwheel and .petal formats, registry and pipe names, project and namespace names.
  - The word "petal" for a wheel item stays.

## 2. Where everything is

- **Code:** private GitHub repository https://github.com/yahyaabohashem332/DropWheel.
  - Branch `master`, HEAD `3e5569e` "DropWheel 1.16.0: Dub translates by meaning, and a licence audit for the Store", 44 commits.
  - A fresh clone was checked with `git fsck` and came out clean.
  - On the old PC the working copy was `D:\plugins\Tangerine` (now corrupt; don't use).
  - **History was rewritten on 2026-11-03:**
    - `dist/` is removed from every commit (git-filter-repo `--path dist/ --invert-paths --preserve-commit-hashes`).
    - This matches a copy that had already been pushed on 2026-11-01, whose commit `dcc6614` "Keep build output out of the repository" puts `dist/` in `.gitignore`.
    - Commit IDs from before the rewrite still appear in old notes and in commit messages, on purpose. Examples: 1.15.0 `bcdc270` is now `29f1c70`; 1.16.0 `2d91124` is now `3e5569e`.
- **Git and GitHub accounts:**
  - `gh` on the old PC was signed in as `yahyaabohashem332` (active) and `yahyaabohashemstu`. Repositories belong to `yahyaabohashem332`.
  - Commit locally as `Petal Dev <yahya.abohashem3322@gmail.com>`. End every commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Until 2026-11-03 the rule was "never push". That day the user asked for the private GitHub copy and it was pushed. Ask before later pushes unless the user says to keep GitHub in step.
  - On the old PC, pushes used a one-off credential helper and no global config:
    `git -c credential.helper= -c "credential.helper=!gh auth git-credential" push`.
- **FFmpeg build repository:** https://github.com/yahyaabohashem332/dropwheel-ffmpeg (private), `main` at `8a305e3`.
  - Its workflow is "Build FFmpeg for DropWheel".
  - Run `41619132547` was still in progress at handoff; check it with `gh run list -R yahyaabohashem332/dropwheel-ffmpeg`.
  - Details are in section 9.
- **GitHub Releases** of DropWheel: one per build that ever shipped (see section 8). 8 of 29 are uploaded.
- **The original history with every installer** is only on the failing D: drive:
  - `D:\plugins\DropWheel-original-2026-11-03.bundle` (6.8 GB, verified when made). It holds the full pre-rewrite history.
  - `D:\plugins\DropWheel-release-assets\<tag>\` holds the 97 files extracted from it, plus `manifest.json`.
- **`testdata/` is not in git** (the `testdata/*` rule in `.gitignore`); tests need it.
  - A rescued copy is at `C:\Users\YAHYA\AppData\Local\Temp\claude\D--plugins-Tangerine\3131bbbb-9be3-4b7b-8421-b8bc97eb6aed\scratchpad\rescue\testdata`. It may hold bad bytes, since it was read from the failing disk.
  - It has 15 files: `.gitkeep`, album.pdf, brand.ase, figures.png, "landscape 2.webp", landscape.heic, landscape.jpg, landscape.pdf, landscape.webp, layers.psd, leaflet.idml, mixtape.mp3, weekend.mov, weekend.mp4, weekend.srt.
- **Memory notes** of earlier sessions (all summarised in this file) are on the old PC in `C:\Users\YAHYA\.claude\projects\D--plugins-Tangerine\memory\`.
- **Data the app keeps** in %LOCALAPPDATA%\Petal: `models\` (downloaded AI models), `studio\` (recording projects) and the dub cache.
  - Since 1.20.0 the user can keep the models in another folder (the `ModelsFolder` setting), chosen with Change
    Folder… on a model's download card or in Settings › AI. `ModelStore.MoveAsync` moves only DropWheel's items
    (the catalogue's files, the unpacked runtimes and caches named in `ModelStore`), all or none, under the
    download lock. A new file DropWheel keeps beside the models must be named there too, or it won't move.
  - The installed DropWheel on the old PC is `C:\Users\YAHYA\AppData\Local\Programs\Petal\Petal.exe`. Never touch the installed copy.

## 3. Solution layout

- `src/Petal.App`: WPF app. Contains:
  - the wheel and overlay, tool windows (`Tools/Windows/*`), Settings (`Settings/SettingsWindow*.cs`);
  - screen recording (`Screen/*`, `Screen/Studio/*`), captions and meetings (`Words/*`);
  - Explorer integration, `Localization/Arabic.cs` + `Localization/Languages/{tr,fa,ur,id,fr,es}.json`;
  - the Guide (`Guide/Entries/{en,ar,tr,fa,ur,id,fr,es}.json`), and `DesignSnapshots.cs` (snapshot cases).
- `src/Petal.Core`: formats, the conversion matrix, the tool registry, jobs, `Models/ModelStore.cs` (ModelCatalog: every downloadable model with URL, SHA-256, exact size and licence), `Recording/*` (studio planning) and `Util/*` (ToolLocator, ProcessRunner, Updates).
- `src/Petal.Engines.Media`: everything FFmpeg (`Internal/Ffmpeg.cs`, `FfmpegProcess.cs`, `MediaEncoder.cs`, `Containers.cs`), the media tools (`Tools/*`), MediaProbe and MediaPreview.
- `src/Petal.Engines.Imaging`: Magick.NET-Q8-AnyCPU 15.0.0, our HEIC writer (`Codecs/Heif/*` + `Codecs/FfmpegStillEncoder.cs`) and the image tools.
- `src/Petal.Engines.Documents` (PDF, Office, OCR), `src/Petal.Engines.Files`, and `src/Petal.Recipes` (multi-step features: Dubbing, ShortsMaker, TextEditing, Questions/AskFile, AskPetal, Translation).
- `tests/Petal.Tests` (about 753 tests, run by build.ps1), `tests/Petal.Engines.Media.Tests`, `tests/Petal.Engines.Imaging.Tests`, and others.
- `tools/scripts`:
  - `build.ps1`: the release build.
  - `build-store.ps1`: MSIX; out of scope for now.
  - `fetch-tools.ps1`: downloads FFmpeg and ExifTool into `tools/bin`.
  - `lucide2wpf.js` and `make_icon.py`.
- `store/`: Store package files and `THIRD-PARTY-NOTICES.md`, plus `store/licenses/{Apache-2.0,GPL-3.0,CC-BY-4.0}.txt`.

## 4. Building, testing, releasing

- **Release:** `pwsh tools\scripts\build.ps1 -Version X.Y.Z -Notes "<what's new, English>"`.
  - It runs `fetch-tools.ps1`, then `dotnet test tests\Petal.Tests -c Release`, then publishes self-contained ReadyToRun win-x64 to `dist\Petal`.
  - It copies the licences, makes `dist\DropWheel-X.Y.Z-win-x64.zip`, and builds Inno Setup 6 (`ISCC.exe`) into `dist\DropWheel-Setup-X.Y.Z.exe`.
  - It writes `dist\update.json`, signed with ECDSA P-256 (SHA-256, IEEE P1363) over `petal-update\n{version}\n{url}\n{SHA256 upper}\n{size}`.
  - **The private signing key is `%USERPROFILE%\.petal\update-signing-key.pem`. Copy it from the old PC to the new one, same place, and keep a backup. A new key would make every installed copy refuse updates.**
  - The public key is `Updates.PetalKey` in `src/Petal.Core/Util/Updates.cs`.
- **Check a release** by verifying the hash, size and signature of `update.json` against the setup file (with .NET `ECDsa.ImportSubjectPublicKeyInfo` and `VerifyData` in PowerShell), and `dist\Petal\Petal.com version` should print "DropWheel X.Y.Z".
- **Since the history rewrite, `dist/` is not tracked.**
  - Release files (setup, zip, update.json) now go to a GitHub Release tagged `vX.Y.Z` on the release commit, instead of being committed.
  - The old rule ("git rm the previous setup/zip, add the new ones") is obsolete.
- **Order** if other builds are ever asked for: x64 `build.ps1`, then arm64 `-SkipTests`, then `build-store.ps1`, one at a time.
  - The user currently wants **only the x64 exe**: «حاليا لا تعدل إلا تطبيق سطح المكتب … فقط تطبيق exe». No MSIX, arm64, README, web guide or Stream Deck work unless asked.
  - `store/THIRD-PARTY-NOTICES.md` is still shipped in the exe build, as `licenses\THIRD-PARTY-NOTICES.txt`.
- **Tests:** set `PETAL_SETTINGS_DIR` to a scratch `settings-test` folder so the real settings are untouched.
  - Timing tests flake under load; rerun them alone: WheelPreviewTimingTests, WheelTextTests' 1-second test, PhotoEditTests.PreviewRendersFast, OfficeTests' hung-job test.
    - One reason, measured 2026-11-15: through a full run Windows ignores the test process's requests for finer ticks (`timeBeginPeriod`) most of the time. Since Windows 11 it may, for a program none of whose windows can be seen, and the tests' windows come and go. Sleep(1) took a whole 15.6 ms tick in 39 of 45 seconds watched. Code that keeps time waits on a high-resolution waitable timer (`PreciseWait`: the studio's pointer, the screen's and the camera's frames); tests bring on the coarse clock with `CoarseClock` (`SetProcessInformation`), in the Timing collection, which runs alone (`StudioPointerTests`, `RecordingPaceTests`).
  - Gated tests use env vars:
    - PETAL_ASK and PETAL_ASK_DOCS / PETAL_ASK_KINDS (Ask a File);
    - PETAL_DUB_LINES, PETAL_DUB_LIMIT and PETAL_DUB_MODEL (dub translation);
    - PETAL_VOICES: a folder with `piper_windows_amd64.zip` + `en_US-ljspeech-medium.onnx` + `.onnx.json`;
    - PETAL_TEXTCUT (Edit by Text);
    - PETAL_REAL_PDFS (Edit PDF on files other programs wrote: a folder with `in\*.pdf`, results in an empty `out`, to read back with another program).
- **Snapshots** (checking UI by eye):
  - Command: `Petal.exe --snapshot-tools <outdir> <repo>\testdata <case> --lang en|ar`, after rebuilding Petal.App. Always pass `--lang en` for English, because the PC's language is Arabic.
  - Also `--snapshot-circle`. The first snapshot after a build can miss its video frame; render it again.
  - Cases include dub, dubbest, dubpreparing, shorts, editbytext, askfile and many more (see DesignSnapshots.cs).
- **Translations:**
  - Every UI string needs an Arabic entry in Arabic.cs, as `["key"] = "value",` lines before the closing `};`. LocalizationTests enforces it; sample or technical strings go in its NotTranslated list.
  - It also needs entries in the six JSON tables, rewritten with Python `json.dumps(table, ensure_ascii=False, indent=1, sort_keys=True) + "\n"`, CRLF line ends, no BOM.
  - Guide entries are rewritten with `json.dumps(d, ensure_ascii=False, indent=1)` (unsorted), CRLF, in all 8 languages.
  - The Guide's French puts a no-break space (U+00A0) before ; : ? ! and inside « ». Persian needs ZWNJ (U+200C) built with `chr(0x200C)`.
  - Wheel labels in Arabic, Persian and Urdu are drawn at 10pt, so long Latin words don't fit. LocalizationTests misses all-caps strings of 6 letters or fewer.
  - The keys READ ("مقروء") and ANSWER ("رد") are taken.
- **Shell traps** in the Claude Code environment on the old PC (Git Bash + PowerShell):
  - Backslashes inside Bash heredocs or inline scripts got mangled. Write scripts with the Write tool and run them with `python -I`.
  - The Write tool turns `\uXXXX` into the character itself, so build such escapes with `chr(92)+"u200F"`.
  - The repo mixes LF and CRLF. Never `sed -i` tracked sources. In Python use `open(p, encoding='utf-8', newline='')`, then check with `git --no-pager diff --stat`.
  - To see a file's line ends use `git --no-pager ls-files --eol -- <file>`, or count `b'\r\n'` in Python. Not grep: Git Bash's grep drops CRs unless given `-U`, and typed inline `$'\r$'` loses its backslash, so `grep -c $'\r$'` counted every line of LF files as CRLF.
  - Always `git --no-pager`.

## 5. Standing rules from the user

- **Reporting:** final summaries go to the user in Arabic.
- **Scope:** the x64 exe only, as in section 4.
- **Pace:** one thing at a time, each finished before the next («لا تخلط الأمور أبدا»).
- **Done means all of these:**
  - code and tests;
  - Arabic plus 7 languages, and Guide entries;
  - snapshots checked by eye in en and ar;
  - an x64 release and a commit.
- **The user's PC:**
  - Don't install software on it or change its system or security settings.
  - Don't touch the installed DropWheel.
  - Never script input into Explorer and never move the user's mouse. The user declined computer-use control; check the UI with snapshots and in-process tests instead.
  - A dev instance started with fresh settings writes to the real HKCU (the Explorer thumbnail handler pointing at the debug DLL). Afterwards run the dev exe with `--unregister-shell` and stop the COM Surrogate that holds Petal.Shell.dll.
- **Hardware:**
  - Never run TIGER on DirectML: it hung the GPU (TDR) on the user's PC.
  - Keep heavy AI on the CPU unless measured otherwise.
- **Releases:** never let a release build skip the tests.

## 6. Release history (what each version brought)

- **Petal 1.0.0 to 1.8.4** (2026-10-20 to 10-27):
  - core wheel and converters, then tools, the Arabic interface, Explorer menu, on-device AI;
  - 1.2: recipes, Ask Petal, search by meaning; 1.3: Adobe files; 1.4: folders; 1.5: wheels without files and the Store package;
  - 1.6: screen and text tools; 1.7: command palette and captions; 1.8: eight languages; 1.8.x fixes.
- **1.9.0:** renamed DropWheel.
- **The six features** the user asked for on 2026-10-27, built strictly in order, all shipped:
  - 1.10.0 Circle Anything (Ctrl+Win+C)
  - 1.11.0 Make Shorts
  - 1.12.0 Dub
  - 1.13.0 Edit by Text
  - 1.14.0 Ask a File
  - 1.15.0 Recording Studio (cinematic recording)
- **1.16.0** (2026-11-03):
  - Dub's fourth level "Best" (الأدق), using Gemma 4 12B;
  - the licence audit: lessac-based voices removed; Linda added; complete notices; Settings › Helpers has an "Open-source licences" button.
- **1.17.0 to 1.19.0** (to 2026-11-04): built on the user's PC. **1.19.0 is the code of `master` at `b9b212e`** ("Time Tracker", 2026-11-04 23:01); its `DropWheel-1.19.0-win-x64.zip` was made a minute later. The version is never in git (it is `build.ps1 -Version`), so read the latest one from the user's `dist` or ask; never guess it from the code.
- **1.19.1** (2026-11-07, branch `claude/inspiring-allen-0jcb5x`): DropWheel starts on PCs whose region writes 3.4 as "3,4" (`PlainNumbers`); a part that can't start no longer leaves DropWheel half started (`StartupParts`, a Start-up row in Health); the tray icon retries. Built by `.github/workflows/build.yml` on GitHub Actions (Windows Server 2025), whose `VERSION` default must be raised with each release.
- **1.20.0** (2026-11-11, built on the new PC, `D:\Tangerine`): no patented codec and nothing under the GPL (section 9); AI models kept in a folder the user chooses; live captions rebuilt, with two lines of translation (section 7). The workflow's `VERSION` default still says 1.19.1: raise it to the latest version once `gh` has the `workflow` scope.
- **1.21.0** (2026-11-15): live captions without a graphics card (small models for each pair), the sentence moving up and its proper translation shown gently, fewer words changed when it comes, Arabic without a space before its full stop (`Translator.Tidy`); the Opus-MT Arabic files pinned to their commits; the studio pointer kept at 120 a second by a high-resolution timer (after an export in the same process, Sleep(1) had taken a whole 15.6 ms tick: 64 a second); Explorer thumbnails for a user whose name has a space or Arabic letters (`81869eb`). The workflow's `VERSION` default says 1.20.0 (`c4ae256`, from GitHub); an Actions run on 2026-11-15 (43669144477) built an artifact called 1.20.0 from an earlier form of this work, which 1.21.0 supersedes.
- **1.22.0** (2026-11-15): Study File (section 7): a book, course material, lecture notes, slides or a recorded lecture as one interactive page to study from and send to students. Arabic now counts cards and questions with its five forms («57 بطاقة», not «57 بطاقات»).

## 7. What each big feature does, and what was learned (keep these)

- **Make Shorts:**
  - `Transcriber.TranscribeWordsAsync` feeds whisper.cpp pieces under 28 s, cut at pauses. DTW silently stops returning segments on long audio otherwise.
  - Pipeline: ShortMoments, then SpeakerFraming (Windows FaceDetector + mouth movement), then ShortClip (one FFmpeg pass), with Recipes/ShortsMaker on top.
- **Dub** (DubWindow, Recipes/Dubbing, DubTrack, VoiceCloner, SpokenText, MusicRemover):
  - **Voice model:** Chatterbox Multilingual V3 (Resemble AI, MIT).
    - `speech_encoder`, `embed_tokens`, `language_model_q4` and `tokenizer.json` from Masterx/chatterbox-multilingual-v3-ONNX at pinned commit `19b94afe…`.
    - The voice maker from KitsuMate/chatterbox-multilingual-v3-onnx at `3f44461d…`, split into flow_prepare_slim + flow_step_slim + vocoder_slim. These load in about 7 s; the one-graph exports take 50–96 s.
    - 10 flow steps on a cosine schedule, guidance 0.7. The flow step runs on DirectML, falling back to the CPU.
  - **Hearing back:** Whisper large-v3-turbo judges each spoken line (WER ≤ 0.2). SpokenText writes numbers as words.
  - **Music:**
    - Brief (2026-10-28): the music is removed completely and other background sounds are kept.
    - Pipeline: Demucs, then TIGER-DnR (audiojs tiger.int8.onnx, pinned `65072a21…`) on Demucs' "rest", middle channel only, mask = (effects power share)^4.
    - TIGER runs on the CPU only: about 52 s per 12 s piece, 2 pieces at once when RAM ≥ 15 GB.
  - **Translation levels:** Fast, Balanced and Accurate use Opus-MT (Helsinki-NLP, CC-BY 4.0, Xenova ONNX); Translator.Tidy cleans its Arabic.
  - **Level 4, "Best", added in 1.16.0** (`src/Petal.Recipes/Translation/DubTranslation.cs`):
    - Gemma 4 12B Instruct Q4_K_M (Google, Apache-2.0, unsloth GGUF pinned `fc034cff…`, 7.1 GB), run by llama.cpp b11763 through AskPetal.WriteAsync.
    - The Gemma prompt format `<|turn>system\n…<turn|>\n<|turn>user\n…<turn|>\n<|turn>model\n` is used when the file name contains "gemma"; temperature 0, prefill "1.".
    - It first writes a brief of the whole transcript, then translates batches of 8 lines with the 4 lines before as context.
    - Target words per line = mean of (duration × rate: ar 2.1, en 2.6 words/s) and (original words × ratio: en→ar 0.8, ar→en 1.25).
    - Replies are parsed by number and checked for script and length; any missing line falls back to Opus-MT.
    - A translation that repeats an earlier one's words (overlap > 0.6) while the originals differ (< 0.4) is asked again alone.
    - Measured on the i5-7600K for a 20-line sermon: Qwen3 4B took 96 s (weak); Gemma 4 E4B took 151 s; Gemma 4 12B took 552 s and was clearly best.
    - The window shows level 4 only when Whisper turbo and the Gemma model are present. Its cache name includes the writer model id.
  - **ModelStore.Find needs the exact file size**, so a test ModelInfo must carry the real length.
- **Edit by Text** (TextCut, Recipes/TextEditing):
  - FFmpeg expressions refuse more than 100 terms added in a row, so PauseRemover.Expression builds a search tree of `gte(t,S)*lt(t,E)` leaves.
  - Cuts are half-open, and audio uses 32-sample blocks (PauseRemover.AudioSelect).
  - Whisper's DTW word times are unreliable. Words are placed on runs of sound by a shortest path (TextCut.Place/Part), and candidate "uh" sounds are re-heard alone (CheckSoundsAsync) so speech is never deleted.
  - Measure with the PETAL_TEXTCUT gated test.
- **Ask a File** (Recipes/Questions):
  - Qwen3-4B-Instruct-2507 Q4_K_M answers on the CPU (Qwen2.5-1.5B failed; don't fall back to it).
  - multilingual-e5-small int8 + BM25, joined by reciprocal rank fusion, finds the passages; an Arabic↔English question is also searched translated.
  - Places: page, slide, rows or minute. Highlights come from DocumentPlaces.Marks.
  - The gated tests scored 10/10 and 7/7.
- **Recording Studio** (Screen/Studio/*, Petal.Core/Recording/*):
  - Flow: record with "Cinematic" on, then a project in %LOCALAPPDATA%\Petal\studio\<stamp>\, then StudioWindow.
  - Capture: pointer sampled at 120 Hz; typing recorded by caret position, never letters.
  - Planning: ZoomPlanner, CameraPlan (springs) and PointerPlan.
  - Output: StudioRenderer (Skia, motion blur), then StudioExport (audio graph, then video). The preview is a WPF MediaElement, whose FlowDirection must be LTR. RecordingPolish remains for plain recordings with the camera.
- **Screen recording's frames** (`ScreenRecorder.Grab` and `CameraTrack`, both through `Screen/FramePace.cs` and `Screen/FramePipe.cs`):
  - Frame n is taken n/fps seconds in (30 a second, 24 above 1920×1080). While the PC is a whole frame behind, the last one goes again, so the video keeps time with the sound.
  - Until 2026-11-15 they waited with Thread.Sleep. A recording had finer ticks only because WPF asks Windows for 1 ms ticks while any animation runs: the bar's blinking dot (a probe: Sleep(1) 15.5 ms with a window shown, 1.8 ms once something in it animates, even off screen). With Windows told to ignore that, a frame that waited for its moment was taken 7.4–9.3 ms late on average (95% within 14–18 ms); with `PreciseWait`, 0.3–0.6 ms either way.
  - Measured in the real recorder with `D:\.dropwheel-sdk\record-pace`: a `--record-pace` switch and hooks put into a copy, never committed. It runs a real session on a 1280×720 area or the whole screen, with sound and the camera, and logs every frame's moment.
  - Frames go to FFmpeg through `FramePipe`, a thread of their own. Once its first frames are in, FFmpeg reads nothing while Windows starts the graphics card's H.264 encoder: 0.5–0.7 s at 1280×720 with the PC quiet, up to 2.7 s (4 s for the whole screen) with the card busy; the software encoder, 0.1 s. FFmpeg's `-thread_queue_size` doesn't change it. Written straight in, every recording started with its first picture held that long (until 2026-11-15).
    - Frames wait in native memory, freed as soon as each is written: 85 MB at 1280×720, 310–340 MB at 2560×1440, for the 0.7–0.8 s. Up to 3 s of frames may wait, within a quarter of the free memory and 768 MB; past that the last one goes again. A recording stopped meanwhile still gets every frame.
  - Still open: at 2560×1440 the GDI grab (BitBlt with CAPTUREBLT) takes 32–54 ms of a 41.7 ms slot, so frames there come late while the graphics card is busy.
- **Read Aloud:** Windows voices plus one natural voice, "Linda" (Piper's LJSpeech voice, MIT, public-domain recordings).
  - Kareem (Arabic) and Joe (English) were removed in 1.16.0 because they are fine-tuned from lessac, whose recordings are for research only. **Before adding any Piper voice, read its MODEL_CARD for its dataset and fine-tuning.**
- **Live Captions** (rebuilt 2026-11-11 after the user found them unusable: «يخبص كثير في الترجمات … لا يلتقط الكلام … يعلق عند جملة»):
  - **Parts:** `Engines.Media/Tools/StreamingTranscriber.cs` (sound, speech detection, Whisper passes), `StreamAssembler.cs` (the rules, tested without a model), `SpeechLevel.cs`; `Recipes/Translation/LiveInterpreter.cs` (the two lines) with `GemmaTranslation.SoFarAsync`; `App/Words/LiveCaptions.cs` (capture and the window only).
  - **What was wrong, measured** on FLEURS talks (the same 16 sentences read aloud in all eight languages; `D:\.dropwheel-sdk\live`, see the memory note "live-captions-test-bench"):
    - A loudness gate (rms above 0.006) decided what was speech: a video played quietly or a soft voice was thrown away whole. Clean English came out with 64.8 % of its words wrong, most of them missing.
    - Captions used dictation's model (Whisper small by default). Read a sentence at a time on the RTX 4060, words wrong: turbo en 3.7, ar 12.0, tr 3.9, fa 22.9, ur 17.7, id 5.2, fr 5.7, es 2.4 %; small 4.1, 29.0, 14.3, 72.9, 34.3, 20.5, 14.4, 4.5 %; base 6.8, 39.8, 24.2, 83.7, 48.9, 39.0, 31.2, 10.0 %. A pass takes 0.5 s (turbo), 0.35 s (small), 0.2 s (base) on the card; 20 s, 4.1 s, 1.2 s on the i5-12500H alone. Beam search (5) gained nothing.
    - The second line (the sentence being said) had been taken out in `cd33861`: only finished sentences were translated.
  - **Speech:** Silero VAD 6.2 (MIT; `ModelCatalog.VoiceFinder`, a bundled 0.9 MB ggml file fetched by fetch-tools.ps1, run by Whisper.net's `WhisperVadProcessor` on the processor, 30 ms for 15 s) tells speech from music and quiet. It hears nothing in quiet speech either, so the sound is levelled first (`SpeechLevel.Level`: each tenth of a second raised by what the loudest within 0.4 s needs, the quiet between words by no more than four times its own level). Whisper is given the levelled sound too.
  - **No sound is dropped to catch up.** A Whisper pass costs the same for 2 s or 20 s (it always reads 30 s), so a slow PC shows words later and never fewer. The old engine cut the window on a time-out and lost what was in it.
  - **Assembly:** the held sound always starts at the sentence being said, and every pass reads that sentence whole. What the last two readings agree on (matched by edit distance, not by position) is settled; the rest is the draft; the last word is never settled while the talk goes on. A word heard wrongly at first ("quip", then "equipped") is put right by later readings and can't stand twice. A sentence ends at a full stop that two readings put there (not "U.S." or "Dr.", not before a small letter), at 0.6 s of quiet, at a breath of 0.4 s in talk without full stops (Whisper writes none for Urdu, few for Arabic), or at 30 words. Its sound is cut a pass later, at the quietest moment near where the next pass hears it end: at the edge of a pass a word's time can be 0.3 s out, which is what dropped words like "over" before.
  - **Whisper's traps:** under a second of sound it can write to its token limit for 7 s (so the first pass waits for 1.1 s of sound, and a pass is cancelled at six times the usual length, with nothing lost); under 1 s whisper.cpp reads nothing (padded to 1.2 s); a long quiet after the words isn't read (it invents there); words placed where the VAD heard nobody are dropped; subtitle credits ("ترجمة نانسي قنقر", "Altyazı M.K.", Amara.org) are dropped.
  - **Model:** with a graphics card (the registry names NVIDIA, Radeon RX/Pro or Arc) captions use the most accurate speech model here and offer turbo once when it is missing; `KeepUpAsync` changes to a quicker one through `UseModelAsync`, which keeps the held sound and words. "Detect automatically" chooses among `Transcriber.Languages`, and asks again after every pause (0.8 sure or more changes it), so another video in another language is followed.
  - **Two lines** (the user's design: «الأول … الجملة المنتهية والكاملة بترجمتها الدقيقة، الثاني … ترجمة فورية بالكلمة فورا … ما إن تنتهي الجملة تنتقل إلى السطر الأول فورا»):
    - Second line: `GemmaTranslation.SoFarAsync`, an interpreter's prompt ("stop where the words stop") with the model's turn already holding the words two translations in a row agreed on, so they can't change on screen and only new words cost time; llama-server's prompt cache makes a call 0.15 to 0.5 s on the card. It starts over when Whisper changes an early word, and stops at the sentence's own full stop.
    - First line: the moment the sentence ends the second line's text moves up (`LiveLine.Proper` false), then `TranslateAsync(live: true)` with the sentence before, proofread on a card, replaces it about 1.1 s later. Finished sentences come before the quick lane and go as one when several have gathered; the same words final twice are shown once. A short sentence stays beside the next while it hasn't been up long enough to read.
    - With a cloud key, finished sentences go to the cloud and the second line stays on this PC's model (each cloud request is billed).
    - **Nothing jumps** (2026-11-15, the user: «انتقال الترجمة … إلى السطر الجاهز يجب أن يكون أسلس ولا ينتقل فجأة»): `App/Words/CaptionShades.cs` keeps each word's colour and since when, matched to the line before by their longest run in common (a full stop gained doesn't make a new word); a word that stays keeps going, a new one fades in from nothing over 320 ms, a word that changes shade (newest → settled → first line) goes from where it stood. A line is written again only when its words change. A sentence moving up rises from the second line into the first in 380 ms, growing to its size, its words starting with the colours they had, while the one it replaces rolls up out of the first line (clipped there) in 220 ms. The proper translation then changes only the words that differ. `Petal.exe --captions-rise <out> --lang en|ar` draws the window every 40 ms through both moments (rise.png, proper.png): check by eye after any change. Pictures (`Show`) set the shades still.
    - **Fewer words change when the proper translation comes, without losing accuracy (small models):** `SmallSoFar.Final` writes the sentence afresh and also carries on from the words shown, and keeps the shown ones unless the model finds them less likely by more than 0.2 (mean log chance of a piece, `PairModel.Likelihood`). Measured (16 FLEURS sentences, three words at a time; chrF and words changed, afresh / kept): en→ar 62.3, 27 % / 62.7, 10 %; en→tr 68.4, 10 % / 68.3, 4 %; ar→en 70.9, 12 % / 71.5, 0 %; ar→tr 57.5, 15 % / 57.0, 13 %; tr→en 74.6, 18 % / 74.8, 16 %; tr→ar 58.2, 28 % / 58.0, 23 %. Within 0.4 it lost half a point. This needed `Settled` to cut before a full stop the model joined to the last word (`PairModel.Mark`); before, only en→ar ever kept anything.
    - **On the card it stays afresh:** carrying on from the shown words and proofreading changed a third as many words and took 0.75 s instead of 1.3, but lost chrF on every pair (en→ar 66.0 to 62.5, en→tr 68.3 to 66.2, tr→ar 59.1 to 56.8, ar→tr 58.0 to 57.0, tr→en 77.1 to 76.3, ar→en 73.9 to 73.8), so the window's fade does the smoothing there. Live, 12–37 % of the first line's words change at that moment, most of them putting right what the second line built on Whisper's draft (e.g. "عجّل" for "تعجب").
  - **Measured after, whole pipeline live on the RTX 4060** (words wrong / chrF of the first line against FLEURS / first line right after the sentence was over): en→ar 3.7 % / 64.8 / 1.2 s; ar→en 8.3 % / 71.6 / 1.1 s; tr→ar 3.0 % / 56.7 / 1.3 s; fa→en 20.9 % / 66.7 / 1.2 s; ur→ar 19.4 % / 52.5 / 1.6 s; id→en 5.6 % / 74.3 / 1.2 s; fr→ar 7.2 % / 59.9 / 1.5 s; es→tr 2.1 % / 59.5 / 1.3 s. Talk with hardly a pause: en→ar 4.1 % / 63.4, ar→en 9.1 % / 71.6 and tr→en 3.0 % / 73.2 with the language left to be told. No sentence lost and none said twice in any run; a pass 0.6 to 0.7 s, the newest word 0.65 s behind the talk, the first line as it stood 0.1 to 0.4 s after the sentence was final. On the processor alone (Whisper small, short listening): 3.7 % of English words wrong, a pass 1.7 s.
  - **Measuring again:** `tests/Petal.Tests/LiveCaptionsBench.cs` (PETAL_LIVE, PETAL_LIVE_SPEECH, PETAL_LIVE_TALK, PETAL_LIVE_FROM, PETAL_LIVE_INTO, PETAL_LIVE_MODELS, PETAL_LIVE_GAPS), or the app itself: `Petal.exe --captions-test <out> <wav> <models folder> both --lang ar`.
  - **On a processor alone** (`alignWords: false`, which the captions choose when the registry names no graphics card): a shorter encoder context (`WithAudioContextSize`: what there is to hear plus two seconds) makes small 1.7 s a pass on the i5-12500H instead of 4.1 s, with 3.7 % of English words wrong. whisper.cpp's aligned word times crash with it, so the times are Whisper's own estimates there and the two filters that need aligned times are off; words squeezed into no time at the end of a pass are dropped in both modes (Whisper small said a whole sentence over again in 0.4 s). At most 15 s of sound are held, and a sentence ends and is cut in one pass once three words follow its full stop, since passes are seconds apart there. `KeepUpAsync` goes to a quicker model only above 2.4 s a pass.
  - **Without a graphics card** (finished 2026-11-15): small models made for the two languages translate (`Recipes/Translation/PairModel.cs`, `SmallTranslation.cs`; files in `Core/Models/TranslationCatalog.cs`, pinned, "merged" decoders that carry on). Opus-MT for a pair (tc-big for en↔tr), two of them through English for a pair with none (ar↔tr, tr↔ar…), M2M100 for Persian and Urdu (Opus-MT does them poorly). Greedy (beam search gained nothing). Offered once per pair (`LiveCaptions.OfferTranslation`), 105 to 638 MB; Gemma isn't preloaded there. Whole pipeline on the i5-12500H alone (Whisper small, 4 translation threads), 2026-11-15: en→ar words wrong 3.7 %, second line 0.09 s after its words (about 1.4 s behind the speaker), first line properly 0.17 s after the sentence (worst 0.46), chrF 62.0; ar→en 27 % (Whisper small on Arabic), +0.15 s, 0.38 s, chrF 65.5; tr→ar 14 %, +0.36 s, 0.49 s, chrF 53.9; en→tr 4.1 %, +0.21 s, 0.26 s, chrF 65.8. "Settled words changed" (0–7 a talk) is the line starting over when Whisper re-hears an earlier word, which is right. Measure with `D:\.dropwheel-sdk\live\tools\nocard.sh <talk> <from> <into>` (sets `PETAL_NO_CARD=1`, which also puts Whisper on the processor), the route bench with `routes.sh`, the card with `card.sh`.
  - **Telling the translator the words were heard** (`heard: true`, "a word may be misheard") changed chrF by under a point either way on four pairs (fa→en 66.7 to 67.9, tr→ar 56.7 to 57.8, ar→en 71.6 to 71.0, ur→ar 52.5 to 52.4); it is on.
  - **A talk that changes language** (four sentences each of English, Arabic, Turkish, English): followed after one sentence at most. Two things were needed: asking again when Whisper was unsure of a pass (under 0.5; the wrong language scores 0.2 to 0.45, the right one 0.7 to 0.95), and forgetting the context on a change, because Whisper writes in the language of the words it is told came before, whatever language it is asked for.
  - **Over loud music** (the music 20 to 40 dB above half of the speakers): the sentences that can be heard come through right, and the buried ones give nothing or a fragment rather than invented talk. Words from long ago aren't given to Whisper as context after 8 s without speech: it read them back over the music.

- **Study File** (`ToolId.StudyFile`, wheel STUDY FILE, «ملف دراسي تفاعلي»; the user chose one standalone HTML file over a view inside DropWheel):
  - **Parts** (`Engines.Documents/DocumentOutline.cs`): a PDF by its bookmarks (two levels; a single bookmark holding all the rest is gone through; bookmarks that only name pages are ignored), else by the headings its type sizes show (`Levels`: chapters are the largest level that comes at least twice with ≥150 words each, sections the next level that comes as often or more with ≥80); a part begins at its heading's line on a shared page (`FindLine`: with or without its number, or broken over two lines). Word, ODT, RTF and PPT are laid out by Office or LibreOffice (heading bookmarks) into the study cache; without them, and for EPUB, Markdown, HTML and text, the DocModel's headings (places are the section's heading, no pages). PPTX: slides with speaker's notes, by PowerPoint's own sections (p14:sectionLst). A recording: Whisper through `AskFile.WordsAsync`, a passage a part.
  - **Plan** (`Recipes/Study/StudyFile.cs`): sections under 700 characters join a neighbour in their chapter; passages ≤2800 characters on this PC (the server's context is 4096), 9000 on a key. `Worth` unticks the cover before the first heading, contents, preface, index, references, glossary, key terms, chapter outline/summary and review questions (common headings in the eight languages).
  - **The model** writes a tagged reply (TITLE, IDEAS, EXPLANATION, EXAMPLE, TERMS, CARDS, QUIZ with RIGHT and WHY); `Read` copes with ** and ## decorations, "term: meaning", "question - answer", "question? answer", choices A–D or أ ب ج د or الف. A reply in the wrong language or script (`InLanguage`), or without an explanation, is asked again once at temperature 0.5; never right: the passage's own words are shown. Terms the passage doesn't use are dropped, and the same term again (plural, abbreviation in brackets) is one (`TermKey`). Choices are shuffled by a hash of the question (the model put the right one at C 29 times out of 41). Items point at the page that says them, with the file's sentence when three words or more match (same language only).
  - **Kept** in `%LOCALAPPDATA%\Petal\cache\study\<sha>.txt`, keyed by the writer, the instruction and the passage: a book stopped half way carries on; the window shows how many are made.
  - **The page** (`StudyFileWriter.cs` + embedded `StudyFile.css`/`StudyFile.js`): everything inline; a Content-Security-Policy that allows only the page's own script by its sha256 and no address at all; readable and printable without its script (answers in `<details>`); overview, chapters, parts (key ideas from every passage, explanation, worked example, terms in the part, questions, cards, mark as studied), glossary, cards one by one (known / not yet), a test on a chapter or the whole file, progress in localStorage keyed by the file's id and each item's hash; search folds Arabic letters and accents; terms marked once a part with Arabic's joined little words (و ف ب ك ل، ال). Right to left for ar/fa/ur, Latin runs isolated with `<bdi>`, page ranges held by left-to-right marks around the dash (`StudyFileWindow.Place`). Its words come from the eight tables in the file's language through `L.In`/`L.P(…, code)` (`StudyFileWindow.Words`), not the interface's.
  - **Measured** (Gemma 4 E4B on the RTX 4060): OpenStax *Concepts of Biology* (612 pages, CC BY-NC-SA, downloaded for testing only): structure read in 7.9 s, 195 parts (86 ticked, 622 passages: about 3.8 hours for the whole book on the card). Chapter 3 in English, 36 passages in 13 min: 41 questions, 40 with one right answer (one had two: fungi and protists); explanations faithful and simpler, a few sentences about the passage itself ("this section introduces…") and one small unsupported claim in three passages read, which led to the rule against writing about the passage; one chapter summary repeated a confused key idea. Made again with that rule: 45 questions, 43 clean (two with two defensible answers), the passage named in explanations 2 times instead of 9 and in key ideas none instead of 5; the model still put the right answer at C 26 times in 45, which the shuffle spreads (A 9, B 11, C 11, D 14). Chapter 5 into Arabic, 15 passages in 6.8 min: natural Arabic with the English terms in brackets; 17 questions, 14 clean, one with two defensible answers (stroma/chloroplast), one term mistranslated («الكائنات الأولية» for prokaryotes), one awkward.
  - **A recorded lecture** (MIT OCW 7.012, 51 min of audio, CC BY-NC-SA, test only): heard by Whisper turbo with word times in 3.0 min, explained in 3.6 min: 15 parts the model titled in the lecture's order, each with its minutes, 41 terms, 59 cards, 17 questions all right with one answer each, each pointing at its minute with the lecturer's own sentence. The lecturer misspoke the stop codons and was put right by the class; the card has the three he ended with. Cards written "question? answer" were lost until `Pair` learned to part them at the question mark.
  - **Measure again** with the gated test `StudyFileTests.The_real_model_makes_a_study_file` (PETAL_STUDYFILE, PETAL_STUDYFILE_PARTS "17-22", PETAL_STUDYFILE_INTO, PETAL_STUDYFILE_OUT, PETAL_STUDYFILE_PURPOSE, PETAL_STUDYFILE_SCENES, PETAL_STUDYFILE_REQUEST).
  - **What it is for** (2026-11-23, the user: «دع المستخدم هو يحدد بحرية تامة»): `StudyPurpose` Study, Teach, Explain, Present, Brief, Children, Explore, chosen in the window above the request. Each gives the model its own part and reader (`StudyPurposes.Role/Reader`); only Study, Teach and Children ask for cards and questions and have the meter, Mark as studied, the how-to and Progress (`StudyPurposes.Studies`; the page's body gets `free` otherwise). Study's instructions are byte for byte as before, so its cached passages stay.
  - **Things to try by hand** (`Recipes/Study/StudyScene.cs`, a third request per passage, `StudyScenes` None/Some/Many): LAB (things on a shelf, MIX lines with what is seen, why, an effect and a colour: a flask the page fills, fizzes, smokes, glows, heats…), SIMULATE (sliders, RESULT formulas, levels: readouts, meters, a curve with Play), SYSTEM (RAISES/LOWERS links: press ▲▼ on a part and the push runs along the arrows, loops named), SCENARIO (situations, GOOD/OK/BAD choices), ESTIMATE (a slider guess, the answer slides in). Nothing the model writes is run: a formula is read by `SceneFormula` (C#) and the page's own parser, kept only when it works out at the start and moves with a slider. The reader forgives a small model's habits (a kind under several headings, things named only in a mix, titles copied from the instruction, "8 minutes and 20 seconds" refused as a number). Without the script, and in print, each scene is a list. Try the page by hand from PETAL_STUDYFILE_PAGE's explore-en/ar.html; the bench text is `D:\.dropwheel-sdk\study\scenes\science-sampler.md` (our own).
  - Gemma 4 E4B on the sampler (5 short sections): a lab of 8 things and 4 reactions, a 7-link water cycle, choking situations, Solar System numbers; its one formula was wrong (distance × speed) and was dropped because it never moved. The 12B model (when downloaded) or an API key does better.

## 8. GitHub Releases upload (paused, unfinished)

- **Uploaded so far:** v1.0.0-build.1 to v1.0.0-build.4 (prereleases), v1.0.0, v1.1.0, v1.2.0 and v1.3.0, each complete; v1.3.1 has only its zip.
- **Left:** v1.3.1 (setup + update.json), v1.3.2, v1.4.0, v1.5.0–v1.5.2, v1.6.0, v1.7.0, v1.8.0–v1.8.4 (with arm64 zip/setup), v1.9.0–v1.16.0.
- **Each release:**
  - tag `vX.Y.Z` on the rewritten commit (map old to new with filter-repo's commit-map; 1.16.0 is `3e5569e`);
  - title "Petal X.Y.Z" or "DropWheel X.Y.Z";
  - assets: the zip, the setup, and the update.json / update-arm64.json that offered that build;
  - notes: the update.json notes, then the commit subject and body, then "Built from commit <new> (originally <old7> before the build files were taken out of history)".
  - Latest = v1.16.0. Builds of 1.0.0 before the last are `v1.0.0-build.N` prereleases.
- **The scripts were in the old session's scratchpad** (`gh_assets.py` extracts by commit, `gh_releases.py` uploads and is resumable); they are gone with it, so rewrite them if needed.
- **Integrity first:** the D: disk returns corrupted reads.
  - Before uploading any file, check it against its git blob ID: SHA-1 of `"blob <size>\0" + content`, recorded in manifest.json and in the bundle's history.
  - Re-check the 8 releases already uploaded against GitHub's asset digest.
  - If D: is replaced, the bundle may be the only source. Run `git bundle verify` first, then clone it elsewhere, then extract.

## 9. In progress: FFmpeg and images with no GPL and no patented codec

**Decision (user, 2026-11-03), "All of it":**
- a custom LGPL FFmpeg holding no implementation of any patent-pool codec;
- Windows (Media Foundation) decodes and encodes H.264, HEVC and AAC;
- a custom Magick.Native without libheif's HEVC/AVC parts (libde265, openh264, x265), with HEIC through Windows (WIC);
- rubberband (GPL) replaced by atempo;
- the user accepted that HEVC/HEIC need Windows' HEVC Video Extensions, as the Photos app does.

The user ruled out the "Encoders only" option (BtbN's LGPL FFmpeg as-is).

**Research facts** (from a research agent, 2026-11-03; a lawyer gives the final word):
- BtbN's lgpl builds drop x264, x265, xvid, vidstab, rubberband, frei0r and similar. But they **still contain** FFmpeg's own H.264/HEVC/VVC/AAC decoders, the native AAC encoder, openh264, kvazaar, vvenc, opencore-amr and lcevc. Hence the custom build.
- **FFmpeg's Media Foundation encoders** (libavcodec/mfenc.c):
  - Encoders: h264_mf, hevc_mf, av1_mf, aac_mf, ac3_mf, mp3_mf.
  - `rate_control`: default, cbr, pc_vbr, u_vbr, quality, ld_vbr, g_vbr, gld_vbr. `quality` 0–100 (MS default 70 ≈ QP 24); `scenario`; `hw_encoding` 0/1.
  - **H.264 defaults to Baseline: pass `-profile:v 100` for High** (the text "high" fails to parse).
  - Input: nv12 or yuv420p, even sizes. HEVC: Main 8-bit only, and only CBR or quality.
  - Maximum resolution and 4K60 are undocumented, so test them.
  - FFmpeg 8.1 doesn't set frame size or rate on the input type, which GPU-vendor encoders reject. This is fixed in **9.0**, hence n9.0.2.
  - Windows N editions lack mfplat without the Media Feature Pack.
- **aac_mf:** AAC-LC only, s16 input, **44.1 or 48 kHz**, 1, 2 or 6 channels, **96/128/160/192 kbps** only. Every input sample needs a duration.
- **Patents:**
  - H.264 (Via LA): last US patent 2030-11-26; units 1–100k a year free, but a licence must still be signed.
  - HEVC (Access Advance plus the former Via LA pool, now Access Advance): no free tier.
  - AAC (Via LA): still licensed.
  - Expired: MP3 (2017), AC-3 (2017), MPEG-2 (2018), MPEG-4 Part 2 (2026-07-19).
  - VC-1 pool still active. Apple calls FFmpeg's ProRes unauthorised.
  - Microsoft's Windows licence covers the AVC, VC-1 and MPEG-4 Part 2 codecs "for personal and non-commercial use". **No explicit Microsoft statement covers apps calling Media Foundation**, though Firefox and others rely on it.
  - D3D11VA decoding still runs FFmpeg's own H.264 decoder code, so it is not a way around.

**Custom FFmpeg (repository `dropwheel-ffmpeg`, files `build.sh`, `.github/workflows/build.yml`, `README.md`):**
- **Source:** FFmpeg tag `n9.0.2` (commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8`), compiled inside BtbN's public image `ghcr.io/btbn/ffmpeg-builds/win64-lgpl-shared-9.0:latest`, which holds the prebuilt libraries. Only FFmpeg itself is configured.
- **Licence:** LGPL **v2.1+** (no `--enable-version3`; no gmp, aribb24, opencore-amr or mbedtls). `--disable-autodetect`, pthreads.
- **Enabled libraries:** mediafoundation, zlib, lzma, iconv, libxml2, dav1d, aom, svtav1, vpx, webp, mp3lame, opus, vorbis, soxr, zimg, libass, freetype, harfbuzz, fribidi, fontconfig.
- **Allow-lists:** `--disable-decoders/encoders/parsers/bsfs` with explicit enables, plus `--disable-hwaccels --disable-outdevs --disable-indevs --enable-indev=lavfi`. Every name was checked against n9.0.2's source.
  - **Decoders:**
    - video: av1, libdav1d, vp8, vp9, theora, vp3, mpeg1/2video, mpeg4, h261, h263(i/p), flv, mjpeg(b), jpeg2000, jpegls, png, apng, gif, webp, bmp, tiff, pnm family, pcx, targa, sgi, qoi, hdr, dpx, ffv1, ffvhuff, huffyuv, utvideo, rawvideo, v210, yuv4, qtrle, msrle, msvideo1, cinepak, dirac, snow;
    - audio: mp1/2/3 (+float), flac, alac, opus, vorbis, ac3(+fixed), ape, tta, wavpack, gsm(+ms), all pcm_*, adpcm ima_wav/ima_qt/ms/yamaha/swf/g726(le);
    - subtitles: ass, ssa, srt, subrip, movtext, webvtt, text, microdvd, subviewer(1), sami, realtext, mpl2, pjs, jacosub, vplayer, stl, dvdsub, dvbsub, pgssub, xsub, ccaption.
  - **Encoders:** h264_mf, hevc_mf, aac_mf, libaom_av1, libsvtav1, libvpx_vp8, libvpx_vp9, mpeg4, mjpeg, png, apng, gif, libwebp(+anim), ffv1, utvideo, rawvideo, bmp, tiff, wrapped_avframe (needed by `-f null`), libmp3lame, libopus, opus, libvorbis, flac, alac, pcm_*, ass, ssa, srt, subrip, movtext, webvtt, text.
  - **Parsers:** ac3, av1, dirac, dvbsub, dvdsub, flac, gif, h261, h263, jpeg2000, mjpeg, mpeg4video, mpegaudio, mpegvideo, opus, png, pnm, vorbis, vp3, vp8, vp9, webp, bmp, qoi,
    and (since 2026-11-11) h264, hevc, aac, dca, mlp, tak, evc and apv, which FFmpeg's demuxers of those streams ask for.
    A parser only finds where each frame begins and reads headers, so a stream can be copied untouched; none decodes.
  - **Bitstream filters:** aac_adtstoasc (header conversion for AAC stream copy), chomp, dump_extradata, null, setts, pgs_frame_merge, mpeg4_unpack_bframes, text2movsub, mov2textsub, vp9_*, av1_*,
    and the ones muxers ask for: extract_extradata, h264/hevc/vvc_mp4toannexb, evc_frame_merge, eia608_to_smpte436m,
    pcm_rechunk. They only rewrite headers or split and join packets. filter_units is left out.
  - **Decoders also hold** `wrapped_avframe` (added after run 42967588007): what `-f lavfi` sources hand on; without
    it the graphics card's H.264 probe, the HEIC encoder's probe and the tests' samples fail.
- **Output** (workflow artifact `ffmpeg-win64`; a release when run with `release: true`): `ffmpeg-n9.0.2-dropwheel-win64.zip` (exe, DLLs, LICENSE.txt, configuration.txt, libraries.txt, BUILD.txt) plus `ffmpeg-n9.0.2-source.tar.gz`, needed for LGPL compliance.
- **winarm64 is not built yet.** BtbN's arm64 image lacks libvpx and libaom, so its flags must differ.
- **To do once it finishes:**
  - Check `ffmpeg -decoders/-encoders/-parsers/-bsfs` lists nothing patented.
  - Check that copying H.264 between MP4 and MKV still works without the h264 parser.
  - Then make `fetch-tools.ps1` take this build: SHA-256 pinned, from the build repo's release.

**Where FFmpeg runs today** (inventory, 2026-11-03; M = src/Petal.Engines.Media, A = src/Petal.App):
- **Shared runners:**
  - `M/Internal/FfmpegProcess.cs` (RunAsync, RunReadingStdoutAsync) and `M/Internal/Ffmpeg.cs` (RunAsync, EncodeAsync).
  - `M/Internal/MediaEncoder.cs` (BuildArgs, an NVENC→libx264 fallback, two-pass, the GIF palette).
  - `M/Internal/Containers.cs` (container specs, VideoEncoderArgs, AudioEncoderArgs).
- **Own processes:** M/Tools/Reframer.cs:106, M/Tools/SpeakerFraming.cs:60, A/Screen/RecordingPolish.cs:180–190, A/Screen/ScreenRecorder.cs:234/529/561, A/Screen/CameraBubble.cs:395, A/Screen/Studio/StudioExport.cs:252, A/Screen/Studio/StudioWindow.cs:281, A/Words/MeetingRecorder.cs:185.
  - Also src/Petal.Engines.Imaging/Codecs/FfmpegStillEncoder.cs via ProcessRunner (HEIC via libx265; AVIF via libaom/svtav1).
- **libx264 / aac / rubberband in use:**
  - Containers.cs:128–142 (h264_nvenc, libx264, `-crf`, `-preset`, `-profile:v high`), :176 (aac), :159/:185 (wmv2/wmav2);
  - AudioTools.cs:222 (`-tune`); DubTrack.cs:218 (**rubberband, no fallback**), :282 (libx264), :287 (aac);
  - VideoTools.cs:292/294 (Join forces H.264/AAC), :441–442 (rubberband if probed);
  - ScreenRecorder.cs:244 (libx264 to video.mkv), :559 (aac); CameraBubble.cs:399; RecordingPolish.cs:194;
  - StudioExport.cs:152–153; StudioWindow.cs:282; MeetingRecorder.cs:135 (m4a aac);
  - MediaCapabilities.cs:21 (nvenc probe), :28 (rubberband probe);
  - FfmpegStillEncoder.cs:14 (" libx265 " check), :42.
- **Default encoders by container** (Containers.cs:41–69):
  - mp4/mov/mkv: H.264 + AAC; m4a and .aac: AAC; wmv: wmv2 + wmav2; wma: wmav2; avi: mpeg4 (xvid tag) + mp3.
  - webm (VP9 + Opus), mp3, wav, aiff, flac, ogg, opus and gif are patent-free already.
- **Our own recordings are H.264 and get decoded later** (RecordingPolish, StudioExport, the studio waveform). They need either a royalty-free intermediate or the Windows decoder.
- **Images:**
  - Magick.NET reads HEIC/AVIF through libheif: `Engines.Imaging/Codecs/ImageLoader.cs:61`, `Common/ImageFormats.cs:23/42/95–103`, `ImagePreview.cs:28–63`.
  - Our HEIC writer: `Codecs/Heif/HeifWriter.cs` + HevcBitstream + HeifMuxer, fed by libx265.
  - `A/Tools/Parts/ImageLoader.cs:157` saves MagickFormat.Heic directly, which can't work since Magick has no HEVC encoder.
  - WIC is already used in a few places: Engines.Documents/Util/ImageTools.cs, FileActionWindows.cs:262 (HEIC dates through WPF).
- **Already decoded by Windows:** WPF MediaElement previews, MediaPlayer, Windows.Media (camera, faces, OCR, speech). NAudio is used only for capture, playback and WAV.
- **Tests tied to codecs:** tests/Petal.Engines.Media.Tests/Fixtures.cs (makes the libx264/aac/wmv2/libx265 fixtures), ConverterTests, AudioToolTests, VideoToolTests, VoiceCleanerTests, DubbingTests, TranscribeTests, TextCut/ShortClip/AnimatedCaptions tests, Petal.Tests StudioRenderTests/PolishTests/DuplicateTests, and Imaging HeicTests/ConversionTests/ToolTests/PhotoEditTests/MetadataTests.

**Done for 1.20.0 (2026-11-11):**
- `dropwheel-ffmpeg` builds every library itself from FFmpeg-Builds' stage scripts at commit `c9fe511`, trimmed to the
  libraries named in `build.sh`; `collect.py` gathers each library's licence files and the LGPL sources (LAME,
  soxr, FriBidi, libiconv). `--enable-d3d11va` (mfenc needs D3D11 types), `--disable-network`, no Audible
  demuxers, `-liconv` named at the link. Each build is a zip + `-source.tar.gz`; `fetch-tools.ps1` pins them by
  SHA-256 (from the private repo through `gh release download`). build.ps1 ships `licenses\ffmpeg\` and
  `licenses\sources\`.
- **The Windows bridge** (`src/Petal.Engines.Media/Foundation`): `WindowsReader` (Media Foundation Source Reader,
  NV12/P010 and float), `WindowsBridge` (Matroska over a named pipe; P010 becomes yuv420p10le; AAC lead-in moved by
  the first packet's time, as FFmpeg skips it and Windows doesn't), `FfmpegCommand` (rewrites `-map`s, filter
  labels and FFmpeg's own stream choice; `-ss/-t/-to` go into the bridge), `FfmpegRun` for code that starts
  FFmpeg itself. `FfmpegProcess` prepares every FFmpeg command. `WindowsMedia.ForceWindows(...)` lets tests send
  a format FFmpeg could decode through Windows (WindowsBridgeTests: pictures equal to the pixel, sound aligned).
- **Encoders**: `MediaEncoders.H264` (h264_mf, High profile, quality from a CRF by an SSIM-measured table,
  software `-compression_level 100`, `fast` for live recording) and `Aac` (aac_mf: 96–192 kbps, 1/2/6 channels,
  44.1/48 kHz). Graphics card first where asked (`HasHardwareH264Async` probes h264_mf `-hw_encoding 1`). No
  two-pass for H.264: Compress re-encodes once when one pass lands >5 % over. Rubber Band gone (atempo).
- **What the first real run of the bridge taught** (all in `Foundation/`, with tests in WindowsBridgeTests):
  - Windows doesn't keep a file's order of streams: an MP4's or MOV's sound tracks come last first, before the
    picture (Matroska keeps its order), and its stream identifiers are its own count, not track IDs. Where a file has
    several streams of a kind, `FfmpegCommand.TellTaleAsync` takes 32 bytes from a packet only that stream holds
    (FFprobe `-show_data`), and `WindowsReader.Find` looks for them in each of Windows' streams, read undecoded by a
    second reader (a stream read undecoded can't be decoded after).
  - Without FFmpeg's decoder FFprobe has no pixel format, nor an HEVC profile: more than 8 bits is read from the
    profile ("High 10"), or for HEVC from the hvcC (byte 17, low 3 bits).
  - A cut leaves a few samples of the frame it falls in: FFmpeg 9's FLAC encoder takes its block size from the first
    frame and refuses 2. Sound shorter than 1024 frames is held and sent with what follows.
  - A time past the end gives no samples: an empty cluster after the header lets FFmpeg open the stream all the same.
  - "No Media Foundation" is said only when `MediaFoundationRuntime.Ensure` fails; any other error stays FFmpeg's.
- **FFmpeg's GPL-only filters are not in the LGPL build**: boxblur and eq were in use (Reframer, Make Shorts) and are
  now gblur and hue. Never use blackframe, boxblur, colormatrix, cover_rect, cropdetect, delogo, eq, find_rect, fspp,
  histeq, hqdn3d, interlace, kerndeint, mcdeint, mpdecimate, nnedi, owdenoise, perspective, phase, pp, pp7, pullup,
  repeatfields, sab, signature, smartblur, spp, stereo3d, tinterlace, uspp or vaguedenoiser.
- **Compress to a size** (`Internal/SizeFit.cs`): Windows' H.264 bitrate modes overshoot short videos and small
  sizes (1.2–19x on 4 s 720p clips; within 5 % only on long videos well above its floor), and its lowest quality has
  a floor (about 0.03 bits a pixel at 720p30). Its quality setting gives the same size every run, and six 3 s windows
  predicted a 48 s video within 0.2 %. So the setting is searched for (on the whole video up to 45 s), the picture
  shrinks when only a setting under 15 fits, and `SizeTooSmallException` tells how small the video can get.
- **WMV/WMA are read only** (CanWrite false; ConversionMatrix 350 paths). Tools write MP4/M4A for them
  (`OutputNaming.WritableExtensionOf`); a fast cut copies only into a container that can hold the streams.
- **HEIC**: read by WIC (`WindowsHeif`, metadata by `HeifMetadata`), written with hevc_mf (graphics card, then
  software) into our own HEIF; BT.709 full range (Windows' encoder writes no matrix), at least 256 px coded.
- Test samples that can't be made any more (WMV, WMA, HEVC) are in `tests/Petal.Engines.Media.Tests/Samples`.
- Notices, terms (they replace the Store's standard terms, as Partner Center does when the field is filled,
  and allow what the LGPL asks), privacy policy and Settings' FFmpeg line are updated.

**Still to do:** Magick.Native without libde265/openh264 (files ready in `D:\.dropwheel-sdk\repos\dropwheel-magick`;
publishing a workflow needs the `workflow` scope, `gh auth refresh -h github.com -s workflow`, which only the user
can grant); WMV/WMA writing through a Media Foundation Sink Writer if wanted; the support e-mail in the store texts.

**Planned design** (written before the work; kept for its reasoning):
1. **Windows decoding bridge**, new code in Engines.Media:
   - Media Foundation Source Reader over COM interop (MFStartup, MFCreateSourceReaderFromURL, SetCurrentMediaType NV12 or P010 for video and Float for audio, ReadSample, SetCurrentPosition for seeking; crop to MF_MT_MINIMUM_DISPLAY_APERTURE; pass on MF_MT_VIDEO_ROTATION and the colour attributes).
   - It writes a streaming Matroska: V_UNCOMPRESSED with ColourSpace "NV12", A_PCM/FLOAT/IEEE, unknown-size Segment and Clusters, SimpleBlocks.
   - Delivery: a NamedPipeServerStream that FFmpeg reads with `-f matroska -i \\.\pipe\<name>`; rotation through FFmpeg 9's `-display_rotation`.
   - **Argument rewriter** at every launch point:
     - When an input file's video or audio codec isn't in our FFmpeg's decoder list and the command decodes it (not `-c copy`), keep the original input (for subtitles, cover art, chapters and copied streams) and append the bridge as a new last input.
     - Move that input's `-ss/-t/-to/-sseof` into the bridge (decode from the keyframe, drop to the exact start, timestamps relative to it).
     - Redirect `-map k:v…/k:a…/k:<index>` and `[k:v]`/`[k:a]` filter labels to the bridge input. Add explicit maps where the command relied on automatic selection.
     - If the bridge fails (e.g. no HEVC extension), raise a clear error instead of a short output. Fail fast before starting FFmpeg.
   - Check first, empirically, that FFmpeg's matroska demuxer reads V_UNCOMPRESSED NV12 from a pipe.
2. **Encoders:**
   - H.264: `h264_mf -profile:v 100 -rate_control quality -quality <from CRF, calibrate> -pix_fmt nv12`, with hardware tried first (`-hw_encoding 1`) and then software.
   - Target size: `pc_vbr`/`cbr` with `-b:v`; MF has no two-pass.
   - AAC: `aac_mf` with `-ar 48000 -sample_fmt s16`, the nearest of 96/128/160/192k, channels 1/2/6.
   - Remove NVENC, `-tune`, `-preset`, the libx265 checks and the rubberband probe and uses; atempo chains instead.
   - Recording intermediates: h264_mf, or a royalty-free fast codec. Measure 120 Hz capture.
   - WMV/WMA output: drop, or write through a Media Foundation Sink Writer. Undecided; tell the user.
3. **Images:**
   - HEIC read and write through WIC / Windows.Graphics.Imaging. This needs the HEIF and HEVC Video Extensions; give a clear message when they are missing.
   - A custom Magick.Native build (dlemstra/Magick.Native, private repo, GitHub Actions) without libde265, openh264 and x265, keeping libheif for AVIF with aom/dav1d. Or AVIF through FFmpeg.
4. **Tests:** rebuild the fixtures with h264_mf/aac_mf, update the codec expectations, and add bridge tests (seeking, multiple audio tracks, rotation, an HEVC 10-bit file, a missing extension).
5. **Notices and LGPL:**
   - Update THIRD-PARTY-NOTICES:
     - FFmpeg becomes LGPL-2.1+;
     - list its libraries and their licences (lame, fribidi, soxr and iconv are LGPL; svt-av1 is BSD-3-Clause-Clear plus the AOM patent licence);
     - offer FFmpeg's exact source;
     - follow FFmpeg's legal checklist: https://ffmpeg.org/legal.html (DLLs not renamed, no ban on reverse engineering in the EULA, credit in About).
   - Drop the GPL text if nothing GPL remains. Piper's espeak-ng is GPL and is downloaded from Piper's own release, not shipped in the exe.
   - Update the Settings › Helpers FFmpeg "Get" link: it points to gyan.dev GPL builds.
6. **Release** 1.17.0 to GitHub Releases, and update memory notes.

**Other licence items still open:**
- The Demucs ONNX (timcsy/demucs-web-onnx) states no licence (Meta's weights are MIT); re-export it ourselves, or accept it.
- The Store's policy 11.16 asks for an AI-features declaration in Partner Center; the user does that.

## 10. Licences already settled (1.16.0)

- **Notices:** `store/THIRD-PARTY-NOTICES.md` lists every program, library and model with copyright and licence, and holds the MIT, BSD-2, BSD-3 and ISC texts.
- **What build.ps1 ships:**
  - the notices, as `licenses\THIRD-PARTY-NOTICES.txt`;
  - `store/licenses/*.txt`;
  - every NuGet package's own LICENSE/NOTICE files, found through `Petal.deps.json`, into `licenses\packages\`;
  - FFmpeg.txt, ExifTool.txt, DirectML.txt and FFmpeg-version.txt.
- **In the app:** Settings › Helpers opens the notices ("Open-source licences", translated).
- **Opus-MT** is CC-BY 4.0 (ModelStore says so), credited with the note that Xenova's ONNX is quantized.
- **NuGet packages** are all permissive. Keep SixLabors.Fonts pinned at 1.0.1 (later versions changed licence).
- **Models and their licences:**
  - BiRefNet: MIT
  - Real-ESRGAN: BSD-3, shipped
  - Whisper: MIT
  - Qwen2.5 1.5B and Qwen3 4B: Apache-2.0
  - Gemma 4 12B: Apache-2.0
  - SmolVLM: Apache-2.0
  - e5-small: MIT
  - CLIP: MIT; multilingual CLIP: Apache-2.0
  - Demucs: MIT
  - TIGER: Apache-2.0
  - LaMa: Apache-2.0
  - Chatterbox: MIT
  - DeepFilterNet: MIT/Apache
  - Linda: MIT, public-domain data

## 11. Next step on the new computer

1. Install the .NET 10 SDK, Git, `gh` (sign in as yahyaabohashem332), PowerShell 7, Python 3 and Inno Setup 6.
2. Clone https://github.com/yahyaabohashem332/DropWheel.
3. Copy `testdata/` into it (from a backup, or the rescued copy on the old PC).
4. Copy `%USERPROFILE%\.petal\update-signing-key.pem` from the old PC.
5. Run `tools\scripts\fetch-tools.ps1` and a Release build of the solution.
6. Run `dotnet test tests\Petal.Tests -c Release` with PETAL_SETTINGS_DIR set, to confirm the baseline (753 passed on the old PC at 1.16.0).
7. Then check the `dropwheel-ffmpeg` Actions run (section 9) and continue the plan there, in order: verify the build, then the bridge, then the encoders, then the images, then notices and tests, then release.

## 12. Be careful

- **D: on the old PC** is a JMicron USB enclosure.
  - Logs show event 153 (IO retried, constantly), 129 (UASP reset), 51 and 157 (surprise removal).
  - It disconnected twice on 2026-11-03, and afterwards the repository's pack and the folders `dist/` and `tools/bin/exiftool` read back corrupted.
  - Don't trust files read from it without checking them. The user also keeps Android SDK, Blender projects, WSL and more there.
- **Paused and left behind on the old PC:**
  - `D:\plugins\tmp-filter-test` is a throwaway clone and can be deleted.
  - Releases upload: see section 8. Nothing else was running.
- **Rewritten history:** never force-push GitHub's master; the history there is the rewritten one.
- **gh_releases.py notes file:** it wrote `release-notes.md` into the asset folders. Write such files on a healthy disk instead.
- **Git maintenance:** an automatic git maintenance repack started during a fetch and ran beside a big job. Pass `-c gc.auto=0 -c maintenance.auto=false` for heavy git work.
- **`ToolLocator` (src/Petal.Core/Util) also finds an FFmpeg on PATH or from winget**, which may be a GPL build. The bundled one must win, and the bridge must not assume codecs that build lacks.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. When the answer is under a Markdown heading, `graphify section "<heading>"` prints that section's text, so the .md need not be opened. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.
