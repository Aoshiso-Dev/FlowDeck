using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingPendingTests
{
    private static readonly EndpointReference Target = new("hub-a", "fan1");
    private static readonly EndpointReference Sensor = new("cpu", "temperature");
    private static DeviceCapability Cap(DeviceFeature feature) =>
        new(feature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static CoolingEndpointConfiguration Setup() => new(Target, true,
        new(30, 100, 70, CoolingExitAction.FixedDuty, [new(Sensor, 95, 85)]),
        [new("balanced", "標準", 1, [new(Sensor, [new(30, 30), new(90, 100)], 95, 85)], 3, 2),
         new("quiet", "静音", 1, [new(Sensor, [new(30, 30), new(90, 90)], 95, 85)], 3, 2)]);
    private static DeviceServiceState State(bool owned = true, FanMode mode = FanMode.Manual, int duty = 45, long revision = 0) =>
        new(new("epoch", 1, [new(new("hub-a", "demo", "Hub", DeviceKind.Controller, IdentityConfidence.SerialBound, "a", null, ["a"],
            [new("fan1", "FAN1", EndpointKind.Cooling, [Cap(DeviceFeature.DutyRead), Cap(DeviceFeature.FixedDuty), Cap(DeviceFeature.AutomaticDuty), Cap(DeviceFeature.ReleaseCooling)])]), 1, true)], 1),
            [new(Target, 1, revision, Outcome.Confirmed, RequestedDuty: duty, Owned: owned,
                Cooling: owned ? new(mode, duty, false, null, false, false, mode == FanMode.Auto ? "balanced" : null) : null)], false, null,
            [new(Target, DeviceFeature.DutyRead, duty, 0, false)]);
    private static DeviceCoolingDraft Draft(bool owned = true, FanMode mode = FanMode.Manual)
    {
        var draft = new DeviceCoolingDraft(Target);
        draft.Update(State(owned, mode), "config", Setup(), 0, true);
        return draft;
    }

    [Theory]
    [InlineData(false, FanMode.Manual)]
    [InlineData(true, FanMode.Manual)]
    [InlineData(true, FanMode.Auto)]
    public void ACurrentPolicyNeverOffersAnUnchangedApply(bool owned, FanMode mode)
    {
        var draft = Draft(owned, mode);
        Assert.False(draft.Dirty);
        Assert.False(draft.CanApply);
        Assert.False(draft.ModeChanged);
        Assert.False(draft.DutyChanged);
        Assert.False(draft.ProfileChanged);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        // A distinct, explicitly requested release keeps its existing availability rules.
        Assert.Equal(DeviceOperation.ReleaseCooling, draft.Capture(DeviceOperation.ReleaseCooling).Operation);
    }

    [Fact]
    public void FixedDutyReturningToTheCurrentValueClearsOnlyTheLocalEdit()
    {
        var draft = Draft();
        draft.SetDuty(64);
        Assert.True(draft.Dirty && draft.DutyChanged && draft.CanApply);
        Assert.False(draft.ModeChanged || draft.ProfileChanged);
        draft.Update(State(), "config", Setup(), 0, true);
        Assert.Equal(64, draft.Duty);
        draft.SetDuty(45);
        Assert.False(draft.Dirty || draft.DutyChanged || draft.CanApply);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }

    [Fact]
    public void SelectingFixedAtTheSameReadingChangesManagementWithoutInventingADutyEdit()
    {
        var draft = Draft(owned: false);
        draft.SetAutomatic(false);
        Assert.True(draft.Dirty && draft.ModeChanged && draft.CanApply);
        Assert.False(draft.DutyChanged || draft.ProfileChanged);
        Assert.Equal(45, draft.Capture().Duty);
        draft.SelectDeviceManagement();
        Assert.False(draft.Dirty || draft.ModeChanged || draft.CanApply);
    }

    [Fact]
    public void InactiveInputsDoNotMakeAnUnchangedPolicyApplicable()
    {
        var draft = Draft();
        draft.SelectProfile("quiet");
        Assert.False(draft.Dirty || draft.CanApply || draft.ProfileChanged);
        draft.SetAutomatic(true);
        Assert.True(draft.ModeChanged && draft.CanApply);
        draft.SelectProfile("quiet");
        Assert.True(draft.ProfileChanged);
        draft.SetAutomatic(false);
        Assert.False(draft.Dirty || draft.CanApply || draft.ProfileChanged);
        Assert.Equal("balanced", draft.ProfileId);
    }

    [Fact]
    public void AutomaticProfileChangesCanBeCancelledWithoutSendingAnOutput()
    {
        var draft = Draft(mode: FanMode.Auto);
        draft.SelectProfile("quiet");
        Assert.True(draft.Dirty && draft.ProfileChanged && draft.CanApply);
        Assert.False(draft.ModeChanged || draft.DutyChanged);
        draft.SelectProfile("balanced");
        Assert.False(draft.Dirty || draft.ProfileChanged || draft.CanApply);
        draft.SetAutomatic(false);
        Assert.True(draft.ModeChanged && draft.CanApply);
        draft.SetAutomatic(true);
        Assert.False(draft.Dirty || draft.CanApply);
    }

    [Fact]
    public void ReturningValuesAndPollingCannotApproveAnAlreadyConflictedEdit()
    {
        var draft = Draft();
        draft.SetDuty(64);
        draft.Update(State(revision: 1), "config", Setup(), 0, true);
        Assert.True(draft.Conflict);
        draft.SetDuty(45);
        Assert.False(draft.Dirty);
        Assert.True(draft.Conflict);
        draft.Update(State(revision: 1), "config", Setup(), 0, true);
        draft.SetDuty(64);
        Assert.True(draft.Dirty && draft.Conflict);
        Assert.False(draft.CanApply);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        draft.Reset();
        Assert.False(draft.Dirty || draft.Conflict || draft.CanApply);
        draft.SetDuty(64);
        Assert.True(draft.CanApply);
        Assert.Equal(1, draft.Capture().Version.Target.ExpectedPolicyRevision);
    }

    [Fact]
    public void AConfirmedSnapshotAndResetClearAllPendingFields()
    {
        var draft = Draft(owned: false);
        draft.SetAutomatic(true);
        draft.SelectProfile("quiet");
        var confirmed = State(mode: FanMode.Auto, revision: 1);
        confirmed = confirmed with { Endpoints = [confirmed.Endpoints[0] with { Cooling = confirmed.Endpoints[0].Cooling! with { ProfileId = "quiet" } }] };
        draft.Update(confirmed, "saved", Setup(), 0, true);
        draft.Reset();
        Assert.True(draft.Automatic);
        Assert.Equal("quiet", draft.ProfileId);
        Assert.False(draft.Dirty || draft.ModeChanged || draft.DutyChanged || draft.ProfileChanged || draft.Conflict || draft.CanApply);
    }

    private static DeviceOperationResult Result(DeviceCoolingEdit edit) => new("request", "test", Outcome.Confirmed, DateTimeOffset.UtcNow,
        [new(edit.Version.Target.Target, edit.Version.Target.Generation, edit.Version.Target.ExpectedPolicyRevision + 1,
            Outcome.Confirmed, "確認", Persistence: new(DevicePolicySaveState.Saved, "保存済み"))], "確認");

    [Theory]
    [InlineData("overall")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("target")]
    [InlineData("generation")]
    [InlineData("target-result")]
    [InlineData("save-failed")]
    [InlineData("save-superseded")]
    [InlineData("session-only")]
    public void AFailureOrResultForAnotherEndpointNeverClearsTheEdit(string reason)
    {
        var draft = Draft(); draft.SetDuty(64);
        var edit = draft.Capture(); var revision = draft.EditRevision; var result = Result(edit); var target = result.Targets[0];
        draft.Update(State(duty: 64, revision: 1), "saved", Setup(), 0, true);
        result = reason switch
        {
            "overall" => result with { Outcome = Outcome.Partial },
            "missing" => result with { Targets = [] },
            "extra" => result with { Targets = [target, target] },
            "target" => result with { Targets = [target with { Target = new("hub-b", "fan1") }] },
            "generation" => result with { Targets = [target with { Generation = 2 }] },
            "target-result" => result with { Targets = [target with { Outcome = Outcome.Rejected }] },
            "save-failed" => result with { Targets = [target with { Persistence = new(DevicePolicySaveState.Failed, "保存失敗") }] },
            "save-superseded" => result with { Targets = [target with { Persistence = new(DevicePolicySaveState.Superseded, "別の設定") }] },
            "session-only" => result with { Targets = [target with { Persistence = new(DevicePolicySaveState.SessionOnly, "保存なし") }] },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        Assert.False(draft.Complete(edit, revision, result));
        Assert.True(draft.Dirty && draft.DutyChanged);
        Assert.Equal(64, draft.Duty);
    }

    [Fact]
    public void AConfirmedCompletionCannotClearNewerEditsOrCancellation()
    {
        var draft = Draft(); draft.SetDuty(64);
        var edit = draft.Capture(); var revision = draft.EditRevision;
        draft.SetDuty(71);
        Assert.False(draft.Complete(edit, revision, Result(edit)));
        Assert.Equal(71, draft.Duty); Assert.True(draft.Dirty);
        draft.Reset();
        Assert.False(draft.Complete(edit, revision, Result(edit)));
        Assert.False(draft.Dirty);
    }

    [Fact]
    public void OnlyAConfirmedMatchingCompletionClearsThePendingPolicy()
    {
        var draft = Draft(); draft.SetDuty(64);
        var edit = draft.Capture(); var revision = draft.EditRevision;
        draft.Update(State(duty: 64, revision: 1), "saved", Setup(), 0, true);
        Assert.True(draft.Complete(edit, revision, Result(edit)));
        Assert.Equal(64, draft.Duty);
        Assert.False(draft.Dirty || draft.CanApply || draft.Conflict);
    }

    [Theory]
    [InlineData("epoch")][InlineData("topology")][InlineData("generation")][InlineData("policy")][InlineData("before-result")]
    public void CompletionCannotClearAnEditAfterTheLatestObservationMovedPastItsResult(string change)
    {
        var draft = Draft(); draft.SetDuty(64);
        var edit = draft.Capture(); var revision = draft.EditRevision; var result = Result(edit);
        var state = State(duty: 64, revision: 1);
        state = change switch
        {
            "epoch" => state with { Inventory = state.Inventory with { Epoch = "replacement" } },
            "topology" => state with { Inventory = state.Inventory with { TopologyRevision = 2 } },
            "generation" => state with { Inventory = state.Inventory with { Devices = [state.Inventory.Devices[0] with { Generation = 2 }] },
                Endpoints = [state.Endpoints[0] with { Generation = 2 }] },
            "policy" => State(duty: 71, revision: 2),
            "before-result" => State(),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        draft.Update(state, "saved", Setup(), 0, true);
        Assert.False(draft.Complete(edit, revision, result));
        Assert.True(draft.Dirty); Assert.Equal(64, draft.Duty);
        Assert.False(draft.CanApply);
    }

    [Fact]
    public async Task FailedDemoApplicationRetainsOnlyTheEditedEndpointsDraft()
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient();
        await client.ConnectAsync();
        var snapshot = client.Snapshot!;
        // The existing DEMO mismatch hook affects fan3; other channels deliberately still confirm.
        var drafts = new[] { new DeviceCoolingDraft(new(demo.DeviceIds[0], "fan3")), new DeviceCoolingDraft(new(demo.DeviceIds[1], "fan1")) };
        void Update(DeviceCoolingDraft draft, DeviceWorkspaceSnapshot current) => draft.Update(current.State, current.Configuration.Generation,
            current.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == draft.Target), current.ElapsedSeconds, current.Connected,
            current.Configuration.Configuration.Bindings);
        foreach (var draft in drafts) Update(draft, snapshot);
        drafts[0].SetDuty(64);
        drafts[1].SetDuty(71);
        demo.Hubs[0].FailNextFanWrite = true;
        var result = await client.ApplyCoolingAsync(drafts[0].Capture());
        Assert.NotEqual(Outcome.Confirmed, result.Outcome);
        foreach (var draft in drafts) Update(draft, client.Snapshot!);
        Assert.True(drafts[0].Dirty);
        Assert.Equal(64, drafts[0].Duty);
        Assert.True(drafts[1].Dirty);
        Assert.Equal(71, drafts[1].Duty);
        drafts[0].Reset();
        Assert.False(drafts[0].Dirty || drafts[0].CanApply);
        Assert.True(drafts[1].Dirty);
        Assert.Equal(71, drafts[1].Duty);
        Assert.Equal(0, demo.Hubs[1].Writes);
    }
}
