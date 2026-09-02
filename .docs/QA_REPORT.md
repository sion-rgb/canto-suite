# QA report

Executed on 2026-09-02 (Asia/Hong_Kong). A source file or successful compile alone is not counted as end-to-end acceptance.

## Core acceptance status

| Core item | Status | Evidence / blocker |
|---|---|---|
| CORE-WIN-001 Packaged Windows UI | PASS | Final WinUI 3 EXE launched from a copied portable directory and remained responsive. Publish contains zero filenames matching WebView/Vite/Node/Tauri and uses no localhost. |
| CORE-AND-001 Real microphone pipeline | BLOCKED | `AudioRecord → 16 kHz PCM EventChannel → Dart isolate → FFI → C ABI → SenseVoice` is implemented and packaged, but `adb devices -l` reports no attached Android device. Real microphone speech cannot truthfully be observed without physical hardware. |
| CORE-AND-002 Meeting persistence | BLOCKED | Atomic AAC/M4A segment commits, SQLite rows, and incremental live transcript events are implemented. Fresh-install record/relaunch observation requires an attached Android device. |
| CORE-AND-003 Quality transcription | BLOCKED | MediaExtractor/MediaCodec streamed decode and separate raw/quality columns are implemented. Native SenseVoice real-audio QA passes on Windows; the Android ABI end-to-end run requires an attached device. |
| CORE-AND-004 Local meeting LLM | BLOCKED | Android packages llama.cpp and `libcanto_llm.so`; persistent model ownership, local prompts, nullable fields, and structured persistence are implemented. Real Qwen GGUF inference passed on the host, but the Android post-Stop run requires an attached device. |
| CORE-AND-005 Long-context summarization | PASS | Dart test forces multiple bounded chunks and fan-in consolidation; native GBNF constrains the final JSON shape. Whole-transcript prompting is not used. |
| CORE-AND-006 Crash recovery | BLOCKED | Completed segments and final live transcript events are committed independently of graceful shutdown and incomplete meetings are discoverable. Abrupt termination/relaunch on Android storage requires an attached device. |
| CORE-AND-007 Model first-launch flow | BLOCKED | Release manifest now has `INTERNET`/`ACCESS_NETWORK_STATE`; installer tests prove Range resume, SHA-256, preserved previous install, atomic pin, bounded DNS retry and automatic source fallback. Fresh-device UI/download/offline observation requires an attached device. |
| CORE-AND-008 Offline primary workflow | BLOCKED | All inference paths are local after model installation. Full microphone-to-summary offline observation requires an attached Android device. |
| CORE-AND-009 Android UI flow | BLOCKED | Home, recording, processing, summary/tasks/transcript/info, recovery card, output format settings, concise download error and Advanced details are implemented. Physical UI traversal requires an attached device. |
| CORE-AND-010 Release model download resilience | BLOCKED | The built release APK manifest and packaged OpenCC assets pass inspection; DNS retry/fallback/resume/rollback tests pass. A fresh release APK downloading both required models over a normal physical-device network must still be rerun on the user's ARM64 device. |
| CORE-WIN-002 Model setup | PASS | All enabled Fast/Balanced/High models are size/SHA-pinned; automated test proves two bounded failures, source fallback, nonzero Range resume, SHA-256 and atomic staging. |
| CORE-WIN-003 Real TXT export | PASS | Final copied portable app + real upstream Cantonese WAV + active SenseVoice produced timestamp-free clean Hong Kong Traditional TXT. |
| CORE-WIN-004 Real SRT export | PASS | Final copied portable app + real WAV + whisper.cpp produced syntactically valid timestamped SRT through punctuation/length segmentation. |
| CORE-WIN-005 Offline runtime | BLOCKED | Runtime code audit finds HTTP only in model installers, and local inference succeeds without any server. An EXE-scoped outbound firewall rule still requires administrator/UAC for the literal network-disable test. |
| CORE-WIN-006 Long media bounded processing | PASS | Simulated 5-minute repeated real Cantonese WAV streamed via FFmpeg; checkpoint reached 300,000 ms with 27 real ASR segments without full decoded-media loading. |
| CORE-WIN-007 Resume | PASS | Same 5-minute job was cancelled at persisted 41,344 ms, relaunched, resumed above that position, and completed at 300,000 ms. |
| CORE-WIN-008 Packaging independence | PASS | Final publish copied to `work/portable-final-20260902`; EXE launched responsive and completed real Cantonese TXT/SRT. No source-tree or dev-server runtime dependency is present. |
| CORE-WIN-009 Model roles, profiles, and management | PASS | Fast/Balanced/High map to three distinct TXT and SRT IDs. Model Management exposes active roles, installed/version/size/storage, download/switch/repair-update/delete/redownload; temp-root tests execute install, corruption repair, delete, redownload and storage reporting. Small and Large-v3-Turbo passed real native Cantonese audio. Base is Fast only. |
| CORE-WIN-UI-001 WinUI production UI | PASS | Production is WinUI 3/C# + C ABI/C++; final copied publish launched and real TXT/SRT paths pass. Tauri is legacy/reference only. |
| CORE-SHARED-001 Cantonese display | PASS | Automated preservation test passes for `嘅 喺 冇 咗 啲 嚟 噉`. |
| CORE-SHARED-002 Cantonese-English code switching | PASS | Ground-truth sample `我哋下個 sprint 會 update 個 API。` was processed and observed unchanged. This is preservation QA, not an accuracy claim. |
| CORE-SHARED-003 Custom dictionary | PASS | `union design` maps to `UNION Design HK`; automated test passes. |
| CORE-SHARED-004 Model integrity | PASS | Five QA models and the official SenseVoice archive are revision/size/SHA-256 pinned below and hash-verified. |
| CORE-SHARED-005 No private content upload | PASS | Source audit finds HTTP clients only in Android/Windows model installers. Audio, transcript, summary, speakers, ASR, LLM, and export have no upload endpoint. |
| CORE-SHARED-006 Clean Cantonese and Hong Kong Traditional | PASS | Sanitized regression converts `后/个/柜/倾` to `後/個/櫃/傾`, preserves `我哋/喺/嘅`, conservatively collapses repeated fillers/restarts, applies terminology/punctuation, and deliberately leaves semantic error `開飛` unchanged. |

