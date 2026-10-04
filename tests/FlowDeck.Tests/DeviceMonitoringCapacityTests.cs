using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceMonitoringCapacityTests
{
    private const string Generation = "0123456789abcdef0123456789abcdef";
    private static DeviceServiceState State(int count = 320)
    {
        var devices = Enumerable.Range(0, (count + 159) / 160).Select(d => new DeviceInstance(new DeviceObservation("device-" + d,
            "source-" + d, "同名の機器", DeviceKind.Motherboard, IdentityConfidence.ConnectionBound, "location-" + d, null, ["resource-" + d],
            Enumerable.Range(d * 160, Math.Min(160, count - d * 160)).Select(n => new DeviceEndpoint("sensor-" + n, "同名の温度", EndpointKind.Sensor,
                [new(DeviceFeature.Temperature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)], SensorId: "native-" + n)).ToArray()), 1, true)).ToArray();
        var telemetry = devices.SelectMany(d => d.Device.Endpoints.Select(e => new DeviceTelemetry(new(d.Device.Id, e.Id), DeviceFeature.Temperature, 42, 0, false))).ToArray();
        return new(new(Generation, 1, devices, 1), [], false, null, telemetry);
    }
    private static DeviceDashboardItem[] Items(DeviceServiceState state) => DeviceDashboard.Items(new(2, []), new(2, []), state.Inventory);
    private static DeviceRegistrations Registered(DeviceServiceState state) => new(2, state.Inventory.Devices.Select(d =>
        new RegisteredDevice(d.Device.Id, d.Device.Name, d.Device.Provider, d.Device.Kind,
            d.Device.Endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind, "temperature")).ToArray())).ToArray());

    [Fact] public void DiscoveryKeepsAllSameNamedAndSameValuedSensorsBeyondTheDashboardLimit()
    {
        var state = State(); var values = Items(state);
        Assert.Equal(320, values.Length); Assert.Equal(320, values.DistinctBy(i => (i.Target, i.Measurement)).Count());
        Assert.All(values, i => Assert.False(i.Visible));
        Assert.Equal(values.Select(i => i.Target).OrderBy(t => t.DeviceId).ThenBy(t => t.EndpointId),
            Items(state with { Inventory = state.Inventory with { Devices = state.Inventory.Devices.Reverse().ToArray() } })
                .Select(i => i.Target).OrderBy(t => t.DeviceId).ThenBy(t => t.EndpointId));
    }
    [Fact] public void EveryCandidateIsReachableByPageAndSelectionsSurvivePageChangesAndRefresh()
    {
        var state = State(); var draft = new DeviceMonitoringDraft(); draft.Update(new(2, []), new(2, []), state, Generation, 0, true);
        Assert.Equal(10, draft.PageCount); var visited = new HashSet<string>();
        for (var page = 0; page < draft.PageCount; page++)
        {
            Assert.Equal(page, draft.Page); Assert.Equal(32, draft.VisibleCandidates.Length);
            foreach (var candidate in draft.VisibleCandidates) Assert.True(visited.Add(DeviceMonitoringDraft.Key(candidate)));
            draft.Select(draft.VisibleCandidates[0], true); draft.MovePage(1);
        }
        Assert.Equal(320, visited.Count); Assert.Equal(10, draft.SelectedCount);
        draft.Update(new(2, []), new(2, []), state, Generation, 0, true);
        Assert.Equal(9, draft.Page); Assert.True(draft.IsSelected(draft.VisibleCandidates[0])); Assert.True(draft.CanRegister);
        for (var i = 0; i < 12; i++) draft.MovePage(-1);
        Assert.Equal(0, draft.Page); Assert.True(draft.IsSelected(draft.VisibleCandidates[0])); Assert.Equal(10, draft.Capture().Items.Length);
    }
    [Theory][InlineData(0)][InlineData(255)][InlineData(256)]
    public void SelectionLimitIncludesHiddenRegisteredItemsAndDeselectionFreesOnlyItsOwnSlot(int savedCount)
    {
        var state = State(); var saved = new DeviceDashboardConfiguration(2, Items(state).Take(savedCount).ToArray());
        var draft = new DeviceMonitoringDraft(); draft.Update(Registered(state), saved, state, Generation, 0, true);
        Assert.Equal(320 - savedCount, draft.CandidateCount); Assert.Equal(256 - savedCount, draft.RemainingSlots);
        foreach (var item in draft.Candidates.Take(draft.RemainingSlots)) draft.Select(item, true);
        Assert.False(draft.CanSelectMore); Assert.Equal(savedCount < 256, draft.CanRegister);
        var extra = draft.Candidates[256 - savedCount];
        Assert.Contains("256", Assert.Throws<InvalidOperationException>(() => draft.Select(extra, true)).Message);
        if (savedCount == 256) return;
        draft.Select(draft.Candidates[0], false); Assert.True(draft.CanSelectMore); draft.Select(extra, true);
        Assert.True(draft.CanRegister); Assert.Equal(256 - savedCount, draft.Capture().Items.Length);
    }
    [Fact] public void RemovedDisplayItemsRemainRemovedAcrossPollingAndCanBeExplicitlyRestored()
    {
        var state = State(); var registrations = Registered(state); var saved = new DeviceDashboardConfiguration(2, Items(state).Take(256).ToArray());
        var draft = new DeviceDashboardDraft(); draft.Update(registrations, saved, state, Generation, true);
        Assert.Equal(256, draft.Items.Length); // Registered endpoints outside the dashboard must not be silently adopted.
        var removed = draft.Items[0]; draft.Remove(removed.Target, removed.Measurement);
        draft.Update(registrations, saved, state, Generation, true); Assert.Equal(255, draft.Items.Length); Assert.True(draft.CanSave);
        Assert.DoesNotContain(draft.Items, i => i.Target == removed.Target); Assert.Equal(255, draft.Capture().Dashboard.Items.Length);
        draft.Reset(); Assert.Equal(saved.Items, draft.Items); Assert.False(draft.Dirty);
        draft.Remove(removed.Target, removed.Measurement); var persisted = draft.Capture().Dashboard;
        draft.Reset(); draft.Update(registrations, persisted, state, "changed", true); Assert.Equal(255, draft.Items.Length);
        var monitor = new DeviceMonitoringDraft(); monitor.Update(registrations, persisted, state, "changed", 0, true);
        Assert.Contains(monitor.Candidates, c => c.Selection.Item.Target == removed.Target); Assert.Equal(1, monitor.RemainingSlots);
    }
    [Fact] public void ShrinkingInventoryClampsPageButKeepsMissingSelectionsAndRejectsOldTopology()
    {
        var state = State(); var draft = new DeviceMonitoringDraft(); draft.Update(new(2, []), new(2, []), state, Generation, 0, true);
        for (var i = 0; i < 9; i++) draft.MovePage(1);
        var old = draft.VisibleCandidates[0]; draft.Select(old, true);
        var reduced = State(1); reduced = reduced with { Inventory = reduced.Inventory with { TopologyRevision = 2 } };
        draft.Update(new(2, []), new(2, []), reduced, Generation, 0, true);
        Assert.Equal(0, draft.Page); Assert.Equal(2, draft.CandidateCount); Assert.True(draft.IsSelected(old));
        Assert.True(draft.Conflict); Assert.False(draft.CanRegister); draft.Reset(); Assert.False(draft.Dirty);
    }
    [Fact] public async Task ServiceRejectsOverflowAtomicallyAndRemovingADisplayItemAllowsExplicitReplacement()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, monitoringOnly: true);
        demo.AdditionalSensorsPerDevice = 160; await demo.Runtime.PollAsync();
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var draft = new DeviceMonitoringDraft();
        draft.Update(snapshot.Configuration.Configuration.Devices, snapshot.Configuration.Configuration.Dashboard, snapshot.State, snapshot.Configuration.Generation, 0, true);
        Assert.Equal(332, draft.CandidateCount);
        foreach (var candidate in draft.Candidates.Take(256)) draft.Select(candidate, true);
        await client.RegisterMonitoringAsync(draft.Capture()); snapshot = client.Snapshot!;
        Assert.Equal(256, snapshot.Configuration.Configuration.Dashboard.Items.Length);
        var before = snapshot.Configuration.Generation; draft.Reset();
        draft.Update(snapshot.Configuration.Configuration.Devices, snapshot.Configuration.Configuration.Dashboard, snapshot.State, before, 0, true);
        var extra = draft.Candidates[0].Selection;
        var request = new DeviceMonitoringEdit(snapshot.State.Inventory.Epoch, before, snapshot.State.Inventory.TopologyRevision, [extra]);
        var error = await Assert.ThrowsAsync<IOException>(() => client.RegisterMonitoringAsync(request)); Assert.Contains("256", error.Message);
        await client.RefreshAsync(true); Assert.Equal(before, client.Snapshot!.Configuration.Generation);
        snapshot = client.Snapshot;
        await client.SaveDashboardAsync(new(snapshot.State.Inventory.Epoch, before, new(2, snapshot.Configuration.Configuration.Dashboard.Items.Skip(1).ToArray())));
        snapshot = client.Snapshot!;
        await client.RegisterMonitoringAsync(request with { ConfigurationGeneration = snapshot.Configuration.Generation, TopologyRevision = snapshot.State.Inventory.TopologyRevision });
        Assert.Equal(256, client.Snapshot!.Configuration.Configuration.Dashboard.Items.Length);
        Assert.Contains(client.Snapshot.Configuration.Configuration.Dashboard.Items, i => i.Target == extra.Item.Target);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
