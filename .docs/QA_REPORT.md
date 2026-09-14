# QA report

Model-management amendment executed on 2026-09-05–06 (Asia/Hong_Kong). A source file, button, or successful compile alone is not counted as end-to-end acceptance. This task is **model management only**; the RC3 workflow results retained below are historical baseline evidence, not claims that every unrelated workflow was re-run on this build.

## 2026-09-14 High Accuracy TXT correction and mobile recheck

The Windows `High Accuracy` preset was corrected because the prior catalog incorrectly selected Whisper Large-v3-Turbo for both roles. The actual profile is now **TXT_ASR = Qwen3-ASR 0.6B INT8** (`qwen3-asr-0.6b-int8-2026-03-25`) and **SRT_ASR = Whisper Large-v3-Turbo Q5_0** (`whisper-large-v3-turbo-q5-0`). `High Accuracy` remains a preset label, never a model name. Stored legacy selections that contain the named preset are migrated at registry initialization to these exact IDs; Qwen is not treated as installed until it has passed normal verification.

The apparent inability to remove Whisper Large in the reported Windows screen was therefore expected safety behavior: it was the active TXT and SRT model. The manager now identifies the exact active role(s) in the refusal and tells the user to switch that role to another installed compatible model first. It does not delete active/in-use files or registry state. Android retains its already-correct High preset and exposes the concrete roles independently: **LIVE_ASR = SenseVoice INT8**, **QUALITY_ASR = Qwen3-ASR 0.6B INT8**, **MEETING_LLM = Qwen3 0.6B Q8_0**. Its active-model delete action is disabled with the same role-specific explanation; non-active models continue to use atomic delete-and-metadata rollback.

| Check | Status | Actual evidence |
|---|---|---|
| Windows High mapping/migration | PASS | Shared catalog and `ModelRegistry.InitializeAsync` were checked by the Windows harness: High TXT resolves to Qwen and High SRT resolves to Whisper Large; an old stored `High Accuracy` Large/Large selection migrated to Qwen/Large. The actual RC5 portable directory then loaded Qwen by its selected real model ID through the packaged C ABI and returned nonempty Cantonese output. |
| Windows delete guard | PASS | The role-aware active-model refusal is covered by the registry and UI-handler checks. The production-directory native harness deleted a non-active Qwen copy atomically, removed its installed metadata, and reclaimed exactly 987,015,511 bytes. |
| Android High preset/UI | **EMULATOR PASS** | A fresh install of the new ARM64 release APK on `PixelQuickCut_API35` showed the three independently selected real IDs. Applying High visibly changed Quality to `qwen3-asr-0.6b-int8-2026-03-25` and Meeting LLM to `qwen3-0.6b-q8-0` while retaining Live SenseVoice. **PHYSICAL DEVICE NOT TESTED.** |
| Android uninstall guard | PASS (unit/integration) | Flutter registry tests verify role-specific refusal for active `LIVE_ASR／QUALITY_ASR`, atomic non-active deletion/storage reclamation, and rollback on metadata or deletion failure. No new emulator model download was performed in this fresh-install UI check. |

Checks for this correction: native CMake Release build/CTest **PASS 1/1**; Windows harness **PASS** including Qwen native load and deletion; Flutter `analyze` **PASS**; Flutter tests **PASS 15/15**; shared catalog validator and JSON Schema validation **PASS**. The Android emulator result is not evidence of physical microphone quality, thermal/battery behavior, or ARM64 performance.

## Current model-management acceptance

