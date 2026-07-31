using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using WhisperSubs.Web;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Api
{
    [ApiController]
    [Route("Plugins/WhisperSubs")]
    // All endpoints require Jellyfin admin (elevation), not just authentication: they enumerate
    // libraries and enqueue/trigger resource-heavy transcription, so a non-admin user must not be
    // able to drive them (privilege boundary / resource-exhaustion). Setup/* repeat this policy
    // explicitly for clarity; it is redundant under this class-level default but harmless.
    [Authorize(Policy = "RequiresElevation")]
    // Excluded from coverage as a WHOLE: every endpoint orchestrates Jellyfin runtime services
    // (library manager, task manager, live HTTP) and the testable logic lives in pure helpers
    // elsewhere. coverlet.runsettings already excludes this type for LOCAL runs, but CI's
    // `dotnet test --collect` does not load that runsettings file — the attribute is what makes
    // the exclusion effective in both places.
    [ExcludeFromCodeCoverage(Justification = "Thin API layer over Jellyfin runtime services; logic lives in unit-tested pure helpers")]
    public class SubtitleController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SubtitleController> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ITaskManager _taskManager;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpClientFactory _httpClientFactory;

        public SubtitleController(
            ILibraryManager libraryManager,
            ILogger<SubtitleController> logger,
            ILoggerFactory loggerFactory,
            ITaskManager taskManager,
            IServerApplicationHost serverApplicationHost,
            IHttpClientFactory httpClientFactory)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _taskManager = taskManager;
            _serverApplicationHost = serverApplicationHost;
            _httpClientFactory = httpClientFactory;
        }

        private SubtitleManager GetSubtitleManager()
        {
            return new SubtitleManager(_libraryManager, _loggerFactory.CreateLogger<SubtitleManager>());
        }

        /// <summary>
        /// Gets all libraries.
        /// </summary>
        [HttpGet("Libraries")]
        public ActionResult<IEnumerable<LibraryInfo>> GetLibraries()
        {
            try
            {
                var libraries = _libraryManager.GetVirtualFolders()
                    .Select(vf => new LibraryInfo
                    {
                        Id = vf.ItemId,
                        Name = vf.Name
                    })
                    .ToList();

                return Ok(libraries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting libraries");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets items in a library with pagination.
        /// </summary>
        [HttpGet("Libraries/{libraryId}/Items")]
        public ActionResult<PagedItemResult> GetLibraryItems(
            [FromRoute] string libraryId,
            [FromQuery] int startIndex = 0,
            [FromQuery] int limit = 50,
            [FromQuery] string? searchTerm = null)
        {
            try
            {
                var library = _libraryManager.GetItemById(Guid.Parse(libraryId));
                if (library == null)
                {
                    return NotFound(new { error = "Library not found" });
                }

                var config = Plugin.Instance.Configuration;
                var typeStr = config.EnableLyricsGeneration ? "Movie,Episode,Video,Audio" : "Movie,Episode,Video";
                var includeTypes = GetBaseItemKinds(typeStr);
                var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = library.Id,
                    IncludeItemTypes = includeTypes,
                    Recursive = true
                };

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    query.SearchTerm = searchTerm.Trim();
                }

                var filteredList = _libraryManager.GetItemList(query)
                    .Where(item => !string.IsNullOrEmpty(item.Path))
                    .ToList();
                var totalCount = filteredList.Count;

                var items = filteredList
                    .Skip(startIndex)
                    .Take(limit)
                    .Select(item => new ItemInfo
                    {
                        Id = item.Id.ToString(),
                        Name = item.Name,
                        Type = item.GetType().Name,
                        Path = item.Path,
                        HasSubtitles = item is Video v ? v.HasSubtitles : CheckLyricsExist(item.Path)
                    })
                    .ToList();

                return Ok(new PagedItemResult
                {
                    Items = items,
                    TotalCount = totalCount,
                    StartIndex = startIndex
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting library items");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Enqueues subtitle generation for a specific item. Returns 202 immediately.
        /// A single background worker processes the queue sequentially.
        /// </summary>
        [HttpPost("Items/{itemId}/Generate")]
        public ActionResult GenerateSubtitle(
            [FromRoute] string itemId,
            [FromQuery] string? language = null)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                var config = Plugin.Instance.Configuration;
                var isAudio = item is MediaBrowser.Controller.Entities.Audio.Audio;

                if (!(item is Video) && !(isAudio && config.EnableLyricsGeneration))
                {
                    return BadRequest(new { error = "Item is not a supported media type" });
                }

                var targetLanguage = language ?? config.DefaultLanguage;
                var queue = SubtitleQueueService.Instance;

                // Manual request → force: an explicit user action bypasses the "skip if a usable
                // subtitle already exists" checks (#82) so the user always gets fresh generation.
                // Admin requests carry the configured admin tier (#112).
                queue.Enqueue(item, targetLanguage, config.AdminRequestTier, force: true);
                _logger.LogInformation("Queued {Action} generation for {ItemName} [{Language}]. Queue size: {Count}",
                    isAudio ? "lyrics" : "subtitle", item.Name, targetLanguage, queue.PriorityCount);

                // Ensure the background drain worker is running
                var manager = GetSubtitleManager();
                queue.EnsureDispatching(manager, config, _loggerFactory, _logger, CancellationToken.None);

                return Accepted(new
                {
                    message = isAudio ? "Queued for lyrics generation" : "Queued for subtitle generation",
                    item = item.Name,
                    language = targetLanguage,
                    queueSize = queue.PriorityCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing subtitle for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Enqueues subtitle generation for all child items of a container (Series, Season, MusicAlbum).
        /// For leaf items (Movie, Episode, Audio), behaves like the single-item endpoint.
        /// </summary>
        [HttpPost("Items/{itemId}/GenerateAll")]
        public ActionResult GenerateAllSubtitles(
            [FromRoute] string itemId,
            [FromQuery] string? language = null)
        {
            try
            {
                var parent = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (parent == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                // Allow-list the only kinds we may fan out over: media leaves (movie/episode/audio) and the
                // intended containers (series/season/album). Everything else — library roots, BoxSet
                // collections, MusicArtist, plain folders — is rejected so GenerateAll(Recursive) can't flood
                // the whole library with an uncapped descendant sweep.
                if (!MediaItemResolver.IsAllowedGenerateTarget(parent))
                {
                    return BadRequest(new { error = "This item type cannot be used as a GenerateAll target. Select a movie, episode, series, season, album, or a single media item." });
                }

                var config = Plugin.Instance.Configuration;
                var targetLanguage = language ?? config.DefaultLanguage;

                // Resolve leaf items (Video / Audio) from the container — shared with the user-request
                // path so a Series/Season fans out to exactly the same set (#112).
                var children = MediaItemResolver.ResolveLeafItems(parent, config.EnableLyricsGeneration, _libraryManager);

                if (children.Count == 0)
                {
                    return BadRequest(new { error = "No supported media items found under this item." });
                }

                var queue = SubtitleQueueService.Instance;
                int queued = 0, skipped = 0;
                foreach (var child in children)
                {
                    // Bulk "Generate all" deliberately does NOT force: it respects the
                    // SkipIfSubtitleExists toggle and the original-language / translation toggles so a
                    // library-wide request fills only the gaps the user actually wants (unlike
                    // single-item Generate, which forces a specific item). See #82/#83.
                    if (queue.Enqueue(child, targetLanguage, config.AdminRequestTier)) queued++;
                    else skipped++;
                }

                _logger.LogInformation(
                    "Queued {Queued} items ({Skipped} already queued or in progress) for subtitle generation under {ParentName} [{Language}]",
                    queued, skipped, parent.Name, targetLanguage);

                // Ensure the background drain worker is running
                var manager = GetSubtitleManager();
                queue.EnsureDispatching(manager, config, _loggerFactory, _logger, CancellationToken.None);

                return Accepted(new
                {
                    message = skipped > 0
                        ? $"Queued {queued} item(s) for subtitle generation ({skipped} already queued or in progress)"
                        : $"Queued {queued} item(s) for subtitle generation",
                    parent = parent.Name,
                    count = children.Count,
                    queued,
                    skipped,
                    language = targetLanguage,
                    queueSize = queue.PriorityCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing batch generation for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets the current queue status.
        /// </summary>
        [HttpGet("Queue")]
        public ActionResult GetQueueStatus()
        {
            var queue = SubtitleQueueService.Instance;
            return Ok(new
            {
                isProcessing = queue.IsDraining || queue.IsTaskRunning,
                currentItem = queue.CurrentItemName,
                remaining = queue.IsTaskRunning
                    ? queue.PriorityCount + (queue.TaskTotal - queue.TaskProcessed)
                    : queue.PriorityCount,
                processed = queue.ProcessedCount + queue.TaskProcessed,
                failed = queue.TaskFailed + queue.FailedCount,
                lastError = queue.LastError,
                taskTotal = queue.IsTaskRunning ? queue.TaskTotal : 0,
                fileProgress = queue.CurrentFileProgress,
                phase = queue.CurrentPhase,
                itemType = queue.TaskCurrentItemType,
                library = queue.TaskCurrentItemLibrary,
                // Named-tier breakdown of what's queued + how many user requests await approval (#112).
                tiers = queue.CountsByTier().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                pendingRequests = SubtitleRequestStore.Instance.GetAll().Count(r => r.State == RequestState.Pending),
                // Inbound: the items waiting, in the order they'll run (capped; the full total is `remaining`). (v4.0)
                pending = queue.PendingItems().Select(p => new
                {
                    name = p.Name,
                    tier = p.Tier.ToString(),
                    language = p.Language
                }),
                // Outbound: which worker/endpoint is transcribing which item right now. (v4.0)
                workers = queue.SnapshotWorkers().Select(w => new
                {
                    id = w.Id,
                    name = w.Name,
                    isLocal = w.IsLocal,
                    inFlight = w.InFlight,
                    maxConcurrency = w.MaxConcurrency,
                    costWeight = w.CostWeight,
                    current = w.CurrentItems
                })
            });
        }

        /// <summary>
        /// Tests a worker endpoint (v4.0 config UI): POSTs a tiny silent WAV to its transcription route so
        /// the admin can confirm reachability + auth + a working transcribe path before saving the worker,
        /// without touching the media library. Never throws — always returns a shaped {ok, message} result.
        /// </summary>
        [HttpPost("Workers/TestConnection")]
        public async Task<ActionResult> TestWorkerConnection([FromBody] WorkerTestRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { ok = false, message = "Missing request body." });
            }

            var worker = new Configuration.WhisperWorker
            {
                ApiUrl = request.ApiUrl ?? "",
                ApiKey = request.ApiKey ?? "",
                Model = request.Model ?? "",
                MaxConcurrency = 1,
                CostWeight = 0
            };
            var (valid, error) = Controller.Workers.WorkerConfigValidation.Validate(worker);
            if (!valid)
            {
                return Ok(new { ok = false, message = error });
            }

            // SSRF hardening (v4.0.1): this admin endpoint makes the server POST to an arbitrary URL. Block
            // the cloud-metadata link-local range (169.254.0.0/16 and IPv6 fe80::/10) — never a real worker,
            // the highest-value SSRF target. Loopback/LAN stay allowed (legitimate worker locations).
            if (!System.Uri.TryCreate(worker.ApiUrl.Trim(), System.UriKind.Absolute, out var probeUri))
            {
                return Ok(new { ok = false, message = "Endpoint URL is invalid." });
            }

            System.Net.IPAddress[] pinnedAddresses;
            if (System.Net.IPAddress.TryParse(probeUri.Host, out var probeIp))
            {
                if (Controller.Workers.WorkerProbe.IsBlockedLinkLocal(probeIp))
                {
                    return Ok(new { ok = false, message = "Endpoint host is link-local (169.254.0.0/16), which is blocked." });
                }
                pinnedAddresses = [probeIp];
            }
            else
            {
                try
                {
                    var resolved = await System.Net.Dns.GetHostAddressesAsync(probeUri.Host)
                        .WaitAsync(TimeSpan.FromSeconds(8));
                    if (resolved.Length == 0)
                    {
                        return Ok(new { ok = false, message = "Endpoint hostname did not resolve to an address." });
                    }
                    if (resolved.Any(Controller.Workers.WorkerProbe.IsBlockedLinkLocal))
                    {
                        return Ok(new { ok = false, message = "Endpoint hostname resolves to a link-local address (169.254.0.0/16), which is blocked." });
                    }
                    pinnedAddresses = resolved;
                }
                catch (System.Exception)
                {
                    return Ok(new { ok = false, message = "Endpoint hostname did not resolve (DNS lookup failed)." });
                }
            }

            System.Net.Http.SocketsHttpHandler CreatePinnedHandler(TimeSpan connectTimeout)
            {
                var handler = new System.Net.Http.SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = connectTimeout
                };
                // SECURITY-REVIEW: pin both probe connections to the address validated above so DNS
                // rebinding cannot switch the hostname to link-local metadata between validation and use.
                handler.ConnectCallback = async (context, cancellationToken) =>
                {
                    Exception? lastError = null;
                    foreach (var pinnedAddress in pinnedAddresses)
                    {
                        var socket = new System.Net.Sockets.Socket(
                            pinnedAddress.AddressFamily,
                            System.Net.Sockets.SocketType.Stream,
                            System.Net.Sockets.ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(
                                new System.Net.IPEndPoint(pinnedAddress, context.DnsEndPoint.Port),
                                cancellationToken);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                        catch (OperationCanceledException)
                        {
                            socket.Dispose();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            socket.Dispose();
                            lastError = ex;
                        }
                    }
                    throw new System.Net.Http.HttpRequestException(
                        "No validated endpoint address accepted the connection", lastError);
                };
                return handler;
            }

            var url = worker.ApiUrl.TrimEnd('/') + "/v1/audio/transcriptions";
            var model = string.IsNullOrWhiteSpace(worker.Model) ? "Systran/faster-whisper-large-v3" : worker.Model.Trim();
            var wav = Controller.Workers.SyntheticAudio.SilentWav16kMono(100);

            // Reachability pre-probe (v4.1.1): before the (potentially slow) transcribe, do a cheap GET of the
            // worker BASE url so we can tell a genuinely-unreachable endpoint (connection refused / DNS / TLS /
            // host-unreachable) apart from a reachable-but-slow one. A modest GPU running a large model can take
            // far longer than the transcribe deadline to decode even the silent probe clip (whisper decodes
            // silence as a worst-case, full-length hallucination), and that must NOT be reported as "Unreachable" —
            // a false negative that scares admins off a perfectly good worker. Same SSRF hardening as the transcribe
            // call: no auto-redirect, and the link-local IP/DNS checks above already ran for this exact host.
            var baseUrl = worker.ApiUrl.Trim();
            if (System.Uri.TryCreate(baseUrl, System.UriKind.Absolute, out var baseUri))
            {
                baseUrl = baseUri.Scheme + "://" + baseUri.Authority + "/";
            }
            using (var reachHandler = CreatePinnedHandler(TimeSpan.FromSeconds(8)))
            using (var reachHttp = new System.Net.Http.HttpClient(reachHandler)
            {
                Timeout = TimeSpan.FromSeconds(8),
                MaxResponseContentBufferSize = 64 * 1024
            })
            {
                try
                {
                    using var getReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, baseUrl);
                    using var getResp = await reachHttp.SendAsync(
                        getReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    // Any HTTP status answered (even 401/404) proves the host is reachable — fall through to transcribe.
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    // Connection refused / DNS failure / TLS failure / connect-timeout / host-unreachable — genuinely
                    // unreachable. Stop here; the transcribe would only fail the same way, slower.
                    var (ok, warning, msg) = Controller.Workers.WorkerProbe.ClassifyProbeOutcome(reachable: false, httpStatus: null, timedOut: false);
                    return Ok(new { ok, warning, message = msg + ": " + ex.Message });
                }
                catch (OperationCanceledException)
                {
                    // The overall GET timeout elapsed AFTER connecting: the host completed the TCP/TLS handshake but
                    // is slow to answer a bare GET (some transcribe servers only handle POST). That is REACHABLE —
                    // fall through and let the transcribe probe be the real test. A GET timeout is NOT "unreachable".
                }
            }

            // Dedicated no-redirect client (v4.0.1): a real transcribe endpoint never 302s, so disabling
            // auto-redirect blocks a redirect-based SSRF pivot to an arbitrary URL. Overall timeout is 30s (v4.1.1,
            // up from 20s): reachability is already confirmed, so a timeout here means "reachable and it accepted the
            // audio but the decode is slow" — surfaced as a warning (worker still usable), never a hard failure.
            using var handler = CreatePinnedHandler(TimeSpan.FromSeconds(10));
            using var http = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 1024 * 1024
            };

            async Task<(System.Net.HttpStatusCode StatusCode, string Body)> SendProbeAsync(
                string responseFormat, CancellationToken cancellationToken)
            {
                using var content = new System.Net.Http.MultipartFormDataContent();
                var fileContent = new System.Net.Http.ByteArrayContent(wav);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                content.Add(fileContent, "file", "test.wav");
                content.Add(new System.Net.Http.StringContent(model), "model");
                content.Add(new System.Net.Http.StringContent(responseFormat), "response_format");
                if (string.Equals(responseFormat, "verbose_json", StringComparison.Ordinal))
                {
                    // Omitted on purpose — see RemoteWhisperProvider: redundant (segment is the default)
                    // and rejected with 400 by some OpenRouter-routed providers. The probe must send the
                    // same shape as a real job, or it would go green against an endpoint that rejects jobs.
                }

                using var probe = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
                {
                    Content = content
                };
                if (!string.IsNullOrWhiteSpace(worker.ApiKey))
                {
                    probe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer", worker.ApiKey.Trim());
                }

                // SECURITY-REVIEW: the admin-supplied URL passed link-local/DNS checks above and this
                // dedicated client disables redirects; do not replace it with the shared redirecting client.
                using var response = await http.SendAsync(
                    probe,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var body = await Providers.RemoteWhisperProvider.ReadUtf8BoundedAsync(
                    response.Content, 1024 * 1024, cancellationToken);
                return (response.StatusCode, body);
            }

            static bool IsValidSrtProbeBody(string body)
            {
                try
                {
                    Providers.RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(body);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Match the runtime negotiation: preserve SRT-first behavior for existing workers, then
                // retry timestamped JSON only when a Groq/OpenRouter-style endpoint rejects that format.
                var responseFormat = "srt";
                var response = await SendProbeAsync(responseFormat, probeCts.Token);
                if (Providers.RemoteWhisperProvider.ShouldRetryProbeAsVerbose(
                        response.StatusCode, response.Body))
                {
                    responseFormat = "verbose_json";
                    response = await SendProbeAsync(responseFormat, probeCts.Token);
                }

                sw.Stop();
                var status = (int)response.StatusCode;
                var (ok, warning, msg) = Controller.Workers.WorkerProbe.ClassifyProbeOutcome(
                    reachable: true, httpStatus: status, timedOut: false);
                if (ok && !warning)
                {
                    var emptyResult = string.Equals(responseFormat, "verbose_json", StringComparison.Ordinal)
                        ? Providers.RemoteWhisperProvider.HasEmptyTimestampedSegmentsArray(response.Body)
                        : string.IsNullOrWhiteSpace(response.Body);
                    if (emptyResult)
                    {
                        return Ok(new
                        {
                            ok = true,
                            warning = true,
                            status,
                            latencyMs = sw.ElapsedMilliseconds,
                            message = "Endpoint is reachable and accepted the audio, but the silent probe produced no cues. Test with real speech if you need content verification."
                        });
                    }

                    var validBody = string.Equals(responseFormat, "verbose_json", StringComparison.Ordinal)
                        ? Providers.RemoteWhisperProvider.HasTimestampedSegmentsArray(response.Body)
                        : IsValidSrtProbeBody(response.Body);
                    if (!validBody)
                    {
                        return Ok(new
                        {
                            ok = false,
                            warning = false,
                            status,
                            latencyMs = sw.ElapsedMilliseconds,
                            message = "Endpoint responded, but did not return timestamped subtitles."
                        });
                    }
                }

                // Do NOT echo the upstream response body (SSRF response-disclosure) — status + latency only.
                var message = (ok && !warning) ? (msg + " (" + sw.ElapsedMilliseconds + " ms).") : msg;
                return Ok(new { ok, warning, status, latencyMs = sw.ElapsedMilliseconds, message });
            }
            catch (OperationCanceledException)
            {
                // Overall 30s timeout, and reachability already passed: reachable + accepted the audio, decode slow.
                sw.Stop();
                var (ok, warning, msg) = Controller.Workers.WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: null, timedOut: true);
                return Ok(new { ok, warning, latencyMs = sw.ElapsedMilliseconds, message = msg });
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                // The connection dropped mid-transcribe (it was reachable a moment ago) — report as unreachable.
                sw.Stop();
                var (ok, warning, msg) = Controller.Workers.WorkerProbe.ClassifyProbeOutcome(reachable: false, httpStatus: null, timedOut: false);
                return Ok(new { ok, warning, latencyMs = sw.ElapsedMilliseconds, message = msg + ": " + ex.Message });
            }
            catch (Exception ex)
            {
                // Never throw — the endpoint contract is an always-shaped {ok, warning, ...} result.
                sw.Stop();
                return Ok(new { ok = false, warning = false, latencyMs = sw.ElapsedMilliseconds, message = $"Unreachable: {ex.Message}" });
            }
        }

        /// <summary>
        /// Hot-reloads the worker pool from the current configuration WITHOUT a Jellyfin restart
        /// (whisper-subs-9gq): grows the live pool so a just-added worker joins the running drain immediately,
        /// within one drain cycle and without dropping in-flight jobs on the other workers. A manual fallback
        /// to the automatic on-config-change trigger, and makes the feature verifiable without a config
        /// round-trip. Admin-only via the class-level RequiresElevation policy (mirrors Workers/TestConnection).
        /// </summary>
        [HttpPost("Workers/Reload")]
        public ActionResult ReloadWorkers()
        {
            try
            {
                var count = SubtitleQueueService.Instance.ReconcileWorkers(Plugin.Instance.Configuration, _loggerFactory);
                return Ok(new { message = "Worker pool reconciled", workers = count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading worker pool");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Lists all user subtitle requests (admin), newest first — for the config-page approval panel.
        /// Returns item names + state only; never a filesystem path. (Issue #112.)
        /// </summary>
        [HttpGet("Requests")]
        public ActionResult GetAllRequests()
        {
            var requests = SubtitleRequestStore.Instance.GetAll();
            return Ok(requests.Select(r => new
            {
                requestId = r.Id,
                itemId = r.ItemId,
                itemName = r.ItemName,
                itemType = r.ItemType,
                language = r.Language,
                userName = r.UserName,
                tier = r.Tier.ToString(),
                state = r.State.ToString(),
                itemCount = r.ItemCount,
                created = new DateTime(r.CreatedTicks, DateTimeKind.Utc),
                updated = new DateTime(r.UpdatedTicks, DateTimeKind.Utc)
            }));
        }

        /// <summary>Approves a pending user request → enqueues its items at the user tier. (Issue #112.)</summary>
        [HttpPost("Requests/{requestId}/Approve")]
        public ActionResult ApproveRequest([FromRoute] string requestId)
        {
            try
            {
                var store = SubtitleRequestStore.Instance;
                var existing = store.GetById(requestId);
                if (existing == null)
                {
                    return NotFound(new { error = "Request not found" });
                }

                // Atomic Pending→Queued so two concurrent approvals can't both enqueue the same request.
                if (!store.TryTransition(requestId, RequestState.Pending, RequestState.Queued, DateTime.UtcNow.Ticks, out var req))
                {
                    return Conflict(new { error = $"Request is {existing.State}, not Pending." });
                }

                try
                {
                    var config = Plugin.Instance.Configuration;
                    var queued = SubtitleRequestService.EnqueueRequest(
                        req!.ItemId, req.Language, req.Tier, config, _libraryManager, _loggerFactory, _logger);
                    _logger.LogInformation("Approved request {Id} for {Item} → queued {Queued} item(s)", requestId, req.ItemName, queued);
                    return Ok(new { message = "Request approved and queued", queued });
                }
                catch
                {
                    // Roll the request out of Queued so it isn't stuck showing queued with nothing enqueued.
                    store.TryTransition(requestId, RequestState.Queued, RequestState.Failed, DateTime.UtcNow.Ticks, out _);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error approving request {Id}", requestId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Declines a pending user request. (Issue #112.)</summary>
        [HttpPost("Requests/{requestId}/Decline")]
        public ActionResult DeclineRequest([FromRoute] string requestId)
        {
            try
            {
                var store = SubtitleRequestStore.Instance;
                var existing = store.GetById(requestId);
                if (existing == null)
                {
                    return NotFound(new { error = "Request not found" });
                }

                if (!store.TryTransition(requestId, RequestState.Pending, RequestState.Declined, DateTime.UtcNow.Ticks, out _))
                {
                    return Conflict(new { error = $"Request is {existing.State}, not Pending." });
                }

                _logger.LogInformation("Declined request {Id} for {Item}", requestId, existing.ItemName);
                return Ok(new { message = "Request declined" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error declining request {Id}", requestId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Detects audio languages present in a media file.
        /// </summary>
        [HttpGet("Items/{itemId}/AudioLanguages")]
        public async Task<ActionResult<IEnumerable<string>>> GetAudioLanguages(
            [FromRoute] string itemId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                if (string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
                {
                    return BadRequest(new { error = "Media file not found" });
                }

                var subtitleManager = GetSubtitleManager();
                var languages = await subtitleManager.DetectAudioLanguagesAsync(item.Path, cancellationToken);
                return Ok(languages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting audio languages for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets the status of subtitle generation for an item.
        /// When language is omitted or "auto", checks for any generated .srt file.
        /// </summary>
        [HttpGet("Items/{itemId}/Status")]
        public ActionResult<SubtitleStatus> GetSubtitleStatus(
            [FromRoute] string itemId,
            [FromQuery] string? language = null)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                var lang = language ?? Plugin.Instance.Configuration.DefaultLanguage;

                if (string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = System.IO.Path.GetDirectoryName(item.Path);
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
                    // Issue #101: subtitles may live in the media folder OR the item's metadata path.
                    // Configurable naming: widen the glob to any .srt and keep only the plugin's own
                    // sidecars (new label-anchored OR legacy .generated./.translated.).
                    var label = Plugin.Instance.Configuration.SubtitleLabel ?? SubtitleNaming.DefaultLabel;
                    var found = SubtitleManager.FindGeneratedFiles(item, dir, $"{baseName}.*.srt")
                        .Where(f => SubtitleNaming.IsPluginOwnedSubtitle(System.IO.Path.GetFileName(f), label))
                        .ToList();

                    var fullFiles = found.Where(f => SubtitleNaming.Classify(System.IO.Path.GetFileName(f), label) == SubtitleNaming.OwnedKind.Full).ToList();
                    var forcedFiles = found.Where(f => System.IO.Path.GetFileName(f).Contains(".forced.")).ToList();

                    return Ok(new SubtitleStatus
                    {
                        ItemId = itemId,
                        HasGeneratedSubtitle = fullFiles.Count > 0,
                        HasForcedSubtitle = forcedFiles.Count > 0,
                        HasExistingSubtitles = item is Video v1 && v1.HasSubtitles,
                        SubtitlePath = found.Count > 0 ? string.Join("; ", found) : null
                    });
                }

                // Configurable naming: the CANONICAL (freshly-written) full-sub name comes from the
                // template, but EXISTENCE is a glob-and-filter so a legacy-named sidecar still reports
                // "exists". Restrict to this language via the ".{lang}." segment both schemes share.
                var statusLabel = Plugin.Instance.Configuration.SubtitleLabel ?? SubtitleNaming.DefaultLabel;
                var statusTemplate = SubtitleNaming.EffectiveTemplate(Plugin.Instance.Configuration.SubtitleFilenameTemplate);
                var canonicalFullPath = SubtitleNaming.BuildMediaAdjacentPath(item.Path, statusTemplate, lang, statusLabel, type: "", ".srt");

                var statusDir = System.IO.Path.GetDirectoryName(item.Path);
                var statusBase = System.IO.Path.GetFileNameWithoutExtension(item.Path);
                var ownedForLang = SubtitleManager.FindGeneratedFiles(item, statusDir, $"{statusBase}.*.srt")
                    .Select(f => System.IO.Path.GetFileName(f))
                    .Where(name => SubtitleNaming.IsPluginOwnedSubtitle(name, statusLabel)
                        && name.Contains($".{lang}.", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var hasGeneratedSubtitle = ownedForLang.Any(name => SubtitleNaming.Classify(name, statusLabel) == SubtitleNaming.OwnedKind.Full);
                var hasForcedSubtitle = ownedForLang.Any(name => name.Contains(".forced."));

                return Ok(new SubtitleStatus
                {
                    ItemId = itemId,
                    HasGeneratedSubtitle = hasGeneratedSubtitle,
                    HasForcedSubtitle = hasForcedSubtitle,
                    HasExistingSubtitles = item is Video v2 && v2.HasSubtitles,
                    SubtitlePath = hasGeneratedSubtitle ? canonicalFullPath : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting subtitle status for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Lists available whisper model files.
        /// Scans the directory of the currently configured model path for .bin files.
        /// </summary>
        [HttpGet("Models")]
        public ActionResult<IEnumerable<ModelInfo>> GetAvailableModels()
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var modelsDir = !string.IsNullOrEmpty(config.WhisperModelPath)
                    ? System.IO.Path.GetDirectoryName(config.WhisperModelPath)
                    : null;

                // Fallback to default models directory
                if (string.IsNullOrEmpty(modelsDir) || !System.IO.Directory.Exists(modelsDir))
                {
                    var dataDir = Plugin.Instance?.DataFolderPath;
                    if (!string.IsNullOrEmpty(dataDir))
                    {
                        var defaultDir = System.IO.Path.Combine(dataDir, "whisper", "models");
                        if (System.IO.Directory.Exists(defaultDir))
                            modelsDir = defaultDir;
                    }
                }

                if (string.IsNullOrEmpty(modelsDir) || !System.IO.Directory.Exists(modelsDir))
                {
                    return Ok(Array.Empty<ModelInfo>());
                }

                var models = System.IO.Directory.GetFiles(modelsDir, "*.bin")
                    .Select(path => new ModelInfo
                    {
                        Path = path,
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        FileName = System.IO.Path.GetFileName(path),
                        SizeMB = new System.IO.FileInfo(path).Length / (1024.0 * 1024.0),
                        IsActive = string.Equals(path, config.WhisperModelPath, StringComparison.OrdinalIgnoreCase)
                    })
                    .OrderBy(m => m.SizeMB)
                    .ToList();

                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing models");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Triggers the subtitle generation scheduled task immediately.
        /// </summary>
        [HttpPost("RunTask")]
        public ActionResult RunTask()
        {
            try
            {
                _taskManager.QueueScheduledTask<WhisperSubs.ScheduledTasks.SubtitleGenerationTask>();
                return Ok(new { message = "Subtitle generation task queued" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering subtitle generation task");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Clears the issue #110 skip cache so the next scheduled run re-checks every item from
        /// scratch. Escape hatch for the one case the change token can't see — a subtitle deleted
        /// outside Jellyfin — when a user doesn't want to wait for the backstop re-verify.
        /// </summary>
        [HttpPost("Setup/ClearSkipCache")]
        public ActionResult ClearSkipCache()
        {
            try
            {
                var path = WhisperSubs.Controller.SubtitleSkipCache.DefaultPath();
                var existed = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
                if (existed) System.IO.File.Delete(path);
                return Ok(new { cleared = existed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing skip cache");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Setup endpoints ──────────────────────────────────────────────

        private WhisperSetupService GetSetupService()
        {
            return new WhisperSetupService(
                _loggerFactory.CreateLogger<WhisperSetupService>(),
                Plugin.Instance.DataFolderPath);
        }

        /// <summary>
        /// Returns whether whisper binary and model are configured and reachable.
        /// </summary>
        [HttpGet("Setup/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetSetupStatus()
        {
            try
            {
                var status = GetSetupService().GetStatus();
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking setup status");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Reports whether the in-page client script is injected into Jellyfin's index.html, so an
        /// admin can diagnose a missing "Generate Subtitles" button/menu without reading server logs.
        /// </summary>
        [HttpGet("Setup/InjectionStatus")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult> GetInjectionStatus()
        {
            try
            {
                var status = Plugin.Instance.GetInjectionStatus();
                status.ServedHtmlVerified = await ProbeServedIndexHtmlAsync().ConfigureAwait(false);
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking client-script injection status");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Re-runs the index.html script injection on demand (config-page button), then returns the
        /// fresh status — lets an admin fix a wiped/missing injection without restarting Jellyfin.
        /// Also re-attempts File Transformation registration (issue #108) so installing that plugin
        /// after WhisperSubs is picked up from here without a second restart.
        /// </summary>
        [HttpPost("Setup/ReinjectScript")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult> ReinjectScript()
        {
            try
            {
                Plugin.Instance.FileTransformation = WebFileTransformation.TryRegister(_logger);
                var status = Plugin.Instance.ReinjectScript();
                status.ServedHtmlVerified = await ProbeServedIndexHtmlAsync().ConfigureAwait(false);
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error re-injecting client script");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Fetches the index.html Jellyfin actually SERVES (locally) and checks for our script marker —
        /// the only ground truth when a serve-time transform (File Transformation) is in play, and a
        /// stronger signal than the on-disk file in every mode. Best-effort: any failure (HTTPS-only
        /// bind, unusual network config) returns "unknown" and never breaks the status endpoint.
        /// </summary>
        private async Task<string> ProbeServedIndexHtmlAsync()
        {
            try
            {
                var baseUrl = _serverApplicationHost.GetApiUrlForLocalAccess(allowHttps: false).TrimEnd('/');
                using var client = _httpClientFactory.CreateClient();
                // One token bounds the WHOLE probe: with ResponseHeadersRead, HttpClient.Timeout only
                // covers the header phase, so the body read needs the same cancellation to stay bounded.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var response = await client.GetAsync(baseUrl + "/web/index.html", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5_000_000)
                {
                    return "unknown";
                }

                var html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return html.Contains(Plugin.ScriptTag, StringComparison.OrdinalIgnoreCase) ? "yes" : "no";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WhisperSubs: served index.html probe failed");
                return "unknown";
            }
        }

        /// <summary>
        /// Lists whisper models available for download from HuggingFace.
        /// </summary>
        [HttpGet("Setup/AvailableModels")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetDownloadableModels()
        {
            return Ok(ModelCatalog.Models.Select(m => new
            {
                m.FileName,
                m.DisplayName,
                m.SizeMB,
                m.IsRecommended,
                m.Description
            }));
        }

        /// <summary>
        /// Downloads a whisper model from HuggingFace. Returns 202 immediately;
        /// poll GET Setup/Progress for status.
        /// </summary>
        [HttpPost("Setup/DownloadModel")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DownloadModel([FromQuery] string name)
        {
            if (string.IsNullOrEmpty(name))
                return BadRequest(new { error = "Model name is required." });

            var catalogEntry = ModelCatalog.Models.FirstOrDefault(m =>
                string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase));
            if (catalogEntry == null)
                return BadRequest(new { error = $"Unknown model: {name}" });

            var canonicalName = catalogEntry.FileName;

            if (!WhisperSetupService.TryAcquire("model", $"Starting download of {canonicalName}..."))
                return Conflict(new { error = "A download is already in progress." });

            var service = GetSetupService();

            _ = Task.Run(async () =>
            {
                try { await service.DownloadModelAsync(canonicalName, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Background model download failed"); }
            });

            return Accepted(new { message = $"Download of {canonicalName} started." });
        }

        /// <summary>
        /// Downloads the Silero VAD model (for whisper-cli --vad speech-onset alignment).
        /// Small (~865 KB). Returns 202 immediately.
        /// </summary>
        [HttpPost("Setup/DownloadVadModel")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DownloadVadModel()
        {
            if (!WhisperSetupService.TryAcquire("vad", "Starting Silero VAD model download..."))
                return Conflict(new { error = "A download is already in progress." });

            var service = GetSetupService();

            _ = Task.Run(async () =>
            {
                try { await service.DownloadVadModelAsync(Plugin.Instance?.Configuration?.VadModelVersion, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Background VAD model download failed"); }
            });

            return Accepted(new { message = "Silero VAD model download started." });
        }

        /// <summary>
        /// Lists available binary variants (CPU, CUDA, Vulkan).
        /// </summary>
        [HttpGet("Setup/BinaryVariants")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetBinaryVariants()
        {
            var platform = WhisperSetupService.GetPlatformIdentifier();
            var variants = BinaryCatalog.GetAvailableVariants(platform);
            return Ok(variants.Select(v => new
            {
                v.Id,
                v.DisplayName,
                v.Description,
                v.IsDefault,
                Platform = platform
            }));
        }

        /// <summary>
        /// Downloads the whisper-cli binary for the current platform from the
        /// whisper-subs GitHub release. Returns 202 immediately.
        /// </summary>
        /// <param name="variant">Binary variant: cpu (default), cuda12, or vulkan.</param>
        [HttpPost("Setup/DownloadBinary")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DownloadBinary([FromQuery] string variant = "cpu")
        {
            var platform = WhisperSetupService.GetPlatformIdentifier();
            var available = BinaryCatalog.GetAvailableVariants(platform);
            var matchedVariant = available.FirstOrDefault(v => string.Equals(v.Id, variant, StringComparison.OrdinalIgnoreCase));
            if (matchedVariant == null)
                return BadRequest(new { error = $"No prebuilt binary for variant '{variant}' on {platform}. Prebuilt binaries are only available for Linux." });

            var canonicalVariant = matchedVariant.Id;

            if (!WhisperSetupService.TryAcquire("binary", $"Starting whisper-cli ({canonicalVariant}) download..."))
                return Conflict(new { error = "A download is already in progress." });

            var service = GetSetupService();

            _ = Task.Run(async () =>
            {
                try { await service.DownloadBinaryAsync(canonicalVariant, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Background binary download failed"); }
            });

            return Accepted(new { message = $"Binary download started (variant: {canonicalVariant})." });
        }

        /// <summary>
        /// Returns the current download progress (model or binary).
        /// </summary>
        [HttpGet("Setup/Progress")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetSetupProgress()
        {
            var p = WhisperSetupService.CurrentProgress;
            return Ok(p);
        }

        /// <summary>
        /// Activates a downloaded model by setting it as the configured model path.
        /// </summary>
        [HttpPost("Setup/Models/{filename}/Activate")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult ActivateModel([FromRoute] string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(filename) || filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                    return BadRequest(new { error = "Invalid filename" });

                var dataDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(dataDir))
                    return StatusCode(500, new { error = "Plugin data directory not available" });

                var modelsDir = System.IO.Path.Combine(dataDir, "whisper", "models");
                var modelPath = System.IO.Path.Combine(modelsDir, filename);

                if (!System.IO.File.Exists(modelPath))
                    return NotFound(new { error = "Model file not found" });

                var config = Plugin.Instance!.Configuration;
                config.WhisperModelPath = modelPath;
                Plugin.Instance.SaveConfiguration();

                _logger.LogInformation("Activated model: {Path}", modelPath);
                return Ok(new { message = "Model activated", path = modelPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating model {Filename}", filename);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Deletes a downloaded model. Cannot delete the active model or the last remaining model.
        /// </summary>
        [HttpDelete("Setup/Models/{filename}")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DeleteModel([FromRoute] string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(filename) || filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                    return BadRequest(new { error = "Invalid filename" });

                var dataDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(dataDir))
                    return StatusCode(500, new { error = "Plugin data directory not available" });

                var modelsDir = System.IO.Path.Combine(dataDir, "whisper", "models");
                var modelPath = System.IO.Path.Combine(modelsDir, filename);

                if (!System.IO.File.Exists(modelPath))
                    return NotFound(new { error = "Model file not found" });

                var config = Plugin.Instance!.Configuration;
                if (string.Equals(config.WhisperModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { error = "Cannot delete the active model. Activate a different model first." });

                var allModels = System.IO.Directory.GetFiles(modelsDir, "*.bin");
                if (allModels.Length <= 1)
                    return BadRequest(new { error = "Cannot delete the last remaining model." });

                System.IO.File.Delete(modelPath);
                _logger.LogInformation("Deleted model: {Path}", modelPath);
                return Ok(new { message = "Model deleted" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting model {Filename}", filename);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private BaseItemKind[] GetBaseItemKinds(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Array.Empty<BaseItemKind>();
            }

            var parts = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var converter = TypeDescriptor.GetConverter(typeof(BaseItemKind));
            var result = new List<BaseItemKind>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (converter.IsValid(trimmed) && Enum.TryParse<BaseItemKind>(trimmed, true, out var kind))
                {
                    result.Add(kind);
                }
            }

            return result.ToArray();
        }

        private static bool CheckLyricsExist(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var dir = System.IO.Path.GetDirectoryName(path);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (dir == null) return false;
            try
            {
                // Check Jellyfin-standard track.lrc and language-tagged track.*.lrc
                var exactLrc = System.IO.Path.Combine(dir, baseName + ".lrc");
                return System.IO.File.Exists(exactLrc) || System.IO.Directory.GetFiles(dir, baseName + ".*.lrc").Length > 0;
            }
            catch { return false; }
        }
    }

    public class LibraryInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class ItemInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? Path { get; set; }
        public bool HasSubtitles { get; set; }
    }

    public class SubtitleStatus
    {
        public string ItemId { get; set; } = string.Empty;
        public bool HasGeneratedSubtitle { get; set; }
        public bool HasForcedSubtitle { get; set; }
        public bool HasExistingSubtitles { get; set; }
        public string? SubtitlePath { get; set; }
    }

    public class ModelInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public double SizeMB { get; set; }
        public bool IsActive { get; set; }
    }

    public class PagedItemResult
    {
        public List<ItemInfo> Items { get; set; } = new List<ItemInfo>();
        public int TotalCount { get; set; }
        public int StartIndex { get; set; }
    }

    /// <summary>Body for the worker "Test connection" probe (v4.0): the endpoint + optional key/model to try.</summary>
    public class WorkerTestRequest
    {
        public string? ApiUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
    }
}

