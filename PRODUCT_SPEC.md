# PRODUCT_SPEC.md — Canto Suite Product Specification

## 1. Product overview

Canto Suite contains two focused products sharing selected native ASR/audio infrastructure.

### CantoMeet — Android

Purpose: **Hong Kong Cantonese meeting recording, transcription, and local meeting intelligence**.

Primary flow:

`Open -> Start Meeting -> Record -> Live Cantonese transcript -> Stop -> Quality transcription -> Local summary -> View summary/tasks/transcript`

### CantoTranscribe — Windows

Purpose: **Hong Kong Cantonese audio/video transcription to TXT or SRT**.

Primary flow:

`Drop/select media -> choose TXT or SRT -> choose quality -> transcribe -> export`

Windows does not use an LLM.

---

# 2. Shared principles

## 2.1 Cantonese-first

Treat Hong Kong Cantonese as a first-class language.

Preserve common Cantonese forms such as:

- 我哋
- 佢哋
- 唔係
- 冇
- 喺
- 啲
- 咗
- 睇吓
- 搞掂
- 嚟
- 噉

Support Cantonese-English code switching.

Do not silently convert all Cantonese transcripts into Mandarin-style written Chinese.

Output Chinese format:

- **香港繁體（預設）**
- **簡體中文**

Text modes:

- **原始廣東話** preserves the closest available ASR output after the selected script conversion.
- **乾淨廣東話** first normalizes to the selected script, then applies licensed deterministic conversion, the controlled terminology dictionary, conservative repeated-filler/restart cleanup, and punctuation/spacing cleanup.

Hong Kong Traditional conversion uses the pinned Apache-2.0 OpenCC `s2hk` dictionary chain. Preserve forms such as `我哋 / 佢哋 / 唔 / 冇 / 喺 / 嘅 / 啲 / 咗`; do not leave unintended Simplified fragments such as `后` or `柜` where `後` or `櫃` is intended. Never guess ambiguous Cantonese homophones. Deterministic cleanup does not claim to repair semantic ASR errors.

## 2.2 Privacy

User-facing privacy copy should clearly state:

- 錄音在本機處理
- 逐字稿在本機處理
- 會議摘要在本機處理
- 網絡只用於下載模型

No audio/transcript/summary content may be uploaded for inference.

Do not write complete user transcripts into production logs.

## 2.3 Custom dictionary

Provide a local terminology dictionary.

Suggested categories:

- People
- Company
- Places
- Technical
- Finance
- Custom

Support preferred display mappings, for example:

`union design -> UNION Design HK`

Use actual ASR hotword support only when verified for the active engine. Otherwise use safe deterministic post-processing.

---

# 3. Model catalog

Use a versioned model catalog.

Each model entry should contain, where relevant:

- model id
- display name
- role
- version/revision
- platform
- backend
- architecture
- file size
- SHA-256
- two or more verified download sources for each downloadable file where available
- source label/type in ordered priority
- optional verified archive bundle source
- RAM requirement
- recommended RAM
- VRAM requirement
- Cantonese support
- HK Cantonese support where verified
- streaming capability
- timestamp capability
- runtime compatibility
- license/reference metadata

Roles may include:

- LIVE_ASR
- QUALITY_ASR
- TXT_ASR
- MEETING_LLM
- VAD
- DIARIZATION
- SRT_ASR

Do not scatter model filenames throughout application code. Resolve active models through a registry.

Download behavior must use HTTPS, bounded retry, automatic fallback, Range resume, `.part` files, pinned byte size/SHA-256, and atomic installation. A partial file may be reused across sources only when the pinned file identity, size, and hash are identical. A failed update must never destroy a working model. Technical network exceptions belong under an Advanced disclosure rather than the main error message.

---

# 4. Hardware-aware setup

## Android

Consider:

- total RAM
- available RAM
- CPU
- GPU/NPU capability where reliably detectable
- available inference backend
- free storage
- peak memory estimate
- thermal considerations

Offer:

- **Low**
- **Standard — Recommended**
- **High**

Each choice shows:

- download size
- simple speed/quality description
- memory/storage warning when relevant

Advanced settings may reveal technical model details.

## Windows

Consider:

- CPU
- RAM
- GPU
- VRAM
- CUDA availability
- available backend
- free storage
- peak memory estimate

Offer:

- **Fast**
- **Balanced — Recommended**
- **High Accuracy**

The user may override the recommendation.

---

