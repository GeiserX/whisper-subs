---
title: Remote workers and hosted providers
description: Distributing transcription across machines, and using Groq, OpenAI or OpenRouter as a backend.
sidebar_position: 6
---

# Remote workers and hosted providers

By default WhisperSubs transcribes on the Jellyfin server itself, one job at a time. The worker pool lets you spread that work across other machines, or hand it to a hosted API.

## What the worker pool is

WhisperSubs always extracts the audio locally with FFmpeg. A worker is any endpoint that implements the OpenAI audio API — `POST /v1/audio/transcriptions` and `POST /v1/audio/translations` — that the plugin can send that audio to over HTTP.

The pool is off by default. With no workers configured, the plugin behaves exactly as it always has: the host's own whisper, one job at a time. Adding workers is additive.

Two rules govern dispatch:

- **Free before paid.** Each worker has a **cost weight**. `0` means free (a local box, your own hardware) and is always preferred. A worker with a weight above `0` is only used to burst when every free worker is busy.
- **Concurrency is per worker.** whisper.cpp runs one transcription per GPU context, so a single-GPU worker should stay at **max concurrency 1**. To do more in parallel, add more workers.

You want the pool when you have a second machine with a real GPU, when the Jellyfin host is a NAS too weak to transcribe (turn **Also use this server as a worker** off and offload entirely), or when you want a backlog to clear in parallel across several boxes.

:::note
The scheduled task runs in two phases and both dispatch across the pool. It first drains queued requests, then sweeps the library. Each phase leases a worker slot per item and starts the next item without waiting for the previous one to finish, so up to the pool's total concurrency runs at once. Manual **Generate** and **Generate All** feed the same dispatcher. Extra workers speed up all of it, including an overnight sweep.
:::

## Running the worker container

The repo ships a ready-to-run worker image: whisper.cpp's `whisper-server` built with Vulkan (so it accelerates on Intel and AMD GPUs), behind a small Python adapter that speaks the exact HTTP dialect the plugin expects.

The adapter exists because `whisper-server` is not fully OpenAI-compatible on its own. It serves a single inference route, selects translation with a `translate=true` form field rather than a separate path, and defaults `language` to `en`, which would break auto-detection. The adapter maps `/v1/audio/translations` onto the single route by injecting that field into the streamed body, and starts the backend with `--language auto`.

### One-time GPU setup

The container runs as a non-root user, so it must join the host group that owns `/dev/dri/renderD128`:

```bash
getent group render | cut -d: -f3      # prints your host's render GID (often 993, 104 or 44)
```

Put that number under `group_add:` in `worker/docker-compose.example.yml`. It ships as `"993"`, which is a common default and is **not** guaranteed to match your host. Use the numeric GID, not the name `render` — the name resolves inside the image but its GID will not match the host's device owner, so the GPU open fails silently and whisper falls back to very slow CPU.

### Start it

```bash
docker compose -f worker/docker-compose.example.yml up -d
docker logs -f whisper-subs-worker      # first boot downloads the model
```

The compose file publishes `9010:8080` and pins a semver image tag:

```yaml
services:
  whisper-subs-worker:
    image: drumsergio/whisper-subs-worker:0.1.4
    ports:
      - "9010:8080"
    environment:
      WHISPER_MODEL: large-v3-turbo-q5_0
    volumes:
      - whisper-models:/models
```

The model is served per worker: `whisper-server` loads exactly one model, fixed at startup by `WHISPER_MODEL`. The plugin's per-worker **Model** field is sent but ignored by this backend, so choose the model here.

Settings worth knowing:

| Variable | Default | What it does |
|---|---|---|
| `WHISPER_MODEL` | `large-v3-turbo-q5_0` | GGML model, downloaded into `/models` on first run |
| `API_KEY` | *(empty)* | When set, requires `Authorization: Bearer <key>` on the POST routes |
| `WHISPER_MAX_LEN` | *(empty = unlimited)* | Max characters per cue (`-ml`), paired with `-sow`. The worker-side twin of the plugin's **Maximum subtitle line length** — a worker segments its own output, so set it in both places |
| `WHISPER_THREADS` | *(whisper default)* | CPU threads; set to your core count on CPU-only workers |
| `WHISPER_VAD` | `true` | Native Silero VAD, so cue starts snap to real speech onset |

:::warning
With no `API_KEY` the worker is an unauthenticated transcription endpoint on `0.0.0.0` — anyone who can reach the port can drive your GPU. Run it on a trusted network, or set `API_KEY` and put TLS in front of it. Do not port-forward it.
:::

### Confirm it is up

```bash
curl -fsS http://<worker-host>:9010/health
```

It returns `{"status":"ok"}` once the model is loaded. The endpoint is unauthenticated by design so Docker healthchecks work.

