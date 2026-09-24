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

        /// <summary>The known dialect for <paramref name="value"/>, or <see cref="OpenAi"/> when blank or unknown.</summary>
        public static string Normalize(string? value)
            => string.Equals(value?.Trim(), CrispAsr, StringComparison.OrdinalIgnoreCase) ? CrispAsr : OpenAi;

        /// <summary>True when <paramref name="value"/> is blank or one of the two dialects.</summary>
        public static bool IsKnown(string? value)
            => string.IsNullOrWhiteSpace(value)
               || string.Equals(value.Trim(), OpenAi, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value.Trim(), CrispAsr, StringComparison.OrdinalIgnoreCase);
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
        /// this server's install while the pool would hold this server (<paramref name="hostsLocal"/>), or an
        /// enabled row with a URL that lists the target. The same rows the registry builds from. Pure.
        /// </summary>
        public static bool ServedByConfig(string target, bool canaryInstalled, bool hostsLocal, IEnumerable<WhisperWorker>? rows)
            => (canaryInstalled && hostsLocal)
               || (rows ?? Enumerable.Empty<WhisperWorker>()).Any(w => w.Enabled && !string.IsNullOrWhiteSpace(w.ApiUrl)
                                                                     && ForRow(w).Contains(target));

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

        /// <summary>
        /// A translate job, whose one target is all it makes: hand its own slot back, then wait for a worker
        /// that lists the target. It holds nothing while it waits, so it cannot be part of a wait cycle, and
        /// a busy target worker delays the job instead of failing it.
        /// </summary>
        ReleaseOwnAndWait,
    }

    /// <summary>Pure routing rule for a Canary target inside an item that already holds a worker slot.</summary>
    public static class TargetLeaseRouting
    {
        /// <summary>
        /// Waiting for a second worker while holding a slot is safe only when the waiting item holds a
        /// worker that serves no Canary target: then every worker a waiter needs is held by an item that
        /// never waits (it serves its targets itself) or by a single-target job, so each wait ends. Two
        /// items on workers with different partial target sets could otherwise each wait for the other's
        /// worker forever, so that case only takes a worker that is free right now. A job that can give its
        /// own slot back first (<paramref name="canReleaseOwn"/>: a translate job, which needs its worker for
        /// nothing but the target) always waits, because it waits holding nothing.
        /// </summary>
        public static TargetLeasePolicy Decide(IReadOnlySet<string> ownTargets, string target, bool canReleaseOwn = false)
        {
            if (ownTargets.Contains(target)) return TargetLeasePolicy.UseOwnWorker;
            if (canReleaseOwn) return TargetLeasePolicy.ReleaseOwnAndWait;
            return ownTargets.Any(t => !string.Equals(t, "en", StringComparison.OrdinalIgnoreCase))
                ? TargetLeasePolicy.TakeFreeAnotherOnly
                : TargetLeasePolicy.WaitForAnother;
        }
    }
}
