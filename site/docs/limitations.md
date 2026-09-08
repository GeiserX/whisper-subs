---
title: Limitations
description: What WhisperSubs cannot do, why, and what would have to change.
sidebar_position: 8
---

# Limitations

This page is the honest list. Everything here is a real constraint, not a setting that has been overlooked. Where a workaround exists, it is given.

## Speaker separation

WhisperSubs cannot label who is speaking, and cannot split a cue by narrator. There is no toggle for this because there is no data to expose.

Whisper is a speech-**to-text** model. It predicts text tokens from audio. Speaker identity never appears in its training targets or in its output vocabulary, so nothing anywhere inside it computes "who said this".

### The two whisper.cpp flags that look like they solve it

**`-di` / `--diarize`** is documented as stereo audio diarization. The implementation is a loudness comparison between the left and right channels: if the left is more than 1.1x louder it prints speaker 0, if the right is louder it prints speaker 1, otherwise it prints `?`. It is gated on the input having exactly two channels. It was built for recordings where each speaker is hard-panned to their own channel, such as a two-mic interview or a phone call. Film and TV dialogue is mixed to the centre and arrives at near-identical energy in both channels, so almost every line falls into the `?` branch. It does not recognise voices at all.

**`-tdrz` / `--tinydiarize`** is genuine speaker-turn detection, with severe constraints. It requires the specific `ggml-small.en-tdrz.bin` checkpoint, which is English-only (finetuned on the AMI meeting corpus) and a *small* model. WhisperSubs downloads the `large-v3` family because accuracy matters on film dialogue. Switching to tinydiarize would remove transcription entirely for every non-English user and make English output materially worse. It still would not answer the question: it emits a bare `[SPEAKER_TURN]` marker meaning "somebody else started talking" — not who, and not how many people. It is explicitly experimental, at roughly 71% turn recall on its own benchmark.

### What real diarization would take

Proper diarization (WhisperX, whisper-diarization, NVIDIA NeMo) is a second machine-learning pipeline running alongside Whisper: voice-activity detection, then neural speaker embeddings, then clustering, then aligning that speaker timeline onto Whisper's word timestamps.

In practice that means a Python and PyTorch runtime sitting next to this plugin's self-contained native binary, plus pyannote's gated models, which require a Hugging Face account and a manually accepted licence before they can be downloaded at all. It is typically the slowest and most memory-hungry stage of the pipelines that use it, often comparable to the transcription itself.

The accuracy is the harder problem. Published error rates for the current pyannote pipeline are about 18.8% DER on AMI meeting audio and 21.7% on DIHARD III. Measured on TV series specifically, audio-only diarization comes in at 25.6% DER, and the difficulty is exactly what film audio is made of: overlapping dialogue, background score and effects masking voice differences, and short speaker turns. About a quarter of speech mislabelled is not a feature that can ship as correct.

Even at its best, it produces anonymous cluster labels — `SPEAKER_00`, `SPEAKER_01` — never character or actor names. Getting from those to "Ellie" and "Joel" is a further unsolved step that needs per-show voice enrollment.

`.srt` also has no speaker field. Even perfect diarization has to be rendered by convention: Netflix's style guide uses a leading hyphen per speaker, one speaker per line, with a bracketed name when the speaker is not visible on screen.

### The one path that exists elsewhere

OpenAI offers a `gpt-4o-transcribe-diarize` model that does diarization server-side and returns speaker-labelled output through a `diarized_json` response format (labels come back as "A", "B", and so on, unless you supply reference samples).

WhisperSubs negotiates `srt` and `verbose_json` only. It neither requests nor parses `diarized_json`, so pointing a remote worker at that model today does not work. It is also a paid cloud service that uploads your audio, which is the opposite of why most people run this plugin.

