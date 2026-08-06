# CLAUDE.md — WhisperSubs

## Overview
Jellyfin plugin for AI-powered subtitle generation using whisper.cpp. By default all transcription runs on this Jellyfin server (no third-party services). Optionally, the v4.0 worker pool distributes transcription across additional self-hosted — or cloud — OpenAI-compatible workers; when workers are configured, the plugin extracts audio locally and sends it over HTTP to the workers you set up. Supports GPU acceleration (CUDA, Vulkan, ROCm).

- **Plugin GUID:** `97124bd9-c8cd-4a53-a213-e593aa3fef52`
- **Target:** Jellyfin 10.11+ / .NET 9.0

## Tech Stack
- C# / .NET 9.0
- Jellyfin Plugin API (10.11+)
- whisper.cpp (local AI inference)
- xUnit for testing (`WhisperSubs.Tests/`)
- GitHub Actions for CI/build/release

## Development
```bash
dotnet build
dotnet test

# Build release DLL
dotnet publish -c Release
```

Plugin repository manifest: `https://geiserx.github.io/whisper-subs/manifest.json`

## Architecture

```
Plugin.cs                          Entry point, IHasWebPages (embeds config UI)
├── Configuration/
│   ├── PluginConfiguration.cs     User-editable settings (model/binary path, language, worker pool, job timeouts, etc.)
│   ├── SubtitleMode.cs            Full / ForcedOnly / FullAndForced / TranslationOnly
│   └── WhisperWorker.cs           One configured remote worker row (endpoint, key, model, concurrency, cost)
├── Api/
│   ├── SubtitleController.cs      Admin REST API under /Plugins/WhisperSubs/* (class-level RequiresElevation)
│   └── SubtitleRequestController.cs  User subtitle-request endpoints (bare [Authorize], gated by AllowUserRequests) (#112)
├── Controller/
│   ├── SubtitleManager.cs         Orchestrator: language detection -> audio extraction -> transcription -> save
│   ├── SubtitleQueueService.cs    Named-tier priority queue feeding an N-slot worker-pool dispatcher (EnsureDispatching)
│   ├── PriorityLanes.cs           Pure multi-lane FIFO priority engine, one lane per tier (#112)
│   └── Workers/                   v4.0 distributed transcription worker pool
│       ├── WorkerPool.cs          Live pool: workers + in-flight counts behind a ΣMaxConcurrency semaphore (replaces the old TranscriptionLock)
│       ├── WorkerRegistry.cs      Builds the pool from config (news up providers) — orchestration
│       ├── WorkerPlan.cs          Pure back-compat: no config→local-only, legacy URL→remote-only, list→those+optional local
│       ├── WorkerScheduling.cs    Pure cost-weighted routing: cheapest capable free worker (local preferred, burst to paid)
│       ├── WorkerJob.cs           Pure config→JobRequirements mapping (translate capability)
│       ├── WorkerModel.cs         WorkerCapabilities / WorkerSlot / WorkerStatus / JobRequirements value types
│       ├── ITranscriptionWorker.cs  Worker interface (Id, Name, Provider, Capabilities) + default record
│       ├── WorkerConfigValidation.cs  Pure per-row validation (URL, concurrency, cost)
│       └── SyntheticAudio.cs      Silent-WAV generator for the worker Test-connection probe
├── Providers/
│   ├── ISubtitleProvider.cs       Provider interface (TranscribeAsync, DetectLanguageAsync, RequiresSpeechAlignmentOptIn)
│   ├── WhisperProvider.cs         Local whisper.cpp integration (finds binary, runs process, reads SRT output)
│   ├── RemoteWhisperProvider.cs   OpenAI-compatible HTTP worker (POSTs audio to /v1/audio/{transcriptions,translations})
│   └── SubtitleProviderFactory.cs Builds the local (or legacy single-remote) provider from config
├── ScheduledTasks/
│   └── SubtitleGenerationTask.cs  Jellyfin scheduled task for auto-generation (drains priority lanes, then sweeps)
├── Web/
│   └── configPage.html            Admin UI (embedded resource) — vanilla JS, Jellyfin emby-* components
└── worker/                        Separate deliverable: an example OpenAI-compatible whisper.cpp worker Docker image
```

## Data Flows

### Full Subtitles

1. **Language detection** — `SubtitleManager.DetectAudioLanguagesAsync` calls FFprobe to read audio stream language tags. ISO 639-2/B codes are normalized to 639-1 (e.g., `spa` -> `es`).
2. **Audio extraction** — FFmpeg extracts 16kHz mono PCM WAV from the media file to a temp path.
3. **Transcription** — `WhisperProvider.TranscribeAsync` invokes `whisper-cli` as a child process with the model and audio file. Output is an SRT file. When `EnableVad` is on (default), it also passes `--vad --vad-model <path>` and the `--vad-*` tuning flags (via `AppendVadTuning`) so whisper.cpp's native Silero VAD snaps cue starts to real speech onset at transcription time (the primary fix for gapless segments / issue #78). The VAD model is selectable (`VadModelVersion`: v5.1.2 default / v6.2.0 opt-in; issue #105) and lives in `whisper/vad/` (~885 KB); `WhisperSetupService.ResolveVadModelPath` resolves the version-aware path, and the flag is only added when the file exists.
4. **Timing corrections** — Before saving, `SubtitleManager.ApplyTimingCorrectionsAsync` first runs `WhisperProvider.AlignSrtToSpeech` against FFmpeg `silencedetect` segments while both are in the extracted WAV's zero-based timebase, then `CompensateAudioOffset` shifts the result by the selected audio stream's effective `start_time`. The speech pass is the **fallback** for gapless segments — used when local native VAD (`EnableVad`) is off, or when `AlignSubtitlesToSpeechWithVad` explicitly layers it onto VAD/provider-owned timestamps. Every path explicitly maps the requested/default-or-first audio stream so extraction and offset probing agree. Resume probes container `format.start_time` and computes input `-ss = playback resume - effective compensation + stream start - format start`, then re-anchors the fresh tail once. Supports local and timestamped remote/worker output (full + translated), not forced.
5. **Save** — The SRT content is written alongside the media as `<filename>.<lang>.generated.srt`.
6. **Metadata refresh** — `item.RefreshMetadata()` tells Jellyfin to pick up the new subtitle file.