# 5. Initial model direction

Model names are not permanent architecture.

Use verified downloadable models through adapters/catalog entries.

## Android

### Low

- lightweight Cantonese-capable ASR, e.g. validated SenseVoice INT8-class
- lightweight local Q4 meeting LLM around sub-1B class

### Standard

- low-latency live ASR
- Qwen3-ASR 0.6B INT8-class quality ASR where validated
- local meeting LLM around 0.8B–1.5B class

### High

- live ASR remains latency-oriented
- highest validated mobile Cantonese quality ASR
- around 3B–4B Q4 meeting LLM if hardware permits

High does **not** mean every real-time model must be larger.

## Windows

No LLM.

Model role and quality profile are separate concepts. The active TXT model and active SRT model are independently selectable.

Validated initial profile mapping:

| Profile | TXT role/runtime | SRT role/runtime |
|---|---|---|
| Fast | SenseVoice INT8, content mode | Whisper Base multilingual Q5_1, timestamp mode |
| Balanced | Whisper Small multilingual Q5_1, content mode (timestamps disabled) | Whisper Small multilingual Q5_1, timestamp mode |
| High Accuracy | Qwen3-ASR 0.6B INT8, content mode (timestamps unavailable) | Whisper Large-v3-Turbo Q5_0, timestamp mode |

All three profiles resolve to different revision/SHA-pinned TXT and SRT bundles through the current C++ backend. High Accuracy intentionally uses Qwen3-ASR for Cantonese TXT and Whisper Large-v3-Turbo for timestamped SRT. A physical Whisper bundle may serve both roles only when the catalog explicitly gives it independent role labels and content-versus-timestamp runtime settings; selecting TXT must never silently run the SRT timestamp configuration. Qwen3-ASR is a functional current high TXT baseline, not a comparative-accuracy claim. Whisper Base must never be marketed as the highest-accuracy SRT engine.

### TXT

Prioritize Cantonese transcription quality.

A Qwen3-ASR-class engine may be appropriate where validated.

### SRT

Prioritize reliable timestamps.

Use a native timestamp-capable engine, e.g. a validated Whisper/whisper.cpp path.

Do not claim word-level timestamp precision unless actually implemented and tested.

---

# 5.1 Explicit model management (2026-09-06, authoritative amendment)

Real names and IDs are required in normal Model Manager UI, overriding the older rule that hides engine/quantization names. A quality profile is an optional preset, never a model identity.

Windows exposes independent `TXT_ASR` and `SRT_ASR` selections. Available choices include SenseVoice INT8, Qwen3-ASR 0.6B INT8, Whisper Base Q5_1, Whisper Small Q5_1 and Whisper Large-v3-Turbo Q5_0. SenseVoice/Qwen are TXT-only on Windows; Whisper models support TXT content mode and SRT timestamp mode. The exact Windows presets are Fast = SenseVoice/Base, Balanced = Whisper Small/Small, High Accuracy = Qwen3-ASR/Whisper Large. Qwen remains an explicit high TXT baseline rather than a promise of superior accuracy in every recording. An older stored High Accuracy preset is migrated to this exact Qwen/Large pair at startup; manually chosen roles remain Custom and are never silently replaced.

Android Settings → AI Models exposes independent `LIVE_ASR`, `QUALITY_ASR`, `MEETING_LLM`. ASR choices are SenseVoice INT8 and Qwen3-ASR 0.6B INT8; LLM choices are Qwen3 0.6B Q4_K_M and Q8_0. Qwen live ASR uses bounded offline-chunk inference, not low-latency streaming. Quantization choices are real different files, not evidence that all semantic ASR errors are corrected.

Current Android preset bundles (`androidPresets` is checked against runtime mappings):

| Preset | LIVE_ASR | QUALITY_ASR | MEETING_LLM |
|---|---|---|---|
| Low / light | SenseVoice INT8 | SenseVoice INT8 | Qwen3 0.6B Q4_K_M |
| Standard | SenseVoice INT8 | Qwen3-ASR 0.6B INT8 | Qwen3 0.6B Q4_K_M |
| High | SenseVoice INT8 | Qwen3-ASR 0.6B INT8 | Qwen3 0.6B Q8_0 |

These exact supported bundles supersede the older speculative 1.5B/3B–4B profile descriptions. Preserve the previously installed SenseVoice/Q4 bundle during migration; do not silently download a larger preset.

