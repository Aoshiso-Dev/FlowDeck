using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceLightingWorkspaceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly EndpointReference A = new("a", "led1"), B = new("b", "led1");
    private static readonly string Generation = new('a', 32);
    private static readonly LightingProgram Blue = new("breathing", [new(0, 0, 200)], Brightness: 60);
    private static readonly LightingProgram Red = new("fixed", [new(200, 0, 0)]);
    private static DeviceCapability Cap(DeviceFeature feature) => new(feature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceServiceState State(long revision = 1) => new(new("epoch", 1, new[] { A, B }.Select(t => new DeviceInstance(
        new(t.DeviceId, "demo", "同じ照明", DeviceKind.Memory, IdentityConfidence.SerialBound, t.DeviceId, null, [t.DeviceId],
            [new("led1", "RGB", EndpointKind.Lighting, [Cap(DeviceFeature.FixedColor), Cap(DeviceFeature.ColorFrames), Cap(DeviceFeature.RestoreLighting)])]), 1, true)).ToArray(), 1),
        [new(A, 1, revision, Outcome.WrittenUnverified), new(B, 1, 1, Outcome.WrittenUnverified)], false, null);
    private static LightingConfiguration Configuration() => new(2, [], [], [], [], [], Preferences: [new(A, Blue), new(B, Blue)], SavedSynchronization: [new([A, B], Blue)]);
    private static DeviceLightingDraft Draft()
    { var draft = new DeviceLightingDraft(); draft.Update(State(), Generation, Configuration(), 0, true); draft.Select([A, B]); return draft; }

    [Fact] public void CommonEditorUsesSavedBaseProgramAndRetainsAnUnsentColorThroughPolling()
    {
        var draft = Draft(); Assert.True(DeviceLighting.SameProgram(Blue, draft.Program));
        draft.SetProgram(Red); var captured = draft.Capture(); draft.Update(State(), Generation, Configuration(), 0, true);
        Assert.True(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program)); Assert.True(draft.CanApply);
        Assert.Equal(2, captured.Version.Targets.Length); Assert.Equal(A, captured.Version.Targets[0].Target);
    }
    [Theory]
    [InlineData(2, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(1, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void RevisionOrConfigurationChangesPreserveDraftWithoutRebasing(long revision, string generation)
    {
        var draft = Draft(); draft.SetProgram(Red); draft.Update(State(revision), generation, Configuration(), 0, true);
        Assert.True(draft.Conflict); Assert.False(draft.CanApply); Assert.Throws<InvalidOperationException>(() => draft.Capture());
        Assert.True(DeviceLighting.SameProgram(Red, draft.Program)); draft.Reset(); Assert.True(draft.CanApply);
    }
    [Fact] public void ChangingSelectionDiscardsOldEditsInsteadOfRetargetingThem()
    {
        var draft = Draft(); draft.Select([A]); draft.SetProgram(Red);
        draft.Update(State(2), Generation, Configuration(), 0, true); draft.Select([A, B]);
        Assert.False(draft.Conflict); Assert.False(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Blue, draft.Program));
        draft.Select([B]); Assert.False(draft.Dirty); draft.SetProgram(Red);
        Assert.Equal(B, Assert.Single(draft.Capture().Version.Targets).Target);
    }
    [Fact] public void MixedSelectionRequiresExplicitProgramChoice()
    {
        var draft = Draft(); draft.Update(State(), Generation, Configuration() with { Preferences = [new(A, Blue), new(B, Red)] }, 0, true);
        Assert.True(draft.Mixed); Assert.False(draft.CanApply); draft.SetProgram(Blue);
        Assert.False(draft.Mixed); Assert.True(draft.CanApply);
    }
    [Fact] public void AnUnavailableSelectedTargetIsNeverSilentlyDropped()
    {
        var draft = Draft(); var state = State(); var instance = state.Inventory.Devices[1];
        draft.Update(state with { Inventory = state.Inventory with { Devices = [state.Inventory.Devices[0], instance with { Connected = false }] } }, Generation, Configuration(), 0, true);
        Assert.Equal(2, draft.Targets.Length); Assert.False(draft.CanApply); Assert.Contains("切断", draft.Problem);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }
    [Fact] public void UnsupportedAnimationIsNotOfferedAsACommonCapability()
    {
        var draft = Draft(); var state = State(); var instance = state.Inventory.Devices[1];
        draft.Update(state with { Inventory = state.Inventory with { Devices = [state.Inventory.Devices[0], instance with { Device = instance.Device with {
            Endpoints = [instance.Device.Endpoints[0] with { Capabilities = [Cap(DeviceFeature.FixedColor)] }] } }] } }, Generation, Configuration(), 0, true);
        Assert.False(draft.Supports("breathing")); Assert.False(draft.CanApply); draft.SetProgram(Red); Assert.True(draft.CanApply);
        Assert.False(draft.CanStop);
    }
    [Theory]
    [InlineData(3, true)]
    [InlineData(0, false)]
    public void StaleOrDisconnectedServiceCannotApplyLighting(double age, bool connected)
    {
        var draft = Draft(); draft.Update(State(), Generation, Configuration(), age, connected);
        Assert.False(draft.CanApply); Assert.False(draft.CanAdjust); Assert.False(draft.CanStop);
    }
    [Fact] public void OffAndRestoreUseServiceBaselineInsteadOfAnUnsentDraft()
    {
        var draft = Draft(); var off = draft.CaptureAdjustment(LightingAdjustmentKind.Off);
        Assert.Null(off.Program); Assert.Equal(LightingAdjustmentScope.Targets, off.Adjustment!.Scope);
        draft.SetProgram(Red); Assert.False(draft.CanAdjust);
        Assert.Throws<InvalidOperationException>(() => draft.CaptureAdjustment(LightingAdjustmentKind.Restore));
        draft.Reset(); Assert.Equal(2, draft.CaptureAdjustment(LightingAdjustmentKind.Restore).Version.Targets.Length);
    }
    [Fact] public void SceneCaptureKeepsChosenColorsAndSelectedSynchronization()
    {
        var scene = DeviceLightingSceneCapture.Create("scene", "ブルー", Configuration(), [A, B]);
        Assert.Empty(scene.Individual); Assert.True(DeviceLighting.SameProgram(Blue, Assert.Single(scene.Synchronized).Program));
        var single = DeviceLightingSceneCapture.Create("scene", "片側", Configuration(), [B]);
        Assert.Empty(single.Synchronized); Assert.Equal(B, Assert.Single(single.Individual).Target);
        Assert.Throws<InvalidOperationException>(() => DeviceLightingSceneCapture.Create("scene", "未適用", Configuration() with { Preferences = [] }, [A]));
    }
    [Fact] public async Task ServiceWorkspaceAppliesAndAdjustsBothLightsAndReportsEachTarget()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var targets = demo.DeviceIds.Select(d => new EndpointReference(d, "led1")).ToArray(); var draft = new DeviceLightingDraft();
        void Refresh() { var s = client.Snapshot!; draft.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, s.Connected); }
        Refresh(); draft.Select(targets); draft.SetProgram(Blue);
        var result = await LightingTestDiagnostics.ApplyAsync(output, client, draft.Capture()); Assert.Equal(2, result.Targets.Length);
        Assert.All(result.Targets, t => Assert.Equal(Outcome.WrittenUnverified, t.Outcome));
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        var running = client.Snapshot!.State.Endpoints.Where(e => targets.Contains(e.Target)).ToArray();
        Assert.Single(running.Select(e => e.Lighting!.GroupId).Distinct()); Assert.All(running, e => Assert.True(e.Lighting!.Running));
        draft.Reset(); Refresh(); var off = await LightingTestDiagnostics.ApplyAsync(output, client, draft.CaptureAdjustment(LightingAdjustmentKind.Off));
        Assert.Equal(2, off.Targets.Length); Refresh();
        Assert.All(client.Snapshot!.Configuration.Configuration.Lighting.Preferences!, p => Assert.Equal(0, p.Program.Brightness));
        await LightingTestDiagnostics.ApplyAsync(output, client, draft.CaptureAdjustment(LightingAdjustmentKind.Restore)); Refresh();
        Assert.All(client.Snapshot!.Configuration.Configuration.Lighting.Preferences!, p => Assert.Equal(60, p.Program.Brightness));
        Assert.True((await client.ReleaseAsync()).Success);
        Assert.All(demo.Hubs, h => Assert.Equal("off", h.LightEffects["led1"].Mode));
        Assert.All(demo.Runtime.State().Endpoints, e => Assert.False(e.Owned));
    }
    [Fact] public async Task SavingSceneIsNonActuatingAndOldPresetCannotUseNewLibraryGeneration()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var target = new EndpointReference(demo.DeviceIds[1], "led1");
        var scene = new DeviceLightingScene("one", "赤", [new(target, Red)], []);
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveScene, snapshot.Configuration.Generation, "one", 0, Scene: scene));
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
        var edit = DeviceWorkspaceClient.Preset(client.Snapshot!, LightingPresetKind.Scene, "one");
        await client.SaveLightingAsync(new(LightingLibraryOperation.SaveGroup, client.Snapshot!.Configuration.Generation, "group", 0,
            Group: new("group", "片側", EndpointKind.Lighting, [target])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyLightingAsync(edit));
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
        var result = await client.ApplyLightingAsync(DeviceWorkspaceClient.Preset(client.Snapshot!, LightingPresetKind.Scene, "one"));
        Assert.Equal(Outcome.WrittenUnverified, result.Outcome); Assert.Equal(0, demo.Hubs[0].LightingWrites); Assert.Equal(1, demo.Hubs[1].LightingWrites);
    }
    [Fact] public async Task FailedExitDoesNotReuseOldOwnerAndAnExplicitRetryCanRestoreReconnectedLight()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var s = client.Snapshot!; var draft = new DeviceLightingDraft();
        draft.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, true);
        draft.Select([new(demo.DeviceIds[0], "led1")]); draft.SetProgram(Red);
        await client.ApplyLightingAsync(draft.Capture());
        demo.Hubs[0].Disconnected = true; await demo.Runtime.PollAsync();
        Assert.False((await client.ReleaseAsync()).Success); Assert.True(client.Snapshot!.ExitAttempted); Assert.False(client.Snapshot.Connected);
        await Assert.ThrowsAsync<IOException>(() => client.RefreshAsync());
        demo.Hubs[0].Disconnected = false; await demo.Runtime.PollAsync();
        Assert.True((await client.ReleaseAsync()).Success); Assert.Equal("off", demo.Hubs[0].LightEffects["led1"].Mode);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
}
