using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The worker pool dialect for translation targets: a worker advertises the set of languages it
/// translates into, jobs for a Canary target route only to workers that list it, and a CrispASR server
/// gets every request on /v1/audio/transcriptions with the target expressed as form fields.
/// </summary>
public class WorkerTranslateTargetsTests
{
    // ── Row back-compat: CanTranslate -> TranslateTargets ──────────────────

    [Theory]
    [InlineData(true, new[] { "en" })]
    [InlineData(false, new string[0])]
    public void ForRow_RowWithoutTheNewField_MapsTheOldFlag(bool canTranslate, string[] expected)
    {
        var row = new WhisperWorker { CanTranslate = canTranslate };
        Assert.Empty(row.TranslateTargets);
        Assert.True(WorkerTargets.ForRow(row).SetEquals(expected));
    }

    [Fact]
    public void ForRow_NewFieldWinsOverTheOldFlag()
    {
        // The config page writes CanTranslate = (list contains en), so a Dutch-only row has it false.
        var row = new WhisperWorker { CanTranslate = false, TranslateTargets = new List<string> { " NL", "de" }, Dialect = "crispasr" };
        Assert.True(WorkerTargets.ForRow(row).SetEquals(new[] { "nl", "de" }));

        row.TranslateTargets = new List<string> { "en", "nl" };
        row.CanTranslate = true;
        Assert.True(WorkerTargets.ForRow(row).SetEquals(new[] { "nl", "en" }));

        // A row that translates nothing: empty list with CanTranslate false.
        row.TranslateTargets = new List<string>();
        row.CanTranslate = false;
        Assert.Empty(WorkerTargets.ForRow(row));
    }

    // An OpenAI-compatible endpoint would answer a Dutch request with English, so the pool never
    // routes a Canary target to it, even if a hand-edited config lists one. Unknown codes drop too.
    [Fact]
    public void ForRow_OpenAiDialect_KeepsOnlyEnglish_AndUnknownCodesDrop()
    {
        var row = new WhisperWorker { TranslateTargets = new List<string> { "en", "nl", "ja" }, Dialect = "openai" };
        Assert.True(WorkerTargets.ForRow(row).SetEquals(new[] { "en" }));

        row.Dialect = "crispasr";
        Assert.True(WorkerTargets.ForRow(row).SetEquals(new[] { "en", "nl" }));
    }

