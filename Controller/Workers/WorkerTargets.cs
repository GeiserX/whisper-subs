using System;
using System.Collections.Generic;
using System.Linq;
using WhisperSubs.Configuration;
using WhisperSubs.Setup;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// The HTTP dialect a remote worker speaks. <see cref="OpenAi"/> is every OpenAI-compatible server
    /// (whisper-server, OpenAI, Groq): English translation goes to <c>/v1/audio/translations</c>.
    /// <see cref="CrispAsr"/> is a <c>crispasr --server</c>: it has no translations route, so every request
    /// goes to <c>/v1/audio/transcriptions</c> and translation is expressed with form fields.
    /// </summary>
    public static class WorkerDialect
    {
        public const string OpenAi = "openai";
        public const string CrispAsr = "crispasr";

        /// <summary>
        /// A <c>crispasr --server</c> running Qwen3-ASR: the OpenAI transcription route, no translation at
        /// all, cue timing from the word timestamps in its <c>verbose_json</c>, and no language detection
        /// of its own (this server's Whisper detects for it). A transcribe-only worker by construction.
        /// </summary>
        public const string CrispAsrQwen3 = "crispasr-qwen3";

        /// <summary>
        /// An akou server: each request is a file job on its own queue at a low priority, so its other
        /// clients go first, deleted once the answer is read. Its <c>best</c> preset is Qwen3-ASR without word
        /// times, so like <see cref="CrispAsrQwen3"/> it only transcribes and this server's Whisper detects
        /// and translates for it.
        /// </summary>
        public const string Akou = "akou";

        /// <summary>
        /// A transcribe-only dialect whose row borrows this server's Whisper for language detection and
        /// English translation (<see cref="Providers.HostAssistedProvider"/>).
        /// </summary>
        public static bool IsHostAssisted(string? value)
        {
            var d = Normalize(value);
            return d == CrispAsrQwen3 || d == Akou;
        }

        /// <summary>The known dialect for <paramref name="value"/>, or <see cref="OpenAi"/> when blank or unknown.</summary>
        public static string Normalize(string? value)
        {
            var v = value?.Trim();
            if (string.Equals(v, CrispAsr, StringComparison.OrdinalIgnoreCase)) return CrispAsr;
            if (string.Equals(v, CrispAsrQwen3, StringComparison.OrdinalIgnoreCase)) return CrispAsrQwen3;
            if (string.Equals(v, Akou, StringComparison.OrdinalIgnoreCase)) return Akou;
            return OpenAi;
        }

        /// <summary>True when <paramref name="value"/> is blank or one of the dialects.</summary>
        public static bool IsKnown(string? value)
            => string.IsNullOrWhiteSpace(value)
               || string.Equals(value.Trim(), OpenAi, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value.Trim(), CrispAsr, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value.Trim(), CrispAsrQwen3, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value.Trim(), Akou, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pure helpers for the languages a worker can translate into. "en" is Whisper's translate task; every
    /// other code is a Canary target from <see cref="CanaryCatalog.Targets"/>.
    /// </summary>
    public static class WorkerTargets
    {
        /// <summary>The one target every translate-capable Whisper worker serves.</summary>
        public static readonly IReadOnlySet<string> EnglishOnly = Set("en");

        /// <summary>A worker that cannot translate at all.</summary>
        public static readonly IReadOnlySet<string> None = Set();

        /// <summary>A case-insensitive target set.</summary>
        public static IReadOnlySet<string> Set(params string[] codes)
            => new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="code"/> is "en" or a Canary target.</summary>
        public static bool IsValidTarget(string? code)
            => string.Equals(code?.Trim(), "en", StringComparison.OrdinalIgnoreCase) || CanaryCatalog.IsTarget(code);

        /// <summary>
        /// Splits the config page's comma-separated field into trimmed, lower-case, de-duplicated codes in
        /// input order. Validation is separate (<see cref="WorkerConfigValidation.Validate"/>).
        /// </summary>
        public static List<string> Parse(string? commaSeparated)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commaSeparated)) return result;
            foreach (var raw in commaSeparated.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var code = raw.Trim().ToLowerInvariant();
                if (code.Length > 0 && !result.Contains(code)) result.Add(code);
            }
            return result;
        }

        /// <summary>
        /// The targets a configured row declares, with back-compat for rows saved before the field existed:
        /// when <see cref="WhisperWorker.TranslateTargets"/> is empty (which is how an absent element
        /// deserializes), the old <see cref="WhisperWorker.CanTranslate"/> flag maps to <c>{"en"}</c> (true)
        /// or nothing (false).
        /// Unrecognised codes are dropped, and so is every non-English code on an OpenAI-dialect row,
        /// because that dialect can only translate into English: the pool must never route a Canary target
        /// to a worker that would answer it with an English subtitle.
        /// </summary>
        public static IReadOnlySet<string> ForRow(WhisperWorker row)
        {
            // A Qwen3-ASR or akou server translates nothing, whatever the row's targets say.
            if (WorkerDialect.IsHostAssisted(row.Dialect)) return None;
            if (row.TranslateTargets == null || row.TranslateTargets.Count == 0) return row.CanTranslate ? EnglishOnly : None;

            var crisp = WorkerDialect.Normalize(row.Dialect) == WorkerDialect.CrispAsr;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in row.TranslateTargets)
            {
                var code = (raw ?? "").Trim().ToLowerInvariant();
                if (!IsValidTarget(code)) continue;
                if (code != "en" && !crisp) continue;
                set.Add(code);
            }
            return set;
        }

        /// <summary>
        /// Whether a remote row takes whole-item jobs. An OpenAI-dialect row always does. A CrispASR row does
        /// only when its targets list "en": without it the row is there for the extra languages alone, and a
        /// server started with Canary would otherwise transcribe titles with Canary, whatever the translation
        /// setting.
        /// </summary>
        public static bool TranscribesItems(string? dialect, IReadOnlySet<string> targets)
            => WorkerDialect.Normalize(dialect) != WorkerDialect.CrispAsr || targets.Contains("en");

        /// <summary>
        /// The host's own worker: English through whisper-cli, plus all 24 Canary targets while the crispasr
        /// binary and the Canary model are installed. Not limited to the targets ticked for the nightly run:
        /// per-title translation needs every target with that list empty, and the nightly run only ever asks
        /// about the ticked ones.
        /// </summary>
        public static IReadOnlySet<string> ForLocal(bool canaryInstalled)
            => canaryInstalled
                ? Set(new[] { "en" }.Concat(CanaryCatalog.Targets.Select(t => t.Code)).ToArray())
                : EnglishOnly;

        /// <summary>
        /// Whether the configuration, read without a running pool, gives <paramref name="target"/> an engine:
        /// this server's own worker while the pool would hold it (<paramref name="hostsLocal"/>), which makes
        /// what <see cref="ForLocal"/> lists (English only while its Whisper model can translate,
        /// <paramref name="localWhisperTranslates"/>); the single legacy remote server, which makes English;
        /// or an enabled row with a URL that lists the target. The same rows the registry builds from. Pure.
        /// </summary>
        public static bool ServedByConfig(string target, bool canaryInstalled, bool hostsLocal, IEnumerable<WhisperWorker>? rows,
            bool legacyRemote = false, bool localWhisperTranslates = true)
        {
            var english = string.Equals(target, "en", StringComparison.OrdinalIgnoreCase);
            return (hostsLocal && ForLocal(canaryInstalled).Contains(target) && (!english || localWhisperTranslates))
                   || (english && legacyRemote)
                   || (rows ?? Enumerable.Empty<WhisperWorker>()).Any(w => w.Enabled && !string.IsNullOrWhiteSpace(w.ApiUrl)
                                                                        && ForRow(w).Contains(target));
        }

        /// <summary>
        /// A stable text form of a target set for the worker signature: sorted, comma-joined.
        /// </summary>
        public static string Signature(IReadOnlySet<string> targets)
            => string.Join(",", targets.Select(t => t.ToLowerInvariant()).OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>How the translation pass gets a worker for one Canary target while an item holds a lease.</summary>
    public enum TargetLeasePolicy
    {
        /// <summary>The item's own worker serves the target: use it, no second slot.</summary>
        UseOwnWorker,

        /// <summary>The item's worker serves no Canary target at all: wait for a worker that does.</summary>
        WaitForAnother,

        /// <summary>
        /// The item's worker serves some Canary targets but not this one: take a free capable worker if one
        /// is free right now, never wait.
        /// </summary>
        TakeFreeAnotherOnly,
    }

    /// <summary>Pure routing rule for a Canary target inside an item that already holds a worker slot.</summary>
    public static class TargetLeaseRouting
    {
        /// <summary>
        /// Waiting for a second worker while holding a slot is safe only when the waiting item holds a
        /// worker that serves no Canary target: then every worker a waiter needs is held by an item that
        /// never waits (it serves its targets itself) or by a single-target job, so each wait ends. Two
        /// items on workers with different partial target sets could otherwise each wait for the other's
        /// worker forever, so that case only takes a worker that is free right now. A translate job is placed
        /// on a worker that lists its target (<see cref="DispatchPlacement"/>), so it takes the first branch.
        /// </summary>
        public static TargetLeasePolicy Decide(IReadOnlySet<string> ownTargets, string target)
        {
            if (ownTargets.Contains(target)) return TargetLeasePolicy.UseOwnWorker;
            return ownTargets.Any(t => !string.Equals(t, "en", StringComparison.OrdinalIgnoreCase))
                ? TargetLeasePolicy.TakeFreeAnotherOnly
                : TargetLeasePolicy.WaitForAnother;
        }
    }
}
