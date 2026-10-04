using FlowDeck.Core;
using HubRig = FlowDeck.Tests.DeviceLightingAdjustmentTests.HubRig;

namespace FlowDeck.Tests;

public sealed partial class DeviceLightingCompositionTests
{
    [Fact]
    public async Task ACommonEffectSelectionAlignsOnlyTheSelectedClocksEvenWhenOneAlreadyHasThatEffect()
    {
        await using var r = await HubRig.Create(); var cycle = new LightingProgram("color-cycle", [Blue], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2", "led3") with { Lighting = cycle });
        r.Clock.Advance(.4); await r.Runtime.TickLightingAsync();
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = new("breathing", [Red], 4) });
        r.Clock.Advance(.2); await r.Runtime.TickLightingAsync();
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2") with { Lighting = cycle });
        Assert.Equal(DeviceLighting.Frame(cycle, 0), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(r.Hub.Colors["led1"].Colors[0], r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(DeviceLighting.Frame(cycle, .6), r.Hub.Colors["led3"].Colors[0]);
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(cycle, .3), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle, .3), r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(DeviceLighting.Frame(cycle, .9), r.Hub.Colors["led3"].Colors[0]);
    }
    [Fact]
    public async Task SelectingOneRunningHubPortChangesOnlyItsEffectClockAndKeepsAllRecoveryOwners()
    {
        await using var r = await HubRig.Create(); var cycle = new LightingProgram("color-cycle", [Red], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2", "led3") with { Lighting = cycle });
        r.Clock.Advance(.7); await r.Runtime.TickLightingAsync(); var owners = r.Journal.Pending;
        var group = r.Runtime.State().Endpoints.Single(e => e.Target == Target(r, "led1")).Lighting!.GroupId;
        var pulse = new LightingProgram("breathing", [Blue], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = pulse });
        Assert.Equal(DeviceLighting.Frame(pulse, 0), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle, .7), r.Hub.Colors["led2"].Colors[0]);
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(pulse, .3), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle, 1), r.Hub.Colors["led2"].Colors[0]);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = pulse with { Brightness = 40 } });
        Assert.Equal(DeviceLighting.Frame(pulse with { Brightness = 40 }, .3), r.Hub.Colors["led1"].Colors[0]);
        Assert.Equal(owners, r.Journal.Pending); Assert.Equal(0, r.Hub.Fans.Writes);
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Lighting?.Running == true), e => Assert.Equal(group, e.Lighting!.GroupId));
        Assert.True(DeviceLighting.SameProgram(cycle, r.Authority.Configuration.Lighting.Preferences!.Single(p => p.Target == Target(r, "led2")).Program));
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, "led1", "led2", "led3"));
        Assert.Equal(Blue.Scale(.4), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(Red, r.Hub.Colors["led2"].Colors[0]); Assert.Empty(r.Journal.Pending);
    }
    [Fact]
    public async Task FixedOffAndBackToAnAnimationCanBeSelectedWithoutStoppingUnselectedPorts()
    {
        await using var r = await HubRig.Create(); var pulse = new LightingProgram("breathing", [Blue], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2", "led3") with { Lighting = pulse });
        r.Clock.Advance(.4); await r.Runtime.TickLightingAsync(); var owner = r.Journal.Pending;
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = new("fixed", [Red]) });
        Assert.Equal(Red, r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(pulse, .4), r.Hub.Colors["led2"].Colors[0]);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = new("off", []) });
        r.Clock.Advance(.2); await r.Runtime.TickLightingAsync();
        Assert.Equal(new RgbColor(0, 0, 0), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(pulse, .6), r.Hub.Colors["led2"].Colors[0]);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = pulse });
        Assert.Equal(DeviceLighting.Frame(pulse, 0), r.Hub.Colors["led1"].Colors[0]);
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Off, LightingAdjustmentScope.All, null, "led1", "led2", "led3"));
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        await r.Apply(r.Adjustment(LightingAdjustmentKind.Restore, LightingAdjustmentScope.All, null, "led1", "led2", "led3"));
        Assert.Equal(DeviceLighting.Frame(pulse, .3), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(pulse, .9), r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(owner, r.Journal.Pending);
    }
    [Fact]
    public async Task ApplyingASceneToSomeRunningPortsKeepsTheOtherPortsClockAndDoesNotReopen()
    {
        await using var r = await HubRig.Create(); var cycle = new LightingProgram("color-cycle", [Blue], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2", "led3") with { Lighting = cycle });
        r.Clock.Advance(.4); await r.Runtime.TickLightingAsync(); var owner = r.Journal.Pending;
        var pulse = new LightingProgram("breathing", [Red], 3);
        await r.Apply(await Scene(r, new("fixed", [Blue]), pulse));
        Assert.Equal(Blue, r.Hub.Colors["led1"].Colors[0]); Assert.Equal(DeviceLighting.Frame(pulse, 0), r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(DeviceLighting.Frame(cycle, .4), r.Hub.Colors["led3"].Colors[0]);
        r.Clock.Advance(.5); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(pulse, .5), r.Hub.Colors["led2"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle, .9), r.Hub.Colors["led3"].Colors[0]);
        Assert.Equal(owner, r.Journal.Pending); Assert.Equal(3, Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length);
    }
    [Fact]
    public async Task AFailureInAnUnselectedPortRestoresTheLiveCompositionAndDoesNotSaveTheNewEffect()
    {
        await using var r = await HubRig.Create(); var cycle = new LightingProgram("color-cycle", [Blue]);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2") with { Lighting = cycle });
        r.Hub.FailChannel = "led2";
        var reply = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, "led1") with { Lighting = new("fixed", [Red]) });
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        Assert.True(DeviceLighting.SameProgram(cycle, r.Authority.Configuration.Lighting.Preferences!.Single(p => p.Target == Target(r, "led1")).Program));
        Assert.NotEqual(Red, r.Hub.Colors["led1"].Colors[0]); Assert.All(r.Runtime.State().Endpoints.Where(e => e.Lighting is not null), e => Assert.False(e.Lighting!.Running));
        r.Hub.FailChannel = null;
    }
}