    // The persisted config is XML: a row written before the field existed has only CanTranslate. The
    // serializer turns the absent list element into an EMPTY list (not null), which is why the mapping
    // keys on "empty", not "null": keying on null would silently make every legacy worker non-translating.
    [Theory]
    [InlineData("true", new[] { "en" })]
    [InlineData("false", new string[0])]
    public void LegacyXmlRow_DeserializesAndMapsCanTranslate(string canTranslate, string[] expected)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-16"?>
            <WhisperWorker xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <Id>abc</Id>
              <ApiUrl>http://worker.example:9010</ApiUrl>
              <CanTranslate>{canTranslate}</CanTranslate>
            </WhisperWorker>
            """;
        using var reader = new StringReader(xml);
        var row = (WhisperWorker)new XmlSerializer(typeof(WhisperWorker)).Deserialize(reader)!;

        Assert.Empty(row.TranslateTargets);
        Assert.Equal(WorkerDialect.OpenAi, WorkerDialect.Normalize(row.Dialect));
        Assert.True(WorkerTargets.ForRow(row).SetEquals(expected));
    }

    // The config page talks JSON: an old page (or API client) posts CanTranslate without the list.
    [Fact]
    public void LegacyJsonRow_DeserializesAndMapsCanTranslate()
    {
        var row = JsonSerializer.Deserialize<WhisperWorker>("""{"Id":"abc","ApiUrl":"http://worker.example:9010","CanTranslate":false}""")!;
        Assert.Empty(row.TranslateTargets);
        Assert.Empty(WorkerTargets.ForRow(row));

        var english = JsonSerializer.Deserialize<WhisperWorker>("""{"Id":"abc","ApiUrl":"http://worker.example:9010","CanTranslate":true}""")!;
        Assert.True(WorkerTargets.ForRow(english).SetEquals(new[] { "en" }));

        var roundTrip = JsonSerializer.Deserialize<WhisperWorker>(JsonSerializer.Serialize(
            new WhisperWorker { TranslateTargets = new List<string> { "en", "nl" }, Dialect = "crispasr" }))!;
        Assert.Equal(new[] { "en", "nl" }, roundTrip.TranslateTargets);
        Assert.Equal("crispasr", roundTrip.Dialect);
    }

    [Fact]
    public void XmlRoundTrip_KeepsTargetsAndDialect()
    {
        var serializer = new XmlSerializer(typeof(WhisperWorker));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new WhisperWorker { TranslateTargets = new List<string> { "en", "de" }, Dialect = "crispasr" });
        using var reader = new StringReader(writer.ToString());
        var row = (WhisperWorker)serializer.Deserialize(reader)!;

        Assert.Equal(new[] { "en", "de" }, row.TranslateTargets);
        Assert.True(WorkerTargets.ForRow(row).SetEquals(new[] { "en", "de" }));
    }

    // ── Local worker ───────────────────────────────────────────────────────

    [Fact]
    public void ForLocal_OffersEveryCanaryTargetOnlyWhenInstalled()
    {
        var installed = WorkerTargets.ForLocal(canaryInstalled: true);
        Assert.Equal(25, installed.Count);
        Assert.Contains("en", installed);
        Assert.All(CanaryCatalog.Targets, t => Assert.Contains(t.Code, installed));
        Assert.Contains("ES", installed);   // case-insensitive like every target set

        Assert.True(WorkerTargets.ForLocal(canaryInstalled: false).SetEquals(new[] { "en" }));
    }

    // ── Served by the configuration (no pool yet) ─────────────────────────

    [Fact]
    public void ServedByConfig_Rules()
    {
        Assert.True(WorkerTargets.ServedByConfig("es", canaryInstalled: true, hostsLocal: true, rows: null));
        Assert.False(WorkerTargets.ServedByConfig("es", canaryInstalled: true, hostsLocal: false, rows: null));
        Assert.False(WorkerTargets.ServedByConfig("es", canaryInstalled: false, hostsLocal: true, rows: new List<WhisperWorker>()));

        var disabled = Row("crispasr", "es");
        disabled.Enabled = false;
        var blankUrl = Row("crispasr", "es");
        blankUrl.ApiUrl = "  ";
        Assert.False(WorkerTargets.ServedByConfig("es", false, false, new[] { disabled, blankUrl }));

        Assert.True(WorkerTargets.ServedByConfig("es", false, false, new[] { Row("crispasr", "en", "es") }));
        Assert.False(WorkerTargets.ServedByConfig("nl", false, false, new[] { Row("crispasr", "en", "es") }));
        // An OpenAI-dialect row can only translate into English, whatever it lists.
        Assert.False(WorkerTargets.ServedByConfig("es", false, false, new[] { Row("openai", "en", "es") }));
    }

    // ── Which provider makes a target ──────────────────────────────────────

    private sealed class NamedProvider : ISubtitleProvider
    {
        public NamedProvider(string name) => Name = name;
        public string Name { get; }
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false, string? targetLanguage = null)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    [Fact]
    public void TargetProviders_EnglishUsesTheWhisperProvider_OthersTheTargetProvider()
    {
        var whisper = new NamedProvider("whisper");
        var canary = new NamedProvider("canary");
        var caps = new WorkerCapabilities { TranslateTargets = WorkerTargets.ForLocal(true) };
        var local = new TranscriptionWorker("local", "Local", whisper, caps, canary);
        var remote = new TranscriptionWorker("r", "Remote", whisper, caps);

        Assert.Same(whisper, TargetProviders.For(local, "en"));
        Assert.Same(whisper, TargetProviders.For(local, "EN"));
        Assert.Same(canary, TargetProviders.For(local, "es"));
        Assert.Same(whisper, TargetProviders.For(remote, "es"));   // no target provider: the worker's own
    }

    // ── Config field parsing and validation ────────────────────────────────

    [Fact]
    public void Parse_SplitsTrimsLowersAndDedupes()
    {
        Assert.Equal(new[] { "en", "nl", "de" }, WorkerTargets.Parse(" en, NL ,de;nl "));
        Assert.Empty(WorkerTargets.Parse(""));
        Assert.Empty(WorkerTargets.Parse(null));
    }

    private static WhisperWorker Row(string dialect, params string[] targets)
        => new() { ApiUrl = "http://worker.example:9010", Dialect = dialect, TranslateTargets = targets.ToList() };

    [Fact]
    public void Validate_CrispAsrRowWithCanaryTargets_IsOk()
    {
        Assert.True(WorkerConfigValidation.Validate(Row("crispasr", "en", "nl", "lv")).Ok);
        Assert.True(WorkerConfigValidation.Validate(Row("crispasr")).Ok);   // translates nothing
    }

    [Fact]
    public void Validate_UnknownTarget_IsRejectedByName()
    {
        var (ok, error) = WorkerConfigValidation.Validate(Row("crispasr", "en", "ja", "xx"));
        Assert.False(ok);
        Assert.Contains("ja, xx", error);
    }

    [Fact]
    public void Validate_BlankTarget_IsRejected()
    {
        var (ok, error) = WorkerConfigValidation.Validate(Row("crispasr", "en", " "));
        Assert.False(ok);
        Assert.Contains("(blank)", error);
    }

    [Fact]
    public void Validate_OpenAiRowWithACanaryTarget_IsRejected()
    {
        Assert.True(WorkerConfigValidation.Validate(Row("openai", "en")).Ok);
        var (ok, error) = WorkerConfigValidation.Validate(Row("openai", "en", "nl"));
        Assert.False(ok);
        Assert.Contains("CrispASR", error);
    }

    [Fact]
    public void Validate_UnknownDialect_IsRejected()
    {
        var row = Row("openai", "en");
        row.Dialect = "whisperx";
        Assert.False(WorkerConfigValidation.Validate(row).Ok);
        row.Dialect = "";
        Assert.True(WorkerConfigValidation.Validate(row).Ok);   // blank = default
    }

    // ── Scheduling on target membership ────────────────────────────────────

    private static WorkerSlot Slot(string id, params string[] targets)
        => new(id, true, 0, new WorkerCapabilities { TranslateTargets = WorkerTargets.Set(targets) });

    [Fact]
    public void CanServe_TargetJob_NeedsTheTargetInTheSet()
    {
        var nl = WorkerJob.ForTarget(" NL ");
        Assert.Equal("nl", nl.TranslateTarget);
        Assert.False(WorkerScheduling.CanServe(Slot("whisper", "en"), nl));
        Assert.True(WorkerScheduling.CanServe(Slot("crisp", "en", "nl"), nl));
        Assert.False(WorkerScheduling.CanServe(Slot("crisp", "en", "de"), nl));
        // English jobs keep working on both, transcription on everything.
        Assert.True(WorkerScheduling.CanServe(Slot("whisper", "en"), WorkerJob.ForTarget("en")));
        Assert.True(WorkerScheduling.CanServe(Slot("none"), new JobRequirements(null, null)));
    }

    [Fact]
    public void Pick_RoutesATargetJobToTheOnlyWorkerThatListsIt()
    {
        var slots = new[] { Slot("local", "en"), Slot("crisp", "en", "nl") };
        Assert.Equal("crisp", WorkerScheduling.Pick(slots, WorkerJob.ForTarget("nl"))!.Value.Id);
    }

    // A slot built from a configured row the way WorkerRegistry.BuildRemote builds it.
    private static WorkerSlot RowSlot(string dialect, params string[] targets)
    {
        var row = new WhisperWorker { Dialect = dialect, TranslateTargets = targets.ToList(), CanTranslate = targets.Contains("en") };
        var set = WorkerTargets.ForRow(row);
        return new(dialect, true, 0, new WorkerCapabilities
        {
            TranslateTargets = set,
            TranscribesItems = WorkerTargets.TranscribesItems(row.Dialect, set),
        });
    }

    private static readonly JobRequirements[] WholeItemJobs =
    {
        WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: true),
        WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: false),
        WorkerJob.Requirements(SubtitleMode.ForcedOnly, enableTranslation: true),
        WorkerJob.Requirements(SubtitleMode.TranslationOnly, enableTranslation: false),
    };

    // A CrispASR row without 'en' is there for the extra languages only. It must never take a whole
    // title, or a server started with Canary would transcribe it with Canary, translation on or off.
    [Fact]
    public void CrispAsrRowWithoutEnglish_ServesOnlyItsTargets()
    {
        var crispNl = RowSlot(WorkerDialect.CrispAsr, "nl");
        Assert.All(WholeItemJobs, job => Assert.False(WorkerScheduling.CanServe(crispNl, job)));
        Assert.True(WorkerScheduling.CanServe(crispNl, WorkerJob.ForTarget("nl")));
        Assert.False(WorkerScheduling.CanServe(crispNl, WorkerJob.ForTarget("de")));

        var pool = new WorkerPool(new[] { new TranscriptionWorker("crisp", "crisp", new FakeProvider(), crispNl.Capabilities) });
        Assert.False(pool.HasCapableWorker(WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: false)));
        Assert.True(pool.HasCapableWorker(WorkerJob.ForTarget("nl")));
    }

    [Fact]
    public void CrispAsrRowWithEnglish_ServesWholeItemsAndItsTargets()
    {
        var crispEnNl = RowSlot(WorkerDialect.CrispAsr, "en", "nl");
        Assert.All(WholeItemJobs, job => Assert.True(WorkerScheduling.CanServe(crispEnNl, job)));
        Assert.True(WorkerScheduling.CanServe(crispEnNl, WorkerJob.ForTarget("nl")));
    }

    // The OpenAI dialect is unchanged: whole items always, English translation only when listed, and
    // never a Canary target.
    [Fact]
    public void OpenAiRow_IsUnchanged()
    {
        var openAiEn = RowSlot(WorkerDialect.OpenAi, "en");
        Assert.All(WholeItemJobs, job => Assert.True(WorkerScheduling.CanServe(openAiEn, job)));
        Assert.False(WorkerScheduling.CanServe(openAiEn, WorkerJob.ForTarget("nl")));

        var openAiTranscribeOnly = RowSlot(WorkerDialect.OpenAi);
        Assert.True(openAiTranscribeOnly.Capabilities.TranscribesItems);
        Assert.True(WorkerScheduling.CanServe(openAiTranscribeOnly, WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: false)));
        Assert.False(WorkerScheduling.CanServe(openAiTranscribeOnly, WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: true)));
    }

    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "Fake";
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false, string? targetLanguage = null)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    private static ITranscriptionWorker Worker(string id, params string[] targets)
        => new TranscriptionWorker(id, id, new FakeProvider(), new WorkerCapabilities { TranslateTargets = WorkerTargets.Set(targets) });

    // Fail-fast still works per target: nothing lists 'nl', so a job for it is refused up front.
    [Fact]
    public async Task HasCapableWorker_TargetNobodyLists_FailsFast()
    {
        var pool = new WorkerPool(new[] { Worker("local", "en") });
        Assert.False(pool.HasCapableWorker(WorkerJob.ForTarget("nl")));
        Assert.True(pool.HasCapableWorker(WorkerJob.ForTarget("en")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync(WorkerJob.ForTarget("nl"), default));

        var withCrisp = new WorkerPool(new[] { Worker("local", "en"), Worker("crisp", "en", "nl") });
        Assert.True(withCrisp.HasCapableWorker(WorkerJob.ForTarget("nl")));
    }

    [Fact]
    public void TryAcquire_NeverWaits()
    {
        var pool = new WorkerPool(new[] { Worker("local", "en"), Worker("crisp", "en", "nl") });

        // Only an incapable worker is free: no lease, and the slot is handed back.
        var crispBusy = pool.TryAcquire(WorkerJob.ForTarget("nl"));
        Assert.Equal("crisp", crispBusy!.Value.Worker.Id);
        Assert.Null(pool.TryAcquire(WorkerJob.ForTarget("nl")));
        Assert.Equal(1, pool.ActiveJobs);

        // Every slot taken: no lease.
        var localBusy = pool.TryAcquire(new JobRequirements(null, null));
        Assert.Equal("local", localBusy!.Value.Worker.Id);
        Assert.Null(pool.TryAcquire(new JobRequirements(null, null)));

        pool.Release(crispBusy.Value.Key);
        Assert.Equal("crisp", pool.TryAcquire(WorkerJob.ForTarget("nl"))!.Value.Worker.Id);
    }

    // ── Target lease routing inside an item ────────────────────────────────

    [Fact]
    public void LeaseRouting_OwnWorkerListsTheTarget_UsesIt()
        => Assert.Equal(TargetLeasePolicy.UseOwnWorker, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "nl"), "nl"));

    [Fact]
    public void LeaseRouting_OwnWorkerServesNoCanaryTarget_Waits()
    {
        Assert.Equal(TargetLeasePolicy.WaitForAnother, TargetLeaseRouting.Decide(WorkerTargets.Set("en"), "nl"));
        Assert.Equal(TargetLeasePolicy.WaitForAnother, TargetLeaseRouting.Decide(WorkerTargets.None, "nl"));
    }

    // Two items on workers with different partial target sets could wait on each other forever.
    [Fact]
    public void LeaseRouting_OwnWorkerServesOtherCanaryTargets_NeverWaits()
        => Assert.Equal(TargetLeasePolicy.TakeFreeAnotherOnly, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "de"), "nl"));

    // ── The local worker carries the Canary provider ───────────────────────

    [Fact]
    public void TranscriptionWorker_TargetProviderDefaultsToNone()
    {
        ITranscriptionWorker plain = Worker("remote", "en", "nl");
        Assert.Null(plain.TargetProvider);

        var canary = new FakeProvider();
        ITranscriptionWorker local = new TranscriptionWorker("local", "Local", new FakeProvider(), new WorkerCapabilities(), canary);
        Assert.Same(canary, local.TargetProvider);
    }

    [Fact]
    public void TargetEngineLease_ReleasesExactlyOnce()
    {
        var released = 0;
        var lease = new TargetEngineLease(new FakeProvider(), "crisp", () => released++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, released);
        Assert.Equal("crisp", lease.WorkerName);

        new TargetEngineLease(new FakeProvider(), "own", null).Dispose();   // own worker: nothing to release
    }

    // ── Request building per dialect ───────────────────────────────────────

    private static void AssertRequest(string expectedPath, string[] expectedFields, string dialect, string? language, bool translate, string? target)
    {
        var (path, fields) = RemoteWhisperProvider.BuildTranscriptionRequest(dialect, "m", "srt", language, translate, target);
        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedFields, fields.Select(f => $"{f.Name}={f.Value}").ToArray());
    }

    [Fact]
    public void OpenAiDialect_RequestsAreUnchanged()
    {
        AssertRequest("/v1/audio/transcriptions", new[] { "model=m", "response_format=srt", "language=es" },
            "openai", "es", translate: false, null);
        AssertRequest("/v1/audio/transcriptions", new[] { "model=m", "response_format=srt" },
            "openai", "auto", translate: false, null);
        // Translation never sends the source language (OpenAI and Groq reject it there).
        AssertRequest("/v1/audio/translations", new[] { "model=m", "response_format=srt" },
            "openai", "es", translate: true, null);
    }

    [Fact]
    public void CrispAsrDialect_Transcribe()
        => AssertRequest("/v1/audio/transcriptions", new[] { "model=m", "response_format=srt", "language=es" },
            "crispasr", "es", translate: false, null);

    [Fact]
    public void CrispAsrDialect_TranslateToEnglish()
        => AssertRequest("/v1/audio/transcriptions", new[] { "model=m", "response_format=srt", "language=es", "translate=true" },
            "crispasr", "es", translate: true, null);

    [Fact]
    public void CrispAsrDialect_CanaryTarget()
        => AssertRequest("/v1/audio/transcriptions", new[] { "model=m", "response_format=srt", "language=en", "source_lang=en", "target_lang=nl" },
            "crispasr", "en", translate: true, "nl");

    [Theory]
    [InlineData(true, "nl", "nl")]
    [InlineData(true, " DE ", "de")]
    [InlineData(true, "en", null)]
    [InlineData(true, null, null)]
    [InlineData(false, "nl", null)]
    public void CanaryTargetOrNull_OnlyForANonEnglishTranslation(bool translate, string? target, string? expected)
        => Assert.Equal(expected, RemoteWhisperProvider.CanaryTargetOrNull(translate, target));

    // A CrispASR server answers an unsupported target with HTTP 200 and an empty body.
    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData(null)]
    public void RequireSrtCues_EmptyBodyForATarget_NamesTheTarget(string? body)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.RequireSrtCues(body, "nl"));
        Assert.Contains("'nl'", ex.Message);

        var plain = Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.RequireSrtCues(body, null));
        Assert.Equal("Remote Whisper API returned empty response", plain.Message);
    }

    [Fact]
    public void RequireSrtCues_PassesARealBodyThrough()
        => Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\nHoi", RemoteWhisperProvider.RequireSrtCues("1\n00:00:00,000 --> 00:00:01,000\nHoi", "nl"));

    // The crispasr dialect accepts a Canary target (it reaches the audio-file check); the openai
    // dialect still refuses it before anything else.
    [Fact]
    public async Task CrispAsrDialect_AcceptsACanaryTarget_OpenAiStillRefuses()
    {
        var crisp = new RemoteWhisperProvider(NullLogger<RemoteWhisperProvider>.Instance, "https://worker.example", "canary", dialect: "crispasr");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            crisp.TranscribeAsync("/nonexistent/a.wav", "en", CancellationToken.None, translate: true, targetLanguage: "nl"));

        var openai = new RemoteWhisperProvider(NullLogger<RemoteWhisperProvider>.Instance, "https://worker.example", "whisper-large-v3", dialect: "openai");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            openai.TranscribeAsync("/nonexistent/a.wav", "en", CancellationToken.None, translate: true, targetLanguage: "nl"));
    }

    // ── Worker signature ───────────────────────────────────────────────────

    [Fact]
    public void WorkerSignature_ChangesWithTargetsAndDialect_NotWithEquivalentLegacyRows()
    {
        PluginConfiguration With(WhisperWorker w) { var c = new PluginConfiguration(); c.Workers.Add(w); return c; }
        WhisperWorker Base() => new() { Id = "w", ApiUrl = "http://worker.example:9010" };

        var legacy = SubtitleQueueService.ComputeWorkersSignature(With(Base()));
        var explicitEnglish = Base(); explicitEnglish.TranslateTargets = new List<string> { "en" };
        var noTranslate = Base(); noTranslate.CanTranslate = false;
        var withNl = Base(); withNl.TranslateTargets = new List<string> { "en", "nl" }; withNl.Dialect = "crispasr";
        var crispEnglish = Base(); crispEnglish.TranslateTargets = new List<string> { "en" }; crispEnglish.Dialect = "crispasr";

        Assert.Equal(legacy, SubtitleQueueService.ComputeWorkersSignature(With(explicitEnglish)));
        Assert.NotEqual(legacy, SubtitleQueueService.ComputeWorkersSignature(With(withNl)));
        Assert.NotEqual(legacy, SubtitleQueueService.ComputeWorkersSignature(With(crispEnglish)));
        Assert.NotEqual(legacy, SubtitleQueueService.ComputeWorkersSignature(With(noTranslate)));
    }
}
