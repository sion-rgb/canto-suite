# QA report

Executed on 2026-09-02 (Asia/Hong_Kong). A source file or successful compile alone is not counted as end-to-end acceptance.

## Core acceptance status

| Core item | Status | Evidence / blocker |
|---|---|---|
| CORE-WIN-001 Packaged Windows UI | PASS | Final WinUI 3 EXE launched directly and remained responsive. Publish contains zero filenames matching WebView/Vite/Node/Tauri and uses no localhost. |
| CORE-AND-001 Real microphone pipeline | BLOCKED | `AudioRecord → 16 kHz PCM EventChannel → Dart isolate → FFI → C ABI → SenseVoice` is implemented and packaged, but `adb devices -l` reports no attached Android device. Real microphone speech cannot truthfully be observed without physical hardware. |
| CORE-AND-002 Meeting persistence | BLOCKED | Atomic AAC/M4A segment commits, SQLite rows, and incremental live transcript events are implemented. Fresh-install record/relaunch observation requires the unavailable Android device. |
| CORE-AND-003 Quality transcription | BLOCKED | MediaExtractor/MediaCodec streamed decode and separate raw/quality columns are implemented. Native SenseVoice real-audio QA passes on Windows; the Android ABI end-to-end run requires the unavailable device. |
| CORE-AND-004 Local meeting LLM | BLOCKED | Android packages llama.cpp and `libcanto_llm.so`; persistent model ownership, local prompts, nullable fields, and structured persistence are implemented. Real Qwen GGUF inference passed on the host, but the Android post-Stop run requires the unavailable device. |
| CORE-AND-005 Long-context summarization | PASS | Dart test forces multiple bounded chunks and fan-in consolidation; native GBNF constrains the final JSON shape. Whole-transcript prompting is not used. |
| CORE-AND-006 Crash recovery | BLOCKED | Completed segments and final live transcript events are committed independently of graceful shutdown and incomplete meetings are discoverable. Abrupt termination/relaunch on Android storage requires the unavailable device. |
| CORE-AND-007 Model first-launch flow | BLOCKED | Physical RAM/CPU recommendation, user choice, Range resume, SHA-256, preserved previous install, and atomic pin are implemented; installer tests pass. Device UI/download/relaunch requires the unavailable device. |
| CORE-AND-008 Offline primary workflow | BLOCKED | All inference paths are local after model installation. Full microphone-to-summary offline observation requires the unavailable device. |
| CORE-AND-009 Android UI flow | BLOCKED | Home, recording, processing, summary/tasks/transcript/info, recovery card, and output settings are implemented. Physical UI traversal requires the unavailable device. |
| CORE-WIN-002 Model setup | PASS | SenseVoice and whisper.cpp models were installed and checksum-verified. Automated test proves nonzero Range resume, SHA-256 verification, staging move, and verified final state. |
| CORE-WIN-003 Real TXT export | PASS | Packaged app + real upstream Cantonese WAV + SenseVoice produced timestamp-free Traditional and Simplified TXT. |
| CORE-WIN-004 Real SRT export | PASS | Packaged app + real WAV + whisper.cpp produced syntactically valid timestamped SRT through punctuation/length segmentation. |
| CORE-WIN-005 Offline runtime | BLOCKED | Runtime code audit finds HTTP only in model installers, and local inference succeeds without any server. An EXE-scoped outbound firewall rule was attempted but Windows returned `The requested operation requires elevation`; no rule was left behind. Administrator/UAC action is externally required for the literal network-disable test. |
| CORE-WIN-006 Long media bounded processing | PASS | Simulated 5-minute repeated real Cantonese WAV streamed via FFmpeg; checkpoint reached 300,000 ms with 27 real ASR segments without full decoded-media loading. |
| CORE-WIN-007 Resume | PASS | Same 5-minute job was cancelled at persisted 41,344 ms, remained stable, relaunched, resumed above that position, and completed at 300,000 ms. |
| CORE-WIN-008 Packaging independence | PASS | Final publish was copied to an ordinary `work/portable-copy-test-final3` path; EXE launched responsive and completed real Cantonese TXT. No source tree runtime, WebView, Vite, Node, Tauri, Rust, Python, or localhost file was present. |
| CORE-WIN-UI-001 WinUI production UI | PASS | Production is WinUI 3/C# + C ABI/C++; real TXT/SRT paths pass. Tauri is legacy/reference only. |
| CORE-SHARED-001 Cantonese display | PASS | Automated preservation test passes for `嘅 喺 冇 咗 啲 嚟 噉`. |
| CORE-SHARED-002 Cantonese-English code switching | PASS | Ground-truth sample `我哋下個 sprint 會 update 個 API。` was processed and observed unchanged. This is preservation QA, not an accuracy claim. |
| CORE-SHARED-003 Custom dictionary | PASS | `union design` maps to `UNION Design HK`; automated test passes. |
| CORE-SHARED-004 Model integrity | PASS | All three QA models are revision/size/SHA-256 pinned below and were hash-verified. |
| CORE-SHARED-005 No private content upload | PASS | Source audit finds HTTP clients only in Android/Windows model installers. Audio, transcript, summary, speakers, ASR, LLM, and export have no upload client or endpoint. |

