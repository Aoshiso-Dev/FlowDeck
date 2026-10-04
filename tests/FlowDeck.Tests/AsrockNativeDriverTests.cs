using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    private static string NativePath(Rig r, Cell a) => Path.Combine(r.Directory, "asrock-native-" + SettingsTransfer.Hash(a.Id).ToLowerInvariant() + ".json");
    private static Task<DeviceServiceReply> Fixed(Rig r, Cell a, byte zone, int brightness = 100) =>
        r.Command(DeviceOperation.SetLighting, [(a, zone)], new("fixed", [new(100, 60, 20)], Brightness: brightness));

    [Fact]
    public async Task NativeEditsKeepTheFirstOriginalAndRestoreOnlyTheSelectedZone()
    {
        var a = new Cell(1, true); var b = new Cell(2, true); a.Device.Set(5, new(7, 12, 34, 56, 99));
        await using var r = await new Rig(new(a, b)).Open(); r.Configure(a, exit: LightingExitAction.RestoreOriginal);
        var original = Setting(a, 5); var unselected = Setting(a, 2); var order = a.Device.Order;
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5, 50)).Result!.Outcome);
        Assert.Equal(new BoardLightSetting(1, 50, 30, 10), Setting(a, 5));
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 6)).Result!.Outcome);
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5, 75)).Result!.Outcome);
        var saved = new AsrockNativeRecovery(r.Directory, a.Id).Pending!;
        Assert.Equal(original, saved.Entries.Single(e => e.Zone == 5).Original);
        Assert.Equal(new BoardLightSetting(1, 50, 30, 10), saved.Entries.Single(e => e.Zone == 5).Before);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Equal(original, Setting(a, 5)); Assert.Equal(unselected, Setting(a, 2)); Assert.Equal(order, a.Device.Order);
        Assert.Equal(new BoardLightSetting(1, 100, 60, 20), Setting(a, 6)); Assert.Empty(b.Device.Writes);
        Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 6);
        Assert.All(a.Device.Writes, p => { Assert.Equal(0x10, p[1]); Assert.Contains(p[3], new byte[] { 5, 6 }); });
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public async Task PackagedNativeModesPreserveSpeedAndScaleColorExactlyOnce(int mode)
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open();
        var effect = BoardLighting.Effects.Single(e => e.Id == mode);
        var program = new LightingProgram("native/asrock-usb/" + mode, [new(100, 60, 20)], 117, Brightness: effect.Color ? 50 : 100);
        var result = (await r.Command(DeviceOperation.SetLighting, [(a, 5)], program)).Result!;
        Assert.Equal(Outcome.Confirmed, result.Outcome);
        Assert.Equal(new BoardLightSetting((byte)mode, effect.Color ? 50 : 100, effect.Color ? 30 : 60, effect.Color ? 10 : 20, 117), Setting(a, 5));
        Assert.Equal(117, result.Targets.Single().ReportedLighting!.Speed);
        Assert.Single(a.Device.Writes); Assert.Equal(0xfd, a.Device.Order);
    }
    [Theory]
    [InlineData("native/asrock-usb/6", 100)] [InlineData("native/asrock-usb/15", 100)]
    [InlineData("native/asrock-usb/02", 100)] [InlineData("native/other/2", 100)]
    [InlineData("native/asrock-usb/4", 50)] [InlineData("native/asrock-usb/14", 50)]
    public async Task UnsupportedNativeModeOrColorlessDimmingIsRejectedWithoutWrites(string effect, int brightness)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        Assert.False((await r.Command(DeviceOperation.SetLighting, [(a, 5)], new(effect, [new(1, 2, 3)], Brightness: brightness))).Success);
        Assert.Empty(a.Device.Writes); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task CommonOffAndFixedExitAreReadBackWithTheirActualColor()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open();
        var d = r.Driver(a); var ids = d.Observe()!.Endpoints.Select(e => e.Id).ToArray();
        d.ConfigureLighting(d.BindingCandidate, ids.Select(id => new LightingEndpointConfiguration(new(a.Id, id), true, new(LightingExitAction.FixedColor, new(9, 8, 7)))),
            ids.Select(id => new RegisteredEndpoint(id, id, EndpointKind.Lighting, Setup: new(LedCount: a.Device.Counts[id[^1] - '0'], LedCountConfirmed: true))));
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.SetLighting, [(a, 5)], new("off", []))).Result!.Outcome);
        Assert.Equal(new BoardLightSetting(0, 0, 0, 0), Setting(a, 5));
        var stop = (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!;
        Assert.Equal(Outcome.Confirmed, stop.Outcome); Assert.Equal(new RgbColor(9, 8, 7), stop.Targets.Single().ReportedLighting!.Colors.Single());
        Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task NativeRecoveryIntentIsDurableBeforeTheFirstMutation()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); var checkedRecord = false;
        a.Device.BeforeWrite = _ =>
        {
            var saved = new AsrockNativeRecovery(r.Directory, a.Id).Pending!;
            Assert.Equal(new BoardLightSetting(1, 100, 60, 20), saved.Entries.Single().Intended);
            Assert.Equal(a.Connection.ConnectionId, saved.Scope.ConnectionId); checkedRecord = true;
        };
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5)).Result!.Outcome); Assert.True(checkedRecord); a.Device.BeforeWrite = null;
        Assert.Throws<IOException>(() => r.Configure(a));
    }
    [Fact]
    public async Task ForeignNativeJournalCreatedAfterDiscoveryIsNotOverwritten()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        Directory.CreateDirectory(r.Directory); File.WriteAllText(NativePath(r, a), "foreign-owner");
        Assert.Equal(Outcome.Rejected, (await Fixed(r, a, 5)).Result!.Outcome);
        Assert.Empty(a.Device.Writes); Assert.Equal("foreign-owner", File.ReadAllText(NativePath(r, a)));
    }
    [Theory] [InlineData("setting")] [InlineData("order")] [InlineData("count")] [InlineData("connection")]
    public async Task QueuedNativeCommandRechecksItsPhysicalStateAndCancelsOnlyUnsentIntent(string change)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        r.Backend.BeforeTransport = p =>
        {
            if (p[1] != 0x10) return; r.Backend.BeforeTransport = null;
            if (change == "setting") a.Device.Set(5, new(1, 90, 80, 70));
            if (change == "order") a.Device.Order ^= 0x20;
            if (change == "count") a.Device.Counts[5]++;
            if (change == "connection") a.Connection = a.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        };
        Assert.Equal(Outcome.Rejected, (await Fixed(r, a, 5)).Result!.Outcome);
        Assert.Empty(a.Device.Writes); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task CancelledNativeUpdateRetainsThePreviousOriginalAndOwnership()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        var before = File.ReadAllText(NativePath(r, a)); var boundary = a.Device.Writes.Count; using var cancelled = new CancellationTokenSource();
        r.Backend.BeforeTransport = p => { if (p[1] != 0x10) return; r.Backend.BeforeTransport = null; cancelled.Cancel(); };
        var result = await r.Driver(a).ApplyAsync(new("zone:5", DeviceOperation.SetLighting, Lighting: new("fixed", [new(8, 9, 10)])), cancelled.Token);
        Assert.Equal(Outcome.Rejected, result.Outcome); Assert.Equal(boundary, a.Device.Writes.Count);
        Assert.Equal(before, File.ReadAllText(NativePath(r, a)));
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
    }
    [Fact]
    public async Task LostNativeAcknowledgementCanBeConfirmedByFreshReadback()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; throw new IOException("lost ack"); };
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5)).Result!.Outcome); Assert.True(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task UnknownNativeWriteRetainsItsIntentAndCanBeRecovered()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        a.Device.AfterWrite = _ =>
        {
            a.Device.AfterWrite = null;
            r.Backend.BeforeTransport = p => { if (p[1] != 0x11) return; r.Backend.BeforeTransport = null; throw new IOException("read timeout"); };
        };
        Assert.Equal(Outcome.Unknown, (await Fixed(r, a, 5)).Result!.Outcome); Assert.True(File.Exists(NativePath(r, a)));
        Assert.False((await Fixed(r, a, 5, 50)).Success);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Equal(0, Setting(a, 5).Mode); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task ForeignNativeChangesBlockOnlyTheirOwnRecoveryEntry()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); await Fixed(r, a, 6);
        var foreign = new BoardLightSetting(7, 80, 40, 20, 19); a.Device.Set(5, foreign); var boundary = a.Device.Writes.Count;
        var result = await r.Driver(a).ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.Equal(Outcome.Unknown, result.Single(v => v.EndpointId == "zone:5").Result.Outcome);
        Assert.Equal(Outcome.Confirmed, result.Single(v => v.EndpointId == "zone:6").Result.Outcome);
        Assert.Equal(foreign, Setting(a, 5)); Assert.Equal(0, Setting(a, 6).Mode);
        Assert.All(a.Device.Writes.Skip(boundary), p => Assert.Equal(6, p[3]));
        Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 5);
    }
    [Fact]
    public async Task SessionExitReportsTheNativeRecoveryCauseAndKeepsSuccessfulSiblingRelease()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); await Fixed(r, a, 6);
        var foreign = new BoardLightSetting(7, 80, 40, 20, 19); a.Device.Set(5, foreign); var boundary = a.Device.Writes.Count;
        var reply = await r.Command(DeviceOperation.ReleaseSession, []);
        Assert.False(reply.Success);
        Assert.Contains(a.Id, reply.Error); Assert.Contains("zone:5", reply.Error);
        Assert.Contains("Native照明に別の設定または構成が見つかりました。上書きしません", reply.Error);
        Assert.DoesNotContain("zone:6", reply.Error);
        var state = r.Runtime.State(); Assert.Equal(reply.Error, state.LifecycleError);
        var failed = state.Endpoints.Single(e => e.Target.EndpointId == "zone:5");
        var restored = state.Endpoints.Single(e => e.Target.EndpointId == "zone:6");
        Assert.True(failed.Owned); Assert.Equal(Outcome.Unknown, failed.Outcome);
        Assert.False(restored.Owned); Assert.Equal(Outcome.Confirmed, restored.Outcome);
        Assert.Equal(foreign, Setting(a, 5)); Assert.Equal(0, Setting(a, 6).Mode);
        Assert.All(a.Device.Writes.Skip(boundary), p => Assert.Equal(6, p[3]));
        Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 5);
    }
    [Fact]
    public async Task WhiteExitReadBackAsBlackReportsBothValuesAndRetryPreservesTheUnconfirmedZone()
    {
        var a = new Cell(1, true);
        a.Device.Available |= 1 << 3; a.Device.Counts[3] = 80;
        var white = new BoardLightSetting(1, 255, 255, 255);
        var black = new BoardLightSetting(1, 0, 0, 0);
        a.Device.Set(3, white); a.Device.Set(5, white);
        await using var r = await new Rig(new(a)).Open(); r.Configure(a, exit: LightingExitAction.RestoreOriginal);
        Assert.True((await r.Command(DeviceOperation.SetLighting, [(a, 3)], new("fixed", [new(0, 107, 255)]))).Success);
        Assert.True((await r.Command(DeviceOperation.SetLighting, [(a, 3), (a, 5)], new("off", []))).Success);
        a.Device.AfterWrite = packet =>
        {
            if (packet[1] == 0x10 && packet[3] == 3 && packet[4] == 1) a.Device.Set(3, black);
        };
        var reply = await r.Command(DeviceOperation.ReleaseSession, []);
        Assert.False(reply.Success);
        Assert.Contains("zone:3 現在[Mode=1 RGB=#000000 Speed=224] / 期待[Mode=1 RGB=#FFFFFF Speed=224]", reply.Error);
        Assert.Contains("Native照明の終了設定を確認できません", reply.Error);
        Assert.DoesNotContain("構成(期待→現在)", reply.Error);
        Assert.Equal(black, Setting(a, 3)); Assert.Equal(white, Setting(a, 5));
        var state = r.Runtime.State();
        Assert.True(state.Endpoints.Single(e => e.Target.EndpointId == "zone:3").Owned);
        Assert.False(state.Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
        var pending = new AsrockNativeRecovery(r.Directory, a.Id).Pending!;
        Assert.Single(pending.Entries, e => e.Zone == 3);
        var saved = File.ReadAllText(NativePath(r, a)); var writes = a.Device.Writes.Count;
        var retry = Assert.Single(await r.Driver(a).ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(Outcome.Unknown, retry.Result.Outcome);
        Assert.Contains("上書きしません", retry.Result.Message);
        Assert.Contains("現在[Mode=1 RGB=#000000 Speed=224]", retry.Result.Message);
        Assert.Contains("期待[Mode=1 RGB=#006BFF Speed=224 | Mode=0 RGB=#000000 Speed=224 | Mode=1 RGB=#FFFFFF Speed=224]", retry.Result.Message);
        Assert.Equal(writes, a.Device.Writes.Count); Assert.Equal(saved, File.ReadAllText(NativePath(r, a)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeApplyReadbackMismatchReportsValuesEvenAfterAnAcknowledgementError(bool lostAcknowledgement)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        a.Device.AfterWrite = _ =>
        {
            a.Device.Set(5, new(1, 0, 0, 0));
            if (lostAcknowledgement) throw new IOException("lost ack");
        };
        var result = await r.Driver(a).ApplyAsync(new("zone:5", DeviceOperation.SetLighting,
            Lighting: new("fixed", [new(255, 255, 255)])), default);
        Assert.Equal(Outcome.Unknown, result.Outcome);
        Assert.Contains("zone:5 現在[Mode=1 RGB=#000000 Speed=224] / 期待[Mode=1 RGB=#FFFFFF Speed=224]", result.Message);
        Assert.Contains(lostAcknowledgement ? "lost ack" : "Native照明の読み戻しが一致しません", result.Message);
        Assert.Single(a.Device.Writes); Assert.True(File.Exists(NativePath(r, a)));
    }
    [Theory]
    [InlineData("available", "Available=0x64→0x44")]
    [InlineData("order", "Order=0xFD→0xDD")]
    [InlineData("count", "LED数[5]=15→16")]
    public async Task NativeRecoveryReportsTheChangedLayoutWithoutWritingOrRemovingItsRecord(string change, string detail)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        var saved = File.ReadAllText(NativePath(r, a)); var writes = a.Device.Writes.Count;
        if (change == "available") a.Device.Available ^= 1 << 5;
        if (change == "order") a.Device.Order ^= 1 << 5;
        if (change == "count") a.Device.Counts[5]++;
        var result = Assert.Single(await r.Driver(a).ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(Outcome.Unknown, result.Result.Outcome);
        Assert.Contains("zone:5 現在[", result.Result.Message);
        Assert.Contains("構成(期待→現在)[" + detail + "]", result.Result.Message);
        if (change == "available") Assert.Contains("現在[未取得]", result.Result.Message);
        Assert.Equal(writes, a.Device.Writes.Count); Assert.Equal(saved, File.ReadAllText(NativePath(r, a)));
    }
    [Fact]
    public async Task NativeRecoveryRechecksTheDiskOwnerAfterTransportQueueing()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); var boundary = a.Device.Writes.Count;
        r.Backend.BeforeTransport = p =>
        {
            if (p[1] != 0x10) return; r.Backend.BeforeTransport = null;
            var replacement = new AsrockNativeRecovery(r.Directory, a.Id); var old = replacement.Pending!; replacement.Complete(old, 5);
            var state = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange).Snapshot;
            replacement.Prepare(Boot, old.Scope with { OwnerId = Guid.NewGuid().ToString("N") }, state, 5, old.Entries[0].Intended, old.Entries[0].Exit);
        };
        Assert.Equal(Outcome.Unknown, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.True(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task NativeAndDirectCanOwnDifferentZonesAndReleaseInDependencyOrder()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 6);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5))).Result!.Outcome);
        Assert.Equal(Outcome.Rejected, (await r.Command(DeviceOperation.StopLighting, [(a, 6)])).Result!.Outcome);
        Assert.True(File.Exists(NativePath(r, a))); Assert.True(File.Exists(r.JournalPath(a)));
        var results = await r.Driver(a).ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.All(results, v => Assert.Equal(Outcome.Confirmed, v.Result.Outcome)); Assert.Equal(2, results.Length);
        Assert.Equal(0, Setting(a, 5).Mode); Assert.Equal(0, Setting(a, 6).Mode);
        Assert.False(File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task NativeRecoveryWaitsUntilPartialDirectRecoveryFinishes()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 6); await r.Start((a, 5));
        a.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5 && p[4] == 0) throw new IOException("zone 5 unavailable"); };
        var first = await r.Driver(a).ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.Equal(Outcome.Unknown, first.Single(v => v.EndpointId == "zone:6").Result.Outcome);
        Assert.Equal(new BoardLightSetting(1, 100, 60, 20), Setting(a, 6)); Assert.True(File.Exists(NativePath(r, a)));
        a.Device.BeforeWrite = null;
        Assert.All(await r.Driver(a).ReleaseAsync(DeviceReleaseReason.ServiceStopping, default), v => Assert.Equal(Outcome.Confirmed, v.Result.Outcome));
        Assert.Equal(0, Setting(a, 6).Mode); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task NativeAndDirectCanReplaceEachOtherWithoutASeparateStopCommand()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5))).Result!.Outcome);
        Assert.False(File.Exists(NativePath(r, a))); Assert.True(File.Exists(r.JournalPath(a)));
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, a, 5)).Result!.Outcome);
        Assert.False(File.Exists(r.JournalPath(a))); Assert.True(File.Exists(NativePath(r, a)));
        Assert.Equal(new BoardLightSetting(1, 100, 60, 20), Setting(a, 5));
    }
    private static async Task PrepareAbandonedNative(Cell a, Backend backend, string directory, bool written)
    {
        var driver = new AsrockDeviceDriver(a.Id, a.Connection, directory, Boot, backend); await driver.PollAsync(default);
        var state = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange).Snapshot;
        var scope = new AsrockRecoveryScope(driver.BindingCandidate!, a.Connection.ConnectionId, a.Connection.Confidence, Guid.NewGuid().ToString("N"));
        new AsrockNativeRecovery(directory, a.Id).Prepare(Boot, scope, state, 5, new(1, 100, 60, 20), state.Zones.Single(z => z.Address == 5).Setting);
        if (written) a.Device.Set(5, new(1, 100, 60, 20));
    }
    [Fact]
    public async Task NativeCommandAndRecoveryUseTheRetainedTransportThreadIncludingNestedGuardReads()
    {
        var a = new Cell(1, false); var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Native-Actor-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var backend = new AsrockHidDeviceBackend(() => [new("recording-native", null)], _ =>
            { a.Device.OwnerThread = Environment.CurrentManagedThreadId; return a.Device; }, () => { });
            await using var provider = new AsrockDeviceProvider(Installation, directory, Boot, backend);
            await provider.PollAsync(default); var driver = Assert.IsType<AsrockDeviceDriver>(Assert.Single(provider.CachedDrivers));
            await using (driver)
            {
                await driver.PollAsync(default); var ids = driver.Observe()!.Endpoints.Select(e => e.Id).ToArray();
                driver.ConfigureLighting(driver.BindingCandidate, ids.Select(id => new LightingEndpointConfiguration(new(driver.DeviceId, id), true, new(LightingExitAction.Off))),
                    ids.Select(id => new RegisteredEndpoint(id, id, EndpointKind.Lighting, Setup: new(LedCount: a.Device.Counts[id[^1] - '0'], LedCountConfirmed: true))));
                var result = await driver.ApplyAsync(new("zone:5", DeviceOperation.SetLighting, Lighting: new("fixed", [new(10, 20, 30)])), default).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(Outcome.Confirmed, result.Outcome);
                Assert.All(await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default).WaitAsync(TimeSpan.FromSeconds(5)), v => Assert.Equal(Outcome.Confirmed, v.Result.Outcome));
                Assert.Equal(0, Setting(a, 5).Mode); Assert.Equal(2, a.Device.Writes.Count);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task FailedNativeJournalSaveDoesNotReachTheTransport()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        File.WriteAllText(r.Directory, "blocks directory creation");
        try { Assert.Equal(Outcome.Rejected, (await Fixed(r, a, 5)).Result!.Outcome); Assert.Empty(a.Device.Writes); }
        finally { File.Delete(r.Directory); }
    }
    [Fact]
    public async Task FailedNativeRecordRemovalRetriesWithoutRepeatingTheVerifiedExitWrite()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        using (var locked = new FileStream(NativePath(r, a), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await r.Driver(a).ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
            Assert.Equal(Outcome.Unknown, Assert.Single(result).Result.Outcome); Assert.Equal(0, Setting(a, 5).Mode);
        }
        var boundary = a.Device.Writes.Count;
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task CorruptNativeJournalDoesNotHideTheOtherControllerOrPermitWrites()
    {
        var a = new Cell(1, true); var b = new Cell(2, true); await using var r = new Rig(new(a, b));
        Directory.CreateDirectory(r.Directory); File.WriteAllText(NativePath(r, a), "broken-native-record"); await r.Open(configure: false);
        Assert.Equal(2, r.Runtime.State().Inventory.Devices.Length); Assert.Throws<IOException>(() => r.Configure(a));
        r.Configure(b); r.Runtime.Refresh(); Assert.Equal(Outcome.Confirmed, (await Fixed(r, b, 5)).Result!.Outcome);
        Assert.Empty(a.Device.Writes); Assert.All(await r.Driver(a).ReleaseAsync(DeviceReleaseReason.StartupRecovery, default), v => Assert.Equal(Outcome.Unknown, v.Result.Outcome));
    }
    [Theory]
    [InlineData(false, "same", true, true)] [InlineData(false, "same", false, true)]
    [InlineData(true, "reconnect", true, true)] [InlineData(false, "reconnect", true, false)]
    [InlineData(true, "boot", true, false)] [InlineData(true, "revision", true, false)]
    public async Task NativeColdRecoveryRequiresTheRecordedPhysicalIdentityAndBoot(bool serial, string change, bool written, bool recoverable)
    {
        var a = new Cell(1, serial); var backend = new Backend(a); var original = Setting(a, 5);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Native-Cold-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedNative(a, backend, directory, written);
        if (change == "reconnect") a.Connection = a.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        if (change == "revision") a.Connection = a.Connection with { DeviceRevision = 99 };
        await using var r = new Rig(backend, directory, change == "boot" ? "later-boot" : Boot);
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.Equal(!recoverable, r.Runtime.State().LifecycleError is not null);
        Assert.Equal(!recoverable, File.Exists(NativePath(r, a)));
        if (recoverable) Assert.Equal(original, Setting(a, 5)); else Assert.Empty(a.Device.Writes);
    }
}
