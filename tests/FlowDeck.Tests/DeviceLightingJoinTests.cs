using FlowDeck.Core;
using HubRig = FlowDeck.Tests.DeviceLightingAdjustmentTests.HubRig;

namespace FlowDeck.Tests;

public sealed partial class DeviceLightingCompositionTests
{
    [Fact]
    public async Task AddingAHubPortRetainsExistingPortRecoveryRecordsAndUnselectedPhase()
    {
        await using var r = await HubRig.Create(); var cycle = new LightingProgram("color-cycle", [Blue], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led3") with { Lighting = cycle });
        r.Clock.Advance(.4); await r.Runtime.TickLightingAsync(); var old = r.Journal.Pending.ToDictionary(p => p.Channel);
        var pulse = new LightingProgram("breathing", [Red], 4);
        await r.Apply(r.Request(DeviceOperation.SetLighting, "led1", "led2") with { Lighting = pulse });
        var pending = r.Journal.Pending.ToDictionary(p => p.Channel); Assert.Equal(old["led1"], pending["led1"]); Assert.Equal(old["led3"], pending["led3"]); Assert.Equal(3, pending.Count);
        Assert.Equal(DeviceLighting.Frame(pulse, 0), r.Hub.Colors["led1"].Colors[0]); Assert.Equal(r.Hub.Colors["led1"].Colors[0], r.Hub.Colors["led2"].Colors[0]);
        Assert.Equal(DeviceLighting.Frame(cycle, .4), r.Hub.Colors["led3"].Colors[0]);
        r.Clock.Advance(.3); await r.Runtime.TickLightingAsync();
        Assert.Equal(DeviceLighting.Frame(pulse, .3), r.Hub.Colors["led2"].Colors[0]); Assert.Equal(DeviceLighting.Frame(cycle, .7), r.Hub.Colors["led3"].Colors[0]);
        Assert.Equal(3, Assert.Single(r.Authority.Configuration.Lighting.SavedFrameGroups!).Length); Assert.Equal(0, r.Hub.Fans.Writes);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, "led1", "led2", "led3"));
        Assert.Empty(r.Journal.Pending); Assert.Equal(Red, r.Hub.Colors["led1"].Colors[0]); Assert.Equal(Red, r.Hub.Colors["led2"].Colors[0]); Assert.Equal(Blue, r.Hub.Colors["led3"].Colors[0]);
    }
}

