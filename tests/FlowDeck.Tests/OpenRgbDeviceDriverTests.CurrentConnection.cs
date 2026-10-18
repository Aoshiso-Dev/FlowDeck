using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    [Fact]
    public async Task CurrentOpenRgbConnectionUsesInternalShapeAndRestoresWithoutUserApproval()
    {
        await using var r = await new Rig().Open(configure: false); var driver = r.Driver();
        var before = OpenRgbLightingPlan.State(r.Server.Devices[41]);
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []); r.Runtime.Refresh();
        Assert.True(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.FixedColor));
        Assert.False(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.Temperature));
        Assert.False(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.FixedDuty));
        Assert.False(driver.AllowsCurrentConnectionControl("unknown", DeviceFeature.FixedColor));
        Assert.Equal(0, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0))], Blue));
        var writes = r.Server.WriteCount;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.Equal(writes, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.StopLighting, [r.Target(r.Zone(0))]));
        Assert.True(OpenRgbLightingPlan.Same(before, OpenRgbLightingPlan.State(r.Server.Devices[41])));
    }

    [Fact]
    public async Task CurrentOpenRgbPreparationCannotSelectAnUnselectedRoute()
    {
        await using var r = await new Rig([], Device()).Open(configure: false); var driver = r.Driver();
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []); r.Runtime.Refresh();
        Assert.False(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.FixedColor));
        Assert.True(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.RestoreLighting));
        Assert.False(driver.AllowsCurrentConnectionControl("unknown", DeviceFeature.RestoreLighting));
        Assert.Equal(DeviceAvailability.Conflict, driver.Observe()!.Endpoints.Single(e => e.Id == r.Zone(0))
            .Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Availability);
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0))], Blue)).Success);
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task OpenRgbReadFailureIsTemporaryAndPreservesTheRecoveryRoute()
    {
        await using var r = await new Rig().Open(configure: false); var driver = r.Driver();
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        r.RouteAvailable = false; await Assert.ThrowsAsync<IOException>(() => driver.PollAsync(default));
        Assert.All(driver.Observe()!.Endpoints, endpoint =>
            Assert.Equal(DeviceAvailability.TemporarilyUnavailable, endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Availability));
        Assert.True(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.RestoreLighting));
        Assert.Equal(0, r.Server.WriteCount);
        r.RouteAvailable = true; await driver.PollAsync(default);
        Assert.True(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.FixedColor));
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task CurrentOpenRgbConnectionReusesExitWithLegacyApprovalFlagsFalse()
    {
        await using var r = await new Rig().Open(configure: false); var driver = r.Driver(); var id = r.Zone(0);
        var count = driver.DescribeSetup().Single(o => o.EndpointId == id).MaximumLeds;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, driver.BindingCandidate, [],
            [new(new(driver.DeviceId, id), false, new(LightingExitAction.Off))],
            [new(id, id, EndpointKind.Lighting, Setup: new(LedCount: count, LedCountConfirmed: false))]);
        r.Runtime.Refresh(); Assert.Equal(0, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target(id)], Blue));
        Written(await r.Command(DeviceOperation.StopLighting, [r.Target(id)]));
        Assert.Equal(new RgbColor(0, 0, 0), r.Server.Devices[41].Colors[0]);
    }

    [Fact]
    public async Task AsrockSdkExtentDoesNotClaimPhysicalLedDetection()
    {
        var board = Device() with { Type = 0, Vendor = "ASRock", Description = "ASRock Polychrome USB Device" };
        await using var r = await new Rig(board).Open(configure: false); var driver = r.Driver();
        Assert.All(driver.DescribeSetup(), o => { Assert.Null(o.DetectedLeds); Assert.Equal(o.MinimumLeds, o.MaximumLeds); });
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.True(driver.AllowsCurrentConnectionControl(r.Zone(0), DeviceFeature.FixedColor));
        Assert.Equal(0, r.Server.WriteCount);
    }
}