Each manager shows real name, full ID, role, pinned revision, expected size, installed/verified state, selected/active state, and actual disk usage including partial downloads. Download/install, switch/set active, verification/repair, safe redownload and uninstall must operate on these descriptors. A preset may select an uninstalled model only if the corresponding feature is clearly disabled until verified installation; manual activation requires compatible verified files.

Native loading must use the selected role's descriptor, not legacy hard-coded preferences. Android records role/ID/revision/native path only after successful FFI load acknowledgement. Quality recovery checkpoints include model identity so changing Quality ASR does not silently reuse a different model's completed text. Native ownership leases end only after actual worker destruction.

Uninstall is refused while a model is selected by any role or held by an active native worker. The UI identifies the precise active role(s), such as TXT/SRT or LIVE_ASR/QUALITY_ASR/MEETING_LLM, and asks the user to switch those role(s) to compatible installed model(s) before exposing destructive confirmation. Atomically rename the whole model-ID directory (including install metadata and old revisions), then remove files and report reclaimed bytes. On failure, restore the directory where possible and re-verify actual files; never trust stale `install.json` or report damaged bytes as installed. Redownload stages and verifies replacement files before replacing a working install.

Qwen3-ASR uses the vendored sherpa-onnx C API `qwen3_asr` config, official export dated 2026-03-25, revision `68818b2313fe77bd06f6a7c5068ff3ef59d02b8a`. Official GitHub bundle SHA-256 and all extracted model/tokenizer files are pinned and checked against Hugging Face. Upstream: [sherpa-onnx Qwen3-ASR models](https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/pretrained.html).

# 6. CantoMeet — Android

## 6.1 Core workflow

1. Open app.
2. Start new meeting.
3. Record real microphone audio.
4. Show live Cantonese transcript.
5. Persist audio/transcript incrementally.
6. User may add markers:
   - ★ 重點
   - ✓ 決定
   - ? 跟進
7. Stop.
8. Preserve recording immediately.
9. Run quality ASR.
10. Run local meeting intelligence.
11. Show:
    - 摘要
    - 待辦
    - 逐字稿
    - 資訊

Android has no subtitle timeline and no SRT editor.

## 6.2 Transcript model

Meeting intelligence does not need timestamps.

Store structured segments, not one giant mutable string.

Conceptual fields:

- sequence
- speakerId
- rawText
- cleanedText
- userText
- processingState

Preserve raw ASR output permanently and separately.

Never destructively replace raw ASR text.

## 6.3 Audio

Store original meeting audio efficiently.

Preferred storage:

- AAC/M4A
- 48 kHz mono
- reasonable speech bitrate

ASR working stream:

- 16 kHz
- mono
- PCM16

Do not keep hours of decoded PCM in memory or permanent storage.

## 6.4 Live + quality ASR

During meeting:

`Live ASR -> readable draft`

After a chunk completes or after Stop:

`Quality ASR -> formal transcript`

Live transcript is not authoritative.

Quality ASR output becomes the formal transcript after successful completion.

Background quality ASR may run while recording only if it does not threaten:

- recording stability
- battery
- RAM
- thermal state

Recording always has priority.

## 6.5 Long meetings

Target at least a **4-hour session architecture**.

Never:

- keep all PCM in RAM
- keep the entire meeting as one in-memory text object
- wait until the end to persist everything
- send a multi-hour transcript to the LLM in one prompt

Use segmented recording.

Conceptually:

`Meeting -> Segment 1 -> Segment 2 -> ...`

The current Android durable commit interval is 30 seconds. A segment becomes visible to the meeting database only after MediaMuxer closes it and an atomic `.m4a.part -> .m4a` rename succeeds; a process-killed open tail is retained as `.m4a.incomplete` and must not be presented as playable media.

Each segment may contain:

- sequence
- local audio path
- raw transcript
- quality transcript
- speaker
- processing status

## 6.6 Crash recovery

Support recovery from:

- app crash
- Android process termination
- low-memory kill
- restart
- unexpected shutdown as far as filesystem guarantees allow

Use:

- segmented audio
- temporary files
- atomic rename
- incremental DB writes
- safe commit boundaries
- recovery metadata

On relaunch, offer:

**Recover unfinished meeting**

One abnormal close must not destroy an entire multi-hour meeting.

## 6.7 Local meeting LLM

The LLM is fully local.

It is a task-specific meeting engine, not a general chatbot.

Extract:

