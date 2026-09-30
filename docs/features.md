---
description: Every feature, the prerequisites, output files, subtitle modes, models, config-file settings and the REST API.
---

# Features in detail

![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white) [![listed on awesome-jellyfin](https://img.shields.io/badge/listed%20on-awesome--jellyfin-00a4dc?style=flat-square&logo=jellyfin&logoColor=white)](https://github.com/awesome-jellyfin/awesome-jellyfin#readme)

## Features

- **Self-hosted processing.** Audio is transcribed by [whisper.cpp](https://github.com/ggerganov/whisper.cpp) on this server by default, or across a pool of your own workers.
- **Built-in engine setup.** The settings page downloads the `whisper-cli` binary and a model. Binary downloads are **Linux only**: on macOS and Windows the variant dropdown is empty by design, and you install `whisper-cli` yourself and set **Whisper Binary Path**. Model downloads work on every platform.
- **Automatic language detection.** Reads each audio stream's language tag, falling back to whisper's own detection when tags are absent. A multi-language file gets one subtitle per audio language.
- **Forced subtitles.** Transcribe only foreign-language dialogue, via VAD speech segmentation and per-chunk language detection.
- **English translation.** Optionally add an English subtitle to a title that has none. English is the only language whisper can translate into.
- **More target languages (experimental).** English audio, including an English dub on a foreign film, can also get subtitles in 24 European languages from [NVIDIA Canary](https://huggingface.co/nvidia/canary-1b-v2), run by [CrispASR](https://github.com/CrispStrobe/CrispASR) on this server or on a CrispASR worker. Audio in other languages still translates to English only. See [Settings](configuration.md#more-target-languages-experimental).
- **Translate one title.** From the item page, translate a movie, episode, season or series into English or, for English audio, any of the 24 Canary languages, without turning on a library-wide list. See [Translate one title](configuration.md#translate-one-title).
- **Lyrics (experimental).** `.lrc` files for music libraries, picked up by Jellyfin automatically.
- **Vocal separation (optional).** Isolate vocals with [BSRoformer.cpp](https://github.com/chenmozhijin/BSRoformer.cpp) before transcription for noisy content. Falls back to the original audio when unavailable.
- **GPU acceleration.** CUDA (NVIDIA), Vulkan (Intel / AMD / NVIDIA) and ROCm (AMD).
- **Distributed transcription.** Pool extra machines, GPUs, a NAS or a hosted endpoint and transcribe in parallel. Off by default, and free local workers are always used before bursting to a paid one.
- **Priority queue with user requests.** Manual requests outrank the background sweep. Non-admin users can request subtitles from the item menu once you enable it.
- **Real-time progress.** A live banner shows the current item, the phase, per-file progress and queue depth.
- **Subtitle resume.** An interrupted transcription continues from its last timestamp instead of starting over.

- **Scheduled task with a skip cache.** Daily at 2:00 AM and on startup by default. A persistent skip cache means repeat runs do not re-probe unchanged items.
- **Configurable output filenames.** The label and filename template are yours to change.

## Prerequisites

| Dependency | Details |
|---|---|
| **Jellyfin** | 10.11.0 or later |
| **FFmpeg** | Bundled with Jellyfin (`/usr/lib/jellyfin-ffmpeg/ffmpeg`) or on `PATH`. Used to extract audio. |
| **whisper.cpp** | The `whisper-cli` binary. Downloadable from the settings page on Linux; installed manually on macOS and Windows. See the [setup guide](docs/setup.md). |
| **Whisper model** | A GGML model file, downloadable from the settings page on every platform, or manually from [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp). |
| **BSRoformer.cpp** *(optional)* | The `bs_roformer-cli` binary for vocal separation. Downloadable from the settings page on Linux, macOS and Windows. |

## Installation

### From the Jellyfin plugin repository (recommended)

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories**.
2. Add a repository with this URL:
   ```
   https://geiserx.github.io/whisper-subs/manifest.json
   ```
3. Go to **Catalog**, find **WhisperSubs**, and click **Install**.
4. Restart Jellyfin.

Then open **Dashboard** > **Plugins** > **WhisperSubs** and follow the [setup guide](docs/setup.md) to install the engine and a model.

### Manual installation

```bash
dotnet build --configuration Release
# copy WhisperSubs.dll to <Jellyfin program data>/plugins/WhisperSubs/, then restart Jellyfin
# Debian/Ubuntu package: /var/lib/jellyfin/plugins/WhisperSubs/
# jellyfin/jellyfin container: /config/plugins/WhisperSubs/
```

## Output files

Subtitles are written next to the media and picked up by Jellyfin on the metadata refresh that follows. The default filename template is `{name}.{lang}.{label}{.type}` with the label `WhisperSubs`, so a Spanish film produces:

| Kind | File |
|---|---|
| Full subtitles | `Movie.es.WhisperSubs.srt` |
| Forced subtitles | `Movie.es.WhisperSubs.forced.srt` |
| English translation | `Movie.en.WhisperSubs.translated.srt` |
| Canary translation, English audio only (experimental) | `Movie.nl.WhisperSubs.translated.srt` |
| Lyrics | `Song.lrc` |

The label is both the title shown in Jellyfin's subtitle picker and the marker the plugin uses to recognise its own files, so pick something distinctive if you change it. Files written by older versions with the `.generated.` anchor are still recognised. Both **Subtitle label** and **Filename template** are on the settings page.

## Subtitle modes

| Mode | What it generates |
|---|---|
| **Full** (default) | Complete transcription of all speech |
| **Forced only** | Foreign-language dialogue only. Slow: a 2-hour film needs roughly 240 language-detection calls before transcription starts. |
| **Full + forced** | Both, per audio track |
| **Translation only** | An English translated subtitle, skipping native-language transcription |

The scheduled task skips media that already has a usable subtitle. For the full subtitle any language counts: an external subtitle file or an embedded track in another language also makes it skip the item. The English translation and the extra translation targets are checked per language. Forced tracks do not satisfy that check while **Ignore forced subtitles when skipping** is on (the default), and image-based tracks do not unless you turn on **Count image-based subtitles as present**. They are two independent settings. A manual **Generate** on a single item bypasses that check. A complete WhisperSubs subtitle for the language is still left as it is, and a partial one is resumed rather than started over.

## User subtitle requests

Off by default. Turn on **Allow user requests** in the **User Requests** panel and non-admin users get a **Request Subtitles** entry on the item page. Requests land as Pending and cost no CPU until an admin approves them, unless you also turn on **Auto-approve**. Approved requests enter the queue at the **User request priority** tier. With the default tiers (admin High, user Medium, sweep Background) that is below admin requests and above the background sweep. Per-user daily quota (5), active cap (3), per-request item cap (200) and a global cap (500) are all configurable, and `0` means unlimited.

## Models

Nine models are offered on the settings page. The default is **Large V3 Turbo (Q5)**, 574 MB.

| Model | Size | Translates | Notes |
|---|---|---|---|
| `ggml-large-v3-turbo-q5_0.bin` | 574 MB | no | Default. Best quality/size ratio. |
| `ggml-large-v3-turbo.bin` | 1620 MB | no | Full-precision turbo. |
| `ggml-large-v3-q5_0.bin` | 1081 MB | yes | Best choice for non-English audio and translation. |
| `ggml-large-v3.bin` | 3095 MB | yes | Maximum accuracy, 3-4x slower than turbo, ~4 GB RAM. |
| `ggml-medium-q5_0.bin` | 539 MB | yes | Similar size to turbo-q5, slower and less accurate. |
| `ggml-medium.bin` | 1530 MB | yes | Full-precision medium. |
| `ggml-small.bin` | 488 MB | yes | Faster, lower accuracy. |
| `ggml-base.bin` | 148 MB | yes | Lightweight. |
| `ggml-tiny.bin` | 78 MB | yes | Testing and constrained environments only. |

**The two turbo models cannot translate.** They were fine-tuned without the translate task, so enabling translation with one of them writes the *source* language into an English-named `.translated.srt` file. The plugin logs a warning and runs the job anyway; nothing in the filename or the subtitle picker reveals it. Download **Large V3 (Q5)** or **Medium (Q5)** before turning translation on. [Limitations](limitations.md) has the detail.

## Distributed transcription

By default everything runs on this Jellyfin server, one job at a time. Expand **Worker Pool (Optional / Advanced)** to add OpenAI-compatible endpoints: another box with a GPU, a NAS, or a hosted API. The plugin extracts audio locally and sends it over HTTP, always preferring workers with a cost weight of `0` and bursting to a paid one only when the free ones are saturated. [`worker/`](https://github.com/GeiserX/whisper-subs/blob/main/worker/README.md) is a ready-to-run whisper.cpp + Vulkan worker image. Setup, upload-size caps and provider quirks are covered in [Remote workers](remote-workers.md).

## Settings

Every setting on **Dashboard** > **Plugins** > **WhisperSubs** carries its own inline help. Six tunables are deliberately not on that page and can only be changed in the plugin's XML configuration file, followed by a Jellyfin restart:

| Setting | Default | What it does |
|---|---|---|
| `JobTimeoutRealtimeFactor` | `6` | How much slower than real-time a remote call may run before it is presumed hung. Per-call deadline = audio length x this factor. |
| `JobMinTimeoutSeconds` | `60` | Floor for that deadline. |
| `JobMaxTimeoutHours` | `12` | Ceiling for that deadline. |
| `JobMaxRetries` | `3` | Auto-retries for a killed or failed job. `0` disables retrying. |
| `TaskMaxRuntimeHours` | `6` | Cap on one scheduled sweep. It stops cleanly between items and resumes next run. `0` is unlimited. |
| `LanguageDetectionSampleSeconds` | `30` | Audio sampled per chunk during forced-subtitle language detection. |

Three more fields (`WhisperBinaryVariant`, `VocalSeparationBinaryVariant`, `VocalSeparationModelQuant`) record what the plugin downloaded. They are not meant to be edited by hand. `VadModelPath` is also written by the plugin when it downloads a VAD model, and a path inside its managed `vad/` directory is ignored in favour of the **Silero VAD model** selection once that model is on disk. Until the selected model has downloaded, an existing managed path stays in use. You can set it by hand to a custom Silero VAD ggml file outside that directory, and if that file exists it wins over the selection.

## In-page Generate Subtitles button
The plugin injects a script tag into Jellyfin's `index.html` to add a **Generate Subtitles** entry to the item page. Direct on-disk injection is the default and needs a writable web root, which containers often do not have. If yours is read-only, install the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin from `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` and WhisperSubs registers a serve-time injection instead, with no permission changes. Both mechanisms coexist safely. The status panel at the top of the settings page shows which one is active, and `Setup/InjectionStatus` reports the same in JSON.

## REST API

44 endpoints live under `/Plugins/WhisperSubs/`. All of them require authentication. Everything except the five user endpoints (`Requests/Capabilities`, `TranslationTargets`, `Items/{id}/Request`, `Requests/Mine`, `Items/{id}/RequestStatus`) requires a Jellyfin admin. [Diagnostics](diagnostics.md) shows how to call `Setup/Status`, `Queue` and `Setup/InjectionStatus`, which are the three worth capturing when something goes wrong.
