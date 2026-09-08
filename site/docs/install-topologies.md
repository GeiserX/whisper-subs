---
title: Platform-specific installs
description: Getting the whisper engine working on TrueNAS, Proxmox LXC, Synology, unRAID and other non-standard Jellyfin deployments.
sidebar_position: 4
---

# Platform-specific installs

Most support reports that start with "the plugin downloaded the binary but nothing happens" come from four deployments: TrueNAS SCALE, Proxmox LXC, Synology DSM and unRAID. The plugin is not doing anything different on them. The container or the CPU is.

## What all of these have in common

Two things have to be true before a single subtitle gets generated.

**1. The binary has to land somewhere writable that survives container recreation.**

You do not choose that directory. The plugin writes `whisper-cli` under Jellyfin's own data path, at `<data path>/WhisperSubs/whisper/whisper-cli`:

| Deployment | Jellyfin data path | Binary ends up at |
|---|---|---|
| `jellyfin/jellyfin` container, and the NAS app images built on it (TrueNAS, Synology, unRAID) | `/config/data` | `/config/data/WhisperSubs/whisper/whisper-cli` |
| Debian/Ubuntu package on bare metal or in an LXC | `/var/lib/jellyfin/data` | `/var/lib/jellyfin/data/WhisperSubs/whisper/whisper-cli` |

On every mainstream container template `/config` is a mapped volume, so the binary itself persists. What does not persist is anything you install into the container with `apt`. That distinction is the whole of the problem on TrueNAS and unRAID.

The plugin settings page reports the path it actually used. Trust that over the table above.

:::warning
`GET /Plugins/WhisperSubs/Setup/Status` requires an admin token. Pasting that URL into a browser tab answers 401 or 404, which is why several bug reports say "couldn't access the endpoint". Read the status from the plugin settings page instead.
:::

**2. The shared libraries the chosen build needs have to exist inside the container.**

| Variant (dropdown label) | Needs at runtime |
|---|---|
| `noavx` (CPU (Compatibility)) | nothing |
| `cpu` (CPU Only) | `libgomp.so.1` |
| `cuda12-noavx` (NVIDIA CUDA 12 (Compatibility)) | `libcuda.so.1`, supplied by the NVIDIA Container Toolkit |
| `cuda12` (NVIDIA CUDA 12) | `libcuda.so.1` and `libgomp.so.1` |
| `vulkan-noavx` (Vulkan (Compatibility)) | `libvulkan.so.1` and a Mesa ICD |
| `vulkan` (Vulkan) | `libvulkan.so.1`, a Mesa ICD and `libgomp.so.1` |

The `noavx` builds are compiled with `-DGGML_OPENMP=OFF` and SSE4.2 only. They have no external dependency and no AVX instructions, so they run in a minimal container on an old CPU. When in doubt, start there. It costs some speed and removes an entire class of failure.

Two exit codes in the Jellyfin log tell you which of the two problems you have:

- **127** means a shared library is missing. The library name is on the same log line.
- **132** means SIGILL, an illegal instruction. The binary was compiled for a CPU newer than yours. Switch to a Compatibility variant.

Both appear in the log when a real transcription runs. Running the binary with `--help` is a useful first check, but it only proves the dynamic linker is satisfied. `--help` returns before the AVX2 code executes, so an AVX2 build can pass it on a CPU that will crash later. It can also exit non-zero on a healthy build. Read that exit code as "127 means missing library, 132, 134 or 135 means it crashed on launch, anything else means it started".

Prebuilt binaries exist for Linux only. `linux-x64` gets all seven variants; `linux-arm64` gets `cpu` and `noavx` only, with no GPU builds. On a Windows or macOS Jellyfin host the Download button has nothing to offer and you must supply your own `whisper-cli`.

:::tip
Run plugin **v3.21.1.0 or newer** (current releases are 4.x). Older builds re-selected the GPU-recommended variant on every visit to the settings page, so a second Download could silently overwrite a working Compatibility build with one that crashes.
:::

