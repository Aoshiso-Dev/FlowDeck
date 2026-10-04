using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class MotherboardDeviceDriverTests
{
    [Fact] public async Task IndividualSetupRetainsOtherMotherboardFanRegistersAndRecovery()
    {
        await using var rig = await Rig.Create();
        var selected = MotherboardFans.Cpu; var other = MotherboardFans.Chassis;
        Assert.Equal(Outcome.Confirmed, (await rig.Apply(selected, 55)).Outcome);
        Assert.Equal(Outcome.Confirmed, (await rig.Apply(other, 60)).Outcome);
        var binding = rig.Driver.BindingCandidate!;
        Assert.Throws<IOException>(() => rig.Driver.ConfigureCoolingEndpoint(binding, selected.Id, rig.Config(selected, 45)));
        Assert.Equal(Outcome.Confirmed, (await rig.Driver.ApplyAsync(new(selected.Id, DeviceOperation.ReleaseCooling), default)).Outcome);
        var retained = Assert.Single(rig.Journal().Pending); var writes = rig.Backend.Writes;
        rig.Driver.ConfigureCoolingEndpoint(binding, selected.Id, rig.Config(selected, 45));
        Assert.Equal(writes, rig.Backend.Writes); Assert.Equal(retained, Assert.Single(rig.Journal().Pending));
        Assert.Equal(60, (int)Math.Round(rig.Backend.Duty(other))); Assert.Equal(45, rig.Driver.CoolingConfiguration(selected.Id)!.Safety.Minimum);
        Assert.Throws<IOException>(() => rig.Driver.ConfigureCoolingEndpoint(binding with { Fingerprint = new('f', 64) }, selected.Id, rig.Config(selected)));
        rig.Driver.ConfigureCoolingEndpoint(binding, selected.Id, null);
        Assert.Null(rig.Driver.CoolingConfiguration(selected.Id)); Assert.Equal(retained, Assert.Single(rig.Journal().Pending));
        await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default);
        Assert.Empty(rig.Journal().Pending); Assert.Equal(67, (int)Math.Round(rig.Backend.Duty(other)));
    }
}
