---
title: API and internals
description: The REST endpoints, how a subtitle gets made, and the files the plugin writes.
sidebar_position: 7
---

# API and internals

Everything the settings page does, it does over HTTP. This page lists those endpoints, walks the transcription pipeline from trigger to file on disk, and names every file the plugin owns.

## The REST API {#rest-api}

All 36 endpoints live under `/Plugins/WhisperSubs`. Two controllers share that prefix: 32 admin endpoints and 4 user-facing ones.

Every admin endpoint needs an administrator token. [Diagnostics](/diagnostics) covers creating an API key and which header to send. A browser tab sends no credentials, so opening any of these URLs directly returns 401.

### Admin endpoints {#admin-endpoints}

The whole controller carries `[Authorize(Policy = "RequiresElevation")]`, so **admin only**, with no exceptions. Most of the `Setup/*` methods repeat the attribute individually; that is redundant, not different.

**Libraries and items**

| Method | Path | Returns |
|---|---|---|
| GET | `Libraries` | Every Jellyfin library, as id and name. |
| GET | `Libraries/{libraryId}/Items` | A page of media items: id, name, type, path, and whether Jellyfin already sees subtitles. Query: `startIndex` (0), `limit` (50), `searchTerm`. |
| GET | `Items/{itemId}/AudioLanguages` | The ISO 639-1 codes tagged on the file's audio streams, read with FFprobe. |
| GET | `Items/{itemId}/Status` | Whether a plugin-generated full or forced subtitle exists for the item, plus the path. Query: `language`, defaulting to the configured one. `auto` checks any language. |
| POST | `Items/{itemId}/Generate` | 202. Queues one item at the admin tier, forced, so it regenerates even when a usable subtitle already exists. Query: `language`. |
| POST | `Items/{itemId}/GenerateAll` | 202. Expands a series, season or album to its media items and queues each one. Unlike `Generate` this does not force, so it fills gaps rather than redoing work. Query: `language`. |

`GenerateAll` only accepts a movie, episode, audio track, series, season or album. A library root, a collection or a plain folder is rejected with 400, so a single call cannot sweep the whole library.

**Queue and workers**

| Method | Path | Returns |
|---|---|---|
| GET | `Queue` | The live queue: what is processing, what is left, counts of processed and failed, the last error, the current phase, a per-tier breakdown, how many user requests await approval, the next items in run order, and which worker holds which job. |
| POST | `Workers/TestConnection` | `{ok, message}`. Posts a short silent WAV to the worker's transcription route to prove reachability, auth and a working transcribe path. Never touches your library. Takes a worker definition as the JSON body. |
| POST | `Workers/Reload` | Reconciles the running worker pool with the saved configuration and returns the resulting worker count. A just-added worker joins the current drain without a Jellyfin restart. |
| POST | `RunTask` | Queues the Generate Subtitles scheduled task immediately. |

**User requests**

| Method | Path | Returns |
|---|---|---|
| GET | `Requests` | Every user request, newest first: item name, type, language, requester, tier, state, timestamps. Never a filesystem path. |
| POST | `Requests/{requestId}/Approve` | Moves the request from Pending to Queued and enqueues its items. 409 if it is not Pending, 404 if it does not exist. |
| POST | `Requests/{requestId}/Decline` | Moves the request from Pending to Declined. Same 409 and 404 behaviour. |

**Setup: the whisper engine**

| Method | Path | Returns |
|---|---|---|
| GET | `Setup/Status` | Whether the binary and model are configured and present, with their paths, the detected platform and GPU. |
| GET | `Setup/Progress` | Progress of the running download, if any. |
| GET | `Setup/AvailableModels` | The whisper model catalogue offered for download. |
| GET | `Setup/BinaryVariants` | The `whisper-cli` variants published for this platform. |
| GET | `Models` | The `.bin` models already on disk, with size and which one is active. |
| POST | `Setup/DownloadModel` | 202, download runs in the background. 409 if one is already running. Query: `name`, a catalogue filename. |
| POST | `Setup/DownloadBinary` | 202. Query: `variant`, defaulting to `cpu`. 400 for a variant not published for the platform. |
| POST | `Setup/DownloadVadModel` | 202. Fetches the Silero VAD model, roughly 865 KB. |
| POST | `Setup/Models/{filename}/Activate` | Points the configuration at that model file and saves. |
| DELETE | `Setup/Models/{filename}` | Deletes a downloaded model. Refuses the active model and the last remaining one. |
| POST | `Setup/ClearSkipCache` | Deletes the skip cache so the next scheduled run re-checks every item. Returns `{cleared}`. |
| GET | `Setup/InjectionStatus` | Whether the in-page client script is present in `index.html`, including a check of the HTML Jellyfin actually serves. |
| POST | `Setup/ReinjectScript` | Re-runs the script injection and returns the fresh status. Fixes a missing Generate Subtitles button without a restart. |

