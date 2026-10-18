using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class MotherboardDeviceDriverTests
{
    [Fact]
    public async Task MotherboardPreparationMarkerTracksAdoptedSettingsAndInvalidatesAfterIdentityChange()
    {
        await using var r = await Rig.Create(configure: false); var driver = r.Driver; var port = MotherboardFans.Cpu;
        var original = driver.BindingCandidate!; var saved = r.Config(port) with { WiringConfirmed = false };
        Assert.Null(driver.CurrentControlBinding);
        driver.ConfigureCurrentConnection(original, original, [saved], [], []);
        Assert.Equal(original, driver.CurrentControlBinding); Assert.Null(driver.ValidateCoolingSetup(port.Id));
        r.Backend.Fingerprint = new('e', 64); await r.Provider.PollAsync(default);
        Assert.NotEqual(original, driver.BindingCandidate); Assert.Null(driver.CurrentControlBinding);
        Assert.False(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.FixedDuty));
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, original, [saved], [], []);
        Assert.Equal(driver.BindingCandidate, driver.CurrentControlBinding);
        Assert.True(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.FixedDuty));
        Assert.Equal(0, r.Backend.Writes);
    }

    [Fact]
    public async Task NewMotherboardConnectionKeepsFirmwareControlUntilActualSafetyValuesExist()
    {
        await using var r = await Rig.Create(configure: false); var driver = r.Driver; var port = MotherboardFans.Cpu;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.Null(driver.CoolingConfiguration(port.Id)); Assert.NotNull(driver.ValidateCoolingSetup(port.Id));
        Assert.False(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.FixedDuty));
        Assert.False(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.AutomaticDuty));
        Assert.False(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.Temperature));
        Assert.Equal(0, r.Backend.Writes); Assert.Empty(r.Journal().Pending);
    }

    [Fact]
    public async Task MotherboardReusesActualSafetyWithoutWireApprovalAndWithoutResumingAnOldPolicy()
    {
        await using var r = await Rig.Create(configure: false); var driver = r.Driver; var port = MotherboardFans.Cpu;
        var saved = r.Config(port) with { WiringConfirmed = false, ResumeOnStart = true,
            SavedPolicy = new(FanMode.Manual, 55) };
        var old = driver.BindingCandidate! with { Fingerprint = new('f', 64) };
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, old, [saved], [], []);
        Assert.Null(driver.ValidateCoolingSetup(port.Id));
        Assert.True(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.FixedDuty));
        Assert.True(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.AutomaticDuty));
        Assert.False(driver.AllowsCurrentConnectionControl("unknown", DeviceFeature.FixedDuty));
        var resolved = driver.CoolingConfiguration(port.Id)!;
        Assert.True(DeviceConfigurationRules.SameSafety(saved.Safety, resolved.Safety)); Assert.False(resolved.WiringConfirmed);
        Assert.Null(resolved.SavedPolicy); Assert.False(resolved.ResumeOnStart); Assert.Equal(0, r.Backend.Writes);
        Assert.Equal(Outcome.Confirmed, (await r.Apply(port, 55)).Outcome);
        var writes = r.Backend.Writes;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.Equal(writes, r.Backend.Writes); Assert.True(DeviceConfigurationRules.SameSafety(resolved.Safety, driver.CoolingConfiguration(port.Id)!.Safety));
    }

    [Theory, InlineData(null, 100), InlineData(40, null), InlineData(0, 100)]
    public async Task ApprovalFreeMotherboardStillRejectsMissingOrUnsupportedActualSafety(int? minimum, int? fallback)
    {
        await using var r = await Rig.Create(configure: false); var driver = r.Driver; var port = MotherboardFans.Cpu;
        var saved = r.Config(port) with { WiringConfirmed = false,
            Safety = new(minimum, fallback, null, CoolingExitAction.ReleaseToDevice, []) };
        if (minimum == 0)
            Assert.Throws<ArgumentException>(() => driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [saved], [], []));
        else driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [saved], [], []);
        Assert.NotNull(driver.ValidateCoolingSetup(port.Id));
        Assert.False(driver.AllowsCurrentConnectionControl(port.Id, DeviceFeature.FixedDuty));
        Assert.Equal(0, r.Backend.Writes);
    }
}
