using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Device = FlowDeck.Tests.AsrockVolatileTransactionTests.Device;
using Detected = FlowDeck.Infrastructure.AsrockHidDeviceBackend.Detected;

namespace FlowDeck.Tests;

public sealed partial class AsrockHidBackendTests
{
    private sealed class Identified(Device device, string? serial, int? revision = null) : IAsrockIdentitySession
    {
        public string? VerifiedSerial => serial;
        public int? DeviceRevision => revision;
        public byte[] Exchange(byte[] request) => device.Exchange(request);
        public void Dispose() => device.Dispose();
    }
    [Fact]
    public async Task RetainedHandlesAndNestedGuardsAlwaysRunOnTheOwningTransportThread()
    {
        var devices = new Dictionary<string, Device>(); var opens = 0; int? owner = null;
        await using var backend = new AsrockHidDeviceBackend(() => [new("path-a", null), new("path-b", null)], path =>
        {
            owner ??= Environment.CurrentManagedThreadId; Assert.Equal(owner, Environment.CurrentManagedThreadId);
            var device = new Device { OwnerThread = owner }; devices.Add(path, device); opens++; return device;
        }, () => { });
        var first = await backend.DiscoverAsync(default); var second = await backend.DiscoverAsync(default);
        Assert.Equal(first, second); Assert.Equal(2, opens);
        using var session = backend.Borrow(first[0]);
        var guarded = Assert.IsAssignableFrom<IAsrockGuardedRgbSession>(session);
        await Task.Run(() => guarded.Exchange(AsrockRgbMonitor.Query(0x14, 1), () =>
        { Assert.Equal(owner, Environment.CurrentManagedThreadId); backend.Check(first[0]); })).WaitAsync(TimeSpan.FromSeconds(3));
        session.Dispose(); Assert.False(devices["path-a"].Disposed);
        using var next = backend.Borrow(first[0]); next.Exchange(AsrockRgbMonitor.Query(0x14, 2)); Assert.Equal(2, opens);
        await backend.DisposeAsync(); Assert.All(devices.Values, d => Assert.True(d.Disposed));
    }
    [Fact]
    public async Task GuardRejectionAfterQueueingDoesNotWriteOrInvalidateTheHandle()
    {
        var device = new Device();
        await using var backend = new AsrockHidDeviceBackend(() => [new("path-a", null)], _ =>
        { device.OwnerThread = Environment.CurrentManagedThreadId; return device; }, () => { });
        var connection = Assert.Single(await backend.DiscoverAsync(default)); using var session = backend.Borrow(connection);
        var guarded = (IAsrockGuardedRgbSession)session;
        Assert.Throws<IOException>(() => guarded.Exchange(AsrockRgbControl.Packet(5, new(1, 1, 2, 3), 0xfd), () => throw new IOException("expired after queue wait")));
        Assert.Empty(device.Writes); Assert.False(device.Disposed);
        Assert.Equal(connection, Assert.Single(await backend.DiscoverAsync(default)));
        _ = session.Exchange(AsrockRgbMonitor.Query(0x14, 3));
    }
    [Fact]
    public async Task LostHandleGetsANewConnectionAndOldBorrowersCannotReachItsReplacement()
    {
        var devices = new List<Device>();
        await using var backend = new AsrockHidDeviceBackend(() => [new("path-a", null)], _ =>
        { var d = new Device { OwnerThread = Environment.CurrentManagedThreadId }; devices.Add(d); return d; }, () => { });
        var first = Assert.Single(await backend.DiscoverAsync(default)); using var old = backend.Borrow(first);
        devices[0].BeforeWrite = _ => throw new IOException("HID removed");
        Assert.Throws<IOException>(() => old.Exchange(AsrockRgbControl.Packet(5, new(1, 10, 20, 30), 0xfd)));
        Assert.True(devices[0].Disposed);
        var next = Assert.Single(await backend.DiscoverAsync(default)); Assert.NotEqual(first.ConnectionId, next.ConnectionId);
        Assert.Throws<IOException>(() => old.Exchange(AsrockRgbControl.Packet(5, new(1, 100, 0, 0), 0xfd)));
        Assert.Empty(devices[1].Writes); Assert.Throws<IOException>(() => backend.Check(first));
    }
    [Fact]
    public async Task DuplicateSerialsRemainConnectionBoundAndNewAmbiguityInvalidatesTheOldSerialIdentity()
    {
        Detected[] inventory = [new("path-a", "real-serial-41")]; var all = new List<Device>();
        await using var backend = new AsrockHidDeviceBackend(() => inventory, _ =>
        { var d = new Device { OwnerThread = Environment.CurrentManagedThreadId }; all.Add(d); return new Identified(d, "real-serial-41"); }, () => { });
        var unique = Assert.Single(await backend.DiscoverAsync(default)); Assert.Equal(IdentityConfidence.SerialBound, unique.Confidence);
        using var old = backend.Borrow(unique);
        inventory = [new("path-a", "real-serial-41"), new("path-b", "real-serial-41")];
        var duplicate = await backend.DiscoverAsync(default);
        Assert.All(duplicate, c => { Assert.Equal(IdentityConfidence.ConnectionBound, c.Confidence); Assert.StartsWith("location:", c.NativeIdentity); });
        Assert.Equal(2, duplicate.DistinctBy(c => c.NativeIdentity).Count()); Assert.False(all[0].Disposed);
        Assert.Throws<IOException>(() => old.Exchange(AsrockRgbMonitor.Query(0x14, 1)));
        Assert.All(all, d => Assert.Empty(d.Writes));
    }
    [Fact]
    public async Task MissingPathsCloseOnlyTheirOwnRetainedSessionAndPlaceholderSerialsAreNotTrusted()
    {
        Detected[] inventory = [new("path-a", "Default string"), new("path-b", "serial-b-22")];
        var all = new Dictionary<string, Device>();
        await using var backend = new AsrockHidDeviceBackend(() => inventory, path =>
        { var d = new Device { OwnerThread = Environment.CurrentManagedThreadId }; all.Add(path, d); return d; }, () => { });
        var both = await backend.DiscoverAsync(default); Assert.Equal(IdentityConfidence.ConnectionBound, both.Single(c => c.Path == "path-a").Confidence);
        var kept = both.Single(c => c.Path == "path-b"); inventory = [new("path-b", "serial-b-22")];
        Assert.Equal(kept, Assert.Single(await backend.DiscoverAsync(default)));
        Assert.True(all["path-a"].Disposed); Assert.False(all["path-b"].Disposed);
    }
    [Fact]
    public async Task IdentityComesFromTheOpenedSessionInsteadOfTheCachedEnumeratorString()
    {
        var device = new Device();
        await using var backend = new AsrockHidDeviceBackend(() => [new("path-a", "stale-device-A")], _ =>
        { device.OwnerThread = Environment.CurrentManagedThreadId; return new Identified(device, "actual-device-B", 0x123); }, () => { });
        var connection = Assert.Single(await backend.DiscoverAsync(default));
        Assert.Equal(IdentityConfidence.SerialBound, connection.Confidence); Assert.Equal(0x123, connection.DeviceRevision);
        Assert.Equal("serial:" + SettingsTransfer.Hash(new { Vendor = 0x26ce, Product = 0x01a2, Serial = "actual-device-B" }), connection.NativeIdentity);
        Assert.Empty(device.Writes);
    }
    [Fact]
    public async Task AnEnumeratorSerialAloneCannotMakeAConnectionSerialBound()
    {
        var device = new Device();
        await using var backend = new AsrockHidDeviceBackend(() => [new("path-a", "plausible-serial-41")], _ =>
        { device.OwnerThread = Environment.CurrentManagedThreadId; return device; }, () => { });
        var connection = Assert.Single(await backend.DiscoverAsync(default));
        Assert.Equal(IdentityConfidence.ConnectionBound, connection.Confidence); Assert.Null(connection.DeviceRevision);
        Assert.StartsWith("location:", connection.NativeIdentity);
    }
    [Theory]
    [InlineData(" Default string\0", null)] [InlineData("000000000000\0", null)]
    [InlineData("real-serial-41", null)] [InlineData("real\nserial\0", null)] [InlineData(" real-serial-41 \0", "real-serial-41")]
    public void UsbSerialNeedsACompleteUsefulEmbeddedString(string value, string? expected)
        => Assert.Equal(expected, AsrockHidIdentity.DecodeSerial(System.Text.Encoding.Unicode.GetBytes(value)));
    [Fact]
    public void MalformedWideStringsCannotBecomeTrustedSerials()
    {
        Assert.Null(AsrockHidIdentity.DecodeSerial([1])); Assert.Null(AsrockHidIdentity.DecodeSerial([0, 0xd8, 0, 0]));
        Assert.Null(AsrockHidIdentity.DecodeSerial(new byte[4094]));
    }
}
