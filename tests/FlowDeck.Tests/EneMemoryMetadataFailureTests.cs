using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneMemoryMetadataFailureTests
{
    [Theory] [InlineData(0)] [InlineData(2.1)]
    public async Task FailedPlatformCaptureNeverBecomesAnAgeOnlyRetry(double age)
    {
        var rack = new Rack(0x71); var clock = new TestClock(); var fail = false; var captures = 0;
        var transport = rack.Transport();
        await using var backend = new EneMemoryDeviceBackend([transport with { ReadPlatformFingerprint = () =>
        {
            captures++;
            if (fail) throw new IOException("platform capture unavailable");
            return transport.ReadPlatformFingerprint();
        } }], clock);
        var connection = Assert.Single(await backend.DiscoverAsync(default));
        var opens = rack.Opens; fail = true;
        var error = await Assert.ThrowsAsync<IOException>(() => backend.PrepareOperationAsync(async ct =>
        {
            clock.Advance(age);
            return await backend.ExecuteAsync(connection, true, s => s.Read(0x8020), ct);
        }, default));
        Assert.Contains("platform capture unavailable", error.Message);
        Assert.IsNotType<EneMemoryFreshnessException>(error);
        Assert.Equal(2, captures); Assert.Equal(opens, rack.Opens); Assert.Empty(rack.Writes);
        Assert.Throws<IOException>(() => backend.Check(connection));
    }
}
