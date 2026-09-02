# ACCEPTANCE_TESTS.md — Canto Suite Release Gates

## Rule

**Build success is not product completion.**

Before any final response, run every test that is technically possible and update `.docs/QA_REPORT.md`.

Allowed statuses:

- PASS
- PARTIAL
- FAIL
- BLOCKED
- NOT TESTED

A **CORE** item may be `BLOCKED` only when it depends on a genuine external human-only blocker such as unavailable physical hardware, credentials, signing keys, UAC/manual interaction, or a required reboot.

If a CORE item is `PARTIAL`, `FAIL`, or `NOT TESTED`, continue working.

---

# A. Current known release blocker

## CORE-WIN-001 — Packaged Windows UI

**Requirement**

The production `CantoTranscribe.exe` must launch without localhost or any dev server.

**Known failure from the previous build**

The packaged application showed:

- `localhost refused to connect`
- `ERR_CONNECTION_REFUSED`

**Acceptance**

1. Kill Node/Vite development servers.
2. Launch packaged `CantoTranscribe.exe` directly.
3. Confirm the full UI loads.
4. Confirm there is no localhost dependency.
5. Continue with real transcription tests.

Do not mark Windows complete until this passes.

---

# B. Android core acceptance

## CORE-AND-001 — Real microphone pipeline

Fresh install / clean state.

Flow:

`Microphone -> PCM capture -> worker isolate -> Dart FFI -> native C ABI -> real Cantonese ASR -> visible live transcript`

**PASS only if real microphone speech changes the visible transcript.**

Mocked text does not count.

## CORE-AND-002 — Meeting persistence

1. Start meeting.
2. Record real audio.
3. Confirm segmented audio is persisted.
4. Confirm transcript segments are persisted incrementally.
5. Stop.
6. Reopen meeting.
7. Confirm recording/transcript remain available.

## CORE-AND-003 — Quality transcription

After Stop:

1. Run quality ASR.
2. Confirm formal transcript is produced.
3. Confirm raw ASR text is preserved separately.
4. Confirm quality text does not destructively overwrite raw text.

## CORE-AND-004 — Local meeting LLM

After quality transcription:

1. Run the selected local LLM.
2. No cloud API.
3. No localhost server.
4. No Python runtime.
5. Produce structured:
   - summary
   - core topics
   - decisions
   - action items
   - owners
   - due dates
   - follow-ups
   - unresolved questions
6. Missing owner/date remains unknown/null/未指定.

## CORE-AND-005 — Long-context summarization

Verify bounded hierarchical processing:

`transcript chunks -> structured extraction -> section consolidation -> final reduction`

Do not send an entire simulated multi-hour transcript as one giant prompt.

## CORE-AND-006 — Crash recovery

Verify recovery metadata and partial-session recovery.

At minimum test an interrupted/aborted session and relaunch.

The architecture must not depend on graceful shutdown to preserve the entire meeting.

## CORE-AND-007 — Model first-launch flow

Verify:

`hardware detection -> Low/Standard/High recommendation -> user selection -> download -> resume -> SHA-256 -> atomic install`

After download, disable network and continue using the app.

## CORE-AND-008 — Offline primary workflow

After models are installed:

1. Disable network.
2. Record real Cantonese.
3. Obtain live transcript.
4. Stop.
5. Complete quality transcription.
6. Generate local meeting summary.
7. Reopen saved meeting.

All steps must work without network.

## CORE-AND-009 — Android UI flow

Verify:

`Home -> 開始新會議 -> Recording -> Stop -> Processing -> 摘要/待辦/逐字稿/資訊`

Normal UI must not require the user to understand ONNX/GGUF/Q4/INT8/CUDA.

## CORE-AND-010 — Release model download resilience

1. Build/install a fresh ARM64 **release** APK.
2. Verify the merged release manifest includes `android.permission.INTERNET` and `android.permission.ACCESS_NETWORK_STATE`.
3. On a normal physical-device network, download and SHA-256 verify all required ASR/LLM models.
4. Force the primary source to fail DNS/connection and verify two bounded attempts followed by the next source.
5. Verify a valid same-identity `.part` file resumes after source fallback.
6. Verify a failed attempt does not destroy an existing verified model.
7. Verify the main error is concise Traditional Chinese with Retry; raw exceptions appear only under Advanced.

---

# C. Windows core acceptance

## CORE-WIN-002 — Model setup

Verify:

`hardware detection -> Fast/Balanced/High Accuracy recommendation -> user selection -> model download -> resume -> SHA-256 -> atomic install`

After model installation, disable network.

## CORE-WIN-003 — Real TXT export

1. Launch packaged app directly.
2. Import real audio/video.
3. Select TXT.
4. Process with real ASR.
5. Export `.txt`.
6. Confirm TXT contains no timestamps.
7. Confirm output is usable.

No mocked transcript.

## CORE-WIN-004 — Real SRT export

