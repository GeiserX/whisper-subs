# Roadmap

## Planned

### Parallelize the scheduled sweep across the worker pool

The v4.0 worker pool already transcribes in parallel for manual **Generate** / **Generate All** and for the priority-drain phase of the scheduled task, but the scheduled task's own library *sweep* still processes its backlog one item at a time — so idle workers sit unused while a large auto-generation run works through the library. Turn the sweep into a bounded producer that keeps every worker slot busy up to the pool's total capacity, so the automatic backlog benefits from the whole pool the same way a manual bulk request does.

### Worker health checks & circuit breaker

Pooled workers are currently assumed healthy; a slow or unreachable worker is only discovered per job, when a call hits its length-derived deadline. Add active health probing plus a circuit breaker that quarantines a worker after repeated failures and re-admits it once it recovers, so one flaky endpoint can't repeatedly waste a slot.

### Cloud burst

Cost-weighted routing already prefers free local workers and only "bursts" to a worker with a non-zero cost weight when the local ones are saturated. Build first-class cloud-burst ergonomics on top of that — managed cloud endpoints and spend caps — so a homelab can spill over to a paid API during a big catch-up run without hand-configuring each endpoint.

### Parakeet Provider

NVIDIA Parakeet integration for GPU-accelerated transcription.

### Custom Command Provider

Define arbitrary CLI commands as transcription backends.

## Done

- **Distributed Transcription (Worker Pool)** — pool multiple machines / GPUs / a NAS / cloud endpoints and transcribe in parallel, with cost-weighted routing that prefers free local workers and bursts to paid ones only when locals are saturated. Off by default; a normal single-server install is unchanged. *(v4.0)*
- **English Translation** — generate an English subtitle for foreign-language audio via whisper's `--translate`, either alongside the native-language subtitle (`Enable Translation`) or on its own (`Translation Only` mode). *(v3.11)*
- **Forced Subtitles** — foreign-parts-only subtitle tracks: VAD-based chunking, per-chunk language detection (`whisper-cli --detect-language`), selective transcription of only the segments whose language differs from the primary audio. Output `Movie.en.forced.generated.srt`, auto-recognized by Jellyfin. Modes: `Full`, `Forced Only`, `Full + Forced`. *(v3.0)*
- **Real-time Progress** — live progress banner in the admin UI: current item, phase (extracting audio, transcribing), per-file progress, and overall stats.
- **Batch Operations** — generate subtitles for a whole series/season/library from the dashboard, with a named-tier priority queue and (v4.0) parallel dispatch across the worker pool.
