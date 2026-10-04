using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Fact] public async Task RevokedBoardLightingPermissionStaysRevokedAfterPollingWithoutWriting()
    {
        var cell = new Cell(1, false); await using var rig = await new Rig(new(cell)).Open();
        var driver = rig.Driver(cell); Assert.Contains(driver.Observe()!.Endpoints.SelectMany(e => e.Capabilities), c => c.Feature == DeviceFeature.FixedColor && c.Usable);
        ((IDeviceConfigurationRevocation)driver).RevokeConfiguration();
        Assert.Empty(cell.Device.Writes); await rig.Runtime.PollAsync();
        Assert.All(driver.Observe()!.Endpoints.SelectMany(e => e.Capabilities).Where(c => c.Feature == DeviceFeature.FixedColor), c => Assert.False(c.Usable));
        Assert.Empty(cell.Device.Writes);
    }
}

public sealed partial class EneMemoryDeviceDriverTests
{
    [Fact] public async Task RevokedMemoryPermissionPreservesPendingRestorationAndRejectsNewColors()
    {
        var rack = new EneMemoryBackendTests.Rack(0x71); await using var rig = await new Rig(rack).Open();
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(2, 3, 4)]))).Result!.Outcome);
        var pending = rig.Pending(); Assert.NotNull(pending); var writes = rack.Writes.Count;
        ((IDeviceConfigurationRevocation)rig.Driver()).RevokeConfiguration();
        Assert.Equal(writes, rack.Writes.Count); Assert.NotNull(rig.Pending());
        await rig.Runtime.PollAsync();
        Assert.DoesNotContain(rig.Driver().Observe()!.Endpoints.SelectMany(e => e.Capabilities), c => c.Feature == DeviceFeature.FixedColor && c.Usable);
        Assert.False((await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(50, 60, 70)]))).Success);
        Assert.Equal(writes, rack.Writes.Count);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome); Assert.Null(rig.Pending());
    }
}

public sealed partial class OpenRgbDeviceDriverTests
{
    [Fact] public async Task RevokedOpenRgbPermissionStaysRevokedAfterPollingWithoutWriting()
    {
        await using var rig = await new Rig(Device()).Open(); var driver = rig.Driver();
        Assert.Contains(driver.Observe()!.Endpoints.SelectMany(e => e.Capabilities), c => c.Feature == DeviceFeature.FixedColor && c.Usable);
        ((IDeviceConfigurationRevocation)driver).RevokeConfiguration(); Assert.Equal(0, rig.Server.WriteCount);
        await rig.Runtime.PollAsync();
        Assert.All(driver.Observe()!.Endpoints.SelectMany(e => e.Capabilities).Where(c => c.Feature == DeviceFeature.FixedColor), c => Assert.False(c.Usable));
        Assert.False((await rig.Command(DeviceOperation.SetLighting, [rig.Target(rig.Zone(0))], Blue)).Success);
        Assert.Equal(0, rig.Server.WriteCount);
    }
}

public partial class MotherboardDeviceDriverTests
{
    [Fact] public async Task RevokedCoolingPermissionPreservesRpmAndRequiresNewConfiguration()
    {
        await using var rig = await Rig.Create(); var driver = rig.Driver;
        Assert.Null(driver.ValidateCoolingSetup(MotherboardFans.Cpu.Id)); var writes = rig.Backend.Writes;
        ((IDeviceConfigurationRevocation)driver).RevokeConfiguration(); await rig.Runtime.PollAsync();
        Assert.Equal(writes, rig.Backend.Writes); Assert.Null(driver.CoolingConfiguration(MotherboardFans.Cpu.Id));
        Assert.NotNull(driver.ValidateCoolingSetup(MotherboardFans.Cpu.Id));
        Assert.Contains(driver.Observe()!.Endpoints.SelectMany(e => e.Capabilities), c => c.Feature == DeviceFeature.Rpm && c.Usable);
        Assert.DoesNotContain(driver.Observe()!.Endpoints.SelectMany(e => e.Capabilities), c => c.Feature == DeviceFeature.FixedDuty && c.Usable);
    }
}
