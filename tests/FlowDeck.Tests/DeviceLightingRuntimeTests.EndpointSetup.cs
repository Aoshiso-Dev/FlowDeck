using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    private sealed class SetupDriver(Driver inner) : IIndividualLightingConfiguration
    {
        public string DeviceId => inner.DeviceId;
        public DeviceLightingLimits? LightingLimits => inner.LightingLimits;
        public PhysicalDeviceBinding BindingCandidate => inner.BindingCandidate;
        public DeviceObservation? Observe() => inner.Observe() is { } value
            ? value with { Endpoints = [.. value.Endpoints, value.Endpoints[0] with { Id = "light2" }] } : null;
        public string? Validate(DeviceEndpointAction action) => inner.Validate(action);
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => inner.ValidateLightingStream(endpoints, program);
        public Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct) => inner.OpenLightingStreamAsync(endpoints, program, ct);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => inner.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => inner.ReleaseAsync(reason, ct);
        public void ConfigureLighting(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations, IEnumerable<RegisteredEndpoint> endpoints) { }
        public void ConfigureLightingEndpoint(PhysicalDeviceBinding binding, string endpointId, LightingEndpointConfiguration? configuration, RegisteredEndpoint endpoint) { }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PortSetupDrainsAnInFlightFrameBeforeReleasingOnlyTheSelectedPort(bool editingActivePort)
    {
        var driver = new Driver("a"); await using var r = new Rig(new SetupDriver(driver)).Open();
        var request = r.Request(DeviceOperation.SetLighting, ["a"], new("breathing", [new(100, 50, 20)]));
        request = request with { Targets = request.Targets!.Where(t => t.Target.EndpointId == "light1").ToArray() };
        Assert.True((await r.Runtime.ExecuteAsync(request)).Success);
        driver.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); driver.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var round = r.Tick(.2); await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var configured = false;
        var apply = r.Runtime.ReconfigureAsync(request.Session, r.Runtime.State().Inventory.TopologyRevision,
            (_, _) => { configured = true; return Task.FromResult(new[] { driver.BindingCandidate }); },
            endpointSetup: new(new("a", editingActivePort ? "light1" : "light2"), [], [editingActivePort ? "light1" : "light2"]));
        try
        {
            Assert.True(r.Runtime.State().ConfigurationChanging); Assert.False(configured); Assert.False(apply.IsCompleted);
        }
        finally { driver.Continue.TrySetResult(); }
        await Task.WhenAll(round, apply).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(configured); Assert.Equal(editingActivePort ? 1 : 0, driver.Ends); Assert.Equal(2, driver.Writes);
        await r.Tick(.2);
        var after = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "light1");
        if (editingActivePort) { Assert.False(after.Owned); Assert.Null(after.Lighting); Assert.Equal(DeviceLightingStopReason.ConfigurationChanged, driver.EndReason); }
        else { Assert.True(after.Owned); Assert.True(after.Lighting!.Running); Assert.Null(after.Lighting.Fault); Assert.Equal(3, driver.Writes); }
    }
}