## TrueNAS SCALE / TrueCharts

This is the most common installation among bug reports, and the one where the usual container advice does not apply.

### What breaks by default

The app image is minimal and the container filesystem is ephemeral. You cannot fix it with `apt install`, because the next app update throws that filesystem away. Three failures follow from that:

- **`libgomp.so.1: cannot open shared object file`**, exit 127. The default `cpu` build links OpenMP; the image does not ship it.
- **Exit code 132 with no message.** Many TrueNAS boxes run CPUs without AVX2. The `cpu`, `vulkan` and `cuda12` builds are compiled with AVX2, FMA and F16C.
- **`libcudart.so.12: cannot open shared object file`** on an NVIDIA box. The NVIDIA Container Toolkit mounts driver libraries (`libcuda.so.1`) into the container by design, not the CUDA runtime. There is nowhere to install the runtime that survives.

### The fix

Pick a variant that needs nothing, in the plugin settings page under the **whisper-cli Binary** panel:

- CPU only: **CPU (Compatibility)** (`noavx`).
- NVIDIA GPU: **NVIDIA CUDA 12 (Compatibility)** (`cuda12-noavx`). Since v3.18.1.0 the CUDA runtime is statically linked into the binary, so the only shared library it needs is `libcuda.so.1`, which the toolkit already provides.
- Intel or AMD GPU via Vulkan: only if `libvulkan.so.1` and a Mesa ICD are already in the image. Check before choosing it; if they are absent, use `noavx` on CPU or offload to a [remote worker](/remote-workers).

Current plugin versions detect a missing `libgomp.so.1` and a CPU without AVX2, then fall back to `noavx` at download time. Choosing the variant yourself is what to do when that automatic fallback does not fire.

### Verify

Open the TrueNAS **Shell** and find the container:

```bash
docker ps --format '{{.Names}}' | grep -i jellyfin
```

Then check the binary launches:

```bash
docker exec <container> /config/data/WhisperSubs/whisper/whisper-cli --help > /dev/null
echo "exit: $?"
```

`127` is a missing library and `132` is an illegal instruction. Anything else, including a non-zero code, means it started. To see which library is missing:

```bash
docker exec <container> ldd /config/data/WhisperSubs/whisper/whisper-cli
```

Read the whole output. Every line should resolve to a path; a line ending in `not found` is your failure. On a working `noavx` build the list is short and contains no GPU or OpenMP entries.

Finish by generating a subtitle for one short item from the plugin settings page. That is the only check that exercises the AVX2 code path, and the Jellyfin log will name the failure if there is one.

:::warning
On TrueNAS releases before 24.10, apps ran under k3s and there is no `docker` command. Use the app's own Shell action in the web UI and run the `whisper-cli --help` and `ldd` commands directly.
:::

## Proxmox LXC

Jellyfin installed from the Debian package inside an LXC container, usually via a community helper script. This is a normal Debian system, so `apt` works and persists. The problems are elsewhere.

### What breaks by default

- **The data path is not `/config`.** The binary is at `/var/lib/jellyfin/data/WhisperSubs/whisper/whisper-cli`, owned by the `jellyfin` service user. Container guides that reference `/config/data` do not apply, and a binary you drop in there by hand as root will fail to execute.
- **Old CPU, new GPU.** A lot of LXC hosts are second-hand workstation boards. An Intel Xeon E5 v2 has AVX but not AVX2, and it is often paired with a modern Arc or GeForce card. GPU transcription works fine on that combination. Language detection does not, because it runs on the CPU by design (it passes `--no-gpu`, since per-chunk GPU init costs more than it saves), and the AVX2 code in the `vulkan` or `cuda12` build is an illegal instruction on that CPU. The symptom hides the cause. You get `Could not detect language`, once per chunk, forever.

