using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    private const string Installation = "d3f516653d764da181e6f5a74bcae5c9";
    private sealed class Close(Action close) : IDisposable { public void Dispose() => close(); }
    private sealed class Watch : IHardwareContinuityWatch
    {
        public bool Revoked, FailClose;
        public bool ResourcesClosed { get; private set; }
        public void Check() { if (Revoked || ResourcesClosed) throw new IOException("old connection"); }
        public void Dispose() { if (FailClose) throw new IOException("watch close failed"); ResourcesClosed = true; }
    }
    private sealed class Board(int index)
    {
        public readonly string Path = @"\\?\hid#vid_26ce&pid_01a2#scope-" + index;
        public readonly AsrockVolatileTransactionTests.Device Device = new();
        public readonly Watch Watch = new();
        public int Revision = 1, Opens, Closes, Reads;
        public bool Present = true;
    }
    private sealed class Probe(Board board) : IAsrockMetadataProbe
    {
        public bool ResourcesClosed { get; private set; }
        public IHardwareContinuityWatch Watch() => board.Watch;
        public (string? Serial, int? Revision) Read() => (null, board.Revision);
        public void Dispose() => ResourcesClosed = true;
    }
    private sealed class Session(Rig rig, Board board, bool capture) : IAsrockRgbSession, IAsrockIdentitySession
    {
        private bool closed;
        public string? VerifiedSerial => null;
        public int? DeviceRevision => board.Revision;
        public byte[] Exchange(byte[] packet)
        {
            Assert.False(closed); Assert.False(rig.Child?.HasExited == false);
            Assert.False(rig.Owner?.ResourcesClosed != true && rig.RealChild?.HasExited == false); board.Reads++;
            return board.Device.Exchange(packet);
        }
        public void Dispose()
        {
            if (closed) return;
            if (capture ? rig.FailCaptureClose : rig.FailRestoreClose) throw new IOException("native close pending");
            board.Closes++; closed = true;
        }
    }
    private sealed class Child(Rig rig) : IOpenRgbSuspendedProcess
    {
        public uint ProcessId => (uint)Environment.ProcessId;
        public bool HasExited { get; private set; }
        public bool RefuseExit;
        public IDisposable AttachJob() => new Close(() => { if (!RefuseExit) HasExited = true; });
        public void Resume()
        {
            Assert.All(rig.Boards, b => Assert.Equal(b.Opens, b.Closes));
            Assert.Equal(rig.Boards.Count, System.IO.Directory.GetFiles(rig.Directory.Path, AsrockOpenRgbDetectionRecovery.FilePrefix + "*.json").Length);
            rig.Resuming?.Invoke();
        }
        public Task CloseInputAsync() => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(HasExited);
        public void Kill() { if (!RefuseExit) HasExited = true; }
        public void Dispose() { }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly List<Board> Boards;
        public readonly AsrockOpenRgbDetectionScope Scope;
        public readonly List<uint?> ConflictPids = [];
        public bool DirectAvailable = true, Conflict, FailCaptureClose, FailRestoreClose, AllowUnrestored;
        public int Factories, Enumerations;
        public Action? Enumerating;
        public Action? Resuming;
        public Child? Child;
        public IOpenRgbSuspendedProcess? RealChild;
        public Action? BeforeRestore;
        public OpenRgbOwnedProcess? Owner;
        public readonly Dictionary<string, AsrockOpenRgbOperationReceipt> DetectionReceipts = [];
        public ulong DetectionSequence;
        public Rig(int count = 1)
        {
            Boards = Enumerable.Range(0, count).Select(i => new Board(i)).ToList();
            Scope = new(Installation, Directory.Path, "scope-boot", () => { if (!DirectAvailable) throw new IOException("direct owns device"); },
                () => { Enumerations++; Enumerating?.Invoke(); return Boards.Where(b => b.Present).Select(b => b.Path).ToArray(); },
                (paths, guard) =>
                {
                    var capture = Factories++ == 0;
                    if (!capture) BeforeRestore?.Invoke();
                    return new AsrockHidDeviceBackend(() => Boards.Where(b => b.Present && paths.Contains(b.Path))
                        .Select(b => new AsrockHidDeviceBackend.Detected(b.Path, null)).ToArray(), path =>
                        {
                            guard(path); Assert.False(Child?.HasExited == false);
                            Assert.False(Owner?.ResourcesClosed != true && RealChild?.HasExited == false);
                            var board = Boards.Single(b => b.Path == path); board.Opens++; board.Device.OwnerThread = Environment.CurrentManagedThreadId;
                            return new Session(this, board, capture);
                        }, () => { if (Conflict) throw new IOException("RGB conflict"); });
                }, (path, ct) => AsrockHidContinuity.CaptureAsync(path, _ => new Probe(Boards.Single(b => b.Path == path)), ct),
                pid => { ConflictPids.Add(pid); if (Conflict) throw new IOException("RGB conflict"); });
        }
        public async Task<OpenRgbOwnedProcess> Start(Func<Task>? release = null, IOpenRgbStartupChannel? channel = null)
        {
            Child = new(this);
            Owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => { }), () => { }, _ => Child, release ?? (() => Task.CompletedTask),
                channel is null ? null : () => channel);
            return Owner;
        }
        public async Task Open()
        {
            await Scope.PrepareAsync(default); Scope.AttachHelper(await Start()); Detect();
        }
        public void Detect()
        {
            var emulator = new AsrockDetectionEmulator();
            foreach (var board in Boards)
            {
                board.Device.OwnerThread = null;
                emulator.Io = new(0, 0, 0, false, -2, -2, new byte[65], new byte[64]);
                DetectionReceipts[board.Path] = emulator.Execute(board.Path, board.Device.Available, board.Device.Counts,
                    board.Device.Order, Scope.Authorize, board.Device.Exchange);
                // This list observes later SDK/native recovery writes, as before this fixture
                // emitted startup callbacks. Detection has its separate receipt above.
                board.Device.Writes.Clear();
            }
            DetectionSequence = emulator.Sequence;
        }
        public OpenRgbSdkController[] Inventory() => Boards.Where(b => b.Present).Select((board, n) =>
        {
            var basis = OpenRgbLightingPlanTests.Device();
            var addresses = Enumerable.Range(0, 8).Where(i => (board.Device.Available & (1 << i)) != 0 && board.Device.Counts[i] != 0x1e).ToArray();
            var zones = addresses.Select(i => basis.Zones[0] with { Name = AsrockOpenRgbDetectionRecovery.ZoneNames[i], Type = 1,
                LedCount = board.Device.Counts[i], MinimumLeds = i is 2 or 3 or 7 ? 0u : board.Device.Counts[i],
                MaximumLeds = i is 2 or 3 or 7 ? 100u : board.Device.Counts[i], Modes = [], ActiveMode = -1, Matrix = null, Segments = [], Flags = 0 }).ToArray();
            var count = zones.Sum(z => (int)z.LedCount);
            return basis with { Id = (uint)(41 + n), Type = 0, Name = "Fixture ASRock", Vendor = "ASRock", Description = "ASRock Polychrome USB Device",
                Serial = "", Location = "HID: " + board.Path, Zones = zones, Leds = Enumerable.Repeat("LED", count).ToArray(),
                Colors = Enumerable.Repeat(new RgbColor(0, 0, 255), count).ToArray(), LedDisplayNames = Enumerable.Repeat("", count).ToArray() };
        }).ToArray();
        public async ValueTask DisposeAsync()
        {
            DirectAvailable = true; Conflict = FailCaptureClose = FailRestoreClose = false; Enumerating = null;
            foreach (var board in Boards) board.Watch.FailClose = false;
            if (Child is not null) Child.RefuseExit = false;
            if (Owner is not null) await Owner.DisposeAsync();
            try { await Scope.DisposeAsync(); }
            catch when (AllowUnrestored) { }
            Directory.Dispose();
        }
    }

    [Fact] public async Task ConstructionAndUnusedShutdownHaveNoEnumerationHandlesOrFiles()
    {
        await using var r = new Rig(); Assert.Equal(0, r.Enumerations); Assert.Equal(0, r.Factories);
        await r.Scope.DisposeAsync(); Assert.True(r.Scope.ResourcesClosed); Assert.Empty(System.IO.Directory.GetFiles(r.Directory.Path));
        Assert.Equal(0, r.Enumerations); Assert.Equal(0, r.Factories);
    }
    [Fact] public async Task CompleteCaptureAndNativeClosurePrecedeHelperAndRestorationFollowsItsExit()
    {
        await using var r = new Rig(2); await r.Open(); r.Scope.CheckInventory(r.Inventory());
        Assert.All(r.Boards, b => { Assert.Equal(1, b.Opens); Assert.Equal(1, b.Closes); Assert.Empty(b.Device.Writes); Assert.False(b.Watch.ResourcesClosed); });
        await Assert.ThrowsAsync<IOException>(() => r.Scope.DisposeAsync().AsTask());
        Assert.All(r.Boards, b => Assert.Equal(1, b.Opens));
        await r.Owner!.DisposeAsync(); await r.Scope.DisposeAsync();
        Assert.True(r.Scope.ResourcesClosed); Assert.Empty(System.IO.Directory.GetFiles(r.Directory.Path));
        Assert.All(r.Boards, b => { Assert.Equal(0xfd, b.Device.Order); Assert.Single(b.Device.Writes); Assert.Equal(2, b.Opens); Assert.Equal(2, b.Closes); Assert.True(b.Watch.ResourcesClosed); });
        Assert.Contains((uint)Environment.ProcessId, r.ConflictPids); Assert.Null(r.ConflictPids[^1]);
    }
    [Fact] public async Task PreparationIsIdempotentAndCancellationDoesNotAcquireHardware()
    {
        await using var r = new Rig(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Scope.PrepareAsync(cancel.Token));
        Assert.Equal(0, r.Factories); await r.Scope.PrepareAsync(default); await r.Scope.PrepareAsync(default);
        Assert.Equal(1, r.Factories); await r.Scope.DisposeAsync(); Assert.Empty(r.Boards[0].Device.Writes);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DirectRouteAndOtherRgbProcessesBlockBeforeAnyNativeAcquisition(bool process)
    {
        await using var r = new Rig(); r.Conflict = process; r.DirectAvailable = process;
        await Assert.ThrowsAsync<IOException>(() => r.Scope.PrepareAsync(default)); Assert.Equal(0, r.Factories);
        Assert.All(r.Boards, b => Assert.Equal(0, b.Reads));
    }
    [Fact] public async Task FailedNativeCloseRetainsTheCaptureAndCannotStartANewPreparation()
    {
        await using var r = new Rig(); r.FailCaptureClose = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Scope.PrepareAsync(default));
        Assert.True(DeviceRecoveryInspection.HasPending(r.Directory.Path)); Assert.False(r.Boards[0].Watch.ResourcesClosed);
        await Assert.ThrowsAsync<IOException>(() => r.Scope.PrepareAsync(default)); Assert.Equal(1, r.Factories);
        r.FailCaptureClose = false; await r.Scope.DisposeAsync(); Assert.True(r.Scope.ResourcesClosed); Assert.Empty(r.Boards[0].Device.Writes);
    }
    [Fact] public async Task AChangedPhysicalListRejectsPreparationBeforeOpeningAnUnwatchedDevice()
    {
        await using var r = new Rig(); r.Enumerating = () => { if (r.Enumerations == 2) r.Boards.Add(new(1)); };
        await Assert.ThrowsAsync<IOException>(() => r.Scope.PrepareAsync(default)); Assert.Equal(0, r.Factories);
        Assert.All(r.Boards, b => Assert.Equal(0, b.Opens));
    }
    [Fact] public async Task ANewUncapturedDeviceRejectsTheRouteAndIsNeverOpenedByRestoration()
    {
        await using var r = new Rig(); await r.Open(); var extra = new Board(1); r.Boards.Add(extra);
        Assert.Throws<IOException>(r.Scope.CheckRoute); Assert.Throws<IOException>(() => r.Scope.CheckInventory(r.Inventory()));
        await r.Owner!.DisposeAsync(); await r.Scope.DisposeAsync(); Assert.Equal(0, extra.Opens); Assert.Equal(0, extra.Reads);
        Assert.Empty(extra.Device.Writes); Assert.Equal(0xfd, r.Boards[0].Device.Order);
    }
    [Theory] [InlineData("notification")] [InlineData("revision")]
    public async Task ReusedPathCannotAuthorizeAnOldCapture(string mismatch)
    {
        await using var r = new Rig(); await r.Open(); await r.Owner!.DisposeAsync(); r.AllowUnrestored = true;
        if (mismatch == "notification") r.Boards[0].Watch.Revoked = true; else r.Boards[0].Revision++;
        await Assert.ThrowsAsync<AggregateException>(() => r.Scope.DisposeAsync().AsTask());
        Assert.True(DeviceRecoveryInspection.HasPending(r.Directory.Path)); Assert.Empty(r.Boards[0].Device.Writes);
        if (mismatch == "notification") Assert.Equal(1, r.Boards[0].Opens);
    }
    [Fact] public async Task ForeignZoneChangesRemainPendingAndDoNotOverwriteOtherDevices()
    {
        await using var r = new Rig(2); await r.Open(); await r.Owner!.DisposeAsync(); r.AllowUnrestored = true;
        r.Boards[0].Device.Set(2, new(1, 230, 100, 90));
        await Assert.ThrowsAsync<AggregateException>(() => r.Scope.DisposeAsync().AsTask());
        Assert.Empty(r.Boards[0].Device.Writes); Assert.Single(r.Boards[1].Device.Writes); Assert.Equal(0xfd, r.Boards[1].Device.Order);
        Assert.Single(System.IO.Directory.GetFiles(r.Directory.Path));
    }
    [Fact] public async Task ARevokedDeviceDoesNotPreventAnIndependentCapturedDeviceFromRestoring()
    {
        await using var r = new Rig(2); await r.Open(); await r.Owner!.DisposeAsync(); r.AllowUnrestored = true;
        r.Boards[0].Watch.Revoked = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Scope.DisposeAsync().AsTask());
        Assert.Equal(1, r.Boards[0].Opens); Assert.Empty(r.Boards[0].Device.Writes); Assert.Single(r.Boards[1].Device.Writes);
        Assert.Single(System.IO.Directory.GetFiles(r.Directory.Path));
    }
    [Fact] public async Task PriorHandoverIsNotAdoptedOrDeletedByANewScope()
    {
        await using var r = new Rig(); var board = r.Boards[0];
        var id = DeviceIdentity.Create(Installation, "asrock-usb", "location:" + SettingsTransfer.Hash(board.Path));
        var old = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, id);
        old.Capture(new(Installation, id, "scope-boot", Guid.NewGuid().ToString("N"), 1), board.Path, board.Device.Exchange, () => { });
        var hash = old.Pending!.Hash;
        await Assert.ThrowsAsync<IOException>(() => r.Scope.PrepareAsync(default)); await r.Scope.DisposeAsync();
        Assert.Equal(hash, new AsrockOpenRgbDetectionRecovery(r.Directory.Path, id).Pending!.Hash); Assert.Empty(board.Device.Writes);
    }
    [Fact] public async Task AnUnrestorableInitialModeNeverStartsTheHelperOrCreatesAnInventedCapture()
    {
        await using var r = new Rig(); r.Boards[0].Device.Raw[2][0] = 15;
        await Assert.ThrowsAsync<IOException>(() => r.Scope.PrepareAsync(default)); await r.Scope.DisposeAsync();
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory.Path)); Assert.Empty(r.Boards[0].Device.Writes); Assert.Null(r.Owner);
    }
    [Fact] public async Task RetryOfBackendCloseDoesNotRepeatSuccessfulHardwareRestoration()
    {
        await using var r = new Rig(); await r.Open(); await r.Owner!.DisposeAsync(); r.FailRestoreClose = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Scope.DisposeAsync().AsTask());
        Assert.Single(r.Boards[0].Device.Writes); Assert.False(r.Scope.ResourcesClosed); Assert.False(r.Boards[0].Watch.ResourcesClosed);
        r.FailRestoreClose = false; await r.Scope.DisposeAsync(); Assert.Single(r.Boards[0].Device.Writes); Assert.Equal(2, r.Factories);
    }
    [Fact] public async Task RetryOfWatchCloseDoesNotReopenNativeHandlesOrRepeatRestoration()
    {
        await using var r = new Rig(); await r.Open(); await r.Owner!.DisposeAsync(); r.Boards[0].Watch.FailClose = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Scope.DisposeAsync().AsTask()); var opens = r.Boards[0].Opens;
        r.Boards[0].Watch.FailClose = false; await r.Scope.DisposeAsync(); Assert.Equal(opens, r.Boards[0].Opens); Assert.Single(r.Boards[0].Device.Writes);
    }
    [Theory] [InlineData("extra")] [InlineData("missing")] [InlineData("location")] [InlineData("vendor")]
    [InlineData("serial")] [InlineData("zone-order")] [InlineData("count")]
    public async Task EntireSdkInventoryMustMatchTheCaptureEvenForUnselectedDevices(string mismatch)
    {
        await using var r = new Rig(2); await r.Open(); var devices = r.Inventory();
        switch (mismatch)
        {
            case "extra": devices = [.. devices, devices[0] with { Id = 900, Location = "HID: another" }]; break;
            case "missing": devices = [devices[0]]; break;
            case "location": devices[1] = devices[1] with { Location = devices[0].Location }; break;
            case "vendor": devices[1] = devices[1] with { Vendor = "Other" }; break;
            case "serial": devices[1] = devices[1] with { Serial = "unverified" }; break;
            case "zone-order": devices[1] = devices[1] with { Zones = devices[1].Zones.Reverse().ToArray() }; break;
            case "count": devices[1].Zones[0] = devices[1].Zones[0] with { LedCount = 79 }; break;
        }
        Assert.Throws<IOException>(() => r.Scope.CheckInventory(devices)); Assert.All(r.Boards, b => Assert.Empty(b.Device.Writes));
    }
    [Fact] public async Task EmptyPresentZonesKeepTheirSdkPositionWithoutBecomingAControllableLight()
    {
        await using var r = new Rig(); r.Boards[0].Device.Available |= 1; r.Boards[0].Device.Set(0, new(0, 0, 0, 0));
        await r.Open(); r.Scope.CheckInventory(r.Inventory()); Assert.Equal(0u, r.Inventory()[0].Zones[0].LedCount);
    }
    [Fact] public async Task ManagedProviderComposesCaptureSdkInspectionOwnedExitAndNativeRecovery()
    {
        await using var r = new Rig(); await using var server = new OpenRgbSdkModelServer(r.Inventory());
        var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, server.Port);
        using var settings = JsonDocument.Parse(profile.Settings); var root = settings.RootElement;
        server.Detectors = root.GetProperty("Detectors").GetRawText(); server.Drivers = root.GetProperty("Drivers").GetRawText();
        server.ServerSettings = root.GetProperty("Server").GetRawText();
        foreach (var key in new[] { "Client", "ProfileManager", "QMKOpenRGBDevices", "QMKVialRGBDevices", "ASRock Polychrome USB" })
            server.StartupSettings[key] = root.GetProperty(key).GetRawText();
        var detection = new MockControlScope(r.Scope, false) { AfterAttach = r.Detect };
        await using var provider = new OpenRgbManagedDeviceProvider(Installation, r.Directory.Path, "scope-boot", profile, detection, [], (bound, release) =>
        {
            Assert.Equal(r.Boards.Select(b => b.Path), bound.CapturedHidPaths);
            using var actual = JsonDocument.Parse(bound.Settings);
            server.StartupSettings["ASRock Polychrome USB"] = actual.RootElement.GetProperty("ASRock Polychrome USB").GetRawText();
            return r.Start(release);
        });
        await provider.PollAsync(default); await provider.PollAsync(default); Assert.Single(provider.CachedDrivers);
        r.Scope.CheckControl();
        await provider.DisposeAsync(); Assert.True(provider.ResourcesClosed); Assert.True(r.Scope.ResourcesClosed);
        Assert.True(r.Child!.HasExited); Assert.Equal(0xfd, r.Boards[0].Device.Order); Assert.Empty(System.IO.Directory.GetFiles(r.Directory.Path));
    }
    [Fact] public async Task BindingRequiresCompletedCaptureAndUsesExactlyTheCapturedSet()
    {
        await using var r = new Rig(2); var template = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6742);
        Assert.Throws<IOException>(() => r.Scope.BindStartup(template));
        await r.Scope.PrepareAsync(default);
        var bound = r.Scope.BindStartup(template);
        Assert.Equal(r.Boards.Select(b => b.Path), bound.CapturedHidPaths); Assert.Empty(template.CapturedHidPaths);
        Assert.All(r.Boards, b => Assert.Equal(b.Opens, b.Closes));
        await r.Scope.DisposeAsync();
        Assert.Throws<IOException>(() => r.Scope.BindStartup(template));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedInventoryOrRevokedContinuityCannotBecomeALaunchAllowlist(bool unplugged)
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default);
        if (unplugged) { r.Boards[0].Watch.Revoked = true; r.AllowUnrestored = true; }
        else r.Boards.Add(new Board(1));
        Assert.Throws<IOException>(() => r.Scope.BindStartup(new(OpenRgbStartupKind.AsrockPolychromeUsb, 6742)));
    }
}
