# Model system

`shared/model-catalog/catalog.v1.json` is the filename, revision, URL, size, checksum, capability, and license authority. Enabled product models are:

- `sensevoice-yue-int8-2024-07-17` — Android/Windows live and quality Cantonese ASR through sherpa-onnx 1.13.6.
- `whisper-base-multilingual-q5-1` — Windows timestamp-capable SRT through whisper.cpp 1.9.3.
- `qwen3-0.6b-q4-k-m` — Android local meeting extraction through pinned llama.cpp b10516.

Qwen3-ASR and Silero VAD catalog entries remain disabled references and cannot be selected as production models.

Both installers stream HTTPS downloads into `.part`, resume with HTTP Range, verify exact byte size and SHA-256, stage the complete revision, and switch atomically without replacing a working version on failure. Android also installs the Qwen Apache-2.0 license alongside the GGUF. Windows packages runtime licenses with the portable directory.

Hardware detection uses CPU count and physical RAM. It recommends a profile but preserves user choice. Android model files are stored under app-private support storage; Windows stores them under `%LOCALAPPDATA%\CantoSuite\CantoTranscribe\models`.

Model/runtime support is not presented as a Hong Kong meeting benchmark. The upstream Cantonese sample and real runtimes are verified, while corpus accuracy, thermal behavior, and multi-speaker meeting performance remain separate product-validation work.