All non-PASS CORE items above depend on unavailable attached Android hardware or a UAC-only Windows firewall action. There are no CORE items marked PARTIAL, FAIL, or NOT TESTED.

## Executed tests and builds

- `flutter test`: PASS 6/6, including DNS failure → two bounded retries → fallback while preserving same-identity `.part` bytes and existing-install rollback.
- `flutter analyze`: PASS, no issues.
- Android ARM64 release Gradle/native build: PASS, 56,955,387-byte APK.
- Release APK inspection: PASS for `android.permission.INTERNET`, `android.permission.ACCESS_NETWORK_STATE`, `android.permission.RECORD_AUDIO`, ARM64 native runtime, and all pinned OpenCC asset files.
- Catalog validator: PASS, 7 entries; all downloadable files have at least two HTTPS sources, all six Windows role/profile mappings are unique, and High SRT is not Base.
- JSON Schema validation: PASS after unattended installation of the `jsonschema` QA dependency.
- Windows pure-logic/integration harness: PASS 10/10.
- CMake/MSVC Release build: PASS for updated generic single-Whisper-file discovery, `canto_core.dll`, and tests.
- Real SenseVoice native test: PASS, `呢几个字都表达唔到我想讲嘅意思。`.
- Real Whisper Small Q5_1 native test: PASS, `這幾個字都表達不到我想說的意思` with timestamp capability.
- Real Whisper Large-v3-Turbo Q5_0 native test: PASS, `這幾個字都表達不到我想講的意思` with timestamp capability.
- Real Qwen3 0.6B Q4_K_M generation: PASS from prior host QA on pinned llama.cpp b10516 with the required constrained JSON shape.
- Final WinUI self-contained publish, copied-directory responsive launch, real clean HK Traditional TXT and real SRT: PASS.
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

- Android: `release/android-arm64/CantoMeet-arm64-release.apk`, 56,955,387 bytes, SHA-256 `4259073828CFE16A3CBDA4D3E6317F76089AF0695065944DD3E8F0300EE3934E`.
- Windows: `release/windows-winui-x64-production/CantoTranscribe.exe`, 272,896 bytes, SHA-256 `60E261934C03C9F414F6400A6530D977EC439AC1D70F10873D25B761FFB2C2C3`; distribute the complete containing directory.

## Physical hardware and remaining release limitations

- Physically tested: this Windows x64 host (CPU inference for SenseVoice/Base/Small/Large-v3-Turbo, WinUI launch, real model output, portable copy, cancellation/resume).
- Not attached to this workspace: an Android ARM64 phone/tablet; `adb devices -l` is empty. The user's reported real-device DNS failure is addressed in this build but the corrected fresh-device download remains externally BLOCKED pending retest.
- Android APK uses development signing. Store release still needs the owner's protected production signing credentials.
- No licensed Hong Kong meeting corpus is committed. CER/WER, multi-speaker/noisy-room accuracy, thermal/battery, four-hour Android soak, and eight-hour Windows soak remain benchmarking rather than CORE claims.
- Flutter reports that `record_android` still applies the legacy Kotlin Gradle Plugin; it builds successfully but should be upgraded before a future Flutter release enforces built-in Kotlin.

## Git state

- Branch: `master`.
- Verified implementation commit: `4be05ab5778bb4abee63011f0b697d6291842d07`.
- Remote: `https://github.com/sion-rgb/canto-suite` (private).
- Verified implementation and final QA metadata are pushed to `origin/master`.
