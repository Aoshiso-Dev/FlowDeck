using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    private sealed class SamplingClock : TimeProvider
    {
        private long timestamp = 1000;
        public int Step;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Add(ref timestamp, Volatile.Read(ref Step));
        public void Advance(int milliseconds) => Interlocked.Add(ref timestamp, milliseconds);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task OneSnapshotUsesOneLightingPhaseAcrossEverySynchronizedDevice(int count)
    {
        var clock = new SamplingClock(); var drivers = Enumerable.Range(0, count).Select(i => new Driver("light-" + i)).ToArray();
        await using var runtime = new DeviceControlRuntime(drivers, ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); var state = runtime.State();
        var targets = state.Endpoints.Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetLighting,
            state.Inventory.TopologyRevision, targets, Lighting: new("breathing", [new(10, 20, 30)]));
        Assert.True((await runtime.ExecuteAsync(request)).Success);
        // A snapshot can be descheduled between endpoints. Its members still describe one instant.
        clock.Step = 2;
        var snapshot = runtime.State();
        clock.Step = 0;
        var mirrors = snapshot.Endpoints.Select(e => Assert.IsType<M5BodyLedFrame>(e.Lighting?.Mirror)).ToArray();
        Assert.Single(mirrors.Select(m => m.PhaseMs).Distinct());
        Assert.Single(mirrors.Select(m => m.ValidForMs).Distinct());
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(new('d', 32));
        config = config with { Devices = new(2, drivers.Select(d => new RegisteredDevice(d.DeviceId, d.DeviceId, "demo", DeviceKind.Controller,
            [new("light1", "LED", EndpointKind.Lighting)])).ToArray()) };
        var view = DeviceM5Lighting.ReadBodyLed(new(snapshot, new(new('a', 32), config, []), 0, true, null, Session: hello.Session), "all");
        Assert.NotNull(view.Frame);
        clock.Advance(3000);
        Assert.All(runtime.State().Endpoints, e => Assert.Null(e.Lighting?.Mirror));
    }
}
