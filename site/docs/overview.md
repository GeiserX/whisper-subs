---
title: WhisperSubs
description: Generate subtitles for your Jellyfin library with local AI. All processing stays on your server.
slug: /
sidebar_position: 1
sidebar_label: Overview
---

# WhisperSubs

Generate subtitles for your Jellyfin library using local speech-to-text. Audio never leaves your server unless you deliberately configure a remote worker or a hosted provider.

## Install

Add the plugin repository to Jellyfin under **Dashboard → Plugins → Repositories**:

```text
https://geiserx.github.io/whisper-subs/manifest.json
```

Then install **WhisperSubs** from the catalogue and restart Jellyfin.

The plugin needs a whisper engine binary and a model before it can transcribe anything. Both are downloaded from the plugin's own settings page.

## Where to go next

| If you want to | Read |
|---|---|
| Get the engine working | [Setup guide](/docs/setup) |
| Work out which binary your hardware needs | [Choosing a variant](/choosing-a-variant) |
| Run it on TrueNAS, Proxmox, Synology or unRAID | [Platform-specific installs](/install-topologies) |
| Diagnose a failure, or file a useful bug report | [Diagnostics](/diagnostics) |
| Spread transcription across machines | [Remote workers](/remote-workers) |
| Know what it cannot do | [Limitations](/limitations) |
