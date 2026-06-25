using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class WhisperExitFailureTests
{
    [Theory]
    [InlineData(132)]
    [InlineData(134)]
    [InlineData(135)]
    public void DescribeWhisperExitFailure_IllegalInstruction_SuggestsNoavx(int exitCode)
    {
        // SIGILL-class crashes (132/134/135) mean the AVX2 build hit a CPU without AVX2 — the
        // diagnosis must point the user at the noavx (Compatibility) build, not a generic error.
        var result = WhisperProvider.DescribeWhisperExitFailure(exitCode, "whatever stderr");

        Assert.NotNull(result);
        Assert.Contains("illegal", result!, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(
            result.Contains("noavx", System.StringComparison.OrdinalIgnoreCase)
            || result.Contains("Compatibility", System.StringComparison.OrdinalIgnoreCase),
            $"Expected the message to mention the noavx/Compatibility build, got: {result}");
    }

    [Fact]
    public void DescribeWhisperExitFailure_Exit127_NamesMissingLibrary()
    {
        var stderr = "whisper-cli: error while loading shared libraries: libvulkan.so.1: cannot open shared object file: No such file or directory";
        var result = WhisperProvider.DescribeWhisperExitFailure(127, stderr);

        Assert.NotNull(result);
        Assert.Contains("libvulkan.so.1", result!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(139)]
    public void DescribeWhisperExitFailure_NonSpecialCodes_ReturnsNull(int exitCode)
    {
        // Only 127 and the SIGILL-class 132/134/135 codes get a tailored message; everything
        // else (including a clean exit 0 and a generic exit 1) falls through to null so the
        // caller emits its own generic error.
        Assert.Null(WhisperProvider.DescribeWhisperExitFailure(exitCode, "some stderr"));
    }
}
