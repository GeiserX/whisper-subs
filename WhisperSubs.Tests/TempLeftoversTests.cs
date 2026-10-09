using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>A killed job leaves its temp audio and folder behind; the next run removes them, and only them.</summary>
public sealed class TempLeftoversTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "leftovers_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime ProcessStart = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public TempLeftoversTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Sweep_RemovesOnlyOurEntriesFromBeforeTheProcessStarted()
    {
        var oldWork = Folder("whispersubs_3f5231ff564595b6db865ce94b805416_e7fec984c1d648adbdfc235de705b66d", old: true, withFile: true);
        var oldAudio = File_("92099522-212b-6319-d1c8-735c4dbf4b49_443fed9c-f860-4794-bedd-bb98d9d638e7.wav", old: true);
        var oldCanary = File_("92099522-212b-6319-d1c8-735c4dbf4b49_443fed9c-f860-4794-bedd-bb98d9d638e7_canary.wav", old: true);
        var oldUpload = File_("whispersubs_upload_0123456789abcdef0123456789abcdef.opus", old: true);
        var liveWork = Folder("whispersubs_3f5231ff564595b6db865ce94b805416_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", old: false, withFile: true);
        var liveAudio = File_("11111111-2222-3333-4444-555555555555_66666666-7777-8888-9999-000000000000.wav", old: false);
        var someoneElses = File_("clr-debug-pipe-1-144438156-in", old: true);
        var otherWav = File_("holiday.wav", old: true);

        var (count, bytes) = TempLeftovers.Sweep(_dir, ProcessStart, NullLogger.Instance);

        Assert.Equal(4, count);
        Assert.True(bytes > 0);
        Assert.False(Directory.Exists(oldWork));
        Assert.False(File.Exists(oldAudio));
        Assert.False(File.Exists(oldCanary));
        Assert.False(File.Exists(oldUpload));
        Assert.True(Directory.Exists(liveWork));     // started by this process: a live job's
        Assert.True(File.Exists(liveAudio));
        Assert.True(File.Exists(someoneElses));      // not ours
        Assert.True(File.Exists(otherWav));
    }

    [Fact]
    public void Sweep_OfAMissingFolder_ReportsNothingAndDoesNotThrow()
        => Assert.Equal((0, 0L), TempLeftovers.Sweep(Path.Combine(_dir, "missing"), ProcessStart, NullLogger.Instance));

    private string Folder(string name, bool old, bool withFile)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        if (withFile) File.WriteAllBytes(Path.Combine(path, "full.wav"), new byte[1024]);
        Directory.SetLastWriteTimeUtc(path, old ? ProcessStart.AddHours(-3) : ProcessStart.AddMinutes(5));
        return path;
    }

    private string File_(string name, bool old)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[512]);
        File.SetLastWriteTimeUtc(path, old ? ProcessStart.AddHours(-3) : ProcessStart.AddMinutes(5));
        return path;
    }
}
