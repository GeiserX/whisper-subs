# WhisperSubs Worker Image

An example, ready-to-run **OpenAI-compatible transcription worker** for the
WhisperSubs remote worker pool. It wraps whisper.cpp's `whisper-server`
([ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp), `examples/server`)
built with **Vulkan** — so it GPU-accelerates on **Intel** and **AMD** integrated
or discrete GPUs — behind a tiny standard-library Python adapter that speaks the
exact HTTP dialect the plugin's `RemoteWhisperProvider` expects.

You run one (or several) of these on any machine with a spare GPU, then point the
plugin at each over HTTP. The plugin extracts audio and farms transcription out to
the workers; all audio still stays inside your own network.

> This is one **example** backend. Any server that implements the OpenAI
> `/v1/audio/transcriptions` and `/v1/audio/translations` endpoints works — see
> [Alternative backends](#alternative-backends) for CPU/NVIDIA options.

---

## What the plugin sends (the contract this image serves)

`Providers/RemoteWhisperProvider.cs` posts `multipart/form-data`. The worker must
serve exactly this:

| Purpose | Method + path | Key form fields | Returns |
|---|---|---|---|
| Transcribe | `POST /v1/audio/transcriptions` | `file` (16 kHz mono WAV), `model`, `response_format=srt`, `language` (optional 2-letter; omitted for auto) | SRT text |
| Detect language | `POST /v1/audio/transcriptions` | `file`, `model`, `response_format=verbose_json` (no `language`) | JSON containing a top-level `language` |
| Translate → English | `POST /v1/audio/translations` | `file`, `model`, `response_format=srt`, `language` (optional source) | English SRT |

Auth is an optional `Authorization: Bearer <key>`. The `file` is always a 16 kHz
mono `s16le` WAV (the plugin extracts it with FFmpeg before sending).

---

## Design: why a thin adapter (and not `whisper-server` directly)

`whisper-server` is **not** fully OpenAI-compatible, verified against
whisper.cpp **v1.8.4** (`examples/server/server.cpp`):

1. **One inference path, not two.** It registers a single inference route —
   `--request-path` + `--inference-path`, default `/inference` — plus `/load` and
   `/health`. It cannot serve both `/v1/audio/transcriptions` **and**
   `/v1/audio/translations`; `--inference-path` can only point the one route at one
   of them.
2. **Translate is a form field, not a path.** Translation is selected by a
   `translate=true` multipart field (`server.cpp` parses `translate`, `language`,
   `response_format` from the form). The plugin instead selects it by **path**
   (`/v1/audio/translations`), sending no `translate` field.
3. **It defaults `language` to `"en"`.** A request with no `language` field would
   force English and break both auto-transcription and the `verbose_json`
   language-detection call.

A pure reverse proxy (nginx/Caddy) can rewrite the path but **cannot inject a new
multipart field** into a streamed request body, so it could never map the
translations path to `translate=true`. Hence a minimal body-aware adapter:

- routes `POST /v1/audio/transcriptions` → upstream `/inference` **verbatim**;
- routes `POST /v1/audio/translations` → upstream `/inference` with a
  `translate=true` part **prepended** to the multipart body — no reparse, the body
  is **streamed** straight through so a 2-hour film (~260 MB WAV) is never buffered
  in RAM (matters when several workers run at once);
- launches `whisper-server` with `--language auto` so detection and auto-mode work
  (`response_format=verbose_json` returns whisper's language name, which the plugin
  maps to a 2-letter code);
- adds optional Bearer auth and a cheap readiness probe;
- supervises the `whisper-server` child and exits (so the container restarts) if it
  dies.

The adapter is ~300 lines of Python standard library — **no pip dependencies** —
in [`adapter.py`](adapter.py). `response_format=srt` and `verbose_json` are both
native to `whisper-server`, so no response rewriting is needed.

> **One model per worker.** `whisper-server` serves a single model, fixed at
> startup by `WHISPER_MODEL`. The plugin's per-worker **Model** field is sent but
> ignored by this backend — choose the model on the worker.

---

## Quick start — Intel / AMD iGPU (Vulkan)

```bash
# from the repo root
docker compose -f worker/docker-compose.example.yml up -d
docker logs -f whisper-subs-worker      # first boot downloads the model
```

Then in Jellyfin: **Dashboard → Plugins → WhisperSubs → Workers**, add a worker:

| Field | Value |
|---|---|
| **Endpoint** | `http://<worker-host>:9010` — base URL, **no** `/v1` suffix |
| **API key** | only if you set `API_KEY` on the worker |
| **Model** | informational (the worker's `WHISPER_MODEL` is authoritative) |
| **Max concurrency** | **1** — one GPU processes ~one stream at a time |

whisper.cpp runs one transcription per GPU context, so keep **MaxConcurrency = 1**
per single-GPU worker. To do more in parallel, run more workers (another GPU,
another host) and add each to the pool.

---

## Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `WHISPER_MODEL` | `large-v3-turbo-q5_0` | GGML model name; downloaded on first run into `/models` |
| `WHISPER_MODEL_DIR` | `/models` | Model cache directory (mount a volume here) |
| `API_KEY` | *(empty)* | If set, `Authorization: Bearer <key>` is required on the POST routes |
| `LISTEN_PORT` | `8080` | Adapter listen port (inside the container) |
| `CONVERT` | `false` | `true` runs FFmpeg to accept non-WAV audio (needs `INSTALL_FFMPEG=true` image) |
| `WHISPER_THREADS` | *(whisper default)* | CPU threads (`--threads`) |
| `WHISPER_LANGUAGE` | `auto` | Upstream default language; per-request `language` still overrides |
| `WHISPER_EXTRA_ARGS` | *(empty)* | Extra `whisper-server` flags, e.g. `--flash-attn` or `--vad --vad-model /models/ggml-silero-v5.1.2.bin` |
| `WHISPER_BACKEND_PORT` | `8081` | Internal `whisper-server` port (localhost only) |
| `WHISPER_READY_TIMEOUT` | `600` | Seconds to wait for the model to load before serving |

---

## GPU backends

### Intel iGPU (Vulkan) — the default

The shipped image already targets Vulkan. Pass `/dev/dri` and add the host
`render` group (see the compose file). Leave `VK_ICD_FILENAMES` **unset** to let
the Vulkan loader auto-discover the Mesa **ANV** driver; only pin it if multiple
GPUs are present and you need a specific one. On Debian/Mesa the Intel ICD is
usually `/usr/share/vulkan/icd.d/intel_icd.x86_64.json` (verify with
`docker exec whisper-subs-worker ls /usr/share/vulkan/icd.d/`). The entrypoint
self-heals a wrong pin by unsetting it and falling back to auto-discovery.

### AMD GPU

- **Vulkan (this image, no rebuild):** works via Mesa **RADV** — pass `/dev/dri`
  exactly as for Intel. Pin `radeon_icd.x86_64.json` only if needed.
- **ROCm/HIP (alternative):** rebuild against the ROCm toolkit — different base
  image and runtime libs:
  ```bash
  # build inside rocm/dev-ubuntu-22.04, then a rocm/runtime base:
  cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
    -DGGML_HIP=ON -DCMAKE_C_COMPILER=hipcc -DCMAKE_CXX_COMPILER=hipcc \
    --target whisper-server
  ```
  Runtime needs `/dev/kfd` + `/dev/dri` and `rocm-hip-runtime`. (This mirrors the
  plugin's own ROCm build recipe.)

### NVIDIA GPU

- **CUDA (recommended for NVIDIA):** rebuild with the CUDA toolkit base image:
  ```bash
  docker build -t whisper-subs-worker:cuda \
    --build-arg BUILD_IMAGE=nvidia/cuda:12.6.3-devel-ubuntu24.04 \
    --build-arg RUNTIME_IMAGE=nvidia/cuda:12.6.3-runtime-ubuntu24.04 \
    --build-arg WHISPER_CMAKE_FLAGS="-DGGML_CUDA=ON -DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON" \
    worker/
  ```
  Run with the NVIDIA Container Toolkit: `docker run --gpus all ...` (or compose
  `deploy.resources.reservations.devices` / `runtime: nvidia`). The CUDA runtime
  base already provides the NVIDIA userspace libs; `mesa-vulkan-drivers` is
  unnecessary for this variant.
- **Vulkan on NVIDIA:** also possible with the default image if the NVIDIA Vulkan
  ICD is exposed to the container, but CUDA is the better-supported path.

### CPU only (no GPU)

```bash
docker build -t whisper-subs-worker:cpu \
  --build-arg WHISPER_CMAKE_FLAGS="-DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON" \
  worker/
```

Drop the `devices:` / `group_add:` lines from the compose file. Expect real-time
factors well above 1 on large models — prefer a smaller model (see below) or a GPU.

---

## Alternative backends

You are not tied to this image. The plugin talks plain OpenAI audio, so any
compliant server works. Pick by hardware:

- **[Speaches](https://github.com/speaches-ai/speaches)** (formerly
  *faster-whisper-server*) — a drop-in OpenAI-compatible server exposing
  `POST /v1/audio/transcriptions` and `POST /v1/audio/translations`. Built on
  **faster-whisper / CTranslate2**, whose accelerated backends are **CPU (INT8)**
  and **NVIDIA CUDA (FP16)**. It is an excellent choice for **CPU-only** boxes and
  **NVIDIA** GPUs. **CTranslate2 has no Vulkan/oneAPI path, so it does *not*
  accelerate on Intel iGPUs** — which is exactly why this whisper.cpp + Vulkan image
  exists for the Intel/AMD-iGPU case.
- **Any other OpenAI `/v1/audio/*` server** (self-hosted or otherwise) that returns
  SRT for `response_format=srt` and a `language` field for `verbose_json`.

Whatever you choose, point the plugin's worker **Endpoint** at its base URL and set
**MaxConcurrency** to match how many streams that backend can really run at once.

---

## Model selection

Models download automatically on first run from
[huggingface.co/ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp)
via whisper.cpp's `download-ggml-model.sh`. Set `WHISPER_MODEL` to any known name:

| `WHISPER_MODEL` | Approx. size | Notes |
|---|---|---|
| `large-v3-turbo-q5_0` | ~547 MB | **Default.** Best speed/quality/size balance; ideal for an iGPU |
| `large-v3-turbo` | ~1.6 GB | Full turbo; a bit more quality, more memory |
| `large-v3` | ~3.1 GB | Highest quality, slowest; ~10 GB RAM under load |
| `medium` / `small` / `base` | ~1.5 GB / ~466 MB / ~142 MB | Lighter/faster, lower accuracy — good for CPU workers |

`large-v3-turbo-q5_0` is the recommended default: fast, small, and high quality.
Raise `mem_limit` in the compose file if you move to a full large model.

---

## Verify it works

Readiness (unauthenticated; returns `{"status":"ok"}` once the model is loaded):

```bash
curl -fsS http://<worker-host>:9010/health
```

Full smoke test — transcribe a 1-second silent WAV (add `-H "Authorization: Bearer <key>"`
if `API_KEY` is set):

```bash
# 16 kHz mono s16le WAV, exactly what the plugin sends
docker run --rm linuxserver/ffmpeg -f lavfi -i anullsrc=r=16000:cl=mono \
  -t 1 -c:a pcm_s16le -f wav - > /tmp/silence.wav 2>/dev/null

curl -fsS http://<worker-host>:9010/v1/audio/transcriptions \
  -F file=@/tmp/silence.wav -F model=whisper-1 -F response_format=srt

# language detection
curl -fsS http://<worker-host>:9010/v1/audio/transcriptions \
  -F file=@/tmp/silence.wav -F model=whisper-1 -F response_format=verbose_json

# translation route (adapter injects translate=true)
curl -fsS http://<worker-host>:9010/v1/audio/translations \
  -F file=@/tmp/silence.wav -F model=whisper-1 -F response_format=srt
```

---

## Building & publishing

The published image is built and pushed by CI, mirroring the repo's conventions:

- **Registry:** Docker Hub, image `drumsergio/whisper-subs-worker`.
- **Tags:** semantic version tags only — e.g. `0.1.0` — **never `:latest`**.
- **Base images:** pinned by explicit tag; use a `@sha256:` digest pin for fully
  reproducible CI builds.
- **whisper.cpp:** pinned to the same tag the plugin uses (`v1.8.4`); bump it in the
  Dockerfile `WHISPER_VERSION` arg in lockstep with the plugin's pin.

Local build:

```bash
docker build -t drumsergio/whisper-subs-worker:0.1.0 worker/
```

---

## Security notes

> **Run this on a trusted network only, unless you set `API_KEY` *and* put TLS in front.**
> With no `API_KEY` (the default) the worker is an **unauthenticated** transcription/translation
> endpoint — anyone who can reach the published port can drive your GPU/CPU. The adapter listens on
> `0.0.0.0` (it must, so the plugin can reach it from another host) and logs a loud warning at startup
> when it is serving without a key. Do **not** port-forward it or run it on a public VPS unprotected.

- Set `API_KEY` to require a `Bearer` token on the transcription and translation routes (the readiness
  probe stays open so Docker healthchecks work). The check is constant-time. The upstream
  `whisper-server` binds to `127.0.0.1` only; the adapter is the sole public listener.
- For traffic that leaves the host, terminate **TLS** at a reverse proxy in front of the worker and use
  an `https://` endpoint in the plugin (it warns when a key is sent over plain HTTP).
- The adapter frames requests strictly by a single non-negative `Content-Length`, rejects
  `Transfer-Encoding` and oversized bodies (`WHISPER_MAX_BODY_BYTES`, default 8 GiB), and drops a
  stalled socket after `WHISPER_SOCKET_TIMEOUT` (default 120 s) so one slow client can't wedge the GPU
  slot. It is still wise to bound total connections at a reverse proxy for an exposed deployment.
- The container runs as a non-root user; the example compose adds `no-new-privileges`, `cap_drop: ALL`,
  and a read-only root filesystem. GPU access is granted only via `/dev/dri` plus the host `render` group.
- **`CONVERT=true`** (with an `INSTALL_FFMPEG=true` image) makes `whisper-server` shell out to FFmpeg on
  the uploaded media — a much larger parser attack surface. Leave it off unless you need non-WAV input,
  and never enable it on an exposed worker.

---

*Part of [WhisperSubs](https://github.com/GeiserX/whisper-subs) — GPL-3.0.*
