---
title: Choosing a whisper binary variant
description: The seven binary variants, which one your CPU and GPU can actually run, and how to fix illegal-instruction crashes.
sidebar_position: 3
---

# Choosing a whisper binary variant

WhisperSubs transcribes by running `whisper-cli`, a native binary. We publish seven prebuilt versions of it. They differ in two ways: the CPU instruction set each was compiled for, and the GPU backend each can use.

Choose one your CPU cannot run and it dies immediately with **exit code 132** and no explanation. That failure is why this page exists.

## The seven variants

| Variant | Name on the Setup page | CPU instructions required | GPU backend | Other runtime libraries |
|---|---|---|---|---|
| `cpu` | CPU Only | AVX2, FMA, F16C, BMI2 | none | `libgomp1` |
| `noavx` | CPU (Compatibility) | SSE4.2 | none | none |
| `vulkan` | Vulkan (Intel / AMD / NVIDIA) | AVX2, FMA, F16C, BMI2 | Vulkan | `libgomp1`, `libvulkan1`, a Vulkan driver |
| `vulkan-noavx` | Vulkan (Compatibility) | SSE4.2 | Vulkan | `libvulkan1`, a Vulkan driver |
| `cuda12` | NVIDIA CUDA 12 | AVX2, FMA, F16C, BMI2 | CUDA 12 | `libgomp1`, NVIDIA driver and CUDA runtime |
| `cuda12-noavx` | NVIDIA CUDA 12 (Compatibility) | SSE4.2 | CUDA 12 | NVIDIA driver and CUDA runtime |
| `rocm` | AMD ROCm | AVX2, FMA, F16C, BMI2 | ROCm / HIP | `libgomp1`, ROCm runtime |

Every build is statically linked, so none of them needs `libwhisper.so`.

SSE4.2 arrived with Intel Nehalem in 2008 and AMD Bulldozer in 2011. AVX2 arrived with Intel Haswell in 2013 and AMD Excavator in 2015. So anything older than Haswell needs a `-noavx` build. So does every Atom, Celeron and Pentium built on the Silvermont or Goldmont cores, no matter how recently it shipped.

:::warning Only four variants need `libgomp1`
`noavx`, `vulkan-noavx` and `cuda12-noavx` are compiled with OpenMP disabled. They are self-contained and will never ask for `libgomp1`. The other four link it for a small threading speed-up: `cpu`, `vulkan`, `cuda12` and `rocm`.

If you have read anywhere that every variant requires `libgomp1`, that is wrong. A missing `libgomp1` is a reason to switch to a `-noavx` build, not a reason to give up on the plugin.
:::

### On ARM64

Only `cpu` and `noavx` are published for ARM64 (Raspberry Pi 4 and 5, Apple silicon Linux VMs, ARM servers). AVX does not exist on ARM, so on those machines the two builds differ **only** in OpenMP: pick `noavx` if `libgomp1` is missing, `cpu` otherwise.

:::warning[ROCm users: re-download your binary]
ROCm binaries published before 4.8.0.2 were built for the CI machine's own CPU, which emitted AVX-512. They crash with an illegal instruction on any CPU without it, which includes every AMD Zen 1 to 3 and every Intel consumer chip from 12th gen onward.

Updating the plugin does not replace a binary you already downloaded: the setup check only looks for a file on disk, it does not record which release produced it. If you use the `rocm` variant, open **Whisper Engine** on the settings page and download it again after updating.
:::

### No ROCm compatibility build

There is no `rocm-noavx` build. If your CPU lacks AVX2 and your GPU is AMD, there is no GPU variant for you: the plugin detects this and falls back to `noavx`, so transcription runs on the CPU.

## Check what your CPU supports

Run this on the Jellyfin host. Inside a Docker container it works too, because `/proc/cpuinfo` reports the host CPU either way.

```bash
grep -m1 '^flags' /proc/cpuinfo | tr ' ' '\n' | grep -x -E 'sse4_2|avx|avx2|fma|f16c|bmi2'
```

It prints only the flags that matter here. Three real examples:

| CPU | Output | Verdict |
|---|---|---|
| Core i5-14500 | `sse4_2 avx avx2 fma f16c bmi2` | full AVX2 set, use the standard builds |
| Xeon E5-2687W v2 | `sse4_2 avx f16c` | AVX but no AVX2, use a `-noavx` build |
| Celeron J4125 | `sse4_2` | SSE4.2 only, use a `-noavx` build |