### The fix

Install the libraries the way you would on any Debian box:

```bash
apt-get update && apt-get install -y libgomp1
# Vulkan (Intel/AMD GPU) additionally:
apt-get install -y libvulkan1 mesa-vulkan-drivers
```

Then, if the CPU lacks AVX2, choose the Compatibility build for your GPU: **Vulkan (Compatibility)** (`vulkan-noavx`) or **NVIDIA CUDA 12 (Compatibility)** (`cuda12-noavx`). These keep GPU transcription and use an AVX2-free CPU path for detection.

### Verify

Check what your CPU actually supports:

```bash
grep -m1 '^flags' /proc/cpuinfo | tr ' ' '\n' | grep -E '^(avx|avx2|f16c|fma|bmi2|sse4_2)$'
```

The command prints the flags that are present. If `avx2` is absent from that list, only the `*-noavx` builds will run.

Then run the binary as the service user, which checks the instruction set and the file permissions in one go:

```bash
sudo -u jellyfin /var/lib/jellyfin/data/WhisperSubs/whisper/whisper-cli --help > /dev/null
echo "exit: $?"
```

`126` is a permissions or ownership problem, `127` a missing library, `132` an illegal instruction. Anything else means it launched, and the remaining check is a real transcription of one short item.

## Synology DSM

Jellyfin in Container Manager on a DSM box. The CPU is the whole story.

### What breaks by default

Exit code 132 on every attempt, CPU or GPU. Synology's popular Intel Celeron and Pentium models (Gemini Lake, Jasper Lake, Atom) support SSE4.2 and nothing above it: no AVX, no AVX2, no FMA, no F16C, and no BMI2. The default `whisper-cli-linux-x64` build uses AVX2, FMA, F16C and BMI2 with no runtime fallback.

Vulkan does not rescue it, for two reasons. The crash happens in CPU-side ggml code that runs before the GPU backend initialises. And DSM does not ship the Mesa Vulkan ICD or `libvulkan.so`; it exposes `/dev/dri` for VA-API hardware transcoding only.

### The fix

Select **CPU (Compatibility)** (`noavx`) in the plugin settings page and click Download. That build is SSE4.2-only with every extended instruction set disabled, and it has no shared library dependency. Expect roughly 30 to 40 percent slower inference than the AVX2 build.

If you build `whisper-cli` yourself instead, `GGML_BMI2` defaults to **ON** in whisper.cpp and will produce a binary that still crashes on these CPUs. All of these flags are required:

```bash
git clone --depth 1 --branch v1.8.4 https://github.com/ggml-org/whisper.cpp.git
cd whisper.cpp
cmake -B build \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=OFF \
  -DGGML_NATIVE=OFF \
  -DGGML_AVX=OFF -DGGML_AVX2=OFF -DGGML_FMA=OFF -DGGML_F16C=OFF -DGGML_BMI2=OFF \
  -DGGML_SSE42=ON
cmake --build build --config Release -j4
```

`-DBUILD_SHARED_LIBS=OFF` is not optional. Without it the binary needs `libwhisper.so.1` at runtime, which is not in the container.

### Verify

Over SSH on the DSM host, confirm the instruction sets:

```bash
grep -m1 '^flags' /proc/cpuinfo | tr ' ' '\n' | grep -E '^(avx|avx2|f16c|fma|bmi2|sse4_2)$'
```

On a J4125 this prints `sse4_2` and nothing else. If a crash already happened, the kernel logged it:

```bash
sudo dmesg | grep -i 'trap invalid opcode' | tail -5
```

A line naming `whisper-cli` confirms an instruction-set mismatch rather than a missing library or a permissions problem. After switching to `noavx`, generate a subtitle for one short item and re-run the `dmesg` command: no new trap means the build matches the CPU. Checking with `--help` is not enough here, because it returns before the instruction that crashes.

## unRAID

