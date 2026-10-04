using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    private static RgbColor LastDirectColor(Cell cell, byte zone)
    {
        var start = cell.Device.Writes.FindLastIndex(p => p[1] == 0x10 && p[3] == 0xff && p[4] == 0xe3);
        Assert.True(start >= 0);
        var bytes = cell.Device.Writes.Skip(start).Where(p => p[1] == 0x10 && p[3] == 0xff)
            .SelectMany(p => p[4] == 0xe3 ? p[9..63] : p[5..62]).ToArray();
        var offset = Enumerable.Range(0, zone).Where(z => (cell.Device.Available & (1 << z)) != 0 && cell.Device.Counts[z] is not (0 or 0x1e))
            .Sum(z => cell.Device.Counts[z]) * 3;
        return new(bytes[offset + 1], bytes[offset], bytes[offset + 2]);
    }
    [Fact]
    public async Task SelectedAsrockZoneBecomesFixedWithoutLeavingDirectOrResettingItsSibling()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        var outside = Setting(a, 2); await r.Start((a, 5), (a, 6)); await r.Tick(.4);
        var before = new AsrockBoundRecovery(r.Directory, a.Id).Pending!; var boundary = a.Device.Writes.Count;
        var group = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:6").Lighting!.GroupId;
        var other = LastDirectColor(a, 6); var stopped = await r.Command(DeviceOperation.StopLighting, [(a, 5)]);
        Assert.Equal(Outcome.WrittenUnverified, stopped.Result!.Outcome);
        Assert.Equal(new RgbColor(50, 25, 10), stopped.Result.Targets.Single().ReportedLighting!.Colors[0]);
        Assert.Equal(new RgbColor(50, 25, 10), LastDirectColor(a, 5)); Assert.Equal(other, LastDirectColor(a, 6));
        Assert.All(a.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); });
        var saved = new AsrockBoundRecovery(r.Directory, a.Id).Pending!;
        Assert.Equal(new byte[] { 6 }, saved.Targets); Assert.Equal(before.Scope, saved.Scope);
        Assert.Equal(SettingsTransfer.Hash(before.Snapshot), SettingsTransfer.Hash(saved.Snapshot));
        Assert.Equal(new BoardLightSetting(1, 50, 25, 10), saved.Desired[5]); Assert.Equal(before.Desired[6], saved.Desired[6]);
        var state = r.Runtime.State(); Assert.False(state.Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
        Assert.Equal(group, state.Endpoints.Single(e => e.Target.EndpointId == "zone:6").Lighting!.GroupId);
        await r.Tick(.3);
        Assert.Equal(DeviceLighting.Frame(new("color-cycle", [new(100, 50, 20)], 4, Brightness: 50), .7), LastDirectColor(a, 6));
        Assert.Equal(new RgbColor(50, 25, 10), LastDirectColor(a, 5));
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 6)])).Result!.Outcome);
        Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(a, 5)); Assert.Equal(outside, Setting(a, 2));
        Assert.Equal(0xfd, a.Device.Order); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task RepeatedReductionsKeepEarlierFixedColorsAndDoNotEnableNativeWritesDuringDirect()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        await r.Start((a, 2), (a, 5), (a, 6)); await r.Tick(.2);
        var red = new LightingProgram("breathing", [new(150, 40, 20)], 4);
        await r.Command(DeviceOperation.SetLighting, [(a, 2)], red);
        await r.Command(DeviceOperation.StopLighting, [(a, 2)]); await r.Tick(.2);
        await r.Command(DeviceOperation.StopLighting, [(a, 5)]); await r.Tick(.2);
        Assert.Equal(new RgbColor(150, 40, 20), LastDirectColor(a, 2)); Assert.Equal(new RgbColor(50, 25, 10), LastDirectColor(a, 5));
        Assert.Equal(new byte[] { 6 }, new AsrockBoundRecovery(r.Directory, a.Id).Pending!.Targets);
        var writes = a.Device.Writes.Count;
        var native = await r.Command(DeviceOperation.SetLighting, [(a, 5)], new("native/asrock-usb/2", [new(20, 40, 60)], 100));
        Assert.False(native.Success); Assert.Equal(writes, a.Device.Writes.Count);
        await r.Command(DeviceOperation.StopLighting, [(a, 6)]);
        Assert.Equal(new BoardLightSetting(1, 150, 40, 20), Setting(a, 2)); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(a, 5));
    }
    [Fact]
    public async Task ADetachedNativeHandoffCanRejoinAndLaterExpansionKeepsItsNewFixedOriginal()
    {
        var a = new Cell(1, true); a.Device.Set(5, new(7, 33, 44, 55, 88));
        await using var r = await new Rig(new(a)).Open(); r.Configure(a, exit: LightingExitAction.RestoreOriginal);
        await Fixed(r, a, 5); await r.Start((a, 5), (a, 6)); await r.Tick(.2);
        var before = new AsrockBoundRecovery(r.Directory, a.Id).Pending!; Assert.NotNull(before.Handoff);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Null(new AsrockBoundRecovery(r.Directory, a.Id).Pending!.Handoff);
        var boundary = a.Device.Writes.Count;
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5))).Result!.Outcome);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 2))).Result!.Outcome);
        Assert.All(a.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); });
        Assert.Equal(new BoardLightSetting(1, 50, 25, 10), new AsrockBoundRecovery(r.Directory, a.Id).Pending!.Desired[5]);
        a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; throw new IOException("lost subsequent frame"); };
        await r.Tick(.2);
        Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(a, 5)); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task ReductionFailureUsesDurableFixedIntentAndLeavesAnotherControllerRunning()
    {
        var a = new Cell(1, true); var b = new Cell(2, true); await using var r = await new Rig(new(a, b)).Open();
        await r.Start((a, 5), (a, 6)); await r.Start((b, 5)); await r.Tick(.4);
        a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; throw new IOException("lost partial stop frame"); };
        var reply = await r.Command(DeviceOperation.StopLighting, [(a, 5)]); Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(a, 5)); Assert.Equal(0, Setting(a, 6).Mode);
        Assert.False(File.Exists(r.JournalPath(a))); Assert.True(File.Exists(r.JournalPath(b)));
        await r.Tick(.3); Assert.Equal(DeviceLighting.Frame(new("color-cycle", [new(100, 50, 20)], 4, Brightness: 50), .7), LastDirectColor(b, 5));
    }
    [Fact]
    public async Task FailedReductionCommitRetainsTheSupersetAndCanBeRecoveredAfterStorageReturns()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 5), (a, 6));
        FileStream? locked = null;
        a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; locked = new FileStream(r.JournalPath(a), FileMode.Open, FileAccess.Read, FileShare.Read); };
        try
        {
            Assert.Equal(Outcome.Unknown, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
            var pending = new AsrockBoundRecovery(r.Directory, a.Id).Pending!;
            Assert.Equal(new byte[] { 5, 6 }, pending.Targets); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), pending.Desired[5]);
        }
        finally { locked?.Dispose(); a.Device.AfterWrite = null; }
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5), (a, 6)])).Result!.Outcome);
        Assert.False(File.Exists(r.JournalPath(a))); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(a, 5));
    }
    [Theory] [InlineData("intent")] [InlineData("frame")] [InlineData("committed")]
    public async Task StartupRecoveryRetainsStoppedColorAtEveryReductionBoundary(string stage)
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Reduction-" + Guid.NewGuid().ToString("N"));
        var driver = new AsrockDeviceDriver(a.Id, a.Connection, directory, Boot, backend); await driver.PollAsync(default);
        var transaction = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange);
        var scope = new AsrockRecoveryScope(driver.BindingCandidate!, a.Connection.ConnectionId, a.Connection.Confidence, Guid.NewGuid().ToString("N"));
        var store = new AsrockBoundRecovery(directory, a.Id); var writer = store.ForStream(scope, [5, 6]);
        var desired = transaction.Snapshot.Zones.ToDictionary(z => z.Address, z => z.Address is 5 or 6 ? new BoardLightSetting(0, 0, 0, 0) : z.Setting);
        transaction.Begin(snapshot => writer.Save(Boot, snapshot, desired), true);
        desired[5] = new(1, 60, 30, 90); writer.Save(Boot, transaction.Snapshot, desired);
        if (stage != "intent")
            foreach (var packet in AsrockDirectProtocol.Frame(transaction.Snapshot.Zones.Select(z => new AsrockDirectProtocol.Zone(z.Address, z.Count,
                z.Address == 5 ? new(60, 30, 90) : new(20, 40, 60))).ToArray(), 0)) a.Device.Exchange(packet);
        if (stage == "committed") writer.Reduce([6]);
        await using var r = new Rig(backend, directory); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.Null(r.Runtime.State().LifecycleError); Assert.Equal(new BoardLightSetting(1, 60, 30, 90), Setting(a, 5));
        Assert.Equal(0, Setting(a, 6).Mode); Assert.Equal(0xfd, a.Device.Order); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task ReplacedJournalBeforeReductionOutputDoesNotAllowAnyStaleWrite()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 5), (a, 6));
        var writes = a.Device.Writes.Count; string? intended = null;
        r.Backend.BeforeTransport = packet =>
        {
            if (packet[1] != 0x10 || packet[3] != 0xff) return;
            r.Backend.BeforeTransport = null; intended = File.ReadAllText(r.JournalPath(a));
            var record = new AsrockBoundRecovery(r.Directory, a.Id).Pending!;
            ServiceEnvironment.Write(r.JournalPath(a), record with { Scope = record.Scope with { OwnerId = Guid.NewGuid().ToString("N") } });
        };
        try
        {
            Assert.Equal(Outcome.Unknown, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
            Assert.NotNull(intended); Assert.Equal(writes, a.Device.Writes.Count);
        }
        finally { r.Backend.BeforeTransport = null; if (intended is not null) File.WriteAllText(r.JournalPath(a), intended); }
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5), (a, 6)])).Result!.Outcome);
    }
}