- summary
- core topics
- decisions
- action items
- owners
- due dates
- follow-ups
- unresolved questions
- optional risks

If information is missing, preserve it as:

- null
- unknown
- 未指定

Never invent a deadline, owner, or decision.

## 6.8 Long-context strategy

For long meetings use hierarchical summarization.

### Stage 1 — Chunk extraction

Extract structured facts:

- topics
- decisions
- tasks
- owners
- dates
- unresolved questions

### Stage 2 — Section consolidation

Merge and deduplicate repeated facts.

### Stage 3 — Final reduction

Produce final meeting report from consolidated structured information.

Context usage must remain bounded regardless of meeting duration.

## 6.9 Speaker handling

Diarization may provide:

- Speaker A
- Speaker B
- Speaker C

The user may rename:

`Speaker A -> Peter`

Do not claim real-world speaker identification.

If reliable diarization cannot be shipped in v1 without destabilizing the product, keep the architecture compatible and hide/disable the unfinished feature.

## 6.10 Android UI

### First launch

Show:

**Choose AI Quality**

- Light
- Standard — Recommended
- High Quality

Show download size and simple performance description.

### Home

Primary action:

**開始新會議**

Below:

**Recent Meetings**

Keep the home screen uncluttered.

### Recording screen

Show:

- large elapsed time
- clear recording state
- live transcript
- large Stop button
- optional markers:
  - ★ 重點
  - ✓ 決定
  - ? 跟進

Markers attach to transcript segment IDs, not a timeline.

### Post-meeting processing

Show:

- ✓ 錄音已保存
- ✓ 即時逐字稿已保存
- ● 高精度轉錄
- ○ 整理會議重點

Allow safe background processing where Android permits.

### Meeting detail

Primary tabs:

- 摘要
- 待辦
- 逐字稿
- 資訊

#### 摘要

Show:

- core topics
- decisions
- follow-ups

#### 待辦

Show:

- task
- owner
- due date
- done state

#### 逐字稿

Support:

- search
- speaker labels
- Cantonese / cleaned text toggle

#### 資訊

Show:

- recording information
- selected model profile
- storage use
- reprocess
- delete

## 6.11 Android release model download

The main/release manifest must declare `android.permission.INTERNET` and should declare `android.permission.ACCESS_NETWORK_STATE`; debug/profile-only permission declarations do not count. SenseVoice first attempts the exact SHA-pinned official sherpa-onnx GitHub release archive, then identical pinned Hugging Face files and a hash-verified mirror. The meeting LLM uses multiple identical SHA-pinned sources. DNS/connection failures receive two bounded attempts per source before automatic fallback.

The main UI shows a concise Traditional Chinese failure, a Retry action, the current source, and technical details only under **進階資料**. Existing verified models stay untouched throughout a failed download/update.

---

# 7. CantoTranscribe — Windows

## 7.1 Core workflow

1. Launch packaged application.
2. Drop/select audio or video.
3. Choose:
   - TXT
   - SRT
4. Choose:
   - Fast
   - Balanced
   - High Accuracy
5. Start.
6. Process with bounded memory.
7. Export.
8. Open file/folder.

No LLM.

No editing timeline.

No video editing.

## 7.2 Inputs

Use packaged FFmpeg to support common formats such as:

- MP4
- MOV
- MKV
- MP3
- M4A/AAC
- WAV

End users must not need to install FFmpeg manually.

## 7.3 TXT mode

TXT contains **no timestamps**.

Flow:

`media -> decode -> Cantonese ASR -> deterministic cleanup -> TXT`

Support at least:

- Raw Cantonese
- Clean Cantonese

No LLM.

Do not claim semantic rewriting beyond deterministic functionality.

## 7.4 SRT mode

Flow:

`media -> decode -> timestamp-capable ASR -> subtitle segmentation -> SRT`

Minimal cue model:

- startMs
- endMs
- text

No timeline engine.

No subtitle editor.

## 7.5 SRT segmentation

Do not blindly output raw ASR segments.

Use:

- pauses
- punctuation
- maximum cue length
- maximum duration
- minimum duration
- reading speed
- Cantonese phrase boundaries

Avoid obviously awkward Cantonese line breaks.

## 7.6 Long media

Target at least an **8-hour media architecture**.

Never decode the whole file into RAM.

Use bounded streaming/chunk processing.

Persist progress.

Support:

- progress
- cancel
- resume

