using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    private static OpenRgbSdkController SharedDevice(bool direct = false)
    {
        var device = Device();
        return device with { ActiveMode = direct ? 0 : 1, Zones = device.Zones.Select(z => z with { Modes = [] }).ToArray() };
    }
    private static DeviceLightingFrame Frame(long sequence, RgbColor color) => new(sequence, color, () => new(ValidFor: TimeSpan.FromMilliseconds(500)));
    [Fact]
    public async Task ControllerOnlyDirectUsesOneWholeFrameForAllSelectedZones()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        Written(await r.Command(DeviceOperation.SetLighting, targets, Animation)); await r.Tick(.5);
        Assert.Equal(0, r.Server.Devices[41].ActiveMode); Assert.Equal(r.Server.Devices[41].Colors[0], r.Server.Devices[41].Colors[1]);
        Assert.Single(r.Server.Wire.Requests, p => p.Command == 1101);
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command is 1051 or 1103);
        var journal = new OpenRgbLightingRecovery(r.Directory, r.Driver().DeviceId, null).Pending!;
        Assert.Null(journal.Scope.Zone); Assert.All(journal.Exit.Colors, color => Assert.Equal(new RgbColor(0, 0, 0), color));
        Assert.All(r.Driver().Observe()!.Endpoints.Where(p => p.Id != "device"), p => Assert.True(p.Capabilities.Single(c => c.Feature == DeviceFeature.ColorFrames).Usable));
        Assert.All(r.Driver().DescribeSetup().Where(p => p.EndpointId != "device"), p => Assert.DoesNotContain(LightingExitAction.RestoreOriginal, p.LightingExitActions));
    }

    [Fact]
    public async Task PartialNormalStopBecomesDurableBackgroundWhileSiblingKeepsItsPhase()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        Written(await r.Command(DeviceOperation.SetLighting, [a, b], Animation)); await r.Tick(.5);
        Assert.True((await r.Command(DeviceOperation.StopLighting, [a])).Success);
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]);
        var record = new OpenRgbLightingRecovery(r.Directory, r.Driver().DeviceId, null).Pending!;
        Assert.Equal(new RgbColor(100, 50, 20), record.Exit.Colors[0]);
        await r.Tick(.5); Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]);
        Assert.NotEqual(r.Server.Devices[41].Colors[0], r.Server.Devices[41].Colors[1]);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target == b).Lighting?.Running);
        var released = await r.Driver().ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.All(released, result => Assert.Equal(Outcome.WrittenUnverified, result.Result.Outcome));
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]); Assert.Equal(new RgbColor(0, 0, 0), r.Server.Devices[41].Colors[1]);
        Assert.Equal(0, r.Server.Devices[41].ActiveMode); Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task PartialEntryCannotReplaceUnselectedNativeEffectsOrAssumePerZoneRestoration()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0))], Animation)).Success);
        Assert.Equal(0, r.Server.WriteCount);
        Rig.Configure(r.Driver(), LightingExitAction.RestoreOriginal); r.Runtime.Refresh();
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0)), r.Target(r.Zone(1))], Animation)).Success);
        Assert.Equal(0, r.Server.WriteCount); Assert.False(System.IO.Directory.Exists(r.Directory));
    }

    [Fact]
    public async Task PartialStaticColorPreservesUnselectedColorWhenControllerIsAlreadyDirect()
    {
        await using var r = await new Rig(SharedDevice(direct: true)).Open(exit: LightingExitAction.Off);
        var before = r.Server.Devices[41].Colors[1]; var target = r.Target(r.Zone(0));
        Written(await r.Command(DeviceOperation.SetLighting, [target], Blue));
        Assert.Equal(new RgbColor(0, 0, 255), r.Server.Devices[41].Colors[0]); Assert.Equal(before, r.Server.Devices[41].Colors[1]);
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command == 1101);
        Written(await r.Command(DeviceOperation.StopLighting, [target]));
        Assert.Equal(new RgbColor(0, 0, 0), r.Server.Devices[41].Colors[0]); Assert.Equal(before, r.Server.Devices[41].Colors[1]);
    }

    [Fact]
    public async Task NewZoneCanJoinWithoutRestoringTheFirstZoneOrResettingItsColor()
    {
        await using var r = await new Rig(SharedDevice(direct: true)).Open(exit: LightingExitAction.Off);
        var a = await r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await a.WriteAsync(Frame(1, new(0, 0, 255)), default)).Result.Outcome);
        var b = await r.Driver().OpenLightingStreamAsync([r.Zone(1)], Animation, default);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await b.WriteAsync(Frame(1, new(0, 255, 0)), default)).Result.Outcome);
        Assert.Equal(new RgbColor(0, 0, 255), r.Server.Devices[41].Colors[0]); Assert.Equal(new RgbColor(0, 255, 0), r.Server.Devices[41].Colors[1]);
        await a.EndAsync(DeviceLightingStopReason.Normal, default);
        await b.WriteAsync(Frame(2, new(255, 0, 0)), default);
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]); Assert.Equal(new RgbColor(255, 0, 0), r.Server.Devices[41].Colors[1]);
    }

    [Fact]
    public async Task OldControllerNativeEffectIsNotRestoredOverAStoppedZoneAfterReload()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        Written(await r.Command(DeviceOperation.SetLighting, [a, b], Animation));
        await r.Command(DeviceOperation.StopLighting, [a]);
        var snapshot = await r.Connection!.ReadAsync(new OpenRgbLightingRecovery(r.Directory, r.Driver().DeviceId, null).Pending!.Scope.Target);
        var identity = OpenRgbDeviceIdentities.Resolve([snapshot], new Dictionary<OpenRgbSdkTarget, string>())[41];
        // Model abrupt host loss: the old runtime no longer executes frames; a new driver loads only the persisted obligation.
        await using var restarted = new OpenRgbDeviceDriver(r.Driver().DeviceId, r.Directory, Boot, () => { }, selectedForControl: false);
        restarted.SetConnection(new(r.Connection, snapshot, identity));
        var results = await restarted.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(results).Result.Outcome);
        Assert.Equal(0, r.Server.Devices[41].ActiveMode); Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]);
        Assert.Equal(new RgbColor(0, 0, 0), r.Server.Devices[41].Colors[1]); Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
        r.ExpectUnresolvedShutdown = true;
    }

    [Fact]
    public async Task StopFailureKeepsNewBackgroundIntentAndFaultsOtherStreamsSharingThatController()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var stream = Assert.IsAssignableFrom<ISelectiveDeviceLightingStream>(await r.Driver().OpenLightingStreamAsync([r.Zone(0), r.Zone(1)], Animation, default));
        await stream.WriteAsync(Frame(1, new(0, 0, 255)), default); r.Server.IgnoreWriteNumber = r.Server.WriteCount + 1;
        var first = await stream.EndSelectedAsync([r.Zone(0)], default); Assert.Equal(Outcome.Unknown, Assert.Single(first).Result.Outcome);
        var intent = new OpenRgbLightingRecovery(r.Directory, r.Driver().DeviceId, null).Pending!;
        Assert.Equal(new RgbColor(100, 50, 20), intent.Exit.Colors[0]);
        r.Server.IgnoreWriteNumber = 0; Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await stream.EndSelectedAsync([r.Zone(0)], default)).Result.Outcome);
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]); Assert.Equal(new RgbColor(0, 0, 0), r.Server.Devices[41].Colors[1]);
        var count = r.Server.WriteCount; Assert.All(await stream.WriteAsync(Frame(2, new(0, 255, 0)), default), result => Assert.Equal(Outcome.Rejected, result.Result.Outcome));
        Assert.Equal(count, r.Server.WriteCount); Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task ExpiredPreparedSharedFrameRestoresPreviousIntentAndAllowsNextFrame()
    {
        await using var r = await new Rig(SharedDevice(direct: true)).Open(exit: LightingExitAction.Off);
        var stream = await r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default);
        await stream.WriteAsync(Frame(1, new(0, 0, 255)), default);
        var file = Assert.Single(System.IO.Directory.GetFiles(r.Directory)); var previous = File.ReadAllText(file); var count = r.Server.WriteCount;
        LightingFrameAuthorization Authorize() => File.ReadAllText(file) != previous ? new(Expired: true) : new(ValidFor: TimeSpan.FromMilliseconds(500));
        var result = Assert.Single(await stream.WriteAsync(new(2, new(0, 255, 0), Authorize), default));
        Assert.Equal(DeviceDriverFailure.LightingFrameExpired, result.Result.Failure); Assert.Equal(count, r.Server.WriteCount); Assert.Equal(previous, File.ReadAllText(file));
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await stream.WriteAsync(Frame(3, new(255, 0, 0)), default)).Result.Outcome);
    }

    [Fact]
    public async Task ForeignChangesToUnselectedBackgroundAreNotOverwritten()
    {
        await using var r = await new Rig(SharedDevice(direct: true)).Open(exit: LightingExitAction.Off);
        var stream = await r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default);
        await stream.WriteAsync(Frame(1, new(0, 0, 255)), default); var count = r.Server.WriteCount;
        r.Server.Devices[41].Colors[1] = new(42, 42, 42);
        Assert.Equal(Outcome.Rejected, Assert.Single(await stream.WriteAsync(Frame(2, new(0, 255, 0)), default)).Result.Outcome);
        Assert.Equal(Outcome.Unknown, Assert.Single(await stream.EndAsync(DeviceLightingStopReason.Failure, default)).Result.Outcome);
        Assert.Equal(count, r.Server.WriteCount); Assert.Equal(new RgbColor(42, 42, 42), r.Server.Devices[41].Colors[1]);
        r.ExpectUnresolvedShutdown = true;
    }

    [Fact]
    public async Task UnselectedZoneOverrideCannotBePreservedByAWholeLedPacket()
    {
        var device = SharedDevice(direct: true); var original = Device();
        device.Zones[1] = device.Zones[1] with { Modes = original.Zones[1].Modes, ActiveMode = 1 };
        await using var r = await new Rig(device).Open(exit: LightingExitAction.Off);
        Assert.NotNull(r.Driver().ValidateLightingStream([r.Zone(0)], Animation));
        Assert.Equal(0, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0)), r.Target(r.Zone(1))], Animation));
        Assert.All(r.Server.Devices[41].Zones, zone => Assert.Equal(-1, zone.ActiveMode));
    }

    [Fact]
    public async Task EndingTheRemainingGroupAfterPartialStopReturnsOnlyItsOwnedEndpoints()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        Written(await r.Command(DeviceOperation.SetLighting, [a, b], Animation));
        Assert.True((await r.Command(DeviceOperation.StopLighting, [a])).Success);
        var final = await r.Command(DeviceOperation.StopLighting, [b]);
        Assert.True(final.Success); Assert.True(Assert.Single(final.Result!.Targets).Outcome is Outcome.Confirmed or Outcome.WrittenUnverified);
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Target == a || e.Target == b), e => Assert.False(e.Owned));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
        Assert.All(r.Server.Devices[41].Colors, color => Assert.Equal(new RgbColor(100, 50, 20), color));
    }
}
