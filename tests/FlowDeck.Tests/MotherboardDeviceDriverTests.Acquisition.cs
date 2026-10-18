using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class MotherboardDeviceDriverTests
{
    [Fact] public async Task InitialFirmwareOutputDriftUsesStableControlSnapshotAndStillConfirmsAndRestores()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2"); var map = NctFanMap.For(port.Chip, port.Channel);
        var bank = r.Backend.Banks[port.Chip]; bank.Values[map.Mode] = 0x40;
        r.Backend.BeforeExecute = command => { if (command.Operation == MotherboardFanOperation.Write) bank.Values[map.Output] = 210; };
        var result = await r.Apply(port, 93);
        Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(93, result.ReportedDuty);
        var write = Assert.Single(r.Backend.Commands, c => c.Operation == MotherboardFanOperation.Write);
        Assert.Equal(new MotherboardFanControlSnapshot(port.Chip, port.Channel, 0x40, 170), write.ExpectedControl);
        var journal = Assert.Single(r.Journal().Pending); Assert.Equal(0x40, journal.OriginalMode); Assert.Equal(170, journal.OriginalCommand);
        Assert.Equal(new[] { (map.Command, (byte)237), (map.Mode, (byte)0) }, bank.Writes);
        r.Backend.BeforeExecute = null;
        Assert.Equal(Outcome.Confirmed, (await r.Apply(port, 89)).Outcome);
        Assert.Null(r.Backend.Commands.Last(c => c.Operation == MotherboardFanOperation.Write).ExpectedControl);
        Assert.Equal(Outcome.Confirmed, Assert.Single(await r.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Empty(r.Journal().Pending); Assert.Equal(0x40, bank.Values[map.Mode]); Assert.Equal(170, bank.Values[map.Command]);
    }

    [Theory] [InlineData("mode")] [InlineData("command")]
    public async Task InitialAcquisitionNeverAdoptsAChangedControlSetting(string changed)
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2"); var map = NctFanMap.For(port.Chip, port.Channel);
        var bank = r.Backend.Banks[port.Chip]; bank.Values[map.Mode] = 0x40;
        r.Backend.BeforeExecute = command => { if (command.Operation == MotherboardFanOperation.Write) bank.Values[changed == "mode" ? map.Mode : map.Command] = 0; };
        Assert.Equal(Outcome.Rejected, (await r.Apply(port, 93)).Outcome);
        Assert.Empty(bank.Writes); Assert.Empty(r.Journal().Pending);
        Assert.Single(r.Backend.Commands, c => c.Operation == MotherboardFanOperation.Write);
        Assert.Equal(Outcome.Confirmed, Assert.Single(await r.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Empty(bank.Writes); Assert.Empty(r.Journal().Pending);
    }

    [Theory] [InlineData("fallback")] [InlineData("owned")] [InlineData("missing")]
    public async Task OutputDriftRemainsStrictOutsideTheFirstNormalSnapshotAcquisition(string operation)
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2"); var map = NctFanMap.For(port.Chip, port.Channel);
        var bank = r.Backend.Banks[port.Chip]; bank.Values[map.Mode] = 0x40;
        if (operation == "owned") Assert.Equal(Outcome.Confirmed, (await r.Apply(port, 70)).Outcome);
        r.Backend.SupplyControlSnapshot = operation != "missing"; var writes = r.Backend.Writes;
        r.Backend.BeforeExecute = command => { if (command.Operation == MotherboardFanOperation.Write) bank.Values[map.Output] = 210; };
        Assert.Equal(Outcome.Rejected, (await r.Apply(port, operation == "fallback" ? 100 : 93, fallback: operation == "fallback")).Outcome);
        Assert.Equal(writes, r.Backend.Writes);
        Assert.Null(r.Backend.Commands.Last(c => c.Operation == MotherboardFanOperation.Write).ExpectedControl);
        Assert.Equal(operation == "owned" ? 1 : 0, r.Journal().Pending.Length);
        r.Backend.BeforeExecute = null;
    }

    [Fact] public async Task CalibrationKeepsItsMeasuredOutputPreconditionEvenInAutomaticMode()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2"); var map = NctFanMap.For(port.Chip, port.Channel);
        var bank = r.Backend.Banks[port.Chip]; bank.Values[map.Mode] = 0x40;
        r.Backend.BeforeExecute = command => { if (command.Operation == MotherboardFanOperation.Write) bank.Values[map.Output] = 210; };
        var result = await r.Driver.BeginCalibrationAsync(CalibrationTestActuation.Create(port.Id, r.Driver.BindingCandidate!, 100, 67), default);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.Empty(bank.Writes); Assert.Empty(r.Journal().Pending);
        Assert.Null(Assert.Single(r.Backend.Commands, c => c.Operation == MotherboardFanOperation.Write).ExpectedControl);
        r.Backend.BeforeExecute = null;
        Assert.Equal(Outcome.Confirmed, (await r.Driver.EndCalibrationAsync(port.Id, default)).Outcome); Assert.Empty(bank.Writes);
    }

    [Fact] public async Task FirstAcquisitionDoesNotBypassAuthorityOrDeadline()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2"); var map = NctFanMap.For(port.Chip, port.Channel);
        r.Backend.Banks[port.Chip].Values[map.Mode] = 0x40;
        Assert.Equal(Outcome.Rejected, (await r.Apply(port, 93, authorize: () => new("owner changed"))).Outcome);
        var result = await r.Apply(port, 93, authorize: () => new(ValidFor: TimeSpan.Zero));
        Assert.Equal(Outcome.Rejected, result.Outcome); Assert.Equal(DeviceDriverFailure.CoolingInputExpired, result.Failure);
        Assert.Equal(0, r.Backend.Writes); Assert.Empty(r.Journal().Pending);
    }
}
