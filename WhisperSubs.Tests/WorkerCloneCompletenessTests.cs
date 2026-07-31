using System;
using System.Linq;
using System.Reflection;
using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Structural guard for <see cref="WorkerEndpointDedup.CollapseByEndpoint"/>, which returns COPIES of the
/// configured worker rows. Every enabled row is passed through it by
/// <c>WorkerRegistry.BuildWorkers</c>, which then reads the worker's settings off that copy — so any
/// property the copy forgets is silently reset to its default for every worker on the system.
/// <para>
/// This is not hypothetical. <c>MaxUploadBytes</c> and <c>UploadCodec</c> were added in 4.5.0.0 but not to
/// the copy (written in 4.3.1), so a worker configured for Opus silently uploaded raw WAV and its upload
/// cap silently became "unlimited" — reported on issue #138 as "it's trying to send a WAV file even when
/// OPUS is selected", after two releases that were supposed to fix exactly that.
/// </para>
/// <para>
/// The reflection test below is the real fix: it fails the moment ANY future property is added to
/// <see cref="WhisperWorker"/> without being carried through the copy, so this bug class cannot recur.
/// </para>
/// </summary>
public class WorkerCloneCompletenessTests
{
    /// <summary>A value that differs from the property's default, so a dropped copy is detectable.</summary>
    private static object DistinctValue(PropertyInfo p) => p.PropertyType switch
    {
        var t when t == typeof(string) => "distinct-" + p.Name,
        var t when t == typeof(bool) => false,      // every bool on this type defaults to true
        var t when t == typeof(int) => 7,
        var t when t == typeof(long) => 123_456_789L,
        var t when t == typeof(double) => 3.5,
        _ => throw new NotSupportedException(
            $"WhisperWorker.{p.Name} has unhandled type {p.PropertyType}; extend this test."),
    };

    [Fact]
    public void CollapsePreservesEveryConfiguredProperty()
    {
        var properties = typeof(WhisperWorker)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

        Assert.NotEmpty(properties);

        // A single row with EVERY property set to a non-default value.
        var source = new WhisperWorker();
        foreach (var p in properties)
        {
            p.SetValue(source, DistinctValue(p));
        }

        // Enabled and a valid URL, so it survives the enabled/blank filtering the registry applies.
        source.Enabled = true;
        source.ApiUrl = "http://worker.example:9010";
        source.UploadCodec = RemoteUploadFormat.Opus;   // the property that actually regressed
        source.MaxConcurrency = 1;

        var collapsed = WorkerEndpointDedup.CollapseByEndpoint([source]);
        var result = Assert.Single(collapsed);

        var dropped = properties
            .Where(p => !Equals(p.GetValue(result), p.GetValue(source)))
            .Select(p => $"{p.Name}: expected {p.GetValue(source)}, got {p.GetValue(result)}")
            .ToArray();

        Assert.True(
            dropped.Length == 0,
            "CollapseByEndpoint dropped configured settings, which silently resets them for every worker:\n  "
            + string.Join("\n  ", dropped));
    }

    [Fact]
    public void ConfiguredOpusSurvivesTheCollapse()
    {
        // The exact user-visible regression from issue #138, pinned explicitly so the intent is readable
        // even if the reflection test above is ever weakened.
        var worker = new WhisperWorker
        {
            Enabled = true,
            ApiUrl = "https://api.groq.com/openai",
            UploadCodec = RemoteUploadFormat.Opus,
            MaxUploadBytes = 25_000_000,
        };

        var result = Assert.Single(WorkerEndpointDedup.CollapseByEndpoint([worker]));

        Assert.Equal(RemoteUploadFormat.Opus, result.UploadCodec);
        Assert.True(RemoteUploadFormat.RequiresReencode(result.UploadCodec),
            "a worker configured for Opus must still require a re-encode after collapsing");
        Assert.Equal(25_000_000, result.MaxUploadBytes);
    }

    [Fact]
    public void DuplicateEndpointsStillCollapseAndKeepTheFirstRowsUploadSettings()
    {
        var first = new WhisperWorker
        {
            Id = "a",
            Enabled = true,
            ApiUrl = "https://api.groq.com/openai",
            UploadCodec = RemoteUploadFormat.Opus,
            MaxUploadBytes = 25_000_000,
            MaxConcurrency = 2,
        };
        var duplicate = new WhisperWorker
        {
            Id = "b",
            Enabled = true,
            ApiUrl = "https://api.groq.com/openai/",
            UploadCodec = RemoteUploadFormat.Wav,
            MaxConcurrency = 1,
        };

        var result = Assert.Single(WorkerEndpointDedup.CollapseByEndpoint([first, duplicate]));

        Assert.Equal("a", result.Id);
        Assert.Equal(RemoteUploadFormat.Opus, result.UploadCodec);   // first row's settings win
        Assert.Equal(25_000_000, result.MaxUploadBytes);
        Assert.Equal(1, result.MaxConcurrency);                      // but concurrency still takes the min
    }
}
