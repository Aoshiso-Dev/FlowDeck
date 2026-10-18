using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceLightingPendingTests
{
    private static readonly EndpointReference A = new("a", "led1"), B = new("b", "led1");
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 200)], Brightness: 60);
    private static readonly LightingProgram Red = new("fixed", [new(200, 0, 0)], Brightness: 60);
    private static readonly DeviceNativeLightingEffect Native = new("native/test/wave", "Wave", true, 1, 3, 0, 255, 128, Direction: true, LedCount: 8);
    private static DeviceServiceState State() => new(new("epoch", 1, new[] { A, B }.Select(t => new DeviceInstance(
        new(t.DeviceId, "demo", t.DeviceId, DeviceKind.Memory, IdentityConfidence.SerialBound, t.DeviceId, null, [t.DeviceId],
            [new(t.EndpointId, "RGB", EndpointKind.Lighting, new[] { DeviceFeature.FixedColor, DeviceFeature.ColorFrames, DeviceFeature.NativeEffect, DeviceFeature.RestoreLighting }
                .Select(f => new DeviceCapability(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray(), NativeLightingEffects: [Native])]), 1, true)).ToArray(), 1),
        [new(A, 1, 1, Outcome.WrittenUnverified), new(B, 1, 1, Outcome.WrittenUnverified)], false, null);
    private static LightingConfiguration Library(LightingProgram? a = null, LightingProgram? b = null) =>
        new(2, [], [], [], [], [], Preferences: [new(A, a ?? Blue), new(B, b ?? Blue)]);
    private static DeviceLightingDraft Draft(LightingConfiguration? library = null)
    {
        var draft = new DeviceLightingDraft(); draft.Update(State(), new('a', 32), library ?? Library(), 0, true); draft.Select([A, B]); return draft;
    }
    private static DeviceOperationResult Result(DeviceLightingEdit edit, Outcome outcome = Outcome.WrittenUnverified, DevicePolicySaveState save = DevicePolicySaveState.Saved) =>
        new("request", "test", outcome, DateTimeOffset.UtcNow,
            edit.Version.Targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision + 1, outcome, "test", Persistence: new(save, "test"))).ToArray(), "test");
    private static DeviceServiceState ResultState(DeviceOperationResult result)
    {
        var state = State();
        return state with { Endpoints = state.Endpoints.Select(e => result.Targets.SingleOrDefault(t => t.Target == e.Target) is { } target
            ? e with { Generation = target.Generation, PolicyRevision = target.PolicyRevision, Outcome = target.Outcome,
                RequestedLighting = Red, Owned = true, Persistence = target.Persistence } : e).ToArray() };
    }

    [Fact] public void RevertingAnEditClearsPendingAndOnlyEditedFieldsAreMarked()
    {
        var draft = Draft(); draft.SetProgram(Blue); Assert.False(draft.Dirty);
        draft.SetProgram(Red); Assert.True(draft.Dirty); Assert.True(draft.ColorChanged);
        Assert.False(draft.EffectChanged); Assert.False(draft.BrightnessChanged); Assert.False(draft.SpeedChanged);
        draft.SetProgram(Red with { Brightness = 42 }); Assert.True(draft.ColorChanged); Assert.True(draft.BrightnessChanged);
        draft.SetProgram(Blue); Assert.False(draft.Dirty); Assert.False(draft.ColorChanged); Assert.False(draft.BrightnessChanged);
    }
    [Fact] public void NativePaletteDirectionSpeedAndPerLedPendingTrackIndependentFields()
    {
        var program = new LightingProgram(Native.Id, Blue.Colors, 128, Brightness: 60);
        var draft = Draft(Library(program, program));
        draft.SetProgram(program with { Speed = 150, Reverse = true });
        Assert.True(draft.SpeedChanged); Assert.True(draft.DirectionChanged); Assert.False(draft.ColorChanged);
        draft.SetProgram(program with { LedColors = Enumerable.Repeat(Blue.Colors[0], 8).ToArray() });
        Assert.True(draft.PerLedChanged); Assert.True(draft.ColorChanged); Assert.False(draft.SpeedChanged); Assert.False(draft.DirectionChanged);
        draft.SetProgram(program); Assert.False(draft.Dirty);
    }
    [Fact] public void MixedBaselineNeedsAnExplicitChoiceAndCancelRestoresMixedState()
    {
        var draft = Draft(Library(Blue, Red)); Assert.True(draft.Mixed); Assert.False(draft.Dirty); Assert.False(draft.CanApply);
        draft.SetProgram(Blue); Assert.True(draft.Dirty); Assert.True(draft.ColorChanged); Assert.False(draft.BrightnessChanged);
        draft.Reset(); Assert.True(draft.Mixed); Assert.False(draft.Dirty);
    }
    [Fact] public void TargetChangeCancelsDraftButReorderedSameTargetsKeepIt()
    {
        var draft = Draft(); draft.SetProgram(Red); draft.Select([B, A]); Assert.True(draft.Dirty);
        draft.Select([B]); Assert.False(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Blue, draft.Program));
        draft.SetProgram(Red); draft.Select([]); Assert.False(draft.Dirty);
        draft.Select([A]); Assert.False(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Blue, draft.Program));
    }
    [Fact] public void UnconfiguredTargetDoesNotInheritPreviousTargetsColor()
    {
        var draft = Draft(Library() with { Preferences = [new(A, Red)] }); draft.Select([A]);
        Assert.True(DeviceLighting.SameProgram(Red, draft.Program)); draft.Select([B]);
        Assert.False(draft.Dirty); Assert.Equal(new RgbColor(52, 137, 255), Assert.Single(draft.Program.Colors));
    }
    [Fact] public void RevertingAnEditCannotSilentlyRebaseAConflictingVersion()
    {
        var draft = Draft(); draft.SetProgram(Red);
        draft.Update(State(), new('b', 32), Library(Red, Red), 0, true); draft.SetProgram(Blue);
        Assert.True(draft.Dirty); Assert.True(draft.Conflict); Assert.False(draft.CanApply);
        draft.Reset(); Assert.False(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program));
    }
    [Theory]
    [InlineData(Outcome.Confirmed, DevicePolicySaveState.Saved)]
    [InlineData(Outcome.WrittenUnverified, DevicePolicySaveState.Saved)]
    [InlineData(Outcome.WrittenUnverified, DevicePolicySaveState.SessionOnly)]
    public void AcceptedResultsClearOnlyTheCapturedDraft(Outcome outcome, DevicePolicySaveState save)
    {
        var draft = Draft(); draft.SetProgram(Red); var edit = draft.Capture(); var result = Result(edit, outcome, save);
        draft.Update(ResultState(result), new('b', 32), save == DevicePolicySaveState.SessionOnly ? Library() : Library(Red, Red), 0, true);
        Assert.True(draft.AcceptApplied(edit, result)); Assert.False(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program));
    }
    [Theory]
    [InlineData("partial")][InlineData("unknown")][InlineData("rejected")][InlineData("missing")]
    [InlineData("duplicate")][InlineData("target-failed")][InlineData("generation")][InlineData("save-failed")][InlineData("superseded")]
    public void IncompleteOrFailedResultsKeepPendingValues(string kind)
    {
        var draft = Draft(); draft.SetProgram(Red); var edit = draft.Capture(); var result = Result(edit);
        draft.Update(ResultState(result), new('b', 32), Library(Red, Red), 0, true);
        result = kind switch {
            "partial" => result with { Outcome = Outcome.Partial }, "unknown" => result with { Outcome = Outcome.Unknown },
            "rejected" => result with { Outcome = Outcome.Rejected }, "missing" => result with { Targets = [result.Targets[0]] },
            "duplicate" => result with { Targets = [result.Targets[0], result.Targets[0]] },
            "target-failed" => result with { Targets = [result.Targets[0], result.Targets[1] with { Outcome = Outcome.Unknown }] },
            "generation" => result with { Targets = [result.Targets[0], result.Targets[1] with { Generation = 2 }] },
            "save-failed" => Result(edit, save: DevicePolicySaveState.Failed), _ => Result(edit, save: DevicePolicySaveState.Superseded) };
        Assert.False(draft.AcceptApplied(edit, result)); Assert.True(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program));
    }
    [Theory][InlineData("epoch")][InlineData("generation")][InlineData("newer-policy")][InlineData("missing")][InlineData("unrefreshed")]
    public void SuccessfulOldResultCannotClearDraftAfterTheLatestServiceStateHasMovedOn(string kind)
    {
        var draft = Draft(); draft.SetProgram(Red); var edit = draft.Capture(); var result = Result(edit); var state = ResultState(result);
        state = kind switch {
            "epoch" => state with { Inventory = state.Inventory with { Epoch = "new-epoch" } },
            "generation" => state with { Endpoints = state.Endpoints.Select(e => e with { Generation = 2 }).ToArray() },
            "newer-policy" => state with { Endpoints = state.Endpoints.Select(e => e with { PolicyRevision = e.PolicyRevision + 1, RequestedLighting = Blue }).ToArray() },
            "missing" => state with { Endpoints = [state.Endpoints[0]] }, _ => State() };
        draft.Update(state, new('b', 32), Library(), 0, true);
        Assert.False(draft.AcceptApplied(edit, result)); Assert.True(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program));
    }
    [Theory][InlineData("selection")][InlineData("cancel")][InlineData("new-edit")][InlineData("scene")]
    public void OldCompletionCannotClearAReplacementDraftEvenWhenItsValuesMatch(string kind)
    {
        var draft = Draft(); draft.SetProgram(Red); var edit = draft.Capture(); var result = Result(edit);
        draft.Update(ResultState(result), new('b', 32), Library(), 0, true);
        if (kind == "selection") { draft.Select([A]); draft.Select([A, B]); }
        else if (kind == "cancel") draft.Reset();
        else if (kind == "new-edit") draft.SetProgram(Red with { Brightness = 30 });
        draft.SetProgram(Red);
        if (kind == "scene") edit = edit with { Operation = DeviceOperation.ApplyLightingPreset };
        Assert.False(draft.AcceptApplied(edit, result)); Assert.True(draft.Dirty);
    }
    [Fact] public async Task ServiceFailureRetainsDraftAndSuccessfulRetryAdoptsReturnedProgramWithoutAdditionalWrites()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var draft = new DeviceLightingDraft();
        void Refresh() { var s = client.Snapshot!; draft.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, s.ElapsedSeconds, s.Connected); }
        Refresh(); draft.Select([new(demo.DeviceIds[0], "led1")]); draft.SetProgram(Red); var edit = draft.Capture();
        demo.Hubs[0].FailNextLightingWrite = true;
        var failed = await client.ApplyLightingAsync(edit); Refresh();
        Assert.False(draft.AcceptApplied(edit, failed)); Assert.True(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program));
        await client.ApplyLightingAsync(draft.CaptureStop()); Refresh(); draft.Reset(); draft.SetProgram(Red);
        edit = draft.Capture(); var applied = await client.ApplyLightingAsync(edit); Refresh();
        var writes = demo.Hubs.Select(h => h.LightingWrites).ToArray();
        Assert.True(draft.AcceptApplied(edit, applied)); Assert.False(draft.Dirty); Assert.True(DeviceLighting.SameProgram(Red, draft.Program));
        Refresh(); draft.Select([]); Refresh(); draft.Select([new(demo.DeviceIds[0], "led1")]);
        Assert.False(draft.Dirty); Assert.Equal(writes, demo.Hubs.Select(h => h.LightingWrites).ToArray()); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
}