| Core item | Status | Actual evidence |
|---|---|---|
| CORE-MODEL-WIN-001 | PASS | Production WinUI manager exposes SenseVoice INT8, Qwen3-ASR 0.6B INT8, Whisper Base/Small/Large-v3-Turbo names, full IDs, roles, version, size, installed/active state. TXT and SRT are independently persisted. High Accuracy resolves to **Qwen TXT + Whisper Large SRT** and migrates legacy named-preset selections accordingly. A real-installer/native harness loaded selected TXT SenseVoice/Qwen/Base and SRT Small/Base through the actual RC5 C ABI; every run produced nonempty real Cantonese output and recorded loaded path/backend. Uninstall removed the entire non-active Qwen model directory and metadata, returned false installed state and reclaimed exactly 987,015,511 bytes; active refusal now names the specific TXT/SRT roles. |
| CORE-MODEL-AND-001 | PASS | **EMULATOR PASS:** API 35 Google APIs x86_64, 6144 MiB RAM, ARM64 AOT APK via `libndk_translation.so`. Settings → AI Models exposes all three independent roles and real names/IDs. Fresh RC5 install/application of High visibly sets Live SenseVoice, Quality Qwen3-ASR 0.6B INT8, and Meeting LLM Qwen3 0.6B Q8_0. Earlier actual native runs loaded both SenseVoice and Qwen ASR for both LIVE_ASR and QUALITY_ASR, then both Qwen Q4_K_M and Q8_0 LLM files with nonempty generation. Selected-role receipts were written only after successful FFI load acknowledgement. Non-active Qwen ASR and Q8 LLM directories/metadata were removed and reclaimed exactly 987,015,542 and 639,458,412 bytes. Registry reopen and cold-relaunch retained independent selections. **PHYSICAL DEVICE NOT TESTED:** no phone/tablet attached. |

Machine-readable, sanitized evidence: [MODEL_MANAGEMENT_EVIDENCE.json](MODEL_MANAGEMENT_EVIDENCE.json). No private recording, transcript, or model weight is committed. Windows and Android native QA use public upstream Cantonese PCM and isolated model copies; original host model caches remain untouched. Deleted QA models can be restored through their pinned downloads.

### Checks executed for this amendment

- Flutter tests: **PASS 15/15**, including independent role/preset persistence; selected/in-use uninstall refusal; real filesystem deletion and exact byte accounting; failed-delete rollback; failed selection-file writes; failed redownload preserving verified bytes; corrupt-model activation refusal; Quality ASR checkpoints only reused for the same model/revision; retained Chinese-script setting.
- `flutter analyze`: **PASS**, no issues. Existing hardware recommendation, first-setup output-script choice, and privacy disclosure are retained alongside explicit model selection.
- Windows tests: **PASS 12/12**, including registry role compatibility, selected/in-use guards, file deletion, storage reclamation, failed deletion/redownload/selection writes, and TXT/SRT cleanup regression.
- Native CMake/MSVC Release build and CTest: **PASS 1/1**. Existing sherpa-onnx `qwen3_asr` C API adapter is used; TXT Qwen has timestamps disabled. Whisper timestamp mode remains role-specific.
- Real Windows native harness: **PASS**, including successful native load, exact selected ID/revision/path, real ASR output, persisted roles, and physical file deletion. Final local evidence: `release/native-qa-059057d4a18441eaade8c5d9f64de27c/evidence.json` (ignored QA folder, never a release asset).
- Real Android alternate-entry-point harness: **EMULATOR PASS**, completed `2026-09-05T13:52:15Z`, retrieved again after emulator restart. Local raw evidence: `work/model-management/android-evidence.json`; on-device isolated root: `files/qa-model-management`. All four models passed the real installer's size/SHA verification before use. Files were preseeded into verified staging for repeatability; this is **not** new fresh-network-download evidence.
- Production Android UI: **EMULATOR PASS** for Settings → AI Models, separate ASR/LLM drop-down names, full role IDs, revision/size/storage and uninstall controls. Applying Standard changed Quality to Qwen while preserving Live SenseVoice and LLM Q4; the UI explicitly disabled uninstalled Quality until download. Switching Quality back to installed SenseVoice persisted `custom` without changing Live or LLM. Main product APK, not the QA entry point, was installed for these observations.
- Catalog validator: **PASS**, eight entries and three distinct Android preset bundles; JSON Schema validation **PASS**. Shared catalog is read from the actual APK Android asset through the existing method channel, not an omitted out-of-project Flutter asset.
- APK/WinUI production builds: **PASS**; the downloadable local artifacts below contain no model weights. The QA-only alternate entry point is not distributed. Windows localization directories are retained intact.

The two current model-management CORE items have no PARTIAL/FAIL/NOT TESTED implementation remainder. Android physical-device testing remains explicitly **PHYSICAL DEVICE NOT TESTED**, not an emulator-equivalent claim. Native LLM QA in this amendment intentionally generates a short nonempty token sample: it validates selected-model loading/invocation, **not** complete summary JSON, semantic fidelity, or comparative model quality. Qwen live ASR is offline-chunk inference, not a low-latency streaming claim. No new thermal, battery, acoustic-microphone, ARM64 performance, accuracy-ranking, or multi-hour physical-soak claim is made.

