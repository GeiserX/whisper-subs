using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Web
{
    /// <summary>
    /// Result of the most recent File Transformation plugin detection/registration attempt.
    /// Surfaced on the config page so an admin can see which injection mechanism is active. (Issue #108.)
    /// </summary>
    public sealed class FileTransformationState
    {
        /// <summary>State before the startup registration service has run.</summary>
        public static readonly FileTransformationState NotChecked = new();

        /// <summary>The File Transformation plugin's assembly is loaded in this Jellyfin instance.</summary>
        public bool Present { get; init; }

        /// <summary>File Transformation's assembly version (informational, e.g. "2.5.11.0").</summary>
        public string Version { get; init; } = "";

        /// <summary>Our index.html transformation was successfully registered with File Transformation.</summary>
        public bool Registered { get; init; }

        /// <summary>Why registration was skipped or failed (empty when registered or not installed).</summary>
        public string Error { get; init; } = "";
    }

    /// <summary>
    /// Integration with IAmParadox27's "File Transformation" plugin
    /// (https://github.com/IAmParadox27/jellyfin-plugin-file-transformation): registers a serve-time
    /// transformation of index.html so the in-page "Generate Subtitles" button works WITHOUT writing
    /// to the web directory — the fix for read-only web roots. Everything is reflection-based because
    /// Jellyfin loads each plugin in its own AssemblyLoadContext (no cross-plugin type identity), which
    /// is also the integration path File Transformation itself documents. Direct on-disk injection
    /// remains the default mechanism; this is layered on top whenever File Transformation is installed.
    /// (Issue #108.)
    /// </summary>
    public static class WebFileTransformation
    {
        /// <summary>
        /// The id File Transformation keys our transformation by. Stable across restarts (our plugin
        /// GUID) so its same-id guard makes re-registration idempotent.
        /// </summary>
        internal static readonly Guid TransformationId = Guid.Parse("97124bd9-c8cd-4a53-a213-e593aa3fef52");

        internal const string FileTransformationAssemblyName = "Jellyfin.Plugin.FileTransformation";
        internal const string PluginInterfaceTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";
        internal const string RegisterMethodName = "RegisterTransformation";

        // File Transformation treats this as an unanchored REGEX over the served path, so anchor the
        // end and escape the dot: matches "index.html", "web/index.html", "/jellyfin/web/index.html";
        // rejects "index2html" or "index.htmlx". (Do NOT fully anchor with ^ — the served path carries
        // a "web/" prefix.)
        internal const string IndexFileNamePattern = "(^|/)index\\.html$";

        /// <summary>
        /// File Transformation deserializes its <c>{"contents": "..."}</c> callback payload into the
        /// callback method's parameter type (property matched by name, case-insensitively).
        /// </summary>
        public sealed class FileTransformationInput
        {
            /// <summary>The full text of the file being served (index.html).</summary>
            public string? Contents { get; set; }
        }

        /// <summary>
        /// Invoked BY the File Transformation plugin (via reflection) for every served index.html.
        /// The signature is a wire contract pinned by tests: public static, single
        /// <see cref="FileTransformationInput"/> parameter, returns string. It must never throw and
        /// never return null — File Transformation's serve pipeline has no error handling, so an
        /// escaped exception would blank the whole Jellyfin web UI for every user. On any internal
        /// failure the original contents are returned unchanged.
        /// </summary>
        public static string TransformIndexHtml(FileTransformationInput? input)
        {
            var original = input?.Contents ?? string.Empty;
            try
            {
                return Plugin.NormalizeInjection(original);
            }
            catch
            {
                return original;
            }
        }

        /// <summary>
        /// Pure: the registration payload File Transformation expects, as a JSON string (it is parsed
        /// into FT's own JObject type at registration time — see <see cref="TryRegister"/>).
        /// </summary>
        internal static string BuildRegistrationJson(Guid id, string fileNamePattern, string callbackAssembly, string callbackClass, string callbackMethod)
            => JsonSerializer.Serialize(new
            {
                id = id.ToString(),
                fileNamePattern,
                callbackAssembly,
                callbackClass,
                callbackMethod
            });

        /// <summary>
        /// Detects the File Transformation plugin and registers our index.html transformation with it.
        /// Never throws — any failure is captured in the returned state and direct on-disk injection
        /// (which already ran at startup) simply remains the only active mechanism.
        ///
        /// The payload is built with FT's OWN Newtonsoft JObject type — obtained from
        /// RegisterTransformation's parameter and populated via its static Parse(string) — instead of
        /// referencing Newtonsoft ourselves. Cross-AssemblyLoadContext type identity makes "our"
        /// JObject a different .NET type from FT's, so constructing theirs is the only version-proof
        /// way to call it (and it keeps this plugin dependency-free).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Reflection against another plugin's live assembly + AssemblyLoadContext scan")]
        internal static FileTransformationState TryRegister(ILogger logger)
        {
            var present = false;
            var version = "";
            try
            {
                // Exact simple-name match (File Transformation's assembly name equals its project name,
                // verified against its csproj): avoids both accidental matches on companion assemblies
                // and name-contains masquerading.
                var ftAssembly = AssemblyLoadContext.All
                    .SelectMany(ctx => ctx.Assemblies)
                    .FirstOrDefault(asm => string.Equals(asm.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase));

                if (ftAssembly == null)
                {
                    logger.LogInformation("WhisperSubs: File Transformation plugin not installed; using direct index.html injection only.");
                    return new FileTransformationState { Present = false };
                }

                present = true;
                version = ftAssembly.GetName().Version?.ToString() ?? "";

                var pluginInterface = ftAssembly.GetType(PluginInterfaceTypeName);
                // Resolved by name + single-parameter arity (not GetMethod(name)) so a future File
                // Transformation overload can never turn this into an AmbiguousMatchException.
                var register = pluginInterface?
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == RegisterMethodName && m.GetParameters().Length == 1);
                if (register == null)
                {
                    logger.LogWarning("WhisperSubs: File Transformation {Version} found but its registration API is missing/incompatible; using direct injection only.", version);
                    return new FileTransformationState { Present = true, Version = version, Error = "registration API not found (incompatible File Transformation version)" };
                }

                var paramType = register.GetParameters().FirstOrDefault()?.ParameterType;
                var parse = paramType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
                if (parse == null)
                {
                    logger.LogWarning("WhisperSubs: File Transformation {Version} uses an unsupported registration payload type ({Type}); using direct injection only.", version, paramType?.FullName ?? "unknown");
                    return new FileTransformationState { Present = true, Version = version, Error = "unsupported registration payload type " + (paramType?.FullName ?? "unknown") };
                }

                var json = BuildRegistrationJson(
                    TransformationId,
                    IndexFileNamePattern,
                    typeof(WebFileTransformation).Assembly.FullName ?? "WhisperSubs",
                    typeof(WebFileTransformation).FullName ?? "WhisperSubs.Web.WebFileTransformation",
                    nameof(TransformIndexHtml));

                var payload = parse.Invoke(null, new object[] { json });
                register.Invoke(null, new[] { payload });

                logger.LogInformation("WhisperSubs: registered index.html transformation with File Transformation {Version} — the in-page button is injected at serve time (no web-directory writes needed).", version);
                return new FileTransformationState { Present = true, Version = version, Registered = true };
            }
            catch (Exception ex)
            {
                // `present` reflects how far we got: an exception before the assembly scan completed
                // (e.g. assemblies still loading at startup) must not claim FT is installed.
                var baseError = ex.GetBaseException().Message;
                logger.LogWarning(ex, "WhisperSubs: File Transformation registration failed; keeping direct index.html injection.");
                return new FileTransformationState { Present = present, Version = version, Error = baseError };
            }
        }
    }
}
