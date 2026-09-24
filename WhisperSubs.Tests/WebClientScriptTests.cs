using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The item-page script (Web/whisperSubs.js), read from the plugin assembly exactly as Jellyfin serves it.
/// Jellyfin's web client builds <c>window.ApiClient</c> from jellyfin-apiclient, whose <c>ajax</c> parses a
/// 2xx body only when the call passes <c>dataType: 'json'</c>; otherwise it resolves with the raw fetch
/// Response. Every endpoint this script calls answers application/json, so a call without it reads
/// <c>undefined</c> fields: the Translate list never rendered, the admin was told "Queued 0", and
/// viewers never saw their controls because <c>caps.enabled</c> was undefined.
/// </summary>
public class WebClientScriptTests
{
    private static string Script()
    {
        using var stream = typeof(WhisperSubs.Plugin).Assembly.GetManifestResourceStream("WhisperSubs.Web.whisperSubs.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void EveryApiCall_AsksForAParsedJsonBody()
    {
        var calls = Regex.Matches(Script(), @"ApiClient\.ajax\(\{[^}]*\}\)").Select(m => m.Value).ToList();

        // Capabilities, TranslationTargets, RequestStatus, GenerateAll, Request, Translate, Request?target.
        Assert.Equal(7, calls.Count);
        Assert.All(calls, call => Assert.Contains("dataType: 'json'", call));
    }
}
