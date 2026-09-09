---
title: Diagnostics and the status endpoint
description: How to read WhisperSubs' own diagnostic endpoints, including the API key you need to reach them.
sidebar_position: 1
---

# Diagnostics and the status endpoint

WhisperSubs exposes JSON endpoints that answer two questions without reading a single log line: is the transcription engine actually installed, and what is the queue doing right now. Every one of them is admin-only. Opening the URL in a browser tab returns **401 with a blank page**, because a browser tab sends no credentials. That is expected, not a bug.

## Read the status endpoint

### 1. Create an API key

In the Jellyfin dashboard, go to **API Keys**, click **New API Key**, give it an app name such as `diagnostics`, and copy the key.

### 2. Call the endpoint

```bash
JF_URL="https://your-jellyfin"
JF_KEY="paste-your-api-key-here"

curl -s -H "Authorization: MediaBrowser Token=$JF_KEY" \
  "$JF_URL/Plugins/WhisperSubs/Setup/Status"
```

The response is one long line of JSON. Add `| jq .` if you have `jq` installed.

If your server has no TLS, the URL is `http://your-jellyfin:8096` instead. Your API key then crosses the network in cleartext and anything on the path can read it, so only do that on a network you trust.

On Windows PowerShell, use `curl.exe`. Plain `curl` there is an alias for `Invoke-WebRequest`, which does not take `-H` the same way:

```text
$JF_URL = "https://your-jellyfin"
$JF_KEY = "paste-your-api-key-here"

curl.exe -H "Authorization: MediaBrowser Token=$JF_KEY" "$JF_URL/Plugins/WhisperSubs/Setup/Status"
```

### Which header to send

Two forms work, and they are not equally reliable:

| Header | Works |
|---|---|
| `Authorization: MediaBrowser Token=<key>` | Always. Quotes around the value are optional: `Token="<key>"` parses identically. |
| `X-Emby-Token: <key>` | Only while the server has legacy authorization enabled, which depends on your Jellyfin version and its configuration. Jellyfin 10.11, the version this plugin targets, has it on by default and an admin can switch it off. Jellyfin 12.0 turns it off by default, on new and upgraded servers alike. When it is off this header is ignored and you get 401. |

Use the `Authorization` form. It is the one the plugin's own settings page uses, and it does not depend on a server setting.

:::warning
Your API key grants full administrator access to your server. Never paste it into a GitHub issue, a screenshot, or a log excerpt. Keep it in a shell variable as shown above, and revoke it in **Dashboard → API Keys** when you are finished.
:::

## Why you got 401, 403 or 404

| Response | What it means | Fix |
|---|---|---|
| **401** with an empty body | No token on the request, or a token the server does not recognise. This is what a browser tab always gets. | Send the `Authorization` header above. If you already do, the key is wrong or was revoked. Create a fresh one. |
| **403 Forbidden** | The token is valid but belongs to a non-admin user. These endpoints require the administrator role. | Use an API key from **Dashboard → API Keys** (those always count as admin) or an admin account's own token. |
| **404 Not Found** | The URL did not match a route at all. Three causes, below. | |

A 401 is good news: it proves the plugin's routes are registered and only your credentials were missing.

**404 cause 1: the server has a base URL.** If **Dashboard → Networking → Base URL** is set to something like `/jellyfin`, every API path carries that prefix:

```bash
curl -s -H "Authorization: MediaBrowser Token=$JF_KEY" \
  "$JF_URL/jellyfin/Plugins/WhisperSubs/Setup/Status"
```

Reverse proxy setups and prebuilt NAS templates set this often, and it is the most common reason a copied command returns 404.

**404 cause 2: the path was anchored under `/web`.** Your address bar shows something like `https://your-jellyfin/web/#/dashboard`, so it is tempting to build the URL from there. The API does not live under `/web`. The path starts at the server root, and the full route is exactly:

```text
/Plugins/WhisperSubs/Setup/Status
```

**404 cause 3: the plugin is not loaded.** If WhisperSubs failed to load, none of its routes exist and every path under `/Plugins/WhisperSubs` returns 404 whether or not you send a key. Check that **Dashboard → Plugins** lists WhisperSubs as active, then search the server log for `WhisperSubs`.

## What the status response tells you

