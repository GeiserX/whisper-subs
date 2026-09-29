<p align="center">
  <img src="docs/images/banner.svg" alt="WhisperSubs Banner" width="900"/>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/whisper-subs/releases"><img src="https://img.shields.io/github/v/release/GeiserX/whisper-subs?style=flat-square&logo=github&color=6B4C9A" alt="Release"></a>
  <a href="https://github.com/GeiserX/whisper-subs/actions/workflows/build-release.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/whisper-subs/build-release.yml?branch=main&style=flat-square&label=tests" alt="Tests"></a>
  <a href="https://github.com/GeiserX/whisper-subs/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-6B4C9A?style=flat-square" alt="Jellyfin 10.11+">
  <a href="https://codecov.io/gh/GeiserX/whisper-subs"><img src="https://codecov.io/gh/GeiserX/whisper-subs/graph/badge.svg" alt="codecov"></a>
</p>

**WhisperSubs** is a Jellyfin plugin that generates subtitles for your media library with local AI models. Transcription runs on hardware you control: this Jellyfin server by default, plus any transcription workers you choose to add. Your media never reaches a third-party cloud unless you deliberately configure one.

## Features

- Transcribes with [whisper.cpp](https://github.com/ggerganov/whisper.cpp) on the Jellyfin server, or across a pool of your own workers or a hosted endpoint.
- The settings page downloads the engine (Linux) and a model (every platform).
- Detects each audio stream's language; a multi-language file gets one subtitle per audio language.
- Full, forced-only and English-translation subtitles, plus experimental Canary translation of English audio into 24 European languages.
- GPU acceleration with CUDA, Vulkan and ROCm, and optional vocal separation with BSRoformer.cpp.
- Priority queue: manual and user requests outrank the daily background sweep, which keeps a skip cache.
- Live progress banner, resume from the last timestamp, and an admin dashboard with an in-page **Generate Subtitles** button.
- Experimental `.lrc` lyrics for music libraries.

## Quick start

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories** and add `https://geiserx.github.io/whisper-subs/manifest.json`.
2. Install **WhisperSubs** from **Catalog** and restart Jellyfin.
3. Open **Dashboard** > **Plugins** > **WhisperSubs** and follow the [setup guide](https://geiserx.github.io/whisper-subs/docs/setup/) to install the engine and a model.

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
| [Features in detail](https://geiserx.github.io/whisper-subs/features/) | Prerequisites, manual install, output files, subtitle modes, user requests, models, settings, the in-page button, the REST API |

Planned work is in [ROADMAP.md](ROADMAP.md).

## Other Jellyfin projects by GeiserX

- [smart-covers](https://github.com/GeiserX/smart-covers): cover extraction for books, audiobooks, comics, magazines and music libraries, with online fallback
- [quality-gate](https://github.com/GeiserX/quality-gate): restrict users to specific media versions based on configurable path-based policies
- [quality-gate-encoder](https://github.com/GeiserX/quality-gate-encoder) (formerly jellyfin-encoder): automatic 720p HEVC/AV1 transcoding service with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync): sync Jellyfin access with Telegram channel membership

## License

This project is licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for the full text.

This project is made possible by generous supporters: **yskaa001**.
