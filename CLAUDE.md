# CLAUDE.md — WhisperSubs

## Overview
Jellyfin plugin for local AI-powered subtitle generation using whisper.cpp. All transcription runs entirely on the server with no external API calls. Supports GPU acceleration (CUDA, Vulkan, ROCm).

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
│   └── PluginConfiguration.cs     User-editable settings (model path, binary path, language, etc.)
├── Api/
│   └── SubtitleController.cs      REST API endpoints under /Plugins/WhisperSubs/*
├── Controller/
│   ├── SubtitleManager.cs         Orchestrator: language detection -> audio extraction -> transcription -> save
│   └── SubtitleQueueService.cs    Thread-safe in-memory queue with single-worker drain loop
├── Providers/
│   ├── ISubtitleProvider.cs       Provider interface (TranscribeAsync)
│   └── WhisperProvider.cs         whisper.cpp integration (finds binary, runs process, reads SRT output)
├── ScheduledTasks/
│   └── SubtitleGenerationTask.cs  Jellyfin scheduled task for auto-generation
└── Web/
    └── configPage.html            Admin UI (embedded resource) — vanilla JS, Jellyfin emby-* components
```

## Data Flows

### Full Subtitles

1. **Language detection** — `SubtitleManager.DetectAudioLanguagesAsync` calls FFprobe to read audio stream language tags. ISO 639-2/B codes are normalized to 639-1 (e.g., `spa` -> `es`).
2. **Audio extraction** — FFmpeg extracts 16kHz mono PCM WAV from the media file to a temp path.
3. **Transcription** — `WhisperProvider.TranscribeAsync` invokes `whisper-cli` as a child process with the model and audio file. Output is an SRT file. When `EnableVad` is on (default), it also passes `--vad --vad-model <path>` so whisper.cpp's native Silero VAD snaps cue starts to real speech onset at transcription time (the primary fix for gapless segments / issue #78). The VAD model lives in `whisper/vad/` and auto-downloads on first use; `WhisperSetupService.ResolveVadModelPath` resolves the configured-or-default path, and the flag is only added when the file exists.
4. **Timing corrections** — Before saving, `SubtitleManager.ApplyTimingCorrectionsAsync` applies (when the matching config toggle is on): `CompensateAudioOffset` shifts all timestamps by the audio stream's container `start_time`, then `WhisperProvider.AlignSrtToSpeech` snaps each subtitle's start forward to detected speech onset via FFmpeg `silencedetect`. The `AlignSrtToSpeech`/`silencedetect` pass is now the **fallback** for gapless segments — used when native VAD (`EnableVad`) is off; native VAD does this more reliably at transcription time. `CompensateAudioOffset` defaults on; local whisper-cli only (full + translated), not the remote API, not forced.
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

**SubtitleMode** enum: `Full` (0, default), `ForcedOnly` (1), `FullAndForced` (2).

## API Endpoints

All require Jellyfin admin auth (`Authorization: MediaBrowser Token="<token>"`).

| Method | Path | Returns | Notes |
|--------|------|---------|-------|
| `GET` | `/Plugins/WhisperSubs/Libraries` | `LibraryInfo[]` | All virtual folders |
| `GET` | `/Plugins/WhisperSubs/Libraries/{id}/Items?startIndex=0&limit=50` | `PagedItemResult` | Movies/Episodes with subtitle status |
| `POST` | `/Plugins/WhisperSubs/Items/{id}/Generate?language=auto` | 202 Accepted | Enqueues, returns immediately |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/Status?language=auto` | `SubtitleStatus` | Checks for `.generated.srt` on disk |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/AudioLanguages` | `string[]` | FFprobe-detected languages |
| `GET` | `/Plugins/WhisperSubs/Queue` | `{isProcessing, currentItem, remaining, processed}` | Live queue status |
| `GET` | `/Plugins/WhisperSubs/Models` | `ModelInfo[]` | `.bin` files in model directory |
| `POST` | `/Plugins/WhisperSubs/RunTask` | 200 | Triggers the scheduled task |

## Queue System

- **`Enqueue()`** — Fire-and-forget. The `POST /Items/{id}/Generate` endpoint returns HTTP 202 immediately.
- **`EnsureDraining()`** — Starts a single background worker if one isn't already running. Uses `Interlocked.CompareExchange` for thread safety.
- **Race condition protection** — After the drain loop exits, it re-checks the queue and restarts if new items arrived during the `finally` block.
- **Skip existing** — The drain loop checks for `.generated.srt` files before processing, so re-queuing is safe.
- **Persisted to disk** — Queue is saved to `queue.json` (`/config/data/WhisperSubs/queue.json`) on every enqueue/dequeue. On startup, `RestoreQueue()` reloads pending items.
- **Global `TranscriptionLock`** (`SemaphoreSlim(1,1)`) prevents concurrent whisper processes.
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

## Known Issues

- **Hallucination on non-speech audio**: During music/credits/silence, large-v3 generates nonsense. The `--suppress-non-speech` (`-sns`) flag helps but doesn't eliminate it.
- **Language detection false positives**: At p>=0.3, concert/music audio can be misidentified as foreign language. Consider raising threshold for non-dialogue content.
- **Hallucination signatures** (Spanish): "La Iglesia de Jesucristo de los Santos de los Ultimos Dias", "Suscribete al canal", "Subtitulos por".
- **whisper.cpp writes SRT only at completion** — not incrementally. Mid-process kills produce no partial file.
- **Subtitle timing**: whisper.cpp emits gapless segments, so a line can show during the pause before its speech. Primary fix (issue #78) is whisper-cli's native **Silero VAD**: `EnableVad` (default on) injects `--vad --vad-model <path>` so cue starts snap to real speech onset at transcription time. The model lives in `whisper/vad/` (`ggml-silero-v5.1.2.bin`, ~865 KB) and auto-downloads; path resolved by `WhisperSetupService.ResolveVadModelPath`. The older energy-based `AlignSubtitlesToSpeech` (`WhisperProvider.AlignSrtToSpeech` via FFmpeg `silencedetect`) is demoted to **fallback** — used only when VAD is off (the shipped silencedetect fix proved unreliable on real TV content). `CompensateAudioOffset` (default on) still shifts by audio `start_time`. See `WhisperProvider.AlignSrtToSpeech` + `SubtitleManager.ApplyTimingCorrectionsAsync`. Applies to full + translated, not forced/lyrics.

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

## Config Page (Web UI)

`Web/configPage.html` is an embedded resource. Changes require rebuilding the DLL.

- Uses Jellyfin `emby-*` custom elements with `data-require="emby-input,emby-button,emby-select,emby-checkbox"`
- Dynamic dropdowns must use `is="emby-select"` and populate only after `pageshow` event. Do not call `loadLibraries()` twice.
- Debug via browser console: look for `WhisperSubs:` prefixed log lines.

## Key Rules
- All processing must remain local; never send audio to external services
- Supports multiple GPU backends (CUDA, Vulkan, ROCm) — never default to CPU-only
- Docker images use semver tags, never `:latest`
- License is GPL-3.0
- Listed on awesome-jellyfin

*Generated by [LynxPrompt](https://lynxprompt.com) CLI*
