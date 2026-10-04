using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceLightingLevelDraftTests
{
    private static readonly EndpointReference A = new("a", "led1"), B = new("b", "led1");
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 200)], Brightness: 80);
    private static DeviceWorkspaceSnapshot Snapshot()
    {
        var config = new DeviceConfigurationPackage(new(2, new[] { A, B }.Select(t => new RegisteredDevice(t.DeviceId, "同名", "demo", DeviceKind.Memory, [new(t.EndpointId, "照明", EndpointKind.Lighting)])).ToArray()),
            new(2, new('a', 32), []), new(2, [], []), new(2, [], [new("group", "前面", EndpointKind.Lighting, [A], 3)], [], [], [], Preferences: [new(A, Blue), new(B, Blue)]),
            new(2, []), new(2, new(), new(), new(), new(), new([], [])), new(2, "fresh", new('a', 64), [], []));
        var state = new DeviceServiceState(new(new('a', 32), 1, new[] { A, B }.Select(t => new DeviceInstance(
            new(t.DeviceId, "demo", "同名", DeviceKind.Memory, IdentityConfidence.SerialBound, t.DeviceId, null, [t.DeviceId],
                [new(t.EndpointId, "照明", EndpointKind.Lighting, [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]), 1, true)).ToArray(), 1),
            [new(A, 1, 1, Outcome.WrittenUnverified), new(B, 1, 1, Outcome.WrittenUnverified)], false, null);
        return new(state, new(new('b', 32), config, []), 0, true, null, Session: new('c', 32));
    }
    private static DeviceLightingLevelDraft Draft(DeviceWorkspaceSnapshot s) { var d = new DeviceLightingLevelDraft(); d.Update(s, [B]); return d; }
    private static DeviceWorkspaceSnapshot WithLibrary(DeviceWorkspaceSnapshot s, LightingConfiguration l) => s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Lighting = l } } };
    [Theory][InlineData("missing-smoke")][InlineData("other-smoke")][InlineData("external")]
    public void LevelGuiSmokeRequiresIsolatedEmbeddedHardware(string mode)
    {
        string[] args = ["--devices-preview", "--demo", "--data-dir", Path.GetTempPath(), "--lighting-levels"];
        if (mode != "missing-smoke") args = [.. args, "--smoke"];
        if (mode == "other-smoke") args = [.. args, "--lighting-library"];
        if (mode == "external") args = [.. args, "--devices-pipe", "FlowDeck.Devices.Demo." + new string('a', 32), "--server-pid", "1"];
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse(args));
    }
    [Fact] public void DeletedGroupAndUnregisteredSelectionCannotFallBackToOtherTargets()
    {
        var s = Snapshot(); var d = Draft(s); d.Select(LightingAdjustmentScope.Group, "missing"); Assert.Empty(d.Targets); Assert.NotNull(d.Problem);
        d.Select(LightingAdjustmentScope.Targets); d.Update(s, [new("unknown", "led1")]); Assert.NotNull(d.Problem);
        Assert.Throws<InvalidOperationException>(() => d.Capture(LightingAdjustmentKind.Restore));
    }
    [Fact] public void ScopeCapturesExactSavedMembershipAndLayerRatherThanEffectiveBrightness()
    {
        var s = Snapshot(); var d = Draft(s); Assert.Equal(100, d.Brightness); Assert.Equal(2, d.Targets.Length);
        d.SetBrightness(50); var all = d.Capture(LightingAdjustmentKind.Brightness); Assert.Equal(s.Session, all.Session); Assert.Equal(2, all.Lighting.Version.Targets.Length);
        d.Update(s, [A]); Assert.False(d.Conflict); Assert.Equal(50, d.Brightness);
        Assert.Throws<InvalidOperationException>(() => d.Select(LightingAdjustmentScope.Group, "group"));
        d.Reset(); d.Select(LightingAdjustmentScope.Group, "group"); var group = d.Capture(LightingAdjustmentKind.Off).Lighting;
        Assert.Equal(A, Assert.Single(group.Version.Targets).Target); Assert.Equal(3, group.Adjustment!.GroupRevision);
        d.Select(LightingAdjustmentScope.Targets); Assert.Equal(A, Assert.Single(d.Capture(LightingAdjustmentKind.Restore).Lighting.Version.Targets).Target);
    }
    [Theory][InlineData("epoch")][InlineData("session")][InlineData("generation")][InlineData("topology")][InlineData("endpoint")][InlineData("policy")][InlineData("selection")]
    public void EditingConflictsStayVisibleUntilReload(string kind)
    {
        var s = Snapshot(); var d = Draft(s); d.Select(LightingAdjustmentScope.Targets); d.SetBrightness(41);
        var state = s.State;
        var changed = kind switch {
            "epoch" => s with { State = state with { Inventory = state.Inventory with { Epoch = new('d', 32) } } },
            "session" => s with { Session = new('d', 32) },
            "generation" => s with { Configuration = s.Configuration with { Generation = new('d', 32) } },
            "topology" => s with { State = state with { Inventory = state.Inventory with { TopologyRevision = 2 } } },
            "endpoint" => s with { State = state with { Endpoints = state.Endpoints.Select(e => e with { Generation = 2 }).ToArray() } },
            "policy" => s with { State = state with { Endpoints = state.Endpoints.Select(e => e with { PolicyRevision = 2 }).ToArray() } }, _ => s };
        d.Update(changed, kind == "selection" ? [A] : [B]); d.Update(s, [B]);
        Assert.True(d.Conflict); Assert.Equal(41, d.Brightness); Assert.Throws<InvalidOperationException>(() => d.Capture(LightingAdjustmentKind.Brightness));
        d.Reset(); Assert.False(d.Conflict); Assert.Equal(100, d.Brightness); Assert.Null(d.Problem);
    }
    [Theory][InlineData("disconnect")][InlineData("stale")][InlineData("nan")][InlineData("release")][InlineData("switch")][InlineData("exit")][InlineData("offline")][InlineData("missing-program")]
    public void UnavailableTargetsAreNeverSilentlyRemovedFromAll(string kind)
    {
        var s = Snapshot(); var d = Draft(s);
        var changed = kind switch {
            "disconnect" => s with { Connected = false }, "stale" => s with { ElapsedSeconds = 3 }, "nan" => s with { ElapsedSeconds = double.NaN },
            "release" => s with { State = s.State with { Releasing = true } }, "switch" => s with { State = s.State with { ConfigurationChanging = true } },
            "exit" => s with { ExitAttempted = true },
            "offline" => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(i => i.Device.Id == A.DeviceId ? i with { Connected = false } : i).ToArray() } } },
            _ => WithLibrary(s, s.Configuration.Configuration.Lighting with { Preferences = [new(B, Blue)] }) };
        d.Update(changed, [B]); Assert.Equal(2, d.Targets.Length); Assert.NotNull(d.Problem); Assert.Throws<InvalidOperationException>(() => d.Capture(LightingAdjustmentKind.Off));
    }
    [Fact] public void MixedLevelsOffStateAndGroupOwnershipAreDisplayedWithoutInventingACommonValue()
    {
        var s = Snapshot(); var l = s.Configuration.Configuration.Lighting;
        s = WithLibrary(s, l with { TargetAdjustments = [new(A, Blue, new(50), new(20, true), new(30), "group", 3), new(B, Blue, new(), new(), new())] });
        var d = Draft(s); Assert.True(d.Mixed); Assert.Contains("混在", d.Current); Assert.Throws<InvalidOperationException>(() => d.Capture(LightingAdjustmentKind.Brightness));
        d.SetBrightness(45); Assert.False(d.Conflict); Assert.Equal(45, d.Capture(LightingAdjustmentKind.Brightness).Lighting.Adjustment!.Brightness);
        Assert.Throws<InvalidOperationException>(() => d.Capture(LightingAdjustmentKind.Off)); d.Reset(); d.Select(LightingAdjustmentScope.Group, "group");
        Assert.Equal(20, d.Brightness); Assert.Contains("消灯中", d.Current);
        d.Update(WithLibrary(s, s.Configuration.Configuration.Lighting with { Groups = [new("group", "改訂", EndpointKind.Lighting, [A], 4)] }), [B]);
        Assert.Equal(100, d.Brightness); Assert.DoesNotContain("消灯中", d.Current);
    }
    [Fact] public async Task AuthenticatedLayersComposeAndRepeatedValuesDoNotAccumulateOrChangeExcludedEndpoints()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var refs = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        var program = new DeviceLightingDraft(); var s = client.Snapshot!; program.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, 0, true);
        program.Select(refs); program.SetProgram(Blue); await client.ApplyLightingAsync(program.Capture());
        var group = new DeviceLightingLibraryDraft(client.Snapshot!, LightingPresetKind.Group) { Name = "一台目" }; group.SetTargets([refs[0]]); await client.SaveLightingEditAsync(group.Capture());
        var d = new DeviceLightingLevelDraft();
        async Task Apply(LightingAdjustmentScope scope, int level)
        {
            d.Reset(); d.Update(client.Snapshot!, refs); d.Select(scope, scope == LightingAdjustmentScope.Group ? group.Id : null); d.SetBrightness(level);
            Assert.Equal(Outcome.WrittenUnverified, (await client.ApplyLightingLevelAsync(d.Capture(LightingAdjustmentKind.Brightness))).Outcome);
        }
        await Apply(LightingAdjustmentScope.All, 50); await Apply(LightingAdjustmentScope.All, 50);
        Assert.All(demo.Hubs, h => Assert.Equal(new RgbColor(0, 0, 80), h.LightEffects["led1"].Colors[0]));
        var untouched = demo.Hubs[1].LightingWrites; await Apply(LightingAdjustmentScope.Group, 50);
        Assert.Equal(new RgbColor(0, 0, 40), demo.Hubs[0].LightEffects["led1"].Colors[0]); Assert.Equal(untouched, demo.Hubs[1].LightingWrites);
        d.Reset(); d.Update(client.Snapshot!, [refs[0]]); d.Select(LightingAdjustmentScope.Targets); d.SetBrightness(50);
        var captured = d.Capture(LightingAdjustmentKind.Brightness);
        await Assert.ThrowsAsync<IOException>(() => client.ApplyLightingLevelAsync(captured with { Session = new('d', 32) }));
        await client.ApplyLightingLevelAsync(captured); Assert.Equal(new RgbColor(0, 0, 20), demo.Hubs[0].LightEffects["led1"].Colors[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyLightingLevelAsync(captured));
        foreach (var kind in new[] { LightingAdjustmentKind.Off, LightingAdjustmentKind.Restore })
        { d.Reset(); d.Update(client.Snapshot!, [refs[0]]); await client.ApplyLightingLevelAsync(d.Capture(kind)); }
        Assert.Equal(new RgbColor(0, 0, 20), demo.Hubs[0].LightEffects["led1"].Colors[0]); Assert.Equal(untouched, demo.Hubs[1].LightingWrites); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
}
