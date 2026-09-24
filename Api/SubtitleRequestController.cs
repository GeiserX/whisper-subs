using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using WhisperSubs.Setup;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Api
{
    /// <summary>
    /// User-facing subtitle requests (#112). Deliberately a SEPARATE controller from the admin
    /// <see cref="SubtitleController"/> so that controller keeps its blanket
    /// <c>[Authorize(Policy="RequiresElevation")]</c> — opening one endpoint by removing the class-level
    /// elevation would be fail-open (Jellyfin has no fallback policy, so an un-attributed method becomes
    /// public). Every method here re-derives the user from the authenticated session, enforces per-user
    /// visibility on the item, assigns the tier server-side, and lets <see cref="SubtitleRequestStore"/>
    /// enforce quota + caps + de-dup. The injected client script is served anonymously and is trusted
    /// for nothing — all of that is checked here, on the server.
    /// </summary>
    [ApiController]
    [Route("Plugins/WhisperSubs")]
    [Authorize] // any authenticated user (the default policy); NOT admin. Gated further by AllowUserRequests.
    [ExcludeFromCodeCoverage(Justification = "Thin API layer over Jellyfin runtime services; logic lives in unit-tested pure helpers")]
    public class SubtitleRequestController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<SubtitleRequestController> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public SubtitleRequestController(
            ILibraryManager libraryManager,
            IAuthorizationContext authContext,
            ILogger<SubtitleRequestController> logger,
            ILoggerFactory loggerFactory)
        {
            _libraryManager = libraryManager;
            _authContext = authContext;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Tells the client whether the request feature is on (so the injected script can show or hide
        /// the "Request subtitles" entry). Safe for any authenticated user — no per-item or other-user data.
        /// </summary>
        [HttpGet("Requests/Capabilities")]
        public ActionResult GetCapabilities()
        {
            var c = Plugin.Instance.Configuration;
            return Ok(new
            {
                enabled = c.AllowUserRequests,
                autoApprove = c.AutoApproveUserRequests,
                userTier = c.UserRequestTier.ToString(),
                dailyQuota = c.UserRequestDailyQuota,
                activeCap = c.UserRequestActiveCap
            });
        }

        /// <summary>
        /// The languages a title can be translated into: English, then the 24 Canary languages, as
        /// <c>[{ code, name }]</c>. Static catalog data, so it is not gated by AllowUserRequests; the item page
        /// reads its list from here (viewers cannot call the admin-only engine status).
        /// </summary>
        [HttpGet("TranslationTargets")]
        public ActionResult GetTranslationTargets()
            => Ok(CanaryCatalog.RequestableTargets.Select(t => new { code = t.Code, name = t.DisplayName }));

        /// <summary>
        /// Creates a subtitle request for an item the user can see. Returns 202 (or 200 if a duplicate).
        /// With <paramref name="target"/> it is a translation request into that one language instead; the
        /// language must then be "auto" or absent, and a target no engine can make is refused with 409.
        /// </summary>
        [HttpPost("Items/{itemId}/Request")]
        public async Task<ActionResult> RequestSubtitles(
            [FromRoute] string itemId,
            [FromQuery] string? language = null,
            [FromQuery] string? target = null)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.AllowUserRequests)
            {
                return NotFound(new { error = "Subtitle requests are not enabled on this server." });
            }

            var authInfo = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            if (authInfo?.User == null || authInfo.IsApiKey)
            {
                // API-key calls satisfy [Authorize] but have no user → no visibility context. Reject.
                return Unauthorized(new { error = "A signed-in user session is required to request subtitles." });
            }
            var user = authInfo.User;

            if (!Guid.TryParse(itemId, out var guid))
            {
                return NotFound(new { error = "Item not found" });
            }

            var item = _libraryManager.GetItemById(guid);
            // null OR not visible to this user → 404 (never 403: don't confirm an item they can't see exists).
            if (item == null || !item.IsVisibleStandalone(user))
            {
                return NotFound(new { error = "Item not found" });
            }

            // Allow-list the only kinds a request may target — media leaves + series/season/album — so a
            // BoxSet, MusicArtist, plain folder, or library root can't fan out over the whole library.
            if (!MediaItemResolver.IsAllowedGenerateTarget(item))
            {
                return BadRequest(new { error = "This item type cannot be requested. Select a movie, episode, series, season, or album." });
            }

            var lang = RequestValidation.NormalizeLanguage(language, config.DefaultLanguage);
            if (lang == null)
            {
                return BadRequest(new { error = "Unsupported language." });
            }

            // A translation request: one target, source language read from the audio.
            string? translateTarget = null;
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (!RequestValidation.IsTranslationLanguageAllowed(language))
                {
                    return BadRequest(new { error = "A translation request reads the language from the audio. Send language=auto or leave it out." });
                }

                translateTarget = RequestValidation.NormalizeTranslationTarget(target);
                if (translateTarget == null)
                {
                    return BadRequest(new { error = "Unsupported target language." });
                }

                if (!MediaItemResolver.IsTranslatableTarget(item))
                {
                    return BadRequest(new { error = "This item type cannot be translated. Select a movie, episode, series or season." });
                }

                // Never the default language: the job reads the audio.
                lang = "auto";
            }

            var leaves = MediaItemResolver.ResolveLeafItems(
                item, translateTarget == null && config.EnableLyricsGeneration, _libraryManager);
            if (leaves.Count == 0)
            {
                return BadRequest(new { error = "No subtitle-eligible media found for this item." });
            }
            if (config.UserRequestMaxItemsPerRequest > 0 && leaves.Count > config.UserRequestMaxItemsPerRequest)
            {
                return BadRequest(new
                {
                    error = $"That request covers {leaves.Count} items, above the per-request limit of {config.UserRequestMaxItemsPerRequest}."
                });
            }

            // Refused before TryCreate, so a request nothing could serve spends no quota.
            if (translateTarget != null
                && SubtitleQueueService.Instance.TargetEngineUnavailableReason(config, translateTarget) is { } engineReason)
            {
                return Conflict(new { error = engineReason });
            }

            var now = DateTime.UtcNow.Ticks;
            var window = TimeSpan.FromHours(Math.Max(1, config.UserRequestQuotaWindowHours)).Ticks;
            // Tier assigned server-side from the requester's role — never from client input (#112).
            var userTier = PriorityScheduling.ResolveTier(
                RequesterKind.User, config.AdminRequestTier, config.UserRequestTier, config.BackgroundSweepTier);
            var store = SubtitleRequestStore.Instance;

            var result = store.TryCreate(
                itemId: guid.ToString("N"),
                itemName: item.Name ?? string.Empty,
                itemType: item.GetType().Name,
                language: lang,
                userId: user.Id.ToString("N"),
                userName: user.Username ?? string.Empty,
                tier: userTier,
                itemCount: leaves.Count,
                autoApprove: config.AutoApproveUserRequests,
                nowTicks: now,
                windowTicks: window,
                dailyQuota: config.UserRequestDailyQuota,
                activeCap: config.UserRequestActiveCap,
                globalCap: config.UserRequestGlobalCap,
                target: translateTarget);

            switch (result.Outcome)
            {
                case RequestCreateOutcome.QuotaExceeded:
                    return StatusCode(429, new { error = "You've reached your subtitle-request limit for now. Please try again later." });
                case RequestCreateOutcome.ActiveCapExceeded:
                    return StatusCode(429, new { error = "You already have the maximum number of pending or queued requests." });
                case RequestCreateOutcome.GlobalCapExceeded:
                    return StatusCode(503, new { error = "The request queue is full right now. Please try again later." });
                case RequestCreateOutcome.DuplicateActive:
                    return Ok(new
                    {
                        message = "You have already requested this.",
                        state = result.Request!.State.ToString(),
                        requestId = result.Request.Id
                    });
            }

            var req = result.Request!;
            _logger.LogInformation("[Request] {User} requested {Item} ({Count} item(s)) [{Tier}] → {State}",
                user.Username, item.Name, leaves.Count, userTier, req.State);

            if (config.AutoApproveUserRequests)
            {
                SubtitleRequestService.EnqueueRequest(req.ItemId, lang, userTier, config, _libraryManager, _loggerFactory, _logger, translateTarget);
            }

            return Accepted(new
            {
                message = config.AutoApproveUserRequests
                    ? "Your subtitle request was queued."
                    : "Your subtitle request was submitted and is awaiting approval.",
                state = req.State.ToString(),
                requestId = req.Id,
                itemCount = leaves.Count,
                target = translateTarget
            });
        }

        /// <summary>Returns the signed-in user's OWN requests only — never anyone else's, never file paths.</summary>
        [HttpGet("Requests/Mine")]
        public async Task<ActionResult> GetMyRequests()
        {
            var config = Plugin.Instance.Configuration;
            if (!config.AllowUserRequests)
            {
                // Behave as if the feature does not exist when disabled (parity with the request endpoint).
                return NotFound(new { error = "Subtitle requests are not enabled on this server." });
            }

            var authInfo = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            if (authInfo?.User == null || authInfo.IsApiKey)
            {
                return Unauthorized(new { error = "A signed-in user session is required." });
            }

            var mine = SubtitleRequestStore.Instance.GetByUser(authInfo.User.Id.ToString("N"));
            return Ok(mine.Select(r => new
            {
                requestId = r.Id,
                itemId = r.ItemId,
                itemName = r.ItemName,
                language = r.Language,
                state = r.State.ToString(),
                itemCount = r.ItemCount,
                target = r.Target,
                created = new DateTime(r.CreatedTicks, DateTimeKind.Utc)
            }));
        }

        /// <summary>This user's active request state for a specific item (drives the item-page badge).</summary>
        [HttpGet("Items/{itemId}/RequestStatus")]
        public async Task<ActionResult> GetItemRequestStatus([FromRoute] string itemId)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.AllowUserRequests)
            {
                // Behave as if the feature does not exist when disabled (parity with the request endpoint).
                return NotFound(new { error = "Subtitle requests are not enabled on this server." });
            }

            var authInfo = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            if (authInfo?.User == null || authInfo.IsApiKey)
            {
                return Unauthorized(new { error = "A signed-in user session is required." });
            }

            if (!Guid.TryParse(itemId, out var guid))
            {
                return NotFound(new { error = "Item not found" });
            }

            var normId = guid.ToString("N");
            var mine = SubtitleRequestStore.Instance
                .GetByUser(authInfo.User.Id.ToString("N"))
                // Subtitle requests only: the badge means "Request Subs", not a translation.
                .FirstOrDefault(r => r.ItemId == normId && r.Target == null && RequestPolicy.IsActive(r.State));

            return Ok(new { enabled = true, state = mine?.State.ToString() });
        }
    }
}