### Additional pinned models actually tested

| Model | Revision | Source and main-file SHA-256 | Local QA path / license |
|---|---|---|---|
| Qwen3-ASR 0.6B INT8 | `68818b2313fe77bd06f6a7c5068ff3ef59d02b8a` | Official `k2-fsa/sherpa-onnx` release `asr-models` dated 2026-03-25; archive SHA-256 `393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96`. Decoder SHA-256 `4f6885be5959ae26af3089d38ee7972c5fafbeeb1cf8d5e76eab6d8b61ca5771`; all six model/tokenizer files match pinned Hugging Face identities in the catalog. | `work/model-management/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25`; Apache-2.0 reference in catalog/notices. |
| Qwen3 0.6B Q8_0 | `23749fefcc72300e3a2ad315e1317431b06b590a` | Official `Qwen/Qwen3-0.6B-GGUF`; GGUF SHA-256 `9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031`, 639,446,688 bytes. | `work/model-management/Qwen3-0.6B-Q8_0.gguf`; Apache-2.0, downloaded license pinned in catalog. |

SenseVoice, Whisper Base/Small, and Qwen Q4 use the unchanged revisions/hashes in the integrity table below. Whisper Large-v3-Turbo remains selectable and was validated in the RC3 baseline; it was not used as a new comparative-accuracy benchmark in this amendment.

### Current local model-management test artifacts

