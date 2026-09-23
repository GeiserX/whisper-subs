# Design: non-English translation targets with CrispASR + Canary

Status: proposed (2026-09-23). Not implemented. Tracks issues [#38](https://github.com/GeiserX/whisper-subs/issues/38) and [#41](https://github.com/GeiserX/whisper-subs/issues/41).

## The problem

Whisper translates in one direction only: any language to English. That is a property of the
model, not of whisper.cpp, and the two upstream requests to change it ([whisper.cpp #1219](https://github.com/ggml-org/whisper.cpp/issues/1219) and [#1956](https://github.com/ggml-org/whisper.cpp/issues/1956))
were closed as not planned in September 2026. WhisperSubs therefore offers "Translation to
English" and nothing else. A Dutch, Spanish or Polish viewer of an English film gets no subtitle in
their own language from this plugin.

Text-to-text translation of the finished `.srt` stays out of scope. It is a different pipeline
stage with its own service, failure modes and configuration, and [Lingarr](https://github.com/lingarr-translate/lingarr) already does it well.
That decision from April 2026 is unchanged.

What changed is that a speech model now exists that translates audio directly into 24 European
languages, and a whisper.cpp fork runs it with the same CLI contract this plugin already drives.
That keeps the feature inside the plugin's domain: audio in, subtitle out, one process.

## The pieces

**[NVIDIA Canary-1B-v2](https://huggingface.co/nvidia/canary-1b-v2)** (Aug 2025, CC-BY-4.0, 978M parameters). Speech recognition for 25
European languages (bg, hr, cs, da, nl, en, et, fi, fr, de, el, hu, it, lv, lt, mt, pl, pt, ro,
sk, sl, es, sv, ru, uk) and speech translation from English into 24 of them and from those 24 into English. Segment
timestamps are emitted for translated output. There is no direct path between two non-English languages.

**[CrispASR](https://github.com/CrispStrobe/CrispASR)** (MIT, forked from whisper.cpp in March 2026). One binary, `crispasr`, that runs
Whisper GGML models with whisper-cli's flags and adds ggml runtimes for Canary, Parakeet, Voxtral,
Qwen3-ASR and others, selected by `--backend` or auto-detected from the model file. Prebuilt Linux
x86-64 tarballs exist for CPU, CPU-legacy, Vulkan, CUDA 12/13 and HIP. It also has a server mode
whose `/v1/audio/transcriptions` accepts `source_lang` and `target_lang` multipart fields, which
matters for the worker pool. That server has no `/v1/audio/translations` route.

**GGUF weights**: [cstr/canary-1b-v2-GGUF](https://huggingface.co/cstr/canary-1b-v2-GGUF) on Hugging Face. `canary-1b-v2-q8_0.gguf` is 1.05 GB,
`q5_0` is 720 MB, f16 is 1.97 GB.

## Scope

In scope for the first release:

- A second translation target set next to "English": any of Canary's 24 non-English languages,
  for titles whose audio is English.
- Canary as the engine for those targets, running locally through a plugin-managed `crispasr`
  binary, or remotely through a CrispASR server in the worker pool.
- English stays on Whisper, byte-for-byte the same command line as today.

Out of scope, stated on the settings page and in the limitations doc:

- Source languages other than English for a non-English target. A Spanish film cannot become a
  Dutch subtitle here; that is Lingarr's job on the English subtitle this plugin can produce.
  The spike tried it anyway (French to Spanish, Spanish to French) and got code-mixed cues or a
  single cue out of 23 speech segments, with exit code 0 and no warning.
- Non-European languages as targets.
- Forced (foreign-parts-only) subtitles in a non-English target.
- Replacing whisper-cli with CrispASR for transcription. CrispASR is six months old, ships 119
  backends and moves fast; whisper-cli stays the pinned, statically built transcription engine.

## Architecture

### One new pure decision: the translation route

Today the translation pass has one question, "is the model translation-capable?", answered by
[`ModelCatalog.IsTranslationCapable`](../../Setup/ModelCatalog.cs). The new pass has three inputs and one answer:

```
TranslationRoute.Decide(sourceLanguage, targetLanguage, canaryAvailable)
  -> Whisper      target == "en"                              (unchanged path)
  -> Canary       source == "en" && target in CanaryTargets && canaryAvailable
  -> Unsupported  anything else, with a reason string
```

`Unsupported` fails the item with a clear message and never writes a file. This is deliberate:
the turbo-model trap ([issue #44](https://github.com/GeiserX/whisper-subs/issues/44)) taught that a mislabelled subtitle is worse than no subtitle,
so a route the engine cannot honour must not produce output.

`CanaryTargets` is a static list in `Setup/CanaryCatalog.cs` mirroring the model card. Unit
tests cover every branch, including the Latvian exception if the model card's note holds up.

### Provider interface

[`ISubtitleProvider.TranscribeAsync`](../../Providers/ISubtitleProvider.cs) gains a target:

```csharp
Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct,
    bool translate = false, string? targetLanguage = null);
```

`targetLanguage == null` or `"en"` with `translate == true` is the existing Whisper behaviour.
`WhisperProvider` throws `NotSupported` for any other target; it never guesses.

A new `CanaryProvider : ISubtitleProvider` wraps the `crispasr` binary:

```
crispasr --backend canary -m <canary.gguf> -f <wav> -sl en -tl <target> -t <threads>
         --no-auto-aligner --cache-dir <data>/crispasr/cache
         --vad --vad-model <silero> [--vad-* tuning] [--max-len N --split-on-word]
         --print-progress -osrt -of <prefix>
```

Every flag after the target is there because the spike showed what happens without it:

- `--no-auto-aligner`: Canary's CTC aligner is on by default, doubles the runtime, and keeps
  using the GPU under `--no-gpu`. Segment timestamps without it were fine for subtitles. Word
  alignment can become an opt-in later.
- `--cache-dir`: the binary fetches companion models on its own, even without `--auto-download`.
  Pinning the cache under the plugin's data folder keeps that inside the managed tree, and
  passing `-sl en` explicitly avoids the language-ID download that `-l auto` triggers.
- `--vad`: without VAD, Canary returns the whole file as one cue. The plugin's existing Silero
  model and tuning flags apply unchanged. `--max-len` with `--split-on-word` is honoured too and
  is the same knob as today's `SubtitleMaxLineLength`.
- No `--prompt`: Canary ignores it.

It reuses [`WhisperProvider`](../../Providers/WhisperProvider.cs)'s process handling and SRT read-back. `ProgressRegex` gains a second
anchored alternative for `crispasr: progress = N%`. The pure argument builder
(`CanaryProvider.BuildArguments`) is unit-tested like `BuildTranscribeArguments`.
`DetectLanguageAsync` is not implemented on it; detection stays on the Whisper base model, so
the forced-subtitle and translation-probe paths do not change.

### Binary and model management

Mirror the BSRoformer pattern, not the whisper-cli pattern. whisper-cli is built by this
project's CI from a pinned whisper.cpp tag and shipped as a raw static binary. CrispASR publishes
its own release tarballs, so `Setup/CrispAsrCatalog.cs` and `Setup/CrispAsrSetupService.cs`
copy [`RoformerCatalog`](../../Setup/RoformerCatalog.cs) and [`VocalSeparationSetupService`](../../Setup/VocalSeparationSetupService.cs): a pinned upstream tag, a
(platform, variant) to asset map, a SHA-256 per asset, download to a staging dir, extract,
`chmod +x`, validate with `--help`, promote atomically, keep the previous install on failure.

Variants offered on Linux x64: `cpu` (default), `vulkan`, `cuda12`, `cuda13`, `hip`. CPU is
the default on purpose: the spike measured no speed-up for Canary on an Intel iGPU over 16 CPU
threads, and the CPU tarball has no driver dependency. GPU variants are for discrete cards. Note
from the [upstream install guide](https://github.com/CrispStrobe/CrispASR/blob/main/docs/install.md): the Vulkan and HIP tarballs do not fall back to CPU when the
driver is missing, the CUDA ones do. The validation step must run a real one-second inference,
not only `--help`, because `--help` succeeds on a Vulkan build with no usable device, and it
must run with the pinned cache dir so validation cannot trigger a download elsewhere.

Layout under the plugin data folder:

```
crispasr/
  bin/          extracted tarball (binary + bundled shared libraries, RUNPATH=$ORIGIN)
  models/       canary-1b-v2-q8_0.gguf (default) or q5_0
  cache/        whatever the binary fetches on its own (--cache-dir); nothing expected in v1
```

The model download reuses the Hugging Face resolve URL pattern from `ModelCatalog` with a
content-length check. The upstream tarball version is pinned in the catalog and bumped by hand
after a compatibility run, exactly like BSRoformer's `Version`.

### Configuration

[`PluginConfiguration`](../../Configuration/PluginConfiguration.cs) gains:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `TranslationTargetLanguages` | `List<string>` | `[]` | Extra targets besides English. Empty keeps today's behaviour. |
| `CrispAsrBinaryPath` | `string` | `""` | Set by the setup service after a validated install; manual override allowed. |
| `CrispAsrBinaryVariant` | `string` | `""` | Installed variant, re-offered on the setup page. |
| `CrispAsrBinaryVersion` | `string` | `""` | Pinned upstream tag the install came from. |
| `CanaryModelPath` | `string` | `""` | Active GGUF. |
| `CrispAsrThreadCount` | `int` | `0` | 0 = engine default. |

`EnableTranslation` keeps its meaning (English). The settings page shows the new list under the
Translation section as a multi-select limited to `CanaryTargets`, with the sentence "Only for
titles with English audio. Other source languages still translate to English only."

### The translation pass

[`SubtitleManager.GenerateTranslatedSubtitleAsync`](../../Controller/SubtitleManager.cs) becomes a loop over `["en"] ∪
TranslationTargetLanguages`, one output file per target using the existing naming engine:

```
{name}.{lang}.{label}.translated.srt   ->   Movie.nl.WhisperSubs.translated.srt
```

[`SubtitleNaming`](../../Controller/SubtitleNaming.cs) already takes `lang` as a parameter and classifies `.translated.` as owned, so
naming needs no change. Two places assume the single target is English and must change together:

- the "already translated" glob, which today matches any owned `.translated.` file and must match
  per language;
- `ShouldSkipForExistingSubtitle(item, "en")`, which must be evaluated per target.

The per-target skip rules keep the current semantics: skip when the title's audio is already in
the target, when a usable subtitle in the target exists, or when the route is Unsupported. The
English pass runs first, and its language probe result (the existing `DetectLanguageAsync` call)
decides the Canary routes too, so no extra detection runs.

### Worker pool

[`WorkerCapabilities.CanTranslate`](../../Controller/Workers/WorkerModel.cs) (bool) becomes `TranslateTargets` (set of language codes;
`{"en"}` is the compatibility default, an empty set means "cannot translate"). Remote rows get a
"Translation targets" field. [`WorkerScheduling.CanServe`](../../Controller/Workers/WorkerScheduling.cs) filters on target membership; the
cost-weighted scoring is untouched.

[`RemoteWhisperProvider`](../../Providers/RemoteWhisperProvider.cs) today selects the endpoint by
path: `/v1/audio/translations` for English, `/v1/audio/transcriptions` otherwise. A CrispASR
server answers 404 on the translations route, so a worker row needs a dialect switch. Rows keep
the current path-based dialect by default (whisper-server, OpenAI, Groq); a row marked as a
CrispASR server sends everything to `/v1/audio/transcriptions` and expresses translation as
`translate=true` (to English) or `source_lang` + `target_lang` (Canary targets). Two more facts
from the spike shape that dialect: the server runs VAD on every request when started with `-vm`,
and there is no per-request VAD model field, so the worker's startup command owns segmentation
(same as the whisper-server worker today); and an unsupported `target_lang` returns HTTP 200 with
an empty body, so the empty-SRT rejection the plugin already applies must name the target language
in its error. The plugin's own worker image stays whisper.cpp based; a CrispASR-based worker
variant is a later deliverable if demand appears.

### Progress and errors

CrispASR's `--print-progress` line for the Canary backend is `crispasr: progress =  11% (1/9
slices)`, so `ProgressRegex` gets a second anchored alternative rather than a looser pattern.
Launch failures go through the existing [`LocalBinaryHealth`](../../Setup/LocalBinaryHealth.cs) probe, with a second health instance
keyed to the crispasr binary so a missing Vulkan driver on one engine does not park the other.

## Spike evidence (Intel UHD 770 iGPU, Vulkan, CrispASR v0.8.35, 2026-09-23)

Run inside a Debian 13 container (glibc 2.41) with the same Vulkan packages the plugin's
GPU wrapper installs, on a 90-second clip from a film that has both an English and a Spanish
dub, cut from the same offset so the runs compare like for like. GPU use comes from the i915 fdinfo counters; a `--no-gpu` run read 0.0 s, which is the negative control that proves the counter can go to zero.

**Binary.** `crispasr-linux-x86_64-vulkan.tar.gz` is 74 MB and unpacks to an 82 MB `crispasr`
plus bundled OpenBLAS, gomp, gfortran and quadmath with `RUNPATH=$ORIGIN`. It needs the
system's `libvulkan.so.1` and fails to start on a host without the loader, and at most
GLIBC 2.34 / GLIBCXX 3.4.30. `--help` runs, an unknown flag exits 1 with
`error: unknown argument`.

**Model.** `canary-1b-v2-q8_0.gguf` (1.05 GB). On first use the binary silently fetched two
companions into its cache dir even without `--auto-download`: the CTC aligner
(`canary-ctc-aligner-q4_k.gguf`, 392 MB) and `ggml-tiny.bin` (78 MB, used for language ID when
`-l auto`). Peak GPU memory (UMA) 1.9 GB with aligner and VAD, against 3.6 GB for Whisper
large-v3.

**Timings** (90 s clip, 16 threads, box under other load):

| Run | Wall | RTF | GPU busy | Peak RSS |
|---|---|---|---|---|
| Canary en to es, no aligner, no VAD | 11.1 s | 8.9× | 8.6 s | 1.1 GB |
| Same, `--no-gpu` | 10.2 s | 9.2× | 0.0 s | 1.6 GB |
| Canary en to es, VAD, no aligner | 12.8 s | 7.8× | 8.8 s | 1.1 GB |
| Canary en to es, VAD + aligner (the default) | 26.3 s | 3.6× | 16.4 s | 1.1 GB |
| Canary en to en, VAD + aligner | 26.0 s | 3.6× | 15.5 s | 1.1 GB |
| Canary es to es, VAD + aligner | 23.8 s | 4.0× | 14.7 s | 1.1 GB |
| Whisper large-v3, plugin's whisper-cli (5 beams), warm | 44.6 s | 2.0× | 40.0 s | 0.23 GB |
| Whisper large-v3 through crispasr (greedy) | 26.4 s | 3.4× | 22.0 s | 0.24 GB |

Two findings drive the design. The iGPU gives Canary no speed-up over the CPU (Vulkan and
`--no-gpu` are within a second), it only moves the load off the cores and saves 0.5 GB of RSS.
And the auto-aligner, on by default for Canary, doubles the runtime and keeps using the GPU even
under `--no-gpu`; `--no-auto-aligner` brings GPU time to zero.

**Flag compatibility** with what `BuildTranscribeArguments` emits: `-m -f -l -t -mc -sns
--translate --vad --vad-model --max-len --split-on-word -osrt -of --detect-language --no-gpu`
and all six `--vad-*` tuning flags exist with the same semantics and defaults. The plugin's
Silero v6.2.0 model loads unchanged. `-of` writes `<prefix>.srt`. Differences:

- `--print-progress` is backend-specific. Whisper prints
  `whisper_print_progress_callback: progress = 100%` (matches `ProgressRegex`); Canary prints
  `crispasr: progress =  11% (1/9 slices)`, which does not.
- `--prompt` is accepted and ignored by Canary (byte-identical output with and without).
- Beam size defaults to greedy (1) in crispasr and 5 in whisper-cli. That is the whole reason the
  Whisper-through-crispasr row is 1.7× faster; `-bs 5` restores parity.
- `-v` is `--verbose` here and `--vad` in whisper-cli. The plugin only sends the long form.
- Canary needs `--backend canary` or model auto-detection, and `-sl`/`-tl`. The plugin's current
  `-l es --translate` maps to `src=es tgt=en` unchanged, so the English pass would work as is.
- `--detect-language` output is the same line from both binaries:
  `whisper_full_with_state: auto-detected language: en (p = 0.942324)`.

**Output.** Without VAD, Canary emits the whole 90 s as one cue. With VAD it emits 8 or 9 cues of
up to 11 s; `--max-len 42 --split-on-word` splits them to 19 cues at 42 characters. SRT format
matches whisper-cli, including the leading space on text lines. In the en to es run one of eight
VAD slices came out untranslated (English text in a Spanish file), and it repeated across runs.
Spanish transcription quality (es to es) was on par with Whisper large-v3 on this clip. Canary's
es to en was weaker than Whisper's.

**Pairs with no English side.** The CrispASR CLI doc says `-tl` can be "set different from
`-sl`" for any pair. The model card says only English-hub pairs exist. The model card is right.
On a 60 s French clip and the 90 s Spanish clip, with VAD and no aligner:

| Pair | Exit | Cues | Output |
|---|---|---|---|
| fr to fr (control) | 0 | 5 | clean French |
| fr to en (control) | 0 | 5 | English, weak |
| fr to es | 0 | 4 | Spanish and French mixed inside the same cue |
| es to fr | 0 | 1 | 23 VAD segments in, one cue out, the rest dropped silently |

No warning, exit code 0 every time, and the server gives the identical result. The route
decision therefore admits only `en` as source for the new targets, and the doc stops calling
this a model-card limitation and calls it a tested one.

**Server mode.** `crispasr --server --backend canary -m <gguf> -vm <silero> --cache-dir <dir>`
warms up in about a second after model load and lists `/inference`, `/v1/audio/transcriptions`,
`/load`, `/health`, `/backends` and `/v1/models`. A POST with `response_format=srt language=en
source_lang=en target_lang=es` answered in 8.6 s with the same eight cues as the CLI, including
the same untranslated first slice. `translate=true` with `language=es` returned English. A
`task=translate` field is ignored. `POST /v1/audio/translations` returns 404. The server did not
run the aligner even without `no_auto_aligner`, and it ran VAD on every request because the model
was given at startup. `target_lang=xx` returned 200 with an empty `application/x-subrip` body and
only a stderr line naming the cause.

**Detection.** Canary has no language identification of its own. `--detect-language` on the
Canary backend loads Whisper tiny, prints `crispasr[lid]: detected 'en' (p=0.973) via whisper`
in a format the plugin's regex does not match, and then transcribes the whole clip anyway.
Detection stays on the Whisper base model.

## Rollout

1. Catalog + setup service + settings page section, behind the empty-list default. Nothing
   changes for an existing install.
2. `TranslationRoute`, `CanaryProvider`, the per-target loop, unit tests for every pure piece.
3. Worker capability change with the `{"en"}` default and the two multipart fields.
4. Docs: [`configuration.md`](../../site/docs/configuration.md) gains the field, [`limitations.md`](../../site/docs/limitations.md) rewrites the "English is the only
   target language" section to "English is the only target for non-English audio", and the
   Lingarr pointer stays for that case.
5. Ship as a minor version. Bump the CrispASR pin only after re-running the spike commands.

## Risks

- CrispASR is young and broad. Mitigation: pinned tag, SHA-256 per asset, inference-based
  validation, and the engine only ever handles the new targets. If the project stalls, the
  feature is removed by deleting the catalog; whisper-cli users are unaffected.
- Canary quality on film audio (music, effects, crosstalk) is unmeasured beyond the spike, and
  the spike already showed one VAD slice in eight coming out untranslated (English text inside a
  Spanish file), reproducibly. The first release labels the targets "experimental" on the
  settings page, and the leak goes upstream as a CrispASR issue with the clip's parameters. If it
  persists, a cheap per-cue guard (re-run the leaked slice once, then drop it) is the fallback.
- Canary's translation into English was weaker than Whisper's on the same clip. The English pass
  stays on Whisper; Canary is only ever used for the new targets.
- The binary accepts a pair it cannot serve and exits 0 with mixed-language output. The route
  decision is the only guard, so it must run before any process is spawned, and the "experimental"
  label stays until an upstream check exists.
