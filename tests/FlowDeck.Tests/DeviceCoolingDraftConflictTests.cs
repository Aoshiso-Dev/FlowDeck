using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCoolingDraftConflictTests
{
    private static readonly EndpointReference Target = new("hub-a", "fan1");
    private static readonly EndpointReference Sensor = new("cpu", "temperature");
    private static DeviceCapability Cap(DeviceFeature feature) =>
        new(feature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceServiceState State(string epoch = "epoch", long topology = 1, long generation = 1, long revision = 0) =>
        new(new(epoch, 1, [new(new("hub-a", "demo", "Hub", DeviceKind.Controller, IdentityConfidence.SerialBound, "a", null, ["a"],
            [new("fan1", "FAN1", EndpointKind.Cooling, [Cap(DeviceFeature.DutyRead), Cap(DeviceFeature.FixedDuty), Cap(DeviceFeature.AutomaticDuty), Cap(DeviceFeature.ReleaseCooling)])]), generation, true)], topology),
            [new(Target, generation, revision, Outcome.Confirmed)], false, null,
            [new(Target, DeviceFeature.DutyRead, 45, 0, false)]);
    private static CoolingEndpointConfiguration Setup() => new(Target, true,
        new(30, 100, 70, CoolingExitAction.FixedDuty, [new(Sensor, 95, 85)]),
        [new("balanced", "標準", 1, [new(Sensor, [new(30, 30), new(90, 100)], 95, 85)], 3, 2)]);
    private static DeviceBindings Bindings() => new(2, "11111111111111111111111111111111",
        [new("hub-a", "demo:a", "hub", new('a', 64)), new("cpu", "demo:cpu", "temperature", new('b', 64))]);
    private static DeviceCoolingDraft Edited(CoolingEndpointConfiguration? setup = null, DeviceBindings? bindings = null)
    {
        var draft = new DeviceCoolingDraft(Target);
        draft.Update(State(), "initial", setup ?? Setup(), 0, true, bindings ?? Bindings());
        draft.SetDuty(58);
        Assert.True(draft.CanApply);
        return draft;
    }
    private static void Refresh(DeviceCoolingDraft draft, DeviceWorkspaceSnapshot snapshot) =>
        draft.Update(snapshot.State, snapshot.Configuration.Generation,
            snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == draft.Target),
            snapshot.ElapsedSeconds, snapshot.Connected, snapshot.Configuration.Configuration.Bindings);
    private static void AssertBlocked(DeviceCoolingDraft draft)
    {
        Assert.True(draft.Dirty);
        Assert.True(draft.Conflict);
        Assert.Equal(58, draft.Duty);
        Assert.False(draft.CanApply);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PersistingOneFanLeavesAnotherEditedFanApplicableWithoutAcceptingAnOldCapturedCommand(int secondHub)
    {
        using var directory = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient();
        await client.ConnectAsync();
        var initial = client.Snapshot!;
        var first = new DeviceCoolingDraft(new(demo.DeviceIds[0], "fan1"));
        var second = new DeviceCoolingDraft(new(demo.DeviceIds[secondHub], "fan3"));
        var withoutContext = new DeviceCoolingDraft(second.Target);
        Refresh(first, initial);
        Refresh(second, initial);
        withoutContext.Update(initial.State, initial.Configuration.Generation,
            initial.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == second.Target),
            initial.ElapsedSeconds, initial.Connected);
        first.SetDuty(56);
        second.SetDuty(63);
        withoutContext.SetDuty(63);
        var staleSecond = second.Capture();

        var firstResult = await client.ApplyCoolingAsync(first.Capture());
        Assert.Equal(Outcome.Confirmed, firstResult.Outcome);
        Assert.Equal(first.Target, Assert.Single(firstResult.Targets).Target);
        Assert.Equal(1, demo.Hubs[0].Writes);
        Assert.Equal(0, demo.Hubs[1].Writes);
        var afterFirst = client.Snapshot!;
        Assert.NotEqual(initial.Configuration.Generation, afterFirst.Configuration.Generation);
        Refresh(second, afterFirst);
        withoutContext.Update(afterFirst.State, afterFirst.Configuration.Generation,
            afterFirst.Configuration.Configuration.Cooling.Endpoints.Single(e => e.Target == second.Target),
            afterFirst.ElapsedSeconds, afterFirst.Connected);
        Assert.Equal(63, withoutContext.Duty);
        Assert.True(withoutContext.Conflict);
        Assert.False(withoutContext.CanApply);
        Assert.Equal(63, second.Duty);
        Assert.True(second.Dirty);
        Assert.False(second.Conflict);
        Assert.True(second.CanApply);
        Assert.Equal(afterFirst.Configuration.Generation, second.Capture().Version.ConfigurationGeneration);
        Assert.Equal(staleSecond.Version.Target, second.Capture().Version.Target);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyCoolingAsync(staleSecond));
        Assert.Equal(1, demo.Hubs.Sum(h => h.Writes));
        var secondResult = await client.ApplyCoolingAsync(second.Capture());
        Assert.Equal(Outcome.Confirmed, secondResult.Outcome);
        Assert.Equal(second.Target, Assert.Single(secondResult.Targets).Target);
        Assert.Equal(secondHub == 0 ? 2 : 1, demo.Hubs[0].Writes);
        Assert.Equal(secondHub == 1 ? 1 : 0, demo.Hubs[1].Writes);
        for (var hub = 0; hub < demo.Hubs.Length; hub++)
        {
            var status = await demo.Hubs[hub].ReadAsync();
            foreach (var channel in status.Channels)
            {
                var target = new EndpointReference(demo.DeviceIds[hub], channel.Id);
                Assert.Equal(target == first.Target ? 56 : target == second.Target ? 63 : 45, channel.Duty);
            }
        }
        var saved = client.Snapshot!.Configuration.Configuration.Cooling.Endpoints;
        Assert.Equal(56, saved.Single(e => e.Target == first.Target).SavedPolicy!.Duty);
        Assert.Equal(63, saved.Single(e => e.Target == second.Target).SavedPolicy!.Duty);
    }

    [Fact]
    public void EqualCopiedContextAllowsRepeatedGenerationChangesWithoutReplacingUnsentChoices()
    {
        var draft = Edited();
        var original = draft.Capture();
        foreach (var generation in new[] { "other-fan", "lighting", "dashboard" })
        {
            draft.Update(State(), generation, Setup(), 0, true, Bindings());
            Assert.Equal(58, draft.Duty);
            Assert.True(draft.Dirty);
            Assert.False(draft.DeviceManaged);
            Assert.False(draft.Conflict);
            Assert.True(draft.CanApply);
            var edit = draft.Capture();
            Assert.Equal(generation, edit.Version.ConfigurationGeneration);
            Assert.Equal(original.Version.Target, edit.Version.Target);
            Assert.Equal(original.Version.Epoch, edit.Version.Epoch);
            Assert.Equal(original.Version.Topology, edit.Version.Topology);
        }
        Assert.Equal("initial", original.Version.ConfigurationGeneration);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GenerationChangeWithoutBothBindingContextsRemainsBlocked(bool initialContext, bool nextContext)
    {
        var draft = new DeviceCoolingDraft(Target);
        draft.Update(State(), "initial", Setup(), 0, true, initialContext ? Bindings() : null);
        draft.SetDuty(58);
        draft.Update(State(), "unrelated", Setup(), 0, true, nextContext ? Bindings() : null);
        AssertBlocked(draft);
        draft.Update(State(), "unrelated", Setup(), 0, true, Bindings());
        AssertBlocked(draft);
    }

    [Theory]
    [InlineData("wiring", false)]
    [InlineData("wiring", true)]
    [InlineData("safety", false)]
    [InlineData("safety", true)]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    [InlineData("saved-policy", false)]
    [InlineData("saved-policy", true)]
    [InlineData("saved-safety", false)]
    [InlineData("saved-safety", true)]
    [InlineData("startup", false)]
    [InlineData("startup", true)]
    [InlineData("removed", false)]
    [InlineData("removed", true)]
    public void AnyTargetConfigurationChangeBlocksEvenIfItsGenerationDidNotChange(string change, bool changedGeneration)
    {
        var setup = Setup();
        setup = setup with { SavedPolicy = new(FanMode.Manual, 45, SafetySnapshot: setup.Safety) };
        var draft = Edited(setup);
        CoolingEndpointConfiguration? next = change switch
        {
            "wiring" => setup with { WiringConfirmed = false },
            "safety" => setup with { Safety = setup.Safety with { Minimum = 35 } },
            "profile" => setup with { Profiles = [setup.Profiles[0] with { Name = "変更したカーブ" }] },
            "saved-policy" => setup with { SavedPolicy = setup.SavedPolicy! with { Duty = 50 } },
            "saved-safety" => setup with { SavedPolicy = setup.SavedPolicy! with { SafetySnapshot = setup.Safety with { Exit = 80 } } },
            "startup" => setup with { ResumeOnStart = true },
            "removed" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        draft.Update(State(), changedGeneration ? "next" : "initial", next, 0, true, Bindings());
        AssertBlocked(draft);
        // Even restoration of the original data cannot approve an already conflicted edit.
        draft.Update(State(), "initial", setup, 0, true, Bindings());
        AssertBlocked(draft);
        draft.Reset();
        Assert.False(draft.Conflict);
        Assert.Equal(45, draft.Duty);
        draft.SetDuty(58);
        Assert.True(draft.CanApply);
    }

    [Theory]
    [InlineData("installation", false)]
    [InlineData("installation", true)]
    [InlineData("target", false)]
    [InlineData("target", true)]
    [InlineData("sensor", false)]
    [InlineData("sensor", true)]
    [InlineData("definition", false)]
    [InlineData("definition", true)]
    [InlineData("version", false)]
    [InlineData("version", true)]
    public void BindingChangesBlockEvenWhenTheTargetVersionAndGenerationAreUnchanged(string change, bool changedGeneration)
    {
        var draft = Edited();
        var bindings = Bindings();
        var next = change switch
        {
            "installation" => bindings with { InstallationId = "22222222222222222222222222222222" },
            "target" => bindings with { Devices = [bindings.Devices[0] with { ProviderIdentity = "demo:replacement" }, bindings.Devices[1]] },
            "sensor" => bindings with { Devices = [bindings.Devices[0], bindings.Devices[1] with { Fingerprint = new('c', 64) }] },
            "definition" => bindings with { Devices = [bindings.Devices[0] with { DefinitionId = "replacement" }, bindings.Devices[1]] },
            "version" => bindings with { Version = 3 },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        draft.Update(State(), changedGeneration ? "next" : "initial", Setup(), 0, true, next);
        AssertBlocked(draft);
        draft.Update(State(), "initial", Setup(), 0, true, Bindings());
        AssertBlocked(draft);
        draft.Reset();
        draft.SetDuty(58);
        Assert.True(draft.CanApply);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("topology")]
    [InlineData("device-generation")]
    [InlineData("policy-revision")]
    public void RuntimeVersionChangesRemainConflictedAfterLaterPollingRestoresTheOriginalSnapshot(string change)
    {
        var draft = Edited();
        var next = change switch
        {
            "epoch" => State(epoch: "replacement"),
            "topology" => State(topology: 2),
            "device-generation" => State(generation: 2),
            "policy-revision" => State(revision: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        draft.Update(next, "next", Setup(), 0, true, Bindings());
        AssertBlocked(draft);
        draft.Update(State(), "initial", Setup(), 0, true, Bindings());
        AssertBlocked(draft);
        draft.Update(State(), "unrelated", Setup(), 0, true, Bindings());
        AssertBlocked(draft);
        draft.Reset();
        draft.SetDuty(58);
        Assert.True(draft.CanApply);
        Assert.Equal("unrelated", draft.Capture().Version.ConfigurationGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutatingCallerOwnedArraysCannotRewriteTheOriginalConflictContext(bool mutateBindings)
    {
        var setup = Setup();
        var bindings = Bindings();
        var draft = Edited(setup, bindings);
        if (mutateBindings)
            bindings.Devices[1] = bindings.Devices[1] with { Fingerprint = new('d', 64) };
        else
            setup.Profiles[0].Curves[0].Points[0] = new(30, 40);
        draft.Update(State(), "next", setup, 0, true, bindings);
        AssertBlocked(draft);
    }
}
