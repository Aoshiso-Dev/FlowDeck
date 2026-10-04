using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceRecoveryInspectionTests
{
    [Fact] public void HubCoolingAndLightingMustBothBeReleasedBeforeSetupCanContinue()
    {
        using var dir = new ServiceTestDirectory(); Assert.False(DeviceRecoveryInspection.HasPending(dir.Path));
        var fans = new HubEndpointRecoveryJournal(dir.Path, "hub"); var lights = new HubLightingRecoveryJournal(dir.Path, "hub");
        var fan = fans.Prepare("physical", new('b', 64), "wiring", "fan1", 70, 50, 60);
        var exit = new LightingExitPolicy(LightingExitAction.Off);
        lights.Prepare([new("hub", "physical", new('b', 64), "wiring", "led1", Guid.NewGuid().ToString("N"), exit)]);
        Assert.True(DeviceRecoveryInspection.HasPending(dir.Path)); fans.ConfirmReleased(fan);
        Assert.True(DeviceRecoveryInspection.HasPending(dir.Path)); lights.Complete(lights.Pending.Single());
        Assert.False(DeviceRecoveryInspection.HasPending(dir.Path));
    }
    [Theory] [InlineData("asrock-lighting-")] [InlineData("asrock-native-")] [InlineData("ene-lighting-")]
    public void SurvivingRgbRecordBlocksSetupAndIsNeverDeletedOrReinterpreted(string prefix)
    {
        using var dir = new ServiceTestDirectory(); var file = Path.Combine(dir.Path, prefix + SettingsTransfer.Hash("absent-device") + ".json");
        File.WriteAllText(file, "pending or corrupt record"); Assert.True(DeviceRecoveryInspection.HasPending(dir.Path));
        Assert.Equal("pending or corrupt record", File.ReadAllText(file));
    }
    [Fact] public void CorruptEmptyHubJournalAndUnknownFormatsDoNotCountAsReleased()
    {
        using var dir = new ServiceTestDirectory(); var file = Path.Combine(dir.Path, "hub-endpoints-" + SettingsTransfer.Hash("hub") + ".json");
        ServiceEnvironment.Write(file, new HubEndpointRecoveryDocument(2, [], "wrong hash"));
        Assert.Throws<IOException>(() => DeviceRecoveryInspection.HasPending(dir.Path));
        File.Delete(file); File.WriteAllText(Path.Combine(dir.Path, "unknown.json"), "{}");
        Assert.Throws<IOException>(() => DeviceRecoveryInspection.HasPending(dir.Path));
    }
    [Fact] public void BoardObligationIsCheckedWithoutOpeningOrRestoringTheBoard()
    {
        using var dir = new ServiceTestDirectory();
        var binding = new PhysicalDeviceBinding("board", "native", MotherboardDefinitionCatalog.BuiltIn.Boards.First().Id, new('a', 64));
        var board = Path.Combine(dir.Path, "board-" + SettingsTransfer.Hash(binding.DeviceId));
        var journal = new FanRecoveryJournal(board, "boot", binding); var port = MotherboardFans.Ports.First();
        journal.Save(new(port.Chip, port.Channel, 0, 100, 0, 100, 0, 150));
        var bytes = File.ReadAllBytes(Path.Combine(board, "fan-recovery.json")); Assert.True(DeviceRecoveryInspection.HasPending(dir.Path));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(board, "fan-recovery.json")));
        journal.Clear(port.Chip, port.Channel); Assert.False(DeviceRecoveryInspection.HasPending(dir.Path));
    }
}
