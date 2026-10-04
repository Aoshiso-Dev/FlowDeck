using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbDetectionRecoveryTests
{
    private static readonly AsrockOpenRgbRecoveryIdentity Scope = new("d3f516653d764da181e6f5a74bcae5c9", "board-fixture", "boot-fixture",
        "20c980723ffe433a94931b93f3d742b0", 1);
    private sealed class Rig : IDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly AsrockVolatileTransactionTests.Device Device = new();
        public readonly AsrockOpenRgbDetectionRecovery Journal;
        public bool Owns = true;
        public Rig(Action<string, AsrockOpenRgbRecoveryRecord>? write = null) => Journal = new(Directory.Path, Scope.DeviceId, write);
        public void Guard() { if (!Owns) throw new IOException("physical handover changed"); }
        public AsrockRgbSnapshot Capture() => Journal.Capture(Scope, "hid-fixture", Device.Exchange, Guard);
        public AsrockRgbSnapshot Read() => new AsrockVolatileTransaction("hid-fixture", Device.Exchange).Snapshot;
        public AsrockRgbRestoreResult Restore() => Journal.Restore(Scope, Device.Exchange, Guard);
        public string RecordPath => Assert.Single(System.IO.Directory.GetFiles(Directory.Path));
        public void Dispose() => Directory.Dispose();
        public AsrockRgbSnapshot NativeExit()
        {
            Device.Order = 0;
            foreach (var zone in Device.Raw.Keys) Device.Set(zone, new(1, 90, 20 + zone, 60, 150));
            return Read();
        }
    }
    private static void OnlyVolatileWrites(IEnumerable<byte[]> writes) => Assert.All(writes, packet =>
        Assert.True(packet[1] == 0x15 && packet[3] == 3 || packet[1] == 0x10 && packet[3] < 8));

    [Fact] public void CaptureIsReadOnlyAndDurableBeforeTheDetectorMayStart()
    {
        using var r = new Rig(); var before = r.Capture();
        Assert.Empty(r.Device.Writes); Assert.True(DeviceRecoveryInspection.HasPending(r.Directory.Path));
        var reopened = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Scope.DeviceId);
        Assert.Equal(SettingsTransfer.Hash(before), SettingsTransfer.Hash(reopened.Pending!.Before));
        Assert.Equal(AsrockOpenRgbRecoveryPhase.Detecting, reopened.Pending.Phase);
        before.Counts[2] = 1; Assert.Equal(80, reopened.Pending.Before.Counts[2]);
        Assert.Throws<IOException>(() => r.Capture());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DetectionStartupOrNoStartupRestoresOriginalOrderWithoutRewritingUnchangedZones(bool started)
    {
        using var r = new Rig(); var before = r.Capture(); var raw = r.Device.Raw.ToDictionary(p => p.Key, p => p.Value.ToArray());
        if (started) r.Device.Order = AsrockOpenRgbDetectionRecovery.DetectionOrder(before);
        var result = r.Restore(); Assert.True(result.Restored); Assert.Equal(before.Order, r.Device.Order);
        Assert.Equal(started ? 1 : 0, r.Device.Writes.Count); Assert.Null(r.Journal.Pending);
        foreach (var zone in raw) Assert.Equal(zone.Value, r.Device.Raw[zone.Key]);
        Assert.False(DeviceRecoveryInspection.HasPending(r.Directory.Path)); OnlyVolatileWrites(r.Device.Writes);
    }
    [Fact] public void ZeroLengthPresentZonesPreserveTheirConfiguredOrderBitButUnavailableZonesDoNot()
    {
        using var r = new Rig(); r.Device.Available |= 1; r.Device.Counts[0] = 0; r.Device.Set(0, new(0, 0, 0, 0));
        var before = r.Capture(); Assert.Equal(0x65, AsrockOpenRgbDetectionRecovery.DetectionOrder(before));
        r.Device.Counts[0] = 0x1e;
        Assert.Equal(0x64, AsrockOpenRgbDetectionRecovery.DetectionOrder(r.Read()));
    }
    [Theory] [InlineData(15)] [InlineData(0xe2)] [InlineData(0xe3)]
    public void UnreadableDirectPixelsAreNotInventedAsAnOriginalNativeSetting(int mode)
    {
        using var r = new Rig(); r.Device.Raw[2][0] = (byte)mode;
        Assert.Throws<IOException>(() => r.Capture()); Assert.Empty(r.Device.Writes); Assert.Null(r.Journal.Pending);
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory.Path));
    }
    [Fact] public void FailedDurableSaveCannotPermitStartupAndMakesNoHardwareMutation()
    {
        using var r = new Rig((_, _) => throw new IOException("disk full"));
        Assert.Throws<IOException>(() => r.Capture()); Assert.Null(r.Journal.Pending); Assert.Empty(r.Device.Writes);
    }
    [Fact] public void MissingPhysicalHandoverIdentityIsRejectedBeforeEvenReadingNativeState()
    {
        using var r = new Rig(); var reads = 0;
        byte[] Exchange(byte[] request) { reads++; return r.Device.Exchange(request); }
        Assert.Throws<IOException>(() => r.Journal.Capture(Scope with { Handover = "" }, "hid-fixture", Exchange, r.Guard));
        Assert.Throws<IOException>(() => r.Journal.Capture(Scope, "", Exchange, r.Guard));
        Assert.Equal(0, reads); Assert.Empty(r.Device.Writes);
    }
    [Fact] public void ChangeWhileSavingLeavesAnObligationAndRejectsStartupWithoutOverwritingIt()
    {
        AsrockVolatileTransactionTests.Device? hardware = null;
        using var r = new Rig((path, record) => { ServiceEnvironment.Write(path, record); hardware!.Set(2, new(1, 201, 202, 203)); });
        hardware = r.Device;
        Assert.Throws<IOException>(() => r.Capture()); Assert.NotNull(r.Journal.Pending); Assert.Empty(r.Device.Writes);
        Assert.Throws<IOException>(() => r.Restore()); Assert.True(DeviceRecoveryInspection.HasPending(r.Directory.Path));
    }
    [Theory] [InlineData("boot")] [InlineData("handover")] [InlineData("revision")] [InlineData("device")]
    public void AReusedPathDoesNotAuthorizeAnotherBootHandoverRevisionOrDevice(string change)
    {
        using var r = new Rig(); r.Capture();
        var wrong = change switch
        {
            "boot" => Scope with { Boot = "another-boot" }, "handover" => Scope with { Handover = Guid.NewGuid().ToString("N") },
            "revision" => Scope with { DeviceRevision = 2 }, _ => Scope with { DeviceId = "another-board" }
        };
        Assert.Throws<IOException>(() => r.Journal.Restore(wrong, r.Device.Exchange, r.Guard)); Assert.Empty(r.Device.Writes);
    }
    [Theory] [InlineData("color")] [InlineData("count")] [InlineData("availability")] [InlineData("order")]
    public void ForeignSettingsOrTopologyAreRejectedBeforeAnyRecoveryWrite(string change)
    {
        using var r = new Rig(); r.Capture();
        switch (change)
        {
            case "color": r.Device.Set(5, new(1, 240, 230, 220)); break;
            case "count": r.Device.Counts[2]++; break;
            case "availability": r.Device.Available &= 0x24; break;
            case "order": r.Device.Order = 1; break;
        }
        Assert.Throws<IOException>(() => r.Restore()); Assert.Empty(r.Device.Writes); Assert.NotNull(r.Journal.Pending);
    }
    [Theory] [InlineData("original")] [InlineData("off")] [InlineData("fixed")]
    public void NativeExitRestoresTheOriginalOrderWhilePreservingTheSelectedExitPolicy(string policy)
    {
        using var r = new Rig(); var before = r.Capture(); var expected = r.NativeExit();
        var desired = before.Zones.ToDictionary(z => z.Address, z => policy switch
        {
            "original" => z.Setting, "off" => new BoardLightSetting(0, 0, 0, 0), _ => new BoardLightSetting(1, 4, 80, 170)
        });
        r.Journal.PrepareExit(Scope, expected, desired, r.Guard);
        Assert.Empty(r.Device.Writes); Assert.True(r.Restore().Restored);
        var actual = r.Read(); Assert.Equal(before.Order, actual.Order);
        foreach (var zone in actual.Zones) Assert.Equal(desired[zone.Address], zone.Setting);
        OnlyVolatileWrites(r.Device.Writes);
    }
    [Fact] public void ExplicitDirectRegisterExpectationCanSettleToKnownNativeExitWithoutTreatingRegistersAsPixels()
    {
        using var r = new Rig(); var before = r.Capture(); var native = r.NativeExit();
        var direct = native with { Zones = native.Zones.Select(z => z with { Setting = z.Setting with { Mode = 15 } }).ToArray() };
        foreach (var address in r.Device.Raw.Keys) r.Device.Raw[address][0] = 15;
        // Expected register bytes and desired blue come from different evidence. Never use direct raw colors as desired pixels.
        var desired = before.Zones.ToDictionary(z => z.Address, _ => new BoardLightSetting(1, 0, 0, 255));
        r.Journal.PrepareExit(Scope, direct, desired, r.Guard);
        Assert.True(r.Restore().Restored); Assert.All(r.Read().Zones, z => Assert.Equal(desired[z.Address], z.Setting));
        OnlyVolatileWrites(r.Device.Writes);
    }
    [Fact] public void PartialRecoverySurvivesReopeningAndRetriesOnlyUnfinishedZones()
    {
        using var r = new Rig(); var before = r.Capture(); var expected = r.NativeExit();
        var desired = before.Zones.ToDictionary(z => z.Address, z => z.Setting);
        r.Journal.PrepareExit(Scope, expected, desired, r.Guard);
        r.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5) throw new IOException("zone unavailable"); };
        var first = r.Restore(); Assert.False(first.Restored); Assert.True(first.OrderRestored);
        Assert.Equal(new[] { "verified", "mismatch", "verified" }, first.Zones.Select(z => z.Status));
        Assert.Throws<IOException>(() => r.Journal.PrepareExit(Scope, expected, desired, r.Guard));
        r.Device.BeforeWrite = null; r.Device.Writes.Clear();
        var reopened = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Scope.DeviceId);
        Assert.True(reopened.Restore(Scope, r.Device.Exchange, r.Guard).Restored);
        var retry = Assert.Single(r.Device.Writes); Assert.Equal(0x10, retry[1]); Assert.Equal(5, retry[3]);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void LostAcknowledgementsAreResolvedByReadbackAndDoNotForceRepeatedWrites(bool orderWrite)
    {
        using var r = new Rig(); var before = r.Capture(); var expected = r.NativeExit();
        r.Journal.PrepareExit(Scope, expected, before.Zones.ToDictionary(z => z.Address, z => z.Setting), r.Guard);
        r.Device.AfterWrite = p =>
        {
            if (p[1] == (orderWrite ? 0x15 : 0x10)) { r.Device.AfterWrite = null; throw new IOException("ack lost"); }
        };
        Assert.True(r.Restore().Restored);
        Assert.Single(r.Device.Writes, p => p[1] == 0x15); Assert.Equal(3, r.Device.Writes.Count(p => p[1] == 0x10));
    }
    [Fact] public void RecoveryProgressMustBeDurableBeforeAnyNativeMutation()
    {
        var saves = 0;
        using var r = new Rig((path, record) => { if (++saves == 3) throw new IOException("disk full"); ServiceEnvironment.Write(path, record); });
        var before = r.Capture(); var expected = r.NativeExit();
        r.Journal.PrepareExit(Scope, expected, before.Zones.ToDictionary(z => z.Address, z => z.Setting), r.Guard);
        Assert.Throws<IOException>(() => r.Restore()); Assert.Empty(r.Device.Writes);
        Assert.Equal(AsrockOpenRgbRecoveryPhase.ExitReady, r.Journal.Pending!.Phase);
    }
    [Fact] public void NewForeignChangeDuringRecoveryStopsRemainingWritesAndKeepsTheJournal()
    {
        using var r = new Rig(); var before = r.Capture(); var expected = r.NativeExit();
        r.Journal.PrepareExit(Scope, expected, before.Zones.ToDictionary(z => z.Address, z => z.Setting), r.Guard);
        r.Device.AfterWrite = p => { if (p[1] == 0x15) r.Device.Set(5, new(1, 200, 199, 198)); };
        Assert.Throws<IOException>(() => r.Restore()); Assert.Single(r.Device.Writes); Assert.NotNull(r.Journal.Pending);
    }
    [Fact] public void RevokedPhysicalOwnershipStopsBeforeAnotherNativeCommand()
    {
        using var r = new Rig(); var before = r.Capture(); r.Device.Order = AsrockOpenRgbDetectionRecovery.DetectionOrder(before);
        r.Device.AfterWrite = _ => r.Owns = false;
        Assert.Throws<IOException>(() => r.Restore()); Assert.Single(r.Device.Writes); Assert.NotNull(r.Journal.Pending);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ChangedOrMalformedDurableRecordCannotBeConsumedOrReplaced(bool malformed)
    {
        using var r = new Rig(); r.Capture(); var path = r.RecordPath;
        if (malformed) File.WriteAllText(path, "{\"Version\":1,\"version\":2}");
        else ServiceEnvironment.Write(path, r.Journal.Pending! with { SourceRevision = new string('0', 40) });
        Assert.ThrowsAny<IOException>(() => new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Scope.DeviceId));
        Assert.ThrowsAny<IOException>(() => r.Restore()); Assert.Empty(r.Device.Writes);
        Assert.True(DeviceRecoveryInspection.HasPending(r.Directory.Path));
    }
}