**Setup: vocal separation**

These mirror the whisper endpoints for the BSRoformer.cpp binary and model. They use a separate download lock, so a download here never collides with a whisper download.

| Method | Path | Returns |
|---|---|---|
| GET | `Setup/VocalSeparation/Status` | Whether `bs_roformer-cli` and a GGUF model are configured and present. |
| GET | `Setup/VocalSeparation/Progress` | Progress of the running vocal-separation download. |
| GET | `Setup/VocalSeparation/AvailableModels` | The GGUF quantizations offered for download. |
| GET | `Setup/VocalSeparation/BinaryVariants` | The BSRoformer.cpp variants published for this platform. |
| POST | `Setup/VocalSeparation/DownloadBinary` | 202. Query: `variant`, defaulting to `cpu`. |
| POST | `Setup/VocalSeparation/DownloadModel` | 202. Query: `quant`, a catalogue key such as `q8_0`. |

### User endpoints {#user-endpoints}

Four endpoints let a signed-in user ask for subtitles instead of waiting for an admin. They live in their own controller with a plain `[Authorize]`, so **any authenticated user** can reach them. That is deliberate: opening one method on the admin controller would have meant dropping its class-level elevation, and an un-attributed method in Jellyfin becomes public.

The gates are not the same on all four.

`Requests/Capabilities` answers for any authenticated user whether the feature is on, because the client script needs that answer either way to decide whether to show the request entry at all. It returns 200 with `enabled: false` when requests are switched off.

The other three apply two gates:

- The feature must be on. With **Allow users to request subtitles** off, they behave as if they do not exist and return 404.
- An API key is not enough. They re-derive the user from the session and reject key-only calls with 401, because a key carries no per-user visibility.

One further gate applies only to `Items/{itemId}/Request`: the item must be visible to that user. An item they cannot see returns 404, never 403, so the endpoint cannot be used to probe what exists.

| Method | Path | Returns |
|---|---|---|
| GET | `Requests/Capabilities` | Whether requests are enabled, whether they auto-approve, the user tier, and the daily and active limits. Drives the client script showing or hiding the request entry. |
| POST | `Items/{itemId}/Request` | 202 when the request is created, 200 when it duplicates an active one. 429 over the daily quota or the per-user active cap, 503 when the global queue cap is full, 400 for an unsupported item type or language. Query: `language`. |
| GET | `Requests/Mine` | That user's own requests only. Never anyone else's, never a file path. |
| GET | `Items/{itemId}/RequestStatus` | That user's active request state for one item, which is what the item-page badge reads. |

The tier of a user request is assigned on the server from the requester's role. It is never read from the request body.

## How a subtitle gets made {#pipeline}

Three things start a job: the nightly scheduled task, a manual **Generate** from the item page or the API, and an approved user request. All three put the item on the same queue, and a background drain hands jobs to whichever worker is free. The queue survives a restart.

Once a job starts, this is what happens.

**1. Work out the language.** With a specific language configured, that is the language. With `auto`, FFprobe reads the language tags off the audio streams and every distinct code found gets its own pass. When the file carries no tags at all, the plugin extracts the first 30 seconds and lets whisper detect the language.

**2. Decide which passes apply.** A run can produce up to three kinds of subtitle: the full transcription in the audio's own language, a forced subtitle holding only the foreign-language dialogue, and an English translation. Which ones run comes from the subtitle mode and the translation and original-language toggles. A pass that would overwrite a usable existing subtitle is skipped, unless the job was forced by a manual Generate.

**3. Resume or start fresh.** If the plugin's own partial subtitle for that language is already on disk, the run resumes from two seconds before its last cue and appends, renumbering as it goes. If that last cue is within 30 seconds of the end of the media, the file counts as finished and the pass is skipped.

**4. Extract the audio.** FFmpeg writes 16 kHz mono PCM to the system temp directory, mapping the audio stream that matches the target language. Jellyfin's own bundled FFmpeg at `/usr/lib/jellyfin-ffmpeg/ffmpeg` is preferred over one on the PATH.