public partial class DeviceLightingRuntimeTests
{
    private static LightingProgram JoinProgram(int brightness = 100) => new("color-cycle", [new(100, 50, 20)], 4, Brightness: brightness);
    private sealed class OpeningDriver(Driver source) : IDeviceLightingDriver, IPhysicalDeviceDriver
    {
        public bool FailOpen, FailCleanup;
        public TaskCompletionSource? Entered, Continue;
        public string DeviceId => source.DeviceId;
        public PhysicalDeviceBinding BindingCandidate => source.BindingCandidate;
        public DeviceLightingLimits? LightingLimits => source.LightingLimits;
        public DeviceObservation? Observe() => source.Observe();
        public string? Validate(DeviceEndpointAction action) => source.Validate(action);
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => source.ValidateLightingStream(endpoints, program);
        public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
        {
            Entered?.TrySetResult(); if (Continue is not null) await Continue.Task.WaitAsync(ct);
            if (FailOpen) throw new IOException("open failed");
            return await source.OpenLightingStreamAsync(endpoints, program, ct);
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) =>
            action.Operation == DeviceOperation.StopLighting && FailCleanup ? Task.FromResult(new DeviceDriverResult(Outcome.Unknown, "cleanup not confirmed")) : source.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => source.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
    [Fact]
    public async Task JoiningTwoExistingCompositionsKeepsAllStreamsAndUnselectedClocks()
    {
        var a = new Driver("a"); var x = new Driver("x"); var b = new Driver("b"); var y = new Driver("y");
        await using var r = new Rig(a, x, b, y).Open(); await r.Start(["a", "x"]); await r.Tick(.4); await r.Start(["b", "y"]); await r.Tick(.3);
        var joined = await r.Start(["a", "b"], brightness: 80); Assert.Equal(Outcome.WrittenUnverified, joined.Result!.Outcome);
        Assert.Equal(DeviceLighting.Frame(JoinProgram(80), 0), a.Color); Assert.Equal(a.Color, b.Color);
        Assert.Equal(DeviceLighting.Frame(JoinProgram(), .7), x.Color); Assert.Equal(DeviceLighting.Frame(JoinProgram(), .3), y.Color);
        Assert.Single(r.Runtime.State().Endpoints.Select(e => e.Lighting!.GroupId).Distinct());
        Assert.All(new[] { a, b, x, y }, d => { Assert.Equal(1, d.Opens); Assert.Equal(0, d.Ends); });
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(JoinProgram(80), .2), a.Color); Assert.Equal(DeviceLighting.Frame(JoinProgram(), .9), x.Color);
        Assert.Equal(DeviceLighting.Frame(JoinProgram(), .5), y.Color);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, ["a", "b", "x", "y"]));
        Assert.All(new[] { a, b, x, y }, d => Assert.Equal(1, d.Ends));
    }
    [Fact]
    public async Task AnIndependentNewControllerJoinsWithoutReopeningTheExistingComposition()
    {
        var a = new Driver("a"); var b = new Driver("b"); var c = new Driver("c"); await using var r = new Rig(a, b, c).Open();
        await r.Start(["a", "b"]); await r.Tick(.5);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start(["a", "c"], brightness: 60)).Result!.Outcome);
        Assert.Equal(DeviceLighting.Frame(JoinProgram(60), 0), a.Color); Assert.Equal(a.Color, c.Color); Assert.Equal(DeviceLighting.Frame(JoinProgram(), .5), b.Color);
        Assert.All(new[] { a, b, c }, d => { Assert.Equal(1, d.Opens); Assert.Equal(0, d.Ends); });
    }
    [Fact]
    public async Task FailedNewStreamPreparationLeavesExistingCompositionRunningAtItsOriginalPhase()
    {
        var a = new Driver("a"); var b = new Driver("b"); var c = new Driver("c"); var opening = new OpeningDriver(c) { FailOpen = true };
        await using var r = new Rig(a, b, opening).Open(); await r.Start(["a", "b"]); await r.Tick(.5);
        Assert.Equal(Outcome.Rejected, (await r.Start(["a", "c"], brightness: 60)).Result!.Outcome);
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(JoinProgram(), .7), a.Color); Assert.Equal(a.Color, b.Color);
        Assert.Equal(0, a.Ends); Assert.Equal(0, b.Ends); Assert.Equal(0, c.Writes);
        Assert.Equal(100, r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "a").RequestedLighting!.Brightness);
        opening.FailOpen = false; Assert.Equal(Outcome.WrittenUnverified, (await r.Start(["a", "c"])).Result!.Outcome);
    }
    [Fact]
    public async Task UnconfirmedNewStreamCleanupBlocksFurtherOutputToThatController()
    {
        var a = new Driver("a"); var c = new Driver("c"); var opening = new OpeningDriver(c) { FailOpen = true, FailCleanup = true };
        await using var r = new Rig(a, opening).Open(); await r.Start(["a"]);
        Assert.Equal(Outcome.Rejected, (await r.Start(["a", "c"])).Result!.Outcome);
        opening.FailOpen = false; var retry = await r.Start(["c"]); Assert.False(retry.Success); Assert.Contains("復元", retry.Error);
        Assert.Equal(0, c.Opens); Assert.Equal(0, a.Ends); opening.FailCleanup = false;
    }
    [Fact]
    public async Task ATopologyChangeDuringNewStreamOpeningAbortsTheJoinAndReleasesOnlyTheNewStream()
    {
        var a = new Driver("a"); var b = new Driver("b"); var c = new Driver("c"); var fan = new GenericTestDriver("fan");
        var opening = new OpeningDriver(c) { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = new Rig(a, b, opening, fan).Open(); await r.Start(["a", "b"]); await r.Tick(.5);
        var joining = r.Start(["a", "c"]);
        try
        {
            await opening.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.Renew, []) with { Targets = null, TopologyRevision = 0 })).Success);
            Assert.False((await r.Start(["a"])).Success);
            Assert.Equal(Outcome.Confirmed, (await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty, ["fan"], duty: 70))).Result!.Outcome);
            fan.Connected = false; r.Runtime.Refresh();
        }
        finally { opening.Continue.TrySetResult(); }
        Assert.Equal(Outcome.Rejected, (await joining.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        await r.Tick(.2); Assert.Equal(DeviceLighting.Frame(JoinProgram(), .7), a.Color); Assert.Equal(a.Color, b.Color);
        Assert.Equal(1, c.Ends); Assert.Equal(0, c.Writes); Assert.Equal(0, a.Ends); Assert.Equal(0, b.Ends);
    }
    [Fact]
    public async Task ANewMemberFirstFrameFailureReleasesEveryTransferredStreamExactlyOnce()
    {
        var a = new Driver("a"); var b = new Driver("b"); var c = new Driver("c") { FailFrame = true }; await using var r = new Rig(a, b, c).Open();
        await r.Start(["a", "b"]); var reply = await r.Start(["a", "c"]);
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        Assert.All(new[] { a, b, c }, d => { Assert.Equal(1, d.Ends); Assert.Equal(DeviceLightingStopReason.Failure, d.EndReason); });
        Assert.All(r.Runtime.State().Endpoints, e => { Assert.False(e.Owned); Assert.False(e.Lighting!.Running); });
    }
}
