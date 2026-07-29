using System.IO;
using System.Xml.Serialization;
using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Back-compat lock for the v4.5 upload settings (issue #138). Every existing install has a config XML
/// written before <c>MaxUploadBytes</c> and <c>UploadCodec</c> existed; deserializing it must reproduce
/// the old behaviour exactly — unlimited size and an uncompressed WAV upload — or self-hosted workers
/// (whisper.cpp decodes WAV only) would break on upgrade.
/// </summary>
public class WorkerUploadBackCompatTests
{
    private static WhisperWorker Deserialize(string xml)
    {
        var serializer = new XmlSerializer(typeof(WhisperWorker));
        using var reader = new StringReader(xml);
        return (WhisperWorker)serializer.Deserialize(reader)!;
    }

    [Fact]
    public void PreV45ConfigDeserializesToUnlimitedWav()
    {
        // A worker row exactly as v4.4 would have persisted it: neither new element exists.
        const string legacy = """
            <?xml version="1.0" encoding="utf-16"?>
            <WhisperWorker xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <Id>abc</Id>
              <Name>groq</Name>
              <Enabled>true</Enabled>
              <ApiUrl>https://api.groq.com/openai</ApiUrl>
              <ApiKey></ApiKey>
              <Model>whisper-large-v3</Model>
              <MaxConcurrency>1</MaxConcurrency>
              <CostWeight>0</CostWeight>
              <CanTranslate>true</CanTranslate>
            </WhisperWorker>
            """;

        var worker = Deserialize(legacy);

        Assert.Equal(0, worker.MaxUploadBytes);                       // unlimited => never blocks
        Assert.Equal("wav", RemoteUploadFormat.Normalize(worker.UploadCodec));
        Assert.False(RemoteUploadFormat.RequiresReencode(worker.UploadCodec));
        Assert.True(UploadPreflight.IsAllowed(long.MaxValue / 2, worker.MaxUploadBytes));
    }

    [Fact]
    public void EmptyUploadCodecElementIsTreatedAsWav()
    {
        // A round-trip through some editors can leave the element present but empty.
        const string emptyElement = """
            <?xml version="1.0" encoding="utf-16"?>
            <WhisperWorker xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <ApiUrl>http://box:9010</ApiUrl>
              <UploadCodec></UploadCodec>
            </WhisperWorker>
            """;

        var worker = Deserialize(emptyElement);

        Assert.Equal("wav", RemoteUploadFormat.Normalize(worker.UploadCodec));
        Assert.False(RemoteUploadFormat.RequiresReencode(worker.UploadCodec));
    }

    [Fact]
    public void FreshWorkerDefaultsAreUnlimitedWav()
    {
        var worker = new WhisperWorker();

        Assert.Equal(0, worker.MaxUploadBytes);
        Assert.Equal("wav", worker.UploadCodec);
        Assert.False(RemoteUploadFormat.RequiresReencode(worker.UploadCodec));
    }

    [Fact]
    public void LegacyWorkerStillValidates()
    {
        // The new validation rules must not reject a row that was perfectly valid before the upgrade.
        var worker = new WhisperWorker { ApiUrl = "http://box:9010", MaxConcurrency = 1, CostWeight = 0 };
        var (ok, error) = WorkerConfigValidation.Validate(worker);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("aac")]
    public void UnsupportedCodecIsRejectedByValidation(string codec)
    {
        var worker = new WhisperWorker { ApiUrl = "http://box:9010", UploadCodec = codec };
        var (ok, error) = WorkerConfigValidation.Validate(worker);

        Assert.False(ok);
        Assert.Contains("wav, flac or opus", error!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NegativeMaxUploadIsRejected()
    {
        var worker = new WhisperWorker { ApiUrl = "http://box:9010", MaxUploadBytes = -1 };
        var (ok, _) = WorkerConfigValidation.Validate(worker);

        Assert.False(ok);
    }
}
