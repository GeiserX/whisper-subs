using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using WhisperSubs.Configuration;
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

            var (level, message) = DescribeInjection(exists, tagPresent, writable);
            return new ScriptInjectionStatus
            {
                WebPath = WebPath,
                IndexHtmlPath = path,
                IndexExists = exists,
                ScriptTagPresent = tagPresent,
                Writable = writable,
                LastStartupOutcome = LastInjectionOutcome,
                Level = level,
                Message = message
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
        /// Pure: turns the three observable facts about index.html into a severity + user-facing
        /// message for the config panel. Unit-tested so the guidance stays correct.
        /// </summary>
        internal static (string Level, string Message) DescribeInjection(bool indexExists, bool scriptTagPresent, bool writable)
        {
            if (!indexExists)
            {
                return ("error",
                    "Jellyfin's index.html was not found at the web path, so the in-page \"Generate Subtitles\" " +
                    "button and menu item can't be added. This is unusual — confirm this server hosts the Jellyfin web UI.");
            }

            if (scriptTagPresent)
            {
                return ("ok",
                    "The WhisperSubs client script is injected. If you don't see it, hard-refresh your browser " +
                    "(Ctrl/Cmd+Shift+R) and make sure you're signed in as an administrator — it's admin-only. The " +
                    "\"Generate Subtitles\" entry in an item's three-dot (⋮) menu is the most reliable; the button on " +
                    "the detail page depends on your Jellyfin theme/version, so if only the page button is missing, use the menu item.");
            }

            if (!writable)
            {
                return ("error",
                    "index.html is present but NOT writable, so the client script can't be injected (common with " +
                    "read-only web roots in Docker). Make the Jellyfin web directory writable, then click Re-inject " +
                    "(or restart Jellyfin).");
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
    }
}
