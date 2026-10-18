using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class MotherboardDeviceDriverTests
{
    [Fact]
    public async Task MixedFourFanRuntimeAcquiresDriftingBiosFanConfirmsEveryTargetAndAcceptsTheNextAdjustment()
    {
        await using var rig = await Rig.Create();
        var ports = MotherboardFans.Ports.Where(p => p.Chip == "NCT6799D" && p.Channel is 0 or 2 or 3 or 4).ToArray();
        Assert.Equal(4, ports.Length);
        var bank = rig.Backend.Banks["NCT6799D"];
        for (var i = 0; i < ports.Length; i++)
        {
            var map = NctFanMap.For(ports[i].Chip, ports[i].Channel);
            bank.Values[map.Mode] = 0x40;
            bank.Values[map.Output] = (byte)(100 + i * 20);
        }
        await rig.Runtime.PollAsync();
        var cpuFan2 = MotherboardFans.Find("nct6799d-2");
        var cpuMap = NctFanMap.For(cpuFan2.Chip, cpuFan2.Channel);
        rig.Backend.BeforeExecute = command =>
        {
            if (command.EndpointId == cpuFan2.Id && command.Operation == MotherboardFanOperation.Write)
            {
                // The BIOS raises measured PWM after the driver's fresh read. Its
                // automatic mode and configured command have not been changed.
                bank.Values[cpuMap.Output] = 210;
                rig.Backend.BeforeExecute = null;
            }
        };
        var hello = rig.Runtime.OpenSession("user"); var sequence = 0L;
        Task<DeviceServiceReply> Batch(DeviceOperation operation, int? duty = null)
        {
            var state = rig.Runtime.State();
            var targets = ports.Select(port =>
            {
                var current = state.Endpoints.Single(e => e.Target == new EndpointReference(rig.Driver.DeviceId, port.Id));
                return new DeviceTargetVersion(current.Target, current.Generation, current.PolicyRevision);
            }).ToArray();
            return rig.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence,
                "mixed-acquisition", operation, state.Inventory.TopologyRevision, targets, duty));
        }
        var first = await Batch(DeviceOperation.SetDuty, 93);
        Assert.Equal(Outcome.Confirmed, first.Result!.Outcome);
        Assert.Equal(4, first.Result.Targets.Length);
        Assert.All(first.Result.Targets, result => { Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(93, result.ReportedDuty); });
        Assert.Equal(4, rig.Journal().Pending.Length);
        Assert.NotNull(rig.Backend.Commands.Single(c => c.EndpointId == cpuFan2.Id && c.Operation == MotherboardFanOperation.Write).ExpectedControl);
        var next = await Batch(DeviceOperation.SetDuty, 89);
        Assert.Equal(Outcome.Confirmed, next.Result!.Outcome);
        Assert.All(next.Result.Targets, result => { Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(89, result.ReportedDuty); });
        Assert.Null(rig.Backend.Commands.Last(c => c.EndpointId == cpuFan2.Id && c.Operation == MotherboardFanOperation.Write).ExpectedControl);
        Assert.Equal(Outcome.Confirmed, (await Batch(DeviceOperation.ReleaseCooling)).Result!.Outcome);
        Assert.Empty(rig.Journal().Pending);
        Assert.All(ports, port => Assert.Equal(0x40, bank.Values[NctFanMap.For(port.Chip, port.Channel).Mode]));
    }
}
