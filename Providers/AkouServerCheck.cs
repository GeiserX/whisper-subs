using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WhisperSubs.Providers
{
    /// <summary>What the akou check found: the server's version and presets, and what stops it from working.</summary>
    internal sealed record AkouCheckResult(
        bool Ok,
        string? Version,
        IReadOnlyList<string> Presets,
        bool BestAvailable,
        IReadOnlyList<string> BoundLanguages,
        int? QueueDepth,
        IReadOnlyList<string> Problems)
    {
        /// <summary>One line for the page: what was found when it works, every problem when it does not.</summary>
        public string Message => Ok
            ? $"akou {Version}: takes jobs, best is installed" + (QueueDepth is { } depth ? $", {depth} job(s) queued." : ".")
            : string.Join(" ", Problems);
    }

    /// <summary>
    /// Reads akou's two answers the Test button asks for, <c>GET /v1/server</c> (no key) and
    /// <c>GET /v1/keys/me</c> (with the key), into plain problems an admin can act on. Pure: the HTTP calls
    /// are the controller's.
    /// </summary>
    internal static class AkouServerCheck
    {
        internal const string KeyCommand = "akou keys create --name whisper-subs --scope jobs";

        /// <summary>
        /// The verdict over the two answers. <paramref name="keyStatus"/> is null when no key was given, so
        /// <c>/v1/keys/me</c> was not called. Pure.
        /// </summary>
        internal static AkouCheckResult Evaluate(int serverStatus, string? serverBody, int? keyStatus, string? keyBody)
        {
            var problems = new List<string>();
            string? version = null;
            var presets = new List<string>();
            var best = false;
            var bound = new List<string>();
            int? depth = null;

            if (serverStatus == 403 && ErrorCode(serverBody) == "bad_host")
            {
                problems.Add(BadHost);
                return new AkouCheckResult(false, null, presets, false, bound, null, problems);
            }
            if (serverStatus != 200)
            {
                problems.Add($"GET /v1/server answered HTTP {serverStatus}: this does not look like an akou server.");
                return new AkouCheckResult(false, null, presets, false, bound, null, problems);
            }

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(serverBody ?? "");
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                problems.Add(NotAkou);
                return new AkouCheckResult(false, null, presets, false, bound, null, problems);
            }
            if (root.ValueKind != JsonValueKind.Object || Text(root, "name") != "akou")
            {
                problems.Add(NotAkou);
                return new AkouCheckResult(false, null, presets, false, bound, null, problems);
            }

            version = Text(root, "version");
            if (root.TryGetProperty("presets", out var presetArray) && presetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var preset in presetArray.EnumerateArray())
                {
                    if (preset.ValueKind != JsonValueKind.Object) continue;
                    var name = Text(preset, "name");
                    var available = preset.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;
                    if (name == null || !available) continue;
                    presets.Add(name);
                    if (name == "best") best = true;
                }
            }
            if (root.TryGetProperty("bound_languages", out var languages) && languages.ValueKind == JsonValueKind.Array)
            {
                bound.AddRange(languages.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!));
            }
            if (root.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Object
                && queue.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n))
            {
                depth = n;
            }

            var jobs = root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty("jobs", out var j) && j.ValueKind == JsonValueKind.True;
            if (!jobs)
            {
                problems.Add("This akou takes no file jobs (capabilities.jobs is false): run it as a server, `akou serve`.");
            }
            if (!best)
            {
                problems.Add("The best preset (Qwen3-ASR) is not installed on this akou: run `akou models pull best` on it.");
            }

            switch (keyStatus)
            {
                case null:
                    problems.Add($"No key: create one on the akou server with `{KeyCommand}` and paste it here.");
                    break;
                case 200:
                    if (!HasJobsScope(keyBody))
                    {
                        problems.Add($"The key lacks the jobs scope: create one with `{KeyCommand}`.");
                    }
                    break;
                case 401:
                    problems.Add($"akou refused the key (401): check it, or create a new one with `{KeyCommand}`.");
                    break;
                case 403 when ErrorCode(keyBody) == "bad_host":
                    problems.Add(BadHost);
                    break;
                default:
                    problems.Add($"GET /v1/keys/me answered HTTP {keyStatus}.");
                    break;
            }

            return new AkouCheckResult(problems.Count == 0, version, presets, best, bound, depth, problems);
        }

        private const string NotAkou = "This is not an akou server: GET /v1/server did not answer as akou.";

        private const string BadHost = "akou refused the host name in this URL (403 bad_host): when its server.public_host is set, use exactly that name in the URL; otherwise start akou with AKOU_BEHIND_PROXY=true so it accepts its container name.";

        /// <summary>A key with <c>jobs</c>, or <c>admin</c>, which reaches every route. Pure.</summary>
        internal static bool HasJobsScope(string? keyBody)
        {
            try
            {
                using var document = JsonDocument.Parse(keyBody ?? "");
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("scopes", out var scopes)
                    && scopes.ValueKind == JsonValueKind.Array
                    && scopes.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && s.GetString() is "jobs" or "admin");
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>akou's error code: <c>{"error":"bad_host"}</c> from its guard, <c>{"error":{"code":...}}</c> from a route. Pure.</summary>
        internal static string? ErrorCode(string? body)
        {
            try
            {
                using var document = JsonDocument.Parse(body ?? "");
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var error)) return null;
                return error.ValueKind switch
                {
                    JsonValueKind.String => error.GetString(),
                    JsonValueKind.Object => Text(error, "code"),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
