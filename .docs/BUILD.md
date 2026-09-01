# Build

## Native C++ core on Windows

```powershell
cmake -S . -B build/native-vs -A x64
cmake --build build/native-vs --config Release
build/native-vs/native/canto-core/Release/canto_core_tests.exe
```

Set `CANTO_TEST_MODEL_DIR`, `CANTO_TEST_WAV`, and `CANTO_TEST_EXPECTED_BACKEND` to run the opt-in real SenseVoice or whisper.cpp integration path.

## Production Windows application

The supported production frontend is WinUI 3/C#. No Node, Vite, WebView, Tauri, or localhost process is used.

```powershell
dotnet run --project apps/windows/CantoTranscribe.Tests/CantoTranscribe.Tests.csproj -c Release
dotnet publish apps/windows/CantoTranscribe/CantoTranscribe.csproj `
  -c Release -r win-x64 -o release/windows-winui-x64-production
```

The publish is self-contained and includes WinUI 3, the .NET runtime, app-local MSVC runtime, FFmpeg/ffprobe, Canto Core, sherpa-onnx/ONNX Runtime, dictionaries, catalog, and licenses. `apps/desktop` is legacy/reference only.

## Android

Use Flutter stable plus Android SDK/NDK/CMake:

```powershell
cd apps/mobile
flutter pub get
flutter test
flutter analyze
flutter build apk --release --target-platform android-arm64
```

Gradle builds and packages ARM64 `libcanto_core.so` and `libcanto_llm.so`, plus pinned sherpa-onnx, ONNX Runtime, llama.cpp, and GGML libraries. SenseVoice and Qwen weights are not in the APK; first launch downloads revision-pinned files with Range resume, byte-size and SHA-256 verification, staging, and atomic install.

The generated APK is `apps/mobile/build/app/outputs/flutter-apk/app-release.apk`; the release copy is `release/android-arm64/CantoMeet-arm64-release.apk`.
