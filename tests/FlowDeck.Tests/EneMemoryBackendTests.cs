using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class EneMemoryBackendTests
{
    internal sealed class Rack(params byte[] addresses)
    {
        public readonly Dictionary<(byte Address, ushort Register), byte> Registers = addresses.SelectMany(a =>
            new ushort[] { 0x8020, 0x8021, 0x8022, 0x8023 }.Concat(Enumerable.Range(0x8160, 24).Select(v => (ushort)v))
                .Concat(Enumerable.Range(0x8100, 24).Select(v => (ushort)v)).Select(r => (Key: (a, r), Value: r == 0x8021 ? (byte)1 : (byte)0))).ToDictionary(p => p.Key, p => p.Value);
        public readonly HashSet<byte> Present = addresses.ToHashSet();
        public readonly List<(byte Address, ushort Register, byte Value)> Writes = [];
        public string Platform = "platform-a";
        public EneMemoryIdentification? Identity = new(EneMemoryDefinition.EightLed.Firmware, EneMemoryDefinition.EightLed.ConfigurationHex);
        public bool FailOpen, FailRead, FailWrite, FailDispose;
        public int Opens, Closes, Active, MaximumActive, Identifications, ShortIdentifications;
        public int? Thread;
        public Action? OnOpen, OnIdentify;
        public Action<ushort, bool>? BeforeData;
        public IEneMemoryIo Open()
        {
            CheckThread(); OnOpen?.Invoke(); if (FailOpen) throw new IOException("bus unavailable");
            Opens++; Active++; MaximumActive = Math.Max(Active, MaximumActive); return new Io(this);
        }
        public EneMemoryTransport Transport(string id = "recording-bus") => new(id, addresses, Open, () => SettingsTransfer.Hash(Platform));
        private void CheckThread() { Thread ??= Environment.CurrentManagedThreadId; Assert.Equal(Thread, Environment.CurrentManagedThreadId); }
        private sealed class Io(Rack rack) : IEneMemoryIo, IEneMemoryIdentifyingIo, IEneMemoryStreamIo, IEneMemoryGuardedIo
        {
            private readonly HashSet<byte> verified = [];
            private bool disposed;
            private void Require(byte address)
            { rack.CheckThread(); ObjectDisposedException.ThrowIf(disposed, this); if (!verified.Contains(address)) throw new IOException("not identified"); }
            public bool Identify(byte a) => IdentifyController(a) is not null;
            public EneMemoryIdentification? IdentifyController(byte a)
            {
                rack.CheckThread(); rack.Identifications++; rack.OnIdentify?.Invoke();
                if (!rack.Present.Contains(a)) return null;
                verified.Add(a); return rack.Identity;
            }
            public bool VerifyPreviouslyIdentified(byte a)
            { rack.CheckThread(); rack.ShortIdentifications++; if (!rack.Present.Contains(a)) return false; verified.Add(a); return true; }
            public byte Read(byte a, ushort r) => Read(a, r, () => { });
            public byte Read(byte a, ushort r, Action guard)
            {
                Require(a); rack.BeforeData?.Invoke(r, false); guard(); if (rack.FailRead) throw new IOException("read failed"); return rack.Registers[(a, r)];
            }
            public void Write(byte a, ushort r, byte value) => Write(a, r, value, () => { });
            public void Write(byte a, ushort r, byte value, Action guard)
            {
                Require(a); rack.BeforeData?.Invoke(r, true); guard(); if (rack.FailWrite) throw new IOException("write failed");
                EneMemoryRgbBus.ValidateWrite(r, value); rack.Registers[(a, r)] = value; rack.Writes.Add((a, r, value));
            }
            public void WritePixel(byte a, int led, byte red, byte blue, byte green) => WritePixel(a, led, red, blue, green, () => { });
            public void WritePixel(byte a, int led, byte red, byte blue, byte green, Action guard)
            {
                Require(a); rack.BeforeData?.Invoke((ushort)(0x8100 + led * 3), true); guard(); if (rack.FailWrite) throw new IOException("pixel failed");
                foreach (var (value, index) in new[] { red, blue, green }.Select((v, i) => (v, i)))
                { var r = (ushort)(0x8100 + led * 3 + index); rack.Registers[(a, r)] = value; rack.Writes.Add((a, r, value)); }
            }
            public void Dispose()
            { rack.CheckThread(); if (disposed) return; disposed = true; rack.Closes++; rack.Active--; if (rack.FailDispose) throw new IOException("close failed"); }
        }
    }
    [Fact]
    public async Task TwoBusesAndModulesAreDistinctAndDiscoveryDoesNotWriteOrInventSerials()
    {
        var a = new Rack(0x71, 0x73); var b = new Rack(0x71);
        await using var backend = new EneMemoryDeviceBackend([a.Transport("bus-a"), b.Transport("bus-b")]);
        var first = await backend.DiscoverAsync(default); var second = await backend.DiscoverAsync(default);
        Assert.Equal(3, first.Length); Assert.Equal(first, second); Assert.Equal(3, first.Select(c => c.NativeIdentity).Distinct().Count());
        Assert.All(first, c => { Assert.StartsWith("location:", c.NativeIdentity); Assert.Equal(EneMemoryDefinition.EightLed.Id, c.DefinitionId); });
        Assert.Empty(a.Writes); Assert.Empty(b.Writes); Assert.Equal(a.Opens, a.Closes); Assert.Equal(b.Opens, b.Closes);
    }
    [Theory] [InlineData("firmware")] [InlineData("configuration")]
    public async Task UnmatchedControllerDefinitionIsExcluded(string change)
    {
        var a = new Rack(0x71); a.Identity = change == "firmware" ? a.Identity! with { Firmware = "unknown" } : a.Identity! with { ConfigurationHex = new string('0', 128) };
        await using var backend = new EneMemoryDeviceBackend([a.Transport()]); Assert.Empty(await backend.DiscoverAsync(default)); Assert.Empty(a.Writes);
    }
    [Theory] [InlineData("platform")] [InlineData("removed")] [InlineData("invalidated")]
    public async Task ChangedMemoryOrReacquiredConnectionRejectsOldOperations(string change)
    {
        var a = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var old = Assert.Single(await backend.DiscoverAsync(default));
        if (change == "platform") a.Platform = "platform-b";
        if (change == "removed") { a.Present.Clear(); Assert.Empty(await backend.DiscoverAsync(default)); a.Present.Add(0x71); }
        if (change == "invalidated") backend.Invalidate();
        var next = Assert.Single(await backend.DiscoverAsync(default)); Assert.Equal(old.NativeIdentity, next.NativeIdentity); Assert.NotEqual(old.ConnectionId, next.ConnectionId);
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(old, true, s => { s.Write(0x8020, 1, () => { }); return true; }, default)); Assert.Empty(a.Writes);
    }
    [Fact]
    public async Task FailedBusDoesNotDiscardAnotherBusAndAReopenedBusGetsANewConnection()
    {
        var a = new Rack(0x71); var b = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([a.Transport("bus-a"), b.Transport("bus-b")]);
        var old = await backend.DiscoverAsync(default); a.FailOpen = true; var remaining = Assert.Single(await backend.DiscoverAsync(default));
        Assert.Equal(old.Single(c => c.BusId == "bus-b"), remaining); Assert.Single(backend.Problems);
        a.FailOpen = false; var next = await backend.DiscoverAsync(default); Assert.NotEqual(old.Single(c => c.BusId == "bus-a"), next.Single(c => c.BusId == "bus-a"));
    }
    [Fact]
    public async Task DiscoveryCannotPublishAConnectionInvalidatedDuringItsProbe()
    {
        var a = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([a.Transport()]);
        a.OnIdentify = () => { a.OnIdentify = null; backend.Invalidate(); };
        Assert.Empty(await backend.DiscoverAsync(default)); Assert.NotEmpty(backend.Problems);
        Assert.Single(await backend.DiscoverAsync(default)); Assert.Empty(a.Writes);
    }
    [Fact]
    public async Task BoundSessionTargetsOnlyOneModuleAndReleasesTheBusOnItsOwningThread()
    {
        var a = new Rack(0x71, 0x73); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var connections = await backend.DiscoverAsync(default);
        IEneMemorySession? escaped = null;
        await Task.WhenAll(connections.Select(c => backend.ExecuteAsync(c, true, s =>
        { escaped = s; s.Write(0x8160, c.Address, () => { }); return s.Read(0x8160); }, default)));
        Assert.Equal(1, a.MaximumActive); Assert.Equal(a.Opens, a.Closes); Assert.Equal(2, a.Writes.Count);
        Assert.Equal((byte)0x71, a.Registers[(0x71, 0x8160)]); Assert.Equal((byte)0x73, a.Registers[(0x73, 0x8160)]);
        Assert.Throws<IOException>(() => escaped!.Read(0x8020));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task GuardRunsAfterPointerSelectionForBytesAndPixelsWithoutInvalidatingAHealthyConnection(bool pixel)
    {
        var a = new Rack(0x71); var afterPointer = false; await using var backend = new EneMemoryDeviceBackend([a.Transport()]);
        var c = Assert.Single(await backend.DiscoverAsync(default)); a.BeforeData = (_, write) => { if (write) afterPointer = true; };
        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.ExecuteAsync(c, true, s =>
        {
            void Guard() { Assert.True(afterPointer); throw new OperationCanceledException("expired after pointer"); }
            if (pixel) s.WritePixel(0, 1, 2, 3, Guard); else s.Write(0x8160, 1, Guard); return true;
        }, default));
        backend.Check(c); Assert.Empty(a.Writes); Assert.Equal(a.Opens, a.Closes);
    }
    [Fact]
    public async Task InvalidationAfterPointerSelectionPreventsActualDataWrites()
    {
        var a = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        a.BeforeData = (_, write) => { if (write) backend.Invalidate(); };
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(c, true, s => { s.Write(0x8160, 10, () => { }); return true; }, default));
        Assert.Empty(a.Writes); Assert.Equal(a.Opens, a.Closes);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TransportFailureInvalidatesOnlyTheAffectedModule(bool write)
    {
        var a = new Rack(0x71, 0x73); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var old = await backend.DiscoverAsync(default);
        a.FailWrite = write; a.FailRead = !write;
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(old[0], true, s =>
        { if (write) s.Write(0x8160, 10, () => { }); else _ = s.Read(0x8160); return true; }, default));
        Assert.Throws<IOException>(() => backend.Check(old[0])); backend.Check(old[1]); a.FailWrite = a.FailRead = false;
        var next = await backend.DiscoverAsync(default); Assert.NotEqual(old[0], next[0]); Assert.Equal(old[1], next[1]);
    }
    [Fact]
    public async Task ShortIdentityIsUsableOnlyWithinTheRecentFullIdentificationWindow()
    {
        var a = new Rack(0x71); var clock = new TestClock(); await using var backend = new EneMemoryDeviceBackend([a.Transport()], clock);
        var c = Assert.Single(await backend.DiscoverAsync(default));
        Assert.Equal(0, await backend.ExecuteAsync(c, false, s => s.Read(0x8020), default)); Assert.Equal(1, a.ShortIdentifications);
        clock.Advance(2.1); var opens = a.Opens;
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(c, false, s => s.Read(0x8020), default)); Assert.Equal(opens, a.Opens);
        Assert.Equal(c, Assert.Single(await backend.DiscoverAsync(default)));
        a.BeforeData = (_, write) => { if (write) clock.Advance(2.1); };
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(c, false, s => { s.WritePixel(0, 1, 2, 3, () => { }); return true; }, default));
        Assert.Empty(a.Writes); backend.Check(c);
    }
    [Fact]
    public async Task FullOperationsRecheckThePlatformBeforeAnyBusAccess()
    {
        var a = new Rack(0x71, 0x73); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var old = await backend.DiscoverAsync(default);
        a.Platform = "replaced DIMMs"; var opens = a.Opens;
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(old[0], true, s => s.Read(0x8020), default));
        Assert.Equal(opens, a.Opens); Assert.All(old, c => Assert.Throws<IOException>(() => backend.Check(c)));
    }
    [Fact]
    public async Task RawSpdPmicFlashAndArbitraryRegistersNeverReachTheBus()
    {
        var a = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        await backend.ExecuteAsync(c, true, s =>
        {
            foreach (var register in new ushort[] { 0x1000, 0x1c02, 0x80f8, 0x80f9, 0x8118 })
            { Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(register)); Assert.Throws<ArgumentOutOfRangeException>(() => s.Write(register, 1, () => { })); }
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Write(0x80a0, 0xaa, () => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => s.WritePixel(8, 1, 2, 3, () => { })); return true;
        }, default);
        Assert.Empty(a.Writes); backend.Check(c);
        Assert.Throws<ArgumentException>(() => new EneMemoryDeviceBackend([a.Transport() with { Candidates = [0x50] }]));
    }
    [Fact]
    public async Task CancellationWhileAcquiringTheBusClosesItWithoutInvalidatingTheConnection()
    {
        var a = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        using var cancelled = new CancellationTokenSource(); var identified = a.Identifications; a.OnOpen = cancelled.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.ExecuteAsync(c, true, s => s.Read(0x8020), cancelled.Token));
        backend.Check(c); Assert.Equal(identified, a.Identifications); Assert.Equal(a.Opens, a.Closes); Assert.Empty(a.Writes);
    }
    [Fact]
    public async Task FailureReleasingTheSharedBusInvalidatesEveryModuleOnThatBus()
    {
        var a = new Rack(0x71, 0x73); await using var backend = new EneMemoryDeviceBackend([a.Transport()]); var old = await backend.DiscoverAsync(default);
        a.FailDispose = true;
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(old[0], true, s => s.Read(0x8020), default));
        Assert.All(old, c => Assert.Throws<IOException>(() => backend.Check(c))); Assert.Equal(a.Opens, a.Closes);
    }
    internal static EnePlatformEvidence Evidence => new("board-maker", "board-model", "1", "board-serial", "BIOS-maker", "1.0", "20260901",
        [new("slot-a", "bank-a", "DIMM-maker", "part-1", "serial-1", 32UL * 1024 * 1024 * 1024, 34, 6000),
         new("slot-b", "bank-b", "DIMM-maker", "part-1", "serial-2", 32UL * 1024 * 1024 * 1024, 34, 6000)]);
    [Fact]
    public void WholePlatformProofIsStableAcrossEnumerationOrderButChangesWithPhysicalEvidence()
    {
        var evidence = Evidence; var original = EneMemoryPlatformCapture.Fingerprint(evidence);
        Assert.Equal(original, EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = evidence.Dimms.Reverse().ToArray() }));
        Assert.Equal(original, EneMemoryPlatformCapture.Fingerprint(evidence with { BoardProduct = " board-model " }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with { BiosVersion = "2.0" }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [evidence.Dimms[0] with { Serial = "replacement" }, evidence.Dimms[1]] }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [evidence.Dimms[0] with { Locator = "slot-c" }, evidence.Dimms[1]] }));
        Assert.NotEqual(original, EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [evidence.Dimms[0]] }));
    }
    [Fact]
    public void MissingAmbiguousOrMalformedPlatformProofIsRejected()
    {
        var evidence = Evidence;
        Assert.Throws<IOException>(() => EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [] }));
        Assert.Throws<IOException>(() => EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [evidence.Dimms[0], evidence.Dimms[0]] }));
        Assert.Throws<IOException>(() => EneMemoryPlatformCapture.Fingerprint(evidence with { Dimms = [evidence.Dimms[0] with { Capacity = 0 }] }));
        Assert.Throws<IOException>(() => EneMemoryPlatformCapture.Fingerprint(evidence with { BoardProduct = null }));
        Assert.Throws<IOException>(() => EneMemoryPlatformCapture.Fingerprint(evidence with { BiosVersion = "invalid\nversion" }));
    }
}