All non-PASS CORE items above depend on unavailable physical Android hardware or a UAC-only Windows firewall action. There are no CORE items marked PARTIAL, FAIL, or NOT TESTED.

## Executed tests and builds

- `flutter test`: PASS 5/5 (model Range/checksum/atomic install, checksum-failure rollback, hierarchical summarization, first-launch privacy, persistent Simplified selection).
- `flutter analyze`: PASS, no issues.
- Android ARM64 release Gradle/native build: PASS.
- Catalog validator: PASS, 5 entries.
- Windows pure-logic/integration harness: PASS 8/8.
- CMake/MSVC Release build: PASS for `canto_core.dll` and tests.
- Real native SenseVoice test: PASS, `呢几个字都表达唔到我想讲嘅意思。`.
- Real native whisper.cpp test: PASS, `這幾個字都表達不到我想講的意思` with timestamp capability.
- Real Qwen3 0.6B Q4_K_M generation: PASS on pinned llama.cpp b10516. GBNF-constrained output contained all required top-level fields and nullable due dates.
- WinUI self-contained publish and responsive launch smoke test: PASS.
- Portable-directory real TXT run: PASS.
- Simulated 5-minute cancellation/resume/complete test: PASS.

Observed Windows exports:

- Traditional TXT: `呢幾個字都表達唔到我想講嘅意思。`
- Simplified TXT: `呢几个字都表达唔到我想讲嘅意思。`
- SRT cue: `00:00:00,000 --> 00:00:04,000` / `這幾個字都表達不到我想講的意思`

## Model integrity record

| Model | Revision | Main QA file SHA-256 | Local QA path | License/reference |
|---|---|---|---|---|
| `sensevoice-yue-int8-2024-07-17` | `2365baeacb507f821a0c8120fcee3d484dba7a07` | `c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51` | `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models\sensevoice-yue-int8-2024-07-17\2365baeacb507f821a0c8120fcee3d484dba7a07` | Packaged upstream LICENSE; sherpa-onnx SenseVoice reference in catalog. |
| `whisper-base-multilingual-q5-1` | `5359861c739e955e79d9a303bcbc70fb988958b1` | `422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898` | `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models\whisper-base-multilingual-q5-1\5359861c739e955e79d9a303bcbc70fb988958b1` | MIT; packaged whisper.cpp license. |
| `qwen3-0.6b-q4-k-m` | `1208e45d782fe18602c5eaf10e5758d5b0f24c03` | `b0638f08417a2d3c8652760462eb5407c6e30173cf9608ad0820757a281eea0e` | `work/models/Qwen3-0.6B-Q4_K_M.gguf` (host QA); Android installs app-private copy | Apache-2.0; downloaded LICENSE SHA-256 `5de36594c10839788a8c589443a8ef9d8b8d17c65a1b5807206ae037fc36c6bd`. |

## Final artifacts

- Android: `release/android-arm64/CantoMeet-arm64-release.apk`, 55,596,025 bytes, SHA-256 `9E164FF09533956B3FFE6E6FE2327BA26376E930503F2114478CB875DA0DFD8D`. APK contains only `arm64-v8a` native entries and includes `libcanto_core.so`, `libcanto_llm.so`, `libllama.so`, sherpa-onnx, and ONNX Runtime.
- Windows: `release/windows-winui-x64-production/CantoTranscribe.exe`, 272,896 bytes, SHA-256 `F36D889DECA92E27BE978AD24C99FD1847C03E5DAD9102B11517545539D58858`; distribute the complete containing directory.

## Physical hardware and remaining release limitations

- Physically tested: this Windows x64 host (CPU inference, WinUI launch, real model output, portable copy, cancellation/resume).
- Not available: an attached Android ARM64 phone/tablet; `adb devices -l` was empty.
- Android APK uses development signing. Store release still needs the owner's protected production signing credentials.
- No licensed Hong Kong meeting corpus is committed. CER/WER, multi-speaker/noisy-room accuracy, thermal/battery, four-hour Android soak, and eight-hour Windows soak remain product benchmarking rather than CORE implementation claims.
- Flutter reports that `record_android` still applies the legacy Kotlin Gradle Plugin; it currently builds successfully but should be upgraded before a future Flutter release enforces built-in Kotlin.

## Git state

- Branch name: `master`; the repository has no `HEAD` commit yet.
- Working tree: source/specification files are uncommitted (93 intent-to-add/worktree entries and 27 untracked paths at the final check). Existing staging intent was preserved.
- Remote: none configured, so no commit or push was performed.
