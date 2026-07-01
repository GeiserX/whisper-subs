using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers the selectable Silero VAD model catalog introduced in issue #105:
/// the VadModels array, DefaultVadModelKey constant, and ResolveVadModel lookup.
/// </summary>
public class VadModelSelectionTests
{
    // ── VadModels catalog ──────────────────────────────────────────────────────

    [Fact]
    public void VadModels_HasExactlyTwoEntries()
    {
        Assert.Equal(2, ModelCatalog.VadModels.Length);
    }

    [Fact]
    public void VadModels_Keys_AreExpectedValues()
    {
        Assert.Equal("v5.1.2", ModelCatalog.VadModels[0].Key);
        Assert.Equal("v6.2.0", ModelCatalog.VadModels[1].Key);
    }

    /// <summary>
    /// Guards against the two sources of truth drifting: the legacy VadModel* constants
    /// and the new VadModels[0] entry must point at the same file, URL and size.
    /// </summary>
    [Fact]
    public void VadModels_Entry0_MatchesVadModelConsts()
    {
        var entry0 = ModelCatalog.VadModels[0];
        Assert.Equal(ModelCatalog.VadModelFileName, entry0.FileName);
        Assert.Equal(ModelCatalog.VadModelUrl, entry0.Url);
        Assert.Equal(ModelCatalog.VadModelSizeBytes, entry0.SizeBytes);
    }

    [Fact]
    public void VadModels_BothEntries_UrlContainsWhisperVad()
    {
        foreach (var entry in ModelCatalog.VadModels)
        {
            Assert.Contains("whisper-vad", entry.Url);
        }
    }

    [Fact]
    public void VadModels_BothEntries_UrlEndsWithFileName()
    {
        foreach (var entry in ModelCatalog.VadModels)
        {
            Assert.EndsWith(entry.FileName, entry.Url);
        }
    }

    [Fact]
    public void DefaultVadModelKey_IsV512()
    {
        Assert.Equal("v5.1.2", ModelCatalog.DefaultVadModelKey);
    }

    // ── ResolveVadModel ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveVadModel_KnownKey_ReturnsMatchingEntry()
    {
        var entry = ModelCatalog.ResolveVadModel("v6.2.0");
        Assert.Equal("v6.2.0", entry.Key);
        Assert.Equal("ggml-silero-v6.2.0.bin", entry.FileName);
    }

    [Fact]
    public void ResolveVadModel_KeyCaseInsensitive_ReturnsMatchingEntry()
    {
        // Key comparison must be case-insensitive so "V6.2.0" resolves the same as "v6.2.0".
        var entry = ModelCatalog.ResolveVadModel("V6.2.0");
        Assert.Equal("v6.2.0", entry.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("v99.0.0")]
    public void ResolveVadModel_UnknownOrEmpty_FallsBackToDefaultV512(string? key)
    {
        var entry = ModelCatalog.ResolveVadModel(key);
        Assert.Equal("v5.1.2", entry.Key);
        Assert.Equal(ModelCatalog.VadModelFileName, entry.FileName);
    }
}