1. Launch packaged app directly.
2. Import real audio/video.
3. Select SRT.
4. Use timestamp-capable ASR.
5. Export `.srt`.
6. Validate SRT syntax.
7. Inspect timing against the source.
8. Confirm segmentation is not just a blind dump of raw ASR segments.

## CORE-WIN-005 — Offline runtime

After model installation:

1. Disable network.
2. Launch packaged app.
3. Import media.
4. Export TXT.
5. Export SRT.

No localhost.

No external server.

## CORE-WIN-006 — Long media bounded processing

Verify the implementation does not load full decoded media into RAM.

Use chunked/streaming processing and persisted progress.

A simulated long-media stress test is acceptable if clearly labelled simulated.

## CORE-WIN-007 — Resume

Start a long job, interrupt it, restart, and verify safe resume from persisted progress when supported by the engine.

A restart from 0% should not be the normal design.

## CORE-WIN-008 — Packaging independence

Copy the Windows release directory to a clean ordinary path and launch it.

It must not require:

- source tree
- Node
- pnpm
- Vite
- Rust
- Visual Studio
- Python
- localhost

Required runtime/resources must be packaged correctly.

## CORE-WIN-009 — Model roles, profiles, and management

1. Verify Fast, Balanced, and High Accuracy resolve to three genuinely different SHA-pinned model bundles for both TXT and SRT.
2. Verify model role is separate from quality profile and TXT/SRT active choices can differ.
3. Verify the Model Management page shows installed models, active TXT/SRT, revision/size, storage use, and working download/switch/repair-update/delete/redownload actions.
4. Verify Whisper Base is not mapped or labelled as High Accuracy.
5. Run real native Cantonese audio through every newly enabled Balanced/High model before describing it as validated.
6. Do not manually remove Windows App SDK localization folders. Keep a satellite-language restriction only after copied-portable real TXT/SRT tests pass.

---

# D. Shared quality acceptance

## CORE-SHARED-001 — Cantonese display

Verify the application can display:

- 嘅
- 喺
- 冇
- 咗
- 啲
- 嚟
- 噉

## CORE-SHARED-002 — Cantonese-English code switching

Use a real or ground-truth sample containing mixed Cantonese and English.

Record observed output.

Do not invent accuracy claims.

## CORE-SHARED-003 — Custom dictionary

Verify at least one preferred display mapping such as:

`union design -> UNION Design HK`

## CORE-SHARED-004 — Model integrity

For every downloaded model used in QA, record:

- model id
- source
- revision
- SHA-256
- local path
- license/reference

## CORE-SHARED-005 — No private content upload

Inspect the runtime implementation and verify there is no inference path that uploads:

- audio
- transcript
- summary
- speaker information

## CORE-SHARED-006 — Clean Cantonese and Hong Kong Traditional

Using `曉譽中層c室.txt` when available, otherwise a committed/sanitized equivalent, verify **乾淨廣東話 + 香港繁體**:

1. Uses the selected script before dictionary/cleanup.
2. Contains no unintended Simplified fragments such as `后`/`柜` where `後`/`櫃` is intended.
3. Preserves `我哋 / 佢哋 / 唔 / 冇 / 喺 / 嘅 / 啲 / 咗`.
4. Conservatively collapses excessive repeated fillers/restarts and produces readable punctuation/spacing.
5. Applies preferred terminology consistently.
6. Leaves a deliberately injected semantic ASR error unchanged and makes no claim that deterministic cleanup corrected it.

---

# E. Build acceptance

## BUILD-AND-001

Run:

- `flutter analyze`
- Dart tests
- native Android/C++ build
- release APK build

Fix actual compiler/linker errors.

## BUILD-WIN-001

Run:

- Rust tests
- frontend tests
- native C++ tests
- Tauri production build

Fix actual compiler/linker/package errors.

---

# F. Stress acceptance

## STRESS-AND-001

Architecture target: 4-hour meeting.

If a 4-hour physical run is impractical:

- perform shorter real-device runtime testing where possible;
- perform deterministic long-session simulation;
- label simulated vs physically observed results accurately.

## STRESS-WIN-001

Architecture target: 8-hour media.

If an 8-hour physical run is impractical:

- perform shorter real processing;
- perform deterministic long-media simulation;
- label simulated vs physically observed results accurately.

---

# G. Final release gate

The project may be described as **core-complete** only when:

- all CORE items are `PASS`, or
- the only non-PASS CORE items are genuine externally `BLOCKED` items.

Do not call:

- `PARTIAL`
- `FAIL`
- `NOT TESTED`

core items complete.

The final response must list:

- Android APK path
- Windows package path
- actual models tested
- actual hardware tested
- PASS/PARTIAL/FAIL/BLOCKED/NOT TESTED summary
- known limitations
- Git status

CORE-WIN-UI-001

Production Windows application must be WinUI 3.

PASS:
- WinUI CantoTranscribe.exe launches
- no WebView2
- no localhost
- no Vite/Node
- real media -> real ASR -> TXT works
- real media -> timestamp ASR -> SRT works

Tauri build success does not count as production acceptance.
