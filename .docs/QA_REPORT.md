# QA report

Executed on 2026-09-05 (Asia/Hong_Kong). A source file or successful compile alone is not counted as end-to-end acceptance.

## Core acceptance status

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
| CORE-WIN-009 Model roles, profiles, and management | PASS | Fast/Balanced/High map to three distinct SHA-pinned bundles. TXT/SRT have independent catalog labels and runtime modes; shared Whisper weights explicitly run content mode with timestamps off for TXT and timestamp mode for SRT. Model Management exposes active roles, installed/version/size/storage, download/switch/repair-update/delete/redownload. Small and Large-v3-Turbo passed real native Cantonese audio; Base is Fast SRT only. |
| CORE-WIN-UI-001 WinUI production UI | PASS | Production is WinUI 3/C# + C ABI/C++; final copied publish launched and real TXT/SRT paths pass. Tauri is legacy/reference only. |
| CORE-SHARED-001 Cantonese display | PASS | Automated preservation test passes for `嘅 喺 冇 咗 啲 嚟 噉`. |
| CORE-SHARED-002 Cantonese-English code switching | PASS | Ground-truth sample `我哋下個 sprint 會 update 個 API。` was processed and observed unchanged. This is preservation QA, not an accuracy claim. |
| CORE-SHARED-003 Custom dictionary | PASS | `union design` maps to `UNION Design HK`; automated test passes. |
| CORE-SHARED-004 Model integrity | PASS | Five QA models and the official SenseVoice archive are revision/size/SHA-256 pinned below and hash-verified. |
| CORE-SHARED-005 No private content upload | PASS | Source audit finds HTTP clients only in Android/Windows model installers. Audio, transcript, summary, speakers, ASR, LLM, and export have no upload endpoint. |
| CORE-SHARED-006 Clean Cantonese and Hong Kong Traditional | PASS | Final conversion runs after cleanup for both TXT and every SRT cue. Sanitized regression converts `后/个/柜/墙/这` to `後/個/櫃/牆/這`, preserves `我哋/佢哋/唔/冇/喺/嘅/啲/咗/嚟`, conservatively collapses repeated fillers/restarts, and deliberately leaves semantic error `開飛` unchanged. |

The only non-PASS CORE items are externally blocked: CORE-AND-010 still requires a fresh physical ARM64-device network download, and CORE-WIN-005 still requires an administrator/UAC-only outbound firewall test. There are no CORE items marked PARTIAL, FAIL, or NOT TESTED. Every Android runtime result above independently states emulator versus physical-device status.

## Executed tests and builds

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

## Final artifacts

- Android: `release/android-arm64/CantoMeet-arm64-release.apk`, 56,955,559 bytes, SHA-256 `5590E9A960D493AE264D17C5A36C49BAA1F339EABC0330E245E8809D4F54057C`.
- Windows: `release/windows-winui-x64-production/CantoTranscribe.exe`, 272,896 bytes, SHA-256 `60E261934C03C9F414F6400A6530D977EC439AC1D70F10873D25B761FFB2C2C3`; distribute the complete containing directory.

## Physical hardware and remaining release limitations

- Physically tested: this Windows x64 host (CPU inference for SenseVoice/Base/Small/Large-v3-Turbo, WinUI launch, real model output, portable copy, cancellation/resume).
- Android emulator tested: `PixelQuickCut_API35`, API 35 Google APIs x86_64, 4096 MiB RAM, with the production ARM64 APK executed through `libndk_translation.so`. This is functional evidence only and is not ARM64 performance evidence.
- Physical Android status: **PHYSICAL DEVICE NOT TESTED**. No ARM64 phone/tablet is attached; the corrected fresh-device download remains externally BLOCKED pending the user's retest.
- Android APK uses development signing. Store release still needs the owner's protected production signing credentials.
- No licensed Hong Kong meeting corpus is committed. CER/WER, multi-speaker/noisy-room accuracy, physical microphone quality, ARM64 performance, thermal/battery, four-hour physical Android soak, and eight-hour Windows soak remain untested benchmarking rather than emulator/CORE claims.
- Flutter reports that `record_android` still applies the legacy Kotlin Gradle Plugin; it builds successfully but should be upgraded before a future Flutter release enforces built-in Kotlin.

## Git state

- Branch: `master`.
- Verified implementation commit: `4be05ab5778bb4abee63011f0b697d6291842d07`.
- Remote: `https://github.com/sion-rgb/canto-suite` (private).
- Verified implementation and final QA metadata are pushed to `origin/master`.
- Device-test pre-release: `v0.1.0-rc2`, containing the Android download fallback, Windows model-management/profile, and Hong Kong Traditional cleanup changes documented above.
- RC2 Android asset: `CantoMeet-arm64-release.apk`, SHA-256 `4259073828cfe16a3cbda4d3e6317f76089af0695065944dd3e8f0300ee3934e`.
- RC2 Windows asset: `CantoTranscribe-Windows-x64-v0.1.0-rc2.zip`, SHA-256 `ab0019f23516f03c8e2a9d7e88f179d86055543605c098217a04c99fb9a63573`.