public partial class DeviceLightingRuntimeTests
{
    [Fact]
    public async Task ARejectedReplacementCannotRollbackToAnOlderUnsentSliderValue()
    {
        var a = new Driver("a"); var fan = new GenericTestDriver("fan"); await using var r = new Rig(a, fan).Open(); await r.Start(["a"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = r.Tick(.5); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4);
        var old = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program with { Brightness = 25 }));
        var replacement = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program with { Brightness = 75 }));
        fan.Connected = false; r.Runtime.Refresh(); a.Continue.TrySetResult(); await frame;
        Assert.Equal(Outcome.Rejected, (await old.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        Assert.Equal(Outcome.Rejected, (await replacement.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        await r.Tick(.1); Assert.Equal(DeviceLighting.Frame(program, .6), a.Color);
        Assert.Equal(100, r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "a").RequestedLighting!.Brightness);
        Assert.Equal(1, a.Opens); Assert.Equal(0, a.Ends);
    }
    [Fact]
    public async Task AnUnsentEditOnAnotherMemberCannotBeCarriedIntoAnUnrelatedAcceptedRequest()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = r.Tick(.5); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4);
        var pending = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program with { Brightness = 25 }));
        var other = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["b"], program with { Brightness = 75 }));
        Assert.False(other.Success); Assert.Contains("完了", other.Error);
        a.Continue.TrySetResult(); await frame; Assert.Equal(Outcome.WrittenUnverified, (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        await r.Tick(.1); Assert.Equal(DeviceLighting.Frame(program with { Brightness = 25 }, .6), a.Color); Assert.Equal(DeviceLighting.Frame(program, .6), b.Color);
        var retried = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["b"], program with { Brightness = 75 }));
        Assert.Equal(Outcome.WrittenUnverified, retried.Result!.Outcome); Assert.Equal(DeviceLighting.Frame(program with { Brightness = 75 }, .6), b.Color);
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 25 }, .6), a.Color);
    }
}
