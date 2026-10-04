using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockHidContinuityTests
{
    private const string PathName = @"\\?\hid#vid_26ce&pid_01a2#fixture";
    private sealed class Watch(List<string> events) : IHardwareContinuityWatch
    {
        public bool Revoked, FailClose;
        public bool ResourcesClosed { get; private set; }
        public int Closes;
        public void Check() { if (Revoked || ResourcesClosed) throw new IOException("disconnected"); }
        public void Dispose()
        {
            Closes++; events.Add("unregister");
            if (FailClose) throw new IOException("unregister pending");
            ResourcesClosed = true;
        }
    }
    private sealed class Probe : IAsrockMetadataProbe
    {
        public readonly List<string> Events = [];
        public readonly Watch Watcher;
        public (string? Serial, int? Revision) First = (null, 1), Second = (null, 1);
        public bool FailRead, FailClose;
        public int Reads, Closes;
        public Action? Reading;
        public bool ResourcesClosed { get; private set; }
        public Probe() => Watcher = new(Events);
        public IHardwareContinuityWatch Watch() { Events.Add("register"); return Watcher; }
        public (string? Serial, int? Revision) Read()
        {
            Events.Add("read"); Reads++; Reading?.Invoke();
            if (FailRead) throw new IOException("metadata unavailable");
            return Reads == 1 ? First : Second;
        }
        public void Dispose()
        {
            Events.Add("metadata-close"); Closes++;
            if (FailClose) throw new IOException("handle close pending");
            ResourcesClosed = true;
        }
        public Task<AsrockHidContinuity> Capture(CancellationToken ct = default) => AsrockHidContinuity.CaptureAsync(PathName,
            path => { Assert.Equal(PathName, path); Events.Add("open"); return this; }, ct);
    }
    private static AsrockDeviceConnection Connection(int revision = 1) => new(PathName, "location:" + SettingsTransfer.Hash(PathName),
        Guid.NewGuid().ToString("N"), IdentityConfidence.ConnectionBound, revision);

    [Fact] public async Task WatchesTheOpenedDeviceBeforeReadingAndReleasesMetadataBeforeHandover()
    {
        var probe = new Probe(); await using var lifetime = await probe.Capture();
        Assert.Equal(new[] { "open", "register", "read", "read", "metadata-close" }, probe.Events);
        Assert.True(probe.ResourcesClosed); Assert.False(probe.Watcher.ResourcesClosed); Assert.Null(lifetime.Receipt.Serial);
        Assert.Equal(1, lifetime.Receipt.Revision); Assert.True(DeviceServiceProtocol.Id(lifetime.Receipt.Handover));
        lifetime.Check(Connection()); await lifetime.DisposeAsync(); Assert.True(lifetime.ResourcesClosed); Assert.Equal(1, probe.Closes);
    }
    [Fact] public async Task ReusingTheSamePathAndRevisionCreatesANewLifetimeWithoutRevivingOldRequests()
    {
        var a = new Probe(); await using var old = await a.Capture(); a.Watcher.Revoked = true;
        var b = new Probe(); await using var replacement = await b.Capture();
        Assert.NotEqual(old.Receipt.Handover, replacement.Receipt.Handover);
        Assert.Throws<IOException>(old.Check); replacement.Check(Connection());
    }
    [Theory] [InlineData("path")] [InlineData("revision")] [InlineData("identity")] [InlineData("serial-claim")] [InlineData("confidence")]
    public async Task AReopenedNativeConnectionMustMatchTheWatchedMetadataAndIdentityKind(string mismatch)
    {
        var probe = new Probe(); await using var lifetime = await probe.Capture();
        var connection = mismatch switch
        {
            "path" => Connection() with { Path = PathName + "another" }, "revision" => Connection(2),
            "identity" => Connection() with { NativeIdentity = "location:wrong" },
            "serial-claim" => Connection() with { NativeIdentity = "serial:guessed", Confidence = IdentityConfidence.SerialBound },
            _ => Connection() with { Confidence = (IdentityConfidence)99 }
        };
        Assert.Throws<IOException>(() => lifetime.Check(connection)); Assert.Equal(2, probe.Reads);
    }
    [Fact] public async Task VerifiedSerialMayMatchSerialBindingButDoesNotBecomeOneWhenUniquenessIsUnknown()
    {
        var probe = new Probe { First = ("FD-SERIAL-001", 1), Second = ("FD-SERIAL-001", 1) };
        await using var lifetime = await probe.Capture(); lifetime.Check(Connection());
        var native = "serial:" + SettingsTransfer.Hash(new { Vendor = 0x26ce, Product = 0x01a2, Serial = "FD-SERIAL-001" });
        lifetime.Check(Connection() with { NativeIdentity = native, Confidence = IdentityConfidence.SerialBound });
        Assert.Throws<IOException>(() => lifetime.Check(Connection() with { NativeIdentity = native + "x", Confidence = IdentityConfidence.SerialBound }));
    }
    [Theory] [InlineData("changed")] [InlineData("missing")] [InlineData("serial")]
    public async Task InconsistentOrUnavailableMetadataCannotProduceAContinuityReceipt(string failure)
    {
        var probe = new Probe();
        if (failure == "changed") probe.Second = (null, 2);
        if (failure == "missing") probe.First = probe.Second = (null, null);
        if (failure == "serial") probe.Second = ("FD-NEW-DEVICE", 1);
        await Assert.ThrowsAsync<IOException>(() => probe.Capture());
        Assert.True(probe.Watcher.ResourcesClosed); Assert.True(probe.ResourcesClosed);
        Assert.True(probe.Events.IndexOf("unregister") < probe.Events.IndexOf("metadata-close"));
    }
    [Fact] public async Task RemovalDuringMetadataCaptureInvalidatesTheResultAndCleansUp()
    {
        var probe = new Probe(); probe.Reading = () => probe.Watcher.Revoked = true;
        await Assert.ThrowsAsync<IOException>(() => probe.Capture()); Assert.Equal(1, probe.Reads);
        Assert.True(probe.Watcher.ResourcesClosed); Assert.True(probe.ResourcesClosed);
    }
    [Fact] public async Task ReadFailureAndUnregisterFailureRetainMetadataUntilCleanupCanRetry()
    {
        var probe = new Probe { FailRead = true }; probe.Watcher.FailClose = true;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => probe.Capture());
        Assert.False(error.ResourcesClosed); Assert.False(probe.ResourcesClosed); Assert.Equal(0, probe.Closes);
        probe.Watcher.FailClose = false; await error.DisposeAsync();
        Assert.True(error.ResourcesClosed); Assert.True(probe.ResourcesClosed); Assert.Equal(2, probe.Watcher.Closes);
    }
    [Fact] public async Task FailedMetadataCloseCannotPublishTheWatchAndCompletedUnregistrationIsNotRepeated()
    {
        var probe = new Probe { FailClose = true };
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => probe.Capture());
        Assert.False(error.ResourcesClosed); Assert.True(probe.Watcher.ResourcesClosed);
        probe.FailClose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed); Assert.Equal(1, probe.Watcher.Closes);
    }
    [Fact] public async Task CancellationDuringCaptureClosesTheNotificationAndMetadataHandle()
    {
        using var cancel = new CancellationTokenSource(); var probe = new Probe { Reading = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.Capture(cancel.Token));
        Assert.True(probe.Watcher.ResourcesClosed); Assert.True(probe.ResourcesClosed);
    }
    [Fact] public async Task RevokedContinuityCannotRestoreAJournalAndAReplacementCannotAdoptItsHandoverId()
    {
        using var directory = new ServiceTestDirectory(); var probe = new Probe(); await using var original = await probe.Capture();
        var journal = new AsrockOpenRgbDetectionRecovery(directory.Path, "board-fixture");
        var scope = new AsrockOpenRgbRecoveryIdentity("d3f516653d764da181e6f5a74bcae5c9", "board-fixture", "boot", original.Receipt.Handover, 1);
        var hardware = new AsrockVolatileTransactionTests.Device();
        journal.Capture(scope, PathName, hardware.Exchange, original.Check); probe.Watcher.Revoked = true;
        Assert.Throws<IOException>(() => journal.Restore(scope, hardware.Exchange, original.Check));
        await using var replacement = await new Probe().Capture();
        Assert.Throws<IOException>(() => journal.Restore(scope with { Handover = replacement.Receipt.Handover }, hardware.Exchange, replacement.Check));
        Assert.Empty(hardware.Writes); Assert.NotNull(journal.Pending); Assert.True(DeviceRecoveryInspection.HasPending(directory.Path));
    }
}
