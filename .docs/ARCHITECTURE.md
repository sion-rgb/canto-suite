# Architecture

## Product boundaries

```text
CantoMeet microphone
  → Android AudioRecord (48 kHz mono PCM)
  ├─→ MediaCodec AAC + MediaMuxer → atomic M4A segments → SQLite metadata
  └─→ 16 kHz PCM EventChannel → long-lived Dart isolate → FFI → C ABI → Canto Core

Saved M4A → Android MediaExtractor/MediaCodec → 16 kHz PCM → Canto Core quality ASR
Quality transcript → bounded hierarchical prompts → persistent llama.cpp/Qwen worker

CantoTranscribe WinUI 3/C#
  → packaged FFmpeg streaming decode → C ABI/C++ ASR → atomic TXT/SRT export
  → JSON checkpoint (fingerprint + processed position + segments) → safe resume
```

The WinUI application is the production Windows product. Tauri remains in `apps/desktop` only as historical/reference code.

## Native ownership and bounded work

Every `canto_engine` is independent. `canto_push_pcm16` copies caller memory into a fixed-capacity ring and never retains caller pointers. A native worker consumes bounded chunks; saturation returns `CANTO_QUEUE_FULL` and callers apply backpressure while draining results. Result text crosses the C ABI with one allocation and an explicit free.

SenseVoice is used for real Cantonese live/quality text. whisper.cpp 1.9.3 is used for timestamp-capable Windows SRT. The deterministic backend is compiled only for tests.

## CantoMeet persistence and recovery

The Android bridge captures one microphone stream. Ten-minute AAC-LC segments are written as `.part`, finalized, renamed, then committed to SQLite. Live final transcript events are committed incrementally, independent of graceful Stop. Raw live ASR and formal quality text use separate columns.

After Stop, saved M4A is decoded in bounded buffers and sent to the same real native ASR. The meeting then enters summarization. A persistent llama.cpp model handle processes bounded transcript chunks, structured consolidation groups, and final reduction. A native GBNF sampler guarantees the fixed JSON shape. Owners/dates without source evidence remain nullable.

## CantoTranscribe long media

Packaged FFmpeg emits 16 kHz mono PCM16 through stdout; the complete decoded media is never loaded into RAM. The atomic job file stores the source fingerprint, output/script selection, model id/revision, processed milliseconds, and completed text/timed segments every five seconds. Relaunch seeks FFmpeg to the persisted position and continues. TXT contains text only; SRT uses native whisper.cpp segment times plus punctuation/reading-length splitting.

## Privacy

The only HTTP clients are inside model installers. Audio capture, decoding, ASR, Chinese script conversion, dictionary cleanup, transcript storage, meeting extraction, and export have no content-upload endpoint, localhost server, or cloud inference path.