If you only want the yes/no answer:

```bash
grep -qw avx2 /proc/cpuinfo && echo "AVX2: yes" || echo "AVX2: no"
```

:::danger `avx` is not `avx2`
The Xeon row above is the trap. That CPU advertises `avx`, so a loose check reports success, and then the AVX2 binary crashes anyway. Match the exact flag `avx2`. Nothing else answers the question.
:::

## Pick your variant

| Your CPU has AVX2 | Your GPU | Choose |
|---|---|---|
| yes | NVIDIA with CUDA 12 | `cuda12` |
| no | NVIDIA with CUDA 12 | `cuda12-noavx` |
| yes | Intel or AMD with Vulkan | `vulkan` |
| no | Intel or AMD with Vulkan | `vulkan-noavx` |
| yes | AMD with ROCm | `rocm` |
| no | AMD with ROCm | `noavx` (CPU only) |
| yes | none usable | `cpu` |
| no | none usable | `noavx` |

A GPU variant needs both the device **and** the userspace library. For Vulkan, `/dev/dri/renderD128` on its own is not enough. You also need `libvulkan.so.1` and a working driver ICD. Containers commonly have the device passed through and the library missing.

Synology DSM is the clearest example: it exposes `/dev/dri` for hardware transcoding but ships no Mesa Vulkan ICD and no `libvulkan.so.1`, so no Vulkan variant will initialise there no matter what the CPU supports.

## Troubleshooting by symptom

### Exit code 132, or "Illegal instruction"

The log line looks like this:

```text
System.InvalidOperationException: Whisper process failed with exit code 132.
```

132 is 128 + 4, the code for a process killed by SIGILL. The CPU hit a machine instruction it does not implement. This is always a variant mismatch. It is never a model, path or permission problem, so do not waste time there.

Switch to the compatibility build that keeps your GPU:

| Currently on | Switch to |
|---|---|
| `cpu` | `noavx` |
| `vulkan` | `vulkan-noavx` |
| `cuda12` | `cuda12-noavx` |
| `rocm` | `noavx` |

Exit codes 134 (SIGABRT) and 135 (SIGBUS) mean the same thing in this context and take the same fix.

To confirm it from the host:

```bash
dmesg | grep -i 'trap invalid opcode' | tail -5
```

A line naming `whisper-cli` itself, rather than a `.so`, proves the bad instruction is compiled into the binary.

### "Could not detect language" right after the Vulkan or CUDA banner

The log shows the GPU being found, then the job fails:

```text
ggml_vulkan: Found 1 Vulkan devices:
ggml_vulkan: 0 = Intel(R) Arc(tm) A380 Graphics (DG2) ...
Could not detect language. Ensure your whisper.cpp build supports --detect-language
```

The GPU initialised, printed its banner, and the process then died on an illegal instruction. The message is misleading. Nothing is wrong with `--detect-language`.

Language detection runs **on the CPU on purpose**. It spawns one short process per audio chunk, and GPU initialisation costs more than the detection work itself, so the plugin disables the GPU for that pass. The result is that a GPU build still executes its AVX2 CPU code path here, even when GPU transcription would be fine.

Fix: `vulkan-noavx` or `cuda12-noavx`. You keep GPU transcription and get an AVX2-free CPU path for detection.

### Why a GPU variant never rescues a CPU without AVX2

This surprises people, so it is worth stating directly. Two independent reasons:

1. **The GPU builds carry the same CPU baseline.** `vulkan` and `cuda12` are compiled with exactly the AVX2, FMA, F16C and BMI2 flags that `cpu` is. The GPU backend changes which kernels run the matrix maths; it does not remove the surrounding CPU code, and some of that code runs before the GPU backend is initialised at all.
2. **Parts of the pipeline are CPU-only by design.** Language detection is the one that bites here.

Adding a faster GPU changes neither. The only fix is a binary compiled without those instructions.

### "error while loading shared libraries: libgomp.so.1" (exit 127)

Either install the library or move to a variant that does not need it:

```bash
apt install libgomp1
```

Inside a container that does not survive a restart unless you bake it into the image or the entrypoint. Switching to the matching `-noavx` build is the more durable fix.