The Jellyfin container from Community Applications, usually with `/config` mapped to `/mnt/user/appdata/jellyfin`. Two traps, both about persistence.

### What breaks by default

- **`apt install` inside a running container is lost.** The next image update or container recreation reverts it, and transcription starts failing with exit 127 again, apparently at random.
- **`/opt` on the unRAID host is a RAM disk.** Bind-mounting a host directory under `/opt` for your own `whisper-cli` and models works until the server reboots, then the directory is empty. Use `/mnt/user/appdata/` instead.

### The fix

For the libraries, either select **CPU (Compatibility)** (`noavx`) and need none at all, or make the install repeat on every container start. unRAID gives you two places to do that: the container template's **Post Arguments** field, or the **User Scripts** plugin with an "At Startup of Array" script.

If you supply your own binary and models rather than using the downloader, keep them on the array or the cache pool:

```yaml
volumes:
  - /mnt/user/appdata/whisper:/opt/whisper:ro
```

Then point **Whisper Binary Path** at `/opt/whisper/whisper-cli` and **Whisper Model Path** at the model file inside that mount.

### Verify

On the unRAID host, prove the host directory is not in RAM:

```bash
df -h /mnt/user/appdata/whisper | tail -1
```

The filesystem column must name a disk or pool. If it says `rootfs` or `tmpfs`, that path lives in memory and will be empty after a reboot.

Then prove the container survives recreation, which is the failure the Post Arguments and User Scripts approaches exist to prevent:

```bash
docker restart jellyfin
docker exec jellyfin ldd /config/data/WhisperSubs/whisper/whisper-cli
```

Every line resolving to a path after the restart means the setup is durable. Repeat the check after the next Jellyfin image update, which is the event that actually reverts an interactive `apt install`.

## Plain Docker Compose

The baseline case. `/config` is a bind mount, so the binary and models persist, but the container's root filesystem does not.

### What breaks by default

Anything installed with `apt` into a running container disappears on `docker compose pull` or `--force-recreate`. A working setup silently reverts to exit 127 after an image update.

### The fix

The simplest answer is **CPU (Compatibility)** (`noavx`), which needs no libraries and therefore cannot regress this way.

If you want the `cpu` build's OpenMP threading, or a GPU variant, reinstall the libraries from the entrypoint so they come back every time the container starts:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin
    entrypoint:
      - /bin/bash
      - -c
      - |
        dpkg -s libgomp1 > /dev/null 2>&1 || \
          (apt-get update -qq && \
           apt-get install -y -qq --no-install-recommends \
             libgomp1 > /dev/null 2>&1 && \
           rm -rf /var/lib/apt/lists/*)
        exec /jellyfin/jellyfin
    devices:
      - /dev/dri:/dev/dri   # only for GPU variants
```

For Vulkan, add `libvulkan1 mesa-vulkan-drivers` to the package list and set the ICD explicitly, because the Vulkan loader does not reliably find it inside a container:

```yaml
environment:
  - VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/intel_icd.json   # AMD: radeon_icd.json
```

### Verify

Force the exact event that breaks it, then check:

```bash
docker compose up -d --force-recreate jellyfin
docker exec jellyfin dpkg -s libgomp1 | grep -m1 '^Status:'
```

`Status: install ok installed` means the entrypoint ran and the library came back. Finish with the binary itself, where every line must resolve to a path:

```bash
docker exec jellyfin ldd /config/data/WhisperSubs/whisper/whisper-cli
```

## When none of this fits

If the container cannot get the libraries and the CPU is too slow for `noavx` to be usable, stop fighting the box. Run the transcription somewhere else and point Jellyfin at it: see [Remote workers](/remote-workers). The plugin still extracts audio locally; only the transcription moves.

For picking between the seven binary variants on hardware not covered here, see [Choosing a variant](/choosing-a-variant). For reading a failure out of the logs, see [Diagnostics](/diagnostics).
