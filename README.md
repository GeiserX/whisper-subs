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

**WhisperSubs** is a Jellyfin plugin that automatically generates subtitles for your media library using local AI models. Transcription runs on hardware you control -- this Jellyfin server by default, plus any transcription workers you choose to add -- so your media stays on your own network and is never sent to a third-party cloud unless you deliberately configure one. Your media stays private.

## Features

- **Self-Hosted Processing** -- Audio is transcribed on hardware you control using [whisper.cpp](https://github.com/ggerganov/whisper.cpp) -- this server by default, or a pool of your own workers. No third-party cloud is involved unless you deliberately add a cloud worker.
- **Built-in Engine Setup** -- Download whisper-cli binaries and models directly from the plugin settings page on Linux. No manual installation needed for most users.
- **Automatic Language Detection** -- Reads audio stream metadata to detect the spoken language and generate matching subtitles. Falls back to whisper's built-in language detection when tags are absent.
- **Forced Subtitles** -- Detect and transcribe only foreign-language dialogue (e.g., French lines in an English movie) via VAD-based speech segmentation and per-chunk language detection.
- **Lyrics Generation (Experimental)** -- Generate `.lrc` lyrics files for music libraries via whisper transcription. Jellyfin picks up `.lrc` files automatically.
- **GPU Acceleration** -- Supports CUDA (NVIDIA), Vulkan (Intel / AMD / NVIDIA), and ROCm (AMD) for significantly faster transcription.
- **Distributed Transcription (Worker Pool)** -- Optionally pool several machines, GPUs, a NAS, or a cloud endpoint and transcribe your backlog in parallel across all of them. Additive and off by default: with no workers configured, everything runs on this server exactly as before, and the plugin always prefers your free local workers before bursting to a paid one. See [`worker/`](worker/README.md) for a ready-to-run worker image.
- **Priority Queue** -- Manual requests are queued with priority and processed before scheduled items. Queue persists across restarts.
- **Real-time Progress** -- Live progress banner in the admin UI showing current item, phase (extracting audio, transcribing), per-file progress, and overall stats.
- **Subtitle Resume** -- If transcription is interrupted, it resumes from the last timestamp rather than starting over.
- **Admin Dashboard UI** -- Browse libraries, view items, manage the whisper engine, and trigger subtitle generation directly from the Jellyfin admin panel.
- **Scheduled Tasks** -- Enable automatic scanning so new media gets subtitles without manual intervention. Runs daily at 2:00 AM and on startup by default. On large libraries, repeat runs are fast: a **skip cache** remembers which items already have subtitles and skips re-checking unchanged files, re-checking an item only when Jellyfin re-saves it or you change a skip setting (configurable, on by default; a "Clear skip cache" button forces a full re-check).
- **Per-Library Control** -- Choose which libraries are monitored for automatic subtitle generation.
- **Multiple Output Formats** -- Generates `.srt` subtitles, `.forced.generated.srt` forced subtitles, and `.lrc` lyrics, all placed alongside your media and auto-detected by Jellyfin.

## Prerequisites

| Dependency | Details |
|---|---|
| **Jellyfin** | 10.11.0 or later |
| **FFmpeg** | Bundled with Jellyfin (`/usr/lib/jellyfin-ffmpeg/ffmpeg`) or available in `PATH`. Used to extract audio from media files. |
| **whisper.cpp** | The `whisper-cli` binary. **On Linux, the plugin can download this automatically** from the settings page. Otherwise, install manually -- see [Installing whisper.cpp](#installing-whispercpp). |
| **Whisper Model** | A GGML model file. **The plugin can download models automatically** from the settings page, or download manually from [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp). |

> **Quick start (Linux):** After installing the plugin, go to **Dashboard** > **Plugins** > **WhisperSubs**. The **Whisper Engine** section lets you download both the binary and a model with one click each. The manual steps below are only needed for non-Linux platforms or custom setups.

## Installation

### From the Jellyfin Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories**.
2. Add a new repository with this URL:
   ```
   https://geiserx.github.io/whisper-subs/manifest.json
   ```
3. Go to **Catalog**, find **WhisperSubs**, and click **Install**.
4. Restart Jellyfin.

### Manual Installation

1. Build from source:
   ```bash
   dotnet build --configuration Release
   ```
2. Copy `WhisperSubs.dll` to your Jellyfin plugins directory:
   ```
   /var/lib/jellyfin/plugins/WhisperSubs/
   ```
3. Restart Jellyfin.

## Installing whisper.cpp

The plugin requires whisper.cpp for transcription. Choose the method that matches your setup.

### Option A: Pre-built Binary (Recommended for most users)

1. Download the latest release for your platform from [whisper.cpp releases](https://github.com/ggerganov/whisper.cpp/releases).
2. Extract and place the `whisper-cli` binary somewhere persistent (e.g., `/opt/whisper/`).
3. Download a model:
   ```bash
   mkdir -p /opt/whisper/models

   # Base model (~148 MB) -- fast, good for quick transcription
   wget -O /opt/whisper/models/ggml-base.bin \
     https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin

   # Large V3 Turbo (~1.6 GB) -- best accuracy with reasonable speed (recommended)
   wget -O /opt/whisper/models/ggml-large-v3-turbo.bin \
     https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin
   ```
4. In the plugin settings, set **Whisper Binary Path** to `/opt/whisper/whisper-cli` and **Whisper Model Path** to the model file.

### Option B: Build from Source (CPU only)

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
cmake -B build -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
# Binary will be at build/bin/whisper-cli
```

### Option C: Build from Source with GPU Acceleration

See [GPU Acceleration](#gpu-acceleration) below for detailed instructions.

### Docker / Container Setups

If Jellyfin runs in a Docker container, whisper.cpp must be accessible **inside** the container. The recommended approach is to bind-mount a host directory containing the binary and model:

```yaml
# docker-compose.yml
services:
  jellyfin:
    image: jellyfin/jellyfin
    volumes:
      - /opt/whisper:/opt/whisper:ro   # whisper-cli binary + models
      # ... your other volumes
```

Then configure the plugin with:
- **Whisper Binary Path**: `/opt/whisper/whisper-cli`
- **Whisper Model Path**: `/opt/whisper/models/ggml-large-v3-turbo.bin`

> **Note:** The binary must be compiled for the same architecture as the container (typically x86_64 Linux). Download the `linux-x64` release asset or build inside a matching environment.

#### In-Page "Generate Subtitles" Button

The plugin adds an in-page "Generate Subtitles" button/menu to the Jellyfin web UI. This appears on media detail pages and lets you trigger transcription directly from your browser. Internally, the plugin injects a script tag into Jellyfin's `index.html` to register this UI.

By default, the plugin attempts **direct on-disk injection** — writing the script tag directly to the index.html file. This requires write access to Jellyfin's web root, which is often read-only in containerized setups. If your web root is read-only (common in Docker), you have two options:

**Option A: Use File Transformation (Recommended for read-only roots)**

Install the third-party **File Transformation** plugin by IAmParadox27. This plugin allows whisper-subs to inject the script at serve-time, without modifying the index.html file on disk:

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories**.
2. Add this repository URL:
   ```
   https://www.iamparadox.dev/jellyfin/plugins/manifest.json
   ```
3. Go to **Catalog**, find **File Transformation**, and click **Install**.
4. Restart Jellyfin.
5. Open the **WhisperSubs** plugin settings page — the *In-page "Generate Subtitles" button & menu* status panel at the top shows the active mechanism (it should now include **File Transformation (serve-time)**).

No permission changes or `chown`/`chmod` are needed. Both direct injection and File Transformation coexist safely — if both are available, both mechanisms activate and work together without duplication.

**Option B: Make index.html writable (if you manage the web root)**

The status panel on the WhisperSubs settings page shows the exact `index.html` path it detected. Make that file writable by the Jellyfin service user, then click **Re-inject**:

```bash
# Linux package install (use the path shown in the status panel):
sudo chown root:jellyfin /usr/share/jellyfin/web/index.html
sudo chmod 664 /usr/share/jellyfin/web/index.html
```

On Docker the user/group differ (e.g. linuxserver.io images use your `PUID`/`PGID`), and a read-only web mount must be made writable in your compose file before the direct on-disk injection can work.

#### <a id="container-setup"></a>Container Library Requirements

The plugin's built-in binary downloader fetches pre-built whisper-cli binaries. These require runtime libraries that are **not included** in the default Jellyfin Docker image:

| Variant | Required packages | Install command |
|---------|-------------------|-----------------|
| **CPU** | `libgomp1` | `apt install libgomp1` |
| **CPU (Compatibility)** | none (self-contained) | — |
| **Vulkan** | `libgomp1`, `libvulkan1`, `mesa-vulkan-drivers` | `apt install libgomp1 libvulkan1 mesa-vulkan-drivers` |
| **CUDA 12** | `libgomp1` + NVIDIA Container Toolkit on host | See [CUDA section](#cuda-nvidia) |
| **ROCm** | `libgomp1` + ROCm runtime | See [ROCm docs](https://rocm.docs.amd.com/) |

> **Minimal containers (TrueNAS Scale, slim Docker):** The default `cpu` build links `libgomp1` (OpenMP) for best performance. If that library is missing, **the plugin automatically falls back to the `noavx` build**, which is compiled with OpenMP off and has no such dependency — so transcription still works out of the box, just without the small OpenMP speed-up.

> **Older / low-power CPUs (no AVX):** The `cpu`, `vulkan` and `cuda12` builds are compiled with AVX/AVX2 instructions and will crash with an *illegal instruction* error (exit 132) on CPUs that lack them — common on budget NAS boxes, Atom/Celeron chips and some VMs. The plugin **detects missing AVX support and automatically uses the `noavx` (Compatibility) build** instead, both when recommending a variant and as a download-time fallback. You can also pick **"CPU (Compatibility)"** manually on the setup page.

For the faster build, install `libgomp1` persistently via your container's entrypoint or Dockerfile:

```bash
apt-get update -qq && apt-get install -y -qq --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*
```

The plugin's setup page will detect missing libraries and warn you before downloading.

### Verifying the Installation

```bash
# If in PATH:
whisper-cli --help

# If using an absolute path:
/opt/whisper/whisper-cli --help

# Inside a Docker container:
docker exec jellyfin /opt/whisper/whisper-cli --help
```

## GPU Acceleration

whisper.cpp supports GPU offloading via **Vulkan** (Intel, AMD, and some NVIDIA GPUs), **CUDA** (NVIDIA), and **ROCm** (AMD). GPU acceleration dramatically reduces transcription time, especially with larger models.

> **Offloading to another machine?** You can run transcription on a separate host as an OpenAI-compatible worker and point the plugin at it over HTTP — see [`worker/`](worker/README.md) for a ready-to-run whisper.cpp + Vulkan worker image (plus notes for CPU/NVIDIA/AMD backends).

> **Docker users:** Passing the GPU device (e.g., `/dev/dri`) to a container is **not enough** -- the container also needs the matching userspace libraries installed. The auto-setup wizard detects both the device and the library and will fall back to CPU if the library is missing.
>
> | Backend | Device | Required library | Install command (Debian/Ubuntu) |
> |---------|--------|------------------|---------------------------------|
> | **CUDA** | `/dev/nvidia0` | `libcuda.so.1` | `nvidia-container-toolkit` (host) |
> | **Vulkan** | `/dev/dri` | `libvulkan.so.1` + ICD JSON | `apt install libvulkan1 mesa-vulkan-drivers` (also needs `/usr/share/vulkan/icd.d/*.json`) |
> | **ROCm** | `/dev/kfd` | `libamdhip64.so` | `apt install rocm-hip-runtime` |

### Vulkan (Intel / AMD)

Vulkan is the best option for Intel iGPUs (e.g., UHD 770) and AMD GPUs. It works through the Mesa Vulkan drivers.

#### Building whisper.cpp with Vulkan

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
cmake -B build \
  -DGGML_VULKAN=ON \
  -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
# Binary: build/bin/whisper-cli
```

> **Important:** The CMake flag is `-DGGML_VULKAN=ON` (not `-DWHISPER_VULKAN`). This is a common source of confusion.

#### Runtime Dependencies

The Vulkan binary requires these libraries at runtime:

| Package (Debian/Ubuntu) | Purpose |
|---|---|
| `libvulkan1` | Vulkan loader |
| `mesa-vulkan-drivers` | Intel (ANV) and AMD (RADV) Vulkan ICDs |
| `libgomp1` | OpenMP threading |

```bash
apt-get install -y libvulkan1 mesa-vulkan-drivers libgomp1
```

#### Docker: GPU Passthrough for Vulkan

To use an Intel or AMD GPU inside a Docker container:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin
    devices:
      - /dev/dri:/dev/dri    # GPU render nodes
    volumes:
      - /opt/whisper:/opt/whisper:ro
```

The container also needs the Vulkan runtime libraries. If using the official Jellyfin image (Debian-based), install them on startup:

```yaml
    entrypoint:
      - /bin/bash
      - -c
      - |
        dpkg -s libvulkan1 > /dev/null 2>&1 || \
          (apt-get update -qq && \
           apt-get install -y -qq --no-install-recommends \
             libvulkan1 mesa-vulkan-drivers libgomp1 > /dev/null 2>&1 && \
           rm -rf /var/lib/apt/lists/*)
        exec /jellyfin/jellyfin
```

Verify GPU detection inside the container:

```bash
# Should show your GPU (e.g., "Intel(R) UHD Graphics 770")
docker exec jellyfin apt-get update -qq && \
  docker exec jellyfin apt-get install -y -qq vulkan-tools && \
  docker exec jellyfin vulkaninfo --summary
```

#### Building Inside Docker (ABI Compatibility)

When Jellyfin runs in a container, the whisper binary must be compiled against matching system libraries. Build inside a container with the same base image:

```bash
# On the Docker host:
docker run --rm -v /opt/whisper:/output debian:trixie bash -c '
  apt-get update && apt-get install -y git cmake g++ libvulkan-dev &&
  git clone https://github.com/ggerganov/whisper.cpp.git /tmp/whisper &&
  cd /tmp/whisper &&
  cmake -B build -DGGML_VULKAN=ON -DBUILD_SHARED_LIBS=OFF &&
  cmake --build build --config Release -j$(nproc) &&
  cp build/bin/whisper-cli /output/whisper-cli
'
```

### CUDA (NVIDIA)

For NVIDIA GPUs with CUDA support:

#### Building whisper.cpp with CUDA

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
cmake -B build \
  -DGGML_CUDA=ON \
  -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
```

#### Docker: NVIDIA GPU Passthrough

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin
    runtime: nvidia
    environment:
      - NVIDIA_VISIBLE_DEVICES=all
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
    volumes:
      - /opt/whisper:/opt/whisper:ro
```

> Requires the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html).

### Verifying GPU Acceleration

After configuring GPU support, trigger a transcription and check the Jellyfin logs. You should see:

```
# Vulkan
whisper_backend_init_gpu: using Vulkan0 backend

# CUDA
whisper_backend_init_gpu: using CUDA0 backend
```

If you see `no GPU found` or `using CPU backend`, the binary was not built with GPU support or the runtime drivers are missing.

### Model Recommendations

| Model | Size | Speed (CPU) | Speed (GPU) | Quality | Use Case |
|---|---|---|---|---|---|
| `ggml-large-v3-turbo-q5_0.bin` | 574 MB | Moderate | Fast | Excellent | **Recommended.** Best quality/size ratio. |
| `ggml-large-v3-turbo.bin` | 1.6 GB | Slow | Fast | Excellent | Full-precision turbo. Slightly better quality, 3x larger. |
| `ggml-medium-q5_0.bin` | 539 MB | Moderate | Fast | Very good | Similar size to turbo-q5 but slower and less accurate. |
| `ggml-medium.bin` | 1.5 GB | Moderate | Fast | Very good | Full-precision medium model. |
| `ggml-small.bin` | 488 MB | Fast | Very fast | Good | Faster inference, lower accuracy. |
| `ggml-base.bin` | 148 MB | Fast | Very fast | Fair | Lightweight. Fast but noticeably less accurate. |
| `ggml-tiny.bin` | 78 MB | Very fast | Very fast | Basic | Smallest model. Only for testing or constrained environments. |

The Q5 quantized models offer nearly identical quality to their F16 counterparts at a fraction of the size. `ggml-large-v3-turbo-q5_0` is the default when downloading from the plugin settings page.

## Distributed Transcription (Worker Pool)

By default WhisperSubs transcribes on this Jellyfin server, one job at a time. If you have more than one machine -- a second box with a GPU, a NAS, or a cloud endpoint -- you can pool them and transcribe your backlog **in parallel**.

This is entirely optional and off by default. With no workers configured, nothing changes: the plugin runs exactly as before, on this server. When you add workers, the plugin still extracts audio locally and then farms transcription out over HTTP to any OpenAI-compatible Whisper endpoint you point it at. It always prefers your **free local** workers and only "bursts" to a worker with a non-zero **cost weight** when the local ones are all busy.

**Set it up in the plugin settings:**

1. Open **Dashboard** > **Plugins** > **WhisperSubs** and expand **Worker Pool (Optional / Advanced)**.
2. Leave **Also use this server as a worker** on to keep transcribing here too, or turn it off to offload entirely (e.g. a weak NAS handing all work to a beefier box).
3. Click **+ Add worker** for each endpoint. Give it an endpoint URL (base URL, no `/v1` suffix), an optional API key/model, a **max concurrency** (keep `1` per single GPU), and a **cost weight** (`0` = free/local, preferred; `>0` = only used to burst).
4. Use each row's **Test** button (`POST /Workers/TestConnection`) to confirm the endpoint is reachable and transcribes before saving.

The live queue view shows which worker is transcribing which item, so you can see the pool working.

> **Need a worker to point at?** [`worker/`](worker/README.md) is a ready-to-run whisper.cpp + Vulkan worker image (with notes for CPU/NVIDIA/AMD backends). Any server that implements the OpenAI `/v1/audio/transcriptions` and `/v1/audio/translations` endpoints -- e.g. [Speaches](https://github.com/speaches-ai/speaches) -- also works.

### Hosted providers: upload size, base URLs and model names

WhisperSubs extracts 16 kHz mono PCM WAV, which is **1.92 MB per minute** of audio. That matters as soon as you point a worker at a hosted API:

| Audio length | WAV (default) | FLAC | Opus 24k |
|---|---|---|---|
| 13 minutes | ~25 MB | ~13 MB | ~2 MB |
| 40 minutes | 76.8 MB | ~39 MB | ~7 MB |
| 2 hours | 219.7 MB | ~116 MB | ~20 MB |

So a 25 MB cap (OpenAI, and Groq's free tier) accepts only about **13 minutes** of the default WAV upload, and rejects anything longer with `HTTP 413`. Two per-worker settings fix that:

- **Max upload size (MB)** -- what this endpoint accepts. `0` (default) means unlimited, which is right for every self-hosted worker. Set it (25 for OpenAI and for Groq on **both** tiers -- Groq's 100 MB figure applies only to their `url` parameter, which WhisperSubs does not use) and an oversized title fails immediately with a message telling you the size, the cap and roughly how many minutes fit -- instead of uploading the whole file to earn a bare 413.
- **Upload format** -- `WAV` (default), `FLAC` (lossless, about half) or `Opus 24k` (about a tenth; a 2-hour film lands near 20 MB). **Keep WAV for self-hosted workers**: whisper.cpp's `whisper-server` decodes WAV only, and the [`worker/`](worker/README.md) image ships without ffmpeg. Only choose a compressed format on a hosted endpoint documented to accept it (Groq accepts FLAC and OGG and explicitly recommends FLAC for size reduction; OpenAI's documented list is mp3/mp4/mpeg/mpga/m4a/wav/webm).

**Base URL form** -- enter only the base; WhisperSubs appends `/v1/audio/transcriptions` itself, so the URL must **not** already end in `/v1`:

| Provider | Enter this |
|---|---|
| Groq | `https://api.groq.com/openai` |
| OpenRouter | `https://openrouter.ai/api` |
| OpenAI | `https://api.openai.com` |

**Model names and provider limitations:**

- **OpenRouter** requires a namespaced slug (`openai/whisper-large-v3-turbo`, not `whisper-1`); a bare name returns `400`. It also rejects `response_format=srt` outright -- WhisperSubs negotiates to timestamped JSON automatically -- and has **no `/v1/audio/translations` endpoint**, so turn **Can translate** off for it. Timestamps are only available on its OpenAI-compatible models; models that return text without timings cannot produce synchronized subtitles.
- **OpenAI**: timestamps and SRT are available on `whisper-1` only. `gpt-4o-transcribe` / `gpt-4o-mini-transcribe` return `json`/`text` with no timings, so they cannot be used for subtitles.
- Turn off **Can translate** for any provider that does not expose `/v1/audio/translations`.

> **Note:** the automatic scheduled sweep currently processes its backlog one item at a time; manual **Generate** / **Generate All** already fan out across the whole pool. Parallelizing the scheduled sweep is on the [roadmap](ROADMAP.md).

## Configuration

After installation, navigate to **Dashboard** > **Plugins** > **WhisperSubs** to configure:

| Setting | Description |
|---|---|
| **Default Language** | `Auto-detect` reads the language from each file's audio stream metadata and generates matching subtitles. Choose a specific language to force it for all transcriptions. |
| **Audio Languages (auto-detect)** (`AudioLanguageSelection`) | Only applies with **Auto-detect** on a file that has more than one audio language. `All audio languages` (default) transcribes **every** audio track's language -- one `.<lang>.generated.srt` per language. `Primary audio track only` transcribes just the first/primary track and skips the others. A specific default language, or a file with no audio language tags, is unaffected. |
| **Subtitle Mode** | Full, Forced Only, Full + Forced, or Translation Only. See [Subtitle Modes](#subtitle-modes) below. |
| **Enable Auto-Generation** | When enabled, the scheduled task will scan selected libraries and generate subtitles for items that lack them. |
| **Enabled Libraries** | Select which libraries should be monitored for automatic subtitle generation. |
| **Enable Lyrics Generation** | When enabled, music libraries are scanned and audio tracks receive `.lrc` lyrics files (experimental -- whisper is optimized for speech, not singing). |
| **Whisper Binary Path** | *(Advanced)* Absolute path to the `whisper-cli` binary. Leave empty to use the auto-downloaded binary or search `PATH`. |
| **Whisper Model Path** | *(Advanced)* Absolute path to the GGML model file. Leave empty to use the auto-downloaded model. |
| **Whisper Thread Count** | *(Advanced)* Number of CPU threads for whisper inference. `0` = whisper default (4). Set to your CPU core count for faster transcription. |
| **Maximum subtitle line length** (`SubtitleMaxLineLength`) | *(Advanced)* Maximum characters per subtitle cue, passed to `whisper-cli` as `--max-len` together with `--split-on-word` so a cap never breaks mid-word. `0` (default) = unlimited, which is whisper.cpp's own default, so existing installs are unchanged. **Raise this if subtitles arrive as one long run-on line** -- whisper applies no cap of its own, so a stretch it fails to punctuate lands in a single enormous cue. Broadcast subtitling caps a line near 42 characters; `47` is a good starting point. Applies to the local `whisper-cli` only -- a remote worker segments its own output, so set `WHISPER_MAX_LEN` on the worker as well (see [the worker README](worker/README.md)). |
| **Also use this server as a worker** (`EnableLocalWorker`) | *(Worker Pool)* Whether this Jellyfin host's own whisper participates in the pool. On by default. Turn it off to transcribe **only** on the remote workers below -- e.g. a weak NAS offloading entirely to a beefier box. |
| **Workers** | *(Worker Pool)* A list of extra OpenAI-compatible transcription endpoints to pool alongside (or instead of) this server. Empty by default. Each row has an endpoint URL, optional API key, optional model, max concurrency, cost weight, **max upload size** (`0` = unlimited), **upload format** (`wav` default / `flac` / `opus`), and a "can translate" flag. WhisperSubs preserves direct SRT for existing servers; if an endpoint rejects SRT, it negotiates once to timestamped `verbose_json` segments (supported by Groq, OpenRouter-compatible providers, and OpenAI `whisper-1`), converts them to SRT, and caches that choice. Text-only JSON models cannot produce synchronized subtitles because they return no timestamps. See [Distributed Transcription](#distributed-transcription-worker-pool). |
| **Job timeout -- real-time factor** (`JobTimeoutRealtimeFactor`) | *(Advanced)* Upper bound on how much slower than real-time a remote worker may run before a single call is presumed hung and cancelled. Per-call deadline = audio length x this factor (clamped by the min/max below). Default `6`. A slow-but-working pass is never cut off; a dead endpoint is. |
| **Job timeout -- minimum seconds** (`JobMinTimeoutSeconds`) | *(Advanced)* Floor for the per-call deadline, so a tiny detection clip still gets a sane minimum. Default `60`. |
| **Job timeout -- maximum hours** (`JobMaxTimeoutHours`) | *(Advanced)* Absolute cap for the per-call deadline; a genuinely long film's worst case still fits under it. Default `12`. |

### Subtitle Modes

| Mode | What it generates | Performance |
|---|---|---|
| **Full** (default) | Complete transcription of all speech | Fast -- single whisper run per audio track |
| **Forced Only** | Only foreign-language dialogue (e.g., French lines in an English movie) | Slow -- see below |
| **Full + Forced** | Both files per track | Slowest -- runs both pipelines |
| **Translation Only** | Only an English translated subtitle (skips native-language transcription) | Fast -- single `--translate` pass; medium/large models recommended |

> **Performance warning for Forced / Full + Forced modes:**
> Forced subtitle generation uses a multi-step pipeline: audio extraction, VAD-based speech segmentation, then **per-chunk language detection** on every ~30-second segment of the movie. For a 2-hour film this means ~240 individual whisper calls just for detection, before any transcription begins. On CPU, this phase alone can take **10--20+ minutes per movie**. GPU acceleration helps significantly.
>
> If you don't need forced subtitles (most users don't), use **Full** mode for much faster processing.

### Language Handling

The plugin supports three language modes:

1. **Auto-detect (recommended)** -- The plugin uses FFprobe to read the audio stream's language tag (e.g., `spa` → `es`, `eng` → `en`). Subtitles are generated in the language that matches the audio. If a file has multiple audio tracks in different languages, subtitles are generated for each one.

   **Multiple audio languages.** By default (`AudioLanguageSelection = All`) every audio track's language is transcribed, producing one `.<lang>.generated.srt` per language (e.g. a Spanish + English file yields both `Movie.es.generated.srt` and `Movie.en.generated.srt`, each transcribed from its own audio track). Set **Audio Languages (auto-detect)** to `Primary audio track only` (`AudioLanguageSelection = PrimaryOnly`) to transcribe only the first/primary audio track and skip the secondary ones. This only affects auto-detect on multi-language files; a specific default language, or a file with no audio language tags, already resolves to a single track. The English translation pass is independent of this setting.

2. **Whisper auto-detection** -- When no language metadata is available, the request falls through to whisper's built-in language detection (`-l auto`), which analyzes the first 30 seconds of audio.

3. **Forced language** -- Set a specific language code (e.g., `es`) in the configuration or per-request via the API. This overrides detection and tells whisper to transcribe using that language model.

### Subtitle Timing

whisper.cpp emits subtitle segments back-to-back with no gaps, so the next line can appear on screen during the pause *before* it is actually spoken. The plugin corrects this, and the relevant settings are **on by default**:

| Setting | What it does |
|---|---|
| **Enable VAD** | Runs whisper-cli with its native **Silero Voice Activity Detection** (`--vad`), so each cue starts at the real speech onset rather than during the preceding silence. The Silero VAD model (~865 KB) is auto-downloaded into the plugin's `whisper/vad/` data directory on first use. This is the primary speech-onset mechanism. |
| **Silero VAD model** | Selects the Silero VAD model version: **v5.1.2** (default -- preserves existing timing on upgrade) or **v6.2.0** (newer, opt-in). The chosen model (~885 KB) is downloaded automatically on the next run if not already present. |
| **VAD threshold** | Speech probability cutoff (`--vad-threshold`). Lower values detect more speech; higher values are stricter. Default: `0.5`. Leave blank to use whisper's built-in default. The six VAD tuning settings below apply only when **Enable VAD** is on. |
| **VAD min speech duration** | Minimum speech segment length in milliseconds to keep (`--vad-min-speech-duration-ms`). Default: `250`. |
| **VAD min silence duration** | Minimum silence gap in milliseconds before a segment boundary (`--vad-min-silence-duration-ms`). Default: `100`. |
| **VAD max speech duration** | Maximum speech segment length in seconds before a forced split (`--vad-max-speech-duration-s`). Default: unlimited. |
| **VAD speech pad** | Padding in milliseconds added around each detected speech chunk (`--vad-speech-pad-ms`). Default: `30`. |
| **VAD samples overlap** | Overlap fraction between consecutive analysis windows (`--vad-samples-overlap`). Default: `0.1`. |
| **Align subtitles to speech** | Older, energy-based fallback. Snaps each subtitle's start to the detected speech onset using a quick FFmpeg silence-detection pass over the audio. Used only when **Enable VAD** is off (native VAD handles this more reliably). |
| **Also align VAD / worker timestamps to speech** | If lines still appear early with local VAD or worker/provider timestamps, also runs the forward-snap above. Off by default; requires **Align subtitles to speech** on. Only moves a start later, never earlier (though on coarse audio it may push a few starts slightly late). |
| **Compensate audio start offset** | Shifts all subtitle timestamps by the audio stream's container start time, keeping subtitles in sync when a file's audio doesn't begin exactly at 0:00. |

> These corrections run against the audio extracted on the Jellyfin server, so they apply to both local whisper-cli output and timestamped remote/worker output for full and translated subtitles. Remote/provider-owned timing requires the explicit **Also align VAD / worker timestamps to speech** opt-in. Forced subtitles are unaffected.
>
> With native VAD enabled (the default), the FFmpeg silence-detection pass is skipped -- unless **Also align VAD / worker timestamps to speech** is enabled, which layers it on top for content where lines still appear early.

## Usage

### Admin Dashboard

The plugin adds a dedicated page to the Jellyfin admin dashboard (accessible from **Dashboard** > **Plugins** > **WhisperSubs**, or from the main sidebar menu). From there you can:

- **Configure** the plugin settings (language, subtitle mode, binary/model paths, enabled libraries).
- **Manage the whisper engine** -- download binaries (CPU / Vulkan / CUDA / ROCm) and models directly from the UI.
- **Browse** all libraries and their items.
- **See** which items already have subtitles (green check / orange cross).
- **Select a language** for subtitle generation (auto-detect or any specific language).
- **Generate** subtitles for individual items with a single click.
- **Monitor progress** -- a live banner shows the current item, processing phase, and queue depth.

### REST API

All endpoints require Jellyfin admin authentication. Setup endpoints additionally require elevated privileges.

**Library & Items**

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Plugins/WhisperSubs/Libraries` | List all media libraries |
| `GET` | `/Plugins/WhisperSubs/Libraries/{libraryId}/Items` | List items in a library (supports `startIndex` and `limit`) |
| `POST` | `/Plugins/WhisperSubs/Items/{itemId}/Generate?language=auto` | Queue subtitle generation (priority) |
| `GET` | `/Plugins/WhisperSubs/Items/{itemId}/AudioLanguages` | Detect audio languages in a media file |
| `GET` | `/Plugins/WhisperSubs/Items/{itemId}/Status` | Check subtitle generation status |

**Queue & Task**

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Plugins/WhisperSubs/Queue` | Queue status: current item, progress, phase, remaining count. Also returns `pending` (inbound items in the order they will run) and `workers` (live per-worker load -- which endpoint is transcribing what) for the worker pool. |
| `POST` | `/Plugins/WhisperSubs/Workers/TestConnection` | Test a worker endpoint: POSTs a tiny silent clip to confirm reachability, auth, and a working transcribe path before you save it. Returns `{ok, latencyMs, message}`. |
| `POST` | `/Plugins/WhisperSubs/RunTask` | Trigger the scheduled subtitle generation task |
| `GET` | `/Plugins/WhisperSubs/Models` | List downloaded models with active/size info |

**Engine Setup** (requires elevated privileges)

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Plugins/WhisperSubs/Setup/Status` | Binary/model status, GPU detection, platform info |
| `GET` | `/Plugins/WhisperSubs/Setup/BinaryVariants` | Available binary variants for this platform |
| `POST` | `/Plugins/WhisperSubs/Setup/DownloadBinary?variant=cpu` | Download whisper-cli binary |
| `GET` | `/Plugins/WhisperSubs/Setup/AvailableModels` | Model catalog with sizes and descriptions |
| `POST` | `/Plugins/WhisperSubs/Setup/DownloadModel?name=...` | Download a model from HuggingFace |
| `GET` | `/Plugins/WhisperSubs/Setup/Progress` | Download progress (percent, message, errors) |
| `POST` | `/Plugins/WhisperSubs/Setup/Models/{filename}/Activate` | Set a downloaded model as active |
| `DELETE` | `/Plugins/WhisperSubs/Setup/Models/{filename}` | Delete a downloaded model |

The `language` parameter accepts `auto` (default), or any ISO 639-1 code (`en`, `es`, `fr`, etc.).

### Scheduled Task

A scheduled task named **Generate Subtitles** is registered under the **WhisperSubs** category. It can be configured in **Dashboard** > **Scheduled Tasks** with your preferred schedule or triggered manually. The task:

1. Scans all enabled libraries (or all libraries if none are explicitly selected).
2. Finds video items that lack subtitles.
3. Generates subtitles using the configured default language (auto-detect by default).
4. Reports progress in the Jellyfin task UI.

#### Skipping Already-Subtitled Media

The auto-generation task skips media that already has a usable subtitle in the needed language, so an already-subtitled library is not needlessly re-processed. For the translation pass, an existing English subtitle track -- embedded **or** external -- counts as already translated and is skipped. Forced (foreign-dialogue-only) and image-based subtitle tracks do **not** count as satisfying the need, so full subtitles are still generated when only those are present.

These settings control it. The skip toggles and **Generate original-language subtitles** are **on by default**; **translation** and the image-subtitle toggle are **off by default**:

| Setting | What it does |
|---|---|
| **Skip media that already has subtitles** | Skips media that already has a usable subtitle in the needed language, including an existing English subtitle when translating. |
| **Ignore forced subtitles when skipping** | Forced subtitle tracks do not count as "already subtitled", so full subtitles are still generated when only forced tracks exist. |
| **Generate original-language subtitles** | The main switch: transcribe each title in its own spoken language — a Korean film gets Korean, an English film gets English. On by default. |
| **Count image-based subtitles as present** | When on, existing image-based subtitles (PGS/VOBSUB) count as "already subtitled" and no text subtitle is generated. Off by default, since image subs can't be searched or edited. |
| **Also create an English subtitle when a title has none** | Translation to English. For a title whose audio isn't English and that has no English subtitle, additionally produce one. Skips titles that already have English audio or an English subtitle. Off by default. |

> **What Whisper can produce:** Whisper transcribes the speech it hears, so it writes a subtitle in the title's **own audio language** — that's the **Generate original-language subtitles** switch (an English film naturally gets English subtitles here). The one thing it can additionally do is **translate to English**: that's the separate **Translation** toggle, which only ever *adds* an English subtitle to a foreign-language title that doesn't already have one. There are no other targets — English is the only language Whisper translates *into*.
>
> **Scope:** the skip logic and these toggles apply to the **scheduled auto-generation task** and bulk "Generate all" actions. A **manual "Generate" on a single item always transcribes** (it bypasses the skip and the original-language toggle), so you can force fresh subtitles for a file even when it already has some — e.g. to replace a poor embedded track.
>
> **Note:** detection reads each item's subtitle streams from Jellyfin's library metadata, so a recent library scan keeps it accurate. If an item hasn't been scanned yet, the plugin errs toward generating rather than wrongly skipping.

## How It Works

1. **Language Detection** -- FFprobe reads the audio stream metadata to determine the spoken language(s).
2. **Audio Extraction** -- FFmpeg extracts a 16 kHz mono WAV track from the media file.
3. **Transcription** -- The extracted audio is passed to whisper.cpp, which produces an SRT subtitle file. For forced subtitles, the audio is first segmented via VAD (silence detection), then each ~30-second chunk is language-classified before selectively transcribing only foreign-language segments.
4. **Output** -- Files are saved alongside the original media:
   - Full subtitles: `Movie.es.generated.srt`
   - Forced subtitles: `Movie.es.forced.generated.srt`
   - Lyrics: `Song.lrc`
5. **Metadata Refresh** -- The item's metadata is refreshed so Jellyfin picks up the new files immediately.

Temporary audio files are cleaned up automatically after processing. Items that have already been processed are tracked with marker files (`.noforeignlang`) to avoid redundant work on subsequent scans.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for planned features and design details.

## Other Jellyfin Projects by GeiserX

- [smart-covers](https://github.com/GeiserX/smart-covers) — Cover extraction for books, audiobooks, comics, magazines, and music libraries with online fallback
- [quality-gate](https://github.com/GeiserX/quality-gate) — Restrict users to specific media versions based on configurable path-based policies
- [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder) — Automatic 720p HEVC/AV1 transcoding service with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) — Sync Jellyfin access with Telegram channel membership


## Supporters

> This project is made possible by generous supporters:
> **yskaa001**

## License

This project is licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for the full text.
