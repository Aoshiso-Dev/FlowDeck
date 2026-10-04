using System.Text.Json;
using FlowDeck.Infrastructure;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class DeviceLightingRuntimeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper replayOutput;
    public DeviceLightingRuntimeTests(Xunit.Abstractions.ITestOutputHelper output) => replayOutput = output;
    private sealed class MirrorBackend(Func<DeviceWorkspaceSnapshot> snapshot) : IM5LightingBackend, IM5BodyLedBackend
    {
        public int Commands;
        public M5LightingView Read(string scope) => DeviceM5Lighting.Read(snapshot(), scope);
        public M5BodyLedView ReadBodyLed(string scope) => DeviceM5Lighting.ReadBodyLed(snapshot(), scope);
        public Task<OperationResult> ExecuteAsync(M5LightingCommand command, CancellationToken ct)
        { Commands++; throw new InvalidOperationException("Receive-only test must not issue PC commands"); }
    }

    [Theory]
    [InlineData(Outcome.Confirmed, true, false)]
    [InlineData(Outcome.WrittenUnverified, true, false)]
    [InlineData(Outcome.Rejected, false, false)]
    [InlineData(Outcome.Unknown, false, false)]
    [InlineData(Outcome.Confirmed, true, true)]
    [InlineData(Outcome.WrittenUnverified, true, true)]
    [InlineData(Outcome.Rejected, false, true)]
    [InlineData(Outcome.Unknown, false, true)]
    public async Task OnlyASuccessfulNewStaticOutputClearsItsOwnStoppedGroupFault(Outcome outcome, bool recovered, bool batch)
    {
        var a = new Driver("a"); var b = new Driver("b");
        await using var r = new Rig(batch ? new BatchMirrorDriver(a) : a, b).Open();
        await r.Start(["a", "b"]); a.FailFrame = true; await r.Tick(.2);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.NotNull(e.Lighting?.Fault));
        var color = new LightingProgram("fixed", [new(1, 2, 3)]);
        a.NativeResult = new(outcome, "new static output", ReportedLighting: recovered ? color : null);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["a"], color));
        var state = r.Runtime.State(); var selected = state.Endpoints.Single(e => e.Target.DeviceId == "a");
        Assert.Equal(outcome, selected.Outcome);
        Assert.Equal(recovered, selected.Lighting is null);
        Assert.NotNull(state.Endpoints.Single(e => e.Target.DeviceId == "b").Lighting?.Fault);
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(new('d', 32));
        config = config with { Devices = new(2, [new("a", "Selected light", "demo", DeviceKind.Controller,
            [new("light1", "LED", EndpointKind.Lighting)])]) };
        var snapshot = new DeviceWorkspaceSnapshot(state, new(new('a', 32), config, []), 0, true, null, Session: new('f', 32));
        var mirror = DeviceM5Lighting.ReadBodyLed(snapshot, "all");
        Assert.Equal(recovered, mirror.Frame is not null);
        if (recovered) Assert.Equal(new RgbColor(1, 2, 3), mirror.Frame!.Color);
    }

    private sealed class BatchMirrorDriver(Driver inner) : IDeviceLightingDriver, IPhysicalDeviceDriver, IDeviceLightingBatchDriver
    {
        public string DeviceId => inner.DeviceId;
        public PhysicalDeviceBinding BindingCandidate => inner.BindingCandidate;
        public DeviceLightingLimits? LightingLimits => inner.LightingLimits;
        public DeviceObservation? Observe() => inner.Observe();
        public string? Validate(DeviceEndpointAction action) => inner.Validate(action);
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => inner.ValidateLightingStream(endpoints, program);
        public Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct) => inner.OpenLightingStreamAsync(endpoints, program, ct);
        public string? ValidateLightingBatch(EndpointLightingProgram[] programs) => null;
        public async Task<DeviceDriverReleaseResult[]> ApplyLightingBatchAsync(EndpointLightingProgram[] programs, CancellationToken ct) =>
            [new("light1", await inner.ApplyAsync(new("light1", DeviceOperation.SetLighting, Lighting: programs.Single().Program), ct))];
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => inner.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => inner.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedSkippedFramesKeepTheM5MirrorUntilOriginalFreshnessExpiresThenResumeAndStop()
    {
        var a = new Driver("a"); await using var r = new Rig(a).Open(); await r.Start(["a"]);
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(new('d', 32));
        config = config with { Devices = new(2, [new("a", "Test light", "demo", DeviceKind.Controller,
            [new("light1", "Test LED", EndpointKind.Lighting)])]) };
        DeviceWorkspaceSnapshot Snapshot() => new(r.Runtime.State(), new(new('a', 32), config, []), 0, true, null, Session: new('f', 32));
        var backend = new MirrorBackend(Snapshot);
        await using var f = new Fixture(); await f.Initialize();
        var protocol = new M5ProtocolSession(f.Core, time: f.Clock, lighting: backend);
        var packets = new List<JsonElement>();
        var replay = new List<object>();
        protocol.Outgoing += json =>
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("type").GetString() != "bodyLedState") return;
            var packet = doc.RootElement.Clone(); packets.Add(packet);
            var color = new RgbColor(0, 0, 0);
            if (packet.GetProperty("ready").GetBoolean())
            {
                var frame = packet.GetProperty("frame").Deserialize<M5BodyLedFrame>(Contract.Json)!;
                var speed = Math.Max(0, Array.IndexOf(new[] { 12000, 9000, 6000, 4000, 2000 }, frame.PeriodMs));
                color = DeviceLighting.Frame(new(frame.Effect, frame.Effect == "off" ? [] : [frame.Color], speed, Brightness: frame.Brightness), frame.PhaseMs / 1000);
            }
            replay.Add(new { atMs = r.Clock.GetTimestamp(), packet, expected = new[] { color.R, color.G, color.B } });
        };
        protocol.Receive(JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "panel", bootId = "b",
            firmwareVersion = "1.4.0", capabilities = new[] { "lighting-v1", "body-led-v1" } }));
        Assert.True(packets[^1].GetProperty("ready").GetBoolean());
        var stamp = backend.Read("all").Stamp;
        for (var i = 0; i < 4; i++)
        {
            a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var frame = r.Tick(.1); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            r.Clock.Advance(.55); a.Continue.SetResult(); await frame;
            protocol.SendState();
            Assert.True(packets[^1].GetProperty("ready").GetBoolean());
            Assert.Equal(stamp, backend.Read("all").Stamp);
            Assert.Equal(1, a.Writes);
            Assert.Equal(i + 1, r.Runtime.State().Endpoints[0].Lighting!.DroppedFrames);
            Assert.InRange(packets[^1].GetProperty("frame").GetProperty("validForMs").GetInt32(), 1, 3000 - (i + 1) * 650 + 1);
        }
        r.Clock.Advance(.401); protocol.SendState();
        Assert.False(packets[^1].GetProperty("ready").GetBoolean());
        Assert.True(r.Runtime.State().Endpoints[0].Lighting!.Running);
        a.Continue = null; await r.Tick(.1); protocol.SendState();
        Assert.True(packets[^1].GetProperty("ready").GetBoolean()); Assert.Equal(2, a.Writes);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, ["a"])); protocol.SendState();
        Assert.False(packets[^1].GetProperty("ready").GetBoolean());
        await r.Start(["a"]); protocol.SendState(); Assert.True(packets[^1].GetProperty("ready").GetBoolean());
        a.FailFrame = true; await r.Tick(.2); protocol.SendState();
        Assert.False(packets[^1].GetProperty("ready").GetBoolean());
        protocol.Disconnect(); var count = packets.Count; protocol.SendState(); Assert.Equal(count, packets.Count);
        Assert.Equal(0, backend.Commands);
        replayOutput.WriteLine("M5_REPLAY " + JsonSerializer.Serialize(replay));
    }

    [Fact]
    public async Task SkippingTheFirstFrameDoesNotClaimAnAppliedOutputOrExposeAMirror()
    {
        var a = new Driver("a") { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = new Rig(a).Open();
        var start = r.Start(["a"]); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        r.Clock.Advance(.55); a.Continue.SetResult(); await start;
        Assert.Equal(0, a.Writes);
        Assert.Null(r.Runtime.State().Endpoints[0].Lighting?.Mirror);
        Assert.False(r.Runtime.State().Endpoints[0].Lighting?.Running == true);
    }

    [Fact]
    public async Task SkippedFrameCannotHideAConcurrentFailureOrUnsettledRestoration()
    {
        var a = new Driver("a"); var b = new Driver("b"); await using var r = new Rig(a, b).Open();
        await r.Start(["a", "b"]);
        a.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); a.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        b.FailFrame = b.FailEnd = true;
        var tick = r.Tick(.1); await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => Volatile.Read(ref b.Writes) >= 2, TimeSpan.FromSeconds(3))));
        r.Clock.Advance(.55); a.Continue.SetResult(); await tick;
        Assert.All(r.Runtime.State().Endpoints, s => { Assert.Null(s.Lighting?.Mirror); Assert.False(s.Lighting?.Running == true); });
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "b").Owned);
    }
}
