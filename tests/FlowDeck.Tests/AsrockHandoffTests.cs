using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task NativeToDirectHandoffKeepsTheFirstOriginalAndTheUnselectedNativeOwner(bool normal)
    {
        var a = new Cell(1, true); a.Device.Set(5, new(7, 33, 44, 55, 88)); var original = Setting(a, 5);
        await using var r = await new Rig(new(a)).Open(); r.Configure(a, exit: LightingExitAction.RestoreOriginal);
        await Fixed(r, a, 5); await Fixed(r, a, 5, 25); await Fixed(r, a, 6); var unselected = Setting(a, 6);
        var checkedBeforeWrite = false;
        a.Device.BeforeWrite = _ =>
        {
            var saved = new AsrockBoundRecovery(r.Directory, a.Id).Pending!;
            Assert.NotNull(saved.Handoff); Assert.Equal(original, saved.Desired[5]);
            Assert.Equal(original, saved.Handoff!.Before.Entries.Single(e => e.Zone == 5).Original);
            Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 6);
            checkedBeforeWrite = true; a.Device.BeforeWrite = null;
        };
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5))).Result!.Outcome); Assert.True(checkedBeforeWrite);
        if (normal) Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        else
        {
            // End the selected stream directly so the independently owned native zone remains owned.
            a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; throw new IOException("frame lost"); };
            await r.Tick(.1); await r.Tick(.1);
        }
        Assert.Equal(normal ? new BoardLightSetting(1, 50, 25, 10) : original, Setting(a, 5));
        Assert.Equal(unselected, Setting(a, 6)); Assert.Equal(0xfd, a.Device.Order);
        Assert.False(File.Exists(r.JournalPath(a))); Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 6);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 6)])).Result!.Outcome);
    }
    [Fact]
    public async Task OpeningAHandoffDoesNotChangeOutputOrMoveDurableOwnership()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        var before = File.ReadAllText(NativePath(r, a)); var boundary = a.Device.Writes.Count;
        var stream = await r.Driver(a).OpenLightingStreamAsync(["zone:5"], new("breathing", [new(30, 60, 90)], 2), default);
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.Equal(before, File.ReadAllText(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
        Assert.All(await stream.EndAsync(DeviceLightingStopReason.ServiceStopping, default), e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
        Assert.Equal(0, Setting(a, 5).Mode); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExpiryAfterDurableHandoffDoesNotEnterDirectAndReleasesThePreviousNativeOutput(bool outside)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        if (outside) await Fixed(r, a, 6);
        var boundary = a.Device.Writes.Count;
        r.Backend.BeforeTransport = p =>
        {
            if (p[1] != 0x15) return; r.Backend.BeforeTransport = null;
            Assert.True(File.Exists(r.JournalPath(a))); Assert.Equal(outside, File.Exists(NativePath(r, a))); r.Clock.Advance(.6);
        };
        Assert.Equal(Outcome.Rejected, (await r.Start((a, 5))).Result!.Outcome);
        Assert.All(a.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(5, p[3]); Assert.Equal(0, p[4]); });
        Assert.Equal(0, Setting(a, 5).Mode); Assert.Equal(0xfd, a.Device.Order);
        Assert.Equal(outside, File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
        if (outside)
        {
            Assert.Single(new AsrockNativeRecovery(r.Directory, a.Id).Pending!.Entries, e => e.Zone == 6);
            Assert.Equal(new BoardLightSetting(1, 100, 60, 20), Setting(a, 6));
        }
        Assert.False(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
    }
    [Fact]
    public async Task FailedNativeTransferNeverEntersDirectAndCanFinishItsExitAfterTheFileUnlocks()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); var boundary = a.Device.Writes.Count;
        using (var locked = new FileStream(NativePath(r, a), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(Outcome.Unknown, (await r.Start((a, 5))).Result!.Outcome);
            Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
            Assert.True(File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
            Assert.All(a.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(5, p[3]); Assert.Equal(0, p[4]); });
        }
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.False(File.Exists(NativePath(r, a)));
    }
    [Fact]
    public async Task OpeningFailureReleasesOnlyThePreviouslyOwnedTargets()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); await Fixed(r, a, 6);
        var boundary = a.Device.Writes.Count;
        r.Backend.BeforeTransport = p => { if (p[1] != 0x14) return; r.Backend.BeforeTransport = null; throw new IOException("failed opening read"); };
        Assert.Equal(Outcome.Unknown, (await r.Start((a, 2), (a, 5))).Result!.Outcome);
        Assert.Equal(0, Setting(a, 5).Mode); Assert.Equal(new BoardLightSetting(1, 100, 60, 20), Setting(a, 6));
        Assert.All(a.Device.Writes.Skip(boundary), p => Assert.Equal(5, p[3]));
        var states = r.Runtime.State().Endpoints; Assert.False(states.Single(e => e.Target.EndpointId == "zone:5").Owned);
        Assert.True(states.Single(e => e.Target.EndpointId == "zone:6").Owned);
    }
    [Fact]
    public async Task FailedOpeningAndFailedPreviousExitKeepARecoveryObligation()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        r.Backend.BeforeTransport = p => { if (p[1] != 0x14) return; r.Backend.BeforeTransport = null; throw new IOException("failed opening read"); };
        a.Device.BeforeWrite = _ => throw new IOException("exit unavailable");
        Assert.Equal(Outcome.Unknown, (await r.Start((a, 5))).Result!.Outcome);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned); Assert.True(File.Exists(NativePath(r, a)));
        a.Device.BeforeWrite = null;
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
    }
    [Fact]
    public async Task UnselectedNativeAnimationRejectsAHandoffWithoutChangingEitherOwner()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        await r.Command(DeviceOperation.SetLighting, [(a, 6)], new("native/asrock-usb/2", [new(10, 20, 30)]));
        var boundary = a.Device.Writes.Count; var before = File.ReadAllText(NativePath(r, a));
        Assert.False((await r.Start((a, 5))).Success); Assert.Equal(boundary, a.Device.Writes.Count);
        Assert.Equal(before, File.ReadAllText(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task AChangedNativeOwnerDuringDirectBlocksTheNextFrameAndAutomaticRestore()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); await r.Start((a, 5));
        var saved = new AsrockBoundRecovery(r.Directory, a.Id).Pending!; var boundary = a.Device.Writes.Count;
        r.Backend.BeforeTransport = p =>
        {
            if (p[1] != 0x10 || p[3] != 0xff) return; r.Backend.BeforeTransport = null;
            new AsrockNativeRecovery(r.Directory, a.Id).RestoreUnsentHandoff(saved.Handoff!);
        };
        await r.Tick(.1); await r.Tick(.1);
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.True(File.Exists(r.JournalPath(a))); Assert.True(File.Exists(NativePath(r, a)));
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
    }
    [Fact]
    public async Task FailureDuringFirstDirectMutationRestoresTheOriginalBeforeAnyNativeEdits()
    {
        var a = new Cell(1, true); a.Device.Set(5, new(2, 17, 35, 69, 118)); var original = Setting(a, 5);
        await using var r = await new Rig(new(a)).Open(); r.Configure(a, exit: LightingExitAction.RestoreOriginal);
        await Fixed(r, a, 5); await Fixed(r, a, 5, 50);
        a.Device.AfterWrite = _ => { a.Device.AfterWrite = null; throw new IOException("lost Direct-entry ack"); };
        Assert.Equal(Outcome.Unknown, (await r.Start((a, 5))).Result!.Outcome);
        Assert.Equal(original, Setting(a, 5)); Assert.Equal(0xfd, a.Device.Order);
        Assert.False(File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
    }
    private static async Task<AsrockNativeHandoff> PrepareAbandonedHandoff(Cell a, Backend backend, string directory, string stage)
    {
        var driver = new AsrockDeviceDriver(a.Id, a.Connection, directory, Boot, backend); await driver.PollAsync(default);
        var native = new AsrockNativeRecovery(directory, a.Id);
        var scope = new AsrockRecoveryScope(driver.BindingCandidate!, a.Connection.ConnectionId, a.Connection.Confidence, Guid.NewGuid().ToString("N"));
        foreach (byte zone in new byte[] { 5, 6 })
        {
            var state = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange).Snapshot;
            native.Prepare(Boot, scope, state, zone, new(1, 100, 60, 20), state.Zones.Single(z => z.Address == zone).Setting);
            a.Device.Set(zone, new(1, 100, 60, 20));
        }
        var handoff = new AsrockNativeHandoff(native.Pending!, [5]);
        var transaction = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange);
        var desired = transaction.Snapshot.Zones.ToDictionary(z => z.Address, z => z.Address == 5 ? handoff.Before.Entries.Single(e => e.Zone == 5).Exit : z.Setting);
        var store = new AsrockBoundRecovery(directory, a.Id);
        var writer = store.ForStream(scope with { OwnerId = Guid.NewGuid().ToString("N") }, [5], native, handoff);
        if (stage == "before-transfer")
        {
            var path = Path.Combine(directory, "asrock-native-" + SettingsTransfer.Hash(a.Id).ToLowerInvariant() + ".json");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var blocked = Record.Exception(() => writer.Save(Boot, transaction.Snapshot, desired));
            Assert.True(blocked is IOException or UnauthorizedAccessException); Assert.NotNull(store.Pending);
            native.CheckCurrent(handoff.Before);
        }
        else
        {
            writer.Save(Boot, transaction.Snapshot, desired);
            if (stage != "transferred") transaction.Begin(snapshot => writer.Save(Boot, snapshot, desired), true);
            if (stage is "settling" or "partial" or "restored")
            {
                desired[5] = new(1, 12, 24, 48); writer.Save(Boot, transaction.Snapshot, desired);
                if (stage == "partial") a.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5) throw new IOException("zone 5 timed out"); };
                if (stage != "settling") _ = transaction.Restore(desired);
                a.Device.BeforeWrite = null;
            }
        }
        return handoff;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsentHandoffRestoresTheExactNativeRecordAndCanBePreparedAgain(bool outside)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5);
        if (outside) await Fixed(r, a, 6);
        var before = File.ReadAllText(NativePath(r, a)); var boundary = a.Device.Writes.Count;
        var native = new AsrockNativeRecovery(r.Directory, a.Id); var handoff = new AsrockNativeHandoff(native.Pending!, [5]);
        var snapshot = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange).Snapshot;
        var bound = new AsrockBoundRecovery(r.Directory, a.Id);
        var writer = bound.ForStream(handoff.Before.Scope with { OwnerId = Guid.NewGuid().ToString("N") }, [5], native, handoff);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            writer.Save(Boot, snapshot); writer.CheckOwnership();
            Assert.Equal(outside, File.Exists(NativePath(r, a))); Assert.True(File.Exists(r.JournalPath(a)));
            writer.ClearUnsent(); Assert.Equal(before, File.ReadAllText(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
        }
        Assert.Equal(boundary, a.Device.Writes.Count);
    }
    [Fact]
    public async Task HardwareChangedDuringDurableHandoffIsNotOverwrittenByDirectOrItsCleanup()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); var boundary = a.Device.Writes.Count;
        var foreign = new BoardLightSetting(7, 79, 91, 123, 34);
        r.Backend.BeforeTransport = p =>
        {
            if (p[1] != 0x14 || !File.Exists(r.JournalPath(a)) || File.Exists(NativePath(r, a))) return;
            r.Backend.BeforeTransport = null; a.Device.Set(5, foreign);
        };
        Assert.Equal(Outcome.Unknown, (await r.Start((a, 5))).Result!.Outcome);
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.Equal(foreign, Setting(a, 5));
        Assert.True(File.Exists(NativePath(r, a))); Assert.False(File.Exists(r.JournalPath(a)));
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
    }
    [Theory] [InlineData(5)] [InlineData(6)]
    public async Task AForeignNativeEffectStopsLiveDirectWithoutOverwritingTheExternalSetting(int changedZone)
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); await r.Start((a, 5));
        var boundary = a.Device.Writes.Count; var foreign = new BoardLightSetting(7, 87, 65, 43, 21);
        a.Device.Set((byte)changedZone, foreign); await r.Tick(.1); await r.Tick(.1);
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.Equal(foreign, Setting(a, (byte)changedZone));
        Assert.True(File.Exists(r.JournalPath(a))); Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Owned);
    }
    [Fact]
    public async Task ExpiryDuringOwnershipReadsDropsOnlyThatFrameAndTheNextFrameCanContinue()
    {
        var a = new Cell(1, true); await using var r = await new Rig(new(a)).Open(); await Fixed(r, a, 5); await r.Start((a, 5));
        var boundary = a.Device.Writes.Count;
        r.Backend.BeforeTransport = p => { if (p[1] != 0x11) return; r.Backend.BeforeTransport = null; r.Clock.Advance(.6); };
        await r.Tick(.1); Assert.Equal(boundary, a.Device.Writes.Count);
        var status = r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Lighting!;
        Assert.True(status.Running); Assert.Equal(1, status.DroppedFrames);
        await r.Tick(.1); Assert.True(a.Device.Writes.Count > boundary);
    }
    [Theory]
    [InlineData("before-transfer")] [InlineData("transferred")] [InlineData("direct")]
    [InlineData("settling")] [InlineData("partial")] [InlineData("restored")]
    public async Task ColdHandoffRecoveryCoversEveryDurableTransitionBoundary(string stage)
    {
        var a = new Cell(1, true); var backend = new Backend(a); var first = Setting(a, 5); var outside = Setting(a, 6);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Handoff-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedHandoff(a, backend, directory, stage);
        await using var r = new Rig(backend, directory); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.Null(r.Runtime.State().LifecycleError); Assert.Equal(0xfd, a.Device.Order);
        Assert.Equal(stage is "settling" or "partial" or "restored" ? new BoardLightSetting(1, 12, 24, 48) : first, Setting(a, 5));
        Assert.Equal(outside, Setting(a, 6)); Assert.False(File.Exists(r.JournalPath(a))); Assert.False(File.Exists(NativePath(r, a)));
    }
    [Theory] [InlineData("owner")] [InlineData("record")] [InlineData("boot")] [InlineData("revision")]
    public async Task ColdHandoffDoesNotOverwriteChangedIdentityOrOwnership(string change)
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Handoff-Changed-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedHandoff(a, backend, directory, "transferred");
        await using var r = new Rig(backend, directory, change == "boot" ? "later-boot" : Boot);
        if (change == "owner")
        {
            var native = new AsrockNativeRecovery(directory, a.Id); var old = native.Pending!; native.Complete(old, 6);
            var state = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange).Snapshot;
            native.Prepare(Boot, old.Scope with { OwnerId = Guid.NewGuid().ToString("N") }, state, 6, old.Entries[0].Intended, old.Entries[0].Exit);
        }
        if (change == "record")
        {
            var stored = ServiceEnvironment.Read<AsrockBoundRecoveryRecord>(r.JournalPath(a));
            ServiceEnvironment.Write(r.JournalPath(a), stored with { Handoff = stored.Handoff! with { Zones = [6] } });
        }
        if (change == "revision") a.Connection = a.Connection with { DeviceRevision = 41 };
        var boundary = a.Device.Writes.Count;
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.NotNull(r.Runtime.State().LifecycleError); Assert.Equal(boundary, a.Device.Writes.Count);
        Assert.True(File.Exists(r.JournalPath(a))); Assert.True(File.Exists(NativePath(r, a)));
    }
}
