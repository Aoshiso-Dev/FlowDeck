using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    [Fact]
    public async Task FixedSelectionEntersGlobalDirectOnceWithoutStartingAnAnimation()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        var request = r.Request(DeviceOperation.SetLighting, targets, Blue with { Brightness = 50 });
        var reply = await r.Runtime.ExecuteAsync(request); Written(reply);
        Assert.Single(r.Server.Wire.Requests, p => p.Command == 1101);
        Assert.Single(r.Server.Wire.Requests, p => p.Command == 1050);
        Assert.All(r.Server.Devices[41].Colors, c => Assert.Equal(new RgbColor(0, 0, 128), c));
        Assert.All(reply.Result!.Targets, t => Assert.Equal(new RgbColor(0, 0, 128), Assert.Single(t.ReportedLighting!.Colors)));
        Assert.All(r.Runtime.State().Endpoints.Where(e => targets.Contains(e.Target)), e => { Assert.True(e.Owned); Assert.Null(e.Lighting); });
        var count = r.Server.WriteCount;
        await r.Tick(.5); Written(await r.Runtime.ExecuteAsync(request)); Assert.Equal(count, r.Server.WriteCount);
    }

    [Fact]
    public async Task AllZoneOffWorksFromNativeWithoutAHiddenColorInitialization()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        Written(await r.Command(DeviceOperation.SetLighting, targets, new("off", [])));
        Assert.Single(r.Server.Wire.Requests, p => p.Command == 1050);
        Assert.All(r.Server.Devices[41].Colors, c => Assert.Equal(new RgbColor(0, 0, 0), c));
    }

    [Fact]
    public async Task AFixedSubsetCannotChangeUnselectedNativeZonesOrOtherDevices()
    {
        await using var r = await new Rig(SharedDevice(), Device(700, "FD-UNIT-002")).Open(exit: LightingExitAction.Off);
        var first = OpenRgbLightingPlan.State(r.Server.Devices[41]); var second = OpenRgbLightingPlan.State(r.Server.Devices[700]);
        var reply = await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0)), r.Target("device", 700)], Blue);
        Assert.False(reply.Success); Assert.Equal(0, r.Server.WriteCount);
        Assert.True(OpenRgbLightingPlan.Same(first, OpenRgbLightingPlan.State(r.Server.Devices[41])));
        Assert.True(OpenRgbLightingPlan.Same(second, OpenRgbLightingPlan.State(r.Server.Devices[700])));
    }

    [Fact]
    public async Task FixedBatchRejectsOverlappingWholeAndZonesBeforeWriting()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target("device"), r.Target(r.Zone(0))], Blue)).Success);
        Assert.Equal(0, r.Server.WriteCount); Assert.False(System.IO.Directory.Exists(r.Directory));
    }

    private static async Task<DeviceServiceRequest> FixedScene(Rig r)
    {
        var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        var scene = new DeviceLightingScene("colors", "異なる固定色", [new(a, Blue), new(b, new("fixed", [new(200, 100, 0)], Brightness: 50))], []);
        var saved = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.LightingLibrary, []) with { Targets = null, TopologyRevision = 0,
            LightingLibrary = new(LightingLibraryOperation.SaveScene, r.Authority!.Generation.Id, "colors", 0, Scene: scene) });
        Assert.True(saved.Success, saved.Error);
        return r.Request(DeviceOperation.ApplyLightingPreset, [a, b]) with {
            LightingPreset = new(LightingPresetKind.Scene, r.Authority.Generation.Id, "colors", 1) };
    }

    [Fact]
    public async Task FixedSceneBatchesDifferentColorsAndPersistsTheUnscaledSelections()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off, withConfiguration: true);
        Written(await r.Runtime.ExecuteAsync(await FixedScene(r)));
        Assert.Equal(new RgbColor(0, 0, 255), r.Server.Devices[41].Colors[0]); Assert.Equal(new RgbColor(100, 50, 0), r.Server.Devices[41].Colors[1]);
        Assert.Single(r.Server.Wire.Requests, p => p.Command == 1050);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.Null(e.Lighting));
        var preference = r.Authority!.Configuration.Lighting.Preferences!.Single(p => p.Target == r.Target(r.Zone(1)));
        Assert.Equal(50, preference.Program.Brightness); Assert.Equal(new RgbColor(200, 100, 0), preference.Program.Colors[0]);
        var count = r.Server.WriteCount; await r.Tick(.5); Assert.Equal(count, r.Server.WriteCount);
    }

    [Fact]
    public async Task ColdBrightnessBatchesSavedFixedZonesFromNativeAndKeepsTheirDistinctColors()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off, withConfiguration: true);
        Written(await r.Runtime.ExecuteAsync(await FixedScene(r)));
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        await r.Command(DeviceOperation.StopLighting, targets);
        r.Server.Devices[41] = r.Server.Devices[41] with { ActiveMode = 1 }; await r.Runtime.PollAsync();
        var frames = r.Server.Wire.Requests.Count(p => p.Command == 1050);
        var adjusted = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.AdjustLighting, targets) with {
            LightingAdjustment = new(LightingAdjustmentKind.Brightness, LightingAdjustmentScope.Targets, r.Authority!.Generation.Id, 50) });
        Written(adjusted); Assert.Equal(frames + 1, r.Server.Wire.Requests.Count(p => p.Command == 1050));
        Assert.Equal(new RgbColor(0, 0, 128), r.Server.Devices[41].Colors[0]); Assert.Equal(new RgbColor(50, 25, 0), r.Server.Devices[41].Colors[1]);
        Assert.All(adjusted.Result!.Targets, t => Assert.Equal(DevicePolicySaveState.Saved, t.Persistence!.State));
    }

    [Fact]
    public async Task FinalSharedStopReportsEachAcceptedColorToRuntimeInsteadOfTheLastFrame()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        Written(await r.Command(DeviceOperation.SetLighting, targets, Animation)); await r.Tick(.4);
        var reply = await r.Command(DeviceOperation.StopLighting, targets); Written(reply);
        Assert.All(reply.Result!.Targets, t => Assert.Equal(new RgbColor(100, 50, 20), Assert.Single(t.ReportedLighting!.Colors)));
        Assert.All(r.Runtime.State().Endpoints.Where(e => targets.Contains(e.Target)), e =>
        {
            Assert.False(e.Owned); Assert.False(e.Lighting!.Running);
            Assert.Equal(new RgbColor(100, 50, 20), e.Lighting.LastColor);
            Assert.Equal(e.Lighting.LastColor, Assert.Single(e.ReportedLighting!.Colors));
        });
    }

    [Fact]
    public async Task SharedServiceExitReportsConfiguredOffAndFailureDoesNotInventAReport()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var driver = r.Driver();
        var stream = await driver.OpenLightingStreamAsync([r.Zone(0), r.Zone(1)], Animation, default);
        await stream.WriteAsync(Frame(1, new(200, 100, 50)), default);
        r.Server.IgnoreWriteNumber = r.Server.WriteCount + 1;
        var failed = await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.All(failed, item => { Assert.Equal(Outcome.Unknown, item.Result.Outcome); Assert.Null(item.Result.ReportedLighting); });
        r.Server.IgnoreWriteNumber = 0;
        var retry = await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.All(retry, item => { Assert.Equal(Outcome.WrittenUnverified, item.Result.Outcome); Assert.Equal(new RgbColor(0, 0, 0), Assert.Single(item.Result.ReportedLighting!.Colors)); });
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task IgnoredFixedBatchRetainsRecoveryUntilBothZonesReachTheirExit()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        r.Server.IgnoreWriteNumber = 2;
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        var reply = await r.Command(DeviceOperation.SetLighting, targets, Blue);
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome); Assert.All(reply.Result.Targets, t => Assert.Null(t.ReportedLighting));
        Assert.Single(System.IO.Directory.GetFiles(r.Directory));
        r.Server.IgnoreWriteNumber = 0;
        var recovered = await r.Driver().ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.Equal(2, recovered.Length); Assert.All(recovered, p => Assert.Equal(Outcome.WrittenUnverified, p.Result.Outcome));
        Assert.All(r.Server.Devices[41].Colors, c => Assert.Equal(new RgbColor(0, 0, 0), c)); Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task ANewPolicyBetweenModeAndColorPreventsTheOldBatchFromSendingItsColor()
    {
        await using var r = await new Rig(SharedDevice()).Open(exit: LightingExitAction.Off);
        var targets = new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) };
        Task<DeviceServiceReply>? replacement = null;
        r.Server.BeforeWrite = _ =>
        {
            if (r.Server.WriteCount == 1) replacement = r.Command(DeviceOperation.SetLighting, targets, Blue with { Brightness = 25 });
            return Task.CompletedTask;
        };
        var old = await r.Command(DeviceOperation.SetLighting, targets, Blue);
        Assert.Equal(Outcome.Unknown, old.Result!.Outcome); Assert.NotNull(replacement);
        var next = await replacement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(Outcome.Rejected, next.Result!.Outcome);
        Assert.Equal(1, r.Server.WriteCount); Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command == 1050);
        Assert.Single(System.IO.Directory.GetFiles(r.Directory));
        r.Server.BeforeWrite = null;
        Assert.All(await r.Driver().ReleaseAsync(DeviceReleaseReason.ServiceStopping, default), p => Assert.Equal(Outcome.WrittenUnverified, p.Result.Outcome));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }
}
