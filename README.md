<p align="center">
  <img src="docs/images/banner.svg" alt="WhisperSubs" width="900"/>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/whisper-subs/releases"><img src="https://img.shields.io/github/v/release/GeiserX/whisper-subs?style=flat-square&logo=github&color=F2B33D" alt="Release"></a>
  <a href="https://github.com/GeiserX/whisper-subs/actions/workflows/build-release.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/whisper-subs/build-release.yml?branch=main&style=flat-square&label=tests" alt="Tests"></a>
  <a href="https://github.com/GeiserX/whisper-subs/blob/main/LICENSE"><img src="https://img.shields.io/github/license/GeiserX/whisper-subs?style=flat-square" alt="License"></a>
  <a href="https://github.com/GeiserX/whisper-subs/stargazers"><img src="https://img.shields.io/github/stars/GeiserX/whisper-subs?style=flat-square" alt="Stars"></a>
  <a href="https://codecov.io/gh/GeiserX/whisper-subs"><img src="https://img.shields.io/codecov/c/github/GeiserX/whisper-subs?style=flat-square" alt="Coverage"></a>
</p>

**WhisperSubs** is a Jellyfin plugin that writes subtitles for the titles in your library that have none, by transcribing their audio with local AI models. Download providers such as OpenSubtitles only serve what someone has uploaded, and Bazarr's Whisper path needs a second service running beside it; WhisperSubs runs inside Jellyfin and makes the subtitle from the audio, in the audio's own language, and can translate it into English or, from English audio, into 24 more languages. Transcription runs on hardware you control: this Jellyfin server by default, plus any workers you add, and your media never reaches a third-party cloud unless you deliberately configure one.

<p align="center"><img src="docs/images/screenshots/playback.jpg" alt="Tears of Steel playing in Jellyfin with the subtitle track WhisperSubs made from its audio" width="880"></p>

## Features

- A title with no subtitles gets one in the language of its audio, saved next to the file as `Movie.es.WhisperSubs.srt`, and the title is refreshed at once so the player offers the track right away.
- A film in a language you do not speak gets an English subtitle too; English audio can also get subtitles in 24 European languages (Canary, experimental).
- A film with a few lines of foreign dialogue gets a forced subtitle that covers only those lines.
- A title you ask for jumps ahead of the nightly sweep. Your users can ask too, and a request costs no CPU until you approve it.
- **Generate Subtitles** and **Translate into...** sit on every item page; a live banner on the settings page shows the current title, the phase and the queue.
- The engine and a model download from the settings page on Linux (macOS and Windows servers install `whisper-cli` themselves), and a GPU is used when there is one: CUDA, Vulkan or ROCm.
- One switch swaps the transcription model for Qwen3-ASR 1.7B (experimental), run through CrispASR; detection and English translation stay on Whisper.
- Transcription can spread across your own workers or a hosted endpoint, free workers first.
- An interrupted job resumes from its last timestamp, and the daily sweep skips what it already checked.
- Music libraries can get `.lrc` lyrics (experimental), and vocal separation can clean noisy audio before transcription (optional).

## Quick start

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories** and add `https://geiserx.github.io/whisper-subs/manifest.json`.
2. Install **WhisperSubs** from **Catalog** and restart Jellyfin.
3. Open **Dashboard** > **Plugins** > **My Plugins** > **WhisperSubs**, download the engine and a model, then press **Generate Subtitles** on any title.

A few minutes later the player offers a subtitle track named **WhisperSubs**, and `<title>.<lang>.WhisperSubs.srt` sits next to the media file. It runs on Jellyfin 10.11 and 12.1, with or without a GPU. On a macOS or Windows server there is no engine download: install `whisper-cli` yourself and set its path, as the [setup guide](https://geiserx.github.io/whisper-subs/docs/setup/) explains.

## Documentation

Everything past this page lives at **[geiserx.github.io/whisper-subs](https://geiserx.github.io/whisper-subs/)**.

- [Setup guide](https://geiserx.github.io/whisper-subs/docs/setup/): the engine and model downloads, container libraries, GPU passthrough, vocal separation
- [Choosing a binary variant](https://geiserx.github.io/whisper-subs/choosing-a-variant/): which of the seven builds your CPU and GPU can run, exit code 132
- [Platform-specific installs](https://geiserx.github.io/whisper-subs/install-topologies/): TrueNAS SCALE, Proxmox LXC, Synology DSM, unRAID, Docker Compose
- [Settings reference](https://geiserx.github.io/whisper-subs/configuration/): every setting, what it does, its default
- [Remote workers](https://geiserx.github.io/whisper-subs/remote-workers/): the worker pool, the worker container, Groq, OpenAI or OpenRouter as a backend
- [Diagnostics](https://geiserx.github.io/whisper-subs/diagnostics/): the status and queue endpoints, API keys, what a useful bug report contains
- [Limitations](https://geiserx.github.io/whisper-subs/limitations/): what it cannot do, and why
- [Features in detail](https://geiserx.github.io/whisper-subs/features/): output files, subtitle modes, user requests, models, the in-page button
- [API and internals](https://geiserx.github.io/whisper-subs/api-and-internals/): the REST endpoints and how a subtitle gets made

Planned work is in [ROADMAP.md](ROADMAP.md).

## Related projects

- [smart-covers](https://github.com/GeiserX/smart-covers): cover extraction for books, audiobooks, comics, magazines and music libraries, with online fallback
- [quality-gate](https://github.com/GeiserX/quality-gate): restrict users to specific media versions based on configurable path-based policies
- [quality-gate-encoder](https://github.com/GeiserX/quality-gate-encoder) (formerly jellyfin-encoder): automatic 720p HEVC, H.264 or AV1 copies with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync): sync Jellyfin access with Telegram channel membership

## License

[GPL-3.0-or-later](LICENSE). Supported by yskaa001.
