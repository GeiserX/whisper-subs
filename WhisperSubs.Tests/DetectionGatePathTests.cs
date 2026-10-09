using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>The detection gate compares an unreadable model path as written.</summary>
[Collection(nameof(HostEngineGates))]
public class DetectionGatePathTests
{
    [Fact]
    public void DetectionGate_AnUnreadablePathIsComparedAsWritten()
        => Assert.Same(HostEngineGates.Detection, WhisperProvider.GateForDetection("/m/ba\0d.bin", "/m/ggml-large-v3.bin"));
}
