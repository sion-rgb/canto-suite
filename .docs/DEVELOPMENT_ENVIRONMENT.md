# Development environment

Environment inspection and build verification completed on 2026-09-01:

- Git 2.55; Rust/Cargo 1.98 with `x86_64-pc-windows-msvc` and `aarch64-linux-android` targets.
- NVIDIA GeForce RTX 2070 SUPER, 8 GiB VRAM, compute capability 7.5; CUDA toolkit 12.9.
- Visual Studio 2019 Build Tools with the x64 C++ toolchain. VS 2022 was not present.
- CMake 4.4.3, Ninja 1.13.2, and Python 3.13 were installed during bootstrap.
- The Codex bundled Node 24 runtime and pnpm 11 are usable without system installation.
- FFmpeg 9.0.1 binaries were checksum-verified by WinGet and copied into the Tauri release resources; the stalled per-user registration is not required at runtime.
- Flutter stable 3.47.2 was downloaded from Google's official archive, SHA-256 verified, and provisioned workspace-locally.
- Temurin JDK 17.0.20.1 was downloaded from the Adoptium API, SHA-256 verified, and configured for Flutter.
- The standard per-user Android SDK was discovered after the initial PATH inspection. It contains platform 36/36.1, platform tools/ADB 37, build tools 36.0/36.1, and NDK 27.0 plus 28.2.13676358. Android CMake 3.22.1 was added from a checksum-verified command-line SDK installation; the verified release build uses NDK 28.2 and CMake 3.22.1.

System-wide Node installation requested UAC and was stopped. The bundled runtime is used for frontend builds. Flutter is configured with explicit SDK/JDK paths, so no system-wide PATH change is required.
