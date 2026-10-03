# Design: Qwen3-ASR as a second transcription engine

Status: implemented (2026-10-03), shipped in 4.11.0.0. Measurements below come from the spike on watchtower (Intel i9 class CPU, 20 threads, Intel UHD 770) with CrispASR v0.8.35, the release the plugin pins.

## The problem

WhisperSubs transcribes with whisper.cpp and nothing else. Qwen3-ASR-1.7B scores lower word error rates than Whisper large-v3 on English and most European languages in published benchmarks, and the owner's own measurements on akou put it ahead of Parakeet on English and level on Spanish. People who want that accuracy for their library had no way to get it from this plugin.

## What exists already

The plugin manages a [CrispASR](https://github.com/CrispStrobe/CrispASR) binary for the Canary translation targets ([design](crispasr-translation-engine.md)). CrispASR runs Qwen3-ASR through the same command line contract, selected with `--backend qwen3`, and `cstr/qwen3-asr-1.7b-GGUF` on Hugging Face carries the weights. So the engine costs one more model download, no second binary, and the provider is a sibling of `CanaryProvider`.

## Scope

In scope:

- A `TranscriptionEngine` setting, `whisper` (default) or `qwen3`, in the Whisper Engine section of the settings page.
- Whole-title transcription on this server through Qwen3-ASR while the engine is selected and the crispasr binary and the Qwen3 model exist. Read on every job, so the switch needs no pool rebuild.
- A Qwen3 model panel (Q8_0 recommended, Q4_K smaller), pinned revision and SHA-256 like Canary, with the inference probe after download.
- The Canary CTC aligner as a second managed download in that panel, for word-accurate cue timing in the 25 languages it knows.

Out of scope, and why:

- Language detection on Qwen3-ASR. The model identifies languages natively, but crispasr v0.8.35 does not use that when `-l` is absent: it downloads Whisper tiny for identification and, when that download fails, silently defaults to English. The plugin's Whisper detection model answers instead and Qwen3-ASR always gets a code.
- Translation. Qwen3-ASR has no translate task. English stays on Whisper, the Canary targets on Canary.
- Qwen3-ASR on remote workers. A CrispASR server with `--backend qwen3` already speaks the OpenAI transcription route and can be added as a worker row; nothing in the plugin needed to change for that.
- Word timestamps from the model itself; it has none. The managed aligner covers it, see the timing section.

## Architecture

`EngineSwitchProvider` wraps the local `WhisperProvider`. A plain transcription (no translate flag, no target) goes to a `Qwen3Provider` built from the live configuration when `SubtitleProviderFactory.IsQwen3Active` says so; everything else, and every call while the files are missing, goes to Whisper. The forced-subtitle path passes its already-segmented chunks with `applyVad: false`, which both engines honour.

The command line, unit-tested as an exact vector:

```
crispasr --backend qwen3 -m <qwen3.gguf> -f <wav> -l <code> [-t N] --cache-dir <data>/crispasr/cache
         --vad --vad-model <silero> [--vad-* tuning] [-am <aligner.gguf> --force-aligner --split-on-punct]
         --max-len <N> --split-on-word --print-progress -osrt -of <prefix>
```

- `--backend qwen3` recognises the 1.7B weights from the file; `qwen3-1.7b` is only a name for `-m auto`.
- `-l` is always present, for the reason above, and only one of the model's 30 languages is ever passed; a title in another language goes to Whisper.
- `--vad`: without it the whole file is one cue. Same Silero model and tuning as whisper-cli.
- `--max-len`: unset means 42, doubled for two-byte scripts and tripled for CJK, because crispasr counts bytes.
- The progress line is `crispasr: progress =  10% (1/10 slices)`, which the existing regex already matches.

## Measurements

A 180 s English clip from a dual-audio episode, 8 threads unless noted, VAD on. Wall time includes model load.

| Build | Model | Wall | Speed |
|---|---|---|---|
| CPU | Q4_K | 36 s | 5.0x realtime |
| CPU | Q8_0 | 54 s | 3.3x realtime |
| Vulkan (UHD 770) | Q4_K | 49 s | 3.7x realtime |
| Vulkan (UHD 770) | Q8_0 | 65 s | 2.8x realtime |
| CPU, 16 threads | Q8_0 | 36 s | 5.0x realtime |

The CPU build is the right default on an integrated GPU, as it was for Canary. The Vulkan and CPU transcripts of the same quantization were identical.

Text quality against the human SDH subtitle on that clip: every line present, proper nouns the usual casualties ("Richie Five" for "Richie Fife"), Q8_0 kept one interjection Q4_K dropped. The Spanish track with `-l es` came out correct but lowercase at cue starts.

Full episode (44 min of dense dialogue, 170 slices, CPU Q8_0, 16 threads, on a box that was also serving media): 906 s, 2.9x realtime, 447 cues. Whisper large-v3 on the same box's integrated GPU runs at about 7x, so a library sweep takes roughly two and a half times longer on this engine.

## Timing

Qwen3-ASR returns text per VAD segment with no timestamps. A segment that VAD keeps open under music ran 22 s on the test clip, and `--max-len` splits it into cues timed proportionally, so the first piece shows for 22 s. Whisper does not have this failure because its decoder times each segment.

Levers tried on the pinned release:

- `--vad-max-speech-duration-s 8`: no change in that cue; the segment is not split by the tuning on this backend.
- `-am <canary-ctc-aligner-q4_k.gguf> --force-aligner` (the CTC aligner CrispASR's own `-am auto` resolves to for this backend, 392 MB): the cue that started 22 s early now runs 01:13.54 to 01:15.22 against 01:13.53 to 01:15.91 in the human subtitle. 87 s instead of 36 s on the clip, so about 2.4x the run time. The aligner is Canary's and knows 25 languages, English and the 24 targets.
- The same plus `--split-on-punct`: "Wait, please." becomes its own cue (00:55.04 to 00:56.16, human: 00:54.81 to 00:56.22) instead of one cue bridging the ten-second pause to "This is everything...". Same run time. It also leaves one-word cues of under a second where a sentence end falls just past the length cap.

On the Spanish track with the aligner and punctuation split, every cue landed within 0.3 s of the human subtitle on the first ten ("Dime lo que sabes." 01:56.21, human 01:55.91) and the text came out capitalised and punctuated, which the VAD-timed run had not. Continuation cues carry a leading space from the word boundary, which the provider trims.

So the aligner ships as a managed, pinned download in the Qwen3 panel, on by default once installed, used for the 25 languages it knows, with `--split-on-punct` alongside. Other languages keep VAD timing. Two things the aligner does not fix: the first word of a slice that opens with music can still be anchored at the slice start, and cue cuts by length can leave fragments.

The limitation is documented in [limitations.md](../limitations.md#qwen3-asr-cues-are-timed-by-vad-unless-the-aligner-is-installed). The plugin's own speech alignment pass (`Align subtitles to speech`) snaps cue starts to speech afterwards.

## Rollout

1. The plugin: catalog, provider, switch, setup endpoints, settings panel, tests, this doc.
2. The reference deployment: install 4.11.0.0, download Q8_0 through the new endpoint so the probe runs, select the engine, then one real title through the queue, checked against the file on disk.
