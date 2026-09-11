<p align="center">
  <img src="docs/images/banner.svg" alt="WhisperSubs Banner" width="900"/>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/whisper-subs/releases"><img src="https://img.shields.io/github/v/release/GeiserX/whisper-subs?style=flat-square&logo=github&color=6B4C9A" alt="Release"></a>
  <a href="https://github.com/GeiserX/whisper-subs/actions/workflows/build-release.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/whisper-subs/build-release.yml?branch=main&style=flat-square&label=tests" alt="Tests"></a>
  <a href="https://github.com/GeiserX/whisper-subs/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 9.0">
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-6B4C9A?style=flat-square" alt="Jellyfin 10.11+">
  <a href="https://github.com/awesome-jellyfin/awesome-jellyfin#readme"><img src="https://img.shields.io/badge/listed%20on-awesome--jellyfin-00a4dc?style=flat-square&logo=jellyfin&logoColor=white" alt="listed on awesome-jellyfin"></a>
  <a href="https://codecov.io/gh/GeiserX/whisper-subs"><img src="https://codecov.io/gh/GeiserX/whisper-subs/graph/badge.svg" alt="codecov"></a>
</p>

---

**WhisperSubs** is a Jellyfin plugin that generates subtitles for your media library with local AI models. Transcription runs on hardware you control: this Jellyfin server by default, plus any transcription workers you choose to add. Your media never reaches a third-party cloud unless you deliberately configure one.

## Documentation