:::tip Workaround
Run diarization outside the plugin, on the finished subtitle or the media file: [WhisperX](https://github.com/m-bain/whisperX) or [whisper-diarization](https://github.com/MahmoudAshraf97/whisper-diarization). WhisperSubs produces the transcript; those tools add the speaker layer.
:::

## Run-on lines with no punctuation

whisper.cpp applies no cue-length cap of its own — `--max-len` defaults to `0`, meaning unlimited. When the model drops into its no-punctuation mode, a whole utterance lands in one enormous cue.

The fix is a setting. In **Dashboard → Plugins → WhisperSubs**, set **Maximum subtitle line length** to `47`. That passes `--max-len 47` together with `--split-on-word`, so a capped line breaks on a word boundary instead of mid-word. Broadcast subtitling caps a line near 42 characters.

The default is `0`, so upgrading changes nothing and existing subtitles are untouched. Regenerate an item to see the difference.

Two scope notes:

- The setting reaches the Jellyfin host's own `whisper-cli` only. A remote worker segments its own output, so set `WHISPER_MAX_LEN=47` on the worker as well. See [Remote workers and hosted providers](./remote-workers.md).
- A `--max-len` placed in **Custom Whisper Arguments** wins, because custom arguments are appended last and `whisper-cli` takes the last value.

This is a guard against the worst case, not a cure for missing punctuation. Whisper normally punctuates. When it produces none at all, the cause is usually a small model (use `Large V3 Turbo (Q5)`, the 574 MB recommended default) or a wrong language guess, which applies one language's punctuation conventions to another language's words. If you know the audio language, set it explicitly instead of leaving it on auto.

## Translation

Two separate limits apply, and the second one is easy to hit by accident.

### English is the only target language

Whisper's `--translate` is architecturally limited to English output. It was trained for X→English and nothing else, so there is no way to reach another target language from inside whisper.cpp. The relevant upstream requests are [whisper.cpp#1219](https://github.com/ggml-org/whisper.cpp/issues/1219) and [whisper.cpp#1956](https://github.com/ggml-org/whisper.cpp/issues/1956) (a forced-decoding workaround, unreliable); an OpenAI maintainer confirmed the limitation in [openai/whisper#1167](https://github.com/openai/whisper/discussions/1167).

WhisperSubs will not add a text-to-text translation step. Translating an existing `.srt` is a different pipeline stage with a different API contract, different failure modes and a separate dependency; coupling subtitle generation to a translation service's availability doubles the config surface for something orthogonal to speech-to-text.

:::tip Workaround
Generate the subtitle here, then translate the file with a dedicated tool. [Lingarr](https://github.com/lingarr-translate/lingarr) mounts your media directories and translates subtitles in place, with LibreTranslate, DeepL, Google, OpenAI, Anthropic, Gemini, DeepSeek, Bing, Yandex, Azure and local Ollama backends. [Sublarr](https://github.com/Abrechen2/sublarr) is another option with Sonarr/Radarr webhook support.
:::

### The recommended model cannot translate

:::warning
The recommended model, **Large V3 Turbo (Q5)** (`ggml-large-v3-turbo-q5_0.bin`), cannot translate. Neither can **Large V3 Turbo (F16)**. Both are flagged `canTranslate: false` in the model catalog.
:::

whisper.cpp's distilled turbo models were fine-tuned without the translate task, so `--translate` emits the **source language** instead of English.

WhisperSubs does not block this. It logs a warning and runs the job anyway. The result is source-language text written into an English-named file: with the default filename template `{name}.{lang}.{label}{.type}`, that is `Movie.en.WhisperSubs.translated.srt`. Nothing in the file name, and nothing in Jellyfin's subtitle picker, says the text is not English. The only signal is the warning line in `jellyfin.log`.

The same applies to **Forced Only** and **Full + Forced** in an English-primary library, where foreign-language inserts are translated to English: a turbo model emits the source language, and again only a log warning is raised.

To translate reliably, open the plugin setup page and download and activate one of these instead:

| Model | Size | Note |
|---|---|---|
| **Large V3 (Q5)** | 1081 MB | Best choice for non-English audio and translation |
| **Large V3 (F16)** | 3095 MB | Maximum accuracy, 3-4x slower than turbo |
| **Medium (Q5)** | 539 MB | Similar size to turbo-q5, slower and less accurate |

Then regenerate the affected items.

### Forced subtitles in a non-English library stay in the source language

Forced subtitles are translated to English only when the title's primary language is English. When the primary language is something else, Whisper has no path to that target, so the foreign-language inserts are kept as transcribed rather than written as mislabelled English into a `.<lang>.forced` file. A French insert in a Spanish film ends up as French text, not Spanish.

## Forced subtitle generation is slow

Forced mode runs a multi-step pipeline: audio extraction, VAD-based speech segmentation, then per-chunk language detection on every ~30-second segment, before any transcription starts. For a 2-hour film that is roughly 240 individual detection calls up front. On CPU that phase alone can take 10-20+ minutes per title. GPU acceleration helps significantly.

A dedicated ~148 MB base model is downloaded for the detection pass specifically to keep it cheap, but the call count is inherent to the approach.

If you do not need foreign-dialogue-only tracks, use **Full** mode.

## The scheduled sweep processes one item at a time

The worker pool fans out across every configured worker for manual **Generate** and **Generate All**. The scheduled auto-generation task still walks its backlog serially, so adding workers does not speed up an overnight sweep. Parallelizing it is on the roadmap.

The workaround today is to run **Generate All** from the admin dashboard, which does use the whole pool.

## Engine auto-download is Linux only

The setup page downloads prebuilt `whisper-cli` binaries for two platforms:

| Platform | Variants offered |
|---|---|
| `linux-x64` | CPU, CPU compatibility (no AVX), CUDA 12, CUDA 12 compatibility, Vulkan, Vulkan compatibility, AMD ROCm |
| `linux-arm64` | CPU, CPU compatibility (no AVX) |

Every other platform gets no variants at all. On Windows or macOS, build or install whisper.cpp yourself and point **Whisper Binary Path** at the binary. Model downloads are unaffected and still work everywhere.

## Lyrics generation is experimental

`.lrc` lyrics generation for music libraries is off by default. Whisper is trained on speech, not singing, so accuracy on music is well below what the same model achieves on dialogue.

## Image-based subtitles are never converted

PGS and VOBSUB tracks are images. WhisperSubs does not OCR them — it transcribes the audio instead. By default an image track does not count as "already subtitled", so a text subtitle is still generated alongside it. Turning on **Count image-based subtitles as present** suppresses that, at the cost of leaving the title without a searchable text track.

## Hosted models that return text without timestamps

A subtitle needs timings. `response_format=json` returns the transcript text and no timestamps at all, so it cannot be synchronized, and WhisperSubs rejects such a response rather than fabricating timings.

On OpenAI, timestamps and SRT are available on `whisper-1` only; `gpt-4o-transcribe` and `gpt-4o-mini-transcribe` return text with no timings and cannot be used for subtitles. On OpenRouter, timestamps exist only on its OpenAI-compatible models.

Details, and the matching upload-size limits, are in [Remote workers and hosted providers](./remote-workers.md).