### The job fails with no clear message

Find the whisper exit code in the Jellyfin log and read it as a signal: 127 is a missing shared library, 132/134/135 is an illegal instruction. Both are covered above.

## Building whisper-cli yourself

If you compile [whisper.cpp](https://github.com/ggml-org/whisper.cpp) for an old CPU, turning off AVX, AVX2, FMA and F16C is **not enough**. `GGML_BMI2` defaults to on, and Goldmont-class chips such as the Celeron J4125 have no BMI2 either. You get the identical exit 132, and it looks like your flags did nothing.

```bash
git clone --depth 1 --branch v1.8.4 https://github.com/ggml-org/whisper.cpp.git
cd whisper.cpp
cmake -B build \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=OFF \
  -DGGML_NATIVE=OFF \
  -DGGML_AVX=OFF -DGGML_AVX2=OFF -DGGML_FMA=OFF -DGGML_F16C=OFF -DGGML_BMI2=OFF \
  -DGGML_SSE42=ON \
  -DGGML_OPENMP=OFF
cmake --build build --config Release -j"$(nproc)"
```

Two of those flags carry the weight. `-DGGML_NATIVE=OFF` stops CMake compiling for the build machine's CPU. Leave it out and CMake ignores every instruction flag you set, then compiles for the build machine anyway. `-DBUILD_SHARED_LIBS=OFF` is mandatory. Without it the binary hunts for `libwhisper.so.1` at runtime and never starts. `-DGGML_OPENMP=OFF` is optional and just drops the `libgomp1` dependency.

Copy `build/bin/whisper-cli` to your WhisperSubs binary directory, or point **Whisper Binary Path** in Advanced settings at it.

## What the plugin does on its own

You usually do not need this page. The plugin already:

- **Reads your CPU flags.** It parses `/proc/cpuinfo` for an exact `avx2` token. ARM64 is treated as satisfied, since those builds use no AVX.
- **Recommends a variant.** NVIDIA GPU with CUDA libraries present gives `cuda12` or `cuda12-noavx`; an AMD GPU with the ROCm runtime gives `rocm`; a render device plus `libvulkan.so.1` gives `vulkan` or `vulkan-noavx`; otherwise `cpu` or `noavx`. The `-noavx` arm is chosen whenever AVX2 is absent.
- **Refuses an impossible download.** Asking for an AVX2 build on a CPU without AVX2 is rejected before the binary is even probed, because a `--help` check would pass and only crash later during real transcription.
- **Falls back automatically.** After downloading, it launches the binary to check it starts. On failure it walks a fallback chain: `cuda12` and `vulkan` fall back to `cpu`, then `cpu` to `noavx`; `cuda12-noavx`, `vulkan-noavx` and `rocm` fall back straight to `noavx`. A crash on launch (132, 134, 135) or a missing shared library triggers the walk.
- **Remembers what you installed.** The chosen variant is stored in the config, so revisiting the Setup page will not silently swap you back to a GPU-recommended build that crashes.

:::tip When not to intervene
If the Setup page hint is green and subtitles are being generated, leave the variant alone. Override the recommendation only when you know something the detection cannot see, such as a GPU that is present but unusable inside your container.
:::

One limit worth knowing: CPU detection is deliberately fail-open. On a non-Linux host, or if `/proc/cpuinfo` cannot be read, the plugin assumes AVX2 is available rather than steering you to a slower build on a guess. The download-time launch check is what catches a genuine mismatch.

## Prebuilt binaries are Linux only

The built-in downloader serves `linux-x64` (all seven variants) and `linux-arm64` (`cpu` and `noavx`). On Windows and macOS it returns nothing, so the Setup page shows **"No prebuilt binaries for this platform"** and disables the Download button.

That is expected, not a fault. On those platforms, install `whisper-cli` yourself and set **Whisper Binary Path** in Advanced settings.

- **macOS.** `brew install whisper-cpp` puts `whisper-cli` on your PATH. Or build from source with Metal enabled.
- **Windows.** Download a release from [whisper.cpp](https://github.com/ggml-org/whisper.cpp/releases) or build it yourself, then point **Whisper Binary Path** at your `whisper-cli.exe`.

Once the path points at a working binary, the plugin uses it and every other feature behaves normally. The Setup page keeps the download controls disabled and says so.
