using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneMemoryRecoveryTests
{
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "FlowDeck-Ene-Recovery-" + Guid.NewGuid().ToString("N"));
        public EneMemoryRecovery Journal(string id = "memory-a") => new(Directory, id);
        public string PathOfRecord => Assert.Single(System.IO.Directory.GetFiles(Directory, "*.json"));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    private static EneMemoryState State() => new([0, 1, 0, 0], Enumerable.Range(1, 24).Select(n => (byte)n).ToArray(),
        Enumerable.Range(51, 24).Select(n => (byte)n).ToArray());
    private static EneMemoryRecoveryScope Scope(string id = "memory-a", string bus = "recording-bus", byte address = 0x71)
    {
        var d = EneMemoryDefinition.EightLed;
        var connection = new EneMemoryConnection(bus, address, "location:" + SettingsTransfer.Hash(new { Bus = bus, Address = address }),
            Guid.NewGuid().ToString("N"), SettingsTransfer.Hash("platform"), d.Id, EneMemoryDefinition.Proof(new(d.Firmware, d.ConfigurationHex)));
        return new(EneMemoryRecovery.BindingFor(id, connection), connection, Guid.NewGuid().ToString("N"));
    }
    private static EneMemoryMutation Native(EneMemoryState before, byte red = 11) =>
        EneMemoryProtocol.Native(before, EneMemoryProtocol.NativeTarget(before, new(1, red, 22, 33, 224)));
    private static EneMemoryRecoveryRecord Begin(EneMemoryRecovery journal, EneMemoryRecoveryScope scope) =>
        journal.Prepare("boot-a", scope, Native(State()), State());
    [Fact]
    public void NativeEditsAndDirectHandoffRetainTheFirstExactOriginalAndExit()
    {
        using var files = new Files(); var journal = files.Journal(); var scope = Scope(); var a = Begin(journal, scope);
        var b = journal.Prepare("boot-a", scope, Native(a.Mutation.After, 44), a.Exit, a);
        var streamScope = scope with { OwnerId = Guid.NewGuid().ToString("N") };
        var c = journal.Prepare("boot-a", streamScope, EneMemoryProtocol.Frame(b.Mutation.After, new(70, 80, 90)), b.Exit, b);
        var loaded = files.Journal().Pending!;
        Assert.Equal(c.Hash, loaded.Hash); Assert.Equal(streamScope, loaded.Scope);
        Assert.True(EneMemoryProtocol.Same(State(), loaded.Original)); Assert.True(EneMemoryProtocol.Same(State(), loaded.Exit));
        Assert.Equal(0, loaded.Original.Control[2]); Assert.Equal(1, loaded.Mutation.After.Control[0]);
        Assert.Throws<IOException>(() => journal.CheckCurrent(a)); Assert.Throws<IOException>(() => journal.CheckCurrent(b));
        loaded.Original.NativeColors[0] = 199; c.Exit.DirectColors[0] = 201;
        Assert.Equal(1, journal.Pending!.Original.NativeColors[0]); Assert.Equal(51, journal.Pending.Exit.DirectColors[0]);
    }
    [Fact]
    public void FirstOwnershipCannotAdoptAnUnownedDirectStream()
    {
        using var files = new Files(); var journal = files.Journal(); var state = State(); state.Control[0] = 1;
        Assert.Throws<IOException>(() => journal.Prepare("boot-a", Scope(), Native(state), State()));
        Assert.Null(journal.Pending); Assert.False(Directory.Exists(files.Directory));
    }
    [Theory] [InlineData("boot")] [InlineData("owner")] [InlineData("connection")] [InlineData("platform")]
    [InlineData("bus")] [InlineData("address")] [InlineData("definition")] [InlineData("controller")] [InlineData("binding")]
    public void NewOwnershipCannotReplaceAnOutstandingDifferentScope(string change)
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope()); var next = a.Scope; var boot = a.Boot;
        next = change switch
        {
            "owner" => next with { OwnerId = "not-an-owner-id" },
            "connection" => next with { Connection = next.Connection with { ConnectionId = Guid.NewGuid().ToString("N") } },
            "platform" => next with { Connection = next.Connection with { PlatformFingerprint = SettingsTransfer.Hash("other-platform") } },
            "bus" => next with { Connection = next.Connection with { BusId = "another-bus" } },
            "address" => next with { Connection = next.Connection with { Address = 0x73 } },
            "definition" => next with { Connection = next.Connection with { DefinitionId = "unknown-ene" } },
            "controller" => next with { Connection = next.Connection with { ControllerFingerprint = SettingsTransfer.Hash("unknown-firmware") } },
            "binding" => next with { Binding = next.Binding with { DeviceId = "memory-b" } },
            _ => next
        };
        if (change == "boot") boot = "boot-b";
        Assert.Throws<IOException>(() => journal.Prepare(boot, next, Native(a.Mutation.After, 44), a.Exit, a));
        Assert.Equal(a.Hash, journal.Pending!.Hash); journal.CheckCurrent(a);
    }
    [Fact]
    public void ChangedExitForeignStateAndPartialPreviousOperationCannotBecomeANewBaseline()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope());
        var exit = Contract.Copy(a.Exit); exit.NativeColors[0]++;
        Assert.Throws<IOException>(() => journal.Prepare(a.Boot, a.Scope, Native(a.Mutation.After), exit, a));
        Assert.Throws<IOException>(() => journal.Prepare(a.Boot, a.Scope, Native(a.Mutation.Before), a.Exit, a));
        var foreign = Contract.Copy(a.Mutation.After); foreign.NativeColors[7]++;
        Assert.Throws<IOException>(() => journal.Prepare(a.Boot, a.Scope, Native(foreign), a.Exit, a));
        journal.CheckCurrent(a);
    }
    [Fact]
    public void RecoveryIsPersistedFromAPartialWriteAndCanResumeItsOwnPartialWrite()
    {
        using var files = new Files(); var journal = files.Journal(); var scope = Scope(); var before = State();
        var frame = EneMemoryProtocol.Frame(before, new(70, 80, 90)); var first = journal.Prepare("boot-a", scope, frame, before);
        var partial = Contract.Copy(before); Array.Copy(frame.After.DirectColors, partial.DirectColors, 6);
        var recovery = journal.PrepareRecovery(first, first.Boot, scope.Binding, scope.Connection, partial);
        Assert.True(EneMemoryProtocol.Same(partial, recovery.Mutation.Before)); Assert.True(EneMemoryProtocol.Same(before, recovery.Mutation.After));
        var interruptedRecovery = Contract.Copy(partial); Array.Copy(before.DirectColors, interruptedRecovery.DirectColors, 3);
        var loaded = files.Journal(); var retry = loaded.PrepareRecovery(loaded.Pending!, first.Boot, scope.Binding, scope.Connection, interruptedRecovery);
        Assert.True(EneMemoryProtocol.Same(before, retry.Mutation.After)); Assert.Equal(EneMemoryRecoveryPhase.Recovering, retry.Phase);
        Assert.Throws<IOException>(() => loaded.Complete(retry));
        Assert.Throws<IOException>(() => loaded.ConfirmRecovery(retry, interruptedRecovery));
        var confirmed = loaded.ConfirmRecovery(retry, before); loaded.Complete(confirmed); Assert.Null(loaded.Pending);
        Assert.Empty(Directory.GetFiles(files.Directory));
    }
    [Fact]
    public void NormalStopTargetIsDurableAndCannotChangeDuringRecovery()
    {
        using var files = new Files(); var journal = files.Journal(); var scope = Scope(); var before = State();
        var a = journal.Prepare("boot-a", scope, EneMemoryProtocol.Frame(before, new(255, 0, 0)), before);
        var baseColor = EneMemoryProtocol.NativeTarget(before, new(1, 0, 0, 128, 224));
        var r = journal.PrepareRecovery(a, a.Boot, scope.Binding, scope.Connection, a.Mutation.After, baseColor);
        Assert.True(EneMemoryProtocol.Same(baseColor, r.Mutation.After)); Assert.True(EneMemoryProtocol.Same(before, r.Exit));
        Assert.Throws<IOException>(() => journal.PrepareRecovery(r, r.Boot, scope.Binding, scope.Connection, r.Mutation.Before, before));
        var again = journal.PrepareRecovery(r, r.Boot, scope.Binding, scope.Connection, r.Mutation.Before);
        Assert.True(EneMemoryProtocol.Same(baseColor, again.Mutation.After));
        Assert.Throws<IOException>(() => journal.Prepare(again.Boot, scope, Native(again.Mutation.After), again.Exit, again));
    }
    [Fact]
    public void RecoveryRejectsForeignChangesReconnectionAndAnotherWindowsBoot()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope());
        var foreign = Contract.Copy(a.Mutation.After); foreign.DirectColors[8]++;
        Assert.Throws<IOException>(() => journal.PrepareRecovery(a, a.Boot, a.Scope.Binding, a.Scope.Connection, foreign));
        var connection = a.Scope.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        Assert.Throws<IOException>(() => journal.PrepareRecovery(a, a.Boot, a.Scope.Binding, connection, a.Mutation.After));
        Assert.Throws<IOException>(() => journal.PrepareRecovery(a, "another-boot", a.Scope.Binding, a.Scope.Connection, a.Mutation.After));
        Assert.Throws<IOException>(() => journal.PrepareRecovery(a, a.Boot, a.Scope.Binding with { Fingerprint = new('0', 64) }, a.Scope.Connection, a.Mutation.After));
        journal.CheckCurrent(a);
    }
    [Fact]
    public void UnsentCancellationRestoresOnlyTheImmediatePreviousRecordIncludingItsOwner()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope());
        var b = journal.Prepare(a.Boot, a.Scope with { OwnerId = Guid.NewGuid().ToString("N") },
            EneMemoryProtocol.Frame(a.Mutation.After, new(55, 66, 77)), a.Exit, a);
        Assert.Throws<IOException>(() => journal.CancelUnsent(b, null));
        var other = a with { Scope = a.Scope with { OwnerId = Guid.NewGuid().ToString("N") } };
        Assert.Throws<IOException>(() => journal.CancelUnsent(b, other));
        journal.CancelUnsent(b, a); Assert.Equal(a.Hash, files.Journal().Pending!.Hash); journal.CheckCurrent(a);
        journal.CancelUnsent(a, null); Assert.Null(journal.Pending);
        var repeated = Begin(journal, a.Scope); Assert.NotEqual(a.Hash, repeated.Hash);
        Assert.Throws<IOException>(() => journal.CancelUnsent(a, null)); journal.CheckCurrent(repeated);
    }
    [Fact]
    public void TwoDimmsHaveIndependentFilesAndIndependentRecovery()
    {
        using var files = new Files(); var a = files.Journal(); var b = files.Journal("memory-b");
        var x = Begin(a, Scope()); var y = Begin(b, Scope("memory-b", address: 0x73));
        Assert.Equal(2, Directory.GetFiles(files.Directory).Length);
        var r = a.PrepareRecovery(x, x.Boot, x.Scope.Binding, x.Scope.Connection, x.Mutation.After);
        a.Complete(a.ConfirmRecovery(r, r.Mutation.After)); Assert.Single(Directory.GetFiles(files.Directory)); b.CheckCurrent(y);
    }
    [Fact]
    public void TamperedDiskAndMutatedReturnedObjectsCannotAuthorizeOrEraseAnObligation()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope()); var changed = Contract.Copy(a);
        changed.Mutation.After.NativeColors[0]++;
        Assert.Throws<IOException>(() => journal.CheckCurrent(changed)); journal.CheckCurrent(a);
        ServiceEnvironment.Write(files.PathOfRecord, changed);
        Assert.Throws<IOException>(() => journal.CheckCurrent(a)); Assert.Throws<IOException>(() => files.Journal());
        Assert.Throws<IOException>(() => journal.CancelUnsent(a, null)); Assert.Equal(a.Hash, journal.Pending!.Hash);
    }
    [Fact]
    public void StaleJournalInstanceCannotOverwriteARecordCreatedOrAdvancedByAnotherOwner()
    {
        using var files = new Files(); var stale = files.Journal(); var current = files.Journal(); var a = Begin(current, Scope());
        Assert.Throws<IOException>(() => Begin(stale, Scope())); var loaded = files.Journal();
        var b = current.Prepare(a.Boot, a.Scope, Native(a.Mutation.After, 55), a.Exit, a);
        Assert.Throws<IOException>(() => loaded.Prepare(a.Boot, a.Scope, Native(a.Mutation.After, 66), a.Exit, a));
        Assert.Throws<IOException>(() => loaded.CancelUnsent(a, null)); current.CheckCurrent(b);
    }
    [Fact]
    public void FailedDiskUpdateLeavesTheLastDurableObligationAndCanRetryConfirmedCleanup()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope());
        using (var locked = new FileStream(files.PathOfRecord, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => journal.Prepare(a.Boot, a.Scope, Native(a.Mutation.After, 55), a.Exit, a));
            Assert.True(error is IOException or UnauthorizedAccessException); Assert.Equal(a.Hash, journal.Pending!.Hash);
        }
        journal.CheckCurrent(a); var r = journal.PrepareRecovery(a, a.Boot, a.Scope.Binding, a.Scope.Connection, a.Mutation.After);
        using (var locked = new FileStream(files.PathOfRecord, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => journal.ConfirmRecovery(r, r.Mutation.After));
            Assert.True(error is IOException or UnauthorizedAccessException); Assert.Equal(r.Hash, journal.Pending!.Hash);
        }
        Assert.Equal(EneMemoryRecoveryPhase.Recovering, files.Journal().Pending!.Phase);
        var recovered = journal.ConfirmRecovery(r, r.Mutation.After);
        using (var locked = new FileStream(files.PathOfRecord, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => journal.Complete(recovered));
            Assert.True(error is IOException or UnauthorizedAccessException); Assert.Equal(recovered.Hash, journal.Pending!.Hash);
        }
        var loaded = files.Journal(); Assert.Equal(EneMemoryRecoveryPhase.Recovered, loaded.Pending!.Phase);
        loaded.Complete(loaded.Pending); Assert.Null(loaded.Pending);
    }
    [Fact]
    public async Task DurableRecordPrecedesActualDataAndNativeToDirectCanRestoreBothRawPalettes()
    {
        using var files = new Files(); var journal = files.Journal(); var rack = new Rack(0x71, 0x73);
        rack.Registers[(0x71, 0x8167)] = 91; rack.Registers[(0x71, 0x8105)] = 111;
        await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = (await backend.DiscoverAsync(default))[0];
        var scope = new EneMemoryRecoveryScope(EneMemoryRecovery.BindingFor("memory-a", c), c, Guid.NewGuid().ToString("N"));
        await backend.ExecuteAsync(c, true, session =>
        {
            var original = EneMemoryProtocol.Read(session); var native = Native(original);
            var a = journal.Prepare("boot-a", scope, native, original); var writes = 0;
            EneMemoryProtocol.ExecutePrepared(session, native, () => journal.CheckCurrent(a), () =>
            { Assert.Equal(a.Hash, files.Journal().Pending!.Hash); writes++; });
            Assert.True(writes > 0);
            var b = journal.Prepare(a.Boot, scope, EneMemoryProtocol.Frame(EneMemoryProtocol.Read(session), new(17, 29, 43)), a.Exit, a);
            EneMemoryProtocol.ExecutePrepared(session, b.Mutation, () => journal.CheckCurrent(b));
            var r = journal.PrepareRecovery(b, b.Boot, scope.Binding, c, EneMemoryProtocol.Read(session));
            EneMemoryProtocol.ExecutePrepared(session, r.Mutation, () => journal.CheckCurrent(r));
            var final = EneMemoryProtocol.Read(session); journal.Complete(journal.ConfirmRecovery(r, final));
            Assert.True(EneMemoryProtocol.Same(original, final)); return true;
        }, default);
        Assert.Null(journal.Pending); Assert.All(rack.Writes, w => Assert.Equal(0x71, w.Address));
    }
    [Fact]
    public async Task ReadOrAuthorizationFailureIsNotCountedAsAWriteAttempt()
    {
        var rack = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([rack.Transport()]);
        var c = Assert.Single(await backend.DiscoverAsync(default)); var attempted = 0;
        await backend.ExecuteAsync(c, true, session =>
        {
            var mutation = Native(EneMemoryProtocol.Read(session));
            Assert.Throws<OperationCanceledException>(() => EneMemoryProtocol.ExecutePrepared(session, mutation,
                () => throw new OperationCanceledException(), () => attempted++));
            var readCount = 0;
            Assert.Throws<OperationCanceledException>(() => EneMemoryProtocol.ExecutePrepared(session, mutation,
                () => { if (++readCount > 52) throw new OperationCanceledException(); }, () => attempted++));
            Assert.Equal(53, readCount); return true;
        }, default);
        Assert.Equal(0, attempted); Assert.Empty(rack.Writes); backend.Check(c);
    }
    [Fact]
    public async Task InterruptedRecoveryCanRetryFromFreshReadWithoutChangingAnotherDimm()
    {
        using var files = new Files(); var journal = files.Journal(); var rack = new Rack(0x71, 0x73);
        await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = (await backend.DiscoverAsync(default))[0];
        var scope = new EneMemoryRecoveryScope(EneMemoryRecovery.BindingFor("memory-a", c), c, Guid.NewGuid().ToString("N"));
        await backend.ExecuteAsync(c, true, session =>
        {
            var original = EneMemoryProtocol.Read(session); var a = journal.Prepare("boot-a", scope,
                EneMemoryProtocol.Frame(original, new(17, 29, 43)), original);
            EneMemoryProtocol.ExecutePrepared(session, a.Mutation, () => journal.CheckCurrent(a));
            var r = journal.PrepareRecovery(a, a.Boot, scope.Binding, c, EneMemoryProtocol.Read(session)); var writes = 0;
            Assert.Throws<OperationCanceledException>(() => EneMemoryProtocol.ExecutePrepared(session, r.Mutation,
                () => journal.CheckCurrent(r), () => { if (++writes == 3) throw new OperationCanceledException(); }));
            var partial = EneMemoryProtocol.Read(session);
            Assert.False(EneMemoryProtocol.Same(original, partial)); Assert.True(EneMemoryProtocol.OwnedPrefix(partial, r.Mutation));
            var restartedJournal = files.Journal(); var retry = restartedJournal.PrepareRecovery(restartedJournal.Pending!, r.Boot, scope.Binding, c, partial);
            EneMemoryProtocol.ExecutePrepared(session, retry.Mutation, () => restartedJournal.CheckCurrent(retry));
            var final = EneMemoryProtocol.Read(session); Assert.True(EneMemoryProtocol.Same(original, final));
            restartedJournal.Complete(restartedJournal.ConfirmRecovery(retry, final)); return true;
        }, default);
        backend.Check(c); Assert.Null(files.Journal().Pending); Assert.All(rack.Writes, w => Assert.Equal(0x71, w.Address));
    }
    [Fact]
    public void ReadLeaseKeepsTheDurableRecordImmutableUntilItIsClosed()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope());
        var lease = journal.AcquireReadLease(a);
        using (lease)
        {
            lease.Check(); Assert.Equal(a.Hash, files.Journal().Pending!.Hash);
            var replace = Record.Exception(() => ServiceEnvironment.Write(files.PathOfRecord, a with { Boot = "changed" }));
            var remove = Record.Exception(() => File.Delete(files.PathOfRecord));
            Assert.True(replace is IOException or UnauthorizedAccessException); Assert.True(remove is IOException or UnauthorizedAccessException);
            lease.Check(); Assert.Equal(a.Hash, journal.Pending!.Hash);
        }
        Assert.Throws<IOException>(lease.Check); journal.CancelUnsent(a, null); Assert.Null(journal.Pending);
    }
    [Fact]
    public void AStaleRecordCannotObtainAReadLeaseOverANewerOwner()
    {
        using var files = new Files(); var journal = files.Journal(); var a = Begin(journal, Scope());
        var b = journal.Prepare(a.Boot, a.Scope, Native(a.Mutation.After, 55), a.Exit, a);
        Assert.Throws<IOException>(() => journal.AcquireReadLease(a));
        using (var lease = journal.AcquireReadLease(b)) lease.Check();
        journal.CancelUnsent(b, a); journal.CheckCurrent(a);
    }
}
