# Canto Suite

Local-first Hong Kong Cantonese tools in one monorepo:

- **CantoMeet** — Android meeting recording, live Cantonese transcription, quality transcription, and local meeting intelligence.
- **CantoTranscribe** — production Windows WinUI 3/C# media-to-TXT/SRT utility using the native C ABI/C++ engine.
- **Canto Core** — C++20 bounded audio/inference worker shared by Android and Windows.

The production Windows application is `apps/windows/CantoTranscribe`. The older Tauri implementation under `apps/desktop` is retained only as legacy/reference code and is not part of production acceptance.

## Implemented workflows

CantoMeet captures a real Android microphone stream once, stores atomic AAC/M4A segments, tees 16 kHz PCM through a Dart isolate and FFI into native SenseVoice ASR, persists live results incrementally, then decodes the saved media for quality ASR. A persistent llama.cpp worker runs Qwen3 0.6B locally with bounded hierarchical extraction and a grammar-constrained meeting JSON schema. Missing owners and dates stay `null`/`未指定`.

CantoTranscribe is a self-contained WinUI 3 application. Packaged FFmpeg streams bounded PCM into either SenseVoice for TXT or whisper.cpp for timestamped SRT. Jobs checkpoint their source fingerprint, position, model revision, and completed segments for restart-safe resume. The published application has no WebView, localhost, Vite, Node, Python, Rust, or development-server dependency.

Both products offer Traditional Chinese (Hong Kong, default) and Simplified Chinese output. Meeting content is never uploaded; HTTPS is used only to download revision-pinned model files and licenses before offline inference.

## Release artifacts

- Android ARM64 APK: `release/android-arm64/CantoMeet-arm64-release.apk`
- Windows x64 portable directory: `release/windows-winui-x64-production/`

See [.docs/BUILD.md](.docs/BUILD.md), [.docs/ARCHITECTURE.md](.docs/ARCHITECTURE.md), and [.docs/QA_REPORT.md](.docs/QA_REPORT.md).
