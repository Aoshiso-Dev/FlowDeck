using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    private sealed class OffRecoveryCase : IAsyncDisposable
    {
        public Cell Cell = new(1, true);
        public Backend Backend;
        public string Directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Native-Off-" + Guid.NewGuid().ToString("N"));
        public AsrockNativeRecoveryRecord Saved = null!;
        public string PathToJournal => Path.Combine(Directory, "asrock-native-" + SettingsTransfer.Hash(Cell.Id).ToLowerInvariant() + ".json");
        public static readonly BoardLightSetting Black = new(1, 0, 0, 0);
        public static readonly BoardLightSetting Off = new(0, 0, 0, 0);
        public OffRecoveryCase() => Backend = new(Cell);
        public async Task<OffRecoveryCase> Open(bool sibling = false)
        {
            Cell.Device.Set(5, new(1, 255, 255, 255));
            await PrepareAbandonedNative(Cell, Backend, Directory, false);
            var journal = new AsrockNativeRecovery(Directory, Cell.Id);
            if (sibling)
            {
                var state = new AsrockVolatileTransaction(Cell.Connection.Path, Cell.Device.Exchange).Snapshot;
                journal.Prepare(Boot, journal.Pending!.Scope, state, 6, Setting(Cell, 6), Setting(Cell, 6));
            }
            Saved = journal.Pending!;
            Cell.Device.Set(5, Black);
            return this;
        }
        public Task<AsrockNativeOffRecoveryResult> Recover(string? hash = null, string? currentBoot = null) =>
            AsrockNativeOffRecovery.RecoverAsync(Installation, Directory, Cell.Id, hash ?? Saved.Hash, Boot, 5, Black,
                Backend, () => currentBoot ?? Boot);
        public async ValueTask DisposeAsync()
        {
            await Backend.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
    [Fact]
    public async Task OperatorOffRecoverySavesIntentBeforeWriteAndPreservesOriginalScopeAndSibling()
    {
        await using var r = await new OffRecoveryCase().Open(sibling: true);
        var checkedDurable = false;
        r.Cell.Device.BeforeWrite = packet =>
        {
            Assert.Equal(0x10, packet[1]); Assert.Equal(5, packet[3]); Assert.Equal(0, packet[4]);
            var saved = new AsrockNativeRecovery(r.Directory, r.Cell.Id).Pending!;
            Assert.NotEqual(r.Saved.Hash, saved.Hash); Assert.Equal(r.Saved.Scope, saved.Scope);
            var entry = saved.Entries.Single(e => e.Zone == 5);
            Assert.Equal(r.Saved.Entries.Single(e => e.Zone == 5).Original, entry.Original);
            Assert.Equal(OffRecoveryCase.Black, entry.Before); Assert.Equal(OffRecoveryCase.Off, entry.Intended); Assert.Equal(OffRecoveryCase.Off, entry.Exit);
            checkedDurable = true;
        };
        var result = await r.Recover();
        Assert.True(checkedDurable); Assert.Equal(r.Saved.Hash, result.PreviousHash); Assert.Equal(OffRecoveryCase.Off, result.After);
        Assert.Single(r.Cell.Device.Writes); Assert.Equal(OffRecoveryCase.Off, Setting(r.Cell, 5));
        Assert.Equal(r.Saved.Entries.Single(e => e.Zone == 6), Assert.Single(new AsrockNativeRecovery(r.Directory, r.Cell.Id).Pending!.Entries));
    }
    [Theory]
    [InlineData("setting")] [InlineData("order")] [InlineData("count")] [InlineData("revision")]
    [InlineData("boot")] [InlineData("hash")] [InlineData("serial")] [InlineData("direct")]
    public async Task OperatorOffRecoveryRejectsChangedPrerequisitesWithoutChangingIntentOrWriting(string change)
    {
        await using var r = await new OffRecoveryCase().Open();
        var saved = File.ReadAllText(r.PathToJournal);
        if (change == "setting") r.Cell.Device.Set(5, new(1, 90, 80, 70));
        if (change == "order") r.Cell.Device.Order ^= 1 << 5;
        if (change == "count") r.Cell.Device.Counts[5]++;
        if (change == "revision") r.Cell.Connection = r.Cell.Connection with { DeviceRevision = 99 };
        if (change == "serial") r.Cell.Connection = r.Cell.Connection with { Confidence = IdentityConfidence.ConnectionBound };
        if (change == "direct") File.WriteAllText(Path.Combine(r.Directory, "asrock-lighting-" + SettingsTransfer.Hash(r.Cell.Id).ToLowerInvariant() + ".json"), "foreign-direct-record");
        await Assert.ThrowsAnyAsync<Exception>(() => r.Recover(change == "hash" ? new string('0', 64) : null, change == "boot" ? "other-boot" : null));
        Assert.Empty(r.Cell.Device.Writes); Assert.Equal(saved, File.ReadAllText(r.PathToJournal));
    }
    [Theory]
    [InlineData("setting")] [InlineData("order")] [InlineData("count")] [InlineData("connection")] [InlineData("disk")]
    public async Task OperatorOffRecoveryRechecksAtQueuedWriteAndRetainsPreparedOffOnFailure(string change)
    {
        await using var r = await new OffRecoveryCase().Open();
        r.Backend.BeforeTransport = packet =>
        {
            if (packet[1] != 0x10) return;
            r.Backend.BeforeTransport = null;
            if (change == "setting") r.Cell.Device.Set(5, new(1, 90, 80, 70));
            if (change == "order") r.Cell.Device.Order ^= 1 << 5;
            if (change == "count") r.Cell.Device.Counts[5]++;
            if (change == "connection") r.Cell.Connection = r.Cell.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
            if (change == "disk") File.WriteAllText(r.PathToJournal, "different-owner");
        };
        await Assert.ThrowsAsync<IOException>(() => r.Recover());
        Assert.Empty(r.Cell.Device.Writes);
        if (change == "disk") Assert.Equal("different-owner", File.ReadAllText(r.PathToJournal));
        else
        {
            var pending = new AsrockNativeRecovery(r.Directory, r.Cell.Id).Pending!;
            Assert.NotEqual(r.Saved.Hash, pending.Hash);
            Assert.Equal(OffRecoveryCase.Off, Assert.Single(pending.Entries).Exit);
        }
    }
    [Fact]
    public async Task OperatorOffRecoveryRejectsFixedBlackReadbackAndPreservesOffForOrdinaryColdRecovery()
    {
        await using var r = await new OffRecoveryCase().Open();
        r.Cell.Device.AfterWrite = _ => r.Cell.Device.Set(5, OffRecoveryCase.Black);
        await Assert.ThrowsAsync<IOException>(() => r.Recover());
        Assert.Single(r.Cell.Device.Writes);
        var pending = new AsrockNativeRecovery(r.Directory, r.Cell.Id).Pending!;
        Assert.Equal(OffRecoveryCase.Off, Assert.Single(pending.Entries).Exit);
        r.Cell.Device.AfterWrite = null;
        var driver = new AsrockDeviceDriver(r.Cell.Id, r.Cell.Connection, r.Directory, Boot, r.Backend);
        await driver.PollAsync(default);
        var result = Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default));
        Assert.Equal(Outcome.Confirmed, result.Result.Outcome);
        Assert.Equal(OffRecoveryCase.Off, Setting(r.Cell, 5)); Assert.False(File.Exists(r.PathToJournal));
    }
    [Fact]
    public async Task OperatorOffRecoveryAcceptsLostAcknowledgementOnlyAfterExactFreshOffReadback()
    {
        await using var r = await new OffRecoveryCase().Open();
        r.Cell.Device.AfterWrite = _ => throw new IOException("lost acknowledgement");
        var result = await r.Recover();
        Assert.Equal(OffRecoveryCase.Off, result.After); Assert.Single(r.Cell.Device.Writes); Assert.False(File.Exists(r.PathToJournal));
    }
}
