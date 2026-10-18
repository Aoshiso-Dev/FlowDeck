using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task IndividualSetupKeepsSiblingOpenRgbFramesAndRecovery(bool sharedDirect, bool remove)
    {
        await using var r = await new Rig(sharedDirect ? SharedDevice(direct: true) : Device()).Open(exit: LightingExitAction.Off);
        var driver = r.Driver(); var selected = r.Zone(0); var other = r.Zone(1);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target(other)], Animation)); await r.Tick(.4);
        var before = r.Runtime.State().Endpoints.Single(e => e.Target == r.Target(other));
        var pending = new OpenRgbLightingRecovery(r.Directory, driver.DeviceId, sharedDirect ? null : 1).Pending!;
        var option = driver.DescribeSetup().Single(p => p.EndpointId == selected);
        var endpoint = new RegisteredEndpoint(selected, selected, EndpointKind.Lighting, Setup: new(LedCount: option.DetectedLeds, LedCountConfirmed: true));
        LightingEndpointConfiguration? configuration = remove ? null : new(new(driver.DeviceId, selected), true, new(LightingExitAction.FixedColor, new(255, 255, 255)));
        var writes = r.Server.WriteCount;
        Assert.Throws<IOException>(() => driver.ConfigureLightingEndpoint(driver.BindingCandidate! with { Fingerprint = new('f', 64) }, selected, configuration, endpoint));
        driver.ConfigureLightingEndpoint(driver.BindingCandidate!, selected, configuration, endpoint);
        Assert.Equal(writes, r.Server.WriteCount);
        Assert.True(driver.AllowsCurrentConnectionControl(selected, DeviceFeature.FixedColor));
        Assert.True(driver.AllowsCurrentConnectionControl(selected, DeviceFeature.ColorFrames));
        Assert.Equal(driver.BindingCandidate, driver.CurrentControlBinding);
        Assert.Equal(SettingsTransfer.Hash(pending), SettingsTransfer.Hash(new OpenRgbLightingRecovery(r.Directory, driver.DeviceId, sharedDirect ? null : 1).Pending!));
        Assert.Throws<IOException>(() => driver.ConfigureLightingEndpoint(driver.BindingCandidate!, other, null,
            new(other, other, EndpointKind.Lighting)));
        r.Runtime.Refresh(); await r.Tick(.4);
        var after = r.Runtime.State().Endpoints.Single(e => e.Target == r.Target(other));
        Assert.True(after.Owned); Assert.True(after.Lighting!.Running); Assert.Null(after.Lighting.Fault);
        Assert.Equal(before.Lighting!.GroupId, after.Lighting.GroupId); Assert.True(after.Lighting.Frames > before.Lighting.Frames);
        Assert.All(await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default), result => Assert.Equal(Outcome.WrittenUnverified, result.Result.Outcome));
        Assert.Null(new OpenRgbLightingRecovery(r.Directory, driver.DeviceId, sharedDirect ? null : 1).Pending);
        if (remove)
        {
            Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new(selected, DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
            Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new(selected, DeviceOperation.StopLighting), default)).Outcome);
        }
    }
}

public sealed partial class AsrockDeviceDriverTests
{
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task IndividualSetupKeepsSiblingAsrockOutputAndRecovery(bool streaming, bool remove)
    {
        var cell = new Cell(1, true); await using var r = await new Rig(new(cell)).Open();
        var driver = r.Driver(cell); const string selected = "zone:5";
        var reply = streaming ? await r.Start((cell, 6)) : await r.Command(DeviceOperation.SetLighting, [(cell, 6)], new("fixed", [new(10, 20, 30)]));
        Assert.True(reply.Success, reply.Error); await r.Tick(.4);
        var before = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:6");
        var files = System.IO.Directory.GetFiles(r.Directory).ToDictionary(p => p, File.ReadAllBytes);
        var endpoint = new RegisteredEndpoint(selected, selected, EndpointKind.Lighting, Setup: new(LedCount: cell.Device.Counts[5], LedCountConfirmed: true));
        LightingEndpointConfiguration? configuration = remove ? null : new(new(driver.DeviceId, selected), true, new(LightingExitAction.FixedColor, new(255, 255, 255)));
        var writes = cell.Device.Writes.Count;
        Assert.Throws<IOException>(() => driver.ConfigureLightingEndpoint(driver.BindingCandidate! with { Fingerprint = new('f', 64) }, selected, configuration, endpoint));
        driver.ConfigureLightingEndpoint(driver.BindingCandidate!, selected, configuration, endpoint);
        Assert.Equal(writes, cell.Device.Writes.Count); foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.True(driver.AllowsCurrentConnectionControl(selected, DeviceFeature.FixedColor));
        Assert.True(driver.AllowsCurrentConnectionControl(selected, DeviceFeature.ColorFrames));
        Assert.Equal(driver.BindingCandidate, driver.CurrentControlBinding);
        Assert.Throws<IOException>(() => driver.ConfigureLightingEndpoint(driver.BindingCandidate!, "zone:6", null, new("zone:6", "zone:6", EndpointKind.Lighting)));
        r.Runtime.Refresh(); await r.Tick(.4);
        var after = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:6");
        Assert.True(after.Owned); Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        if (streaming) { Assert.True(after.Lighting!.Running); Assert.Null(after.Lighting.Fault); Assert.Equal(before.Lighting!.GroupId, after.Lighting.GroupId); Assert.True(after.Lighting.Frames > before.Lighting.Frames); }
        else Assert.Equal(new BoardLightSetting(1, 10, 20, 30), Setting(cell, 6));
        Assert.All(await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default), result => Assert.Equal(Outcome.Confirmed, result.Result.Outcome));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
        if (remove)
        {
            var original = Setting(cell, 5);
            Assert.Equal(Outcome.Confirmed, (await driver.ApplyAsync(new(selected, DeviceOperation.SetLighting, Lighting: new("fixed", [new(1, 2, 3)])), default)).Outcome);
            Assert.Equal(Outcome.Confirmed, (await driver.ApplyAsync(new(selected, DeviceOperation.StopLighting), default)).Outcome);
            Assert.Equal(original, Setting(cell, 5));
        }
    }
}
