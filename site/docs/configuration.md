---
title: Settings reference
description: Every WhisperSubs setting, what it does, and its default.
sidebar_position: 5
---

# Settings reference

Everything below lives at **Dashboard → Plugins → WhisperSubs**, except the handful marked config file only. The sections follow the order of the settings page, so you can read down this page and down the screen at the same time.

Defaults are the values a fresh install starts with.

## Generation defaults

| Setting | Default | What it does |
|---|---|---|
| Default Language | Auto-detect from audio (`auto`) | Reads the language tag from each audio stream and transcribes in that language. With no tags, whisper detects the spoken language and the file is written with language `auto`. Picking a specific language forces every transcription to use it. |
| Audio languages (auto-detect) | All audio languages | Only applies when the language above is Auto-detect and the file carries more than one audio language. **All audio languages** transcribes every one of them, producing one subtitle per language. **Primary audio track only** transcribes just the first track. A specific language, or a file with no language tags, is one language already and is unaffected. |
| Subtitle Mode | Full | Which passes run. See below. |

### Subtitle modes

| Mode | What it produces |
|---|---|
| **Full** | A complete transcription of all speech. |
| **Forced Only** | Only the segments where the spoken language differs from the track's primary language, for example French dialogue inside an English film. |
| **Full + Forced** | Both files, per track. |
| **Translation Only (English)** | Skips native-language transcription entirely and produces only an English translation, in a single whisper pass. |

