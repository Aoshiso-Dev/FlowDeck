using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class EneMemoryPlatformCaptureTests
{
    private static EnePlatformEvidence RepeatedLocator => EneMemoryBackendTests.Evidence with
    {
        Dimms = [
            new("DIMM 1", "P0 CHANNEL A", "Unknown", "F5-6000J3040G32G", "unknown", 32UL << 30, 34, 4800),
            new("DIMM 1", "P0 CHANNEL B", "Unknown", "F5-6000J3040G32G", "unknown", 32UL << 30, 34, 4800)]
    };

    [Fact]
    public void RepeatedLocatorInDifferentBanksHasStableWholePlatformProof()
    {
        var evidence = RepeatedLocator;
        var original = EneMemoryPlatformCapture.Fingerprint(evidence);
        Assert.Equal(64, original.Length);
        Assert.Equal(original, EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = evidence.Dimms.Reverse().ToArray() }));
        Assert.Equal(original, EneMemoryPlatformCapture.Fingerprint(evidence with
        { Dimms = [evidence.Dimms[0] with { Bank = " P0 CHANNEL A ", Locator = " DIMM 1 " }, evidence.Dimms[1]] }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with
        { Dimms = [evidence.Dimms[0], evidence.Dimms[1] with { Bank = "P0 CHANNEL C" }] }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with
        { Dimms = [evidence.Dimms[0], evidence.Dimms[1] with { Serial = "replacement" }] }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [evidence.Dimms[0]] }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("P0 CHANNEL A")]
    [InlineData(" P0 CHANNEL A ")]
    public void SharedLocatorStillRequiresDistinctKnownBanks(string? secondBank)
    {
        var evidence = RepeatedLocator;
        Assert.Throws<IOException>(() => EneMemoryPlatformCapture.Fingerprint(evidence with
        { Dimms = [evidence.Dimms[0], evidence.Dimms[1] with { Bank = secondBank, Serial = "different-serial" }] }));
    }

    [Fact]
    public void UniqueLocatorProofRemainsCompatibleWithPreviouslySavedProof()
    {
        var evidence = EneMemoryBackendTests.Evidence;
        Assert.Equal(SettingsTransfer.Hash(evidence), EneMemoryPlatformCapture.Fingerprint(evidence));
        var withoutBanks = evidence with { Dimms = evidence.Dimms.Select(d => d with { Bank = null }).ToArray() };
        Assert.Equal(SettingsTransfer.Hash(withoutBanks), EneMemoryPlatformCapture.Fingerprint(withoutBanks));
    }
}
