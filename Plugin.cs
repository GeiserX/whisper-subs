using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using WhisperSubs.Configuration;
using WhisperSubs.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace WhisperSubs
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<Plugin> _logger;

        internal const string ScriptTag = "<script src=\"configurationpage?name=whisperSubs.js\"></script>";

        public override string Name => "WhisperSubs";
        public override Guid Id => Guid.Parse("97124bd9-c8cd-4a53-a213-e593aa3fef52"); // Using a static GUID

        // Store data outside /plugins/ to avoid Jellyfin treating the data dir as a plugin folder
        public new string DataFolderPath => Path.Combine(_appPaths.DataPath, "WhisperSubs");

        /// <summary>Jellyfin web root (where index.html lives). Surfaced for the injection-status panel.</summary>
        public string WebPath => _appPaths.WebPath;

        /// <summary>Path to Jellyfin's index.html that the client script is injected into.</summary>
        public string IndexHtmlPath => Path.Combine(_appPaths.WebPath, "index.html");

        /// <summary>Outcome of the most recent <see cref="InjectClientScript"/> run (startup or manual re-inject).</summary>
        public string LastInjectionOutcome { get; private set; } = "not run";

        /// <summary>
        /// State of the File Transformation plugin integration (issue #108) — written by
        /// <see cref="FileTransformationRegistrationService"/> at startup and by the config-page
        /// Re-inject action. When registered, the client script is injected into the SERVED
        /// index.html without touching the file on disk.
        /// </summary>
        public FileTransformationState FileTransformation { get; internal set; } = FileTransformationState.NotChecked;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _appPaths = applicationPaths;
            _logger = logger;
            Instance = this;

            InjectClientScript();
        }

        public static Plugin Instance { get; private set; } = null!;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
                    EnableInMainMenu = true
                },
                new PluginPageInfo
                {
                    Name = "whisperSubs.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.whisperSubs.js"
                }
            };
        }

        /// <summary>
        /// Injects a script tag into Jellyfin's index.html so our JS runs on every page.
        /// Follows the same pattern used by intro-skipper and JellyScrub plugins. Records the result
        /// in <see cref="LastInjectionOutcome"/> and returns it so the config page can surface whether
        /// the in-page button/menu integration is actually wired up. (Issue #94.)
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Reads/writes Jellyfin's index.html on disk")]
        public ScriptInjectionOutcome InjectClientScript()
        {
            try
            {
                var indexPath = IndexHtmlPath;
                if (!File.Exists(indexPath))
                {
                    LastInjectionOutcome = "index.html not found";
                    _logger.LogDebug("WhisperSubs: index.html not found at {Path}, skipping script injection", indexPath);
                    return ScriptInjectionOutcome.IndexNotFound;
                }

                var (outcome, newHtml) = ComputeInjection(File.ReadAllText(indexPath));
                switch (outcome)
                {
                    case ScriptInjectionOutcome.Injected:
                        File.WriteAllText(indexPath, newHtml!);
                        LastInjectionOutcome = "injected";
                        _logger.LogInformation("WhisperSubs: injected client script into index.html");
                        return ScriptInjectionOutcome.Injected;

                    case ScriptInjectionOutcome.AlreadyPresent:
                        LastInjectionOutcome = "already injected";
                        _logger.LogDebug("WhisperSubs: script tag already present in index.html");
                        return outcome;

                    default: // NoHeadTag — ComputeInjection never returns IndexNotFound/WriteFailed here
                        LastInjectionOutcome = "no </head> tag in index.html";
                        _logger.LogWarning("WhisperSubs: could not find </head> in index.html, skipping script injection");
                        return outcome;
                }
            }
            catch (Exception ex)
            {
                LastInjectionOutcome = "failed: " + ex.Message;
                _logger.LogWarning(ex, "WhisperSubs: failed to inject client script into index.html");
                return ScriptInjectionOutcome.WriteFailed;
            }
        }

        /// <summary>
        /// Pure core of <see cref="InjectClientScript"/>: decides what to do with the given index.html
        /// content. Returns the outcome and, when the tag should be added, the rewritten HTML (the tag
        /// inserted once before the first &lt;/head&gt;). No I/O, so it is unit-testable.
        /// </summary>
        internal static (ScriptInjectionOutcome Outcome, string? NewHtml) ComputeInjection(string? html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return (ScriptInjectionOutcome.NoHeadTag, null);
            }

            if (html.Contains(ScriptTag, StringComparison.OrdinalIgnoreCase))
            {
                return (ScriptInjectionOutcome.AlreadyPresent, null);
            }

            var headEnd = new Regex("</head>", RegexOptions.IgnoreCase);
            if (!headEnd.IsMatch(html))
            {
                return (ScriptInjectionOutcome.NoHeadTag, null);
            }

            return (ScriptInjectionOutcome.Injected, headEnd.Replace(html, ScriptTag + "\n</head>", 1));
        }

        // Any <script> tag referencing our client script, tolerant of attribute/spacing variants and a
        // trailing newline, so NormalizeInjection can strip historical or hand-edited forms. The match
        // timeout is cheap insurance on the per-serve hot path: a timeout surfaces as an exception that
        // TransformIndexHtml's catch converts into "serve the original content unchanged".
        private static readonly Regex WhisperSubsScriptTagRegex = new(
            "<script[^>]*whisperSubs\\.js[^>]*>\\s*</script>\\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        // Hoisted + compiled because NormalizeInjection runs on every served index.html.
        private static readonly Regex HeadEndRegex = new(
            "</head>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Pure, self-healing serve-time transform used as the File Transformation callback body
        /// (issue #108): strips every existing WhisperSubs script-tag variant, then inserts exactly one
        /// canonical <see cref="ScriptTag"/> before the first &lt;/head&gt;. Idempotent — HTML that is
        /// already canonical comes back byte-identical, which is what makes serve-time injection safe
        /// to layer on top of a direct on-disk injection (no double tag). When the HTML has no
        /// &lt;/head&gt; to anchor on, the input is returned unchanged (never strip without re-adding,
        /// never return null).
        /// </summary>
        internal static string NormalizeInjection(string? html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return html ?? string.Empty;
            }

            var stripped = WhisperSubsScriptTagRegex.Replace(html, string.Empty);

            if (!HeadEndRegex.IsMatch(stripped))
            {
                return html;
            }

            return HeadEndRegex.Replace(stripped, ScriptTag + "\n</head>", 1);
        }

        /// <summary>
        /// Pure: which injection mechanism(s) are effectively active, for the status panel.
        /// "direct" = tag present in index.html on disk; "file-transformation" = registered serve-time
        /// transform; both can be active at once (the idempotent transform keeps the served page canonical).
        /// </summary>
        internal static string ResolveInjectionMode(bool scriptTagPresent, bool ftRegistered)
            => (scriptTagPresent, ftRegistered) switch
            {
                (true, true) => "direct+file-transformation",
                (true, false) => "direct",
                (false, true) => "file-transformation",
                _ => "none"
            };

        /// <summary>
        /// Live snapshot of whether the client script is wired into index.html, for the config-page
        /// "In-page button &amp; menu" panel. Re-reads the file each call so it reflects reality now
        /// (e.g. a Jellyfin update that replaced index.html after startup). (Issue #94.)
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Reads/probes Jellyfin's index.html on disk")]
        public ScriptInjectionStatus GetInjectionStatus()
        {
            var path = IndexHtmlPath;
            var exists = File.Exists(path);
            var tagPresent = false;
            var writable = false;

            if (exists)
            {
                try
                {
                    tagPresent = File.ReadAllText(path).Contains(ScriptTag, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WhisperSubs: could not read index.html for status");
                }
                writable = IsWritable(path);
            }

            var ft = FileTransformation;
            var (level, message) = DescribeInjection(exists, tagPresent, writable, path, ft.Registered);
            return new ScriptInjectionStatus
            {
                WebPath = WebPath,
                IndexHtmlPath = path,
                IndexExists = exists,
                ScriptTagPresent = tagPresent,
                Writable = writable,
                LastStartupOutcome = LastInjectionOutcome,
                Level = level,
                Message = message,
                Mode = ResolveInjectionMode(tagPresent, ft.Registered),
                FileTransformationPresent = ft.Present,
                FileTransformationVersion = ft.Version,
                FileTransformationRegistered = ft.Registered,
                FileTransformationError = ft.Error
            };
        }

        /// <summary>Re-runs the injection on demand (config-page button), then returns the fresh status.</summary>
        [ExcludeFromCodeCoverage(Justification = "Writes Jellyfin's index.html on disk")]
        public ScriptInjectionStatus ReinjectScript()
        {
            InjectClientScript();
            return GetInjectionStatus();
        }

        /// <summary>
        /// Pure: turns the observable facts about index.html into a severity + user-facing message for
        /// the config panel. <paramref name="indexHtmlPath"/> is woven into the not-writable remediation
        /// so the fix is copy-paste. Unit-tested so the guidance stays correct.
        /// </summary>
        internal static (string Level, string Message) DescribeInjection(bool indexExists, bool scriptTagPresent, bool writable, string indexHtmlPath, bool fileTransformationRegistered = false)
        {
            if (!indexExists)
            {
                return ("error",
                    "Jellyfin's index.html was not found at the web path, so the in-page \"Generate Subtitles\" " +
                    "button and menu item can't be added. This is unusual — confirm this server hosts the Jellyfin web UI.");
            }

            if (fileTransformationRegistered && !scriptTagPresent)
            {
                return ("ok",
                    "The WhisperSubs client script is injected at serve time via the File Transformation plugin — " +
                    "no changes to index.html on disk are needed (ideal for read-only web roots). If you don't see " +
                    "it, hard-refresh your browser (Ctrl/Cmd+Shift+R) and make sure you're signed in as an " +
                    "administrator — it's admin-only. The \"Generate Subtitles\" entry in an item's three-dot (⋮) " +
                    "menu is the most reliable place.");
            }

            if (scriptTagPresent)
            {
                var alsoFt = fileTransformationRegistered
                    ? " It is also registered with the File Transformation plugin (serve-time), which keeps the served page canonical."
                    : "";
                return ("ok",
                    "The WhisperSubs client script is injected." + alsoFt + " If you don't see it, hard-refresh your browser " +
                    "(Ctrl/Cmd+Shift+R) and make sure you're signed in as an administrator — it's admin-only. The " +
                    "\"Generate Subtitles\" entry in an item's three-dot (⋮) menu is the most reliable; the button on " +
                    "the detail page depends on your Jellyfin theme/version, so if only the page button is missing, use the menu item.");
            }

            if (!writable)
            {
                var target = string.IsNullOrEmpty(indexHtmlPath) ? "your index.html" : indexHtmlPath;
                return ("error",
                    "index.html is present but NOT writable, so the client script can't be injected directly (common with " +
                    "read-only web roots in Docker). Recommended fix: install the File Transformation plugin " +
                    "(Dashboard → Plugins → Repositories → add https://www.iamparadox.dev/jellyfin/plugins/manifest.json, " +
                    "install \"File Transformation\", restart Jellyfin) — WhisperSubs then injects at serve time with no " +
                    "permission changes. Alternatively, make it writable by the Jellyfin service user, then click " +
                    "Re-inject (or restart Jellyfin). On a Linux package install: sudo chown root:jellyfin \"" + target +
                    "\" && sudo chmod 664 \"" + target + "\". On Docker the user/group differs (e.g. linuxserver.io " +
                    "uses your PUID/PGID) and a read-only web mount must be made writable in your compose file.");
            }

            return ("warning",
                "The client script is not currently injected — a Jellyfin update may have replaced index.html. " +
                "Click Re-inject below, then hard-refresh your browser.");
        }

        /// <summary>Best-effort writability probe: open the file for write without modifying it.</summary>
        [ExcludeFromCodeCoverage(Justification = "Probes the filesystem")]
        private static bool IsWritable(string path)
        {
            try
            {
                // FileShare.ReadWrite so a concurrent reader/writer (Jellyfin serving the page, or a
                // racing re-inject) doesn't make this probe spuriously report "not writable".
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Outcome of an attempt to inject the client &lt;script&gt; tag into index.html.</summary>
    public enum ScriptInjectionOutcome
    {
        Injected,
        AlreadyPresent,
        NoHeadTag,
        IndexNotFound,
        WriteFailed
    }

    /// <summary>Serialized to the config page so an admin can see whether the in-page integration is wired up.</summary>
    public class ScriptInjectionStatus
    {
        public string WebPath { get; set; } = "";
        public string IndexHtmlPath { get; set; } = "";
        public bool IndexExists { get; set; }
        public bool ScriptTagPresent { get; set; }
        public bool Writable { get; set; }
        public string LastStartupOutcome { get; set; } = "";
        public string Level { get; set; } = "";
        public string Message { get; set; } = "";

        /// <summary>Effective mechanism(s): "direct", "file-transformation", "direct+file-transformation" or "none". (Issue #108.)</summary>
        public string Mode { get; set; } = "";

        public bool FileTransformationPresent { get; set; }
        public string FileTransformationVersion { get; set; } = "";
        public bool FileTransformationRegistered { get; set; }
        public string FileTransformationError { get; set; } = "";

        /// <summary>
        /// Best-effort probe of the SERVED index.html (the ground truth under serve-time transforms):
        /// "yes" (marker found), "no" (fetched but marker absent) or "unknown" (probe unavailable).
        /// </summary>
        public string ServedHtmlVerified { get; set; } = "unknown";
    }
}