Other backends work too. [Speaches](https://github.com/speaches-ai/speaches) is a good choice for CPU-only boxes and NVIDIA GPUs; it is built on faster-whisper/CTranslate2, which has no Vulkan path, so it will not accelerate on an Intel iGPU — that case is exactly why the whisper.cpp + Vulkan image exists.

## Pointing the plugin at a worker

Open **Dashboard → Plugins → WhisperSubs** and expand **Worker Pool (Optional / Advanced)**.

1. Leave **Also use this server as a worker** on to keep transcribing on the Jellyfin host too, or turn it off to offload entirely.
2. Click **+ Add worker** for each endpoint.
3. Use the row's **Test** button before saving. It calls `POST /Workers/TestConnection` and runs the same format negotiation a real job would.

Each row has these fields:

| Field | Meaning |
|---|---|
| **Endpoint URL** | Base URL only, **no `/v1` suffix**. The plugin appends `/v1/audio/transcriptions` itself |
| **API key** | Optional bearer token for this worker |
| **Model** | Model name to request; empty means the worker's own default. Ignored by the bundled worker image |
| **Max concurrency** | Simultaneous jobs. Keep `1` per single GPU |
| **Cost weight** | `0` = free, preferred. Above `0` = paid, used only to burst |
| **Max upload size (MB)** | What this endpoint accepts. `0` (default) = unlimited |
| **Upload format** | `WAV` (default), `FLAC` (lossless, ~half) or `Opus 24k` (~a tenth) |
| **Can translate to English** | Untick for any provider with no `/v1/audio/translations` endpoint |

Three timeout settings bound a remote call, so a slow-but-working pass is never cut off and a dead endpoint still fails: **Job timeout — real-time factor** (default `6`, multiplied by the audio length), **minimum seconds** (default `60`) and **maximum hours** (default `12`).

## Hosted providers

### Base URLs

Enter only the base. The plugin appends the path, so a URL that already ends in `/v1` becomes `/v1/v1/...` and returns `404`.

| Provider | Enter this |
|---|---|
| Groq | `https://api.groq.com/openai` |
| OpenRouter | `https://openrouter.ai/api` |
| OpenAI | `https://api.openai.com` |

### Model names

- **Groq**: `whisper-large-v3`.
- **OpenRouter**: a namespaced slug such as `openai/whisper-large-v3-turbo`. A bare `whisper-1` or a Groq-style name returns `400`.
- **OpenAI**: `whisper-1`.

### Which response formats carry timestamps

A subtitle needs timings, so this decides whether a provider can be used at all.

| Format | Carries timestamps | Used by WhisperSubs |
|---|---|---|
| `srt` | Yes | First choice. Kept for every server that accepts it |
| `verbose_json` | Yes, per segment | Negotiated automatically when `srt` is rejected |
| `json` | **No** — transcript text only | Never. It cannot be synchronized |
| `text` | **No** | Never |

WhisperSubs asks for `srt` first. If the endpoint rejects it, the plugin retries once with `verbose_json`, validates the returned segments, converts them to SRT, and caches that choice for the endpoint so the retry does not repeat on every job. If a provider ignores the requested format and answers with untimed JSON, the plugin retries with the other format rather than accepting it.

The plugin deliberately does **not** send `timestamp_granularities[]`. The field is optional, and `segment` is already the default for `verbose_json`. Segment is also the only granularity WhisperSubs reads. Support for it varies by model and provider, and some combinations answer `400`: OpenRouter does so unless your chosen model routes to an OpenAI-compatible backend. Omitting the field works everywhere.

Provider-specific limits:

- **OpenAI**: on `/v1/audio/transcriptions`, SRT and timestamps exist on `whisper-1` only. `gpt-4o-transcribe` and `gpt-4o-mini-transcribe` accept `json` and `text` only, so they carry no timings and cannot be used for subtitles; asking either for `verbose_json` fails with `response_format 'verbose_json' is not compatible with model 'gpt-4o-transcribe'`. The separate `gpt-4o-transcribe-diarize` model answers with `diarized_json`, which WhisperSubs neither requests nor parses.
- **OpenRouter**: has **no `/v1/audio/translations` endpoint**, so untick **Can translate to English** for it. Timestamps are only available on its OpenAI-compatible models; models that return text without timings (Chirp, Deepgram, Nova) cannot produce synchronized subtitles at all.

### Upload size, in minutes of audio

WhisperSubs extracts 16 kHz mono s16le PCM WAV. That is 32,000 bytes per second of audio, or **1.92 MB per minute**. The arithmetic decides everything about hosted providers:

| Audio length | WAV (default) | FLAC | Opus 24k |
|---|---|---|---|
| 13 minutes | ~25 MB | ~13 MB | ~2 MB |
| 40 minutes | 76.8 MB | ~39 MB | ~7 MB |
| 2 hours | 219.7 MB | ~116 MB | ~20 MB |

A 25 MB cap therefore accepts only about **13 minutes** of the default WAV upload.

| Provider | Direct upload cap |
|---|---|
| OpenAI | 25 MB |
| Groq | 25 MB on **both** tiers |

Groq's published 100 MB figure applies only to their `url` parameter, which WhisperSubs does not use. Setting 100 MB here would let a 77 MB upload through the plugin's own check and fail upstream anyway.

Two per-worker settings deal with this:

**Max upload size (MB)** tells the plugin what the endpoint accepts. `0` (the default) means unlimited, which is correct for every self-hosted worker. There is deliberately no non-zero default, because real caps differ by orders of magnitude. When set, an oversized title fails immediately — nothing is uploaded, so no bandwidth or provider quota is wasted — with a message naming the audio size, the cap and roughly how many minutes fit.

**Upload format** re-encodes the audio for the wire only. The extracted WAV that the local whisper path uses is untouched.

- `FLAC` is lossless and about half the size. It is encoded with `-sample_fmt s16`; without that flag ffmpeg defaults to 24-bit and produces a file *larger* than the 16-bit PCM source.
- `Opus 24k` is about a tenth. A 40-minute title lands near 7 MB and a full 2-hour film near 20 MB, comfortably inside a 25 MB cap.

For a 40-minute title on Groq, FLAC alone is not enough — ~39 MB still exceeds 25 MB. Use Opus.

:::warning
Keep **WAV** for self-hosted workers. whisper.cpp's `whisper-server` decodes WAV only, and the bundled worker image ships without ffmpeg. Choose a compressed format only on a hosted endpoint documented to accept it. Groq accepts FLAC and OGG; OpenAI's documented list is mp3, mp4, mpeg, mpga, m4a, wav and webm.
:::

When a compressed upload is prepared, the log shows a line like `Prepared opus upload: 6.7 MB (from 76.8 MB)` before the job. If the re-encode cannot run — no ffmpeg, the encode failed, or the result was not actually smaller — the plugin falls back to the original WAV, says why, and labels the upload as a WAV so the provider is not handed a mislabelled body.

## Troubleshooting

Every upstream failure now carries the provider's own explanation (sanitized, with API keys and signed URLs redacted) plus one line of advice. The advice comes first, because the queue's error banner truncates long messages.

### HTTP 413 — request entity too large

The upload exceeded what the endpoint accepts. In order of preference:

1. Set **Max upload size (MB)** on this worker (`25` for OpenAI and Groq) so the next oversized title fails instantly instead of after the upload.
2. Set **Upload format** to `Opus 24k`. That is roughly a tenth of the WAV size and fits a 2-hour film under a 25 MB cap.
3. Use a self-hosted worker, which has no limit.

If you already have Opus selected and still hit the cap, the title is simply too long for that endpoint.

A **502 or a dropped connection on a long title** is usually the same problem: an oversized body gets discarded at the provider's edge before it can return a tidy 413. Check the audio length against the table above before assuming the provider is down.

### The provider returns text without timestamps

The error reads:

```text
Remote Whisper API returned JSON without timestamped segments.
Use a model/provider that supports response_format=verbose_json;
plain json text cannot be synchronized.
```

No client-side change fixes this. The model returned a transcript with no timings, and there is nothing to synchronize to. Switch that worker to a model that supports `verbose_json` with segments — `whisper-1` on OpenAI, `whisper-large-v3` on Groq, an `openai/...` slug on OpenRouter — or point the worker at self-hosted whisper.cpp instead.

### Other status codes

| Status | What it means and what to change |
|---|---|
| `301`/`302`/`307`/`308` | The endpoint redirected instead of answering. WhisperSubs never follows a redirect with your audio. Usually an SSO gateway or an http→https rewrite; point the worker at the API's final address |
| `400` | On OpenRouter, almost always the model name. Use a namespaced slug, not a bare `whisper-1` |
| `401` | Key rejected. Check it is pasted in full, belongs to this provider and has not expired |
| `403` | Key accepted, account not allowed. Check model access and billing on the provider's dashboard |
| `404` | No transcription endpoint at that address. The base URL must **not** end in `/v1` |
| `405`/`501` | The address exists but does not accept audio uploads — probably a web page or a chat-only API |
| `429` | Rate limited. Lower this worker's max concurrency, or raise its cost weight so a local worker is preferred |
| `5xx` | The endpoint failed on its own side. Retry later or check the provider's status page |

If the endpoint sits behind a proxy that answers failures with an HTML page, the plugin reports "the endpoint returned a web page, not an API response" rather than echoing the markup.

### Worker output timing looks different from local output

Worker-generated subtitles can use the same speech-onset correction as local `whisper-cli` output, but it is opt-in for provider-owned timestamps. Enable **Align subtitles to speech** and **Also align VAD / worker timestamps to speech**.