- Android main-entry-point ARM64 release APK: `release/android-arm64-rc5/CantoMeet-arm64-release.apk`, 57,152,867 bytes; SHA-256 `8921ae45cc3d4aae4c101baf0084a706d094efc9614a67f5ea39c34a522d0855`. Development-signed and freshly installed on the emulator; physical Android validation is pending. Release manifest retains INTERNET, ACCESS_NETWORK_STATE and RECORD_AUDIO; all real ARM64 ASR/LLM libraries and the shared model catalog are included.
- Complete WinUI portable package: `release/CantoTranscribe-Windows-x64-v0.1.0-rc5.zip`, 263,789,099 bytes; SHA-256 `6312ac16716fa9cd90b23e03c3ae8da7d95b5f1f537aa16f70349d1e7b46c77d`. Its 466 ZIP files exactly match `release/windows-winui-x64-rc5/`; `THIRD_PARTY_NOTICES.md` is included and no `.onnx`, `.bin`, `.gguf`, or other model weight is present. Extract the complete ZIP and run its EXE; do not move only the EXE.
- Checksums: `release/Canto-Suite-v0.1.0-rc5-SHA256SUMS.txt`.
- Source branch: `master`, existing private remote `https://github.com/sion-rgb/canto-suite`. The correction source commit [`fb6a882`](https://github.com/sion-rgb/canto-suite/commit/fb6a882b575bc608b926b9e917cca13b1d7bf5d6) is tagged and published as the private pre-release [`v0.1.0-rc5`](https://github.com/sion-rgb/canto-suite/releases/tag/v0.1.0-rc5). GitHub reports uploaded APK/ZIP SHA-256 values matching the local checksum manifest. RC3 and RC4 remain historical.

## RC3 baseline core acceptance status (historical)

| Core item | Status | Evidence / blocker |
|---|---|---|
| CORE-WIN-001 Packaged Windows UI | PASS | Final WinUI 3 EXE launched from a copied portable directory and remained responsive. Publish contains zero filenames matching WebView/Vite/Node/Tauri and uses no localhost. |
| CORE-AND-001 Real microphone pipeline | PASS | **EMULATOR PASS:** API 35 x86_64 AVD, production ARM64 APK through Native Bridge, and `-allow-host-audio` carried nonzero host-microphone audio through `AudioRecord → 16 kHz PCM → EventChannel → Dart isolate → FFI → SenseVoice`; visible and persisted Cantonese transcript changed. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-002 Meeting persistence | PASS | **EMULATOR PASS:** three atomic M4A segments, live events, quality text, and meeting JSON persisted in SQLite; completed meeting remained visible and its four result tabs reopened after force-stop/cold relaunch. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-003 Quality transcription | PASS | **EMULATOR PASS:** Stop decoded three real saved M4A segments through MediaExtractor/MediaCodec and native SenseVoice; all reached `quality_complete`, with raw and cleaned text stored separately. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-004 Local meeting LLM | PASS | **EMULATOR PASS:** packaged llama.cpp loaded the explicit Android ARMv8 CPU backend and Qwen3 0.6B produced a parseable constrained `MeetingReport` locally after Stop; missing facts stayed empty rather than fabricated. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-005 Long-context summarization | PASS | Dart test forces multiple bounded chunks and fan-in consolidation; native GBNF constrains the final JSON shape. Whole-transcript prompting is not used. |
| CORE-AND-006 Crash recovery | PASS | **EMULATOR PASS:** force-stop during recording preserved the closed 29.995-second M4A, startup reinserted its missing `audio_saved` row, showed the recovery card, and renamed the unfinalized tail `.m4a.incomplete`; it did not expose a corrupt MP4 as playable. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-007 Model first-launch flow | PASS | **EMULATOR PASS:** fresh production-release install showed the microphone permission flow, downloaded both models over the AVD network, resumed `.part` bytes across force-stop and cold boot, fell back from sherpa-onnx GitHub to Hugging Face, hash-verified and atomically installed, then reopened Home without setup. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-008 Offline primary workflow | PASS | **EMULATOR PASS:** with airplane mode on, Wi-Fi disabled, and direct-IP ping returning `Network is unreachable`, host-mic recording, visible live transcript, Stop, 3/3 quality ASR, local Qwen JSON, result view, force-stop, and relaunch all completed. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-009 Android UI flow | PASS | **EMULATOR PASS:** traversed setup, permission, Home, recording, live transcript, processing, summary/tasks/transcript/info, output settings, recent meeting, and recovery UI on API 35. **PHYSICAL DEVICE NOT TESTED.** |
| CORE-AND-010 Release model download resilience | BLOCKED | **EMULATOR PASS:** production ARM64 release APK downloaded and verified SenseVoice and Qwen with bounded retries, Range resume, source fallback, matching hashes, atomic install, and preserved partial/install state. **PHYSICAL DEVICE NOT TESTED:** the required fresh normal-network ARM64-device download remains externally blocked because no phone/tablet is attached. |
| CORE-WIN-002 Model setup | PASS | All enabled Fast/Balanced/High models are size/SHA-pinned; automated test proves two bounded failures, source fallback, nonzero Range resume, SHA-256 and atomic staging. |
| CORE-WIN-003 Real TXT export | PASS | Final copied portable app + real upstream Cantonese WAV + active SenseVoice produced timestamp-free clean Hong Kong Traditional TXT. |
| CORE-WIN-004 Real SRT export | PASS | Final copied portable app + real WAV + whisper.cpp produced syntactically valid timestamped SRT through punctuation/length segmentation. |
| CORE-WIN-005 Offline runtime | BLOCKED | Runtime code audit finds HTTP only in model installers, and local inference succeeds without any server. An EXE-scoped outbound firewall rule still requires administrator/UAC for the literal network-disable test. |
| CORE-WIN-006 Long media bounded processing | PASS | A 12-minute duration-expanded real Cantonese WAV streamed through packaged FFmpeg into bounded 10-second native chunks. Fast completed 720,000 ms/72 ASR results with 488 MiB working set. During High CPU inference the WinUI window was captured, dragged, clicked and cancelled; reserving two logical processors removed the earlier capture timeout. |
| CORE-WIN-007 Resume | PASS | High job cancellation preserved 69,000 ms of model/revision-bound progress. UI acknowledgement was visible while native cleanup ran in the background; the prior 5-minute job also resumed above 41,344 ms and completed. |
| CORE-WIN-008 Packaging independence | PASS | Current 465-file self-contained publish was copied to `work/portable-hangfix4-20260903`; its EXE launched directly and ran real timestamp SRT. No source-tree or dev-server runtime dependency is present. |
| CORE-WIN-009 Model roles, profiles, and management | PASS | Historical profile/runtime validation: Fast/Balanced/High map to three distinct SHA-pinned bundles; shared Whisper weights run timestamps off for TXT and on for SRT. Explicit real-model management and uninstall are superseded and directly verified by CORE-MODEL-WIN-001 above. Base remains Fast SRT and is now also an explicit manual TXT choice. |
| CORE-WIN-UI-001 WinUI production UI | PASS | Production is WinUI 3/C# + C ABI/C++; final copied publish launched and real TXT/SRT paths pass. Tauri is legacy/reference only. |
| CORE-SHARED-001 Cantonese display | PASS | Automated preservation test passes for `嘅 喺 冇 咗 啲 嚟 噉`. |
| CORE-SHARED-002 Cantonese-English code switching | PASS | Ground-truth sample `我哋下個 sprint 會 update 個 API。` was processed and observed unchanged. This is preservation QA, not an accuracy claim. |
| CORE-SHARED-003 Custom dictionary | PASS | `union design` maps to `UNION Design HK`; automated test passes. |
| CORE-SHARED-004 Model integrity | PASS | Five QA models and the official SenseVoice archive are revision/size/SHA-256 pinned below and hash-verified. |
| CORE-SHARED-005 No private content upload | PASS | Source audit finds HTTP clients only in Android/Windows model installers. Audio, transcript, summary, speakers, ASR, LLM, and export have no upload endpoint. |
| CORE-SHARED-006 Clean Cantonese and Hong Kong Traditional | PASS | Final conversion runs after cleanup for both TXT and every SRT cue. Sanitized regression converts `后/个/柜/墙/这` to `後/個/櫃/牆/這`, preserves `我哋/佢哋/唔/冇/喺/嘅/啲/咗/嚟`, conservatively collapses repeated fillers/restarts, and deliberately leaves semantic error `開飛` unchanged. |

The RC3 baseline retained two external blockers: CORE-AND-010 requires a fresh physical ARM64-device network download, and CORE-WIN-005 requires an administrator/UAC-only outbound firewall test. This model-management-only task does not relabel those tests as passed or rerun unrelated workflows. Every Android runtime result independently states emulator versus physical-device status.

## RC3 baseline tests and builds (historical)

- Android tool inventory: PASS through `scripts/android_emulator_qa.ps1`. `adb`, Emulator 36.6.11, `sdkmanager`, and `avdmanager` are available; installed `system-images;android-35;google_apis;x86_64` and existing `PixelQuickCut_API35` were used.
- Android AVD suitability: PASS after assigning 4096 MiB RAM. The AVD advertises x86_64/ARM64 Native Bridge and successfully loaded every production ARM64 library (`canto_core`, sherpa-onnx, ONNX Runtime, `canto_llm`, llama.cpp/ggml). A separate x86_64 QA build was therefore not required.
- `flutter test`: PASS 9/9, including DNS failure → two bounded retries → fallback while preserving same-identity `.part` bytes and existing-install rollback, odd-offset PCM16 conversion, and installed-model relaunch detection.
- `flutter analyze`: PASS, no issues.
- Android ARM64 release Gradle/native build: PASS, 56,955,559-byte APK.
- Release APK inspection: PASS for `android.permission.INTERNET`, `android.permission.ACCESS_NETWORK_STATE`, `android.permission.RECORD_AUDIO`, ARM64 native runtime, and all pinned OpenCC asset files.
- Fresh release install and permissions on emulator: PASS. Package primary ABI was ARM64 under Native Bridge; the runtime microphone permission changed from denied to granted through the visible system dialog.
- Emulator download/resume/fallback: PASS. SenseVoice `.part` advanced from 9,762,383 bytes and resumed after force-stop/cold boot; SenseVoice used official sherpa-onnx GitHub and Qwen completed through the Hugging Face fallback. Final on-device SHA-256 values matched all five catalog hashes recorded below, and staging was empty after atomic install.
- Emulator microphone/live ASR: PASS. Pulled M4A contained 1,439,744 nonzero samples over 29.99 seconds (mean -46.8 dB, max -18.3 dB); visible live Cantonese output and corresponding SQLite events were observed. This validates host-audio plumbing only, not physical-device microphone acoustics or ASR accuracy.
- Emulator Stop/quality/LLM: PASS. A 79-second meeting saved three M4A segments, reached 3/3 `quality_complete`, invoked packaged Qwen locally, persisted valid structured JSON, and opened all four result tabs. No semantic-accuracy claim is made from the deliberately noisy acoustic fixture.
- Emulator crash recovery: PASS. A closed segment survived abrupt force-stop and reappeared as `audio_saved`; `ffprobe` validated 29.994667 seconds/249,487 bytes. The open tail became `.m4a.incomplete`, and the recovery card appeared.
- Emulator offline primary flow and persistence: PASS. Network unreachability was verified before/during/after local ASR and LLM; the completed meeting and results survived force-stop/relaunch. Network connectivity was restored after the test.
- Catalog validator: PASS, 7 entries; all downloadable files have at least two HTTPS sources, all six Windows role/profile mappings are unique, and High SRT is not Base.
- JSON Schema validation: PASS after unattended installation of the `jsonschema` QA dependency.
- Windows pure-logic/integration harness: PASS 11/11, including the final HK Traditional gate for TXT and SRT and explicit content/timestamp role settings.
- CMake/MSVC Release build and CTest: PASS for `canto_core.dll`, cancellation/reset/timestamp mode, and native ASR duration diagnostics.
- Real SenseVoice native test: PASS, `呢几个字都表达唔到我想讲嘅意思。`.
- Real Whisper Small Q5_1 native test: PASS, `這幾個字都表達不到我想說的意思` with timestamp capability.
- Real Whisper Large-v3-Turbo Q5_0 native test: PASS, `這幾個字都表達不到我想講的意思` with timestamp capability.
- Real Qwen3 0.6B Q4_K_M generation: PASS from prior host QA on pinned llama.cpp b10516 with the required constrained JSON shape.
- Final WinUI self-contained publish, copied-directory responsive launch, real clean HK Traditional TXT and real SRT: PASS.
- Real 12-minute duration-expanded Cantonese TXT: PASS, 23,040,000 decoded PCM bytes, 11,520,000 samples, 72 results, 488 MiB working set. The sample repeats a real upstream Cantonese utterance and is a duration stress fixture, not a natural meeting-accuracy corpus.
- High Accuracy responsiveness/cancel: PASS on the i3-13100F host. While Whisper Large-v3-Turbo was processing, repeated UI snapshots returned in 0.1-2.5 seconds, the window drag completed in 1.2 seconds, and the Cancel click returned in 140 ms. Native cleanup completed in the background in 6.61 seconds for the UI run; a deliberately adverse native stress point returned the cancel request in 0 ms and took 13.75 seconds to leave the current encoder graph and destroy the engine.
- Local diagnostics: PASS for job/model/revision/role/timestamp mode, model load count/duration, worker thread, FFmpeg/chunk/sample progress, queue wait, last native ASR duration, result count, memory, cancellation, and cleanup duration; no source path or transcript content is written.
- Legacy/reference Rust workspace tests: PASS 1 binding + 3 Tauri-library tests; legacy TypeScript/Vite build: PASS. These do not count as production Windows acceptance.
- `SatelliteResourceLanguages=zh-HK;en-US` investigation: the Windows App SDK publish still emitted 76 framework locale directories, so the ineffective property was removed. No locale directories were manually deleted; the complete publish passed copied-portable TXT/SRT.

Observed final Windows exports:

- Clean Hong Kong Traditional TXT: `呢幾個字都表達唔到我想講嘅意思。`
- Clean Hong Kong Traditional SRT: `00:00:00,000 --> 00:00:04,000` / `這幾個字都表達不到我想講的意思。`

## Model integrity record

| Model | Revision | Source | Main QA file SHA-256 | Local QA path | License/reference |
|---|---|---|---|---|---|
| `sensevoice-yue-int8-2024-07-17` | `2365baeacb507f821a0c8120fcee3d484dba7a07` | Official `csukuangfj` Hugging Face repository | `c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51` | `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models\sensevoice-yue-int8-2024-07-17\2365baeacb507f821a0c8120fcee3d484dba7a07` | Packaged upstream LICENSE; sherpa-onnx reference in catalog. |
| Official SenseVoice INT8 archive | `asr-models` / 2024-07-17 | Official `k2-fsa/sherpa-onnx` GitHub release | `7d1efa2138a65b0b488df37f8b89e3d91a60676e416f515b952358d83dfd347e` | `%TEMP%\sherpa-sensevoice-int8-2024-07-17.tar.bz2` (host verification only) | Extracted model/tokens/LICENSE hashes exactly match the pinned individual files. |
| `whisper-base-multilingual-q5-1` | `5359861c739e955e79d9a303bcbc70fb988958b1` | Official `ggerganov/whisper.cpp` Hugging Face repository | `422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898` | `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models\whisper-base-multilingual-q5-1\5359861c739e955e79d9a303bcbc70fb988958b1` | MIT; packaged whisper.cpp license. |
| `whisper-small-multilingual-q5-1` | `5359861c739e955e79d9a303bcbc70fb988958b1` | Official `ggerganov/whisper.cpp` Hugging Face repository | `ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb` | `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models\whisper-small-multilingual-q5-1\5359861c739e955e79d9a303bcbc70fb988958b1` | MIT; official whisper.cpp model repository. |
| `whisper-large-v3-turbo-q5-0` | `5359861c739e955e79d9a303bcbc70fb988958b1` | Official `ggerganov/whisper.cpp` Hugging Face repository | `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2` | `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models\whisper-large-v3-turbo-q5-0\5359861c739e955e79d9a303bcbc70fb988958b1` | MIT; official whisper.cpp model repository. |
| `qwen3-0.6b-q4-k-m` | `1208e45d782fe18602c5eaf10e5758d5b0f24c03` | Official `Qwen/Qwen3-0.6B-GGUF` Hugging Face repository | `b0638f08417a2d3c8652760462eb5407c6e30173cf9608ad0820757a281eea0e` | `work/models/Qwen3-0.6B-Q4_K_M.gguf` (host QA); Android installs app-private copy | Apache-2.0; downloaded LICENSE SHA-256 `5de36594c10839788a8c589443a8ef9d8b8d17c65a1b5807206ae037fc36c6bd`. |

OpenCC dictionary revision: `26753884f1984add422f3b0249ccee8613deaff6`, Apache-2.0, packaged with its LICENSE. `STCharacters.txt` SHA-256 `a0ca1601c70648cf48b33c3c6210ccbecc5c7eead4b4c3daf76587ba2c03582b`; `STPhrases.txt` SHA-256 `f6eab5e5c6dd7640597878d3dfc6599ee1279d2bc91561eadd8e114194e2925a`.

## RC3 artifacts (historical, not the model-management build)

- Android: `release/android-arm64/CantoMeet-arm64-release.apk`, 56,955,559 bytes, SHA-256 `5590E9A960D493AE264D17C5A36C49BAA1F339EABC0330E245E8809D4F54057C`.
- Windows: `release/windows-winui-x64-production/CantoTranscribe.exe`, 272,896 bytes, SHA-256 `18C073896D6535D5665E3FC3A6754B4541144992978643AF96230DD5C605433E`; distribute the complete containing directory.

## Physical hardware and remaining release limitations

- Physically tested: this Windows x64 host (CPU inference for SenseVoice/Base/Small/Large-v3-Turbo, WinUI launch, real model output, portable copy, cancellation/resume).
- Android emulator tested: `PixelQuickCut_API35`, API 35 Google APIs x86_64, 4096 MiB RAM, with the production ARM64 APK executed through `libndk_translation.so`. This is functional evidence only and is not ARM64 performance evidence.
- Physical Android status: **PHYSICAL DEVICE NOT TESTED**. No ARM64 phone/tablet is attached; the corrected fresh-device download remains externally BLOCKED pending the user's retest.
- Android APK uses development signing. Store release still needs the owner's protected production signing credentials.
- No licensed Hong Kong meeting corpus is committed. CER/WER, multi-speaker/noisy-room accuracy, physical microphone quality, ARM64 performance, thermal/battery, four-hour physical Android soak, and eight-hour Windows soak remain untested benchmarking rather than emulator/CORE claims.
- Flutter reports that `record_android` still applies the legacy Kotlin Gradle Plugin; it builds successfully but should be upgraded before a future Flutter release enforces built-in Kotlin.

## RC3 publication record (historical)

- Branch: `master`.
- Verified implementation commit: `2741ae735e9ca7db2d974de88f5af4140351ce61`.
- Remote: `https://github.com/sion-rgb/canto-suite` (private).
- Verified implementation and final QA metadata are pushed to `origin/master`.
- Latest published device-test pre-release: [`v0.1.0-rc3`](https://github.com/sion-rgb/canto-suite/releases/tag/v0.1.0-rc3), targeting commit `4dc1d6a9d8f302ff277f1e5e394cddc112f2049c`.
- RC3 Android asset: `CantoMeet-arm64-release.apk`, SHA-256 `5590e9a960d493ae264d17c5a36c49baa1f339eabc0330e245e8809d4f54057c`.
- RC3 Windows asset: `CantoTranscribe-Windows-x64-v0.1.0-rc3.zip`, SHA-256 `e2daa7b121b9b3077df16ed2b47f4d935062f91a50c7565da74841f54cedb23d`.
- RC3 checksum asset: `Canto-Suite-v0.1.0-rc3-SHA256SUMS.txt`; RC2 assets remain available as historical builds.