A healthy server, formatted for readability:

```json
{
  "BinaryFound": true,
  "BinaryFoundInPath": false,
  "BinaryPath": "/config/data/WhisperSubs/whisper/whisper-cli",
  "ModelFound": true,
  "ModelPath": "/config/data/WhisperSubs/whisper/models/ggml-large-v3-turbo-q5_0.bin",
  "Platform": "linux-x64",
  "SetupComplete": true,
  "InstalledVariant": "vulkan",
  "Gpu": {
    "HasNvidia": false,
    "HasCudaLibrary": false,
    "HasAmdGpu": false,
    "HasRocmLibrary": false,
    "HasRenderDevice": true,
    "HasVulkanLibrary": true,
    "HasOpenMP": true,
    "HasAvx": true,
    "RecommendedVariant": "vulkan"
  }
}
```

| Field | Meaning |
|---|---|
| `SetupComplete` | `true` only when both the binary and a model are present. This is the one field that says whether local transcription can run at all. |
| `BinaryFound` | The `whisper-cli` binary exists, either auto-downloaded by the plugin, or at the path you configured, or on the system `PATH`. |
| `BinaryFoundInPath` | `true` only when the binary was found on `PATH` and there is no auto-downloaded or configured copy. |
| `BinaryPath` | The path in use, or the literal `whisper-cli (PATH)`. |
| `ModelFound` | A model was located: the configured path if you set one, otherwise the largest `.bin` in the plugin's models directory. |
| `ModelPath` | The model file that will be used. |
| `Platform` | Detected platform identifier: `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`, `win-x64` or `win-x86`. Determines which binary builds are downloadable. |
| `InstalledVariant` | The configured binary variant id: `cpu`, `noavx`, `cuda12`, `cuda12-noavx`, `vulkan`, `vulkan-noavx` or `rocm`. Empty until a binary is installed. |
| `Gpu` | Hardware detection, below. |

Inside `Gpu`:

| Field | Meaning |
|---|---|
| `HasNvidia` | `/dev/nvidia0` or `/dev/nvidiactl` exists, or `nvidia-smi` ran successfully. |
| `HasCudaLibrary` | A CUDA userspace library (`libcuda.so.1` or `libcudart.so`) was found. |
| `HasAmdGpu` | `/dev/kfd` exists, or `rocm-smi` ran successfully. |
| `HasRocmLibrary` | `libamdhip64.so` was found. |
| `HasRenderDevice` | `/dev/dri/renderD128` exists. |
| `HasVulkanLibrary` | `libvulkan.so.1` was found. |
| `HasOpenMP` | `libgomp.so.1` was found. Informational: without it the plugin falls back to the self-contained `noavx` build. |
| `HasAvx` | The CPU has the AVX2-class instructions the prebuilt binaries need. Always `true` on arm64. When `false`, only the `*-noavx` variants will run; the others die with SIGILL (exit code 132). |
| `RecommendedVariant` | The variant the plugin would pick for this hardware. |

:::note
A GPU flag on its own is not enough. `RecommendedVariant` only names a GPU build when **both** the device and its userspace library are present, because a GPU binary cannot start without the library. That is why the pairs matter: `HasNvidia` with `HasCudaLibrary`, `HasAmdGpu` with `HasRocmLibrary`, `HasRenderDevice` with `HasVulkanLibrary`.
:::

A broken engine install looks like this:

```json
{
  "BinaryFound": false,
  "BinaryFoundInPath": false,
  "ModelFound": false,
  "Platform": "linux-x64",
  "SetupComplete": false,
  "InstalledVariant": "",
  "Gpu": {
    "HasNvidia": true,
    "HasCudaLibrary": false,
    "HasAmdGpu": false,
    "HasRocmLibrary": false,
    "HasRenderDevice": true,
    "HasVulkanLibrary": false,
    "HasOpenMP": true,
    "HasAvx": true,
    "RecommendedVariant": "cpu"
  }
}
```

Three things to read out of that:

