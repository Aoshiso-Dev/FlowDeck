using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed partial class EneMemoryDeviceDriverTests
{
    [Fact] public async Task SetupDescriptorsRequireEightLedsAndSupportOriginalRestoreWithoutWrites()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(configure: false);
        var value = Assert.Single(r.Driver().DescribeSetup());
        Assert.Equal(8,value.MinimumLeds); Assert.Equal(8,value.MaximumLeds); Assert.Equal(8,value.DetectedLeds);
        Assert.Contains(LightingExitAction.RestoreOriginal,value.LightingExitActions); Assert.Empty(rack.Writes);
    }
    private const string Installation = "af34df7628514347b61bebae979da711", Boot = "recording-boot", Bus = "recording-bus", Endpoint = "zone:0";
    private static string Id(byte address) => DeviceIdentity.Create(Installation, "ene-memory", "location:" + SettingsTransfer.Hash(new { Bus, Address = address }));
    private static EneMemoryState Snapshot(Rack rack, byte address = 0x71) => new(
        Enumerable.Range(0x8020, 4).Select(r => rack.Registers[(address, (ushort)r)]).ToArray(),
        Enumerable.Range(0x8160, 24).Select(r => rack.Registers[(address, (ushort)r)]).ToArray(),
        Enumerable.Range(0x8100, 24).Select(r => rack.Registers[(address, (ushort)r)]).ToArray());
    private sealed class Rig : IAsyncDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Ene-Driver-" + Guid.NewGuid().ToString("N"));
        public readonly TestClock Clock = new();
        public readonly Rack Rack;
        public readonly EneMemoryDeviceBackend Backend;
        public readonly EneMemoryDeviceProvider Provider;
        public readonly DeviceControlRuntime Runtime;
        private DeviceServiceHello hello = null!;
        public string Owner => hello.Session;
        private long sequence;
        public Rig(Rack rack, string boot = Boot)
        {
            Rack = rack; Backend = new([rack.Transport()], Clock); Provider = new(Installation, Directory, boot, Backend);
            Runtime = new([], ["user"], new TestLog(), Clock, providers: [Provider]);
        }
        public EneMemoryDeviceDriver Driver(byte address = 0x71) => Assert.IsType<EneMemoryDeviceDriver>(Provider.CachedDrivers.Single(d => d.DeviceId == Id(address)));
        public EneMemoryRecoveryRecord? Pending(byte address = 0x71) => new EneMemoryRecovery(Directory, Id(address)).Pending;
        public async Task<Rig> Open(bool configure = true)
        {
            await Runtime.PollAsync(); await Runtime.PollAsync();
            if (configure) foreach (var address in Rack.Present) Configure(address);
            Runtime.Refresh(); hello = Runtime.OpenSession("user"); return this;
        }
        public void Configure(byte address = 0x71, int count = 8, LightingExitAction exit = LightingExitAction.Off, RgbColor? exitColor = null)
        {
            var d = Driver(address);
            d.ConfigureLighting(d.BindingCandidate, [new(new(d.DeviceId, Endpoint), true, new(exit, exitColor))],
                [new(Endpoint, "RGB", EndpointKind.Lighting, Setup: new(LedCount: count, LedCountConfirmed: true))]);
        }
        public Task<DeviceServiceReply> Command(DeviceOperation operation, byte[] addresses, LightingProgram? program = null)
        {
            var state = Runtime.State(); var versions = addresses.Select(a => state.Endpoints.Single(e => e.Target.DeviceId == Id(a)))
                .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            return Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                operation == DeviceOperation.ReleaseSession ? 0 : state.Inventory.TopologyRevision,
                operation == DeviceOperation.ReleaseSession ? null : versions, Lighting: program)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        public Task<DeviceServiceReply> Start(params byte[] addresses) => Command(DeviceOperation.SetLighting, addresses,
            new("color-cycle", [new(100, 50, 20)], 4, Brightness: 50));
        public Task Tick(double seconds) { Clock.Advance(seconds); return Runtime.TickLightingAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        public async Task Abandoned(bool direct)
        {
            var c = (await Backend.DiscoverAsync(default))[0]; var scope = new EneMemoryRecoveryScope(EneMemoryRecovery.BindingFor(Id(c.Address), c), c, Guid.NewGuid().ToString("N"));
            await Backend.ExecuteAsync(c, true, session =>
            {
                var original = EneMemoryProtocol.Read(session); var plan = direct ? EneMemoryProtocol.Frame(original, new(20, 40, 60)) :
                    EneMemoryProtocol.Native(original, EneMemoryProtocol.NativeTarget(original, new(1, 20, 40, 60)));
                var record = new EneMemoryRecovery(Directory, Id(c.Address)); var prepared = record.Prepare(Boot, scope, plan, original);
                EneMemoryProtocol.ExecutePrepared(session, plan, () => record.CheckCurrent(prepared)); return true;
            }, default);
        }
        public async ValueTask DisposeAsync()
        {
            Rack.BeforeData = null; Rack.FailWrite = false; Rack.FailRead = false;
            await Runtime.DisposeAsync(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
    [Fact]
    public async Task DiscoveryExposesIndependentLightingOnlyMemoriesAndWritesNothing()
    {
        var rack = new Rack(0x71, 0x73); await using var r = await new Rig(rack).Open();
        Assert.Empty(rack.Writes); var devices = r.Runtime.State().Inventory.Devices; Assert.Equal(2, devices.Length);
        Assert.All(devices, d =>
        {
            Assert.Equal(DeviceKind.Memory, d.Device.Kind); Assert.Equal(IdentityConfidence.ConnectionBound, d.Device.IdentityConfidence);
            var endpoint = Assert.Single(d.Device.Endpoints); Assert.Equal(EndpointKind.Lighting, endpoint.Kind);
            Assert.All(endpoint.Capabilities, c => Assert.Equal(VerificationEvidence.Automated, c.Evidence));
            Assert.DoesNotContain(endpoint.Capabilities, c => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm);
            Assert.Contains("smbus", d.Device.ResourceDomains);
        });
        Assert.NotEqual(r.Driver().BindingCandidate, r.Driver(0x73).BindingCandidate);
        Assert.Empty(r.Driver().LegacyReferences());
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(14)]
    public async Task AllPackagedNativeModesReadBackAndLeaveTheOtherDimmUntouched(int mode)
    {
        var rack = new Rack(0x71, 0x73); await using var r = await new Rig(rack).Open(); var other = Snapshot(rack, 0x73);
        var effect = MemoryLighting.Effects.Single(e => e.Id == mode);
        var reply = await r.Command(DeviceOperation.SetLighting, [0x71], new("native/ene-memory/" + mode, effect.Color ? [new(11, 22, 33)] : [], 128));
        Assert.Equal(Outcome.Confirmed, reply.Result!.Outcome); Assert.Equal(mode == 14 ? 5 : mode, Snapshot(rack).Control[1]);
        Assert.Equal(mode <= 1 ? 4 : 2, Snapshot(rack).Control[2]); Assert.True(EneMemoryProtocol.Same(other, Snapshot(rack, 0x73)));
        Assert.All(rack.Writes, w => Assert.Equal(0x71, w.Address)); Assert.NotNull(r.Pending()); Assert.Throws<IOException>(() => r.Configure());
    }
    [Fact]
    public async Task FixedPerLedColorsScaleOnceAndRestoreTheExactOriginalPaletteAndSpeed()
    {
        var rack = new Rack(0x71); rack.Registers[(0x71, 0x8167)] = 139; rack.Registers[(0x71, 0x8105)] = 117;
        await using var r = await new Rig(rack).Open(); r.Configure(exit: LightingExitAction.RestoreOriginal); var original = Snapshot(rack);
        var palette = Enumerable.Range(0, 8).Select(i => new RgbColor(20 + i * 2, 40 + i * 2, 60 + i * 2)).ToArray();
        var reply = await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(255, 255, 255)], Brightness: 50, LedColors: palette));
        Assert.Equal(Outcome.Confirmed, reply.Result!.Outcome); var reported = reply.Result.Targets.Single().ReportedLighting!;
        Assert.Equal(palette.Select(c => c.Scale(.5)), reported.LedColors!); Assert.Equal(new byte[] { 10, 30, 20 }, Snapshot(rack).NativeColors[..3]);
        await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(4, 5, 6)]));
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome);
        Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack))); Assert.Null(r.Pending());
    }
    [Fact]
    public async Task SetupAndUnsupportedProgramsAreRejectedBeforeAnyOutput()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(configure: false);
        Assert.False((await r.Start(0x71)).Success); r.Configure(count: 7); r.Runtime.Refresh(); Assert.False((await r.Start(0x71)).Success);
        r.Configure(); r.Runtime.Refresh();
        foreach (var program in new LightingProgram[] { new("native/ene-memory/5", [new(1, 2, 3)]), new("native/ene-memory/4", [], Brightness: 50),
            new("fixed", [new(1, 2, 3)], Reverse: true), new("fixed", [new(1, 2, 3)], LedColors: [new(1, 2, 3)]) })
            Assert.False((await r.Command(DeviceOperation.SetLighting, [0x71], program)).Success);
        Assert.Empty(rack.Writes); Assert.Null(r.Pending());
    }
    [Fact]
    public async Task NativeCanEnterCommonSynchronizationAndNormalStopUsesTheBaseColorOnBothDimms()
    {
        var rack = new Rack(0x71, 0x73); await using var r = await new Rig(rack).Open(); var originals = new[] { Snapshot(rack), Snapshot(rack, 0x73) };
        await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(7, 8, 9)]));
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start(0x71, 0x73)).Result!.Outcome); await r.Tick(.5);
        Assert.Equal(new byte[] { 25, 0, 50 }, Snapshot(rack).DirectColors[..3]);
        Assert.Equal(Snapshot(rack).DirectColors, Snapshot(rack, 0x73).DirectColors);
        Assert.True(EneMemoryProtocol.Same(originals[0], r.Pending()!.Original));
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [0x71, 0x73])).Result!.Outcome);
        foreach (var a in new byte[] { 0x71, 0x73 })
        {
            Assert.Equal(0, Snapshot(rack, a).Control[0]); Assert.Equal(new byte[] { 50, 10, 25 }, Snapshot(rack, a).NativeColors[..3]); Assert.Null(r.Pending(a));
        }
    }
    [Fact]
    public async Task StoppingOneDimmPreservesTheOtherDimmPhaseAndRecoveryRecord()
    {
        var rack = new Rack(0x71, 0x73); await using var r = await new Rig(rack).Open();
        await r.Start(0x71, 0x73); await r.Tick(.4);
        var otherRecord = r.Pending(0x73); var reply = await r.Command(DeviceOperation.StopLighting, [0x71]);
        Assert.True(reply.Result?.Outcome == Outcome.Confirmed, System.Text.Json.JsonSerializer.Serialize(reply, Contract.Json));
        Assert.Null(r.Pending()); Assert.Equal(0, Snapshot(rack).Control[0]);
        Assert.Equal(new byte[] { 50, 10, 25 }, Snapshot(rack).NativeColors[..3]);
        Assert.Equal(SettingsTransfer.Hash(otherRecord), SettingsTransfer.Hash(r.Pending(0x73)));
        var stopped = Snapshot(rack); await r.Tick(.3);
        Assert.True(EneMemoryProtocol.Same(stopped, Snapshot(rack)));
        var expected = DeviceLighting.Frame(new("color-cycle", [new(100, 50, 20)], 4, Brightness: 50), .7);
        Assert.Equal(new byte[] { (byte)expected.R, (byte)expected.B, (byte)expected.G }, Snapshot(rack, 0x73).DirectColors[..3]);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == Id(0x73)).Lighting!.Running);
    }
    [Fact]
    public async Task EndpointFramesUseTheSelectedDimmColorAndKeepTheLastAcceptedNormalStopColor()
    {
        var rack = new Rack(0x71, 0x73); await using var r = await new Rig(rack).Open(); var other = Snapshot(rack, 0x73);
        var stream = await r.Driver().OpenLightingStreamAsync([Endpoint], new("breathing", [new(100, 50, 20)]), default);
        Assert.True(r.Driver().LightingLimits.EndpointColors);
        var frame = new DeviceLightingFrame(1, new(255, 0, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500)),
            [new(Endpoint, new(10, 20, 30), new(40, 50, 60))]);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await stream.WriteAsync(frame, default)).Result.Outcome);
        Assert.Equal(new byte[] { 10, 30, 20 }, Snapshot(rack).DirectColors[..3]);
        var before = rack.Writes.Count;
        var wrong = new DeviceLightingFrame(2, new(0, 0, 0), () => new(ValidFor: TimeSpan.FromMilliseconds(500)), [new("zone:1", new(0, 0, 0), new(0, 0, 0))]);
        Assert.Equal(Outcome.Rejected, Assert.Single(await stream.WriteAsync(wrong, default)).Result.Outcome); Assert.Equal(before, rack.Writes.Count);
        var expired = new DeviceLightingFrame(3, new(0, 0, 0), () => new("expired", true), [new(Endpoint, new(0, 0, 0), new(0, 0, 0))]);
        Assert.Equal(Outcome.Rejected, Assert.Single(await stream.WriteAsync(expired, default)).Result.Outcome); Assert.Equal(before, rack.Writes.Count);
        Assert.Equal(Outcome.Confirmed, Assert.Single(await stream.EndAsync(DeviceLightingStopReason.Normal, default)).Result.Outcome);
        Assert.Equal(new byte[] { 40, 60, 50 }, Snapshot(rack).NativeColors[..3]); Assert.True(EneMemoryProtocol.Same(other, Snapshot(rack, 0x73))); Assert.Null(r.Pending());
    }
    [Fact]
    public async Task OpeningAndCancellingAnUnstartedStreamDoesNotWriteUnlessItAlreadyOwnsNativeOutput()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open();
        var stream = await r.Driver().OpenLightingStreamAsync([Endpoint], new("breathing", [new(100, 50, 20)]), default);
        Assert.Empty(rack.Writes); Assert.Null(r.Pending()); await stream.EndAsync(DeviceLightingStopReason.Normal, default); Assert.Empty(rack.Writes);
        await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(11, 22, 33)])); var boundary = rack.Writes.Count;
        stream = await r.Driver().OpenLightingStreamAsync([Endpoint], new("breathing", [new(100, 50, 20)]), default);
        Assert.Equal(boundary, rack.Writes.Count); await stream.EndAsync(DeviceLightingStopReason.Failure, default);
        Assert.Equal(0, Snapshot(rack).Control[1]); Assert.Null(r.Pending());
    }
    [Fact]
    public async Task FrameExpiryBeforeAnyWriteDropsOnlyThatFrameAndAValidFrameContinues()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); await r.Start(0x71); var previous = r.Pending()!; var boundary = rack.Writes.Count;
        rack.BeforeData = (_, write) => { if (write) { rack.BeforeData = null; r.Clock.Advance(.6); } };
        await r.Tick(.1); Assert.Equal(boundary, rack.Writes.Count); Assert.Equal(previous.Hash, r.Pending()!.Hash);
        Assert.Equal(1, r.Runtime.State().Endpoints.Single().Lighting!.DroppedFrames); await r.Tick(.1);
        Assert.True(rack.Writes.Count > boundary); Assert.True(r.Runtime.State().Endpoints.Single().Lighting!.Running);
    }
    [Fact]
    public async Task MidFrameExpiryStopsAndRestoresInsteadOfTreatingAPartialFrameAsUnsent()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); r.Configure(exit: LightingExitAction.RestoreOriginal); var original = Snapshot(rack);
        await r.Start(0x71);
        rack.BeforeData = (register, write) => { if (write && register == 0x8103) { rack.BeforeData = null; r.Clock.Advance(.6); } };
        await r.Tick(.1); Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack))); Assert.Null(r.Pending());
        Assert.False(r.Runtime.State().Endpoints.Single().Lighting!.Running);
    }
    [Fact]
    public async Task CancellationDuringNativeWriteRetainsRecoveryAndStopRetriesOnTheSameConnection()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); r.Configure(exit: LightingExitAction.RestoreOriginal); var original = Snapshot(rack);
        using var cancellation = new CancellationTokenSource();
        rack.BeforeData = (register, write) => { if (write && register == 0x8163) cancellation.Cancel(); };
        var result = await r.Driver().ApplyAsync(new(Endpoint, DeviceOperation.SetLighting, Lighting: new("fixed", [new(11, 22, 33)])), cancellation.Token);
        Assert.Equal(Outcome.Unknown, result.Outcome); Assert.NotNull(r.Pending()); Assert.Equal(3, rack.Writes.Count);
        rack.BeforeData = null; Assert.Equal(Outcome.Confirmed, (await r.Driver().ApplyAsync(new(Endpoint, DeviceOperation.StopLighting), default)).Outcome);
        Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack))); Assert.Null(r.Pending());
    }
    [Fact]
    public async Task ForeignNativeChangeBlocksAFrameAndIsNeverOverwrittenByRecovery()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); await r.Start(0x71); var boundary = rack.Writes.Count;
        rack.Registers[(0x71, 0x8163)] = 177; await r.Tick(.1);
        Assert.Equal(boundary, rack.Writes.Count); Assert.NotNull(r.Pending()); Assert.False(r.Runtime.State().Endpoints.Single().Lighting!.Running);
        Assert.True(r.Runtime.State().Endpoints.Single().Owned);
    }
    [Fact]
    public async Task SameAddressAfterInvalidationDoesNotReceiveAnOldFrameOrExit()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); await r.Start(0x71);
        var before = r.Driver().BindingCandidate; var boundary = rack.Writes.Count;
        r.Backend.Invalidate(); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Tick(.1);
        Assert.NotEqual(before, r.Driver().BindingCandidate); Assert.Equal(boundary, rack.Writes.Count); Assert.NotNull(r.Pending());
        Assert.Throws<IOException>(() => r.Configure()); Assert.True(r.Runtime.State().Endpoints.Single().Owned);
    }
    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(true, true, false)] [InlineData(true, false, true)]
    public async Task ColdRecoveryRequiresTheSameConnectionAndWindowsBoot(bool direct, bool reconnect, bool changedBoot)
    {
        var rack = new Rack(0x71); rack.Registers[(0x71, 0x8165)] = 73;
        await using var r = new Rig(rack, changedBoot ? "another-boot" : Boot); var original = Snapshot(rack);
        await r.Abandoned(direct); var boundary = rack.Writes.Count; if (reconnect) r.Backend.Invalidate();
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        var recovered = !reconnect && !changedBoot; Assert.Equal(recovered, r.Runtime.State().LifecycleError is null);
        Assert.Equal(!recovered, r.Pending() is not null);
        if (recovered) Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack))); else Assert.Equal(boundary, rack.Writes.Count);
    }
    [Fact]
    public async Task ServiceStopUsesTheConfiguredExitColorAndDirectSetupDoesNotInventASlotName()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); r.Configure(exit: LightingExitAction.FixedColor, exitColor: new(13, 27, 39));
        await r.Start(0x71); var released = await r.Driver().ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.Equal(Outcome.Confirmed, released.Single().Result.Outcome); Assert.Equal(new byte[] { 13, 39, 27 }, Snapshot(rack).NativeColors[..3]);
        Assert.Null(r.Pending()); Assert.Equal("RGB", r.Driver().Observe()!.Endpoints.Single().Name);
    }
    [Fact]
    public async Task LiveFrameUsesOnePreflightAndDoesNotClaimReadbackVerification()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); await r.Start(0x71);
        var reads = 0; var writes = 0; rack.BeforeData = (_, write) => { if (write) writes++; else reads++; };
        await r.Tick(.1); rack.BeforeData = null;
        Assert.Equal(52, reads); Assert.Equal(8, writes);
        Assert.Equal(Outcome.WrittenUnverified, r.Runtime.State().Endpoints.Single().Outcome);
    }
    [Fact]
    public async Task PeriodicDiscoveryRefreshesTheProofWithoutLosingTheLiveBinding()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); var binding = r.Driver().BindingCandidate; await r.Start(0x71);
        for (var i = 0; i < 4; i++)
        {
            r.Clock.Advance(1.3); await r.Runtime.PollAsync(); await r.Tick(.1);
            Assert.Equal(binding, r.Driver().BindingCandidate); Assert.True(r.Runtime.State().Endpoints.Single().Lighting!.Running);
        }
        Assert.Equal(5, r.Runtime.State().Endpoints.Single().Lighting!.Frames);
    }
    [Fact]
    public async Task TransportFailureRetainsOnlyTheFailedDimmAndRestoresTheOtherGroupMember()
    {
        var rack = new Rack(0x71, 0x73); await using var r = await new Rig(rack).Open(); await r.Start(0x71, 0x73);
        rack.BeforeData = (_, write) =>
        {
            if (!write) return; rack.BeforeData = null; rack.FailWrite = true;
            rack.OnOpen = () => { rack.OnOpen = null; rack.FailWrite = false; };
        };
        await r.Tick(.1);
        var failed = r.Pending() is not null ? (byte)0x71 : (byte)0x73; var restored = failed == 0x71 ? (byte)0x73 : (byte)0x71;
        Assert.NotNull(r.Pending(failed)); Assert.Null(r.Pending(restored)); Assert.Equal(0, Snapshot(rack, restored).Control[1]);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == Id(failed)).Owned);
        Assert.False(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == Id(restored)).Owned);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Lighting!.Running));
    }
    [Fact]
    public async Task AJournalCannotBeChangedDuringPreflightAndTheVerifiedOwnerCanContinue()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); await r.Start(0x71); var boundary = rack.Writes.Count;
        var path = Assert.Single(Directory.GetFiles(r.Directory, "*.json")); var rejected = false;
        rack.BeforeData = (register, write) =>
        {
            if (write || register != 0x8160) return; rack.BeforeData = null;
            var record = r.Pending()!; var error = Record.Exception(() => ServiceEnvironment.Write(path, record with { Boot = "foreign-boot" }));
            Assert.True(error is IOException or UnauthorizedAccessException); rejected = true;
        };
        await r.Tick(.1); Assert.True(rejected); Assert.True(rack.Writes.Count > boundary); Assert.Equal(Boot, r.Pending()!.Boot);
        Assert.Equal(Outcome.WrittenUnverified, r.Runtime.State().Endpoints.Single().Outcome); Assert.True(r.Runtime.State().Endpoints.Single().Owned);
    }
    [Fact]
    public async Task NativeReplacementStopsTheOldGroupBeforeItsOwnWrite()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open(); await r.Start(0x71);
        var result = await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(12, 34, 56)]));
        Assert.Equal(Outcome.Confirmed, result.Result!.Outcome); Assert.Equal(new byte[] { 12, 56, 34 }, Snapshot(rack).NativeColors[..3]);
        var boundary = rack.Writes.Count; await r.Tick(.1); Assert.Equal(boundary, rack.Writes.Count);
    }
    [Fact]
    public async Task AFailedJournalUpdatePreventsNativeOutputAndPreservesThePreviousOwner()
    {
        var rack = new Rack(0x71); await using var r = await new Rig(rack).Open();
        await r.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(11, 22, 33)])); var saved = r.Pending()!; var boundary = rack.Writes.Count;
        using (var locked = new FileStream(Assert.Single(Directory.GetFiles(r.Directory, "*.json")), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await r.Driver().ApplyAsync(new(Endpoint, DeviceOperation.SetLighting, Lighting: new("fixed", [new(44, 55, 66)])), default);
            Assert.Equal(Outcome.Rejected, result.Outcome); Assert.Equal(boundary, rack.Writes.Count);
        }
        Assert.Equal(saved.Hash, r.Pending()!.Hash); Assert.Equal(new byte[] { 11, 33, 22 }, Snapshot(rack).NativeColors[..3]);
        Assert.Equal(Outcome.Confirmed, (await r.Driver().ApplyAsync(new(Endpoint, DeviceOperation.StopLighting), default)).Outcome);
    }
    [Fact]
    public async Task MultipleBusesRemainDistinctAndOnlyTheKnownAmdBusOffersLegacyAliases()
    {
        var a = new Rack(0x71); var b = new Rack(0x71); const string amd = "amd-1022-790b-0b00-p0";
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Ene-Multi-" + Guid.NewGuid().ToString("N"));
        await using var provider = new EneMemoryDeviceProvider(Installation, directory, Boot, new EneMemoryDeviceBackend([a.Transport(amd), b.Transport("another-bus")]));
        await provider.PollAsync(default); var drivers = provider.CachedDrivers.Cast<EneMemoryDeviceDriver>().ToArray(); Assert.Equal(2, drivers.Length);
        foreach (var driver in drivers) await driver.PollAsync(default);
        Assert.Equal(2, drivers.Select(d => d.DeviceId).Distinct().Count()); Assert.All(drivers, d => Assert.NotNull(d.BindingCandidate));
        var alias = Assert.Single(drivers.SelectMany(d => d.LegacyReferences())); Assert.Equal("rgb", alias.Source);
        Assert.Equal(MemoryLighting.Identity(0x71), alias.LegacyDeviceId); Assert.Equal(Endpoint, alias.LegacyEndpointId);
        Assert.Empty(a.Writes); Assert.Empty(b.Writes); Assert.False(Directory.Exists(directory));
    }
}
