using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceWorkspaceTests
{
    private static readonly EndpointReference Target = new("hub-a", "fan1");
    private static DeviceCapability Cap(DeviceFeature f) => new(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceServiceState State(long revision = 0, bool owned = false, double? duty = 45, long generation = 1, long topology = 1) =>
        new(new("epoch", 1, [new(new("hub-a", "demo", "同じHub", DeviceKind.Controller, IdentityConfidence.SerialBound, "a", null, ["a"],
            [new("fan1", "FAN1", EndpointKind.Cooling, [Cap(DeviceFeature.Rpm), Cap(DeviceFeature.DutyRead), Cap(DeviceFeature.FixedDuty), Cap(DeviceFeature.AutomaticDuty), Cap(DeviceFeature.ReleaseCooling)])]), generation, true)], topology),
            [new(Target, generation, revision, Outcome.Confirmed, RequestedDuty: 65, ReportedDuty: 65, Owned: owned)], false, null,
            [new(Target, DeviceFeature.Rpm, 900, 0, false), new(Target, DeviceFeature.DutyRead, duty, 0, duty is null)]);
    private static CoolingEndpointConfiguration Setup() => new(Target, true,
        new(30, 100, 70, CoolingExitAction.FixedDuty, [new(new("cpu", "temperature"), 95, 85)]), []);
    private static readonly DeviceRegistrations Registrations = new(2, [new("hub-a", "ケース", "demo", DeviceKind.Controller, [new("fan1", "背面", EndpointKind.Cooling)])]);
    private static readonly DeviceDashboardItem Item = new(Target, DeviceFeature.Rpm, "背面", "fan");

    [Fact] public void DashboardKeepsIdentityIndependentOfNamesAndUnambiguousWithSeparators()
    {
        Assert.NotEqual(DeviceDashboard.Key(new("a:b", "c"), DeviceFeature.Rpm), DeviceDashboard.Key(new("a", "b:c"), DeviceFeature.Rpm));
        var state = State(); var other = state.Inventory.Devices[0] with { Device = state.Inventory.Devices[0].Device with { Id = "hub-b" } };
        var items = DeviceDashboard.Items(Registrations, new(2, [Item]), state.Inventory with { Devices = [state.Inventory.Devices[0], other] });
        Assert.Equal(2, items.Length); Assert.Equal(Item, items[0]); Assert.False(items[1].Visible);
        Assert.NotEqual(DeviceDashboard.Key(items[0].Target, items[0].Measurement), DeviceDashboard.Key(items[1].Target, items[1].Measurement));
    }
    [Theory]
    [InlineData(0, true, 900d)]
    [InlineData(2.999, true, 900d)]
    [InlineData(3, true, null)]
    [InlineData(0, false, null)]
    public void DashboardLocallyExpiresValuesEvenWhenNoNewResponseArrives(double elapsed, bool connected, double? expected)
    {
        var result = DeviceDashboard.Read(Item, Registrations, State(), elapsed, connected);
        Assert.Equal(expected, result.Value); Assert.Equal(expected is null ? null : 45, result.Duty);
    }
    [Fact] public void InTransitTimeAddsToServiceSampleAgeAndValidZeroIsNotMissing()
    {
        var state = State() with { Telemetry = [new(Target, DeviceFeature.Rpm, 0, 2.5, false)] };
        var zero = DeviceDashboard.Read(Item, Registrations, state, .4, true);
        Assert.Equal(0, zero.Value); Assert.Equal("0 RPM", zero.State);
        Assert.Null(DeviceDashboard.Read(Item, Registrations, state, .5, true).Value);
        Assert.Null(DeviceDashboard.Read(Item, Registrations, state with { Telemetry = [new(Target, DeviceFeature.Rpm, null, 0, true)] }, 0, true).Value);
        Assert.Null(DeviceDashboard.Read(Item, Registrations, state with { Telemetry = [new(Target, DeviceFeature.Rpm, double.NaN, 0, false)] }, 0, true).Value);
    }
    [Fact] public void DuplicateSamplesAreNotPickedArbitrarilyAndHistoryRestartsAcrossGenerations()
    {
        var state = State(); var normal = DeviceDashboard.Read(Item, Registrations, state, 0, true);
        var duplicate = state with { Telemetry = [.. state.Telemetry!, state.Telemetry![0] with { Value = 1 }] };
        Assert.Null(DeviceDashboard.Read(Item, Registrations, duplicate, 0, true).Value);
        Assert.NotEqual(normal.HistoryKey, DeviceDashboard.Read(Item, Registrations, State(generation: 2), 0, true).HistoryKey);
        Assert.Equal(normal.Key, DeviceDashboard.Read(Item with { Name = "変更" }, Registrations, state, 0, true).Key);
    }
    [Fact] public void MissingSavedItemsRemainAndRgbOnlyDeviceDoesNotInventTemperature()
    {
        var rgb = new DeviceInstance(new("rgb", "demo", "RGB RAM", DeviceKind.Memory, IdentityConfidence.SerialBound, "rgb", null, ["rgb"],
            [new("light", "RGB", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor)])]), 1, true);
        var items = DeviceDashboard.Items(Registrations, new(2, [Item]), State().Inventory with { Devices = [rgb] });
        Assert.Equal(Item, Assert.Single(items));
        Assert.Null(DeviceDashboard.Read(Item, Registrations, State() with { Inventory = State().Inventory with { Devices = [rgb] } }, 0, true).Value);
    }
    [Fact] public void PollingPreservesDraftAndCapturedRevisionUntilExplicitReset()
    {
        var draft = new DeviceCoolingDraft(Target); draft.Update(State(), "config", Setup(), 0, true);
        Assert.Equal(45, draft.Duty); draft.SetDuty(58);
        draft.Update(State(duty: 49), "config", Setup(), 0, true);
        Assert.Equal(58, draft.Duty); Assert.True(draft.CanApply);
        var edit = draft.Capture(); Assert.Equal(0, edit.Version.Target.ExpectedPolicyRevision);
        draft.Update(State(revision: 1, duty: 37), "config", Setup(), 0, true);
        Assert.Equal(58, draft.Duty); Assert.True(draft.Conflict); Assert.False(draft.CanApply);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Reset(); Assert.Equal(37, draft.Duty); draft.SetAutomatic(false); Assert.Equal(1, draft.Capture().Version.Target.ExpectedPolicyRevision);
    }
    [Theory]
    [InlineData(2, 1, "config")]
    [InlineData(1, 2, "config")]
    [InlineData(1, 1, "new-config")]
    public void ChangesToTargetTopologyOrSettingsDoNotRebaseAnEditedSlider(long generation, long topology, string configuration)
    {
        var draft = new DeviceCoolingDraft(Target); draft.Update(State(), "config", Setup(), 0, true); draft.SetDuty(58);
        draft.Update(State(generation: generation, topology: topology), configuration, Setup(), 0, true);
        Assert.True(draft.Conflict); Assert.Equal(58, draft.Duty); Assert.False(draft.CanApply);
    }
    [Fact] public void ReleasedSliderFollowsHardwareInsteadOfOldRequestedOrReportedOutput()
    {
        var draft = new DeviceCoolingDraft(Target); draft.Update(State(owned: true), "config", Setup(), 0, true);
        Assert.Equal(65, draft.Duty);
        draft.Update(State(revision: 1, duty: 37), "config", Setup(), 0, true); Assert.Equal(37, draft.Duty);
        draft.Update(State(revision: 1, duty: null), "config", Setup(), 0, true); Assert.Equal(37, draft.Duty);
        draft.Update(State(revision: 1, duty: 0), "config", Setup(), 0, true); Assert.Equal(0, draft.Duty); Assert.False(draft.CanApply);
    }
    [Fact] public void StaleDisconnectedOrUnconfiguredRowsCannotApply()
    {
        var draft = new DeviceCoolingDraft(Target);
        draft.Update(State(), "config", Setup(), 3, true); Assert.False(draft.CanApply);
        draft.Update(State(), "config", Setup(), 0, false); Assert.False(draft.CanApply);
        draft.Update(State(), "config", Setup() with { WiringConfirmed = false }, 0, true); Assert.False(draft.CanApply);
        draft.Update(State(), "config", Setup(), 0, true); Assert.False(draft.CanApply);
        draft.SetAutomatic(false); Assert.True(draft.CanApply);
    }
    [Fact] public void ManagedSelectionDoesNotWriteUntilExplicitlyCapturedAndKeepsConflictProtection()
    {
        var draft = new DeviceCoolingDraft(Target); draft.Update(State(owned: true), "config", Setup(), 0, true);
        draft.SelectDeviceManagement(); Assert.True(draft.CanApply);
        Assert.Equal(DeviceOperation.ReleaseCooling, draft.Capture().Operation);
        draft.Update(State(revision: 1, owned: true), "config", Setup(), 0, true);
        Assert.False(draft.CanApply); Assert.Throws<InvalidOperationException>(() => draft.Capture());
        // The separate explicit release action captures the displayed current revision, never the stale edit.
        Assert.Equal(1, draft.Capture(DeviceOperation.ReleaseCooling).Version.Target.ExpectedPolicyRevision);
    }
    [Fact] public void SelectingFixedBeforeSetupExplainsMissingConfigurationWithoutGrantingOutput()
    {
        var draft = new DeviceCoolingDraft(Target);
        var state = State(); var device = state.Inventory.Devices[0]; var endpoint = device.Device.Endpoints[0];
        state = state with { Inventory = state.Inventory with { Devices = [device with { Device = device.Device with {
            Endpoints = [endpoint with { Capabilities = endpoint.Capabilities.Select(c => c.Feature is DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty
                ? c with { Availability = DeviceAvailability.NeedsSetup } : c).ToArray() }] } }] } };
        draft.Update(state, "config", null, 0, true);
        Assert.True(draft.NeedsSetup); Assert.True(draft.CanRelease);
        draft.SetAutomatic(false); draft.SetDuty(60);
        Assert.True(draft.NeedsSetup); Assert.False(draft.Available); Assert.False(draft.CanApply);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Update(State(), "configured", Setup(), 0, true); draft.Reset(); draft.SetAutomatic(false);
        Assert.False(draft.NeedsSetup); Assert.True(draft.CanApply); Assert.Equal(DeviceOperation.SetDuty, draft.Capture().Operation);
    }
    [Fact] public void IncompleteSafetyNeedsSetupButACompleteConfigurationDoesNot()
    {
        var draft = new DeviceCoolingDraft(Target);
        foreach (var setup in new[] { Setup() with { WiringConfirmed = false }, Setup() with { Safety = Setup().Safety with { Minimum = null } } })
        {
            draft.Update(State(), "config", setup, 0, true); draft.SetAutomatic(false);
            Assert.True(draft.NeedsSetup); Assert.False(draft.CanApply);
        }
        draft.Update(State(), "config", Setup(), 0, true);
        Assert.False(draft.NeedsSetup); Assert.True(draft.CanApply);
    }
    [Fact] public void DisconnectedStaleUnsupportedOrFaultedFansDoNotOfferSetupAsTheirRemedy()
    {
        var draft = new DeviceCoolingDraft(Target);
        draft.Update(State(), "config", null, 0, false); Assert.False(draft.NeedsSetup);
        draft.Update(State(), "config", null, 3, true); Assert.False(draft.NeedsSetup);
        draft.Update(State() with { LifecycleError = "復旧が必要です" }, "config", null, 0, true); Assert.False(draft.NeedsSetup);
        var state = State(); var device = state.Inventory.Devices[0]; var endpoint = device.Device.Endpoints[0];
        state = state with { Inventory = state.Inventory with { Devices = [device with { Device = device.Device with {
            Endpoints = [endpoint with { Capabilities = endpoint.Capabilities.Where(c => c.Feature is not (DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty)).ToArray() }] } }] } };
        draft.Update(state, "config", null, 0, true); Assert.False(draft.NeedsSetup);
    }
    [Fact] public void ReleaseIsNeverOfferedAsAnImplementedFeatureWhenOnlyDutyWriteIsSupported()
    {
        var state = State(); var device = state.Inventory.Devices[0]; var endpoint = device.Device.Endpoints[0];
        state = state with { Inventory = state.Inventory with { Devices = [device with { Device = device.Device with {
            Endpoints = [endpoint with { Capabilities = endpoint.Capabilities.Where(c => c.Feature != DeviceFeature.ReleaseCooling).ToArray() }] } }] } };
        var draft = new DeviceCoolingDraft(Target); draft.Update(state, "config", Setup(), 0, true);
        Assert.False(draft.SupportsRelease); Assert.False(draft.CanRelease);
        Assert.Throws<InvalidOperationException>(() => draft.Capture(DeviceOperation.ReleaseCooling));
        draft.SetAutomatic(false); Assert.True(draft.CanApply);
    }
    [Fact] public async Task WorkspaceCommandsUseAuthenticatedPipeAndNeverRetargetAnOldDraft()
    {
        using var directory = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var snapshot = client.Snapshot!; Assert.True(snapshot.Connected); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        var target = new EndpointReference(demo.DeviceIds[1], "fan3"); var draft = new DeviceCoolingDraft(target);
        draft.Update(snapshot.State, snapshot.Configuration.Generation, snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == target), snapshot.ElapsedSeconds, true);
        draft.SetDuty(56); var edit = draft.Capture();
        var result = await client.ApplyCoolingAsync(edit); Assert.Equal(Outcome.Confirmed, result.Outcome);
        Assert.Equal(0, demo.Hubs[0].Writes); Assert.Equal(1, demo.Hubs[1].Writes);
        Assert.Equal(56, Assert.Single(result.Targets).ReportedDuty);
        Assert.NotEqual(snapshot.Configuration.Generation, client.Snapshot!.Configuration.Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyCoolingAsync(edit));
        Assert.Equal(1, demo.Hubs[1].Writes);
        Assert.True((await client.ReleaseAsync()).Success); Assert.False(client.Snapshot!.Connected);
    }
}