Everything past this page lives at **[geiserx.github.io/whisper-subs](https://geiserx.github.io/whisper-subs/)**.

| Page | What it covers |
|---|---|
| [Setup guide](https://geiserx.github.io/whisper-subs/docs/setup/) | Installing the engine and a model, container library requirements, GPU passthrough, vocal separation |
| [Choosing a binary variant](https://geiserx.github.io/whisper-subs/choosing-a-variant/) | The seven prebuilt variants, which one your CPU and GPU can actually run, exit code 132 |
| [Platform-specific installs](https://geiserx.github.io/whisper-subs/install-topologies/) | TrueNAS SCALE, Proxmox LXC, Synology DSM, unRAID, plain Docker Compose |
| [Diagnostics](https://geiserx.github.io/whisper-subs/diagnostics/) | Reading the status and queue endpoints, API keys, what a useful bug report contains |
| [Remote workers](https://geiserx.github.io/whisper-subs/remote-workers/) | The worker pool, the worker container, Groq / OpenAI / OpenRouter as a backend |
| [Limitations](https://geiserx.github.io/whisper-subs/limitations/) | What it cannot do, and why |

## Features

- **Self-hosted processing.** Audio is transcribed by [whisper.cpp](https://github.com/ggerganov/whisper.cpp) on this server by default, or across a pool of your own workers.
- **Built-in engine setup.** The settings page downloads the `whisper-cli` binary and a model. Binary downloads are **Linux only**: on macOS and Windows the variant dropdown is empty by design, and you install `whisper-cli` yourself and set **Whisper Binary Path**. Model downloads work on every platform.
- **Automatic language detection.** Reads each audio stream's language tag, falling back to whisper's own detection when tags are absent. A multi-language file gets one subtitle per audio language.
- **Forced subtitles.** Transcribe only foreign-language dialogue, via VAD speech segmentation and per-chunk language detection.
- **English translation.** Optionally add an English subtitle to a title that has none. English is the only language whisper can translate into.
- **Lyrics (experimental).** `.lrc` files for music libraries, picked up by Jellyfin automatically.
- **Vocal separation (optional).** Isolate vocals with [BSRoformer.cpp](https://github.com/chenmozhijin/BSRoformer.cpp) before transcription for noisy content. Falls back to the original audio when unavailable.
- **GPU acceleration.** CUDA (NVIDIA), Vulkan (Intel / AMD / NVIDIA) and ROCm (AMD).
- **Distributed transcription.** Pool extra machines, GPUs, a NAS or a hosted endpoint and transcribe in parallel. Off by default, and free local workers are always used before bursting to a paid one.
- **Priority queue with user requests.** Manual requests outrank the background sweep. Non-admin users can request subtitles from the item menu once you enable it.
- **Real-time progress.** A live banner shows the current item, the phase, per-file progress and queue depth.
- **Subtitle resume.** An interrupted transcription continues from its last timestamp instead of starting over.
- **Admin dashboard.** Browse libraries, see which items have subtitles, and generate them from the Jellyfin admin panel or from an in-page button on the item page.
- **Scheduled task with a skip cache.** Daily at 2:00 AM and on startup by default. A persistent skip cache means repeat runs do not re-probe unchanged items.
- **Configurable output filenames.** The label and filename template are yours to change.

## Prerequisites

| Dependency | Details |
|---|---|
| **Jellyfin** | 10.11.0 or later |
| **FFmpeg** | Bundled with Jellyfin (`/usr/lib/jellyfin-ffmpeg/ffmpeg`) or on `PATH`. Used to extract audio. |
| **whisper.cpp** | The `whisper-cli` binary. Downloadable from the settings page on Linux; installed manually on macOS and Windows. See the [setup guide](https://geiserx.github.io/whisper-subs/docs/setup/). |
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

Then open **Dashboard** > **Plugins** > **WhisperSubs** and follow the [setup guide](https://geiserx.github.io/whisper-subs/docs/setup/) to install the engine and a model.

### Manual installation

```bash
dotnet build --configuration Release
# copy WhisperSubs.dll to /var/lib/jellyfin/plugins/WhisperSubs/, then restart Jellyfin
```

## Output files

Subtitles are written next to the media and picked up by Jellyfin on the metadata refresh that follows. The default filename template is `{name}.{lang}.{label}{.type}` with the label `WhisperSubs`, so a Spanish film produces:

| Kind | File |
|---|---|
| Full subtitles | `Movie.es.WhisperSubs.srt` |
| Forced subtitles | `Movie.es.WhisperSubs.forced.srt` |
| English translation | `Movie.en.WhisperSubs.translated.srt` |
| Lyrics | `Song.lrc` |

The label is both the title shown in Jellyfin's subtitle picker and the marker the plugin uses to recognise its own files, so pick something distinctive if you change it. Files written by older versions with the `.generated.` anchor are still recognised. Both **Subtitle label** and **Filename template** are on the settings page.

## Subtitle modes

| Mode | What it generates |
|---|---|
| **Full** (default) | Complete transcription of all speech |
| **Forced only** | Foreign-language dialogue only. Slow: a 2-hour film needs roughly 240 language-detection calls before transcription starts. |
| **Full + forced** | Both, per audio track |
| **Translation only** | An English translated subtitle, skipping native-language transcription |

The scheduled task skips media that already has a usable subtitle in the needed language. Forced tracks do not satisfy that need while **Ignore forced subtitles when skipping** is on (the default), and image-based tracks do not unless you turn on **Count image-based subtitles as present**. They are two independent settings. A manual **Generate** on a single item always transcribes, bypassing the skip.

## User subtitle requests

Off by default. Turn on **Allow user requests** in the **User Requests** panel and non-admin users get a **Request Subtitles** entry on the item page. Requests land as Pending and cost no CPU until an admin approves them, unless you also turn on **Auto-approve**. Approved requests enter the queue below admin requests and above the background sweep. Per-user daily quota (5), active cap (3), per-request item cap (200) and a global cap (500) are all configurable, and `0` means unlimited.

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

**The two turbo models cannot translate.** They were fine-tuned without the translate task, so enabling translation with one of them writes the *source* language into an English-named `.translated.srt` file. The plugin logs a warning and runs the job anyway; nothing in the filename or the subtitle picker reveals it. Download **Large V3 (Q5)** or **Medium (Q5)** before turning translation on. [Limitations](https://geiserx.github.io/whisper-subs/limitations/) has the detail.

## Distributed transcription

By default everything runs on this Jellyfin server, one job at a time. Expand **Worker Pool (Optional / Advanced)** to add OpenAI-compatible endpoints: another box with a GPU, a NAS, or a hosted API. The plugin extracts audio locally and sends it over HTTP, always preferring workers with a cost weight of `0` and bursting to a paid one only when the free ones are saturated. [`worker/`](worker/README.md) is a ready-to-run whisper.cpp + Vulkan worker image. Setup, upload-size caps and provider quirks are covered in [Remote workers](https://geiserx.github.io/whisper-subs/remote-workers/).

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

Four more fields (`WhisperBinaryVariant`, `VocalSeparationBinaryVariant`, `VocalSeparationModelQuant`, `VadModelPath`) record what the plugin downloaded or resolved. They are not meant to be edited by hand.

## In-page Generate Subtitles button

The plugin injects a script tag into Jellyfin's `index.html` to add a **Generate Subtitles** entry to the item page. Direct on-disk injection is the default and needs a writable web root, which containers often do not have. If yours is read-only, install the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin from `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` and WhisperSubs registers a serve-time injection instead, with no permission changes. Both mechanisms coexist safely. The status panel at the top of the settings page shows which one is active, and `Setup/InjectionStatus` reports the same in JSON.

## REST API

36 endpoints live under `/Plugins/WhisperSubs/`. All of them require authentication. Everything except the four user-request endpoints (`Requests/Capabilities`, `Items/{id}/Request`, `Requests/Mine`, `Items/{id}/RequestStatus`) requires a Jellyfin admin. [Diagnostics](https://geiserx.github.io/whisper-subs/diagnostics/) shows how to call `Setup/Status`, `Queue` and `Setup/InjectionStatus`, which are the three worth capturing when something goes wrong.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for planned features and design details.

## Other Jellyfin Projects by GeiserX

- [smart-covers](https://github.com/GeiserX/smart-covers): cover extraction for books, audiobooks, comics, magazines and music libraries, with online fallback
- [quality-gate](https://github.com/GeiserX/quality-gate): restrict users to specific media versions based on configurable path-based policies
- [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder): automatic 720p HEVC/AV1 transcoding service with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync): sync Jellyfin access with Telegram channel membership

## Supporters

> This project is made possible by generous supporters:
> **yskaa001**

## License

This project is licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for the full text.
