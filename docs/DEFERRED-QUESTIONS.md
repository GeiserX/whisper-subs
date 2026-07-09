# Deferred Questions — whisper-subs

## bd delete whisper-subs-meo is blocked (beads tooling)
- **Context:** Sergio asked to DELETE (not close) the `whisper-subs-meo` bead. `bd delete` fails on both clones: pod bd **1.0.4** → `column "depends_on_id" could not be found` (schema mismatch); Mac bd **1.1.0** → refuses to auto-apply 4 pending schema migrations (v49→v53) on a "remote-backed" DB (would fork the schema). `bd dolt show` shows NO remotes, and there's no raw-SQL passthrough / mysql client to do a targeted row delete.
- **Default taken:** left meo in place (it's only in the LOCAL beads DB — `.beads` is untracked in this public repo — so it is NOT "in the project"; Sergio's "not for the project" concern is already satisfied). Did not force a schema migration on the shared beads DB.
- **To change / needs Sergio:** align the bd version across pod+Mac (both to 1.1.0), confirm the remote-sync situation, apply the v49→v53 migration once, then `bd delete whisper-subs-meo --force`. His beads-infra call.

## Activate the Mac mini worker in the live pool (needs a Jellyfin restart)
- **Context:** M4 worker is built, persistent, configured, and verified (TestConnection ok, 46× faster than the iGPUs). But the live WorkerPool only rebuilds when idle, and the S6 dispatcher is busy — so the M4 isn't dispatched to yet. A Jellyfin restart rebuilds the pool with all 3 workers.
- **Default taken:** did NOT restart Jellyfin (2 people were watching; a restart also drops the in-flight items — bead 1t0). The M4 will join the pool at the next natural rebuild / restart-when-idle.
- **To change:** restart Jellyfin when playback is clear (`docker restart jellyfin` on watchtower), then re-`GenerateAll` S6 to re-queue any dropped in-flight; the M4 then finishes S6 in minutes. (Bead 1t0's in-flight-lease persistence would make this restart lossless.)
