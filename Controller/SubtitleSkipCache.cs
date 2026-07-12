using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using WhisperSubs.Configuration;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Persisted per-item "already satisfied" cache (issue #110). Lets a scheduled run skip the
    /// expensive per-item filesystem/stream probe for items that were already complete on a previous
    /// run AND have not changed since — turning a repeat full-library scan from tens of minutes into
    /// seconds.
    ///
    /// Correctness is preserved without a blind time cache:
    /// <list type="bullet">
    /// <item>A CHANGE TOKEN — Jellyfin's <c>DateLastSaved</c>, which advances whenever an item's
    /// metadata/subtitles are re-saved (including after this plugin's own <c>RefreshMetadata</c>) —
    /// so any content change re-checks the item.</item>
    /// <item>A SETTINGS SIGNATURE — a hash of every toggle that affects the skip verdict — so flipping
    /// a mode/translation/forced/image option discards the whole cache (guards the #82/#83 invariants).</item>
    /// <item>A backstop TTL — re-verify after N days even if unchanged — to catch the one case the
    /// token cannot see: a subtitle deleted outside Jellyfin before any library scan.</item>
    /// </list>
    /// A miss NEVER defaults to "skip": it falls back to the full per-item evaluation, preserving the
    /// #82 fail-safe bias toward generating. Persisted as JSON under the plugin data folder (the
    /// <c>queue.json</c> pattern), written atomically.
    /// </summary>
    public sealed class SubtitleSkipCache
    {
        /// <summary>Bumped when the on-disk shape changes so an old file is discarded, not mis-parsed.</summary>
        internal const int CurrentVersion = 1;

        /// <summary>A remembered per-item verdict.</summary>
        public sealed class Entry
        {
            /// <summary>The item's change token when it was recorded (<c>DateLastSaved.Ticks</c>).</summary>
            public long Token { get; set; }

            public bool Full { get; set; }
            public bool Forced { get; set; }
            public bool Translated { get; set; }

            /// <summary>UtcNow ticks when recorded, for the backstop TTL.</summary>
            public long CachedAtTicks { get; set; }
        }

        private sealed class Model
        {
            public int Version { get; set; }
            public string Signature { get; set; } = "";
            public Dictionary<string, Entry> Entries { get; set; } = new();
        }

        private readonly Dictionary<Guid, Entry> _entries;

        private SubtitleSkipCache(Dictionary<Guid, Entry> entries) => _entries = entries;

        /// <summary>Number of remembered items.</summary>
        public int Count => _entries.Count;

        // ── Pure decision helpers (unit-tested) ─────────────────────────────────

        /// <summary>
        /// Signature of every config toggle that changes the skip verdict. When it differs from the
        /// persisted one, the whole cache is discarded on load, so a mode/translation/forced/image,
        /// language, or subtitle-naming (template/label) change never reuses a stale "complete" verdict.
        /// The template/label are included because they change which filenames count as plugin-owned,
        /// hence which items read as already-satisfied. (Issue #110; guards #82/#83.)
        /// </summary>
        public static string ComputeSignature(PluginConfiguration c) => string.Join(
            "|",
            "v" + CurrentVersion,
            (int)c.SubtitleMode,
            c.EnableTranslation ? 1 : 0,
            c.GenerateOriginalLanguageSubtitles ? 1 : 0,
            c.SkipIfSubtitleExists ? 1 : 0,
            c.IgnoreForcedSubtitles ? 1 : 0,
            c.CountImageSubtitlesAsPresent ? 1 : 0,
            c.EnableLyricsGeneration ? 1 : 0,
            c.DefaultLanguage ?? string.Empty,
            c.SubtitleFilenameTemplate ?? string.Empty,
            c.SubtitleLabel ?? string.Empty);

        /// <summary>
        /// True when a cached entry lets us skip the probe for this item now: the entry exists, its
        /// change token matches the item's current token, and — when a positive backstop is set — it
        /// was recorded within that window. The caller has already ensured the whole-cache signature
        /// matched (a mismatch yields an empty cache at load time).
        /// </summary>
        public static bool CanSkip(Entry? entry, long currentToken, long nowTicks, int backstopDays)
        {
            if (entry == null) return false;
            if (entry.Token != currentToken) return false;
            if (backstopDays > 0)
            {
                var age = nowTicks - entry.CachedAtTicks;
                // Guard TimeSpan.FromDays overflow on an absurd config value — TimeSpan maxes at
                // ~10.7M days. Without this, a huge SkipCacheExpiryDays would throw here and, since the
                // task loop's try has only a finally, fail the whole scheduled run. Treat it as "never
                // expires on time" (the change token still invalidates).
                var maxAgeTicks = backstopDays >= 10_000_000 ? long.MaxValue : TimeSpan.FromDays(backstopDays).Ticks;
                if (age < 0 || age > maxAgeTicks) return false;
            }
            return true;
        }

        // ── Mutation (unit-tested) ──────────────────────────────────────────────

        public Entry? TryGet(Guid id) => _entries.TryGetValue(id, out var e) ? e : null;

        public void Record(Guid id, Entry entry) => _entries[id] = entry;

        public void Remove(Guid id) => _entries.Remove(id);

        /// <summary>
        /// Drops entries whose item id was not enumerated this run (deleted / no longer a candidate),
        /// bounding growth. Pruning to the ENUMERATED set (not the reached set) is what makes an
        /// interrupted run safe to persist — items not yet reached keep their prior entry.
        /// </summary>
        public int PruneTo(ICollection<Guid> keepIds)
        {
            var stale = _entries.Keys.Where(k => !keepIds.Contains(k)).ToList();
            foreach (var k in stale) _entries.Remove(k);
            return stale.Count;
        }

        // ── Serialization (unit-tested) ─────────────────────────────────────────

        internal static SubtitleSkipCache Empty() => new(new Dictionary<Guid, Entry>());

        internal string ToJson(string signature) => JsonSerializer.Serialize(new Model
        {
            Version = CurrentVersion,
            Signature = signature,
            Entries = _entries.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value)
        });

        /// <summary>
        /// Parses persisted json into a cache. A version/signature mismatch or ANY corruption yields
        /// an empty cache (self-healing → the run just does a full scan and repopulates).
        /// </summary>
        internal static SubtitleSkipCache FromJson(string? json, string expectedSignature)
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty();
            try
            {
                var model = JsonSerializer.Deserialize<Model>(json);
                if (model == null || model.Version != CurrentVersion || model.Signature != expectedSignature)
                {
                    return Empty();
                }

                var dict = new Dictionary<Guid, Entry>();
                foreach (var kv in model.Entries)
                {
                    if (Guid.TryParse(kv.Key, out var id) && kv.Value != null)
                    {
                        dict[id] = kv.Value;
                    }
                }
                return new SubtitleSkipCache(dict);
            }
            catch
            {
                return Empty();
            }
        }

        // ── Persistence I/O ─────────────────────────────────────────────────────

        [ExcludeFromCodeCoverage(Justification = "Filesystem read of the plugin data dir")]
        public static SubtitleSkipCache Load(string path, string signature, ILogger? logger = null)
        {
            try
            {
                return File.Exists(path) ? FromJson(File.ReadAllText(path), signature) : Empty();
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "WhisperSubs: could not read skip cache at {Path}; starting fresh", path);
                return Empty();
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Atomic filesystem write of the plugin data dir")]
        public void Save(string path, string signature, ILogger? logger = null)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // temp + atomic rename so a process kill mid-write can't leave a truncated cache.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, ToJson(signature));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup of a stray temp file (e.g. a cross-device rename fallback on a
                // union filesystem); the old cache is left intact and Load never reads .tmp.
                try { File.Delete(path + ".tmp"); } catch { /* ignore */ }
                logger?.LogDebug(ex, "WhisperSubs: could not persist skip cache at {Path}", path);
            }
        }

        /// <summary>Default cache location under the plugin data folder (beside <c>queue.json</c>).</summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance (Jellyfin runtime)")]
        public static string DefaultPath()
        {
            var dir = Plugin.Instance?.DataFolderPath ?? string.Empty;
            return string.IsNullOrEmpty(dir) ? string.Empty : Path.Combine(dir, "skip-cache.json");
        }
    }
}
