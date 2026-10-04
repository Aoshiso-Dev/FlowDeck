using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Fact]
    public async Task ExistingAsrockControllersJoinWithoutLeavingDirectOrResettingUnselectedZones()
    {
        var a = new Cell(1, false); var b = new Cell(2, false); await using var r = await new Rig(new(a, b)).Open();
        await r.Start((a, 5), (a, 6)); await r.Tick(.4); await r.Start((b, 5), (b, 6)); await r.Tick(.3);
        var aBoundary = a.Device.Writes.Count; var bBoundary = b.Device.Writes.Count;
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5), (b, 5))).Result!.Outcome);
        var state = r.Runtime.State(); var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 50);
        RgbColor Color(Cell cell, byte zone) => state.Endpoints.Single(e => e.Target == new EndpointReference(cell.Id, "zone:" + zone)).ReportedLighting!.Colors[0];
        Assert.Equal(DeviceLighting.Frame(program, 0), Color(a, 5)); Assert.Equal(Color(a, 5), Color(b, 5));
        Assert.Equal(DeviceLighting.Frame(program, .7), Color(a, 6)); Assert.Equal(DeviceLighting.Frame(program, .3), Color(b, 6));
        Assert.Single(state.Endpoints.Where(e => e.Lighting?.Running == true).Select(e => e.Lighting!.GroupId).Distinct());
        Assert.All(a.Device.Writes.Skip(aBoundary).Concat(b.Device.Writes.Skip(bBoundary)), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); });
    }
    [Fact]
    public async Task AddingAnAsrockZoneKeepsTheExistingDirectStreamAndUnselectedClock()
    {
        var cell = new Cell(1, false); await using var r = await new Rig(new(cell)).Open(); await r.Start((cell, 5), (cell, 6));
        await r.Tick(.4); var before = cell.Device.Writes.Count;
        var saved = new AsrockBoundRecovery(r.Directory, cell.Id).Pending!;
        var reply = await r.Start((cell, 5), (cell, 2)); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
        Assert.All(cell.Device.Writes.Skip(before), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); });
        var after = new AsrockBoundRecovery(r.Directory, cell.Id).Pending!;
        Assert.Equal(saved.Scope, after.Scope); Assert.Equal(SettingsTransfer.Hash(saved.Snapshot), SettingsTransfer.Hash(after.Snapshot));
        Assert.Equal(new byte[] { 2, 5, 6 }, after.Targets.Order());
        var state = r.Runtime.State(); var program = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 50);
        Assert.Equal(DeviceLighting.Frame(program, .4), state.Endpoints.Single(e => e.Target.EndpointId == "zone:6").ReportedLighting!.Colors[0]);
        Assert.All(state.Endpoints.Where(e => e.Target.EndpointId is "zone:5" or "zone:2"), e => Assert.Equal(DeviceLighting.Frame(program, 0), e.ReportedLighting!.Colors[0]));
    }
    [Fact]
    public async Task ReplacingOneLiveAsrockZoneEffectKeepsDirectModeAndTheOtherZoneClock()
    {
        var cell = new Cell(1, false); await using var r = await new Rig(new(cell)).Open();
        var outside = Setting(cell, 2); await r.Start((cell, 5), (cell, 6)); await r.Tick(.4);
        var boundary = cell.Device.Writes.Count;
        var group = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Lighting!.GroupId;
        var pulse = new LightingProgram("breathing", [new(30, 80, 200)], 4);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Command(DeviceOperation.SetLighting, [(cell, 5)], pulse)).Result!.Outcome);
        var original = new LightingProgram("color-cycle", [new(100, 50, 20)], 4, Brightness: 50);
        var state = r.Runtime.State();
        Assert.Equal(DeviceLighting.Frame(pulse, 0), state.Endpoints.Single(e => e.Target.EndpointId == "zone:5").ReportedLighting!.Colors[0]);
        Assert.Equal(DeviceLighting.Frame(original, .4), state.Endpoints.Single(e => e.Target.EndpointId == "zone:6").ReportedLighting!.Colors[0]);
        await r.Tick(.5); state = r.Runtime.State();
        Assert.Equal(DeviceLighting.Frame(pulse, .5), state.Endpoints.Single(e => e.Target.EndpointId == "zone:5").ReportedLighting!.Colors[0]);
        Assert.Equal(DeviceLighting.Frame(original, .9), state.Endpoints.Single(e => e.Target.EndpointId == "zone:6").ReportedLighting!.Colors[0]);
        Assert.All(cell.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); Assert.True(p[4] is 0xe3 or 0xe4); });
        Assert.All(state.Endpoints.Where(e => e.Lighting?.Running == true), e => Assert.Equal(group, e.Lighting!.GroupId));
        await r.Command(DeviceOperation.StopLighting, [(cell, 5), (cell, 6)]);
        Assert.Equal(new BoardLightSetting(1, 30, 80, 200), Setting(cell, 5)); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(cell, 6));
        Assert.Equal(outside, Setting(cell, 2));
    }
    [Fact]
    public async Task CompiledAsrockCompositionPreservesOutsideZonesAndPerZoneStopColors()
    {
        var cell = new Cell(1, false); cell.Device.Available |= 0x80; cell.Device.Counts[7] = 4; cell.Device.Set(7, new(1, 9, 8, 7));
        await using var r = await new Rig(new(cell)).Open();
        var outside = Setting(cell, 2); var driver = (IDeviceLightingCompositionDriver)r.Driver(cell);
        var red = new LightingProgram("fixed", [new(120, 10, 20)], Brightness: 50);
        var pulse = new LightingProgram("breathing", [new(10, 100, 240)], 4);
        EndpointLightingProgram[] programs = [new("zone:5", red), new("zone:6", pulse), new("zone:7", new("off", []))];
        Assert.Null(driver.ValidateLightingComposition(programs)); Assert.Empty(cell.Device.Writes);
        var stream = await driver.OpenLightingCompositionAsync(programs, default);
        Assert.Empty(cell.Device.Writes);
        var colors = programs.Select(p => new EndpointLightingFrame(p.EndpointId, DeviceLighting.Frame(p.Program, .5), DeviceLighting.StopColor(p.Program))).ToArray();
        var frame = new DeviceLightingFrame(1, colors[0].Color, () => new(ValidFor: TimeSpan.FromMilliseconds(500)), colors);
        var result = await stream.WriteAsync(frame, default); Assert.All(result, v => Assert.Equal(Outcome.WrittenUnverified, v.Result.Outcome));
        foreach (var color in colors) Assert.Equal(color.Color, result.Single(v => v.EndpointId == color.EndpointId).Result.ReportedLighting!.Colors[0]);
        var stop = await stream.EndAsync(DeviceLightingStopReason.Normal, default); Assert.All(stop, v => Assert.Equal(Outcome.Confirmed, v.Result.Outcome));
        Assert.Equal(new BoardLightSetting(1, 60, 5, 10), Setting(cell, 5));
        Assert.Equal(new BoardLightSetting(1, 10, 100, 240), Setting(cell, 6));
        Assert.Equal(new BoardLightSetting(1, 0, 0, 0), Setting(cell, 7));
        Assert.Equal(outside, Setting(cell, 2)); Assert.False(File.Exists(r.JournalPath(cell)));
    }
    [Fact]
    public async Task CompiledAsrockCompositionValidatesTheWholeZoneSelectionBeforeRejectingAnOutsideNativeEffect()
    {
        var cell = new Cell(1, false); cell.Device.Raw[5][0] = 2; cell.Device.Raw[6][0] = 2;
        await using var r = await new Rig(new(cell)).Open(); var driver = (IDeviceLightingCompositionDriver)r.Driver(cell);
        EndpointLightingProgram[] both = [new("zone:5", new("fixed", [new(10, 20, 30)])), new("zone:6", new("breathing", [new(40, 50, 60)]))];
        Assert.Null(driver.ValidateLightingComposition(both));
        Assert.NotNull(driver.ValidateLightingComposition(both[..1])); Assert.Empty(cell.Device.Writes);
        Assert.NotNull(driver.ValidateLightingComposition([both[0], both[0]]));
        Assert.NotNull(driver.ValidateLightingComposition([both[0], new("zone:6", new("native/asrock/rainbow", []))]));
        Assert.Empty(cell.Device.Writes);
    }
}
