using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperSubs.Controller
{
    /// <summary>Outcome of enqueuing a key into <see cref="PriorityLanes{T}"/>.</summary>
    public enum LaneEnqueueOutcome
    {
        /// <summary>A brand-new key was added.</summary>
        Added,
        /// <summary>The key already existed and was moved to a higher-priority (lower) tier.</summary>
        Promoted,
        /// <summary>The key already existed at an equal-or-higher priority; only an optional value merge ran.</summary>
        Duplicate
    }

    /// <summary>
    /// A thread-safe multi-lane priority queue: one FIFO lane per integer tier, dequeued
    /// lowest-tier-first (lower int = higher priority, see <see cref="PriorityTier"/>), FIFO within a
    /// tier. Every entry has a unique string key, so enqueuing an existing key never duplicates it — it
    /// promotes the entry to the better tier when the incoming tier is higher priority, and can merge
    /// the stored value via a caller-supplied function. This is the pure ordering + de-dup engine behind
    /// the subtitle queue, deliberately free of Jellyfin types so it is fully unit-testable. (Issue #112.)
    /// </summary>
    internal sealed class PriorityLanes<T>
    {
        private readonly object _gate = new();

        // SortedDictionary keeps lanes ordered by tier ascending, so the first non-empty lane is the
        // highest priority. Each lane is a LinkedList whose node order IS the FIFO order for that tier.
        private readonly SortedDictionary<int, LinkedList<Entry>> _lanes = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _index = new();

        private sealed class Entry
        {
            public required string Key { get; init; }
            public int Tier { get; set; }
            public required T Value { get; set; }
        }

        /// <summary>Number of queued entries.</summary>
        public int Count
        {
            get { lock (_gate) { return _index.Count; } }
        }

        /// <summary>True when no entries are queued.</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>Whether the key is currently queued.</summary>
        public bool ContainsKey(string key)
        {
            lock (_gate) { return _index.ContainsKey(key); }
        }

        /// <summary>
        /// Adds <paramref name="key"/> at <paramref name="tier"/>. If the key already exists it is NOT
        /// duplicated: <paramref name="mergeValue"/>(existing, incoming) — when supplied — replaces the
        /// stored value, and the entry is promoted to <paramref name="tier"/> when that is higher
        /// priority (a strictly lower number). A promoted entry moves to the back of the target lane
        /// (FIFO among its new peers).
        /// </summary>
        public LaneEnqueueOutcome Enqueue(string key, int tier, T value, Func<T, T, T>? mergeValue = null)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var existingNode))
                {
                    var entry = existingNode.Value;
                    if (mergeValue != null)
                    {
                        entry.Value = mergeValue(entry.Value, value);
                    }

                    if (tier < entry.Tier)
                    {
                        RemoveNode(existingNode);
                        AddEntry(new Entry { Key = key, Tier = tier, Value = entry.Value });
                        return LaneEnqueueOutcome.Promoted;
                    }

                    return LaneEnqueueOutcome.Duplicate;
                }

                AddEntry(new Entry { Key = key, Tier = tier, Value = value });
                return LaneEnqueueOutcome.Added;
            }
        }

        /// <summary>Removes and returns the highest-priority entry (lowest tier, then FIFO within it).</summary>
        public bool TryDequeue(out T value)
        {
            lock (_gate)
            {
                foreach (var lane in _lanes.Values)
                {
                    var node = lane.First;
                    if (node != null)
                    {
                        value = node.Value.Value;
                        RemoveNode(node);
                        return true;
                    }
                }

                value = default!;
                return false;
            }
        }

        /// <summary>
        /// Removes and returns the highest-priority entry whose value satisfies <paramref name="canDequeue"/>,
        /// scanning lanes in priority order then FIFO within a lane. Strict tier order is preserved: a
        /// lower-priority entry is returned only when no acceptable higher-priority one exists. Used by the
        /// worker-pool dispatcher to skip a head job that no currently-free worker can serve (v4.0).
        /// </summary>
        public bool TryDequeue(Func<T, bool> canDequeue, out T value)
        {
            lock (_gate)
            {
                foreach (var lane in _lanes.Values)
                {
                    for (var node = lane.First; node != null; node = node.Next)
                    {
                        if (canDequeue(node.Value.Value))
                        {
                            value = node.Value.Value;
                            RemoveNode(node);
                            return true;
                        }
                    }
                }

                value = default!;
                return false;
            }
        }

        /// <summary>Removes a specific key if queued. Returns true if it was present.</summary>
        public bool Remove(string key)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    RemoveNode(node);
                    return true;
                }
                return false;
            }
        }

        /// <summary>All entries in dequeue order (highest priority first) — used for persistence.</summary>
        public List<(string Key, int Tier, T Value)> Snapshot()
        {
            lock (_gate)
            {
                var result = new List<(string, int, T)>(_index.Count);
                foreach (var lane in _lanes.Values)
                {
                    foreach (var e in lane)
                    {
                        result.Add((e.Key, e.Tier, e.Value));
                    }
                }
                return result;
            }
        }

        /// <summary>Number of queued entries per tier (only tiers with at least one entry).</summary>
        public Dictionary<int, int> CountsByTier()
        {
            lock (_gate)
            {
                return _lanes.Where(kv => kv.Value.Count > 0)
                             .ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            }
        }

        // ── helpers (all callers already hold _gate) ──

        private void AddEntry(Entry entry)
        {
            if (!_lanes.TryGetValue(entry.Tier, out var lane))
            {
                lane = new LinkedList<Entry>();
                _lanes[entry.Tier] = lane;
            }
            _index[entry.Key] = lane.AddLast(entry);
        }

        private void RemoveNode(LinkedListNode<Entry> node)
        {
            var entry = node.Value;
            _index.Remove(entry.Key);
            if (_lanes.TryGetValue(entry.Tier, out var lane))
            {
                lane.Remove(node);
                if (lane.Count == 0)
                {
                    _lanes.Remove(entry.Tier);
                }
            }
        }
    }
}
