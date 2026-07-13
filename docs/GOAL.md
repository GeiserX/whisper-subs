# GOAL

whisper-subs v3.21.1.0 follow-up to issue #95. Research is already complete (5-agent panel). Implement two fixes, then review (internal review-pr! panel + CodeRabbit), release v3.21.1.0, then reply to @eBellmer. FIX B (config page hint): in Web/configPage.html updateVariantHint, add explicit branches for `vulkan-noavx` and `cuda12-noavx` showing a green "GPU compatibility build — correct for this CPU+GPU" message, so selecting the correct compatibility variant no longer falls into the CPU `else` branch and shows the misleading red "this CPU does not support AVX2 — will crash" warning. FIX A (variant persistence): add `WhisperBinaryVariant` to PluginConfiguration (default ""); in WhisperSetupService.DownloadBinaryAsync success branch set config.WhisperBinaryVariant = currentVariant (the post-fallback validated variant) alongside WhisperBinaryPath; expose the installed variant in the Setup/Status response (GetStatus + SetupStatus); in Web/configPage.html loadEngineVariants prefer the persisted installed variant over gpuInfo.RecommendedVariant when it's non-empty AND present in the available variant list, else fall back to RecommendedVariant. Add a ConfigurationTests assertion for the new field default. Root cause recap: the plugin never persisted the chosen variant (only WhisperBinaryPath, a fixed path), and the config page re-defaulted the dropdown to RecommendedVariant on every load — pre-3.21.0.0 that was the AVX2 `vulkan` for an AVX-but-no-AVX2 CPU, so a re-download silently overwrote the user's vulkan-noavx with the crashing AVX2 build. v3.21.0.0 fixed the recommendation value; these two fixes remove the misleading hint and make the chosen variant stick.

_2026-06-25_

## 2026-07-01 — continuation

over this task . research first, then go all the way and close the issue after answering him:
https://github.com/GeiserX/whisper-subs/issues/105

## 2026-07-02 — continuation

and ace it: https://github.com/GeiserX/whisper-subs/issues/108

## 2026-07-05 — continuation

about this https://github.com/GeiserX/whisper-subs/issues/110
think about his way, research even better ways of improving that... then /sergio-loop with ralph to implement it and release, thank him and close the issue

## 2026-07-05 — continuation (priority-queue request system)

if we can somehow to allow users to "enqueue" themselves in the same UI they have somehow inside a show, episode, season, film, etc... to request it, and it to be enqueued with levels of PRIORITY. priority should be quite a thing here, by default for example in my jellyfin server i want to have everything that doesnt have subtitles, to have it, kind of a background task
But if I as an admin enqueue something, this should be prio number 1
if some user of my server wants to request something, then it becomes prio 2, ahead of the background task but below me, i'm the admin - just as an example, this should be all configurable very easily and with a safe and sound UX.
/sergio-loop over this idea, even create a github issue, maybe if you think it could merit it, we could even launch this queue system as a major version, idk, if you think its better minor, i hear you
use ralph
then install it on my server

### Clarification (2026-07-05): priority levels are NAMED LABELS, not numbers
"the levels of prio shouldn't be numbers, it should be labels 'critical' 'high' 'medium' 'low' 'background', for example."
→ A `PriorityTier` enum with an inherent order (Critical > High > Medium > Low > Background); labels everywhere user/admin-facing (config dropdowns, request UI, queue view). The enum's order drives dequeue internally. Default mapping (his example): admin request → High (or Critical), user request → Medium, background sweep → Background — each requester-kind maps to a configurable tier.

## 2026-07-06 — continuation (v4.0 distributed / pooled transcription)

Then /sergio-loop - dont get caught on what i want, make sure everything is really oriented towards users running however they want, maybe they have a wildly different scenario to mine. use ralph and continue grinding until we get to new major version.

→ Build whisper-subs **v4.0** = distributed / pooled multi-worker transcription, per the locked design (9-lane research + design Artifact 2b61c498). PARAMOUNT: keep it GENERAL for ANY user/topology (single-server default unchanged; N mixed workers; any OpenAI-compatible endpoint = local whisper-cli, CPU faster-whisper, NVIDIA, AMD, NAS, cloud). Sergio's iGPU-only/cloud-deferred picks are only HIS deployment + the example worker image we ship — the code/abstraction stays fully general. Additive, default-off, zero-config-safe.
Scope: one v4.0 (safety fixes folded in). PR sequence: (1) resilience foundation [4-guard 70h-fix + atomic writes + stream WAV + lease persistence], (2) worker abstraction + config + backward-compat, (3) N-slot dispatcher replacing TranscriptionLock + sweep-as-producer, (4) whisper.cpp-Vulkan worker Docker image + compose + docs, (5) config-UI worker rows + Test-connection + per-worker status. Preserve #112 queue contract + #110 skip-cache. Merge-to-main + release + install gated on Sergio's explicit OK.

### Refinement (2026-07-06): simple by default, progressive disclosure
"what most users will do is to have the local nas to perform the operations in his gpu or cpu, maybe some will use the remote api endpoint, and only a few might use the worker approach, so make it easy for the normal scenario and don't crowd too much for the users who just want something simple - yet for power users should be whatever they need"
→ UX tiers: (1) MOST users = local single-server (NAS's own GPU/CPU) — the DEFAULT, dead-simple, ZERO new clutter, existing engine config untouched. (2) SOME = one remote API endpoint (existing RemoteWhisperApiUrl). (3) FEW = full multi-worker pool (power users). The worker-pool config MUST be tucked away (advanced/collapsible, hidden/empty by default) so a normal user never sees pool complexity. General in CAPABILITY, simple in DEFAULT UX. Progressive disclosure.

### Release strategy refinement (2026-07-06)
"make sure we dont release, or either we release this one as 3.x as its purely just fixes of 3.x - then continue with building 4.x but make sure you dont release 4.x even if you merge stuff"
→ PR-1 (transcription resilience: 70h-fix + atomic writes + fail-fast) is PURE 3.x fixes → release as **v3.29.0.0** (retarget to main, bump version, merge → 3.x release; benefits all current users incl. the 70h class). The 4.x distributed feature (PRs 2-5) keeps building + merging into **v4-dev** but must **NOT release** — build-release is main-only, so v4-dev merges never release; v4-dev→main (the 4.0.0.0 release) stays gated on Sergio's explicit OK.

## 2026-07-09 — continuation (/sergio-loop)
over the beads
just delete (dont close) the meo bead, its just for me, not for the project
extensively check the mac mini (16gb) and then just build whatever needed there to add a worker
only when all's working then simply continue with the other beads

## 2026-07-13 — continuation (v4.3.1 worker robustness)

So also fix the flakiness / non-robustness issue, this macwhisper should be functioning and sound and safe, right? continue

Scope chosen: "The works" — worker-side hardening (serialisation + wedge self-recovery, DONE) + targeted foreign-language detection-stall fix (lightweight/shorter detection, quality-neutral) + per-physical-endpoint concurrency keying (a single whisper-server can never be oversubscribed by MaxConcurrency>1 or duplicate endpoint rows) + duplicate-endpoint validation & UI warning. Then build/test/review/CodeRabbit → merge → release 4.3.1 → install on Jellyfin.

so /sergio-loop until completion with ralph