**5. Separate the vocals, if enabled.** With vocal separation on and configured, the extraction runs at 44.1 kHz instead, `bs_roformer-cli` isolates the vocal track, and the result is downsampled to the 16 kHz whisper expects. Only one separation runs at a time across the whole server. If the binary or model is missing, or the process fails, the job continues on the original mix rather than failing. See [Vocal separation](/docs/setup#vocal-separation).

**6. Transcribe.** The assigned worker transcribes the WAV: local `whisper-cli`, a remote worker or a hosted API. The forced pass works differently. It splits the audio on silence, runs language detection on each speech chunk, and transcribes only the chunks whose language is not the primary one.

**7. Correct the timings.** Optional speech alignment nudges each cue start onto a detected speech onset. Optional offset compensation then shifts the whole file by the audio stream's container start time, which matters on broadcast and transport-stream recordings. Alignment runs first, while both the cues and the detected speech are still on the extracted WAV's zero-based clock, so the offset is applied exactly once.

**8. Save the file.** The SRT is written with a temporary file and a rename, so a crash mid-write cannot leave a truncated subtitle. It lands next to the media when the library has **Save subtitles into media folders** on and that folder is writable. Otherwise it goes to the item's Jellyfin metadata folder, which Jellyfin scans for external subtitles just the same. That fallback is what makes read-only libraries work.

**9. Refresh the item.** The plugin calls `RefreshMetadata` on the item so the new track appears without a library scan.

If every pass in a job failed, the job is reported as failed rather than quietly succeeding.

## Files the plugin writes {#files}

### Subtitles, next to your media

Filenames come from a template, `{name}.{lang}.{label}{.type}` by default, with `WhisperSubs` as the label. The label doubles as the ownership marker and as the title Jellyfin shows in the subtitle picker. Change either in Advanced settings and new files follow the new pattern.

| File | Written by |
|---|---|
| `Movie.es.WhisperSubs.srt` | The full transcription pass. |
| `Movie.es.WhisperSubs.forced.srt` | The forced pass, holding only foreign-language dialogue. |
| `Movie.en.WhisperSubs.translated.srt` | The English translation pass. |
| `Track.lrc` | Lyrics for an audio track, named to match the file, which is what Jellyfin's lyric resolver expects. |
| `Movie.es.forced.noforeignlang` | An empty marker file. |

Subtitles written before v4 used `.generated.` and `.translated.` in place of the label. Those are still recognised as the plugin's own, so an upgraded install resumes and skips them correctly rather than writing a second copy.

The `.noforeignlang` marker is worth understanding. When the forced pass analyses a file and finds no foreign dialogue anywhere in it, that is an expensive answer to compute and an empty subtitle would show up as a broken track in the player. So the plugin writes this empty marker instead, and every later run sees it and skips the analysis. Delete it to force a re-analysis. Note that its name is built directly from the media filename, so it stays `Movie.es.forced.noforeignlang` whatever you set the label and template to.

All of these follow the same save-location rule as the subtitles: media folder when it is writable and the library allows it, the item's metadata folder otherwise.

### State, in the plugin data folder

Three JSON files live in the plugin's data folder, which is `/config/data/WhisperSubs` on a standard Docker install. All three are written with a temporary file and a rename.

| File | Holds |
|---|---|
| `queue.json` | The pending queue, with each item's language, priority tier, forced flag and retry count. Restored on startup, so a restart does not lose queued work. |
| `requests.json` | User subtitle requests and their states, which is what the approval panel reads. |
| `skip-cache.json` | Per-item record of what was already checked, so a repeat scan does not re-probe the filesystem for every item. Clear it from the settings page or `POST Setup/ClearSkipCache`. |

### The engine, in the plugin data folder

| Path | Holds |
|---|---|
| `whisper/whisper-cli` | The transcription binary. `whisper-cli.exe` on Windows. |
| `whisper/models/` | Downloaded whisper models, as `.bin` files. The active one is set in the configuration. |
| `whisper/vad/` | The Silero VAD model, `ggml-silero-v5.1.2.bin`. Kept in its own directory so it is never mistaken for a transcription model. |
| `whisper/detect/` | The dedicated language-detection model, `ggml-base.bin`. Separate for the same reason. |
| `vocal-separation/bin/` | The extracted `bs_roformer-cli` and any libraries shipped alongside it. Replaced wholesale on every download on every download, so a stale library from a previous variant cannot linger. |
| `vocal-separation/models/` | The downloaded GGUF vocal-separation model. |

Working files go to the system temp directory and are deleted when the job ends, including on failure and cancellation. Nothing permanent is kept there.

## The scheduled task {#scheduled-task}

The task is called **Generate Subtitles** and appears under **Dashboard → Scheduled Tasks** in the WhisperSubs category. It scans the enabled libraries and queues anything missing subtitles.

It ships with two triggers: daily at 02:00, and once on server startup. Both are defaults. Change them, add triggers or remove them entirely from the same Scheduled Tasks page, exactly as you would for any built-in Jellyfin task.

Two things stop it early: **Enable automatic generation** turned off, and neither a model path nor a remote worker URL configured. Both are written to the log.

`POST RunTask` queues the same task on demand, which is what the settings page button does.
