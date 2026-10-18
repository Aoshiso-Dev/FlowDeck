using FlowDeck.Core;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed partial class EneMemoryDeviceDriverTests
{
    [Fact]
    public async Task InterruptedReadIsTemporaryAndHealthyReadRestoresSavedSetupWithoutConfirmation()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open();
        var binding = r.Driver().BindingCandidate;
        using var cancelled = new CancellationTokenSource();
        rack.BeforeData = (_, write) => { if (!write) cancelled.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Driver().PollAsync(cancelled.Token));

        var capabilities = r.Driver().Observe()!.Endpoints.Single().Capabilities;
        // Read staleness does not require setup or recovery, but must not block a mixed-selection stop.
        Assert.All(capabilities.Where(c => c.Feature != DeviceFeature.RestoreLighting),
            c => Assert.Equal(DeviceAvailability.TemporarilyUnavailable, c.Availability));
        Assert.Equal(DeviceAvailability.Ready, capabilities.Single(c => c.Feature == DeviceFeature.RestoreLighting).Availability);
        Assert.Equal(binding, r.Driver().BindingCandidate); Assert.Empty(rack.Writes); Assert.Null(r.Pending());

        rack.BeforeData = null;
        await r.Driver().PollAsync(default);

        Assert.All(r.Driver().Observe()!.Endpoints.Single().Capabilities, c => Assert.Equal(DeviceAvailability.Ready, c.Availability));
        Assert.Equal(binding, r.Driver().BindingCandidate); Assert.Empty(rack.Writes); Assert.Null(r.Pending());
    }

    [Fact]
    public async Task InterruptedReadRetainsTheRecoveryActionWhenNativeOutputHasADurableRecord()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open();
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(11, 22, 33)]))).Result!.Outcome);
        var pending = r.Pending()!; var writes = rack.Writes.Count;
        using var cancelled = new CancellationTokenSource();
        rack.BeforeData = (_, write) => { if (!write) cancelled.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Driver().PollAsync(cancelled.Token));

        var capabilities = r.Driver().Observe()!.Endpoints.Single().Capabilities;
        Assert.All(capabilities.Where(c => c.Feature != DeviceFeature.RestoreLighting),
            c => Assert.Equal(DeviceAvailability.TemporarilyUnavailable, c.Availability));
        Assert.Equal(DeviceAvailability.Ready, capabilities.Single(c => c.Feature == DeviceFeature.RestoreLighting).Availability);
        Assert.Equal(pending.Hash, r.Pending()!.Hash); Assert.Equal(writes, rack.Writes.Count);

        rack.BeforeData = null;
        await r.Driver().PollAsync(default);

        Assert.All(r.Driver().Observe()!.Endpoints.Single().Capabilities, c => Assert.Equal(DeviceAvailability.Ready, c.Availability));
        Assert.Equal(pending.Hash, r.Pending()!.Hash); Assert.Equal(writes, rack.Writes.Count);
    }
}