All FFmpeg decoding, model hashing/loading, PCM feeding, native inference, result polling, and native teardown must execute outside the WinUI thread. Progress returns through the UI synchronization context. CPU inference must leave processor capacity for WinUI, cancellation, FFmpeg, and the operating system instead of saturating every logical processor.

The local bounded diagnostics log records job state, chunk, model/revision/role, model load count, FFmpeg progress, native queue/final wait, last native ASR duration, memory use, cancellation, and cleanup duration. It must not record source paths or transcript content.

Store enough job state to continue interrupted work:

- source fingerprint
- selected model
- model version
- processed position
- completed results
- output status

A crash at 70% should not normally require restarting at 0%.

## 7.7 Batch

If the core workflow remains stable, support a practical queue.

Job states:

- queued
- processing
- completed
- failed
- cancelled

Do not run every heavy GPU job simultaneously by default.

Use resource-aware scheduling.

## 7.8 Windows UI

### Home

Large drag/drop zone:

**將影片或音訊拖到這裡**

Button:

**選擇檔案**

After selection show:

- file
- duration

Output:

- TXT
- SRT

Language:

**香港廣東話**

Recognition quality:

- Fast
- Balanced — Recommended
- High Accuracy

Primary button:

**開始轉錄**

Advanced may contain:

- custom dictionary
- output directory
- technical model details

### Model Management

After setup, provide a Model Management page showing:

- installed models
- active TXT model and active SRT model
- model version and byte size
- download, switch, repair/update, delete, and redownload actions
- total model storage usage

Switching a quality profile selects its mapped TXT/SRT pair; manually switching one role changes the selection to Custom without misrepresenting the quality profile.

### Processing

Show:

- filename
- progress bar
- processed duration / total duration
- current state
- small current transcript preview

Controls:

- Pause if safely supported
- Cancel

Cancel must immediately update the UI, stop FFmpeg, request native cancellation, persist resumable progress, and release a slow native engine in the background so the window remains usable.

### Completion

Show:

**✓ 轉錄完成**

Buttons:

- 開啟檔案
- 開啟資料夾

---

# 8. Design language

Use a professional, quiet design.

Avoid stereotypical neon AI gradients.

Support system Light/Dark mode.

Use:

- high contrast
- clear typography
- restrained surfaces
- large touch targets on Android
- sensible desktop spacing on Windows
- clear status/progress feedback

Use red only for real recording state.

Ensure Traditional Chinese glyph rendering.

Test:

- 嘅
- 喺
- 冇
- 咗
- 啲
- 嚟
- 噉

---

# 9. Packaging

## Windows

Production build must bundle the frontend correctly.

It must not depend on:

- localhost
- Vite dev server
- Node
- Rust
- Visual Studio
- Python
- source tree

The user must be able to copy the release directory elsewhere and launch it.

WinUI/.NET/Windows App SDK satellite folders such as `fr-FR`, `ga-IE`, `gd-GB`, `it-IT`, `ja-JP`, and `ko-KR` are localization resources, not ASR language models. Do not manually delete them. `SatelliteResourceLanguages=zh-HK;en-US` may be retained only if it materially restricts the copied publish and the copied build still passes real TXT/SRT tests; otherwise retain the complete generated localization set.

## Android

APK must include required native libraries.

Store signing may remain blocked if credentials are unavailable, but functionality must still be completed and tested.

---

# 10. Testing data

Support benchmark corpus categories:

- clean HK Cantonese
- Cantonese-English code switching
- noisy audio
- fast speech
- meeting-room audio
- names/proper nouns
- multiple speakers where applicable

Support ground-truth transcript files.

Measure where possible:

- Cantonese CER
- English token accuracy
- proper-noun accuracy
- model load time
- RTF
- peak RAM
- peak VRAM
- throughput

Do not fake metrics.

---

# 11. Licensing

Inspect redistribution requirements for:

- model weights
- sherpa-onnx
- llama.cpp
- FFmpeg
- ONNX Runtime
- CUDA redistributables
- other native libraries

Maintain:

`THIRD_PARTY_NOTICES.md`

Do not redistribute components contrary to their licenses.

Windows Production UI:
- WinUI 3
- C#
- .NET
- Windows App SDK
- C# -> P/Invoke -> Stable C ABI -> C++20

Tauri/Rust:
- retain in repository
- legacy/reference/debug only
- NOT production frontend

Production CantoTranscribe must not require:
- WebView2
- localhost
- Vite
- Node
- Rust
- Python
