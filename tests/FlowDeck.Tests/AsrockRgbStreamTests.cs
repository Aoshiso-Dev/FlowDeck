using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Device = FlowDeck.Tests.AsrockVolatileTransactionTests.Device;

namespace FlowDeck.Tests;

public class AsrockRgbStreamTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Rgb-" + Guid.NewGuid().ToString("N"));
    private static RgbZone[] Zones(Device d, params byte[] ids) => ids.Select(id =>
    {
        var value = AsrockRgbControl.Decode(AsrockRgbMonitor.Response(d.Exchange(AsrockRgbMonitor.Query(0x11, id)), 65, id), d.Order);
        return new RgbZone("Zone " + id, 1, d.Counts[id], null, null, "Static", id, value);
    }).ToArray();
    private Task<AsrockRgbStream> Open(Device d, bool direct, params byte[] targets)
    {
        var zones = Zones(d, targets);
        return AsrockRgbStream.OpenAsync("test", zones, direct, directory,
            () => { d.OwnerThread = Environment.CurrentManagedThreadId; return d; }, () => "test-boot", () => { });
    }
    [Fact] public async Task StreamingUsesOnlyDirectColorFramesAndSettlesToTheLastFrame()
    {
        var d = new Device();
        d.BeforeWrite = _ => Assert.True(new AsrockRgbRecoveryStore(directory).Exists);
        await using var stream = await Open(d, true, 2, 5, 6);
        Assert.Empty(d.Writes);
        await stream.WriteAsync(new(1, 30, 200)); var boundary = d.Writes.Count;
        await stream.WriteAsync(new(11, 22, 99));
        Assert.All(d.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); Assert.True(p[4] is 0xe3 or 0xe4); });
        await stream.FinishAsync(true);
        Assert.Null(stream.RecoveryError); Assert.Equal(0xfd, d.Order); Assert.False(new AsrockRgbRecoveryStore(directory).Exists);
        Assert.All(d.Raw.Values, r => Assert.Equal(new byte[] { 1, 11, 22, 99, 224 }, r)); Assert.True(d.Disposed);
    }
    [Fact] public async Task SubsetKeepsTheOtherZonesFixedOrOffInEveryFrameAndOnStop()
    {
        var d = new Device(); d.Set(6, new(0, 200, 100, 50, 80)); var old = d.Raw.ToDictionary(z => z.Key, z => z.Value.ToArray());
        await using var stream = await Open(d, true, 5);
        await stream.WriteAsync(new(10, 20, 30));
        var body = d.Writes.Where(p => p[3] == 0xff).SelectMany(p => p[4] == 0xe3 ? p[9..63] : p[5..62]).ToArray();
        Assert.Equal(Enumerable.Range(0, 80).SelectMany(_ => new byte[] { 12, 2, 255 }), body.Take(240));
        Assert.Equal(Enumerable.Range(0, 15).SelectMany(_ => new byte[] { 20, 10, 30 }), body.Skip(240).Take(45));
        Assert.All(body.Skip(285).Take(27), b => Assert.Equal(0, b));
        await stream.FinishAsync(true);
        Assert.Equal(old[2], d.Raw[2]); Assert.Equal(old[6], d.Raw[6]); Assert.Equal(new byte[] { 1, 10, 20, 30, 224 }, d.Raw[5]);
    }
    [Theory]
    [InlineData(255, 0, 0)] [InlineData(255, 255, 0)] [InlineData(0, 255, 0)]
    [InlineData(0, 255, 255)] [InlineData(0, 0, 255)] [InlineData(255, 0, 255)]
    public async Task CycleColorsMatchNativeColorsWhileHardwareSwapIsDisabled(int red, int green, int blue)
    {
        var d = new Device(); var originalOrder = d.Order;
        await using var stream = await Open(d, true, 2, 5, 6);
        await stream.WriteAsync(new(red, green, blue));
        Assert.Equal(0, d.Order);
        var body = d.Writes.Where(p => p[3] == 0xff).SelectMany(p => p[4] == 0xe3 ? p[9..63] : p[5..62]).ToArray();
        var offset = 0;
        foreach (var zone in new byte[] { 2, 5, 6 })
        {
            for (var led = 0; led < d.Counts[zone]; led++, offset += 3)
            {
                // The board now has RGSwap disabled, regardless of its saved native setting.
                Assert.Equal(new byte[] { (byte)green, (byte)red, (byte)blue }, body[offset..(offset + 3)]);
            }
        }
        await stream.FinishAsync(true);
        Assert.Equal(originalOrder, d.Order);
        Assert.Null(stream.RecoveryError);
        foreach (var zone in new byte[] { 2, 5, 6 })
        {
            var response = new byte[64]; response[3] = zone; d.Raw[zone].CopyTo(response, 4);
            var native = AsrockRgbControl.Decode(response, d.Order);
            Assert.Equal(new BoardLightSetting(1, red, green, blue), native);
        }
    }
    [Fact] public async Task UnselectedNativeAnimationIsRejectedBeforeAnyMutation()
    {
        var d = new Device(); d.Set(6, new(2, 4, 5, 6));
        await Assert.ThrowsAsync<IOException>(() => Open(d, true, 2, 5)); Assert.Empty(d.Writes);
        Assert.False(new AsrockRgbRecoveryStore(directory).Exists); Assert.True(d.Disposed);
    }
    [Fact] public async Task NativeFixedWritesRemainScopedAndNeedNoJournalOrBootQuery()
    {
        var d = new Device(); var old = d.Raw[6].ToArray();
        await using var stream = await AsrockRgbStream.OpenAsync("test", Zones(d, 5), false, directory, () => d,
            () => throw new InvalidOperationException("not needed for a static color"), () => { });
        await stream.WriteAsync(new(9, 8, 7)); await stream.FinishAsync(true);
        Assert.Equal(old, d.Raw[6]); Assert.All(d.Writes, p => { Assert.Equal(0x10, p[1]); Assert.Equal(5, p[3]); Assert.Equal(1, p[4]); });
        Assert.False(new AsrockRgbRecoveryStore(directory).Exists);
    }
    [Fact] public async Task MidFrameFailureRestoresOriginalSettingsInsteadOfClaimingTheTargetColor()
    {
        var d = new Device(); var old = d.Raw.ToDictionary(z => z.Key, z => z.Value.ToArray());
        d.AfterWrite = p => { if (p[4] == 0xe4) { d.AfterWrite = null; throw new IOException("disconnected during frame"); } };
        await using var stream = await Open(d, true, 2, 5, 6);
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new(50, 60, 70))); await stream.FinishAsync(true);
        Assert.Null(stream.RecoveryError); Assert.Equal(0xfd, d.Order);
        foreach (var z in old) Assert.Equal(z.Value, d.Raw[z.Key]); Assert.False(new AsrockRgbRecoveryStore(directory).Exists);
    }
    [Fact] public async Task FailedRestorationRetainsTheOriginalDurableRecord()
    {
        var d = new Device(); await using var stream = await Open(d, true, 2, 5, 6);
        await stream.WriteAsync(new(50, 60, 70));
        d.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5) throw new IOException("restore failed"); };
        await stream.FinishAsync(false); Assert.NotNull(stream.RecoveryError);
        var saved = new AsrockRgbRecoveryStore(directory).Load(); Assert.NotNull(saved);
        Assert.Equal(saved.Snapshot.Zones.Single(z => z.Address == 5).Setting, saved.Desired[5]);
    }
    [Fact] public async Task ClosingWithoutAFrameDoesNotTouchTheDevice()
    {
        var d = new Device(); await using var stream = await Open(d, true, 2, 5, 6); await stream.FinishAsync(true);
        Assert.Empty(d.Writes); Assert.False(new AsrockRgbRecoveryStore(directory).Exists); Assert.True(d.Disposed);
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new(1, 2, 3)));
    }
    [Fact] public async Task AnExistingRecoveryRecordCannotBeOverwrittenByANewAnimation()
    {
        Directory.CreateDirectory(directory); var file = Path.Combine(directory, "asrock-rgb-recovery.json"); File.WriteAllText(file, "unresolved");
        var d = new Device(); await Assert.ThrowsAsync<IOException>(() => Open(d, true, 2, 5, 6));
        Assert.Empty(d.Writes); Assert.Equal("unresolved", File.ReadAllText(file));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