### English Translation (v3.11.0.0+)

When `EnableTranslation` is enabled in config (and SubtitleMode is Full or FullAndForced):

1. **English audio check** — FFprobe checks if any audio stream is already English. If so, translation is skipped. If FFprobe returns `"auto"`, whisper detects the language from a 30-second sample; if English with p>=0.3, skip.
2. **Existing file check** — If `<filename>.en.translated.srt` already exists, skip. In `"auto"` mode, also checks for existing English subtitle files.
3. **Source language selection** — Uses the first non-English language from resolved languages. In `"auto"` mode, uses whisper-detected language if available.
4. **Transcription with translation** — Calls `WhisperProvider.TranscribeAsync(audioPath, sourceLanguage, ct, translate: true)`, which adds `--translate` to whisper-cli. The `language` argument must be the SOURCE language (e.g., `es`), not `en`.
5. **Save** — Written as `<filename>.en.translated.srt`.

### Forced Subtitles (v3.0.0+)

Forced subtitles capture only foreign-language dialogue segments (e.g., Russian dialogue in an English film):

1. **VAD** — FFmpeg `silencedetect` splits audio into speech chunks using `-30dB:d=0.5` thresholds.
2. **Per-chunk language detection** — Each chunk is fed to `WhisperProvider.DetectLanguageAsync` (`--detect-language` mode).
3. **Foreign segment identification** — Chunks where `detectedLang != primaryLang && probability >= 0.3` are marked foreign. Adjacent chunks are merged.
4. **Selective transcription** — Only foreign segments are extracted and transcribed individually, with timestamps offset to match original timeline.
5. **Save** — Written as `<filename>.<lang>.forced.generated.srt`.
6. **No-foreign marker** — If zero foreign chunks detected, a `.forced.noforeignlang` empty marker file is written to skip on future runs.

**SubtitleMode** enum: `Full` (0, default), `ForcedOnly` (1), `FullAndForced` (2), `TranslationOnly` (3). `TranslationOnly` skips native-language transcription entirely and produces only an English translated subtitle via a single `--translate` pass (medium/large models recommended); the scheduled task's `needsTranslation` gate is on whenever mode is `TranslationOnly`, or `EnableTranslation` in `Full`/`FullAndForced`.

## API Endpoints

Admin endpoints live in `SubtitleController` (class-level `[Authorize(Policy="RequiresElevation")]`). The user-request endpoints live in a SEPARATE `SubtitleRequestController` with a bare `[Authorize]` (any authenticated user), gated further by `AllowUserRequests` — see issue #112 below.

