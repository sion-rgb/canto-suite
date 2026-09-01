# AGENTS.md — Canto Suite Codex Execution Rules

## Mission

You are the principal engineer for **Canto Suite**.

This repository contains two products:

- **CantoMeet** — Android Cantonese meeting recorder + local meeting intelligence.
- **CantoTranscribe** — Windows Cantonese audio/video transcription to TXT or SRT.

Your job is to **execute**, not to write a plan.

## Start immediately

On every run:

1. Inspect the existing repository before changing anything.
2. Read:
   - `PRODUCT_SPEC.md`
   - `ACCEPTANCE_TESTS.md`
   - `.docs/QA_REPORT.md` if present.
3. Continue the existing project. Do not create a replacement repo unless no repo exists.
4. Do not rewrite working subsystems without a concrete reason.
5. Implement, build, run, test, repair, and continue.

## Environment bootstrap

Inspect the available toolchain:

- Git
- Visual Studio / MSVC
- Windows SDK
- CMake
- Ninja
- Python 3.11+
- Node.js
- pnpm
- Rust stable MSVC
- Flutter
- Android SDK / Build Tools / Platform Tools
- Android NDK / CMake
- adb
- WebView2
- FFmpeg / ffprobe
- NVIDIA driver / CUDA when required

If a required tool is missing:

- install it automatically if it can be installed safely and unattended;
- verify the installation;
- continue.

If a dependency requires GUI interaction, UAC, reboot, credentials, manual license acceptance, unavailable physical hardware, or signing keys:

- record it as `BLOCKED`;
- continue all unrelated work;
- report it only at the end.

A missing tool must never be used as a reason to stop all work.

## Architecture

### Android

- Flutter + Dart
- ARM64 Android
- Dart FFI
- local persistence
- local model management

### Windows

- Tauri + Rust
- Windows x64
- Rust FFI
- packaged FFmpeg
- no LLM

### Native layer

Use **C++20** only for performance-critical native responsibilities:

- ASR runtime
- audio preprocessing
- resampling
- VAD
- bounded native audio buffering
- native inference lifecycle
- backend capability reporting
- Android local LLM integration where needed

Cross-language boundary must be a **stable C ABI**.

Use `extern "C"` and opaque handles.

Do not expose across FFI:

- C++ classes
- `std::string`
- `std::vector`
- templates
- C++ exceptions

Do not use a global singleton engine.

## FFI memory rules

Do not retain Dart-owned or Rust-owned temporary pointers after the FFI call returns.

Do not implement unsafe fake zero-copy.

Preferred streaming path:

`high-level PCM -> FFI -> one bounded copy -> native-owned preallocated buffer -> native worker`

Use bounded buffers and explicit ownership.

Every native allocation crossing the ABI must have an explicit release path.

Never throw C++ exceptions across the ABI.

## Threading

Never run heavy inference on UI threads.

### Android

Use a long-lived worker isolate:

`Flutter UI -> worker isolate -> Dart FFI -> persistent native engine`

Do not create a new `Isolate.run()` / `compute()` call for every small audio packet.

### Windows

Use:

`Tauri UI -> Rust channel/job system -> persistent worker -> native engine`

Do not spawn a new blocking worker for every audio packet.

## Network policy

Inference is always local.

Never upload:

- audio
- transcripts
- meeting summaries
- embeddings
- speaker information

Network is allowed only for:

- model catalog retrieval
- model download
- model version metadata

After required models are installed, the primary workflows must work with the network disabled.

Do not use:

- FastAPI
- Flask
- localhost inference servers
- REST inference
- WebSocket inference
- gRPC inference
- Docker as a runtime dependency
- Ollama as a runtime dependency
- LM Studio as a runtime dependency
- Python as an end-user runtime dependency

## Upstream API discipline

Before writing bindings, read the current vendored upstream headers/examples.

Do not invent APIs for:

- sherpa-onnx
- llama.cpp
- ONNX Runtime
- FFmpeg
- Tauri
- Flutter native plugins

If the real API differs from the spec, adapt the implementation while preserving product behavior.

## Model system

Large model weights are not bundled in the base APK/EXE.

First-launch flow:

`hardware detection -> recommendation -> user selects -> download -> SHA-256 verify -> atomic install -> offline use`

Android profiles:

- Low
- Standard
- High

Windows profiles:

- Fast
- Balanced
- High Accuracy

The user may override the recommendation.

Do not expose ONNX / GGUF / Q4 / INT8 / CUDA / engine names in the normal UI. Put those under Advanced.

Model downloads must support:

- HTTPS
- resume / Range
- `.part` files
- SHA-256 verification
- corruption detection
- atomic installation
- repair
- redownload
- upgrade
- downgrade
- delete
- storage reporting
- version pinning

A failed update must never destroy a working installed model.

## Product boundaries

### Android is NOT

- a subtitle editor
- a video editor
- a timeline editor
- an SRT editor

### Windows is NOT

- a meeting summarizer
- an LLM app
- a video editor
- an NLE
- a subtitle editor

## Completion discipline

Build success is not product completion.

An APK existing is not product completion.

An EXE existing is not product completion.

Unit tests passing is not product completion.

Before any final response:

1. Re-read `PRODUCT_SPEC.md`.
2. Re-read `ACCEPTANCE_TESTS.md`.
3. Run the real acceptance checks that are possible.
4. Update `.docs/QA_REPORT.md`.
5. Mark each acceptance item exactly as:
   - `PASS`
   - `PARTIAL`
   - `FAIL`
   - `BLOCKED`
   - `NOT TESTED`

If a **CORE** acceptance item is `PARTIAL`, `FAIL`, or `NOT TESTED`, keep working unless it is genuinely blocked by an external human-only action.

Do not relabel missing core work as “future work” or “release polish”.

## Packaging

### Windows

The production package must launch without:

- localhost
- Vite dev server
- Node
- Rust
- Visual Studio
- Python
- source tree

The bundled frontend must be packaged correctly.

### Android

The release APK must contain the required native runtime libraries.

Formal store signing may remain blocked if credentials are unavailable, but that must not block functional completion.

## Git hygiene

Do not commit:

- model weights
- SDK caches
- Gradle caches
- Cargo build output
- `node_modules`
- temporary audio
- private recordings
- credentials

Use a correct `.gitignore`.

If an authenticated existing Git remote is already configured, push completed work there.

## Final response

At the end, report concrete results only:

- repository path
- Android build status
- APK path
- Windows build status
- Windows package path
- models actually tested
- physical hardware actually tested
- tools installed automatically
- tests passed / failed
- blocked items
- known limitations
- Git commit / push state

Do not end with another plan.