- `BinaryPath` and `ModelPath` are **missing entirely**, not `null`. Jellyfin drops null fields from its JSON, so an absent field means "not found". Nothing is wrong with your copy of the response.
- `SetupComplete: false` with both `BinaryFound` and `ModelFound` false means the engine was never installed. Install it from the plugin's settings page, described in the [setup guide](/docs/setup).
- `HasNvidia: true` with `HasCudaLibrary: false` is the classic container case: the GPU device was passed through, but the CUDA runtime libraries are not in the image. The plugin therefore recommends `cpu`, because a `cuda12` binary would fail to start.

## Other endpoints worth capturing

Same authentication, same base path.

**Queue state**, for "it is stuck" or "nothing ever finishes":

```bash
curl -s -H "Authorization: MediaBrowser Token=$JF_KEY" \
  "$JF_URL/Plugins/WhisperSubs/Queue"
```

Field names here are camelCase, unlike the status endpoint. The useful ones are `isProcessing`, `currentItem`, `remaining`, `processed`, `failed`, `lastError`, `phase`, `pendingRequests`, and `workers` (which worker is transcribing what).

:::warning
The queue response names the media being processed (`currentItem`, `library`) and `lastError` can contain file paths. Redact anything private before pasting it into a public issue.
:::

**Client script injection**, for "the Generate Subtitles button is missing":

```bash
curl -s -H "Authorization: MediaBrowser Token=$JF_KEY" \
  "$JF_URL/Plugins/WhisperSubs/Setup/InjectionStatus"
```

Read `Level` and `Message` first: they carry the diagnosis and the remedy in plain text. `Mode` says which mechanism is active (`direct`, `file-transformation`, `direct+file-transformation` or `none`), `ScriptTagPresent` is what the on-disk `index.html` contains, and `ServedHtmlVerified` (`yes`, `no` or `unknown`) is a probe of the HTML the server actually serves, which is the ground truth when a serve-time transform is involved.

## Reading it from a browser

**The plugin's own settings page.** **Dashboard → Plugins → WhisperSubs** already calls the status endpoint and renders it. That is the fastest way to see whether the binary and model are installed. It does not give you the raw JSON a bug report wants.

**A query parameter, as a last resort.** The `ApiKey` query parameter works in the address bar, with no header needed. Use it only when you cannot send a header.

:::warning
A key in a URL leaks. It is written to your browser history, to the access log of every reverse proxy or gateway in front of Jellyfin, and to Jellyfin's own log, and it travels with the URL into any bug report or screenshot you paste it into. Revoke the key in **Dashboard → API Keys** as soon as you have the JSON.
:::

```text
https://your-jellyfin/Plugins/WhisperSubs/Setup/Status?ApiKey=your-api-key
```

The capitalisation matters: `ApiKey`, not `api_key`.

**The browser console, with no API key at all.** Sign in to Jellyfin as an admin, open the developer tools console on any Jellyfin page, and run:

```js
fetch(ApiClient.getUrl('Plugins/WhisperSubs/Setup/Status'), {
  headers: { Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
}).then(r => r.json()).then(j => console.log(JSON.stringify(j, null, 2)));
```

This reuses your existing session token, and `ApiClient.getUrl` builds the path with your server's base URL, so it also sidesteps the 404 case above. It prints formatted JSON you can copy straight out of the console.

## What to include when reporting a bug

The [bug report form](https://github.com/GeiserX/whisper-subs/issues/new?template=bug_report.yml) asks for:

- **Plugin version**, from **Dashboard → Plugins → WhisperSubs**.
- **Jellyfin version**.
- **Transcription engine**: local `whisper-cli` (CPU, CUDA, Vulkan or ROCm) or a remote API.
- **Whisper model** filename, for example `ggml-large-v3-turbo-q5_0.bin`.
- **Installation method**: Docker image, TrueNAS, Unraid, bare metal.
- **What happened**, expected versus actual.
- **`Setup/Status` output**, the JSON from the first command on this page.
- **`Queue` output**, if the problem involves generation running, stalling or failing.
- **Logs**: your Jellyfin log filtered for WhisperSubs, at least the last 50 relevant lines. For Docker, `docker logs jellyfin 2>&1 | grep -i whisper`.
- **Docker compose or run command**, if you use Docker, with GPU passthrough and volume mounts visible.

Before you paste anything: remove the API key, remove any provider keys or passwords from the compose file, and redact file paths you would rather not make public. If you cannot get the status endpoint to respond at all, say so and send the logs anyway. The logs matter more.