| Method | Path | Auth | Returns | Notes |
|--------|------|------|---------|-------|
| `GET` | `/Plugins/WhisperSubs/Libraries` | admin | `LibraryInfo[]` | All virtual folders |
| `GET` | `/Plugins/WhisperSubs/Libraries/{id}/Items?startIndex=0&limit=50` | admin | `PagedItemResult` | Movies/Episodes with subtitle status |
| `POST` | `/Plugins/WhisperSubs/Items/{id}/Generate?language=auto` | admin | 202 Accepted | Enqueues (forced) at `AdminRequestTier` |
| `POST` | `/Plugins/WhisperSubs/Items/{id}/GenerateAll?language=auto` | admin | 202 Accepted | Expands a container, enqueues at `AdminRequestTier` |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/Status?language=auto` | admin | `SubtitleStatus` | Checks for `.generated.srt` on disk |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/AudioLanguages` | admin | `string[]` | FFprobe-detected languages |
| `GET` | `/Plugins/WhisperSubs/Queue` | admin | `{isProcessing, currentItem, remaining, processed, tiers, pendingRequests, pending[], workers[], …}` | Live queue status + per-tier breakdown (#112); `pending[]` = inbound items in run order, `workers[]` = live per-worker load (v4.0) |
| `POST` | `/Plugins/WhisperSubs/Workers/TestConnection` | admin | `{ok, status, latencyMs, message}` | Probes a worker endpoint with a silent WAV before saving; never throws, SSRF-hardened (v4.0) |
| `GET` | `/Plugins/WhisperSubs/Models` | admin | `ModelInfo[]` | `.bin` files in model directory |
| `POST` | `/Plugins/WhisperSubs/RunTask` | admin | 200 | Triggers the scheduled task |
| `GET` | `/Plugins/WhisperSubs/Requests` | admin | request[] | All user requests (no file paths) (#112) |
| `POST` | `/Plugins/WhisperSubs/Requests/{id}/Approve` | admin | 200 | Approve → enqueue at user tier (#112) |
| `POST` | `/Plugins/WhisperSubs/Requests/{id}/Decline` | admin | 200 | Decline a pending request (#112) |
| `GET` | `/Plugins/WhisperSubs/Requests/Capabilities` | user | `{enabled, autoApprove, userTier, …}` | Client feature-gate (#112) |
| `POST` | `/Plugins/WhisperSubs/Items/{id}/Request?language=auto` | user | 202 / 200 / 429 / 503 | Create a request (quota/cap/dedup enforced) (#112) |
| `GET` | `/Plugins/WhisperSubs/Requests/Mine` | user | request[] | The caller's OWN requests only (#112) |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/RequestStatus` | user | `{enabled, state}` | The caller's active request state for an item (#112) |

## Queue System

Rewritten in v4.0 around a shared worker pool. The former single-worker drain loop (`EnsureDraining` + a global `TranscriptionLock(1,1)`) is replaced by an N-slot dispatcher over a `WorkerPool`; with the default configuration (one local worker of `MaxConcurrency` 1) it admits exactly one job at a time — **byte-identical to the old lock**.

- **`Enqueue()`** — Fire-and-forget. `POST /Items/{id}/Generate` returns HTTP 202 immediately. It adds into `PriorityLanes<T>` (one FIFO lane per named tier; strongest tier drained first). De-dup identity is `(item, language)`; a re-request merges (`Force` OR'd, tier promoted) rather than queuing twice (#112).
- **`EnsureDispatching()`** — Replaces `EnsureDraining`. Starts the single background dispatch loop (`Interlocked.CompareExchange` guard), fetches the shared `WorkerPool` via `GetPool` (built from config by `WorkerRegistry`), and runs `DispatchDrainAsync`. The pool build happens **inside** the try so a bad-config throw resets `_isDraining` and does not wedge the dispatcher or busy-loop (v4.0.1).
- **`WorkerPool` = the concurrency gate** — A `SemaphoreSlim(ΣMaxConcurrency)` backpressure semaphore across all workers (default = 1). `DispatchDrainAsync` acquires a slot FIRST, then dequeues the current highest-priority item, so an item leaves the persisted queue only when a worker is ready to run it now (same crash-durability as the old single-lock loop). Up to ΣMaxConcurrency jobs run concurrently; each is fired via `Task.Run` (un-tokened so the `finally` always frees the slot + reservation).
- **Atomic dequeue+reserve** — `TryDequeuePriority` moves an identity from the lanes to the in-flight set under `_dispatchGate` (the same lock `Enqueue` takes for its in-flight check + lane-add), so a re-enqueue landing in the dequeue→reserve window can never make the pool dispatch the same `(item,language)` twice — two workers writing one `.srt`.
- **Cost-weighted routing** — `WorkerScheduling.Pick` selects the cheapest capable free worker: `CostWeight` dominates the score (0 = free local, always preferred), then in-flight load, then a stable priority tiebreak. A paid/cloud worker (`CostWeight > 0`) is only ever chosen when every local worker is saturated (burst).
- **Fail-fast on impossible jobs** — `WorkerPool.HasCapableWorker(requirements)` is checked before acquiring a slot; if no worker can EVER serve the job (e.g. translation enabled but every worker is transcribe-only) the whole queue fails fast with a clear per-item error instead of blocking a slot forever.
- **Two consumers, one pool** — The background dispatcher (`EnsureDispatching`) and the scheduled task's priority drain (`DrainPriorityAsync`) both use the SAME pool via `GetPool`, so the global concurrency limit holds no matter which path started the work. `GetPool` only rebuilds at a session start when the other consumer is idle and no jobs are in flight, so a config change (added worker, changed model path) takes effect between drain sessions without splitting the gate.
- **Persisted to disk** — Queue is saved to `queue.json` (`/config/data/WhisperSubs/queue.json`) on every enqueue/dequeue via an atomic temp+rename (v4.0.1). On startup, `RestoreQueue()` reloads pending items and normalises a pre-#112 tier-less entry to `High`.
- **Per-language error isolation**: If whisper fails on one language, remaining languages still proceed. Only `OperationCanceledException` propagates.
- **Killed items are not auto-retried** — they fall out of the queue. The scheduled task will eventually re-process them.

## Partial SRT & Resume on Restart

1. **WhisperProvider** kills the whisper process and returns whatever partial SRT content was written to disk.
2. **SubtitleManager** saves the partial SRT as `<filename>.<lang>.generated.srt`.
3. On next run, detects existing file, parses last timestamp via `WhisperProvider.ParseLastSrtTimestamp()`, compares against media duration.
4. If within 30 seconds of media end, considered **complete** and skipped.
5. If partial, FFmpeg extracts audio from resume offset (`-ss`), whisper transcribes remainder, entries are offset-adjusted and appended.

## whisper.cpp Integration

### Binary Discovery

`WhisperProvider.FindWhisperExecutable()` tries candidates in order:
1. The configured `WhisperBinaryPath` (if set)
2. `whisper-cli` (PATH)
3. `main` (PATH)
4. `whisper` (PATH)

### Build for Docker

The whisper-cli binary must match the Jellyfin container environment (Debian Trixie/Sid).

```bash
# CPU-only build
apt-get install -y git cmake g++ make
git clone --depth 1 --branch v1.8.4 https://github.com/ggml-org/whisper.cpp.git /tmp/whisper
cd /tmp/whisper
cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
```

```bash
# Vulkan (GPU) build
apt-get install -y git cmake g++ make pkg-config libvulkan-dev glslc
git clone --depth 1 --branch v1.8.4 https://github.com/ggml-org/whisper.cpp.git /tmp/whisper
cd /tmp/whisper
cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DGGML_VULKAN=ON
cmake --build build --config Release -j$(nproc)
```

Key flags:
- **`-DBUILD_SHARED_LIBS=OFF`** — MANDATORY. Static link. Without this: `libwhisper.so.1: cannot open shared object file`.
- **`-DGGML_VULKAN=ON`** — Intel/AMD GPU via Vulkan. Needs `libvulkan-dev` + `glslc` at build time.
- **`-DGGML_CUDA=ON`** — NVIDIA GPU. Needs CUDA toolkit.
- **Common failure**: `Could NOT find Vulkan (missing: glslc)` — `glslang-tools`/`glslang-dev` do NOT provide `glslc`. Need `glslc` or `shaderc` package.

### GPU Passthrough (Docker)

```yaml
devices:
  - /dev/dri
environment:
  - VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/intel_icd.json
```

`VK_ICD_FILENAMES` is critical — without it, Vulkan loader fails inside container. Set to:
- **Intel:** `/usr/share/vulkan/icd.d/intel_icd.json`
- **AMD:** `/usr/share/vulkan/icd.d/radeon_icd.json`

The GPU wrapper script (`whisper-cli-gpu`) is self-healing: checks for Vulkan ICD on each invocation.

### Configuration — Thread Count

`WhisperThreadCount` controls `-t N` flag. Default `0` = whisper's internal default (4 threads). Set to CPU core count for faster transcription.

### Configuration — Subtitle Line Length (issue #151)

`SubtitleMaxLineLength` emits `--max-len N --split-on-word` from `BuildTranscribeArguments`. Default `0` = **unset**, leaving whisper.cpp's own default of 0 (unlimited) — so the emitted command line for an existing install is byte-identical. Non-positive is treated as unset, mirroring the `VadTuning` sentinel convention; the two flags are always emitted together (a bare `--max-len` splits on TOKEN boundaries and can cut mid-word, a bare `--split-on-word` is inert). `--max-len` is deliberately NOT in `DeniedArgs`, so a value in Custom Whisper Arguments still supersedes the setting (custom args are appended last; whisper-cli takes the last value) — same contract as the VAD tuning flags.

Why it exists: whisper applies no character cap of its own, so when the model drops into its documented "no-punctuation mode" an entire utterance lands in one enormous run-on cue. **Local `whisper-cli` only** — a remote/worker endpoint owns its own segmentation, so the worker image carries the twin knobs `WHISPER_MAX_LEN` / `WHISPER_SPLIT_ON_WORD` (worker ≥ 0.1.4, same opt-in default). Note this is a guard for the pathological case, not a fix for missing punctuation itself — the usual root causes there are a small model or a wrong language guess. Speaker diarization ("multi narrator", issue #151's original ask) is explicitly **out of scope**: Whisper computes no speaker information, whisper.cpp's `-di` is a stereo left/right energy comparison (useless on centre-mixed film dialogue) and `-tdrz` requires an English-only `small.en-tdrz` model that emits only an anonymous `[SPEAKER_TURN]` marker.

## Performance Benchmarks

Tested with 2h15m film (8107s audio), large-v3 model, 5-beam search on i5-14500:

| Config | Wall time | Real-time factor |
|--------|-----------|------------------|
| CPU, 4 threads (default) | ~7h+ (est.) | ~3.2x |
| CPU, 16 threads | 1h48m | 0.80x |

GPU offloading is critical — encode step dominates and is highly parallelizable. Vulkan on Intel UHD 770 yields 2-4x overall speedup.

**GPU disabled for language detection** (by design): per-chunk process spawning makes GPU init overhead exceed the detection work (~21s/chunk with GPU vs ~15s/chunk CPU-only).

## CI/CD

GitHub Actions workflow (`.github/workflows/build-release.yml`) on push to `main`:
1. Builds the DLL
2. Packages into versioned ZIP
3. Creates GitHub Release
4. Updates `manifest.json` with checksum
5. Deploys to GitHub Pages

Version is read from `<Version>` in `WhisperSubs.csproj`. Bump there before pushing.

**The `manifest.json` in source tree is NOT authoritative** — CI generates a fresh one. The checked-in copy is stale reference only.

**CI lesson — whisper binary cache poisoning (v3.11.3-v3.12.0):** All x64 matrix jobs share the same runner. Use per-variant paths (`/tmp/whisper-${{ matrix.variant }}/...`) so each job's cache is isolated. `softprops/action-gh-release` silently skips missing files.

**Worker image (separate deliverable, v4.0):** `worker/` builds an example OpenAI-compatible whisper.cpp `whisper-server` (Vulkan, static) Docker image, published to Docker Hub as `drumsergio/whisper-subs-worker` with **semver tags only (never `:latest`)** — see `worker/README.md` ("Building & publishing"). The Dockerfile pins `WHISPER_VERSION=v1.8.4`, kept **in lockstep** with the plugin's own whisper.cpp pin in `build-release.yml`; bump both together. NOTE: a dedicated GitHub Actions publish workflow (e.g. `.github/workflows/worker-image.yml`) is **not committed yet** — the repo currently ships only `build-release.yml`, `ci.yml`, and `stale.yml`; the image is built per the `worker/README.md` conventions until that workflow lands. **Decoding accuracy (worker ≥ 0.1.2):** the adapter's whisper-server defaults now MIRROR the plugin's local `whisper-cli` path — `-sns`, `-mc 0`, and native Silero **VAD on** (model baked into the image at `/opt/whisper-vad/`, deliberately outside the `/models` volume so a mounted cache can't shadow it) — so remote and local subtitles match in quality; each is an env-var (`WHISPER_SUPPRESS_NON_SPEECH`/`WHISPER_MAX_CONTEXT`/`WHISPER_VAD`), and `WHISPER_BEAM_SIZE=5` opts into beam search (slower, a bit more accurate). Documented in `worker/README.md`.

## Known Issues

- **Hallucination on non-speech audio**: During music/credits/silence, large-v3 generates nonsense. The `--suppress-non-speech` (`-sns`) flag helps but doesn't eliminate it.
- **Language detection false positives**: At p>=0.3, concert/music audio can be misidentified as foreign language. Consider raising threshold for non-dialogue content.
- **Hallucination signatures** (Spanish): "La Iglesia de Jesucristo de los Santos de los Ultimos Dias", "Suscribete al canal", "Subtitulos por".
- **whisper.cpp writes SRT only at completion** — not incrementally. Mid-process kills produce no partial file.
- **Subtitle timing**: whisper.cpp emits gapless segments, so a line can show during the pause before its speech. Primary fix (issue #78) is whisper-cli's native **Silero VAD**: `EnableVad` (default on) injects `--vad --vad-model <path>` so cue starts snap to real speech onset at transcription time. The VAD model is selectable via `VadModelVersion` (`v5.1.2` default / `v6.2.0` opt-in; issue #105), backed by `ModelCatalog.VadModels`/`ResolveVadModel`; models live in `whisper/vad/` (~885 KB each) and auto-download. `ResolveVadModelPath(configuredPath, versionKey)` is version-aware: the selected version's file takes precedence over a stale legacy path, but a genuine external path override is still honoured. The older energy-based `AlignSubtitlesToSpeech` (`WhisperProvider.AlignSrtToSpeech` via FFmpeg `silencedetect`) is the **fallback** — used when VAD is off, or layered on top of VAD when the `AlignSubtitlesToSpeechWithVad` opt-in is on (default off; issue #78 — native VAD improves transcription but doesn't always fix whisper's slightly-early cue starts; the shipped silencedetect fix proved unreliable on real TV content, hence opt-in). The gate is the pure `SubtitleManager.ShouldAlignToSpeech(alignEnabled, requiresOptIn, alignWithVad)`. `CompensateAudioOffset` (default on) still shifts by audio `start_time`. See `WhisperProvider.AlignSrtToSpeech` + `SubtitleManager.ApplyTimingCorrectionsAsync`. Six `--vad-*` tuning parameters are exposed as `Vad*` config fields (`VadThreshold`, `VadMinSpeechDurationMs`, `VadMinSilenceDurationMs`, `VadMaxSpeechDurationS`, `VadSpeechPadMs`, `VadSamplesOverlap`) and emitted by `WhisperProvider.AppendVadTuning`; a negative sentinel means "unset → whisper default", so default command output is unchanged. A `--vad-*` tuning flag placed in Custom Whisper Arguments supersedes the structured setting (args appended last; whisper-cli takes the last value); tuning flags were removed from `DeniedArgs` (`--vad`/`--vad-model` remain denied). (Issue #105.) Applies to full + translated, not forced/lyrics.
- **Remote timestamped responses + alignment (issues #138/#139)**: `RemoteWhisperProvider` preserves `response_format=srt` as the first choice, then negotiates to `verbose_json` with segment granularity only for an explicit format error; the successful format is cached separately for transcription and translation. A 2xx untimed JSON response gets one timestamped retry. Bodies are streamed through hard limits; timestamped JSON validates duration-bounded, ordered, non-empty segments (`start`, `end`, normalized `text`) and converts them to SRT using invariant millisecond formatting. Raw responses must contain a real SRT cue; plain `json` cannot produce synchronized subtitles. Translation omits the source-language form field for OpenAI/Groq compatibility. Remote/provider-owned timing sets `RequiresSpeechAlignmentOptIn=true`: the local FFmpeg forward-snap remains opt-in via `AlignSubtitlesToSpeechWithVad`, while `ApplyTimingCorrectionsAsync` no longer skips work just because the legacy remote URL is configured. Test Connection pins DNS, bounds/validates the body, and mirrors SRT→timestamped-JSON negotiation.
- **Read-only web root — File Transformation integration (issue #108):** Direct on-disk index.html injection fails on read-only containers. When the third-party File Transformation plugin (by IAmParadox27, `https://github.com/IAmParadox27/jellyfin-plugin-file-transformation`) is installed, whisper-subs automatically registers a serve-time injection via reflection-only integration. Pure helper `Plugin.NormalizeInjection(html)` strips variants → inserts exactly one canonical script tag before `</head>` (no `</head>` → input unchanged; never null; idempotent/self-healing). Wire contract is `WhisperSubs.Web.WebFileTransformation.TransformIndexHtml` (public static `string(FileTransformationInput input)`, must never throw — FT pipeline has no error handling). Registration is via `FileTransformationRegistrationService` (IHostedService via `PluginServiceRegistrator`; re-registers at every boot via stable ID, fire-and-forget + bounded retry (3 attempts, growing delay); detection + registration live in `WebFileTransformation.TryRegister`, which never throws — failures land in `FileTransformationState` for the status panel). Pure `Plugin.ResolveInjectionMode(scriptTagPresent, ftRegistered)` → mode string ("direct" / "file-transformation" / "direct+file-transformation" / "none") on `ScriptInjectionStatus.Mode`, alongside `FileTransformation*` fields and `ServedHtmlVerified`; served-HTML verification is `SubtitleController.ProbeServedIndexHtmlAsync` (GET `GetApiUrlForLocalAccess(allowHttps:false)` + `/web/index.html`, grep for the script marker; best-effort — any failure → "unknown", never fails the endpoint). Direct on-disk injection remains default; both mechanisms coexist safely (serve-time transform is idempotent, will strip stale variants and inject exactly one tag, preventing double injection). Reflection-based only (no compile-time reference to FT; `AssemblyLoadContext` isolation); payload built with FT's own `JObject` via reflected Parse — no Newtonsoft dependency in whisper-subs. The config-page "Re-inject" button re-attempts BOTH File Transformation registration and the direct write, so installing FT after whisper-subs is picked up without a second Jellyfin restart (FT's own install still needs its one restart).
- **Skip-cache for repeat scans (issue #110)**: the scheduled task re-probed the filesystem for every candidate item every run (~5 `Directory.GetFiles`/`Exists` over media+metadata dirs per item, `SubtitleGenerationTask.cs:210-231` → `SubtitleManager.FindGeneratedFiles/GeneratedFileExists`); a 13k-item library took ~40 min just to re-confirm existing subtitles (the plugin's own `.generated.srt` sidecars keep Jellyfin's `HasSubtitles=false`, so the coarse line-137 gate can't skip them, and `HasSubtitles`-based query filtering wouldn't help — verified against v10.11.11 source). Fix = a persisted per-item skip cache (`Controller/SubtitleSkipCache.cs`, JSON under `DataFolderPath/skip-cache.json`, atomic temp+rename): a run skips the probe when the cached entry's **change token** (`item.DateLastSaved.Ticks` — bumped by any Jellyfin re-save incl. this plugin's `RefreshMetadata`) matches AND the whole-cache **settings signature** (`ComputeSignature`: mode + translation + forced/image/skip toggles + lyrics + default language) matches AND it's within the **backstop TTL** (`SkipCacheExpiryDays`, default 30 — catches a sub deleted outside Jellyfin). A miss always falls back to the full evaluation (never defaults to skip — preserves the #82/#83 bias-toward-generating). The completeness switch is extracted to the pure `SubtitleManager.IsSubtitleSetComplete(mode, needsTranslation, full, forced, translated)`. Persisted in `finally` (survives cancellation/pause), pruned to the ENUMERATED candidate set (not the reached set) so an interrupted run keeps valid entries — deliberately NOT a global date high-water mark (which would orphan unscanned items on the routine PauseOnPlayback interruption). Config `CacheSkippedItems` (default on) + `SkipCacheExpiryDays` (30); `POST Setup/ClearSkipCache` + config-page button. Chosen over the user's raw 30-day TTL (staleness) and server-side query filtering (blind to the plugin's own un-indexed sidecars) after a 6-agent research panel; mirrors intro-skipper's ItemId+ConfigHash change-token cache. The task class is `[ExcludeFromCodeCoverage]` (CI's `--collect` ignores coverlet.runsettings, so only the attribute applies the exclusion); logic lives in the unit-tested `SubtitleSkipCache`/`IsSubtitleSetComplete`. (Issue #110.)
- **Subtitle Request System — named-tier priority queue + user requests (issue #112, v3.28.0.0)**: users can request subtitles from the item UI; the queue is now a NAMED-TIER priority queue. Tiers: `PriorityTier` enum `Critical(0) > High(1) > Medium(2) > Low(3) > Background(4)` — **lower int = higher priority; NEVER renumber (persisted to queue.json)**. The former single `ConcurrentQueue` in `SubtitleQueueService` is replaced by a generic multi-lane engine `PriorityLanes<T>` (`Controller/PriorityLanes.cs`, one FIFO `LinkedList` lane per tier under a `SortedDictionary`, `Dictionary` key-index for O(1) dedup/promote; strongest tier drained first, FIFO within a tier). **De-dup identity changed from `(item,lang,force)` to `(item,lang)`** (`SubtitleQueueService.IdentityKey`, force DROPPED): a re-request merges via `MergeWork` — `Force` OR'd, tier promoted to `Stronger()` (min) — fixing a latent double-run (same item queued forced AND non-forced). `_inFlight` now tracks IN-PROCESSING keys (reserved at dequeue, released at completion); queued-dedup is the lane index. `QueueEntry.Tier` is `int?` (nullable) so a legacy queue.json (no tier) restores via `PriorityScheduling.NormalizeRestoredTier(null)` → **High, NOT Critical(0)** (the migration gotcha — a non-nullable int default would silently outrank all admin work). Requester→tier mapping is config (`AdminRequestTier`=High, `UserRequestTier`=Medium, `BackgroundSweepTier`=Background); tier is ALWAYS assigned server-side via `PriorityScheduling.ResolveTier(RequesterKind,…)`, never from the client. The background sweep stays LAZY (not materialized into queue.json — avoids O(N²) persist + preserves the #110 skip-cache); it drains the priority lanes between items (`SubtitleGenerationTask` `DrainPriorityAsync`), so it is effectively lowest priority. **User requests** are a SEPARATE controller (`Api/SubtitleRequestController.cs`, bare `[Authorize]`) so the admin `SubtitleController` keeps its class-level `RequiresElevation` (Jellyfin has no FallbackPolicy → removing it would make un-attributed methods public — the fail-open footgun). Safe-by-default: `AllowUserRequests` master toggle **OFF** (existing installs unchanged); when on, `AutoApproveUserRequests` **OFF** → requests land Pending and spend zero CPU until an admin approves (config-page "User Requests" panel → `Requests`/`Requests/{id}/Approve|Decline`). Identity via `IAuthorizationContext.GetAuthorizationInfo(Request)` (API-key calls rejected — no user); item gated by `item.IsVisibleStandalone(user)` → 404 (never 403, no enumeration); language allow-listed (`RequestValidation.NormalizeLanguage` → "auto" or 2-letter only, blocks path-traversal into the `.<lang>.generated.srt` filename); non-forced (never regenerate over a good sub). Quota/caps/dedup are enforced at ONE chokepoint — `SubtitleRequestStore.TryCreate` (order: dedup→globalCap→activeCap→rolling quota), counting the season/show fan-out (`MediaItemResolver.ResolveLeafItems`, shared with admin GenerateAll). Config: `UserRequestDailyQuota`(5)/`UserRequestQuotaWindowHours`(24)/`UserRequestActiveCap`(3)/`UserRequestMaxItemsPerRequest`(200)/`UserRequestGlobalCap`(500), 0=unlimited. Requests persist to `requests.json` (atomic temp+rename, lazy restore on first access, terminal-state prune after 30d). Client (`Web/whisperSubs.js`): served ANONYMOUSLY → trusted for nothing; admins see "Generate Subtitles", non-admins see "Request Subtitles" only when `Requests/Capabilities.enabled`; all server strings rendered via `textContent` (XSS). Pure decision logic (`PriorityLanes`, `PriorityScheduling`, `RequestPolicy`, `RequestValidation`, `SubtitleRequestStore.TryCreate` in-memory) is unit-tested (coverage gate); controllers/service are `[ExcludeFromCodeCoverage]`. Tier enum fields serialize by NAME over the config REST API (`[JsonConverter(JsonStringEnumConverter)]`; config page uses string option values, `tierName()` tolerates a numeric fallback). Designed via an 8-agent research panel; minor bump (additive, default-off). (Issue #112.)
- **Distributed transcription — worker pool (v4.0)**: transcription can be pooled across many machines/GPUs/NAS/cloud endpoints and run in PARALLEL, while a normal single-server install is unchanged. The concurrency gate is a shared `WorkerPool` (`Controller/Workers/WorkerPool.cs`) — the immutable `ITranscriptionWorker` descriptors + their live in-flight counts behind one lock, gated by a `SemaphoreSlim(TotalCapacity)` where `TotalCapacity = ΣMaxConcurrency`. It **replaces the old global `TranscriptionLock(1,1)`**: with the default one local worker of `MaxConcurrency` 1 it admits exactly one job at a time (byte-identical to the old lock); with N workers the dispatcher (`SubtitleQueueService.DispatchDrainAsync`) runs up to ΣMaxConcurrency jobs at once, acquiring a slot BEFORE dequeuing so an item leaves the persisted queue only when a worker is ready (same crash-durability). **Composition is decided by the pure `WorkerPlan.Decide`** (unit-tested, `WorkerPlanTests`) for exact back-compat: no new config → one **local** worker (identical to pre-v4); a legacy single `RemoteWhisperApiUrl` → one **remote** worker, remote-ONLY (the host's local whisper is NOT silently activated — that would surprise an upgrading remote-offload user); an explicit `Workers` list → those **+ optionally** the local host when `EnableLocalWorker` (default true). `WorkerRegistry.BuildWorkers` news up the providers from that plan (a `RemoteWhisperProvider` per remote row, `SubtitleProviderFactory.CreateLocal` for the host). **Config** (`PluginConfiguration`): `Workers` (`List<WhisperWorker>`, EMPTY by default) + `EnableLocalWorker` (default true) — a `WhisperWorker` row is `{Id, Name, Enabled, ApiUrl, ApiKey, Model, MaxConcurrency(1), CostWeight(0), CanTranslate(true)}`, validated by the pure `WorkerConfigValidation.Validate` (absolute http(s) URL, concurrency ≥ 1, cost ≥ 0). **Routing** is the pure cost-weighted `WorkerScheduling` (mirrors `PriorityScheduling`): `CanServe` is the hard filter (healthy, free slot, translate-capability, model), `Score` prefers `CostWeight` (0 = free local, strictly beats any paid worker) then least-busy then a stable tiebreak — so "prefer local, burst to a paid/cloud worker only when every local one is saturated" falls out with no backend names in the logic. **`HasCapableWorker(requirements)` fail-fast**: `WorkerJob.Requirements(mode, enableTranslation)` conservatively marks a job translate-required whenever translation is possible for the config, and if no worker can EVER serve that (e.g. translation on, every worker transcribe-only) the whole queue fails fast with a clear per-item error instead of blocking a slot forever. **Remote-call resilience**: each `RemoteWhisperProvider` call is bounded by a per-call deadline derived from the audio length via `TranscriptionTimeout.Compute(audioBytes, JobTimeoutRealtimeFactor(6.0), JobMinTimeoutSeconds(60), JobMaxTimeoutHours(12))` — the `HttpClient` itself has `Timeout.InfiniteTimeSpan`, so a slow-but-healthy transcription is never guillotined while a hung/unreachable endpoint fails fast (the old fixed 30-minute timeout was the root of the multi-day stuck-task bug). The WAV is **streamed** (not `ReadAllBytes`) so N parallel workers don't each buffer a ~260 MB film into RAM (OOM path). **Endpoint** `POST Workers/TestConnection` POSTs a tiny in-memory silent WAV (`SyntheticAudio.SilentWav16kMono`) to the worker's `/v1/audio/transcriptions` so the admin can confirm reachability + auth + a working transcribe path before saving; it never throws (always `{ok, status, latencyMs, message}`), is SSRF-hardened (blocks link-local 169.254.0.0/16 + `fe80::/10`, no auto-redirect, never echoes the upstream body). **`GET Queue`** gained `pending[]` (inbound items in the exact order they'll run — name/tier/language, capped) and `workers[]` (outbound: each worker's id/name/isLocal/inFlight/maxConcurrency/costWeight + the item(s) it is transcribing right now, from `WorkerPool.Snapshot`). **Worker image**: `worker/` is a separate deliverable — an example OpenAI-compatible whisper.cpp `whisper-server` (Vulkan) behind a thin Python adapter (`worker/README.md`). The pure decision helpers (`WorkerPlan`, `WorkerScheduling`, `WorkerJob`, `WorkerConfigValidation`, `SyntheticAudio`) are unit-tested (coverage gate); `WorkerPool`/`WorkerRegistry`/the dispatcher are `[ExcludeFromCodeCoverage]` orchestration. **Known follow-up**: the AUTOMATIC scheduled sweep is still one-at-a-time — `SubtitleGenerationTask.RunGenerationAsync` acquires a single slot and awaits each swept item inline, so the background *backlog* does not yet parallelize across the pool. Manual **Generate / Generate All** DO parallelize today (they enqueue into the lanes and the dispatcher fans them out to ΣMaxConcurrency), as does the task's *priority-drain* phase (`DrainPriorityAsync`); only the task's own library sweep remains sequential. (v4.0.)
- **Skip already-subtitled media (issue #82)**: as of this version, `SubtitleGenerationTask` + `GenerateTranslatedSubtitleAsync` use `SubtitleInventory.HasUsableSubtitle` (over `SubtitleStreamReader.GetSubtitleStreams`, which reads embedded+external streams via `item.GetMediaStreams()`) to skip media already subtitled in the needed language. `SkipIfSubtitleExists`/`IgnoreForcedSubtitles` (default on) gate it — forced/image-only tracks don't satisfy the need when `IgnoreForcedSubtitles` is on; for the translation pass an existing English subtitle track (embedded or external) counts as already-translated. The plugin's own `.generated.`/`.translated.` outputs are excluded so they don't self-satisfy.
- **Generation toggle + translation section + image-subs toggle (issue #83)**: whisper can only produce a subtitle in the audio's own language (transcribe) or English (translate — English is the only translate target). The UI mirrors exactly that: a single **Generate original-language subtitles** checkbox (`GenerateOriginalLanguageSubtitles`, default true — primary generate switch, covers English audio by transcription) and, in a separate **Translation** section, the pre-existing `EnableTranslation` (default false) reworded as "Also create an English subtitle when a title has none." Which passes run is decided by the pure helper `SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage, enableTranslation, force)` → `GenerationPlan` record (unit-tested in `GenerationPlanTests`): original-language runs in Full/FullAndForced when wanted or `force`d; forced runs by mode only; translation runs in TranslationOnly always or Full/FullAndForced when `EnableTranslation`, never in ForcedOnly, and `force` never turns translation on. `GenerateTranslatedSubtitleAsync` already skips when English audio or an existing English subtitle is present, so translation only ever *fills the gap*. (Design history: a free-text `DesiredSubtitleLanguages` allow-list, then a second `GenerateEnglishSubtitles` checkbox, were both removed before release — never offer languages whisper can't produce, and don't split "English" across two controls.) `CountImageSubtitlesAsPresent` (default false) flips `requireText` on the skip predicates. The text-vs-image extension classification has ONE source of truth: `SubtitleInventory.UsableSubtitleExtensions(requireText)` (text = srt/ass/ssa/vtt; image = .sub/.sup added when `!requireText`), consumed by both `SubtitleGenerationTask`'s sidecar scan and `GenerateTranslatedSubtitleAsync`'s "auto" fallback; the stream path uses `IsUsableStream(..., requireText)`. When on, image subs (PGS/VOBSUB) count as already-present.

## Operational Gotchas

- **NEVER restart Jellyfin without asking the user first.** Interrupts active playback and kills the transcription queue.
- **Unraid tmpfs**: Do NOT store whisper binaries/models in `/opt` on Unraid — it's a RAM disk. Use `/mnt/user/appdata/whisper`.
- **Static linking is mandatory**: Always build with `-DBUILD_SHARED_LIBS=OFF`.
- **Orphaned docker-proxy**: If Jellyfin crashes, docker-proxy may hold port 8096. On Unraid: `rc.docker restart`.
- **Memory limits**: Large models consume 5-10 GB RAM. Set `mem_limit` in docker-compose.
- **Plugin directory moves on version change**: Always check actual path with `docker exec jellyfin find /config/plugins -name "WhisperSubs*" -type d`.
- **Jellyfin SDK pinning**: ALWAYS pin `Jellyfin.Controller` and `Jellyfin.Model` to MINIMUM supported minor version (e.g., `10.11.0`). NEVER use wildcards like `10.11.*`.

## Language Detection

- FFprobe extracts `language` tags from audio streams. Normalization: 30+ ISO 639-2 -> 639-1 mappings in `SubtitleManager.NormalizeLanguageCode()`.
- Dedup: multiple streams with same language produce only one SRT.
- Fallback: files with no language tags get whisper auto-detection — one SRT with language `auto`.
- Multi-audio selection (`AudioLanguageSelection`, default `All`): with `DefaultLanguage=auto` the plugin transcribes EVERY detected audio language (one `.<lang>.generated.srt` each). `PrimaryOnly` restricts the per-track full/forced passes to the primary track only, via the pure `SubtitleManager.SelectAudioLanguages(detected, selection)` applied to the `foreach (var lang in passLanguages)` in `GenerateSubtitleAsync`. It only narrows the auto multi-language case (a specific code or the no-tags `auto` fallback is single-element and untouched); the translation pass deliberately still sees the full `languages` list. Serialized by name (`JsonStringEnumConverter`).

## Config Page (Web UI)

`Web/configPage.html` is an embedded resource. Changes require rebuilding the DLL.

- Uses Jellyfin `emby-*` custom elements with `data-require="emby-input,emby-button,emby-select,emby-checkbox"`
- Dynamic dropdowns must use `is="emby-select"` and populate only after `pageshow` event. Do not call `loadLibraries()` twice.
- Debug via browser console: look for `WhisperSubs:` prefixed log lines.

## Key Rules
- Privacy by default: transcription runs on this server and audio is never sent to a third party UNLESS the admin explicitly configures a remote/cloud worker (v4.0 worker pool). Keep new features additive, opt-in, and default-off so an unconfigured install never sends audio off-box.
- Supports multiple GPU backends (CUDA, Vulkan, ROCm) — never default to CPU-only
- Docker images use semver tags, never `:latest`
- License is GPL-3.0
- Listed on awesome-jellyfin

*Generated by [LynxPrompt](https://lynxprompt.com) CLI*
