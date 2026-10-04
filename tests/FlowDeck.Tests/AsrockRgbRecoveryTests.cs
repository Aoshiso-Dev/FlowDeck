using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Device = FlowDeck.Tests.AsrockVolatileTransactionTests.Device;

namespace FlowDeck.Tests;

public class AsrockRgbRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FlowDeck-RgbRecovery-" + Guid.NewGuid().ToString("N"));
    [Fact] public void JournalSurvivesRestartAndRecoversInterruptedDirectState()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); var store = new AsrockRgbRecoveryStore(directory);
        t.Begin(s => store.Save("boot", s), true);
        var loaded = new AsrockRgbRecoveryStore(directory).Load()!;
        var restored = new AsrockVolatileTransaction(loaded.Snapshot, d.Exchange).Restore(loaded.Desired);
        Assert.True(restored.Restored); Assert.Equal(0xfd, d.Order);
        store.Clear(); Assert.False(store.Exists);
    }
    [Fact] public void InterruptedNormalStopFinishesTheDurableTarget()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); var store = new AsrockRgbRecoveryStore(directory);
        t.Begin(s => store.Save("boot", s), true);
        var desired = t.Snapshot.Zones.ToDictionary(z => z.Address, z => new BoardLightSetting(1, 7, 8, 9)); store.Save("boot", t.Snapshot, desired);
        _ = d.Exchange(AsrockDirectProtocol.ColorOrder(0xfd)); _ = d.Exchange(AsrockRgbControl.Packet(2, desired[2], 0xfd));
        var loaded = store.Load()!;
        Assert.True(new AsrockVolatileTransaction(loaded.Snapshot, d.Exchange).Restore(loaded.Desired).Restored);
        Assert.All(d.Raw.Values, r => Assert.Equal(new byte[] { 1, 7, 8, 9, 224 }, r));
    }
    [Fact] public void AlreadyRestoredStateRequiresNoFurtherHardwareWrites()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange);
        var desired = t.Snapshot.Zones.ToDictionary(z => z.Address, z => z.Setting);
        Assert.True(new AsrockVolatileTransaction(t.Snapshot, d.Exchange).Restore(desired).Restored); Assert.Empty(d.Writes);
    }
    [Fact] public void ANewNativeSettingFromAnotherAppIsNotOverwritten()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); t.Begin(_ => { }, true);
        d.Order = 0xfd; d.Set(5, new(14, 10, 20, 30)); var count = d.Writes.Count;
        Assert.False(new AsrockVolatileTransaction(t.Snapshot, d.Exchange).Restore().Restored); Assert.Equal(count, d.Writes.Count);
    }
    [Fact] public void AlteredOrTruncatedJournalIsRejectedAndRetained()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); var store = new AsrockRgbRecoveryStore(directory);
        store.Save("boot", t.Snapshot);
        var path = Path.Combine(directory, "asrock-rgb-recovery.json"); var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("test", "other-device")); Assert.Throws<IOException>(() => store.Load()); Assert.True(store.Exists);
        File.WriteAllText(path, "{"); Assert.Throws<IOException>(() => store.Load()); Assert.True(store.Exists); Assert.Empty(d.Writes);
    }
    [Fact] public void PreviousBootIsArchivedWithoutLosingTheEvidence()
    {
        var d = new Device(); var store = new AsrockRgbRecoveryStore(directory); store.Save("old-boot", new AsrockVolatileTransaction("test", d.Exchange).Snapshot);
        store.ArchivePreviousBoot(); Assert.False(store.Exists); Assert.Single(Directory.GetFiles(directory, "*.previous-boot-*")); Assert.Empty(d.Writes);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
