using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Web
{
    /// <summary>
    /// Registers our index.html transformation with the File Transformation plugin at server startup.
    /// A hosted service (not the plugin constructor) because registration resolves services from FT's
    /// DI at call time — plugin construction order is unspecified and runs before the container is
    /// usable, where a too-early call would be silently lost. FT keeps registrations in memory only,
    /// so this runs on every boot; the stable transformation id makes re-registration idempotent.
    /// (Issue #108.)
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Startup orchestration: timers + live plugin reflection")]
    public sealed class FileTransformationRegistrationService : IHostedService
    {
        private readonly ILogger<FileTransformationRegistrationService> _logger;

        public FileTransformationRegistrationService(ILogger<FileTransformationRegistrationService> logger)
        {
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire-and-forget so server startup is never delayed. Bounded retry with backoff covers
            // FT initializing after us; retries are safe because FT ignores duplicate ids.
            _ = Task.Run(async () =>
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var state = WebFileTransformation.TryRegister(_logger);
                        var plugin = Plugin.Instance;
                        if (plugin != null)
                        {
                            plugin.FileTransformation = state;
                        }

                        // Done when registered AND the state was recorded, or when FT is CLEANLY absent
                        // (scan completed, no assembly, no error — nothing to retry). A scan that FAILED
                        // (Present=false with an Error, e.g. assemblies still loading at startup) keeps
                        // retrying, as does a successful registration while Plugin.Instance is still null
                        // (idempotent re-register records the status once it exists).
                        if ((state.Registered && plugin != null) || (!state.Present && state.Error.Length == 0))
                        {
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "WhisperSubs: File Transformation registration attempt {Attempt} failed", attempt);
                    }

                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt), CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }, CancellationToken.None);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
