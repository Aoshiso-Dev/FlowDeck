using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    private static EndpointLightingProgram[] ExpansionPrograms(params byte[] zones) => zones.Select(z => new EndpointLightingProgram("zone:" + z,
        new("breathing", [new(30, 60, 90)], 3))).ToArray();
    private static DeviceLightingFrame ExpansionFrame(long number, params byte[] zones) => new(number, new(10, 20, 30),
        () => new(ValidFor: TimeSpan.FromMilliseconds(500)), zones.Select(z => new EndpointLightingFrame("zone:" + z, new(10, 20, 30), new(z, 40, 80))).ToArray());

    [Fact]
    public async Task PreparingAnExpansionDoesNotChangeOutputRecordsOrTheOriginalStream()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); var driver = (IDeviceLightingExpansionDriver)r.Driver(a);
        var old = await driver.OpenLightingCompositionAsync(ExpansionPrograms(5), default);
        await old.WriteAsync(ExpansionFrame(1, 5), default);
        var record = File.ReadAllText(r.JournalPath(a)); var boundary = a.Device.Writes.Count;
        Assert.NotNull(driver.ValidateLightingExpansion(old, ExpansionPrograms(6)));
        var replacement = driver.CreateLightingExpansion(old, ExpansionPrograms(5, 6));
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.Equal(record, File.ReadAllText(r.JournalPath(a)));
        Assert.All(await old.WriteAsync(ExpansionFrame(2, 5), default), x => Assert.Equal(Outcome.WrittenUnverified, x.Result.Outcome));
        Assert.All(await replacement.WriteAsync(ExpansionFrame(3, 5, 6), default), x => Assert.Equal(Outcome.WrittenUnverified, x.Result.Outcome));
        Assert.All(await replacement.EndAsync(DeviceLightingStopReason.Normal, default), x => Assert.Equal(Outcome.Confirmed, x.Result.Outcome));
        Assert.Equal(new BoardLightSetting(1, 5, 40, 80), Setting(a, 5)); Assert.Equal(new BoardLightSetting(1, 6, 40, 80), Setting(a, 6));
        Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EndingAnExpansionBeforeItsFirstAcceptedFrameDoesNotClaimUntouchedZones(bool expire)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); var driver = (IDeviceLightingExpansionDriver)r.Driver(a);
        var original = Setting(a, 6); var old = await driver.OpenLightingCompositionAsync(ExpansionPrograms(5), default);
        await old.WriteAsync(ExpansionFrame(1, 5), default);
        var replacement = driver.CreateLightingExpansion(old, ExpansionPrograms(5, 6));
        if (expire)
        {
            var frame = new DeviceLightingFrame(2, new(10, 20, 30), () => new("expired", true),
                new byte[] { 5, 6 }.Select(z => new EndpointLightingFrame("zone:" + z, new(10, 20, 30), new(100, 100, 100))).ToArray());
            Assert.All(await replacement.WriteAsync(frame, default), x => Assert.Equal(DeviceDriverFailure.LightingFrameExpired, x.Result.Failure));
        }
        Assert.Equal(new byte[] { 5 }, new AsrockBoundRecovery(r.Directory, a.Id).Pending!.Targets);
        Assert.All(await replacement.EndAsync(DeviceLightingStopReason.Failure, default), x => Assert.Equal(Outcome.Confirmed, x.Result.Outcome));
        Assert.Equal(original, Setting(a, 6)); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExpansionTransfersNativeOwnershipBeforeWritingAndPreservesTheFirstOriginal(bool nativeFirstZone, bool failFrame)
    {
        var a = new Cell(1, true); a.Device.Set(6, new(7, 33, 44, 55, 88)); var original = Setting(a, 6);
        await using var r = await new Rig(new(a)).Open(); r.Configure(a, exit: LightingExitAction.RestoreOriginal);
        if (nativeFirstZone) await Fixed(r, a, 5);
        await Fixed(r, a, 6); await Fixed(r, a, 6, 25); await r.Start((a, 5));
        var owner = new AsrockBoundRecovery(r.Directory, a.Id).Pending!.Scope; var observed = false;
        a.Device.BeforeWrite = p =>
        {
            var saved = new AsrockBoundRecovery(r.Directory, a.Id).Pending!;
            Assert.Equal(owner, saved.Scope); Assert.Equal(new byte[] { 5, 6 }, saved.Targets.Order());
            Assert.Equal(original, saved.Desired[6]); Assert.False(File.Exists(NativePath(r, a))); observed = true; a.Device.BeforeWrite = null;
        };
        if (failFrame) a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; throw new IOException("lost expanded frame"); };
        var reply = await r.Start((a, 6)); Assert.True(observed);
        Assert.Equal(failFrame ? Outcome.Unknown : Outcome.WrittenUnverified, reply.Result!.Outcome);
        if (!failFrame) Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5), (a, 6)])).Result!.Outcome);
        Assert.Equal(failFrame ? original : new BoardLightSetting(1, 50, 25, 10), Setting(a, 6));
        Assert.False(File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task ExpiryAfterExpansionIntentRestoresAllOwnedZonesWithoutSendingTheExpandedFrame()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 5)); var boundary = a.Device.Writes.Count; var expired = false;
        r.Backend.BeforeTransport = _ =>
        {
            if (new AsrockBoundRecovery(r.Directory, a.Id).Pending?.Targets.Contains((byte)6) != true) return;
            expired = true; r.Backend.BeforeTransport = null; r.Clock.Advance(.6);
        };
        Assert.Equal(Outcome.Rejected, (await r.Start((a, 6))).Result!.Outcome); Assert.True(expired);
        Assert.DoesNotContain(a.Device.Writes.Skip(boundary), p => p[1] == 0x10 && p[3] == 0xff);
        Assert.Equal(0, Setting(a, 5).Mode); Assert.Equal(0, Setting(a, 6).Mode); Assert.False(File.Exists(r.JournalPath(a)));
        Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Owned));
    }
    [Fact]
    public async Task InterruptedExpansionHandoffRetainsBothRecordsAndCanFinishRecovery()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open();
        await Fixed(r, a, 5); await Fixed(r, a, 6); await r.Start((a, 5)); var boundary = a.Device.Writes.Count;
        using (var locked = new FileStream(NativePath(r, a), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(Outcome.Unknown, (await r.Start((a, 6))).Result!.Outcome);
        Assert.Equal(boundary, a.Device.Writes.Count);
        Assert.Equal(new byte[] { 5, 6 }, new AsrockBoundRecovery(r.Directory, a.Id).Pending!.Targets.Order());
        Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 6);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5), (a, 6)])).Result!.Outcome);
        Assert.Equal(0, Setting(a, 5).Mode); Assert.Equal(0, Setting(a, 6).Mode);
        Assert.False(File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task AFreshDriverRecoversAnExpansionInterruptedBetweenTheTwoDurableRecords()
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Expand-Cold-" + Guid.NewGuid().ToString("N"));
        var handoff = await PrepareAbandonedHandoff(a, backend, directory, "direct");
        var store = new AsrockBoundRecovery(directory, a.Id); var old = store.Pending!;
        var native = new AsrockNativeRecovery(directory, a.Id);
        var writer = store.ForStream(old.Scope, old.Targets, native, old.Handoff);
        var desired = Contract.Copy(old.Desired); desired[6] = handoff.Before.Entries.Single(e => e.Zone == 6).Exit;
        var nativePath = Path.Combine(directory, "asrock-native-" + SettingsTransfer.Hash(a.Id).ToLowerInvariant() + ".json");
        using (var locked = new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => writer.Expand([5, 6], desired));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(new byte[] { 5, 6 }, new AsrockBoundRecovery(directory, a.Id).Pending!.Targets);
        Assert.Single(new AsrockNativeRecovery(directory, a.Id).Pending!.Entries, e => e.Zone == 6);
        await using var r = new Rig(backend, directory); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.Null(r.Runtime.State().LifecycleError);
        Assert.Equal(desired[5], Setting(a, 5)); Assert.Equal(desired[6], Setting(a, 6)); Assert.Equal(0xfd, a.Device.Order);
        Assert.False(File.Exists(nativePath)); Assert.False(File.Exists(r.JournalPath(a)));
    }
}