Translation Only needs a model that can translate. The recommended turbo models cannot, so pick a medium or large non-turbo model for it. [Limitations](/limitations#the-recommended-model-cannot-translate) explains why.

## Subtitle file naming

| Setting | Default | What it does |
|---|---|---|
| Subtitle label | `WhisperSubs` | The brand written into the plugin's own subtitle filenames through the `{label}` token. With the default template it lands right after the language code, which makes it the Title shown in Jellyfin's subtitle picker. It is also the ownership marker the plugin matches to recognise its own sidecar files, so pick something distinctive. It is matched as a substring. |
| Filename template | `{name}.{lang}.{label}{.type}` | The filename pattern, without the extension. |

### Template tokens

| Token | Expands to |
|---|---|
| `{name}` | The media filename without its extension. Treated as opaque, so dots inside a release name are never read as separators. |
| `{lang}` | The language code, for example `en`. |
| `{label}` | The subtitle label above. |
| `{.type}` | `.translated`, `.forced`, or nothing for a full transcription. |
| `{type}` | The same value without the leading dot. |

A template must contain `{name}`, `{lang}` and `{label}`. The page shows a warning under the field when one is missing, and an invalid template that got saved anyway falls back to the default at the moment it is used, so it can never produce a broken filename.

Empty segments are cleaned up after expansion: any `..` collapses to `.`, and a leading or trailing dot is trimmed. `Movie..srt` and `Movie.srt.` cannot happen.

The three preset buttons fill the field with:

```text
Brand first (recommended)   {name}.{lang}.{label}{.type}
Label at end                {name}.{lang}{.type}.{label}
Keep .generated             {name}.{lang}.{label}.generated
```

:::warning[A template with no type token collides]
The **Keep .generated** preset has no `{.type}`, so the type is simply dropped. Full, forced and translated output for the same language all expand to the same filename and overwrite each other. If you use a custom template, keep `{.type}` in it unless you run in Full mode only.
:::

Files written by older versions are still recognised. Anything containing `.generated.` or `.translated.` counts as the plugin's own output regardless of the current label, alongside the label-based `.WhisperSubs.` form.

## Automation

| Setting | Default | What it does |
|---|---|---|
| Enable Auto-Generation | Off | Lets the scheduled task sweep your libraries. With this off, subtitles are only ever produced by a manual Generate. |
| Libraries | none selected | The checkbox list under Auto-Generation scopes the sweep. **With none selected, every library is scanned.** Selecting one or more restricts the sweep to those. It applies to the scheduled task only; a manual Generate on a single item ignores it. |
| Enable Lyrics Generation (Experimental) | Off | Includes music libraries in the sweep and writes `.lrc` lyrics next to audio tracks. Whisper is trained on speech, not singing, so accuracy varies. See [Limitations](/limitations#lyrics-generation-is-experimental). |
| Pause generation during playback | Off | Pauses transcription while any user is playing something, and resumes when playback stops. Useful when the same box transcodes and transcribes. |
| Skip media that already has subtitles | On | The sweep skips media that already has a usable subtitle in the language it needs, embedded or external. For the translation pass, an existing English subtitle counts as already translated. |
| Ignore forced subtitles when skipping | On | A forced subtitle track does not count as satisfying the need. Forced tracks only cover foreign-dialogue inserts, not the whole dialogue. |

## Subtitle generation

Whisper transcribes the speech it hears, so it can only write a subtitle in the title's own audio language. Turning that audio into English is a separate pass, covered under Translation below.

| Setting | Default | What it does |
|---|---|---|
| Generate original-language subtitles | On | The main generate switch. Transcribes each title in its own spoken language: a Korean film gets Korean subtitles, an English film gets English. Turning it off leaves the sweep producing only whatever forced or translated output the mode calls for. A manual single-item Generate always transcribes regardless of this setting. |
| Count image-based subtitles as present | Off | When on, image-based tracks (PGS, VOBSUB) count as an existing subtitle. Off by default because image subtitles cannot be searched or edited, so the plugin still writes a text one. |

:::note[The two filters are independent]
"Ignore forced subtitles" and "Count image-based subtitles as present" are separate tests, applied one after the other to the same stream. A stream has to pass both to count as an existing subtitle. Neither one governs the other: a forced image track is rejected by the forced test whatever the image setting says, and turning the image setting on does not make forced tracks count.
:::

The plugin's own output is always excluded from these checks, so a subtitle it generated last week never satisfies the check this week and stops it regenerating a file it just wrote.

### Skip cache

Re-checking the filesystem for every candidate item on every scheduled run is the slow part of a large library. The cache stores the per-item verdict so a repeat run can skip the probe.

| Setting | Default | What it does |
|---|---|---|
| Remember already-subtitled items between runs | On | Caches the "already has subtitles" verdict per item. An entry is reused only while the item's Jellyfin change token is unchanged and the skip settings above are unchanged. Any metadata or subtitle change, or any edit to a skip setting, re-checks the item. Scheduled task only. |
| Skip-cache re-verify (days) | `30` | Backstop. Re-check a cached item after this many days even if nothing changed, so a subtitle you deleted outside Jellyfin is eventually regenerated. Deleting a file outside Jellyfin does not bump the change token until the next library scan, which is what this covers. `0` disables the time backstop and relies on the change token alone. |

The **Clear skip cache now** button next to the field empties the cache immediately, so the next scheduled run re-checks every item from scratch.


## Translation to English

English is the only language whisper can translate into.

| Setting | Default | What it does |
|---|---|---|
| Also create an English subtitle when a title has none | Off | For a title whose audio is not English and that has no English subtitle, additionally translate it. Titles with English audio, or an existing English subtitle, are skipped automatically, so this only fills a gap. Uses whisper's own `--translate`, no external service. |

This applies only when Subtitle Mode includes Full subtitles. It does nothing in Forced Only, and it is implicit in Translation Only.

## Subtitle timing

### Why these settings exist

whisper.cpp emits cues back to back. The start of one cue is the end of the previous one, with no gap, even across a silence where nobody is speaking. On screen that reads as a subtitle appearing during the pause before its line is actually spoken.

There are two independent corrections for it:

- **Native VAD** runs Silero voice activity detection inside whisper.cpp at transcription time, so a cue starts at real speech onset. This is the default and the better fix.
- **The forward snap** is the older energy-based fallback. It runs an extra FFmpeg `silencedetect` pass over the audio already extracted on this server and moves each cue start forward to the nearest detected speech onset. It only ever moves a cue later, never earlier.

Both apply to full and translated subtitles. Neither applies to forced subtitles or lyrics.

| Setting | Default | What it does |
|---|---|---|
| Use speech detection (VAD) | On | Passes `--vad` to whisper-cli with the Silero model. The model is about 885 KB and downloads automatically on first use. Local whisper-cli only: it does not apply to a remote worker, which owns its own timing, nor to forced subtitles. |
| Align subtitles to speech (fallback) | On | Enables the FFmpeg forward snap. On its own it runs only when VAD is off. Leave it on so the fallback exists. |
| Also align VAD / worker timestamps to speech | Off | Layers the forward snap on top of VAD output and on top of remote worker output. Turn this on if lines still appear early. Requires "Align subtitles to speech" to be on. Off by default because the energy-based detector proved unreliable on some real material and can push a few starts slightly late. |
| Compensate audio start offset | On | Shifts every timestamp by the audio stream's start time, for containers whose audio does not begin at exactly 0:00. Applies to local and to timestamped remote output, for full and translated subtitles, not forced. |

### When the forward snap actually runs

| Timestamps came from | Align subtitles to speech | Also align VAD / worker timestamps | Forward snap runs |
|---|---|---|---|
| Local whisper-cli, VAD off | On | either | Yes |
| Local whisper-cli, VAD on | On | Off | No |
| Local whisper-cli, VAD on | On | On | Yes |
| Remote worker or hosted provider | On | Off | No |
| Remote worker or hosted provider | On | On | Yes |
| anything | Off | either | No |

### VAD tuning (advanced)

Each of these maps to one `--vad-*` flag on whisper-cli, and each applies only when **Use speech detection (VAD)** is on.

:::note[An empty field means "use whisper's own default"]
Leave a tuning field empty and the plugin stores `-1`, which it treats as unset. No flag is emitted for it and whisper.cpp uses its built-in value. That is why the rows below quote a whisper default rather than a plugin one: on a fresh install the plugin sets none of them, and the command line it builds is identical to one with no tuning feature at all.

**Max Speech Duration** additionally treats `0` as unset, because a zero second cap has no meaning.
:::

| Setting | Default | What it does |
|---|---|---|
| Silero VAD model | `v5.1.2` | Which Silero model whisper-cli loads. `v5.1.2` is the default and `v6.2.0` is a newer opt-in. Both are about 885 KB. An unset or unrecognised value falls back to `v5.1.2`, so upgrading never silently changes the model or shifts your timing. Selecting the other version downloads it on the next run. |
| VAD Threshold | unset (whisper uses `0.5`) | Speech probability cutoff, `0` to `1`. Lower catches quieter speech and adds false positives. |
| Min Speech Duration (ms) | unset (whisper uses `250`) | Segments shorter than this are dropped. |
| Min Silence Duration (ms) | unset (whisper uses `100`) | How much silence has to sit between two speech segments before they are split. Raising it groups sentences together. |
| Max Speech Duration (s) | unset (whisper leaves it unlimited) | Segments longer than this are split automatically. Around `30` limits timestamp drift on long unbroken takes. |
| Speech Padding (ms) | unset (whisper uses `30`) | Padding added to each side of a speech segment so word edges are not clipped. |
| Samples Overlap (s) | unset (whisper uses `0.1`) | Audio carried over between consecutive segments so a word on the boundary is not cut. |

A matching flag placed in **Custom Whisper Arguments** supersedes the value set here. Custom arguments are appended last and whisper-cli takes the last value it sees.

The next two sections on the page, **Remote Whisper API** and **Worker Pool**, are covered in [Remote workers](/remote-workers).

## Performance and advanced

These sit under **Advanced / Manual Install** on the settings page.

| Setting | Default | What it does |
|---|---|---|
| Whisper Thread Count | `0` | CPU threads for whisper inference. `0` emits no `-t` flag, so whisper.cpp uses its own default of 4. Set it to your core count. On a 16-thread i5-14500 a 2h15m film drops from an estimated 7 hours to 1h48m. Language detection is separately capped at 4 threads whatever you set here, because detection is a trivial workload that gains nothing from more parallelism and would only cause CPU spikes. |
| Maximum subtitle line length | `0` | Maximum characters per cue, emitted as `--max-len N` together with `--split-on-word` so a cap never breaks mid-word. `0` is unset, which leaves whisper.cpp's own default of unlimited. Raise it if subtitles arrive as one enormous run-on line: broadcast subtitling caps a line near 42 characters and `47` is a good starting point. Local whisper-cli only, since a remote worker owns its own segmentation. Set `WHISPER_MAX_LEN` on the worker instead. |
| Custom Whisper Arguments | empty | Extra space-separated arguments appended to every whisper-cli invocation, for example `--beam-size 8`. Local whisper-cli only. |

Arguments the plugin manages itself are blocked from that field: the model, input file, language, thread count, max context, non-speech suppression, every output format and the output path, `--translate`, `--detect-language`, the no-timestamps flags, `--prompt`, the offset and duration flags, and `--vad` with `--vad-model`. The VAD tuning flags and `--max-len` are deliberately **not** blocked, so you can override those settings from here.

## Config file only

These have no control on the settings page. They live in Jellyfin's plugin configuration file, at `<Jellyfin program data>/plugins/configurations/WhisperSubs.xml`:

| Deployment | Path |
|---|---|
| `jellyfin/jellyfin` container, and the NAS app images built on it | `/config/plugins/configurations/WhisperSubs.xml` |
| Debian or Ubuntu package on bare metal or in an LXC | `/var/lib/jellyfin/plugins/configurations/WhisperSubs.xml` |

:::warning[Stop Jellyfin before editing it]
Jellyfin holds the plugin configuration in memory and writes the whole file back whenever you press Save on the settings page. Editing the file while Jellyfin is running either has no effect or gets overwritten. Stop Jellyfin, edit, start it again.
:::

### Remote call deadlines

Every call to a remote worker is bounded by a deadline derived from the length of the audio, rather than by a fixed HTTP timeout. The deadline is the audio length multiplied by the real-time factor, then clamped between the floor and the cap. A slow but working worker is never cut off; a dead endpoint fails instead of piling up.

| Setting | Default | What it does |
|---|---|---|
| `JobTimeoutRealtimeFactor` | `6.0` | How much slower than real time a worker may run before a single call is presumed hung. |
| `JobMinTimeoutSeconds` | `60` | Floor for the per-call deadline, so a tiny language-detection chunk still gets a sane minimum. |
| `JobMaxTimeoutHours` | `12` | Absolute cap for the per-call deadline. |

### Retries and sweep length

| Setting | Default | What it does |
|---|---|---|
| `JobMaxRetries` | `3` | How many times a killed or failed job is automatically re-queued before the plugin gives up. It goes back at its original priority tier and the counter survives a Jellyfin restart. `0` disables auto-retry, dropping a killed job outright. |
| `TaskMaxRuntimeHours` | `6` | Wall-clock cap on one sweep of the Generate Subtitles task. At the cap the sweep stops cleanly between items: finished work is kept, the skip cache is written, and the next scheduled run picks up where this one stopped. `0` means unlimited. |

### Other config file values

| Setting | Default | What it does |
|---|---|---|
| `LanguageDetectionSampleSeconds` | `30` | Seconds of audio, from the start of each chunk, sent for language detection only. Detection needs only a short window, so bounding it stops a long or noisy chunk turning into a runaway decode. It does not affect subtitle quality: a chunk confirmed as foreign is still transcribed in full. `0` sends the whole chunk. `30` matches whisper's own language-detection window. |
| `VadModelPath` | empty | An explicit path to a Silero VAD ggml file. The plugin also writes this automatically when it downloads a model, and a path it wrote inside its own managed `vad/` directory is ignored in favour of the **Silero VAD model** selection. A path pointing anywhere else is treated as a deliberate external override and wins over that selection. |

## Subtitle requests and priority

Off by default. When **Allow users to request subtitles** is on, non-admin users get a **Request Subtitles** entry on the item page. Requests land as Pending and consume no CPU until an admin approves them, unless auto-approve is also on.

Work is drained strongest tier first, and FIFO within a tier. Lower tiers never starve a higher one.

| Setting | Default | What it does |
|---|---|---|
| Allow users to request subtitles | off | Master switch. While off, only admins can queue work. |
| Auto-approve user requests (skip my approval) | off | Enqueue a request immediately instead of holding it for an admin. |
| Admin request priority | High | Tier for work an admin queues by hand. |
| User request priority | Medium | Tier for an approved user request. Below admin work, above the sweep. |
| Background sweep priority | Background | Tier for the scheduled library sweep. The weakest tier. |
| Requests per user per window | 5 | Requests one user may make per rolling window. `0` means unlimited. |
| Rolling window (hours) | 24 | The rolling window the daily quota is measured over. |
| Max active requests per user | 3 | Requests one user may have in flight at once. `0` means unlimited. |
| Max items per request | 200 | Ceiling on the fan-out when a user requests a season or a series. The settings page enforces a minimum of 1 and rewrites anything lower back to 200. |
| Global active request cap | 500 | Ceiling on pending and queued requests across all users. `0` means unlimited. |

The last five sit behind the collapsed **User request limits (anti-abuse)** disclosure on the settings page; expand it to find them.

Priorities are always assigned server-side from who made the request. A client cannot ask for one.

## Settings documented elsewhere

The remaining settings belong to features with their own pages.

| Settings | Where |
|---|---|
| Whisper Binary Path, Whisper Model Path, and the recorded binary variant | [Setup guide](/docs/setup) |
| Vocal separation: the master switch, binary and model paths, the recorded variant and quantization, overlap and chunk size | [Vocal separation](/docs/setup#vocal-separation) |
| Remote Whisper API URL, model and key, the worker pool list, and whether the local host participates as a worker | [Remote workers](/remote-workers) |
