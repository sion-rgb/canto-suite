# Third-party notices

Canto Suite ships or downloads the following third-party components. Source/runtime binaries are kept in the repository only where they are required to build or run the tested products; model weights are always downloaded at runtime and are never committed.

| Component | Version/revision | License / notice location | Use |
|---|---|---|---|
| Flutter / Dart | workspace Flutter stable | Flutter SDK license | Android UI/runtime |
| Microsoft Windows App SDK / WinUI 3 | `1.8.260803003` | NuGet package license | Production Windows UI |
| .NET runtime | `8.0` | Microsoft .NET license | Self-contained Windows publish |
| sherpa-onnx C API | `1.13.6` | `third_party/sherpa-onnx/licenses/sherpa-onnx-LICENSE` | SenseVoice ASR adapter |
| ONNX Runtime | `1.27.1` | `third_party/sherpa-onnx/licenses/onnxruntime-LICENSE` | SenseVoice inference |
| whisper.cpp | `1.9.3` | `third_party/whisper.cpp/LICENSE` | Windows timestamped SRT ASR |
| OpenCC dictionaries | `26753884f1984add422f3b0249ccee8613deaff6` | Apache-2.0; `shared/opencc/LICENSE` | Deterministic Simplified-to-Hong-Kong-Traditional conversion |
| llama.cpp | `b10516` (`b95502ba9aa0eb73a2f4fc8878d7fbe6a847a0b9`) | `third_party/llama.cpp/LICENSE` | Android local LLM runtime |
| Qwen3 0.6B GGUF | revision `1208e45d782fe18602c5eaf10e5758d5b0f24c03` | Apache-2.0; downloaded model `LICENSE` | Android local meeting extraction |
| AndroidX / Kotlin / Gradle | pinned by `apps/mobile/android` | Upstream notices in dependency distributions | Android build/runtime |
| FFmpeg / ffprobe | packaged binaries | `apps/desktop/src-tauri/resources/licenses/` and upstream binary notices | Bounded media decode |

The model catalog records each model URL, exact byte size, SHA-256, revision, platform, and license reference. Do not add downloaded `.onnx`, `.gguf`, `.bin`, audio, video, APK, ZIP, or generated publish directories to source control.
