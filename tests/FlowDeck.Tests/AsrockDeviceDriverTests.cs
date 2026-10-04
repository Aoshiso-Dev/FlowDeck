using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Device = FlowDeck.Tests.AsrockVolatileTransactionTests.Device;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Fact] public async Task SetupDescriptorsReportDetectedZoneCountsAndReadbackRestoreWithoutWrites()
    {
        var cell = new Cell(1, false); await using var r = await new Rig(new(cell)).Open(configure: false);
        var values = r.Driver(cell).DescribeSetup(); Assert.NotEmpty(values);
        Assert.All(values, v => { Assert.Equal(cell.Device.Counts[v.EndpointId[^1]-'0'], v.DetectedLeds);
            Assert.Equal(v.DetectedLeds, v.MinimumLeds); Assert.Equal(v.DetectedLeds, v.MaximumLeds);
            Assert.Contains(LightingExitAction.RestoreOriginal,v.LightingExitActions); });
        Assert.Empty(cell.Device.Writes);
    }
    private const string Installation = "baf4a0c412ea41c693a7cb8679c2d290", Boot = "recording-boot";
    private sealed class Cell(int number, bool serial)
    {
        public AsrockDeviceConnection Connection = new("recording-path-" + number, (serial ? "serial:" : "location:") + SettingsTransfer.Hash(number),
            Guid.NewGuid().ToString("N"), serial ? IdentityConfidence.SerialBound : IdentityConfidence.ConnectionBound);
        public Device Device = new();
        public string Id => DeviceIdentity.Create(Installation, "asrock-usb", Connection.NativeIdentity);
    }
    private sealed class Backend(params Cell[] cells) : IAsrockDeviceBackend
    {
        public readonly List<Cell> Cells = [.. cells];
        public Action<byte[]>? BeforeTransport;
        public bool Disposed;
        public Task<AsrockDeviceConnection[]> DiscoverAsync(CancellationToken ct) => Task.FromResult(Cells.Select(c => c.Connection).ToArray());
        public void Check(AsrockDeviceConnection expected)
        { if (!Cells.Any(c => c.Connection == expected)) throw new IOException("recording connection replaced"); }
        public void CheckConflicts() { }
        public IAsrockRgbSession Borrow(AsrockDeviceConnection expected) { Check(expected); return new Session(this, expected); }
        private sealed class Session(Backend backend, AsrockDeviceConnection expected) : IAsrockGuardedRgbSession
        {
            public byte[] Exchange(byte[] request) => Exchange(request, () => { });
            public byte[] Exchange(byte[] request, Action beforeExchange)
            {
                backend.BeforeTransport?.Invoke(request); backend.Check(expected); beforeExchange();
                return backend.Cells.Single(c => c.Connection == expected).Device.Exchange(request);
            }
            public void Dispose() { }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly string Directory;
        public readonly Backend Backend;
        public readonly AsrockDeviceProvider Provider;
        public readonly DeviceControlRuntime Runtime;
        public readonly TestClock Clock = new();
        private DeviceServiceHello hello = null!;
        public string Owner => hello.Session;
        private long sequence;
        public Rig(Backend backend, string? directory = null, string boot = Boot)
        {
            Directory = directory ?? Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Driver-" + Guid.NewGuid().ToString("N"));
            Backend = backend; Provider = new(Installation, Directory, boot, backend);
            Runtime = new([], ["user"], new TestLog(), Clock, providers: [Provider]);
        }
        public AsrockDeviceDriver Driver(Cell cell) => Assert.IsType<AsrockDeviceDriver>(Provider.CachedDrivers.Single(d => d.DeviceId == cell.Id));
        public async Task<Rig> Open(bool configure = true)
        {
            await Runtime.PollAsync(); await Runtime.PollAsync();
            if (configure) foreach (var cell in Backend.Cells) Configure(cell);
            Runtime.Refresh(); hello = Runtime.OpenSession("user"); return this;
        }
        public void Configure(Cell cell, int? countOverride = null, LightingExitAction exit = LightingExitAction.Off)
        {
            var driver = Driver(cell); var ids = driver.Observe()!.Endpoints.Select(e => e.Id).ToArray();
            driver.ConfigureLighting(driver.BindingCandidate, ids.Select(id => new LightingEndpointConfiguration(new(driver.DeviceId, id), true, new(exit))),
                ids.Select(id => new RegisteredEndpoint(id, id, EndpointKind.Lighting,
                    Setup: new(LedCount: countOverride ?? cell.Device.Counts[id[^1] - '0'], LedCountConfirmed: true))));
        }
        public Task<DeviceServiceReply> Command(DeviceOperation operation, (Cell Device, byte Zone)[] targets, LightingProgram? program = null)
        {
            var state = Runtime.State();
            var versions = targets.Select(t => state.Endpoints.Single(e => e.Target.DeviceId == t.Device.Id && e.Target.EndpointId == "zone:" + t.Zone))
                .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            return Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                operation == DeviceOperation.ReleaseSession ? 0 : state.Inventory.TopologyRevision,
                operation == DeviceOperation.ReleaseSession ? null : versions, Lighting: program)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        public Task<DeviceServiceReply> Start(params (Cell Device, byte Zone)[] targets)
            => Command(DeviceOperation.SetLighting, targets, new("color-cycle", [new(100, 50, 20)], 4, Brightness: 50));
        public Task Tick(double seconds) { Clock.Advance(seconds); return Runtime.TickLightingAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        public string JournalPath(Cell cell) => Path.Combine(Directory, "asrock-lighting-" + SettingsTransfer.Hash(cell.Id).ToLowerInvariant() + ".json");
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
    private static BoardLightSetting Setting(Cell cell, byte zone)
    {
        var response = new byte[64]; response[3] = zone; cell.Device.Raw[zone].CopyTo(response, 4);
        return AsrockRgbControl.Decode(response, cell.Device.Order);
    }
    [Fact]
    public async Task TwoControllersDiscoverWithoutWritingAndOnlySelectedZonesFollowTheCommonService()
    {
        var a = new Cell(1, false); var b = new Cell(2, false); await using var r = await new Rig(new(a, b)).Open();
        Assert.Empty(a.Device.Writes); Assert.Empty(b.Device.Writes);
        var aBefore = Setting(a, 2); var bBefore = Setting(b, 5);
        Assert.NotEqual(r.Driver(a).BindingCandidate, r.Driver(b).BindingCandidate);
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5), (b, 6))).Result!.Outcome);
        await r.Tick(.5);
        var state = r.Runtime.State();
        Assert.All(state.Endpoints.Where(e => e.Lighting?.Running == true), e => Assert.Equal(new RgbColor(25, 50, 0), e.ReportedLighting!.Colors[0]));
        var stopped = await r.Command(DeviceOperation.StopLighting, [(a, 5), (b, 6)]); Assert.Equal(Outcome.Confirmed, stopped.Result!.Outcome);
        Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(a, 5)); Assert.Equal(new BoardLightSetting(1, 50, 25, 10), Setting(b, 6));
        Assert.Equal(aBefore, Setting(a, 2)); Assert.Equal(bBefore, Setting(b, 5));
        Assert.False(File.Exists(r.JournalPath(a))); Assert.False(File.Exists(r.JournalPath(b)));
        Assert.All(r.Driver(a).LegacyReferences(), alias => { Assert.Equal("rgb", alias.Source); Assert.Equal(a.Connection.Path, alias.LegacyDeviceId); });
    }
    [Fact]
    public async Task WrongLedCountAndMissingPhysicalBindingKeepSynchronizationUnavailable()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open(configure: false);
        Assert.False((await r.Start((a, 5))).Success); Assert.Empty(a.Device.Writes);
        r.Configure(a, countOverride: 14); r.Runtime.Refresh();
        Assert.False((await r.Start((a, 5))).Success); Assert.Empty(a.Device.Writes);
        r.Configure(a); r.Runtime.Refresh(); Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5))).Result!.Outcome);
        Assert.Throws<IOException>(() => r.Configure(a));
    }
    [Fact]
    public async Task AdditionalZonesJoinTheExistingControllerInsteadOfOpeningIndependentDirectGroups()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 5));
        var boundary = a.Device.Writes.Count;
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 6))).Result!.Outcome);
        Assert.All(a.Device.Writes.Skip(boundary), p => { Assert.Equal(0x10, p[1]); Assert.Equal(0xff, p[3]); });
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((a, 5), (a, 6))).Result!.Outcome);
        Assert.Equal(2, r.Runtime.State().Endpoints.Count(e => e.Lighting?.Running == true));
        Assert.Single(r.Runtime.State().Endpoints.Where(e => e.Lighting?.Running == true).Select(e => e.Lighting!.GroupId).Distinct());
    }
    [Fact]
    public async Task TransportQueueDelayIsCheckedAtTheActualWriteAndFreshFramesCanContinue()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 5));
        var boundary = a.Device.Writes.Count;
        r.Backend.BeforeTransport = packet =>
        {
            if (packet[1] is 0x14 or 0x11) return;
            r.Backend.BeforeTransport = null; r.Clock.Advance(.6);
        };
        await r.Tick(.1); Assert.Equal(boundary, a.Device.Writes.Count);
        Assert.Equal(1, r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "zone:5").Lighting!.DroppedFrames);
        await r.Tick(.1); Assert.True(a.Device.Writes.Count > boundary);
    }
    [Fact]
    public async Task ReplacementAtTheSamePathDoesNotReceiveTheOldFrameOrExit()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 5));
        var old = a.Device; var boundary = old.Writes.Count;
        a.Device = new(); a.Connection = a.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        await r.Runtime.PollAsync(); await r.Tick(.1);
        Assert.Equal(boundary, old.Writes.Count); Assert.Empty(a.Device.Writes); Assert.True(File.Exists(r.JournalPath(a)));
        Assert.All(r.Runtime.State().Endpoints.Where(e => e.Target.EndpointId == "zone:5"), e => Assert.Equal(Outcome.Unknown, e.Outcome));
        Assert.Throws<IOException>(() => r.Configure(a));
    }
    [Fact]
    public async Task PartialRecoveryCanBeRetriedWithoutReplayingTheAnimation()
    {
        var a = new Cell(1, false); await using var r = await new Rig(new(a)).Open(); await r.Start((a, 2), (a, 5));
        a.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5) throw new IOException("failed native restoration"); };
        Assert.Equal(Outcome.Partial, (await r.Command(DeviceOperation.StopLighting, [(a, 2), (a, 5)])).Result!.Outcome);
        Assert.True(File.Exists(r.JournalPath(a))); var boundary = a.Device.Writes.Count; a.Device.BeforeWrite = null;
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        Assert.False(File.Exists(r.JournalPath(a))); Assert.DoesNotContain(a.Device.Writes.Skip(boundary), p => p[3] == 0xff);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Owned));
    }
    private static async Task PrepareAbandonedStream(Cell cell, Backend backend, string directory)
    {
        var first = new AsrockDeviceDriver(cell.Id, cell.Connection, directory, Boot, backend); await first.PollAsync(default);
        var scope = new AsrockRecoveryScope(first.BindingCandidate!, cell.Connection.ConnectionId, cell.Connection.Confidence, Guid.NewGuid().ToString("N"));
        var store = new AsrockBoundRecovery(directory, cell.Id).ForStream(scope, [5]);
        var transaction = new AsrockVolatileTransaction(cell.Connection.Path, cell.Device.Exchange);
        transaction.Begin(snapshot => store.Save(Boot, snapshot, snapshot.Zones.ToDictionary(z => z.Address,
            z => z.Address == 5 ? new BoardLightSetting(0, 0, 0, 0) : z.Setting)), true);
        // This simulates an interrupted owner without keeping any real thread or HID handle alive.
    }
    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public async Task ColdRecoveryRequiresMatchingBindingBootAndAdequateConnectionEvidence(bool serial, bool reconnect, bool newBoot, bool topologyChanged, bool recovered)
    {
        var a = new Cell(1, serial); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Cold-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedStream(a, backend, directory); var boundary = a.Device.Writes.Count;
        if (reconnect) a.Connection = a.Connection with { ConnectionId = Guid.NewGuid().ToString("N") };
        if (topologyChanged) a.Device.Counts[5]++;
        await using var r = new Rig(backend, directory, newBoot ? "other-boot" : Boot);
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.Equal(recovered, r.Runtime.State().LifecycleError is null); Assert.Equal(!recovered, File.Exists(r.JournalPath(a)));
        if (recovered) { Assert.Equal(new BoardLightSetting(0, 0, 0, 0), Setting(a, 5)); Assert.Equal(0xfd, a.Device.Order); }
        else Assert.Equal(boundary, a.Device.Writes.Count);
    }
    [Fact]
    public async Task AnExternalNativeChangeIsNotOverwrittenDuringRecovery()
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Foreign-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedStream(a, backend, directory); a.Device.Set(5, new(7, 80, 90, 100)); var boundary = a.Device.Writes.Count;
        await using var r = new Rig(backend, directory); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.NotNull(r.Runtime.State().LifecycleError); Assert.Equal(boundary, a.Device.Writes.Count); Assert.True(File.Exists(r.JournalPath(a)));
    }
    [Fact]
    public async Task StaleJournalObjectsCannotClearOrOverwriteTheNewOwner()
    {
        var a = new Cell(1, true); var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new Backend(a); var first = new AsrockDeviceDriver(a.Id, a.Connection, directory, Boot, backend); await first.PollAsync(default);
            var snapshot = new AsrockVolatileTransaction(a.Connection.Path, a.Device.Exchange).Snapshot;
            var scope = new AsrockRecoveryScope(first.BindingCandidate!, a.Connection.ConnectionId, a.Connection.Confidence, Guid.NewGuid().ToString("N"));
            var old = new AsrockBoundRecovery(directory, a.Id); var writer = old.ForStream(scope, [5]); writer.Save(Boot, snapshot);
            var newer = new AsrockBoundRecovery(directory, a.Id); newer.Complete(newer.Pending!);
            var next = newer.ForStream(scope with { OwnerId = Guid.NewGuid().ToString("N") }, [5]); next.Save(Boot, snapshot);
            Assert.Throws<IOException>(() => writer.Clear()); Assert.Throws<IOException>(() => writer.Save(Boot, snapshot));
            Assert.NotNull(new AsrockBoundRecovery(directory, a.Id).Pending); Assert.Empty(a.Device.Writes);
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task CorruptRecoveryStaysReadOnlyWithoutRemovingTheOtherDetectedController()
    {
        var a = new Cell(1, true); var b = new Cell(2, true); await using var r = new Rig(new(a, b));
        System.IO.Directory.CreateDirectory(r.Directory); File.WriteAllText(r.JournalPath(a), "invalid record");
        await r.Open(configure: false);
        Assert.Equal(2, r.Runtime.State().Inventory.Devices.Length); Assert.Throws<IOException>(() => r.Configure(a));
        r.Configure(b); r.Runtime.Refresh();
        Assert.Equal(Outcome.WrittenUnverified, (await r.Start((b, 5))).Result!.Outcome); Assert.Empty(a.Device.Writes);
        Assert.All(await r.Driver(a).ReleaseAsync(DeviceReleaseReason.StartupRecovery, default), e => Assert.Equal(Outcome.Unknown, e.Result.Outcome));
        Assert.Equal("invalid record", File.ReadAllText(r.JournalPath(a)));
    }
    [Fact]
    public async Task ARecordWithATamperedBindingIsRejectedBeforeAnyRecoveryWrite()
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Tamper-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedStream(a, backend, directory); var boundary = a.Device.Writes.Count;
        await using var r = new Rig(backend, directory);
        var path = r.JournalPath(a); var saved = ServiceEnvironment.Read<AsrockBoundRecoveryRecord>(path);
        ServiceEnvironment.Write(path, saved with { Scope = saved.Scope with { Binding = saved.Scope.Binding with { Fingerprint = new string('A', 64) } } });
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.NotNull(r.Runtime.State().LifecycleError); Assert.Equal(boundary, a.Device.Writes.Count); Assert.True(File.Exists(path));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task RecoveryRechecksTheDurableOwnerAfterTheRecordWasLoaded(bool runningStream)
    {
        var a = new Cell(1, true); var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Stale-Recovery-" + Guid.NewGuid().ToString("N"));
        if (!runningStream) await PrepareAbandonedStream(a, backend, directory);
        await using var r = new Rig(backend, directory);
        if (runningStream) { await r.Open(); await r.Start((a, 5)); }
        else { await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); }
        var boundary = a.Device.Writes.Count;
        // Replace the disk record through a separate owner; the original driver still has a valid old cache.
        var replacement = new AsrockBoundRecovery(directory, a.Id); var original = replacement.Pending!; replacement.Complete(original);
        replacement.ForStream(original.Scope with { OwnerId = Guid.NewGuid().ToString("N") }, original.Targets)
            .Save(original.Boot, original.Snapshot, original.Desired);
        if (runningStream) Assert.Equal(Outcome.Unknown, (await r.Command(DeviceOperation.StopLighting, [(a, 5)])).Result!.Outcome);
        else { await r.Runtime.RecoverStartupAsync(); Assert.NotNull(r.Runtime.State().LifecycleError); }
        Assert.Equal(boundary, a.Device.Writes.Count); Assert.True(File.Exists(r.JournalPath(a)));
        Assert.NotEqual(original.Scope.OwnerId, new AsrockBoundRecovery(directory, a.Id).Pending!.Scope.OwnerId);
    }
    [Fact]
    public async Task UsbRevisionChangePreventsReplayingThePreviousConfiguration()
    {
        var a = new Cell(1, true); a.Connection = a.Connection with { DeviceRevision = 1 }; var backend = new Backend(a);
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Asrock-Revision-" + Guid.NewGuid().ToString("N"));
        await PrepareAbandonedStream(a, backend, directory); var boundary = a.Device.Writes.Count;
        a.Connection = a.Connection with { DeviceRevision = 2, ConnectionId = Guid.NewGuid().ToString("N") };
        await using var r = new Rig(backend, directory); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Runtime.RecoverStartupAsync();
        Assert.NotNull(r.Runtime.State().LifecycleError); Assert.Equal(boundary, a.Device.Writes.Count); Assert.True(File.Exists(r.JournalPath(a)));
    }
}
