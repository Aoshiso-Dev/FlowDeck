using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    internal sealed class Driver(string id, string? domain = null) : IDeviceLightingDriver, IPhysicalDeviceDriver
    {
        public string DeviceId => id;
        public PhysicalDeviceBinding BindingCandidate => new(id, "demo:" + id + ":" + Locator, "demo-v1", new('a', 64));
        public DeviceLightingLimits? LightingLimits { get; set; } = new(100, 500, 24, EndpointColors: true);
        public string Locator = "original";
        public bool Connected = true, Supported = true, FailFrame, FailEnd, OmitResult;
        public int Opens, Writes, Ends, NativeWrites;
        public TaskCompletionSource? Entered, Continue;
        public TaskCompletionSource? EndEntered, ContinueEnd;
        public Action? AfterWrite;
        public readonly List<RgbColor> Frames = [];
        public RgbColor Color = new(9, 8, 7);
        public DeviceLightingStopReason? EndReason;
        public DeviceObservation? Observe() => !Connected ? null : new(id, "demo-rgb", id, DeviceKind.Memory, IdentityConfidence.SerialBound, Locator, "demo-v1", [domain ?? "usb:" + id],
            [new("light1", "照明", EndpointKind.Lighting, new[] { DeviceFeature.FixedColor, DeviceFeature.ColorFrames, DeviceFeature.RestoreLighting }
                .Select(f => new DeviceCapability(f, Supported ? ImplementationSupport.Supported : ImplementationSupport.Unsupported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray())]);
        public string? Validate(DeviceEndpointAction action) => null;
        public string? ValidateLightingStream(string[] endpoints, LightingProgram p) => Supported && DeviceLighting.ValidAnimation(p) && endpoints.SequenceEqual(["light1"]) ? null : "unsupported";
        public Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram p, CancellationToken ct)
        { Opens++; return Task.FromResult<IDeviceLightingStream>(new Stream(this, p, Color, Locator)); }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
        {
            if (action.Operation == DeviceOperation.SetLighting) { NativeWrites++; Color = action.Lighting!.EffectId == "off" ? new(0, 0, 0) : action.Lighting.Colors[0].Scale(action.Lighting.Brightness / 100d); }
            return Task.FromResult(new DeviceDriverResult(Outcome.WrittenUnverified, "sent", ReportedLighting: new("fixed", [Color])));
        }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>(
            [new("light1", new(FailEnd ? Outcome.Unknown : Outcome.WrittenUnverified, "released", ReportedLighting: new("fixed", [Color])))]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Stream(Driver d, LightingProgram p, RgbColor original, string locator) : IDeviceLightingStream
        {
            private RgbColor? normalStopColor;
            public async Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct)
            {
                if (frame.TargetError(["light1"]) is { } invalid) return [new("light1", new(Outcome.Rejected, invalid))];
                d.Entered?.TrySetResult(); if (d.Continue is not null) await d.Continue.Task;
                var authorization = frame.Authorize();
                if (authorization.Error is { } error) return [new("light1", new(Outcome.Rejected, error, Failure: authorization.Expired ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None))];
                d.Writes++; if (d.FailFrame) return [new("light1", new(Outcome.Unknown, "lost frame"))];
                d.Color = frame.ColorFor("light1"); d.Frames.Add(d.Color); d.AfterWrite?.Invoke();
                normalStopColor = frame.NormalStopColorFor("light1") ?? p.Colors[0].Scale(p.Brightness / 100d);
                return d.OmitResult ? [] : [new("light1", new(Outcome.WrittenUnverified, "sent, no optical readback", ReportedLighting: new("fixed", [d.Color])))];
            }
            public async Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
            {
                d.Ends++; d.EndReason = reason;
                d.EndEntered?.TrySetResult(); if(d.ContinueEnd is not null) await d.ContinueEnd.Task;
                if (d.FailEnd || d.Locator != locator || !d.Connected) return [new("light1", new(Outcome.Unknown, "restore failed"))];
                d.Color = reason == DeviceLightingStopReason.Normal ? normalStopColor ?? p.Colors[0].Scale(p.Brightness / 100d) : original;
                return [new("light1", new(Outcome.WrittenUnverified, "settled", ReportedLighting: new("fixed", [d.Color])))];
            }
        }
    }
    private sealed class Rig(params IDeviceControlDriver[] drivers) : IAsyncDisposable
    {
        public readonly TestClock Clock = new();
        public readonly DeviceResourceScheduler Scheduler = new();
        public DeviceControlRuntime Runtime = null!;
        private DeviceServiceHello hello = null!;
        private long sequence;
        public Rig Open()
        { Runtime = new(drivers, ["user"], new TestLog(), Clock, Scheduler); hello = Runtime.OpenSession("user"); return this; }
        public DeviceServiceRequest Request(DeviceOperation operation, string[] devices, LightingProgram? program = null, int? duty = null)
        {
            var state = Runtime.State();
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation, state.Inventory.TopologyRevision,
                state.Endpoints.Where(e => devices.Contains(e.Target.DeviceId)).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray(), Duty: duty, Lighting: program);
        }
        public Task<DeviceServiceReply> Start(string[] ids, string mode = "color-cycle", int brightness = 100) => Runtime.ExecuteAsync(Request(DeviceOperation.SetLighting, ids, new(mode, [new(100, 50, 20)], 4, Brightness: brightness)));
        public Task Tick(double seconds) { Clock.Advance(seconds); return Runtime.TickLightingAsync(); }
        public Task Disconnect() => Runtime.DisconnectAsync(hello.Session);
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
    [Fact]
    public async Task IntensityEditsPreserveTheClockAndUnselectedBrightnessWithoutReopeningStreams()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]);
        await r.Tick(.5); var id = r.Runtime.State().Endpoints[0].Lighting!.GroupId; var beforeB = b.Color;
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 50);
        foreach (var brightness in new[] { 50, 50, 0, 50 })
        {
            var reply = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program with { Brightness = brightness }));
            Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
            Assert.Equal(DeviceLighting.Frame(program with { Brightness = brightness }, .5), a.Color); Assert.Equal(beforeB, b.Color);
            Assert.Equal(1, a.Opens); Assert.Equal(1, b.Opens); Assert.Equal(0, a.Ends); Assert.Equal(0, b.Ends);
            var state = r.Runtime.State(); Assert.All(state.Endpoints, e => { Assert.True(e.Lighting!.Running); Assert.Equal(id, e.Lighting.GroupId); });
            Assert.Equal(a.Color, state.Endpoints.Single(e => e.Target.DeviceId == "a").Lighting!.LastColor);
            Assert.Equal(b.Color, state.Endpoints.Single(e => e.Target.DeviceId == "b").Lighting!.LastColor);
        }
        await r.Tick(.25); Assert.Equal(DeviceLighting.Frame(program, .75), a.Color);
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 100 }, .75), b.Color);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, ["a", "b"]));
        Assert.Equal(new RgbColor(50, 25, 10), a.Color); Assert.Equal(new RgbColor(100, 50, 20), b.Color);
    }
    [Fact]
    public async Task RapidIntensityEditsDiscardQueuedOlderFramesWithoutStoppingTheNewerEdit()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = r.Tick(.5); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4);
        var old = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program with { Brightness = 25 }));
        var newer = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program with { Brightness = 75 }));
        a.Continue.TrySetResult(); await frame;
        Assert.Equal(Outcome.Rejected, (await old.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        Assert.Equal(Outcome.WrittenUnverified, (await newer.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 75 }, .5), a.Color); Assert.Equal(DeviceLighting.Frame(program, .5), b.Color);
        Assert.Equal(1, a.Opens); Assert.Equal(0, a.Ends); Assert.Equal(0, b.Ends);
        Assert.Equal(75, r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "a").RequestedLighting!.Brightness);
    }
    [Fact]
    public async Task ARejectedUnsentEditDoesNotApplyLaterAndLeavesTheOldStreamRunning()
    {
        var a = new Driver("a"); var fan = new GenericTestDriver("fan"); await using var r = new Rig(a, fan).Open(); await r.Start(["a"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = r.Tick(.5); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 25);
        var edit = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program));
        fan.Connected = false; r.Runtime.Refresh(); a.Continue.TrySetResult(); await frame;
        Assert.Equal(Outcome.Rejected, (await edit.WaitAsync(TimeSpan.FromSeconds(3))).Result!.Outcome);
        await r.Tick(.1); Assert.Equal(DeviceLighting.Frame(program with { Brightness = 100 }, .6), a.Color);
        Assert.Equal(1, a.Opens); Assert.Equal(0, a.Ends); Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "a").Lighting!.Running);
        Assert.Equal(100, r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "a").RequestedLighting!.Brightness);
    }
    [Fact]
    public async Task AnUnselectedMemberFailureCannotReportAnIntensityEditAsSuccessfulAfterRestoration()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]); b.FailFrame = true;
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 50);
        var reply = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], program));
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome); Assert.Equal(1, a.Ends); Assert.Equal(1, b.Ends);
        Assert.Equal(new RgbColor(9, 8, 7), a.Color); Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Lighting!.Running));
    }
    [Fact]
    public async Task BoardAndMemoryUseOneSampleAndOnePhaseWithoutAHubAndKeepUnselectedDevices()
    {
        var a = new Driver("board"); var b = new Driver("memory"); var untouched = new Driver("unused");
        await using var r = new Rig(a, b, untouched).Open();
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start([a.DeviceId, b.DeviceId])).Result!.Outcome);
        await r.Tick(.5); Assert.Equal(a.Frames, b.Frames); Assert.Equal(new RgbColor(50, 100, 0), a.Color);
        Assert.Equal(0, untouched.Writes); Assert.Equal(0, untouched.Opens);
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Target.DeviceId != untouched.DeviceId), e => { Assert.True(e.Lighting!.Running); Assert.Equal(2, e.Lighting.Frames); });
    }
    [Fact]
    public async Task SharedBusMembersReceiveTheSameSampleEvenWhenTimeAdvancesDuringTheFirstWrite()
    {
        var a = new Driver("a", "shared"); var b = new Driver("b", "shared"); await using var r = new Rig(a, b).Open();
        a.AfterWrite = () => r.Clock.Advance(.05); b.AfterWrite = () => r.Clock.Advance(.07); await r.Start(["a", "b"]);
        Assert.Equal(new RgbColor(100, 0, 0), a.Color); Assert.Equal(a.Color, b.Color);
        Assert.Equal(70, r.Runtime.State().Endpoints[0].Lighting!.MaximumWriteMilliseconds);
        await r.Tick(.3); Assert.Equal(a.Frames, b.Frames);
    }
    [Fact]
    public async Task AQueuedCoolingCommandRunsBeforeTheNextLightingMemberOnASharedResource()
    {
        var a = new Driver("a", "shared"); var b = new Driver("b", "shared"); var fan = new GenericTestDriver("fan", "shared");
        await using var r = new Rig(a, b, fan).Open(); await r.Start(["a", "b"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var fanWritesAtNextLight = -1; b.AfterWrite = () => fanWritesAtNextLight = fan.Writes;
        var frame = r.Tick(.1); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var control = r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty, ["fan"], duty: 80));
        try { await Task.Run(async () => { while (r.Scheduler.PendingCount < 2) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { a.Continue.TrySetResult(); }
        await Task.WhenAll(frame, control).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fanWritesAtNextLight); Assert.Equal(80, fan.Duty);
    }
    [Fact]
    public async Task BrightnessIsCalculatedFromTheBaseColorAndNormalStopSettlesTheWholeGroup()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open();
        await r.Start(["a", "b"], "breathing", 50); Assert.Equal(new RgbColor(50, 25, 10), a.Color);
        await r.Tick(1); Assert.Equal(new RgbColor(0, 0, 0), a.Color);
        var stop = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, ["a", "b"]));
        Assert.Equal(Outcome.WrittenUnverified, stop.Result!.Outcome); Assert.Equal(new RgbColor(50, 25, 10), a.Color); Assert.Equal(a.Color, b.Color);
        Assert.Equal(1, a.Ends); Assert.Equal(1, b.Ends); Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Lighting!.Running));
    }
    [Fact]
    public async Task EveryTargetIsCheckedBeforeOpeningAnyStream()
    {
        var a = new Driver("a"); var unsupported = new Driver("b") { Supported = false };
        await using var r = new Rig(a, unsupported).Open(); Assert.False((await r.Start(["a", "b"])).Success);
        Assert.Equal(0, a.Opens); Assert.Equal(0, a.Writes);
        a.LightingLimits = new(1, 100, 24); Assert.False((await r.Start(["a"])).Success); Assert.Equal(0, a.Opens);
    }
    [Fact]
    public async Task SlowFramesDoNotAccumulateAndExpireBeforeTheLateTransportWrite()
    {
        var a = new Driver("a"); await using var r = new Rig(a).Open(); await r.Start(["a"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = r.Tick(.1); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 10; i++) { r.Clock.Advance(.1); _ = r.Runtime.TickLightingAsync(); }
        Assert.Equal(1, a.Writes); a.Continue.SetResult(); await pending;
        Assert.Equal(1, a.Writes); var status = r.Runtime.State().Endpoints[0].Lighting!;
        Assert.True(status.Running); Assert.Equal(1, status.DroppedFrames);
        a.Continue = null; await r.Tick(.1); Assert.Equal(2, a.Writes);
    }
    [Fact]
    public async Task OneFailureStopsOnlyItsSynchronizationGroupAndRestoresTargetsIndependently()
    {
        var a = new Driver("a"); var b = new Driver("b"); var c = new Driver("c"); var fan = new GenericTestDriver("fan");
        await using var r = new Rig(a, b, c, fan).Open(); await r.Start(["a", "b"]); await r.Start(["c"]);
        a.FailFrame = true; b.FailEnd = true; await r.Tick(.2);
        Assert.Equal(DeviceLightingStopReason.Failure, a.EndReason); Assert.Equal(new RgbColor(9, 8, 7), a.Color);
        var state = r.Runtime.State(); Assert.False(state.Endpoints.Single(e => e.Target.DeviceId == "a").Owned);
        Assert.True(state.Endpoints.Single(e => e.Target.DeviceId == "b").Owned);
        Assert.True(state.Endpoints.Single(e => e.Target.DeviceId == "c").Lighting!.Running);
        Assert.Equal(Outcome.Confirmed, (await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetDuty, ["fan"], duty: 75))).Result!.Outcome);
        Assert.Equal(75, fan.Duty); Assert.False((await r.Start(["b"])).Success);
        b.FailEnd = false;
    }
    [Fact]
    public async Task ConnectionChangeRejectsNewFramesAndDoesNotSendRestorationToTheReplacement()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]);
        var before = a.Color; a.Locator = "replaced"; r.Runtime.Refresh(); await r.Tick(.2);
        Assert.Equal(1, a.Writes); Assert.Equal(before, a.Color); Assert.Equal(new RgbColor(9, 8, 7), b.Color);
        Assert.Equal(DeviceLightingStopReason.DeviceChanged, a.EndReason);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "a").Owned);
    }
    [Fact]
    public async Task ReplacingOneMemberSettlesTheOldGroupBeforeStartingTheNewOperation()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open(); await r.Start(["a", "b"]);
        var fixedRequest = r.Request(DeviceOperation.SetLighting, ["a"], new("fixed", [new(1, 2, 3)]));
        Assert.Equal(Outcome.WrittenUnverified, (await r.Runtime.ExecuteAsync(fixedRequest)).Result!.Outcome);
        Assert.Equal(new RgbColor(1, 2, 3), a.Color); Assert.Equal(new RgbColor(100, 50, 20), b.Color);
        await r.Tick(.3); Assert.Equal(new RgbColor(1, 2, 3), a.Color); Assert.Equal(1, a.Ends);
    }
    [Fact]
    public async Task MissingFrameResultsStopTheGroupAndLossOfOwnerRestoresOriginalState()
    {
        var a = new Driver("a"); await using var r = new Rig(a).Open(); await r.Start(["a"]);
        a.OmitResult = true; await r.Tick(.2); Assert.Equal(DeviceLightingStopReason.Failure, a.EndReason);
        Assert.Equal(new RgbColor(9, 8, 7), a.Color);
        a.OmitResult = false; await r.Start(["a"]); await r.Disconnect();
        Assert.Equal(DeviceLightingStopReason.OwnerLost, a.EndReason); Assert.Equal(new RgbColor(9, 8, 7), a.Color);
    }
    [Fact]
    public async Task SameRequestIdCannotOpenASecondAnimation()
    {
        var a = new Driver("a"); await using var r = new Rig(a).Open();
        var request = r.Request(DeviceOperation.SetLighting, ["a"], new("breathing", [new(10, 20, 30)]));
        await r.Runtime.ExecuteAsync(request); await r.Runtime.ExecuteAsync(request); Assert.Equal(1, a.Opens); Assert.Equal(1, a.Writes);
    }
}
