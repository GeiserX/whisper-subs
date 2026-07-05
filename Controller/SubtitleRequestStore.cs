using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperSubs.Controller
{
    /// <summary>Lifecycle state of a user subtitle request (#112).</summary>
    public enum RequestState
    {
        /// <summary>Awaiting admin approval — no CPU spent yet.</summary>
        Pending,
        /// <summary>Approved (or auto-approved) and placed on the priority queue.</summary>
        Queued,
        /// <summary>All requested items finished.</summary>
        Completed,
        /// <summary>Rejected by an admin.</summary>
        Declined,
        /// <summary>Enqueue/processing failed.</summary>
        Failed
    }

    /// <summary>Why a <see cref="SubtitleRequestStore.TryCreate"/> call did or didn't create a request.</summary>
    public enum RequestCreateOutcome
    {
        Created,
        DuplicateActive,
        QuotaExceeded,
        ActiveCapExceeded,
        GlobalCapExceeded
    }

    /// <summary>A single user request for subtitles on an item (leaf or container).</summary>
    public class SubtitleRequest
    {
        public string Id { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string Language { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";

        /// <summary>Assigned priority tier (the configured user tier). Persisted as its name.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PriorityTier Tier { get; set; } = PriorityTier.Medium;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RequestState State { get; set; } = RequestState.Pending;

        public long CreatedTicks { get; set; }
        public long UpdatedTicks { get; set; }

        /// <summary>Number of leaf media items this request expands to (1 for a film/episode).</summary>
        public int ItemCount { get; set; } = 1;
    }

    /// <summary>Result of attempting to create a request.</summary>
    public sealed class RequestCreateResult
    {
        public RequestCreateResult(RequestCreateOutcome outcome, SubtitleRequest? request)
        {
            Outcome = outcome;
            Request = request;
        }
        public RequestCreateOutcome Outcome { get; }
        public SubtitleRequest? Request { get; }
    }

    /// <summary>Pure, unit-tested request state-machine predicates (#112).</summary>
    public static class RequestPolicy
    {
        /// <summary>A request that still occupies quota/queue capacity (pending or queued).</summary>
        public static bool IsActive(RequestState state)
            => state == RequestState.Pending || state == RequestState.Queued;

        /// <summary>A request that has reached a final state.</summary>
        public static bool IsTerminal(RequestState state)
            => state == RequestState.Completed || state == RequestState.Declined || state == RequestState.Failed;
    }

    /// <summary>
    /// Persisted store of user subtitle requests (#112). Enforces per-user quota, per-user active cap,
    /// global cap and per-(user,item) de-dup at the single point that creates a request — the security-
    /// critical chokepoint. In-memory operations take explicit limits (no Plugin.Instance) so they are
    /// unit-testable; persistence is best-effort JSON (atomic temp+rename), mirroring the skip cache, and
    /// the store lazily loads from disk on first use (no startup hook required).
    /// </summary>
    public class SubtitleRequestStore
    {
        // Lazy<T> so concurrent first-time callers can't each construct a separate instance and split the
        // in-memory dedup/quota state this singleton exists to centralize.
        private static readonly Lazy<SubtitleRequestStore> _lazy = new(() => new SubtitleRequestStore());
        public static SubtitleRequestStore Instance => _lazy.Value;

        private readonly object _gate = new();
        private readonly List<SubtitleRequest> _requests = new();
        private bool _restored;

        // Terminal requests older than this are pruned on write to bound growth (kept well beyond any
        // realistic quota window so they never affect a live quota count).
        private const int TerminalRetentionDays = 30;

        internal SubtitleRequestStore() { }

        /// <summary>Count of active (pending + queued) requests across all users — for the global cap.</summary>
        public int CountActive()
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                return _requests.Count(r => RequestPolicy.IsActive(r.State));
            }
        }

        /// <summary>Count of a user's active (pending + queued) requests — for the per-user active cap.</summary>
        public int CountActiveForUser(string userId)
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                return _requests.Count(r => r.UserId == userId && RequestPolicy.IsActive(r.State));
            }
        }

        /// <summary>
        /// Attempts to create a request, enforcing de-dup → global cap → per-user active cap → rolling
        /// quota, in that order. Limits &lt;= 0 mean "unlimited" for that check. On success the request is
        /// added (state Queued when <paramref name="autoApprove"/>, else Pending) and persisted.
        /// </summary>
        public RequestCreateResult TryCreate(
            string itemId, string itemName, string itemType, string language,
            string userId, string userName, PriorityTier tier, int itemCount, bool autoApprove,
            long nowTicks, long windowTicks, int dailyQuota, int activeCap, int globalCap)
        {
            lock (_gate)
            {
                EnsureRestoredLocked();

                // De-dup: a user may not hold two active requests for the same item (idempotent).
                var existing = _requests.FirstOrDefault(r =>
                    r.UserId == userId && r.ItemId == itemId && RequestPolicy.IsActive(r.State));
                if (existing != null)
                {
                    return new RequestCreateResult(RequestCreateOutcome.DuplicateActive, existing);
                }

                if (globalCap > 0 && _requests.Count(r => RequestPolicy.IsActive(r.State)) >= globalCap)
                {
                    return new RequestCreateResult(RequestCreateOutcome.GlobalCapExceeded, null);
                }

                if (activeCap > 0 &&
                    _requests.Count(r => r.UserId == userId && RequestPolicy.IsActive(r.State)) >= activeCap)
                {
                    return new RequestCreateResult(RequestCreateOutcome.ActiveCapExceeded, null);
                }

                var userTicks = _requests.Where(r => r.UserId == userId).Select(r => r.CreatedTicks);
                var quota = PriorityScheduling.EvaluateQuota(userTicks, nowTicks, windowTicks, dailyQuota);
                if (!quota.Allowed)
                {
                    return new RequestCreateResult(RequestCreateOutcome.QuotaExceeded, null);
                }

                var request = new SubtitleRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ItemId = itemId,
                    ItemName = itemName,
                    ItemType = itemType,
                    Language = language,
                    UserId = userId,
                    UserName = userName,
                    Tier = tier,
                    State = autoApprove ? RequestState.Queued : RequestState.Pending,
                    CreatedTicks = nowTicks,
                    UpdatedTicks = nowTicks,
                    ItemCount = itemCount < 1 ? 1 : itemCount
                };
                _requests.Add(request);
                PruneLocked(nowTicks);
                Persist();
                return new RequestCreateResult(RequestCreateOutcome.Created, request);
            }
        }

        /// <summary>Transitions a request to a new state (fresh UpdatedTicks) and persists. Returns the request, or null if unknown.</summary>
        public SubtitleRequest? SetState(string id, RequestState state, long nowTicks)
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                var r = _requests.FirstOrDefault(x => x.Id == id);
                if (r == null) return null;
                r.State = state;
                r.UpdatedTicks = nowTicks;
                Persist();
                return r;
            }
        }

        /// <summary>
        /// Atomic compare-and-swap transition: moves a request from <paramref name="expected"/> to
        /// <paramref name="next"/> only if it is currently in <paramref name="expected"/>, all under the
        /// lock — so concurrent approve/decline calls can't both act on the same pending request. Returns
        /// false (with the current request in <paramref name="request"/>, or null if unknown) when the
        /// state didn't match.
        /// </summary>
        public bool TryTransition(string id, RequestState expected, RequestState next, long nowTicks, out SubtitleRequest? request)
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                request = _requests.FirstOrDefault(x => x.Id == id);
                if (request == null) return false;
                if (request.State != expected) return false;
                request.State = next;
                request.UpdatedTicks = nowTicks;
                Persist();
                return true;
            }
        }

        public SubtitleRequest? GetById(string id)
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                return _requests.FirstOrDefault(r => r.Id == id);
            }
        }

        /// <summary>All requests, newest first (admin view).</summary>
        public List<SubtitleRequest> GetAll()
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                return _requests.OrderByDescending(r => r.CreatedTicks).ToList();
            }
        }

        /// <summary>A single user's requests, newest first (their own view).</summary>
        public List<SubtitleRequest> GetByUser(string userId)
        {
            lock (_gate)
            {
                EnsureRestoredLocked();
                return _requests.Where(r => r.UserId == userId).OrderByDescending(r => r.CreatedTicks).ToList();
            }
        }

        // Drop terminal requests older than the retention window (keeps the file bounded without ever
        // touching an active request or a recent one that a live quota window still counts).
        private void PruneLocked(long nowTicks)
        {
            long cutoff = nowTicks - TimeSpan.FromDays(TerminalRetentionDays).Ticks;
            _requests.RemoveAll(r => RequestPolicy.IsTerminal(r.State) && r.UpdatedTicks < cutoff);
        }

        // ── persistence (best-effort JSON; excluded from coverage like the queue/skip-cache) ──

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance for data path")]
        private static string RequestsFilePath
        {
            get
            {
                var dir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(dir)) return "";
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "requests.json");
            }
        }

        // Lazily loads the persisted store on first access — callers already hold _gate. Skipped entirely
        // when there is no data path (unit tests), so the in-memory logic stays testable without disk.
        [ExcludeFromCodeCoverage(Justification = "Reads request store from disk; no-op without a data path")]
        private void EnsureRestoredLocked()
        {
            if (_restored) return;
            _restored = true;

            var path = RequestsFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<List<SubtitleRequest>>(File.ReadAllText(path));
                if (loaded != null)
                {
                    _requests.Clear();
                    _requests.AddRange(loaded);
                }
            }
            catch
            {
                // Corrupt/unreadable store must never break a request path — start empty.
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Writes request store to disk")]
        private void Persist()
        {
            var path = RequestsFilePath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var json = JsonSerializer.Serialize(_requests);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // Best effort — a failed persist must not throw into a request path.
            }
        }
    }
}
