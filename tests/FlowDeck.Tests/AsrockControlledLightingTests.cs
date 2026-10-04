using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Device = FlowDeck.Tests.AsrockVolatileTransactionTests.Device;

namespace FlowDeck.Tests;

// The real Runtime creates and authorizes frames; the real ASRock encoder and recovery transaction
// run on their dedicated thread. Only the HID session and physical-device binding are simulated.
public sealed class AsrockControlledLightingTests
{
    private sealed class Session(Device device) : IAsrockRgbSession
    {
        public Action<byte[]>? BeforeExchange;
        public byte[] Exchange(byte[] packet) { BeforeExchange?.Invoke(packet); return device.Exchange(packet); }
        public void Dispose() => device.Dispose();
    }
    private sealed class Driver : IDeviceLightingDriver
    {
        public readonly Device Device = new();
        public readonly Session Session;
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Controlled-Rgb-" + Guid.NewGuid().ToString("N"));
        public readonly byte[] Targets;
        public readonly Dictionary<byte, LightingExitPolicy> Exits;
        public Func<string?> Guard = () => null;
        public CancellationToken FrameCancellation;
        public AsrockRgbStream? Transport;
        public DeviceDriverReleaseResult[]? LastWrite, LastEnd;
        public DeviceLightingStopReason? StopReason;
        public string DeviceId => "asrock-recording-hid";
        public DeviceLightingLimits LightingLimits => new(100, 500, 2040, EndpointColors: true);
        public AsrockRgbRecoveryStore Journal => new(Directory);
        public Driver(params byte[] targets)
        {
            Session = new(Device); Targets = targets;
            Exits = targets.ToDictionary(z => z, _ => new LightingExitPolicy(LightingExitAction.RestoreOriginal));
        }
        public DeviceObservation Observe() => new(DeviceId, "recording-asrock", "記録用マザー", DeviceKind.Motherboard,
            IdentityConfidence.ConnectionBound, "recording-hid", "asrock-test", ["usb:recording-asrock"], Targets.Select(z =>
                new DeviceEndpoint("zone:" + z, "Zone " + z, EndpointKind.Lighting,
                    new[] { DeviceFeature.FixedColor, DeviceFeature.ColorFrames, DeviceFeature.RestoreLighting }.Select(f =>
                        new DeviceCapability(f, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)).ToArray())).ToArray());
        public string? Validate(DeviceEndpointAction action) => null;
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => null;
        public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
        {
            var zones = Targets.Select(z => new RgbZone("Zone " + z, 1, Device.Counts[z], null, null, "Static", z, Setting(z))).ToArray();
            Transport = await AsrockRgbStream.OpenControlledAsync("recording-hid", zones, Directory, program, Exits, () => Guard(),
                () => { Device.OwnerThread = Environment.CurrentManagedThreadId; return Session; }, () => "test-boot", () => { });
            return new Stream(this, Transport);
        }
        public BoardLightSetting Setting(byte zone)
        {
            var response = new byte[64]; response[3] = zone; Device.Raw[zone].CopyTo(response, 4);
            return AsrockRgbControl.Decode(response, Device.Order);
        }
        private sealed class Stream(Driver driver, IDeviceLightingStream transport) : IDeviceLightingStream
        {
            public async Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct)
                => driver.LastWrite = await transport.WriteAsync(frame, driver.FrameCancellation.CanBeCanceled ? driver.FrameCancellation : ct);
            public async Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
            {
                driver.StopReason = reason;
                return driver.LastEnd = await transport.EndAsync(reason, ct);
            }
        }
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
            => Task.FromResult(new DeviceDriverResult(Outcome.Rejected, "test transport only"));
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
            => Task.FromResult(LastEnd ?? []);
        public async ValueTask DisposeAsync() { if (Transport is not null) await Transport.DisposeAsync(); }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Driver Driver;
        public readonly TestClock Clock = new();
        public readonly DeviceControlRuntime Runtime;
        private readonly DeviceServiceHello hello;
        private long sequence;
        public Rig(params byte[] targets)
        {
            Driver = new(targets); Runtime = new([Driver], ["user"], new TestLog(), Clock);
            hello = Runtime.OpenSession("user");
        }
        public Task<DeviceServiceReply> Command(DeviceOperation op, LightingProgram? program = null, byte[]? targets = null)
        {
            var state = Runtime.State();
            return Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", op,
                state.Inventory.TopologyRevision, state.Endpoints.Where(e => targets is null || targets.Any(t => e.Target.EndpointId == "zone:" + t))
                    .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray(),
                Lighting: program)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        public Task<DeviceServiceReply> Start(string effect = "color-cycle", int brightness = 50)
            => Command(DeviceOperation.SetLighting, new(effect, [new(100, 50, 20)], 4, Brightness: brightness));
        public Task Tick(double seconds) { Clock.Advance(seconds); return Runtime.TickLightingAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        public Task Disconnect() => Runtime.DisconnectAsync(hello.Session).WaitAsync(TimeSpan.FromSeconds(5));
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (System.IO.Directory.Exists(Driver.Directory)) System.IO.Directory.Delete(Driver.Directory, true);
        }
    }
    [Fact]
    public async Task RuntimeIntensityEditsKeepDirectModePhaseAndUnselectedZoneColors()
    {
        await using var r = new Rig(2, 5); var d = r.Driver; var other = d.Setting(6);
        await r.Start(); await r.Tick(.5); var boundary = d.Device.Writes.Count; var group = r.Runtime.State().Endpoints[0].Lighting!.GroupId;
        var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 25);
        var reply = await r.Command(DeviceOperation.SetLighting, program, [2]); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
        Assert.All(d.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); Assert.True(p[4] is 0xe3 or 0xe4); });
        Assert.Equal(DeviceLighting.Frame(program, .5), d.LastWrite!.Single(v => v.EndpointId == "zone:2").Result.ReportedLighting!.Colors[0]);
        Assert.Equal(DeviceLighting.Frame(program with { Brightness = 50 }, .5), d.LastWrite!.Single(v => v.EndpointId == "zone:5").Result.ReportedLighting!.Colors[0]);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.Equal(group, e.Lighting!.GroupId));
        await r.Command(DeviceOperation.StopLighting, targets: [2, 5]);
        Assert.Equal(new BoardLightSetting(1, 25, 12, 5), d.Setting(2)); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), d.Setting(5));
        Assert.Equal(other, d.Setting(6)); Assert.False(d.Journal.Exists);
    }
    [Fact]
    public async Task EndpointFramesPreserveUnselectedZonesAndStopAtEachSelectedBaseColor()
    {
        await using var r = new Rig(2, 5); var d = r.Driver; var before = d.Setting(6);
        var stream = await d.OpenLightingStreamAsync(["zone:2", "zone:5"], new("breathing", [new(100, 50, 20)]), default);
        var frame = new DeviceLightingFrame(1, new(255, 0, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500)),
            [new("zone:2", new(1, 2, 3), new(10, 20, 30)), new("zone:5", new(4, 5, 6), new(0, 0, 0))]);
        var sent = await stream.WriteAsync(frame, default); Assert.All(sent, row => Assert.Equal(Outcome.WrittenUnverified, row.Result.Outcome));
        Assert.Equal(new RgbColor(1, 2, 3), sent.Single(row => row.EndpointId == "zone:2").Result.ReportedLighting!.Colors[0]);
        Assert.Equal(new RgbColor(4, 5, 6), sent.Single(row => row.EndpointId == "zone:5").Result.ReportedLighting!.Colors[0]);
        var body = d.Device.Writes.Where(p => p[3] == 0xff).SelectMany(p => p[4] == 0xe3 ? p[9..63] : p[5..62]).ToArray();
        Assert.Equal(new byte[] { 2, 1, 3 }, body[..3]); Assert.Equal(new byte[] { 5, 4, 6 }, body[240..243]);
        Assert.Equal(new byte[] { (byte)before.Green, (byte)before.Red, (byte)before.Blue }, body[285..288]);
        var writes = d.Device.Writes.Count;
        var expired = new DeviceLightingFrame(2, new(255, 255, 255), () => new("expired", true),
            [new("zone:2", new(0, 0, 0), new(0, 0, 0)), new("zone:5", new(0, 0, 0), new(0, 0, 0))]);
        Assert.All(await stream.WriteAsync(expired, default), row => Assert.Equal(DeviceDriverFailure.LightingFrameExpired, row.Result.Failure));
        Assert.Equal(writes, d.Device.Writes.Count);
        Assert.All(await stream.EndAsync(DeviceLightingStopReason.Normal, default), row => Assert.Equal(Outcome.Confirmed, row.Result.Outcome));
        Assert.Equal(new BoardLightSetting(1, 10, 20, 30), d.Setting(2)); Assert.Equal(new BoardLightSetting(1, 0, 0, 0), d.Setting(5));
        Assert.Equal(before, d.Setting(6)); Assert.False(d.Journal.Exists);
    }
    [Theory] [InlineData("zone:2")] [InlineData("zone:6")]
    public async Task IncompleteOrExtraEndpointMapsNeverEnterDirectMode(string target)
    {
        await using var r = new Rig(2, 5); var d = r.Driver;
        var stream = await d.OpenLightingStreamAsync(["zone:2", "zone:5"], new("breathing", [new(100, 50, 20)]), default);
        var frame = new DeviceLightingFrame(1, new(255, 255, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500)), [new(target, new(1, 2, 3), new(0, 0, 0))]);
        Assert.All(await stream.WriteAsync(frame, default), row => Assert.Equal(Outcome.Rejected, row.Result.Outcome));
        Assert.Empty(d.Device.Writes); Assert.False(d.Journal.Exists);
        await stream.EndAsync(DeviceLightingStopReason.Normal, default); Assert.Empty(d.Device.Writes);
    }
    [Fact]
    public async Task ServiceFramesUseOneBrightnessCalculationAndNormalStopUsesTheBaseColor()
    {
        await using var r = new Rig(5); var d = r.Driver; var original = d.Device.Raw.ToDictionary(p => p.Key, p => p.Value.ToArray());
        d.Exits[5] = new(LightingExitAction.Off);
        d.Device.BeforeWrite = _ => Assert.NotNull(d.Journal.Load());
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start()).Result!.Outcome);
        Assert.Equal(new BoardLightSetting(0, 0, 0, 0), d.Journal.Load()!.Desired[5]);
        await r.Tick(.5);
        Assert.Equal(new RgbColor(25, 50, 0), d.LastWrite![0].Result.ReportedLighting!.Colors[0]);
        var boundary = d.Device.Writes.Count; var stop = await r.Command(DeviceOperation.StopLighting);
        Assert.Equal(Outcome.Confirmed, stop.Result!.Outcome); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), d.Setting(5));
        Assert.Equal(original[2], d.Device.Raw[2]); Assert.Equal(original[6], d.Device.Raw[6]);
        Assert.DoesNotContain(d.Device.Writes.Skip(boundary), p => p[3] == 0xff);
        Assert.False(d.Journal.Exists); Assert.Equal(0xfd, d.Device.Order); Assert.True(d.Device.Disposed);
    }
    [Theory]
    [InlineData(DeviceLightingStopReason.Failure)] [InlineData(DeviceLightingStopReason.OwnerLost)]
    [InlineData(DeviceLightingStopReason.ServiceStopping)] [InlineData(DeviceLightingStopReason.ConfigurationChanged)]
    [InlineData(DeviceLightingStopReason.Suspended)] [InlineData(DeviceLightingStopReason.DeviceChanged)] [InlineData(DeviceLightingStopReason.SessionEnded)]
    public async Task NonNormalStopsApplyPerZoneExitsAndIgnoreTheCancelledAnimationToken(DeviceLightingStopReason reason)
    {
        await using var r = new Rig(2, 5, 6); var d = r.Driver; var original = d.Setting(6);
        d.Exits[2] = new(LightingExitAction.FixedColor, new(8, 40, 99)); d.Exits[5] = new(LightingExitAction.Off);
        await r.Start("breathing"); await r.Tick(.5);
        var results = await ((IDeviceLightingStream)d.Transport!).EndAsync(reason, new CancellationToken(true));
        Assert.All(results, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
        Assert.Equal(new BoardLightSetting(1, 8, 40, 99), d.Setting(2)); Assert.Equal(new BoardLightSetting(0, 0, 0, 0), d.Setting(5));
        Assert.Equal(original, d.Setting(6)); Assert.False(d.Journal.Exists);
    }
    [Fact]
    public async Task ExpiryAfterDurablePreparationDoesNotSendAnExitColorOrAnyOtherWrite()
    {
        await using var r = new Rig(5); var d = r.Driver; d.Exits[5] = new(LightingExitAction.Off);
        var delayed = false;
        d.Guard = () => { if (!delayed && d.Journal.Exists) { delayed = true; r.Clock.Advance(.6); } return null; };
        Assert.Equal(Outcome.Rejected, (await r.Start()).Result!.Outcome);
        Assert.True(delayed); Assert.All(d.LastWrite!, e => Assert.Equal(DeviceDriverFailure.LightingFrameExpired, e.Result.Failure));
        Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
        Assert.Empty(d.Device.Writes); Assert.False(d.Journal.Exists); Assert.True(d.Device.Disposed);
    }
    [Fact]
    public async Task AReadFailureWhenDiscardingTheUnsentFirstFrameCompletesTheRequest()
    {
        await using var r = new Rig(5); var d = r.Driver; var delayed = false;
        d.Guard = () => { if (!delayed && d.Journal.Exists) { delayed = true; r.Clock.Advance(.6); } return null; };
        d.Session.BeforeExchange = p => { if (delayed && p[1] == 0x14) throw new IOException("read failed after expiry"); };
        Assert.Equal(Outcome.Unknown, (await r.Start()).Result!.Outcome);
        Assert.All(d.LastWrite!, e => Assert.Contains("read failed after expiry", e.Result.Message));
        Assert.Empty(d.Device.Writes); Assert.False(d.Journal.Exists); Assert.True(d.Device.Disposed);
    }
    [Fact]
    public async Task AnExpiredLaterFrameIsDroppedAndTheNextFreshFrameCanContinue()
    {
        await using var r = new Rig(5); var d = r.Driver; await r.Start(); var boundary = d.Device.Writes.Count;
        d.Session.BeforeExchange = p => { if (p[1] == 0x14 && p[3] == 3) { d.Session.BeforeExchange = null; r.Clock.Advance(.6); } };
        await r.Tick(.1); Assert.Equal(boundary, d.Device.Writes.Count);
        var status = r.Runtime.State().Endpoints[0].Lighting!; Assert.True(status.Running); Assert.Equal(1, status.DroppedFrames);
        await r.Tick(.1); Assert.True(d.Device.Writes.Count > boundary); Assert.Equal(2, r.Runtime.State().Endpoints[0].Lighting!.Frames);
        Assert.True(d.Journal.Exists); await r.Disconnect(); Assert.False(d.Journal.Exists);
    }
    [Fact]
    public async Task ExpiryAfterAPartialFrameIsUnknownAndRestoresTheSavedExit()
    {
        await using var r = new Rig(5); var d = r.Driver; d.Exits[5] = new(LightingExitAction.Off); await r.Start();
        var boundary = d.Device.Writes.Count;
        d.Device.AfterWrite = p => { if (p[1] == 0x10 && p[3] == 0xff) { d.Device.AfterWrite = null; r.Clock.Advance(.6); } };
        await r.Tick(.1);
        Assert.Single(d.Device.Writes.Skip(boundary), p => p[3] == 0xff);
        Assert.All(d.LastWrite!, e => { Assert.Equal(Outcome.Unknown, e.Result.Outcome); Assert.Equal(DeviceDriverFailure.None, e.Result.Failure); });
        Assert.Equal(DeviceLightingStopReason.Failure, d.StopReason); Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
        Assert.Equal(new BoardLightSetting(0, 0, 0, 0), d.Setting(5)); Assert.False(d.Journal.Exists);
    }
    [Fact]
    public async Task CancellationAfterJournalSaveDoesNotCauseRecoveryToWriteAnUnstartedOutput()
    {
        await using var r = new Rig(5); var d = r.Driver; using var cancel = new CancellationTokenSource();
        d.FrameCancellation = cancel.Token; d.Exits[5] = new(LightingExitAction.Off);
        d.Guard = () => { if (d.Journal.Exists) cancel.Cancel(); return null; };
        Assert.Equal(Outcome.Unknown, (await r.Start()).Result!.Outcome);
        Assert.Empty(d.Device.Writes); Assert.False(d.Journal.Exists); Assert.True(d.Device.Disposed);
        Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
    }
    [Fact]
    public async Task ChangedPhysicalBindingStopsBeforeTheNextPacketAndRetainsRecovery()
    {
        await using var r = new Rig(2, 5); var d = r.Driver; await r.Start(); var boundary = d.Device.Writes.Count;
        d.Guard = () => "physical connection changed"; await r.Tick(.1);
        Assert.Equal(boundary, d.Device.Writes.Count); Assert.True(d.Journal.Exists); Assert.True(d.Device.Disposed);
        Assert.All(d.LastWrite!, e => Assert.Equal(Outcome.Unknown, e.Result.Outcome));
        Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Unknown, e.Result.Outcome));
    }
    [Fact]
    public async Task BindingIsRecheckedDuringRecoveryAndDoesNotWriteSubsequentZones()
    {
        await using var r = new Rig(2, 5); var d = r.Driver; await r.Start(); var boundary = d.Device.Writes.Count;
        d.Device.AfterWrite = p => { if (p[1] == 0x15) d.Guard = () => "disconnected during restoration"; };
        await r.Disconnect(); Assert.Single(d.Device.Writes.Skip(boundary)); Assert.True(d.Journal.Exists);
        Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Unknown, e.Result.Outcome));
    }
    [Fact]
    public async Task PartialRestorationReturnsIndependentResultsAndKeepsTheDurableRecord()
    {
        await using var r = new Rig(2, 5); var d = r.Driver; d.Exits[2] = d.Exits[5] = new(LightingExitAction.Off);
        await r.Start(); d.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5) throw new IOException("zone 5 write failed"); };
        await r.Disconnect();
        Assert.Equal(Outcome.Confirmed, d.LastEnd!.Single(e => e.EndpointId == "zone:2").Result.Outcome);
        Assert.Equal(Outcome.Unknown, d.LastEnd!.Single(e => e.EndpointId == "zone:5").Result.Outcome);
        Assert.Equal(new BoardLightSetting(0, 0, 0, 0), d.Setting(2)); Assert.True(d.Journal.Exists);
        Assert.False(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:2").Owned);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
    }
    [Fact]
    public async Task FailureToRestoreAnUnselectedZoneIsNotReportedAsCompleteForTheSelectedZone()
    {
        await using var r = new Rig(5); var d = r.Driver; await r.Start();
        d.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 6) throw new IOException("unselected zone restore failed"); };
        await r.Disconnect(); Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Unknown, e.Result.Outcome)); Assert.True(d.Journal.Exists);
    }
    [Fact]
    public async Task RestoredNativeEffectRetainsItsColorAndHardwareSpeedInTheCommonResult()
    {
        await using var r = new Rig(5); var d = r.Driver; var original = new BoardLightSetting(7, 4, 80, 23, 192);
        d.Device.Set(5, original); await r.Start(); await r.Disconnect();
        Assert.Equal(original, d.Setting(5));
        var result = Assert.Single(d.LastEnd!).Result; Assert.Equal(Outcome.Confirmed, result.Outcome);
        Assert.Equal("native/asrock-usb/7", result.ReportedLighting!.EffectId); Assert.Equal(192, result.ReportedLighting.Speed);
        Assert.Equal(new RgbColor(4, 80, 23), Assert.Single(result.ReportedLighting.Colors));
        Assert.False(d.Journal.Exists);
    }
    [Fact]
    public async Task JournalFailurePreventsBothTheFirstFrameAndExitWrites()
    {
        await using var r = new Rig(5); var d = r.Driver; d.Exits[5] = new(LightingExitAction.Off);
        // A regular file at the journal directory makes preparation fail without injecting a fake save routine.
        File.WriteAllText(d.Directory, "unwritable journal directory");
        try
        {
            Assert.Equal(Outcome.Unknown, (await r.Start()).Result!.Outcome); Assert.Empty(d.Device.Writes);
            Assert.All(d.LastEnd!, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome)); Assert.True(d.Device.Disposed);
        }
        finally { File.Delete(d.Directory); }
    }
    [Fact]
    public async Task PreparingAndAbandoningWithoutAFrameIsReadOnlyAndRejectsTheLegacyWriteBypass()
    {
        var d = new Driver(5);
        try
        {
            var stream = await d.OpenLightingStreamAsync(["zone:5"], new("breathing", [new(1, 2, 3)]), default);
            await Assert.ThrowsAsync<IOException>(() => d.Transport!.WriteAsync(new(1, 2, 3)));
            var results = await stream.EndAsync(DeviceLightingStopReason.Normal, default);
            Assert.All(results, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
            Assert.Empty(d.Device.Writes); Assert.False(d.Journal.Exists); Assert.True(d.Device.Disposed);
        }
        finally { await d.DisposeAsync(); if (System.IO.Directory.Exists(d.Directory)) System.IO.Directory.Delete(d.Directory, true); }
    }
}
