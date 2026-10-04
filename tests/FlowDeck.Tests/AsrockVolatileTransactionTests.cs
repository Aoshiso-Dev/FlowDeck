using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class AsrockVolatileTransactionTests
{
    internal sealed class Device : IAsrockRgbSession
    {
        public byte Order = 0xfd, Available = 0x64;
        public byte[] Counts = [0, 0, 80, 0, 0, 15, 9, 0];
        public Dictionary<byte, byte[]> Raw = new();
        public List<byte[]> Writes = [];
        public Action<byte[]>? BeforeWrite, AfterWrite;
        public int? OwnerThread;
        public bool Disposed;
        public Device()
        {
            foreach (byte zone in new byte[] { 2, 5, 6 })
                Set(zone, new(1, zone, zone + 10, 255, 224));
        }
        public void Set(byte zone, BoardLightSetting value) => Raw[zone] = AsrockRgbControl.Packet(zone, value, Order)[4..9];
        public byte[] Exchange(byte[] request)
        {
            if (OwnerThread is { } owner) Assert.Equal(owner, Environment.CurrentManagedThreadId);
            var response = new byte[65]; var selector = request[3]; response[4] = selector;
            if (request[1] == 0x14)
            {
                if (selector == 1) response[5] = Available;
                if (selector == 2) Counts.CopyTo(response, 5);
                if (selector == 3) response[5] = Order;
            }
            else if (request[1] == 0x11) Raw[selector].CopyTo(response, 5);
            else
            {
                Writes.Add(request.ToArray()); BeforeWrite?.Invoke(request);
                if (request[1] == 0x15 && selector == 3) Order = request[4];
                else if (request[1] == 0x10 && selector < 8) Raw[selector] = request[4..9];
                else if (request[1] == 0x10 && selector == 0xff && request[4] is 0xe3 or 0xe4) { }
                else throw new IOException("Unexpected diagnostic write");
                AfterWrite?.Invoke(request);
            }
            return response;
        }
        public void Dispose()
        { if (OwnerThread is { } owner) Assert.Equal(owner, Environment.CurrentManagedThreadId); Disposed = true; }
    }

    [Fact] public void DurableBackupMustCompleteBeforeAnyHardwareWrite()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange);
        Assert.Throws<IOException>(() => t.Begin(_ => throw new IOException("disk full"), true));
        Assert.Empty(d.Writes); Assert.True(t.Restore().Restored); Assert.Empty(d.Writes);
    }
    [Fact] public void StartingAfterAStaleCaptureDoesNotWriteOrClaimRecovery()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange);
        d.Set(5, new(1, 255, 0, 0));
        Assert.Throws<IOException>(() => t.Begin(_ => { }, true));
        Assert.Empty(d.Writes); Assert.False(t.Restore().Restored); Assert.Empty(d.Writes);
    }
    [Fact] public void LostStartAcknowledgementStillRestoresTheChangedOrder()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange);
        d.AfterWrite = _ => { d.AfterWrite = null; throw new TimeoutException("lost ack"); };
        Assert.Throws<TimeoutException>(() => t.Begin(_ => { }, true));
        Assert.Equal(0, d.Order); Assert.True(t.Restore().Restored); Assert.Equal(0xfd, d.Order);
    }
    [Fact] public void PartialDirectEntryRestoresEveryOriginalZone()
    {
        var d = new Device(); var original = d.Raw.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var t = new AsrockVolatileTransaction("test", d.Exchange);
        d.AfterWrite = p => { if (p[1] == 0x10 && p[3] == 5) { d.AfterWrite = null; throw new TimeoutException(); } };
        Assert.Throws<TimeoutException>(() => t.Begin(_ => { }, true));
        Assert.Equal(15, d.Raw[2][0]); Assert.True(t.Restore().Restored);
        foreach (var pair in original) Assert.Equal(pair.Value, d.Raw[pair.Key]);
    }
    [Fact] public void OneFailedZoneDoesNotPreventTheOtherZonesFromRestoring()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); t.Begin(_ => { }, true);
        d.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 2) throw new TimeoutException(); };
        var result = t.Restore(); Assert.False(result.Restored); Assert.True(result.OrderRestored);
        Assert.Equal(new[] { "mismatch", "verified", "verified" }, result.Zones.Select(z => z.Status));
    }
    [Fact] public void LostOrderRestoreAcknowledgementDoesNotSkipZoneRecovery()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); t.Begin(_ => { }, true);
        d.AfterWrite = p => { if (p[1] == 0x15) { d.AfterWrite = null; throw new TimeoutException(); } };
        var result = t.Restore(); Assert.True(result.OrderRestored); Assert.NotEmpty(result.Errors);
        Assert.All(result.Zones, z => Assert.Equal("verified", z.Status));
    }
    [Fact] public void UnconfirmedOrderDoesNotSendNativeColorsInTheWrongOrder()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); t.Begin(_ => { }, true);
        var writesBefore = d.Writes.Count;
        d.BeforeWrite = p => { if (p[1] == 0x15) throw new TimeoutException(); };
        var result = t.Restore(); Assert.False(result.OrderRestored);
        Assert.All(result.Zones, z => Assert.Equal("notWritten", z.Status)); Assert.Equal(writesBefore + 1, d.Writes.Count);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ChangedTopologyOrOrderPreventsStaleRecoveryWrites(bool topology)
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange); t.Begin(_ => { }, true);
        var writesBefore = d.Writes.Count;
        if (topology) d.Counts[2]++; else d.Order = 0x80;
        Assert.False(t.Restore().Restored); Assert.Equal(writesBefore, d.Writes.Count);
    }
    [Fact] public void FrameOwnershipAndRepeatedRestoreAreRejectedAfterEnd()
    {
        var d = new Device(); var t = new AsrockVolatileTransaction("test", d.Exchange);
        Assert.Throws<InvalidOperationException>(t.CheckFrameOwnership);
        t.Begin(_ => { }, true); t.CheckFrameOwnership(); Assert.True(t.Restore().Restored);
        var writesBefore = d.Writes.Count;
        Assert.Throws<InvalidOperationException>(t.CheckFrameOwnership);
        Assert.Throws<InvalidOperationException>(() => t.Restore());
        Assert.Throws<InvalidOperationException>(() => t.Begin(_ => { }, true)); Assert.Equal(writesBefore, d.Writes.Count);
    }
    [Fact] public void UnknownDirectColorsCannotBecomeAnInventedRestoreBaseline()
    {
        var d = new Device(); d.Raw[5][0] = 15;
        Assert.Throws<IOException>(() => new AsrockVolatileTransaction("test", d.Exchange)); Assert.Empty(d.Writes);
    }
    [Fact] public void ExistingZeroOrderIsRetainedAndNoFlashOrLedCountWritesAreSent()
    {
        var d = new Device { Order = 0 }; var t = new AsrockVolatileTransaction("test", d.Exchange);
        var backedUp = false;
        d.BeforeWrite = _ => Assert.True(backedUp);
        t.Begin(s => { Assert.Equal(0, s.Order); backedUp = true; }, true); Assert.True(t.Restore().Restored);
        Assert.All(d.Writes, p => Assert.True(p[1] == 0x10 && p[3] < 8 || p[1] == 0x15 && p[3] == 3));
    }
}
