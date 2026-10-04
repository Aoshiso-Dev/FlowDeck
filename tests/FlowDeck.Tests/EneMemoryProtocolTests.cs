using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneMemoryProtocolTests
{
    private static EneMemoryState Snapshot(Rack rack, byte address = 0x71) => new(
        Enumerable.Range(0x8020, 4).Select(r => rack.Registers[(address, (ushort)r)]).ToArray(),
        Enumerable.Range(0x8160, 24).Select(r => rack.Registers[(address, (ushort)r)]).ToArray(),
        Enumerable.Range(0x8100, 24).Select(r => rack.Registers[(address, (ushort)r)]).ToArray());
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(14)]
    public async Task NativeModesUseRbgAndPreserveTheOtherModuleAndInactiveDirectBuffer(int mode)
    {
        var rack = new Rack(0x71, 0x73); rack.Registers[(0x71, 0x8101)] = 101;
        await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = (await backend.DiscoverAsync(default))[0];
        var other = Snapshot(rack, 0x73);
        await backend.ExecuteAsync(c, true, session =>
        {
            var before = EneMemoryProtocol.Read(session); var after = EneMemoryProtocol.NativeTarget(before, new((byte)mode, 12, 34, 56, 128));
            var mutation = EneMemoryProtocol.Native(before, after); EneMemoryProtocol.ExecutePrepared(session, mutation, () => { });
            Assert.True(EneMemoryProtocol.Same(after, EneMemoryProtocol.Read(session))); return true;
        }, default);
        Assert.Equal(new byte[] { 12, 56, 34 }, Snapshot(rack).NativeColors[..3]); Assert.Equal(mode == 14 ? 5 : mode, rack.Registers[(0x71, 0x8021)]);
        Assert.Equal(mode <= 1 ? 4 : 2, rack.Registers[(0x71, 0x8022)]); Assert.Equal(101, rack.Registers[(0x71, 0x8101)]);
        Assert.True(EneMemoryProtocol.Same(other, Snapshot(rack, 0x73))); Assert.All(rack.Writes, w => Assert.Equal(0x71, w.Address));
        Assert.Single(rack.Writes, w => w.Register == 0x80a0);
    }
    [Fact]
    public async Task SubsequentDirectFramesDoNotRestartEffectsAndExactRawPalettesCanBeRestored()
    {
        var rack = new Rack(0x71); rack.Registers[(0x71, 0x8164)] = 113; rack.Registers[(0x71, 0x8108)] = 127;
        await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        await backend.ExecuteAsync(c, true, session =>
        {
            var original = EneMemoryProtocol.Read(session); Assert.NotNull(EneMemoryProtocol.NativeSetting(original)!.MemoryPalette);
            var first = EneMemoryProtocol.Frame(original, new(10, 20, 30)); EneMemoryProtocol.ExecutePrepared(session, first, () => { });
            var boundary = rack.Writes.Count; var second = EneMemoryProtocol.Frame(EneMemoryProtocol.Read(session), new(40, 50, 60));
            EneMemoryProtocol.ExecutePrepared(session, second, () => { });
            Assert.All(rack.Writes.Skip(boundary), w => Assert.InRange(w.Register, 0x8100, 0x8117));
            Assert.Equal(new byte[] { 40, 60, 50 }, EneMemoryProtocol.Read(session).DirectColors[..3]);
            var restore = EneMemoryProtocol.Native(EneMemoryProtocol.Read(session), original); EneMemoryProtocol.ExecutePrepared(session, restore, () => { });
            Assert.True(EneMemoryProtocol.Same(original, EneMemoryProtocol.Read(session))); Assert.Equal(0, rack.Registers[(0x71, 0x8022)]); return true;
        }, default);
    }
    [Fact]
    public async Task APartialPixelSequenceIsRecognizedButAnExternalPaletteIsNot()
    {
        var rack = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        var plan = EneMemoryProtocol.Frame(Snapshot(rack), new(17, 43, 91));
        rack.BeforeData = (r, write) => { if (write && r == 0x8109) throw new IOException("fourth pixel interrupted"); };
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(c, true, session => { EneMemoryProtocol.ExecutePrepared(session, plan, () => { }); return true; }, default));
        Assert.Equal(9, rack.Writes.Count); Assert.True(EneMemoryProtocol.OwnedPrefix(Snapshot(rack), plan));
        rack.Registers[(0x71, 0x8101)] = 231; Assert.False(EneMemoryProtocol.OwnedPrefix(Snapshot(rack), plan));
    }
    [Fact]
    public async Task StaleNativeStateAndUnrelatedNativeChangesCannotBecomeAFrameBaseline()
    {
        var rack = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        var plan = EneMemoryProtocol.Frame(Snapshot(rack), new(11, 22, 33)); rack.Registers[(0x71, 0x8169)] = 71;
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(c, true, session => { EneMemoryProtocol.ExecutePrepared(session, plan, () => { }); return true; }, default));
        Assert.Empty(rack.Writes); Assert.False(EneMemoryProtocol.OwnedPrefix(Snapshot(rack), plan)); backend.Check(c);
    }
    [Fact]
    public async Task GuardCanStopBeforeTheFirstWriteOrDuringALaterPixelWithoutRestartingTheEffect()
    {
        var rack = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        var plan = EneMemoryProtocol.Frame(Snapshot(rack), new(17, 29, 43)); var writeDenied = false;
        rack.BeforeData = (_, write) => { if (write) writeDenied = true; };
        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.ExecuteAsync(c, true, session =>
        { EneMemoryProtocol.ExecutePrepared(session, plan, () => { if (writeDenied) throw new OperationCanceledException(); }); return true; }, default));
        Assert.Empty(rack.Writes); backend.Check(c);
        writeDenied = false; rack.BeforeData = (r, write) => { if (write && r == 0x8106) writeDenied = true; };
        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.ExecuteAsync(c, true, session =>
        { EneMemoryProtocol.ExecutePrepared(session, plan, () => { if (writeDenied) throw new OperationCanceledException(); }); return true; }, default));
        Assert.Equal(6, rack.Writes.Count); Assert.DoesNotContain(rack.Writes, w => w.Register == 0x80a0); Assert.True(EneMemoryProtocol.OwnedPrefix(Snapshot(rack), plan));
    }
    [Fact]
    public void RawStateRetainsNativeSpeedAndBufferBytesInsteadOfNormalizingTheRecoveryBaseline()
    {
        var original = Snapshot(new Rack(0x71)); var plan = EneMemoryProtocol.Frame(original, new(10, 20, 30));
        original.NativeColors[5] = 129; original.Control[2] = 4;
        Assert.Equal(0, plan.Before.Control[2]); Assert.Equal(0, plan.Before.NativeColors[5]); Assert.Equal(0, plan.After.Control[2]);
        Assert.True(EneMemoryProtocol.ValidMutation(plan));
        Assert.False(EneMemoryProtocol.ValidMutation(plan with { Writes = [new(0x80f8, 1)] }));
        Assert.False(EneMemoryProtocol.ValidMutation(plan with { Writes = plan.Writes.Reverse().ToArray() }));
        var extraNative = Contract.Copy(plan.After); extraNative.NativeColors[0] = 99;
        Assert.False(EneMemoryProtocol.ValidMutation(plan with { After = extraNative }));
        var invalid = Contract.Copy(plan.Before); invalid.Control[3] = 1;
        Assert.False(EneMemoryProtocol.Valid(invalid)); Assert.Null(EneMemoryProtocol.NativeSetting(invalid));
    }
}
