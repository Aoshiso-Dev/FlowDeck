using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ReleaseCoolingCurrentAuthorityTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";

    // Losing the deadline capability removes permission for a new duty while
    // leaving identity, readback and the existing journal's exit operation intact.
    private sealed class Hub(DemoHub inner) : IHubEndpointAdapter
    {
        public bool SupportsFanDeadlines { get; set; } = true;
        public bool SupportsFanEndpoints => true;
        public bool IsDemo => true;
        public string Version => inner.Version;
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => inner.OpenAsync(identity, generation, ct);
        public Task<HubStatus> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => inner.SetFanGroupAsync(channels, duty, ct);
        public Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default) => inner.SetFanEndpointAsync(channel, duty, expected, ct);
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => inner.SetLightingAsync(channels, preset, ct);
        public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCoolingExitSurvivesLossOfNewOutputAuthority(bool externalGeneration)
    {
        using var directory = new ServiceTestDirectory();
        var bus = new DemoHub { DelayMs = 0 }; var hub = new Hub(bus);
        var config = await DeviceRuntimeStartupTests.Package(bus, directory.Path);
        var oldBinding = config.Bindings.Devices.Single() with { Fingerprint = new('c', 64) };
        config = config with {
            Bindings = config.Bindings with { Devices = externalGeneration ? [oldBinding] : [] },
            Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Select(e => e with {
                WiringConfirmed = false, ResumeOnStart = false
            }).ToArray() }
        };
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var driver = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, () => new([driver], []),
            ["user"], new TestLog(), demo: true);
        var runtime = startup.Runtime; var hello = runtime.OpenSession("user"); long sequence = 0;
        DeviceServiceRequest Request(DeviceOperation operation, int? duty = null)
        {
            var state = runtime.State(); var row = state.Endpoints.Single(e => e.Target.EndpointId == "fan1");
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                state.Inventory.TopologyRevision, [new(row.Target, row.Generation, row.PolicyRevision)], duty);
        }
        Assert.Equal(0, bus.Writes);
        Assert.True((await runtime.ExecuteAsync(Request(DeviceOperation.SetDuty, 60))).Success);
        HubEndpointObligation[] Pending() => new HubEndpointRecoveryJournal(directory.Path, driver.DeviceId).Pending;
        var pending = Assert.Single(Pending()); var binding = driver.BindingCandidate;
        Assert.Equal(50, pending.ExitDuty); Assert.Equal(1, bus.Writes);

        if (externalGeneration)
        {
            var current = startup.Configuration;
            var next = current with { Preferences = current.Preferences with {
                Notifications = current.Preferences.Notifications with { Desktop = !current.Preferences.Notifications.Desktop }
            } };
            store.Activate(store.Stage(DeviceConfigurationCodec.Write(next)));
            var check = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"),
                ++sequence, "test", DeviceOperation.Configuration,
                Configuration: new(DeviceConfigurationOperation.PrepareConnectionLighting)));
            Assert.False(check.Success);
        }
        else hub.SupportsFanDeadlines = false;

        Assert.False((await runtime.ExecuteAsync(Request(DeviceOperation.SetDuty, 70))).Success);
        Assert.Equal(1, bus.Writes); Assert.Equal(pending, Assert.Single(Pending()));
        Assert.Equal(binding, driver.BindingCandidate);
        var beforeRelease = store.ReadActivePointer();
        var released = await runtime.ExecuteAsync(Request(DeviceOperation.ReleaseCooling));
        var result = Assert.Single(released.Result!.Targets);
        Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(50, result.ReportedDuty);
        Assert.Equal(externalGeneration ? DevicePolicySaveState.Failed : DevicePolicySaveState.Saved, result.Persistence!.State);
        Assert.Equal(externalGeneration ? Outcome.Partial : Outcome.Confirmed, released.Result.Outcome);
        Assert.Equal(2, bus.Writes); Assert.Empty(Pending());
        var stateAfter = runtime.State().Endpoints.Single(e => e.Target.EndpointId == "fan1");
        Assert.False(stateAfter.Owned); Assert.Null(stateAfter.Cooling);
        Assert.Equal(result.Persistence.State, stateAfter.Persistence!.State);
        Assert.Equal(SettingsTransfer.Hash(config.Bindings), SettingsTransfer.Hash(startup.Configuration.Bindings));
        Assert.False(startup.Configuration.Cooling.Endpoints.Single().WiringConfirmed);
        Assert.False(startup.Configuration.Cooling.Endpoints.Single().ResumeOnStart);
        if (externalGeneration)
        {
            Assert.Equal(beforeRelease, store.ReadActivePointer());
            Assert.NotNull(startup.Configuration.Cooling.Endpoints.Single().SavedPolicy);
        }
        else Assert.Null(startup.Configuration.Cooling.Endpoints.Single().SavedPolicy);
    }
}
