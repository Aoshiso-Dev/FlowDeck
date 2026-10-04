using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class HubLightingDriverTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private static readonly LightingProgram Blue = new("fixed", [new(20, 80, 240)]);
    internal sealed class Hub : IHubLightingEndpointAdapter
    {
        public HubIdentity Identity = new("light-hub", "Hub", "light-hub", "virtual");
        public readonly DemoHub Fans = new(new("light-hub", "Hub", "light-hub", "virtual")) { DelayMs = 0 };
        public bool IsDemo => true;
        public string Version => "test";
        public bool SupportsFanEndpoints => true;
        public bool SupportsFanDeadlines => true;
        public bool SupportsLightingEndpoints { get; set; } = true;
        public bool SupportsLightingDeadlines { get; set; } = true;
        public bool SupportsSmoothLightingFrames { get; set; }
        public Func<HubStatus, HubStatus> Transform = s => s;
        public Action? BeforeLighting;
        public bool ExpireNext, OmitResults;
        public string? FailChannel;
        public bool Disconnected;
        public int Opens;
        public readonly List<(string Channel, HubLightSetting Effect, long? Deadline)> Writes = [];
        public readonly Dictionary<string, HubLightSetting> Colors = [];
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => Task.FromResult<HubIdentity[]>([Identity]);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default)
        { Opens++; Disconnected = false; return ReadAsync(ct); }
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default)
        {
            if (Disconnected) throw new IOException("HUB_DISCONNECTED");
            return Transform((await Fans.ReadAsync(ct)) with
                { Lighting = [new("led1", "RGB", 24), new("led2", "RGB", 24), new("led3", "RGB", 24)] });
        }
        public async Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default)
        {
            if (expected.HubId != Identity.Id || expected.WiringSignature != ControlCoordinator.WiringSignature(await ReadAsync(ct)))
                return new(Outcome.Rejected, Error: "WIRING_CHANGED");
            var result = await Fans.SetFanEndpointAsync(channel, duty, expected with { WiringSignature = ControlCoordinator.WiringSignature(await Fans.ReadAsync(ct)) }, ct);
            return result with { Status = await ReadAsync(ct) };
        }
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => throw new InvalidOperationException("No fan broadcast");
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => throw new InvalidOperationException("No legacy lighting path");
        public async Task<HubResult> SetLightingEndpointsAsync(string[] channels, HubLightSetting effect, HubLightingExpectation expected, CancellationToken ct = default)
        {
            BeforeLighting?.Invoke();
            if (ExpireNext || HardwareWriteDeadline.Expired(expected.ValidUntilTick64)) { ExpireNext = false; return new(Outcome.Rejected, Error: HardwareWriteDeadline.LightingExpiredError); }
            if (expected.HubId != Identity.Id || expected.WiringSignature != ControlCoordinator.WiringSignature(await ReadAsync(ct)))
                return new(Outcome.Rejected, Error: "WIRING_CHANGED");
            var failed = false; var rows = new List<ChannelResult>();
            foreach (var channel in channels)
            {
                if (failed) { rows.Add(new(channel, "notExecuted", null, "unknown")); continue; }
                Writes.Add((channel, Contract.Copy(effect), expected.ValidUntilTick64));
                if (FailChannel == channel) { failed = true; rows.Add(new(channel, "unknown", null, "unknown", "SIMULATED_LOSS")); continue; }
                Colors[channel] = Contract.Copy(effect); rows.Add(new(channel, "completed", null, "writtenUnverified"));
            }
            if (OmitResults) return new(Outcome.WrittenUnverified);
            return new(failed ? rows.Any(r => r.Write == "completed") ? Outcome.Partial : Outcome.Unknown : Outcome.WrittenUnverified, Channels: rows.ToArray());
        }
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static RegisteredEndpoint[] Ports => [new("led1", "上", EndpointKind.Lighting, Setup: new("RGB", 24, true)),
        new("led2", "横", EndpointKind.Lighting, Setup: new("RGB", 24, true)), new("led3", "下", EndpointKind.Lighting, Setup: new("RGB", 24, true))];
    private static LightingEndpointConfiguration[] Lights(HubDeviceDriver d) => [new(new(d.DeviceId, "led1"), true, new(LightingExitAction.FixedColor, new(9, 8, 7))),
        new(new(d.DeviceId, "led2"), true, new(LightingExitAction.Off)), new(new(d.DeviceId, "led3"), true, new(LightingExitAction.FixedColor, new(1, 2, 3)))];
    private static CoolingEndpointConfiguration Fan(HubDeviceDriver d) => new(new(d.DeviceId, "fan1"), true, new(30, 100, 65, CoolingExitAction.FixedDuty, []), []);
    internal static async Task<HubDeviceDriver> Ready(Hub hub, string path, TestClock? clock = null)
    {
        var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, path, clock); await driver.InitializeAsync();
        driver.Configure(driver.BindingCandidate, [Fan(driver)]); driver.ConfigureLighting(driver.BindingCandidate, Lights(driver), Ports); return driver;
    }
    private static HubLightingRecoveryJournal Journal(HubDeviceDriver d, string path) => new(path, d.DeviceId);
    [Fact] public async Task LightingRecoveryReopensTheSameHubAndKeepsTheStoredExitColor()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        hub.Disconnected = true;
        var result = Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(Outcome.WrittenUnverified, result.Result.Outcome); Assert.Equal(2, hub.Opens);
        Assert.Equal(2, hub.Writes.Count); Assert.All(hub.Writes, w => Assert.Equal("led1", w.Channel));
        Assert.Equal(new RgbColor(9, 8, 7), Assert.Single(hub.Colors["led1"].Colors));
        Assert.Empty(Journal(driver, dir.Path).Pending); Assert.Equal(0, hub.Fans.Writes);
    }
    private sealed class Rig(HubDeviceDriver driver, TestClock clock, params IDeviceControlDriver[] others) : IAsyncDisposable
    {
        public DeviceControlRuntime Runtime = new([driver, .. others], ["user"], new TestLog(), clock);
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public Rig Open() { Hello = Runtime.OpenSession("user"); return this; }
        public DeviceServiceRequest Request(DeviceOperation op, string[] channels, LightingProgram? program = null, int? duty = null)
        {
            var state = Runtime.State();
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", op, state.Inventory.TopologyRevision,
                state.Endpoints.Where(e => e.Target.DeviceId == driver.DeviceId && channels.Contains(e.Target.EndpointId))
                    .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray(), duty, Lighting: program);
        }
        public Task<DeviceServiceReply> Start(params string[] channels) => Runtime.ExecuteAsync(Request(DeviceOperation.SetLighting, channels,
            new("breathing", [new(40, 80, 200)], 4, Brightness: 50)));
        public Task Tick(double seconds) { clock.Advance(seconds); return Runtime.TickLightingAsync(); }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
    [Fact] public async Task SessionExitRestoresEachConfiguredPortAndDoesNotWriteFansOrOtherLights()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var r = new Rig(driver, clock).Open(); Assert.True((await r.Start("led1", "led2")).Success); await r.Tick(.5);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseSession, []) with { Targets = null, TopologyRevision = 0 })).Success);
        Assert.Equal(new RgbColor(9, 8, 7), hub.Colors["led1"].Colors[0]); Assert.Equal("off", hub.Colors["led2"].Mode);
        Assert.False(hub.Colors.ContainsKey("led3")); Assert.Equal(0, hub.Fans.Writes); Assert.Empty(Journal(driver, dir.Path).Pending);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Owned));
        var writes = hub.Writes.Count; await r.Tick(.5); Assert.Equal(writes, hub.Writes.Count);
    }
    [Fact] public async Task FailedSessionExitRetainsOnlyTheUnconfirmedPortForAnExplicitRetry()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var r = new Rig(driver, clock).Open(); Assert.True((await r.Start("led1", "led2")).Success);
        hub.FailChannel = "led2";
        Assert.False((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseSession, []) with { Targets = null, TopologyRevision = 0 })).Success);
        Assert.Equal(new RgbColor(9, 8, 7), hub.Colors["led1"].Colors[0]); Assert.Equal("led2", Assert.Single(Journal(driver, dir.Path).Pending).Channel);
        Assert.Equal(Outcome.Unknown, r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "led2").Outcome);
        hub.FailChannel = null; var boundary = hub.Writes.Count;
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Equal("led2", Assert.Single(hub.Writes.Skip(boundary)).Channel); Assert.Equal("off", hub.Colors["led2"].Mode);
        Assert.Empty(Journal(driver, dir.Path).Pending); Assert.Equal(0, hub.Fans.Writes); Assert.False(hub.Colors.ContainsKey("led3"));
    }
    [Fact] public async Task RuntimeIntensityEditsKeepOneHubStreamAndItsPerPortRecoveryObligations()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var r = new Rig(driver, clock).Open(); await r.Start("led1", "led2"); await r.Tick(.5);
        var obligations = Journal(driver, dir.Path).Pending; var other = hub.Colors["led2"].Colors[0];
        var program = new LightingProgram("breathing", [new(40, 80, 200)], 4, Brightness: 25);
        var reply = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.SetLighting, ["led1"], program));
        Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
        Assert.Equal(DeviceLighting.Frame(program, .5), hub.Colors["led1"].Colors[0]); Assert.Equal(other, hub.Colors["led2"].Colors[0]);
        Assert.Equal(obligations, Journal(driver, dir.Path).Pending); Assert.False(hub.Colors.ContainsKey("led3")); Assert.Equal(0, hub.Fans.Writes);
        await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.StopLighting, ["led1", "led2"]));
        Assert.Equal(new RgbColor(10, 20, 50), hub.Colors["led1"].Colors[0]); Assert.Equal(new RgbColor(20, 40, 100), hub.Colors["led2"].Colors[0]);
        Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task EndpointFramesKeepDistinctPortColorsAndTheirOwnNormalStopColors()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        var stream = await driver.OpenLightingStreamAsync(["led1", "led2"], new("breathing", [new(40, 80, 200)]), default);
        Assert.True(driver.LightingLimits!.EndpointColors);
        var frame = new DeviceLightingFrame(1, new(255, 0, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500)),
            [new("led1", new(1, 2, 3), new(10, 20, 30)), new("led2", new(4, 5, 6), new(0, 0, 0))]);
        var sent = await stream.WriteAsync(frame, default); Assert.All(sent, r => Assert.Equal(Outcome.WrittenUnverified, r.Result.Outcome));
        Assert.Equal(new RgbColor(1, 2, 3), hub.Colors["led1"].Colors[0]); Assert.Equal(new RgbColor(4, 5, 6), hub.Colors["led2"].Colors[0]);
        Assert.False(hub.Colors.ContainsKey("led3")); Assert.Equal(0, hub.Fans.Writes);
        var writes = hub.Writes.Count;
        var expired = new DeviceLightingFrame(2, new(255, 255, 255), () => new("expired", true),
            [new("led1", new(0, 0, 0), new(0, 0, 0)), new("led2", new(0, 0, 0), new(0, 0, 0))]);
        Assert.All(await stream.WriteAsync(expired, default), row => Assert.Equal(DeviceDriverFailure.LightingFrameExpired, row.Result.Failure));
        Assert.Equal(writes, hub.Writes.Count);
        await stream.EndAsync(DeviceLightingStopReason.Normal, default);
        Assert.Equal(new RgbColor(10, 20, 30), hub.Colors["led1"].Colors[0]); Assert.Equal(new RgbColor(0, 0, 0), hub.Colors["led2"].Colors[0]);
        Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Theory] [InlineData("led1")] [InlineData("led3")]
    public async Task EndpointFrameTargetMismatchDoesNotWriteOrReplaceThePreviousStopColor(string target)
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        var stream = await driver.OpenLightingStreamAsync(["led1", "led2"], new("breathing", [new(40, 80, 200)]), default);
        var frame = new DeviceLightingFrame(1, new(255, 255, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500)), [new(target, new(1, 2, 3), new(9, 9, 9))]);
        Assert.All(await stream.WriteAsync(frame, default), r => Assert.Equal(Outcome.Rejected, r.Result.Outcome)); Assert.Empty(hub.Writes);
        await stream.EndAsync(DeviceLightingStopReason.Normal, default); Assert.Empty(hub.Writes); Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task LightingNeedsItsOwnBindingWiringCountAndExplicitExitAndDoesNotEnableCooling()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); var action = new DeviceEndpointAction("led1", DeviceOperation.SetLighting, Lighting: Blue);
        Assert.NotNull(driver.Validate(action));
        foreach (var config in new[] { Lights(driver)[0] with { WiringConfirmed = false }, Lights(driver)[0] with { Exit = null },
                     Lights(driver)[0] with { Exit = new(LightingExitAction.RestoreOriginal) } })
        { driver.ConfigureLighting(driver.BindingCandidate, [config], Ports); Assert.NotNull(driver.Validate(action)); }
        foreach (var setup in new[] { new EndpointSetup("RGB", 24, false), new("wrong", 24, true), new("RGB", 25, true), new("RGB", 12, true) })
        { driver.ConfigureLighting(driver.BindingCandidate, Lights(driver), [Ports[0] with { Setup = setup }, .. Ports.Skip(1)]); Assert.NotNull(driver.Validate(action)); }
        driver.ConfigureLighting(null, Lights(driver), Ports); Assert.NotNull(driver.Validate(action));
        driver.ConfigureLighting(driver.BindingCandidate, Lights(driver), Ports); Assert.Null(driver.Validate(action));
        Assert.NotNull(driver.Validate(new("fan1", DeviceOperation.SetDuty, 50))); Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
        hub.SupportsLightingDeadlines = false; Assert.Null(driver.Validate(action)); Assert.NotNull(driver.ValidateLightingStream(["led1"], new("breathing", [new(1, 2, 3)])));
        hub.SupportsLightingEndpoints = false; Assert.NotNull(driver.Validate(action));
    }
    [Fact] public async Task NativeAndFixedColorsNormalizeBrightnessAndApplyIndependentExitPolicies()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        hub.BeforeLighting = () => Assert.NotEmpty(Journal(driver, dir.Path).Pending);
        var fixedResult = await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue with { Brightness = 50 }), default);
        Assert.Equal(Outcome.WrittenUnverified, fixedResult.Outcome); Assert.Equal(new RgbColor(10, 40, 120), hub.Colors["led1"].Colors[0]);
        Assert.Equal(100, fixedResult.ReportedLighting!.Brightness);
        var native = new LightingProgram("native/nzxt-control-hub/breathing", [new(60, 80, 100)], Brightness: 50);
        Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new("led2", DeviceOperation.SetLighting, Lighting: native), default)).Outcome);
        Assert.Equal("breathing", hub.Colors["led2"].Mode); Assert.Equal(new RgbColor(30, 40, 50), hub.Colors["led2"].Colors[0]);
        Assert.NotNull(driver.Validate(new("led3", DeviceOperation.SetLighting, Lighting: new("native/nzxt-control-hub/spectrum-wave", [], Brightness: 50))));
        Assert.NotNull(driver.Validate(new("led3", DeviceOperation.SetLighting, Lighting: Blue with { LedColors = [new(1, 2, 3)] })));
        hub.BeforeLighting = null;
        var released = await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default);
        Assert.Equal(2, released.Length); Assert.All(released, r => Assert.Equal(Outcome.WrittenUnverified, r.Result.Outcome));
        Assert.Equal(new RgbColor(9, 8, 7), hub.Colors["led1"].Colors[0]); Assert.Equal("off", hub.Colors["led2"].Mode);
        Assert.False(hub.Colors.ContainsKey("led3")); Assert.Equal(0, hub.Fans.Writes); Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task OpeningAndAbandoningAStreamDoesNotChangeAnyOutput()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        var stream = await driver.OpenLightingStreamAsync(["led1", "led2"], new("color-cycle", [new(30, 60, 90)]), default);
        Assert.Equal(2, Journal(driver, dir.Path).Pending.Length); Assert.Empty(hub.Writes);
        Assert.Throws<IOException>(() => driver.ConfigureLighting(driver.BindingCandidate, Lights(driver), Ports));
        Assert.All(await stream.EndAsync(DeviceLightingStopReason.Failure, default), r => Assert.Equal(Outcome.Confirmed, r.Result.Outcome));
        await stream.EndAsync(DeviceLightingStopReason.Normal, default); Assert.Empty(hub.Writes); Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task ServiceFramesSharePhaseUseOneBrightnessAndNormalStopSettlesTheWholeHubGroup()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var rig = new Rig(driver, clock).Open(); Assert.Equal(Outcome.WrittenUnverified, (await rig.Start("led1", "led2")).Result!.Outcome);
        Assert.Equal(new RgbColor(20, 40, 100), hub.Colors["led1"].Colors[0]); Assert.Equal(hub.Colors["led1"].Colors[0], hub.Colors["led2"].Colors[0]);
        Assert.All(hub.Writes, w => Assert.NotNull(w.Deadline)); var obligations = Journal(driver, dir.Path).Pending;
        await rig.Tick(1); Assert.Equal(new RgbColor(0, 0, 0), hub.Colors["led1"].Colors[0]); Assert.Equal(obligations, Journal(driver, dir.Path).Pending);
        var stopped = await rig.Runtime.ExecuteAsync(rig.Request(DeviceOperation.StopLighting, ["led1", "led2"]));
        Assert.Equal(Outcome.WrittenUnverified, stopped.Result!.Outcome); Assert.Empty(Journal(driver, dir.Path).Pending);
        Assert.Equal(new RgbColor(20, 40, 100), hub.Colors["led1"].Colors[0]); Assert.Equal(hub.Colors["led1"].Colors[0], hub.Colors["led2"].Colors[0]);
        var writes = hub.Writes.Count; await rig.Tick(.5); Assert.Equal(writes, hub.Writes.Count); Assert.False(hub.Colors.ContainsKey("led3"));
    }
    [Fact] public async Task WorkerExpiryDropsAFrameAndNextFrameContinuesWithoutRewritingRecoveryRecords()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var rig = new Rig(driver, clock).Open(); await rig.Start("led1"); var saved = Journal(driver, dir.Path).Pending;
        hub.ExpireNext = true; await rig.Tick(.1); Assert.Single(hub.Writes);
        Assert.Equal(1, rig.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "led1").Lighting!.DroppedFrames);
        await rig.Tick(.1); Assert.Equal(2, hub.Writes.Count); Assert.Equal(saved, Journal(driver, dir.Path).Pending);
        await rig.Runtime.DisconnectAsync(rig.Hello.Session); Assert.Equal(new RgbColor(9, 8, 7), hub.Colors["led1"].Colors[0]); Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task PartialFailureRestoresOtherPortsAndKeepsOnlyFailedObligationWhileCoolingContinues()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var rig = new Rig(driver, clock).Open(); await rig.Start("led1", "led2"); hub.FailChannel = "led2";
        await rig.Tick(.1); Assert.Equal(new RgbColor(9, 8, 7), hub.Colors["led1"].Colors[0]);
        Assert.Equal("led2", Assert.Single(Journal(driver, dir.Path).Pending).Channel);
        Assert.Equal(Outcome.Confirmed, (await rig.Runtime.ExecuteAsync(rig.Request(DeviceOperation.SetDuty, ["fan1"], duty: 70))).Result!.Outcome);
        Assert.False((await rig.Start("led2")).Success);
        hub.FailChannel = null;
        Assert.Equal(Outcome.WrittenUnverified, (await rig.Runtime.ExecuteAsync(rig.Request(DeviceOperation.StopLighting, ["led2"]))).Result!.Outcome);
        Assert.Empty(Journal(driver, dir.Path).Pending); Assert.Equal("off", hub.Colors["led2"].Mode);
    }
    [Fact] public async Task ANewConnectionCannotReceiveOldStreamFramesOrAutomaticRestoration()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock(); var driver = await Ready(hub, dir.Path, clock);
        await using var rig = new Rig(driver, clock).Open(); await rig.Start("led1"); var count = hub.Writes.Count;
        hub.Transform = s => s with { Firmware = "different" }; await driver.InitializeAsync(); rig.Runtime.Refresh(); await rig.Tick(.1);
        Assert.Equal(count, hub.Writes.Count); Assert.Single(Journal(driver, dir.Path).Pending);
        Assert.Equal(Outcome.Unknown, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.DeviceChanged, default)).Result.Outcome);
        Assert.Equal(count, hub.Writes.Count);
    }
    [Fact] public async Task MissingResultCannotClearOwnershipAndColdRecoveryUsesSavedExitBeforeNewSettings()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var first = await Ready(hub, dir.Path);
        hub.OmitResults = true; Assert.Equal(Outcome.Unknown, (await first.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Single(Journal(first, dir.Path).Pending); hub.OmitResults = false;
        await using var second = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path); await second.InitializeAsync();
        Assert.Throws<IOException>(() => second.ConfigureLighting(second.BindingCandidate, Lights(second), Ports));
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await second.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default)).Result.Outcome);
        Assert.Equal(new RgbColor(9, 8, 7), hub.Colors["led1"].Colors[0]); Assert.Empty(Journal(second, dir.Path).Pending);
        second.ConfigureLighting(second.BindingCandidate, Lights(second), Ports);
        // The first object represents a terminated process and must never resume or dispose after ownership transfers.
    }
    [Fact] public async Task ReadinessIsRecheckedAfterAStatusReadAndJournalFailurePreventsOutput()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        hub.Transform = s => s with { Lighting = s.Lighting.Select(l => l.Id == "led1" ? l with { Accessory = "replaced" } : l).ToArray() };
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Empty(hub.Writes);
        hub.Transform = s => s; await driver.RefreshAsync();
        var path = Path.Combine(dir.Path, "hub-lighting-" + SettingsTransfer.Hash(driver.DeviceId).ToLowerInvariant() + ".json");
        Directory.CreateDirectory(path);
        var error = await Record.ExceptionAsync(() => driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(hub.Writes);
    }
}
