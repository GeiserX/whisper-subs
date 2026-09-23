using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// The whisper language probe's result per item, persisted next to the skip cache, so the scheduled
    /// sweep can tell whether untagged audio is English without probing it again every night. The
    /// translation pass records a result after it probes; the sweep reads it.
    ///
    /// An entry is keyed by the media file's size and last-write time, not the item's change token: the
    /// pass ends with <c>RefreshMetadata</c>, which re-saves the item, so a token taken when the probe ran
    /// would already be stale on the next run and every untagged title would be probed again. The audio
    /// language only changes when the file does. It is also kept apart from the skip cache's entries,
    /// which are dropped on every settings change and whenever an item reads as incomplete.
    /// </summary>
    public sealed class AudioProbeCache
    {
        /// <summary>Bumped when the on-disk shape changes so an old file is discarded, not mis-parsed.</summary>
        internal const int CurrentVersion = 1;

        /// <summary>One remembered probe result and the file it was measured on.</summary>
        public sealed class Entry
        {
            public long FileLength { get; set; }
            public long FileLastWriteTicks { get; set; }
            public string Language { get; set; } = "";
            public float Probability { get; set; }
        }

        private sealed class Model
        {
            public int Version { get; set; }
            public Dictionary<string, Entry> Entries { get; set; } = new();
        }

        private readonly object _gate = new();
        private readonly Dictionary<Guid, Entry> _entries;
        private readonly string _path;

        private AudioProbeCache(Dictionary<Guid, Entry> entries, string path)
        {
            _entries = entries;
            _path = path;
        }

        private static readonly Lazy<AudioProbeCache> SharedInstance = new(LoadShared);

        /// <summary>The process-wide cache, loaded on first use. In-memory only when the plugin has no data folder.</summary>
        public static AudioProbeCache Shared => SharedInstance.Value;

        /// <summary>Number of remembered items.</summary>
        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>An empty cache that never persists. For tests.</summary>
        internal static AudioProbeCache InMemory() => new(new Dictionary<Guid, Entry>(), string.Empty);

        /// <summary>
        /// The remembered probe for this item, or null when there is none or the file changed since it was
        /// measured (<paramref name="identity"/> null means the file could not be read: no answer).
        /// </summary>
        public (string Language, float Probability)? TryGet(Guid id, (long Length, long LastWriteTicks)? identity)
        {
            if (identity is not { } file) return null;
            lock (_gate)
            {
                if (!_entries.TryGetValue(id, out var e)) return null;
                if (e.FileLength != file.Length || e.FileLastWriteTicks != file.LastWriteTicks) return null;
                return (e.Language, e.Probability);
            }
        }

        /// <summary>Remembers a probe result for this item's current file and persists the cache.</summary>
        public void Record(Guid id, (long Length, long LastWriteTicks)? identity, string language, float probability, ILogger? logger = null)
        {
            if (identity is not { } file || string.IsNullOrWhiteSpace(language)) return;
            lock (_gate)
            {
                _entries[id] = new Entry
                {
                    FileLength = file.Length,
                    FileLastWriteTicks = file.LastWriteTicks,
                    Language = language.Trim().ToLowerInvariant(),
                    Probability = probability,
                };
                SaveLocked(logger);
            }
        }

        /// <summary>Drops entries for items the sweep no longer enumerates, then persists.</summary>
        public int PruneTo(ICollection<Guid> keepIds, ILogger? logger = null)
        {
            lock (_gate)
            {
                var stale = _entries.Keys.Where(k => !keepIds.Contains(k)).ToList();
                foreach (var k in stale) _entries.Remove(k);
                if (stale.Count > 0) SaveLocked(logger);
                return stale.Count;
            }
        }

        // ── Serialization (unit-tested) ─────────────────────────────────────────

        internal string ToJson()
        {
            lock (_gate)
            {
                return JsonSerializer.Serialize(new Model
                {
                    Version = CurrentVersion,
                    Entries = _entries.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value),
                });
            }
        }

        /// <summary>Parses persisted json. A version mismatch or any corruption yields an empty cache.</summary>
        internal static AudioProbeCache FromJson(string? json, string path = "")
        {
            var dict = new Dictionary<Guid, Entry>();
            if (string.IsNullOrWhiteSpace(json)) return new AudioProbeCache(dict, path);
            try
            {
                var model = JsonSerializer.Deserialize<Model>(json);
                if (model == null || model.Version != CurrentVersion) return new AudioProbeCache(dict, path);
                foreach (var kv in model.Entries)
                {
                    if (Guid.TryParse(kv.Key, out var id) && kv.Value != null && !string.IsNullOrWhiteSpace(kv.Value.Language))
                    {
                        dict[id] = kv.Value;
                    }
                }
            }
            catch
            {
                dict.Clear();
            }
            return new AudioProbeCache(dict, path);
        }

        // ── Persistence I/O ─────────────────────────────────────────────────────

        /// <summary>The media file's size and last-write time, or null when it cannot be read.</summary>
        [ExcludeFromCodeCoverage(Justification = "Filesystem stat")]
        public static (long Length, long LastWriteTicks)? IdentityOf(string? mediaPath)
        {
            try
            {
                if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath)) return null;
                var info = new FileInfo(mediaPath);
                return (info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                return null;
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance and the plugin data dir")]
        private static AudioProbeCache LoadShared()
        {
            var dir = Plugin.Instance?.DataFolderPath ?? string.Empty;
            var path = string.IsNullOrEmpty(dir) ? string.Empty : Path.Combine(dir, "audio-probe-cache.json");
            try
            {
                return FromJson(path.Length > 0 && File.Exists(path) ? File.ReadAllText(path) : null, path);
            }
            catch
            {
                return FromJson(null, path);
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Atomic filesystem write of the plugin data dir")]
        private void SaveLocked(ILogger? logger)
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Model
                {
                    Version = CurrentVersion,
                    Entries = _entries.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value),
                }));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                try { File.Delete(_path + ".tmp"); } catch { /* ignore */ }
                logger?.LogDebug(ex, "WhisperSubs: could not persist the audio probe cache at {Path}", _path);
            }
        }
    }
}
