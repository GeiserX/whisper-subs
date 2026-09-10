---
title: Setup guide
description: Installing the whisper engine, including container library requirements and GPU passthrough.
slug: /docs/setup
sidebar_position: 2
---

# Setup guide

WhisperSubs needs two things before it can transcribe anything: the `whisper-cli` binary and a language model. Both are downloaded from the plugin's own settings page, and they are two separate downloads.

The binaries are prebuilt, so they need runtime libraries that a slim Jellyfin container often does not ship. That is what most of this page is about.

## Quick start {#quick-start}

Open **Dashboard → Plugins → WhisperSubs**. The **Whisper Engine** section has two panels, *whisper-cli Binary* and *Language Models*. Each shows an empty orange box until it is satisfied, then a green checked box.

1. **Pick a variant.** The dropdown in the binary panel comes preselected with the variant the plugin recommends after looking at your GPU devices and the libraries present on the system. Take that recommendation unless you have a reason not to. [Variant requirements](#variants) explains the options.
2. **Download the binary.** The plugin fetches it from this project's GitHub release matching the installed plugin version, then launches it once to check it actually runs. If it fails to launch, the plugin retries automatically with a more compatible variant, ending at `noavx`, which has no external library dependencies at all.
3. **Download a model.** This is the second, separate download, from Hugging Face. The default is **Large V3 Turbo (Q5)**, about 574 MB, and it suits most libraries. If you want the English translation feature, pick a non-turbo model instead: the turbo models were fine-tuned without the translate task and return the source language rather than English.
4. **Install any missing libraries.** If the binary panel reports a missing shared library, see [Docker setup](#docker-setup) below.

:::note[The variant dropdown is empty]
It reads "No prebuilt binaries for this platform" and both controls are disabled on **macOS and Windows**. The plugin only publishes prebuilt `whisper-cli` binaries for Linux, so there is nothing to offer.

This is not a broken install. Install `whisper-cli` yourself (Homebrew, a whisper.cpp release, or your own build), then set **Whisper Binary Path** and **Whisper Model Path** under Advanced settings. A manually installed binary is fully supported: the plugin will find it and use it.
:::

:::tip[Running on bare metal Linux?]
The required libraries are usually already present. Select your variant and download; the plugin tells you if anything is missing.
:::

## Variant requirements {#variants}

Seven variants are published. The three `*-noavx` builds are compiled with OpenMP off (`-DGGML_OPENMP=OFF`), so unlike the rest they need no `libgomp1`.

| Variant | Required libraries | Install command |
|---|---|---|
| CPU (`cpu`) | `libgomp1` | `apt install libgomp1` |
| CPU compatibility (`noavx`) | none, self-contained | — |
| Vulkan (`vulkan`) | `libgomp1` `libvulkan1` + your GPU's Vulkan driver | Intel/AMD: `apt install libgomp1 libvulkan1 mesa-vulkan-drivers`. NVIDIA: [see below](#vulkan-nvidia) |
| Vulkan compatibility (`vulkan-noavx`) | `libvulkan1` + your GPU's Vulkan driver | Intel/AMD: `apt install libvulkan1 mesa-vulkan-drivers`. NVIDIA: [see below](#vulkan-nvidia) |
| CUDA 12 (`cuda12`) | `libgomp1` + NVIDIA Container Toolkit on the host | [NVIDIA docs](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) |
| CUDA 12 compatibility (`cuda12-noavx`) | NVIDIA Container Toolkit on the host | [NVIDIA docs](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) |
| ROCm (`rocm`) | `libgomp1` + ROCm runtime | [ROCm docs](https://rocm.docs.amd.com/) |

### Vulkan on NVIDIA {#vulkan-nvidia}

The dropdown offers Vulkan for NVIDIA too, but the packages differ. `libvulkan1` is only the loader. It finds a driver, it does not provide one. The driver, the ICD, comes from the GPU vendor: `mesa-vulkan-drivers` supplies the Intel (ANV) and AMD (RADV) ICDs and nothing for NVIDIA. On NVIDIA the ICD ships with the proprietary driver and registers as `/usr/share/vulkan/icd.d/nvidia_icd.json`. Install Mesa on an NVIDIA-only box and the loader finds no device, so `whisper-cli` runs on the CPU.

Prefer **CUDA 12 (`cuda12`)** on NVIDIA. It is the better-supported path, and the plugin recommends it on its own when it finds an NVIDIA device plus `libcuda.so.1`. Vulkan is still the right pick when CUDA is not available to you: the CUDA build will not run in your container, or one binary has to drive an NVIDIA card and an Intel or AMD GPU in the same machine.

Container setup differs as well. See [Vulkan variant, NVIDIA GPU](#vulkan-docker-nvidia).

### Which variants your platform gets

| Platform | Offered in the dropdown |
|---|---|
| Linux x64 | All seven |
| Linux arm64 | `cpu` and `noavx` |
| macOS, Windows | None. Install `whisper-cli` manually and set the path in Advanced settings |

### Why the compatibility builds exist

The `cpu`, `vulkan` and `cuda12` builds are compiled with AVX2, FMA and F16C. On a CPU without those instructions they die immediately with an illegal instruction, exit code 132. That is common on budget NAS boxes, Atom and Celeron chips, and some VMs.

The plugin detects this. When it sees no AVX2 support it recommends the matching `*-noavx` variant instead of the standard one, and if a download validates badly at runtime it walks a fallback chain to a more compatible build: `cuda12` and `vulkan` fall back to `cpu`, `cpu` falls back to `noavx`, and `cuda12-noavx`, `vulkan-noavx` and `rocm` fall back to `noavx` directly. `noavx` is the end of the chain, which is why it is built to depend on nothing.

You can also select **CPU (Compatibility)** manually at any time.

## Docker setup {#docker-setup}

### CPU variant (simplest)

Add to your container entrypoint or run once inside the container:

```bash
apt-get update -qq && apt-get install -y -qq --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*
```

If you would rather install nothing, use the `noavx` variant. It runs without `libgomp1`, at the cost of the OpenMP threading speed-up.

### Vulkan variant (Intel / AMD GPU)

Pass the GPU device through:

```yaml
# docker-compose.yml
services:
  jellyfin:
    image: jellyfin/jellyfin
    devices:
      - /dev/dri:/dev/dri
```

Then inside the container:

```bash
apt-get update -qq && \
apt-get install -y -qq --no-install-recommends \
  libgomp1 libvulkan1 mesa-vulkan-drivers && \
rm -rf /var/lib/apt/lists/*
```

### Vulkan variant (NVIDIA GPU) {#vulkan-docker-nvidia}

Do not pass `/dev/dri`, and do not install `mesa-vulkan-drivers`. The NVIDIA Vulkan driver is injected into the container by the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html), and only when `NVIDIA_DRIVER_CAPABILITIES` includes `graphics`. The compute-oriented values (`utility`, `compute,utility`) leave it out, which is why a container where `nvidia-smi` works can still have no Vulkan device.

```yaml
# docker-compose.yml
services:
  jellyfin:
    image: jellyfin/jellyfin
    runtime: nvidia
    environment:
      - NVIDIA_VISIBLE_DEVICES=all
      - NVIDIA_DRIVER_CAPABILITIES=compute,utility,graphics
```

Then inside the container, the loader and OpenMP only:

```bash
apt-get update -qq && \
apt-get install -y -qq --no-install-recommends \
  libgomp1 libvulkan1 && \
rm -rf /var/lib/apt/lists/*
```

Check the driver actually arrived:

```bash
docker exec jellyfin ls /usr/share/vulkan/icd.d/
```

No `nvidia_icd.json` means the `graphics` capability is not set, and Vulkan will not see the card whatever else you install.

### Persistent install via entrypoint

Libraries installed interactively inside a running container are lost when the container is recreated or updated. To reinstall them automatically, override the entrypoint:

```yaml
# docker-compose.yml
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
      - /dev/dri:/dev/dri   # only if using GPU variants
```

:::note[unRAID users]
You can add the `apt install` command to the container's **Post Arguments**, or use the **User Scripts** plugin to run it on container start.
:::

## Vocal separation (optional) {#vocal-separation}

Vocal separation is optional and improves transcription accuracy on noisy content. If it is missing or fails, the plugin transcribes the original unseparated audio. A failed or hung custom separator can add processing time before its timeout, but it does not prevent transcription.

[BSRoformer.cpp](https://github.com/chenmozhijin/BSRoformer.cpp) provides `bs_roformer-cli`, which isolates vocals from background music and noise before transcription. The plugin can download its pinned binary and model automatically.

Unlike `whisper-cli`, this downloader does work on macOS and Windows, because BSRoformer.cpp publishes its own prebuilt archives upstream rather than relying on this project's CI.

### Variant requirements

Linux and macOS archive extraction requires `tar` with xz support. Every Linux `bs_roformer-cli` variant also requires `libgomp1` and the same GPU dependencies as `whisper-cli`. Windows needs the Microsoft Visual C++ 2015–2022 Redistributable.

| Variant | Required packages | Install command |
|---|---|---|
| CPU | `libgomp1` `xz-utils` | `apt install libgomp1 xz-utils` |
| Vulkan | `libgomp1` `xz-utils` `libvulkan1` + your GPU's Vulkan driver | Intel/AMD: `apt install libgomp1 xz-utils libvulkan1 mesa-vulkan-drivers`. NVIDIA: [see above](#vulkan-nvidia) |
| CUDA 12 | `libgomp1` `xz-utils` + NVIDIA Container Toolkit on the host | [NVIDIA docs](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) |

Vulkan and CUDA are not published for every platform:

| Platform | Offered in the dropdown |
|---|---|
| Linux x64 | CPU, Vulkan, CUDA 12 |
| Windows x64 | CPU, Vulkan, CUDA 12 |
| Linux arm64 | CPU only |
| macOS (Intel and Apple silicon) | CPU only, which uses Apple Metal when available |

The GPU variants are experimental. Setup verifies that the CLI launches, but cannot prove real GPU inference works; a failure at that point falls back to CPU.

### Docker setup for vocal separation

If using the automatic downloader on Linux, just make sure the variant dependencies are installed. Use the same entrypoint approach as above:

```yaml
# docker-compose.yml
services:
  jellyfin:
    image: jellyfin/jellyfin
    entrypoint:
      - /bin/bash
      - -c
      - |
        dpkg -s libgomp1 xz-utils > /dev/null 2>&1 || \
          (apt-get update -qq && \
           apt-get install -y -qq --no-install-recommends \
             libgomp1 xz-utils > /dev/null 2>&1 && \
           rm -rf /var/lib/apt/lists/*)
        exec /jellyfin/jellyfin
    volumes:
      - /mnt/user/appdata/roformer:/opt/roformer:ro   # optional custom assets on unRAID
```

### Separation tuning (advanced)

The **Separation tuning (advanced)** block sits under the two download panels and holds four fields. The Download buttons fill the two paths in for you; set them by hand only for a manual install.

| Field | Default | What it does |
|---|---|---|
| BSRoformer binary path | empty | Absolute path to `bs_roformer-cli`, as visible to Jellyfin. |
| BSRoformer model path | empty | Absolute path to a compatible GGUF model. |
| Chunk overlap | `0`, meaning the model's own default | BSRoformer.cpp `--overlap`: how many times each chunk is reprocessed and blended with its neighbours to smooth the boundaries. 1 is the minimum, faster than the default and barely worse on dialogue. 2 to 4 reduce audible seams in music and multiply processing time proportionally. |
| Chunk size (samples) | `-1`, meaning the model's own default | BSRoformer.cpp `--chunk-size`: the audio window fed to the model in one pass, in samples at 44.1 kHz. 2822400, about 64 seconds, works well on 8 GB of VRAM with the Q8_0 model. 176400, 4 seconds, cuts VRAM use at the cost of more passes. |

Two more values are stored but not editable: which binary variant and which model quantization were actually downloaded. The setup page reads them back so it re-offers what you installed rather than defaulting to a GPU recommendation every time it loads.

### Installing it manually {#vocal-separation-manual}

Use this when the container cannot reach GitHub or Hugging Face, or when you want the files on storage the plugin does not manage.

**The binary.** The plugin pins upstream BSRoformer.cpp **v0.1.0** and pulls the archive from that release. Take the same asset yourself:

```text
https://github.com/chenmozhijin/BSRoformer.cpp/releases/tag/v0.1.0
```

The assets are `.tar.xz` on Linux and macOS and `.zip` on Windows. Linux x64 has three: `BSRoformer-linux-x64-cpu.tar.xz`, `BSRoformer-linux-vulkan.tar.xz` and `BSRoformer-linux-cuda-12.9.1.tar.xz`. Extract the archive and keep its contents together, because some builds ship shared libraries next to `bs_roformer-cli` that it needs at runtime.

**The model.** The downloader takes GGUF conversions of the anvuew BS-RoFormer model from [chenmozhijin/BSRoformer-GGUF](https://huggingface.co/chenmozhijin/BSRoformer-GGUF), under `anvuew/BS-RoFormer`, pinned to revision `df802a6773d25ba6ef785ff619daa3e510503168`.

| File | Size | Notes |
|---|---|---|
| `BSRoformer-anvuew-Q4_0.gguf` | 31 MB | Lowest quality, smallest download. |
| `BSRoformer-anvuew-Q5_1.gguf` | 40 MB | Quality and size trade-off for constrained setups. |
| `BSRoformer-anvuew-Q8_0.gguf` | 56 MB | The recommended default. Near-FP32 separation quality at a fraction of the size. |
| `BSRoformer-anvuew-FP16.gguf` | 102 MB | Maximum precision, marginal gain over Q8_0. |

**Where they go.** When the downloader does it, both land in the plugin's data folder: the binary in `vocal-separation/bin/` and the model in `vocal-separation/models/`, so `/config/data/WhisperSubs/vocal-separation/` on a standard Docker install. A manual install can live anywhere Jellyfin can read, and the binary must be executable. Set the two paths under **Separation tuning (advanced)** and save.

:::warning[unRAID: keep it off /opt]
unRAID rebuilds its root filesystem in RAM from the flash drive on every boot, and `/opt` is part of that. Anything you put there is gone after a reboot. Keep the binary and the model under `/mnt/user/appdata` and bind-mount that into the container, as in the compose example above.
:::

### Licences {#vocal-separation-licences}

WhisperSubs is GPL-3.0. It ships neither of these. Both are fetched at runtime, and both are optional, because vocal separation is off by default.

| Component | Licence |
|---|---|
| [BSRoformer.cpp](https://github.com/chenmozhijin/BSRoformer.cpp), which provides `bs_roformer-cli` | [MIT](https://github.com/chenmozhijin/BSRoformer.cpp/blob/master/LICENSE) |
| [anvuew BS-RoFormer](https://huggingface.co/anvuew/BS-RoFormer), the separation model | GPL-3.0 |

The GGUF files the plugin downloads are quantized conversions of that anvuew model, republished by the BSRoformer.cpp author.

## Troubleshooting {#troubleshooting}

### "Missing libgomp.so.1"

The `cpu`, `vulkan`, `cuda12` and `rocm` builds use OpenMP for threading. Install it:

```bash
apt install libgomp1
```

Or switch to the matching `*-noavx` variant, which does not need it. The plugin also makes that switch on its own when the `cpu` build fails to launch.

### "Missing libvulkan.so.1"

The Vulkan variants need the Vulkan loader. Jellyfin's bundled FFmpeg includes its own copy, but `whisper-cli` needs the system one:

```bash
apt install libvulkan1 mesa-vulkan-drivers   # Intel / AMD
apt install libvulkan1                       # NVIDIA, the driver comes from the container toolkit
```

The plugin only checks for the loader, so clearing this warning does not prove Vulkan works. The driver is a separate thing: see [Vulkan on NVIDIA](#vulkan-nvidia).

### Illegal instruction, or exit code 132

The CPU does not support AVX2 and you are running one of the AVX2 builds. Select the matching compatibility variant: `noavx`, `vulkan-noavx` or `cuda12-noavx`.

### Binary downloads return 404

The download URL uses the plugin version to find the matching GitHub release. Make sure the plugin version matches an existing [release](https://github.com/GeiserX/whisper-subs/releases).

### Verify GPU inside Docker

```bash
# Check Vulkan
docker exec jellyfin apt-get update -qq && \
  docker exec jellyfin apt-get install -y -qq vulkan-tools && \
  docker exec jellyfin vulkaninfo --summary

# Check NVIDIA
docker exec jellyfin nvidia-smi
```
