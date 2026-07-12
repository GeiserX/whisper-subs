using System;
using System.Collections.Generic;
using System.IO;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Issue #110: the scheduled task re-probed the filesystem for every item every run (~40 min on a
/// 13k library just to re-confirm existing subtitles). The skip cache remembers a per-item "already
/// satisfied" verdict, invalidated by a change token (DateLastSaved) + a settings signature + a
/// backstop TTL — never a blind time cache. These pin that decision + persistence logic.
/// </summary>
public class SubtitleSkipCacheTests
{
    private static PluginConfiguration BaseConfig() => new()
    {
        SubtitleMode = SubtitleMode.Full,
        EnableTranslation = false,
        GenerateOriginalLanguageSubtitles = true,
        SkipIfSubtitleExists = true,
        IgnoreForcedSubtitles = true,
        CountImageSubtitlesAsPresent = false,
        EnableLyricsGeneration = false,
        DefaultLanguage = "auto"
    };

    // ── ComputeSignature ────────────────────────────────────────────────────

    [Fact]
    public void Signature_IsStable_ForEqualConfig()
    {
        Assert.Equal(SubtitleSkipCache.ComputeSignature(BaseConfig()), SubtitleSkipCache.ComputeSignature(BaseConfig()));
    }

    public static IEnumerable<object[]> SignatureMutations()
    {
        yield return new object[] { (Action<PluginConfiguration>)(c => c.SubtitleMode = SubtitleMode.FullAndForced) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.EnableTranslation = true) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.GenerateOriginalLanguageSubtitles = false) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.SkipIfSubtitleExists = false) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.IgnoreForcedSubtitles = false) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.CountImageSubtitlesAsPresent = true) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.EnableLyricsGeneration = true) };
        yield return new object[] { (Action<PluginConfiguration>)(c => c.DefaultLanguage = "es") };
    }

    [Theory]
    [MemberData(nameof(SignatureMutations))]
    public void Signature_Changes_WhenAnySkipAffectingToggleChanges(Action<PluginConfiguration> mutate)
    {
        var baseSig = SubtitleSkipCache.ComputeSignature(BaseConfig());
        var mutated = BaseConfig();
        mutate(mutated);
        Assert.NotEqual(baseSig, SubtitleSkipCache.ComputeSignature(mutated));
    }

    [Fact]
    public void ComputeSignature_ChangesWhenTemplateOrLabelChanges()
    {
        // Configurable naming: the template and label decide which filenames read as plugin-owned,
        // hence which items are "already satisfied". Changing either MUST invalidate the whole cache
        // (FromJson discards on a signature mismatch) so a rename never reuses a stale verdict.
        var baseSig = SubtitleSkipCache.ComputeSignature(BaseConfig());

        var templateChanged = BaseConfig();
        templateChanged.SubtitleFilenameTemplate = "{name}.{lang}{.type}.{label}";
        Assert.NotEqual(baseSig, SubtitleSkipCache.ComputeSignature(templateChanged));

        var labelChanged = BaseConfig();
        labelChanged.SubtitleLabel = "MyBrand";
        Assert.NotEqual(baseSig, SubtitleSkipCache.ComputeSignature(labelChanged));
    }

    // ── CanSkip ─────────────────────────────────────────────────────────────

    private const long Now = 1_000_000_000_000L;

    [Fact]
    public void CanSkip_NullEntry_False()
        => Assert.False(SubtitleSkipCache.CanSkip(null, currentToken: 5, nowTicks: Now, backstopDays: 30));

    [Fact]
    public void CanSkip_TokenMismatch_False()
    {
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now };
        Assert.False(SubtitleSkipCache.CanSkip(e, currentToken: 6, nowTicks: Now, backstopDays: 30));
    }

    [Fact]
    public void CanSkip_TokenMatch_NoTtl_True()
    {
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now - TimeSpan.FromDays(9999).Ticks };
        Assert.True(SubtitleSkipCache.CanSkip(e, currentToken: 5, nowTicks: Now, backstopDays: 0));
    }

    [Fact]
    public void CanSkip_TokenMatch_WithinTtl_True()
    {
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now - TimeSpan.FromDays(10).Ticks };
        Assert.True(SubtitleSkipCache.CanSkip(e, currentToken: 5, nowTicks: Now, backstopDays: 30));
    }

    [Fact]
    public void CanSkip_TokenMatch_TtlExpired_False()
    {
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now - TimeSpan.FromDays(40).Ticks };
        Assert.False(SubtitleSkipCache.CanSkip(e, currentToken: 5, nowTicks: Now, backstopDays: 30));
    }

    [Fact]
    public void CanSkip_NegativeAge_ClockSkew_False()
    {
        // Entry stamped in the "future" (clock stepped back) → re-verify rather than trust it.
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now + TimeSpan.FromDays(1).Ticks };
        Assert.False(SubtitleSkipCache.CanSkip(e, currentToken: 5, nowTicks: Now, backstopDays: 30));
    }

    [Fact]
    public void CanSkip_AbsurdBackstopDays_DoesNotOverflow()
    {
        // A huge SkipCacheExpiryDays must not throw (TimeSpan.FromDays overflows past ~10.7M days);
        // it means "effectively never expires on time". Guards against bricking the scheduled task.
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now - TimeSpan.FromDays(9999).Ticks };
        Assert.True(SubtitleSkipCache.CanSkip(e, currentToken: 5, nowTicks: Now, backstopDays: int.MaxValue));
    }

    [Fact]
    public void CanSkip_ExactlyAtTtlBoundary_True()
    {
        // age == backstop window → still valid (guard is age > max, not >=).
        var e = new SubtitleSkipCache.Entry { Token = 5, CachedAtTicks = Now - TimeSpan.FromDays(30).Ticks };
        Assert.True(SubtitleSkipCache.CanSkip(e, currentToken: 5, nowTicks: Now, backstopDays: 30));
    }

    // ── IsSubtitleSetComplete (extracted from the task's inline switch) ───────

    [Theory]
    // mode, needsTranslation, full, forced, translated => expected
    [InlineData(SubtitleMode.Full, false, true, false, false, true)]     // full sub present
    [InlineData(SubtitleMode.Full, false, false, false, false, false)]   // no full sub
    [InlineData(SubtitleMode.Full, true, true, false, false, false)]     // needs translation too
    [InlineData(SubtitleMode.Full, true, true, false, true, true)]       // full + translated
    [InlineData(SubtitleMode.ForcedOnly, false, true, false, false, false)] // full doesn't satisfy forced
    [InlineData(SubtitleMode.ForcedOnly, false, false, true, false, true)]  // forced present
    [InlineData(SubtitleMode.FullAndForced, false, true, true, false, true)]
    [InlineData(SubtitleMode.FullAndForced, false, true, false, false, false)] // missing forced
    [InlineData(SubtitleMode.FullAndForced, true, true, true, false, false)]   // missing translation
    [InlineData(SubtitleMode.FullAndForced, true, true, true, true, true)]
    [InlineData(SubtitleMode.TranslationOnly, false, false, false, true, true)]
    [InlineData(SubtitleMode.TranslationOnly, false, true, true, false, false)] // only translation counts
    [InlineData((SubtitleMode)999, false, true, false, false, true)]            // defensive default arm => hasFull
    public void IsSubtitleSetComplete_Matrix(SubtitleMode mode, bool needsTranslation, bool full, bool forced, bool translated, bool expected)
    {
        Assert.Equal(expected, SubtitleManager.IsSubtitleSetComplete(mode, needsTranslation, full, forced, translated));
    }

    // ── Mutation + prune ────────────────────────────────────────────────────

    [Fact]
    public void Record_TryGet_Remove_RoundTrip()
    {
        var cache = SubtitleSkipCache.Empty();
        var id = Guid.NewGuid();
        Assert.Null(cache.TryGet(id));

        cache.Record(id, new SubtitleSkipCache.Entry { Token = 42, Full = true, CachedAtTicks = Now });
        Assert.Equal(42, cache.TryGet(id)!.Token);
        Assert.Equal(1, cache.Count);

        cache.Remove(id);
        Assert.Null(cache.TryGet(id));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void PruneTo_DropsUnseen_KeepsSeen()
    {
        var cache = SubtitleSkipCache.Empty();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        cache.Record(keep, new SubtitleSkipCache.Entry { Token = 1 });
        cache.Record(drop, new SubtitleSkipCache.Entry { Token = 2 });

        var removed = cache.PruneTo(new HashSet<Guid> { keep });

        Assert.Equal(1, removed);
        Assert.NotNull(cache.TryGet(keep));
        Assert.Null(cache.TryGet(drop));
    }

    // ── Serialization ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json ")]
    public void FromJson_NullEmptyOrCorrupt_ReturnsEmpty(string? json)
    {
        Assert.Equal(0, SubtitleSkipCache.FromJson(json, "sig").Count);
    }

    [Fact]
    public void FromJson_SignatureMismatch_ReturnsEmpty()
    {
        var cache = SubtitleSkipCache.Empty();
        cache.Record(Guid.NewGuid(), new SubtitleSkipCache.Entry { Token = 1 });
        var json = cache.ToJson("signature-A");

        Assert.Equal(0, SubtitleSkipCache.FromJson(json, "signature-B").Count);
        Assert.Equal(1, SubtitleSkipCache.FromJson(json, "signature-A").Count);
    }

    [Fact]
    public void FromJson_VersionMismatch_ReturnsEmpty()
    {
        // A future/older on-disk shape is discarded rather than mis-parsed.
        var json = "{\"Version\":999,\"Signature\":\"sig\",\"Entries\":{}}";
        Assert.Equal(0, SubtitleSkipCache.FromJson(json, "sig").Count);
    }

    [Fact]
    public void FromJson_SkipsMalformedKeys_KeepsValidEntries()
    {
        // Partial corruption (a non-Guid key) drops that entry, not the whole file.
        var valid = Guid.NewGuid().ToString("N");
        var json = "{\"Version\":1,\"Signature\":\"sig\",\"Entries\":{" +
                   "\"not-a-guid\":{\"Token\":1},\"" + valid + "\":{\"Token\":2}}}";
        var cache = SubtitleSkipCache.FromJson(json, "sig");
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, cache.TryGet(Guid.Parse(valid))!.Token);
    }

    [Fact]
    public void ToJson_FromJson_PreservesEntries()
    {
        var cache = SubtitleSkipCache.Empty();
        var id = Guid.NewGuid();
        cache.Record(id, new SubtitleSkipCache.Entry { Token = 7, Full = true, Forced = false, Translated = true, CachedAtTicks = Now });

        var round = SubtitleSkipCache.FromJson(cache.ToJson("sig"), "sig");

        var e = round.TryGet(id);
        Assert.NotNull(e);
        Assert.Equal(7, e!.Token);
        Assert.True(e.Full);
        Assert.False(e.Forced);
        Assert.True(e.Translated);
        Assert.Equal(Now, e.CachedAtTicks);
    }

    [Fact]
    public void Save_Load_RoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whispersubs-skipcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "skip-cache.json");
        try
        {
            var cache = SubtitleSkipCache.Empty();
            var id = Guid.NewGuid();
            cache.Record(id, new SubtitleSkipCache.Entry { Token = 99, Full = true, CachedAtTicks = Now });
            cache.Save(path, "sig");

            Assert.True(File.Exists(path));
            var loaded = SubtitleSkipCache.Load(path, "sig");
            Assert.Equal(99, loaded.TryGet(id)!.Token);

            // Overwrite (atomic rename) works when the file already exists.
            cache.Record(Guid.NewGuid(), new SubtitleSkipCache.Entry { Token = 1 });
            cache.Save(path, "sig");
            Assert.Equal(2, SubtitleSkipCache.Load(path, "sig").Count);

            // Wrong signature on load → fresh empty cache.
            Assert.Equal(0, SubtitleSkipCache.Load(path, "different").Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // ── Config defaults ─────────────────────────────────────────────────────

    [Fact]
    public void Config_Defaults_CacheOn_30DayBackstop()
    {
        var c = new PluginConfiguration();
        Assert.True(c.CacheSkippedItems);
        Assert.Equal(30, c.SkipCacheExpiryDays);
    }
}
