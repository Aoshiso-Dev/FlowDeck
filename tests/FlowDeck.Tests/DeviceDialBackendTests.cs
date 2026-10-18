using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

[Collection("Wall-clock transport")]
public class DeviceDialBackendTests
{
    private static DialTarget Fans(DeviceDialBackend backend) => backend.Read().Targets.Single(t => t.Id == "cooling:all-fans");
    private static DialTarget Pump(DeviceDialBackend backend) => backend.Read().Targets.Single(t => t.Id == "cooling:pumps");
    private static async Task SetDuty(DeviceWorkspaceClient client, EndpointReference target, int duty)
    {
        var s = client.Snapshot!; var draft = new DeviceCoolingDraft(target);
        draft.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == target), s.ElapsedSeconds, s.Connected, s.Configuration.Configuration.Bindings);
        draft.SetDuty(duty); Assert.Equal(Outcome.Confirmed, (await client.ApplyCoolingAsync(draft.Capture())).Outcome);
    }
    private static DialCoolingBatchEdit Edit(DeviceWorkspaceSnapshot s, string role = "fan", int value = 61)
    {
        var members = DeviceDialBackend.MemberTargets(s, role);
        return new(s.Session!, s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation, role,
            members.Select(t => { var e = s.State.Endpoints.Single(e => e.Target == t); return new DeviceTargetVersion(t, e.Generation, e.PolicyRevision); }).ToArray(), value);
    }
    [Fact] public async Task AllFansAndPumpsAreTwoSeparateConfirmedTargetsWithoutChangingSavedGroupsOrLighting()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var backend = new DeviceDialBackend(client);
        var targets = backend.Read().Targets; Assert.Equal(new[] { "cooling:all-fans", "cooling:pumps" }, targets.Select(t => t.Id));
        Assert.Equal(9, targets[0].MemberCount); Assert.Equal(1, targets[1].MemberCount); Assert.Equal("全ファン・9台", targets[0].Label);
        var before = client.Snapshot!; var groups = SettingsTransfer.Hash(before.Configuration.Configuration.Cooling.Groups);
        var lights = SettingsTransfer.Hash(before.Configuration.Configuration.Lighting); var pump = DeviceDialBackend.MemberTargets(before, "pump").Single();
        var first = Fans(backend); Assert.True(first.Enabled, first.Message);
        var result = await backend.AdjustAsync(first.Id, first.Version, 61);
        Assert.Equal(DialAdjustmentStatus.Confirmed, result.Status); Assert.Contains("確認 9/9", result.Message);
        var next = Fans(backend); Assert.False(next.ValueMixed); Assert.Equal(61, next.Value); Assert.Equal(next.Version, result.ConfirmedVersion);
        Assert.Equal(first.ControlContext, next.ControlContext);
        Assert.All(client.Snapshot!.State.Endpoints.Where(e => DeviceDialBackend.MemberTargets(before, "fan").Contains(e.Target)), e => Assert.Equal(61, e.ReportedDuty));
        Assert.False(client.Snapshot.State.Endpoints.Single(e => e.Target == pump).Owned);
        var fanVersions = client.Snapshot.State.Endpoints.Where(e => e.Target != pump).Select(e => (e.Target, e.PolicyRevision)).ToArray();
        var pumpTarget = Pump(backend); Assert.Equal(DialAdjustmentStatus.Confirmed, (await backend.AdjustAsync(pumpTarget.Id, pumpTarget.Version, 73)).Status);
        Assert.Equal(73, Pump(backend).Value); Assert.Equal(61, Fans(backend).Value);
        Assert.Equal(fanVersions, client.Snapshot!.State.Endpoints.Where(e => e.Target != pump).Select(e => (e.Target, e.PolicyRevision)).ToArray());
        Assert.Equal(groups, SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration.Cooling.Groups));
        Assert.Equal(lights, SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration.Lighting)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
        Assert.True(client.Connected);
    }
    [Fact] public async Task MixedValuesUseMaximumAndApplyingThatSameMaximumStillUnifiesEveryMember()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        await SetDuty(client, new(demo.DeviceIds[0], "fan1"), 66); var backend = new DeviceDialBackend(client); var first = Fans(backend);
        Assert.True(first.ValueMixed); Assert.Equal(66, first.Value); var writes = demo.Hubs.Sum(h => h.Writes);
        var result = await backend.AdjustAsync(first.Id, first.Version, first.Value);
        Assert.Equal(DialAdjustmentStatus.Confirmed, result.Status); Assert.Equal(writes + 9, demo.Hubs.Sum(h => h.Writes));
        Assert.False(Fans(backend).ValueMixed); Assert.Equal(66, Fans(backend).Value); Assert.Equal(first.ControlContext, Fans(backend).ControlContext);
    }
    [Fact] public async Task GroupMinimumIsStrictestMemberAndNoMemberGetsADifferentClampedDuty()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var member = new EndpointReference(demo.DeviceIds[0], "fan1");
        var safety = new DeviceCoolingSettingsDraft(client.Snapshot!, member); safety.Change(safety.Safety with { Minimum = 60 }, safety.Profiles); safety.Confirmed = true;
        await client.ApplySetupAsync(await client.PreviewSetupAsync(safety.Capture())); await client.RefreshAsync(true);
        var backend = new DeviceDialBackend(client); var first = Fans(backend); Assert.Equal(60, first.Minimum);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.AdjustAsync(first.Id, first.Version, 59)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        Assert.Equal(DialAdjustmentStatus.Confirmed, (await backend.AdjustAsync(first.Id, first.Version, 60)).Status);
        Assert.All(DeviceDialBackend.MemberTargets(client.Snapshot!, "fan"), t => Assert.Equal(60, client.Snapshot!.State.Endpoints.Single(e => e.Target == t).ReportedDuty));
    }
    [Theory] [InlineData("pending")] [InlineData("readback")] [InlineData("execution")] [InlineData("fault")] [InlineData("edited")] [InlineData("unavailable")]
    public async Task TemporarilyBlockedMemberDisablesWholeGroupAndNeverShrinksMembership(string reason)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        var original = DeviceDialBackend.ReadSnapshot(s).Targets[0];
        if (reason == "pending") s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e.Target == target ? e with { Outcome = Outcome.Pending } : e).ToArray() } };
        if (reason == "execution") s = s with { State = s.State with { Endpoints = s.State.Endpoints.Where(e => e.Target != target).ToArray() } };
        if (reason == "readback") s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Select(t => t.Target == target && t.Feature == DeviceFeature.DutyRead ? t with { Value = null, Stale = true } : t).ToArray() } };
        if (reason == "fault") s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e.Target == target ? e with { Cooling = new(FanMode.Manual, 45, true, "failure", false, false) } : e).ToArray() } };
        if (reason == "unavailable") s = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d.Device.Id == target.DeviceId ? d with { Device = d.Device with { Endpoints = d.Device.Endpoints.Select(e => e.Id == target.EndpointId ? e with { Capabilities = e.Capabilities.Select(c => c.Feature == DeviceFeature.FixedDuty ? c with { Availability = DeviceAvailability.TemporarilyUnavailable } : c).ToArray() } : e).ToArray() } } : d).ToArray() } } };
        var actual = DeviceDialBackend.ReadSnapshot(s, t => reason == "edited" && t == target).Targets[0];
        Assert.Equal(original.MemberCount, actual.MemberCount); Assert.Equal(original.Id, actual.Id); Assert.False(actual.Enabled, actual.Message);
        if (reason == "pending") { Assert.True(actual.Busy); Assert.Equal(original.ControlContext, actual.ControlContext); }
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task ExplicitPumpOverridesFanUnknownIsExcludedAndDisconnectedOrUnconfiguredMembersAreExcluded()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!; var config = s.Configuration.Configuration;
        var a = new EndpointReference(demo.DeviceIds[0], "fan1"); var b = new EndpointReference(demo.DeviceIds[0], "fan2"); var c = new EndpointReference(demo.DeviceIds[0], "fan3");
        config = config with { Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d.Id == a.DeviceId ? d with { Endpoints = d.Endpoints.Select(e => e.Id == a.EndpointId ? e with { Role = "pump" } : e.Id == b.EndpointId ? e with { Role = "" } : e).ToArray() } : d).ToArray() },
            Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Where(e => e.Target != c).ToArray() } };
        s = s with { Configuration = s.Configuration with { Configuration = config } };
        var view = DeviceDialBackend.ReadSnapshot(s); Assert.Equal(6, view.Targets[0].MemberCount); Assert.Equal(2, view.Targets[1].MemberCount);
        Assert.DoesNotContain(b, DeviceDialBackend.MemberTargets(s, "fan")); Assert.DoesNotContain(c, DeviceDialBackend.MemberTargets(s, "fan"));
        s = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d => d.Device.Id == demo.DeviceIds[1] ? d with { Connected = false } : d).ToArray() } } };
        view = DeviceDialBackend.ReadSnapshot(s); Assert.Equal(2, view.Targets[0].MemberCount); Assert.Equal(1, view.Targets[1].MemberCount);
    }
    [Fact] public async Task UnconfiguredExplicitPumpRemainsASeparateDisabledTargetWithoutInventingSafety()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!;
        var pump = DeviceDialBackend.MemberTargets(s, "pump").Single(); var config = s.Configuration.Configuration;
        config = config with { Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Where(e => e.Target != pump).ToArray() } };
        s = s with { Configuration = s.Configuration with { Configuration = config } };
        var view = DeviceDialBackend.ReadSnapshot(s); Assert.Equal(2, view.Targets.Length); Assert.Equal(9, view.Targets[0].MemberCount);
        Assert.Equal("cooling:pumps", view.Targets[1].Id); Assert.False(view.Targets[1].Enabled); Assert.Contains("制御設定が必要", view.Targets[1].Message);
        Assert.Contains(pump, DeviceDialBackend.MemberTargets(s, "pump")); Assert.DoesNotContain(pump, DeviceDialBackend.MemberTargets(s, "fan"));
        Assert.DoesNotContain(s.Configuration.Configuration.Cooling.Endpoints, e => e.Target == pump); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task DashboardPumpOverrideReclassifiesRegisteredFanAndInvalidatesBothGroupContexts()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[0], "fan1"); var config = s.Configuration.Configuration;
        Assert.Equal("fan", config.Devices.Devices.Single(d => d.Id == target.DeviceId).Endpoints.Single(e => e.Id == target.EndpointId).Role);
        var before = DeviceDialBackend.ReadSnapshot(s).Targets;
        config = config with { Dashboard = config.Dashboard with { Items = config.Dashboard.Items.Select(i => i.Target == target ? i with { Role = "pump" } : i).ToArray() } };
        s = s with { Configuration = s.Configuration with { Configuration = config } }; var after = DeviceDialBackend.ReadSnapshot(s).Targets;
        Assert.Equal(8, after[0].MemberCount); Assert.Equal(2, after[1].MemberCount);
        Assert.DoesNotContain(target, DeviceDialBackend.MemberTargets(s, "fan")); Assert.Contains(target, DeviceDialBackend.MemberTargets(s, "pump"));
        for (var i = 0; i < 2; i++) { Assert.NotEqual(before[i].ControlContext, after[i].ControlContext); Assert.NotEqual(before[i].Version, after[i].Version); }
    }
    [Fact] public async Task PartialResultReportsCountCancelsContinuationAndRetainsFailedMember()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        var raw = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); await using var client = new DeviceWorkspaceClient(raw); await client.ConnectAsync();
        var snapshot = client.Snapshot!; var a = new EndpointReference(demo.DeviceIds[0], "fan3"); var b = new EndpointReference(demo.DeviceIds[1], "fan1");
        var config = snapshot.Configuration.Configuration; config = config with { Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d with { Endpoints = d.Endpoints.Select(e => e.Kind == EndpointKind.Cooling ? e with { Role = new EndpointReference(d.Id, e.Id) == a || new EndpointReference(d.Id, e.Id) == b ? "fan" : "" } : e).ToArray() }).ToArray() } };
        var preview = await raw.PreviewConfigurationAsync(snapshot.Configuration.Generation, config); await raw.ApplyConfigurationAsync(preview, snapshot.State.Inventory.TopologyRevision); await client.RefreshAsync(true);
        var backend = new DeviceDialBackend(client); var first = Fans(backend); Assert.Equal(2, first.MemberCount); demo.Hubs[0].FailNextFanWrite = true;
        var result = await backend.AdjustAsync(first.Id, first.Version, 66); Assert.Equal(DialAdjustmentStatus.Rejected, result.Status); Assert.Null(result.ConfirmedVersion); Assert.Contains("確認 1/2", result.Message);
        Assert.Equal(2, Fans(backend).MemberCount); Assert.False(Fans(backend).Enabled); Assert.Equal(66, client.Snapshot!.State.Endpoints.Single(e => e.Target == b).ReportedDuty);
    }
    [Fact] public async Task GuiEditOrOldVersionRejectsWholeGroupAndEditDuringWriteRevokesContinuation()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var target = new EndpointReference(demo.DeviceIds[0], "fan1");
        var edited = false; var backend = new DeviceDialBackend(client, t => t == target && edited); var first = Fans(backend);
        edited = true; Assert.False(Fans(backend).Enabled); await Assert.ThrowsAsync<InvalidOperationException>(() => backend.AdjustAsync(first.Id, first.Version, 61)); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        edited = false; await SetDuty(client, target, 55); await Assert.ThrowsAsync<InvalidOperationException>(() => backend.AdjustAsync(first.Id, first.Version, 61)); first = Fans(backend);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demo.Hubs[0].BeforeFanWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var applying = backend.AdjustAsync(first.Id, first.Version, 63);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); edited = true; }
        finally { demo.Hubs[0].BeforeFanWrite = null; release.TrySetResult(); }
        var result = await applying; Assert.Equal(DialAdjustmentStatus.Rejected, result.Status); Assert.Null(result.ConfirmedVersion);
        Assert.False(Fans(backend).Enabled); Assert.Equal(9, Fans(backend).MemberCount);
    }
    [Fact] public async Task RpmUsesMeasuredFanAndPumpRangesWithoutChangingControlAuthority()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot! with { ElapsedSeconds = 0 };
        var fans = DeviceDialBackend.MemberTargets(s, "fan"); var pump = DeviceDialBackend.MemberTargets(s, "pump").Single();
        var before = DeviceDialBackend.ReadSnapshot(s).Targets;
        s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Select(t => t.Feature != DeviceFeature.Rpm ? t : t with {
            Value = t.Target == pump ? 2450.6 : Array.IndexOf(fans, t.Target) == 0 ? 0 : 1000 + Array.IndexOf(fans, t.Target) * 100.1,
            AgeSeconds = 0, Stale = false, Problem = null, Issue = DeviceMeasurementIssue.None }).ToArray() } };
        var after = DeviceDialBackend.ReadSnapshot(s).Targets;
        Assert.Equal((0, 1801, 9), (after[0].RpmMin, after[0].RpmMax, after[0].RpmCount));
        Assert.Equal((2451, 2451, 1), (after[1].RpmMin, after[1].RpmMax, after[1].RpmCount));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(before[i].Version, after[i].Version); Assert.Equal(before[i].ControlContext, after[i].ControlContext);
            Assert.Equal(before[i].Enabled, after[i].Enabled); Assert.Equal(before[i].Message, after[i].Message);
            Assert.Equal(before[i].Value, after[i].Value); Assert.Equal(before[i].ValueMixed, after[i].ValueMixed);
        }
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task RpmExcludesMissingInvalidAndExpiredSamplesButKeepsMeasuredZero()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot! with { ElapsedSeconds = 0.5 };
        var fans = DeviceDialBackend.MemberTargets(s, "fan");
        s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Select(t =>
        {
            if (t.Feature != DeviceFeature.Rpm) return t;
            var sample = t with { AgeSeconds = 0, Stale = false, Problem = null, Issue = DeviceMeasurementIssue.None };
            return Array.IndexOf(fans, t.Target) switch {
                0 => sample with { Value = 0 }, 1 => sample with { Value = 1234.6 }, 2 => sample with { Value = null },
                3 => sample with { Value = double.NaN }, 4 => sample with { Value = -1 }, 5 => sample with { Value = 1_000_001 },
                6 => sample with { Value = 2200, Stale = true }, 7 => sample with { Value = double.PositiveInfinity },
                8 => sample with { Value = 1300, AgeSeconds = 2.5 }, _ => sample with { Value = null } };
        }).ToArray() } };
        var view = DeviceDialBackend.ReadSnapshot(s).Targets;
        Assert.Equal((0, 1235, 2), (view[0].RpmMin, view[0].RpmMax, view[0].RpmCount));
        Assert.Equal(9, view[0].MemberCount); Assert.False(view[0].Enabled); // Existing RPM safety preflight still applies.
        Assert.Null(view[1].RpmMin); Assert.Null(view[1].RpmMax); Assert.Equal(0, view[1].RpmCount); Assert.False(view[1].Enabled);
        s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Where(t => t.Feature != DeviceFeature.Rpm).ToArray() } };
        Assert.All(DeviceDialBackend.ReadSnapshot(s).Targets, t => { Assert.Null(t.RpmMin); Assert.Null(t.RpmMax); Assert.Equal(0, t.RpmCount); Assert.False(t.Enabled); });
    }
    [Fact] public async Task RpmUpperBoundAndSingleValidSampleUseTheSameMinAndMax()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = client.Snapshot! with { ElapsedSeconds = 0 };
        var fan = DeviceDialBackend.MemberTargets(s, "fan").First();
        var rpm = new DeviceTelemetry(fan, DeviceFeature.Rpm, 1_000_000, 0, false);
        s = s with { State = s.State with { Telemetry = [.. s.State.Telemetry!.Where(t => t.Feature != DeviceFeature.Rpm), rpm] } };
        var target = DeviceDialBackend.ReadSnapshot(s).Targets[0]; Assert.Equal((1_000_000, 1_000_000, 1), (target.RpmMin, target.RpmMax, target.RpmCount));
        // Duplicate or acquisition-error samples must not be counted as an extra group member.
        foreach (var samples in new[] { new[] { rpm, rpm }, new[] { rpm with { Issue = DeviceMeasurementIssue.Unavailable } }, new[] { rpm with { Problem = "unavailable" } } })
        {
            var changed = s with { State = s.State with { Telemetry = [.. s.State.Telemetry!.Where(t => t.Feature != DeviceFeature.Rpm), .. samples] } };
            target = DeviceDialBackend.ReadSnapshot(changed).Targets[0]; Assert.Null(target.RpmMin); Assert.Null(target.RpmMax); Assert.Equal(0, target.RpmCount);
        }
    }
    [Theory] [InlineData("subset")] [InlineData("role")] [InlineData("session")] [InlineData("epoch")] [InlineData("topology")] [InlineData("configuration")] [InlineData("generation")] [InlineData("revision")] [InlineData("minimum")] [InlineData("gui")]
    public async Task BatchClientRejectsChangedAuthorityAndPartialMembershipBeforeAnyWrite(string reason)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var edit = Edit(client.Snapshot!);
        edit = reason switch { "subset" => edit with { Targets = [edit.Targets[0]] }, "role" => edit with { Role = "pump" },
            "session" => edit with { Session = "other" }, "epoch" => edit with { Epoch = "other" }, "topology" => edit with { Topology = edit.Topology + 1 },
            "configuration" => edit with { ConfigurationGeneration = "other" }, "generation" => edit with { Targets = [edit.Targets[0] with { Generation = edit.Targets[0].Generation + 1 }, .. edit.Targets.Skip(1)] },
            "revision" => edit with { Targets = [edit.Targets[0] with { ExpectedPolicyRevision = edit.Targets[0].ExpectedPolicyRevision + 1 }, .. edit.Targets.Skip(1)] },
            "minimum" => edit with { Duty = 29 }, _ => edit };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyDialCoolingBatchAsync(edit, () => reason != "gui")); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
}
