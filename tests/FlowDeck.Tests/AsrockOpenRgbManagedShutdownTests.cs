using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using static FlowDeck.Tests.AsrockOpenRgbNativeOperationTests;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    // Allows only this in-process SDK/HID emulator to exercise the compiled native transport.
    // This legacy emulator isolates shutdown tests; the native SDK integration tests use
    // the actual scope readiness guard. The full product executable is never started here.
    private sealed class MockControlScope(AsrockOpenRgbDetectionScope actual, bool allowControl = true) : IOpenRgbDetectionScope, IOpenRgbNativeShutdownScope
    {
        public bool RejectHandover;
        public int Handovers;
        public Action? AfterAttach;
        public string[] ResourceDomains => actual.ResourceDomains;
        public bool ResourcesClosed => actual.ResourcesClosed;
        public void CheckRoute() => actual.CheckRoute();
        public Task PrepareAsync(CancellationToken ct) => actual.PrepareAsync(ct);
        public OpenRgbStartupProfile BindStartup(OpenRgbStartupProfile profile) => actual.BindStartup(profile);
        public void AttachHelper(OpenRgbOwnedProcess owner) { actual.AttachHelper(owner); AfterAttach?.Invoke(); }
        public void CheckInventory(OpenRgbSdkController[] controllers) => actual.CheckInventory(controllers);
        public void CheckControl() { if (allowControl) actual.CheckRoute(); else actual.CheckControl(); }
        public IOpenRgbLightingTransport? CreateLightingTransport(OpenRgbBoundDevice device) => actual.CreateLightingTransport(device);
        public void CheckNativeShutdown(OpenRgbRecoveryRecord[] records)
        {
            Handovers++;
            if (RejectHandover) throw new IOException("test: coverage refused");
            actual.CheckNativeShutdown(records);
        }
        public ValueTask DisposeAsync() => actual.DisposeAsync();
        public DeviceShutdownRecovery[] ReadNativeShutdownRecoveries() => actual.ReadNativeShutdownRecoveries();
    }
    private sealed class ManagedRig : IAsyncDisposable
    {
        public readonly Rig Native = new();
        public readonly OpenRgbSdkModelServer Server;
        public readonly MockControlScope Detection;
        public readonly OpenRgbManagedDeviceProvider Provider;
        public AsrockOpenRgbOperationReceipt Receipt = Initial();
        private readonly AsrockPacketEmulator nativePackets = new();
        public int NativeWrites;
        public bool DropAfterMutation;
        public TaskCompletionSource? WriteEntered, WriteResume;
        public ManagedRig()
        {
            Detection = new(Native.Scope);
            Detection.AfterAttach = () => { Native.Detect(); Receipt = Native.DetectionReceipts.Values.Single(); nativePackets.Sequence = Native.DetectionSequence; };
            var device = Native.Inventory().Single() with { Modes = AsrockOpenRgbNativePlanTests.Modes(), ActiveMode = 1 };
            Server = new(device);
            var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, Server.Port);
            using var settings = JsonDocument.Parse(profile.Settings); var root = settings.RootElement;
            Server.Detectors = root.GetProperty("Detectors").GetRawText(); Server.Drivers = root.GetProperty("Drivers").GetRawText();
            Server.ServerSettings = root.GetProperty("Server").GetRawText();
            foreach (var key in new[] { "Client", "ProfileManager", "QMKOpenRGBDevices", "QMKVialRGBDevices", "ASRock Polychrome USB" })
                Server.StartupSettings[key] = root.GetProperty(key).GetRawText();
            var snapshot = new OpenRgbSdkSnapshot(new(Guid.Empty, 1, device.Id, OpenRgbSdkProtocol.Structure(device)), device, 1);
            var identity = OpenRgbDeviceIdentities.Resolve([snapshot], new Dictionary<OpenRgbSdkTarget, string>()).Values.Single();
            var id = DeviceIdentity.Create(Installation, OpenRgbDeviceProvider.Provider, identity.Native);
            Provider = new(Installation, Native.Directory.Path, "scope-boot", profile, Detection, [id], async (bound, release) =>
            {
                using var actual = JsonDocument.Parse(bound.Settings);
                Server.StartupSettings["ASRock Polychrome USB"] = actual.RootElement.GetProperty("ASRock Polychrome USB").GetRawText();
                return await Native.Start(release);
            });
            var sdk = Server.Wire.Intercept!;
            Server.Wire.Intercept = async packet =>
            {
                if (packet.Command == AsrockOpenRgbOperationReceipt.QueryCommand)
                { await Server.Wire.ReplyAsync(packet, packet.Command, Wire(Receipt)); return true; }
                if (packet.Command is 1050 or 1051 or 1101)
                {
                    if (WriteEntered is not null) { WriteEntered.TrySetResult(); await WriteResume!.Task; }
                    Assert.False(Native.Child!.HasExited);
                    var record = ServiceEnvironment.Read<AsrockOpenRgbRecoveryRecord>(RecordPath);
                    var operation = record.Operation!.Mutation;
                    Assert.NotEmpty(System.IO.Directory.GetFiles(Native.Directory.Path, "openrgb-lighting-*.json"));
                    Native.Boards[0].Device.OwnerThread = null; // The emulated child has its own I/O owner.
                    Receipt = nativePackets.Execute(record, Receipt, Server.Wire, packet.Device, Native.Scope.AuthorizeOperation, Native.Boards[0].Device.Exchange);
                    NativeWrites++;
                    if (DropAfterMutation) { Server.Wire.Disconnect(); return true; }
                }
                return await sdk(packet);
            };
        }
        public string RecordPath => Assert.Single(System.IO.Directory.GetFiles(Native.Directory.Path, AsrockOpenRgbDetectionRecovery.FilePrefix + "*.json"));
        public OpenRgbDeviceDriver Driver => Assert.IsType<OpenRgbDeviceDriver>(Assert.Single(Provider.CachedDrivers));
        public async Task<ManagedRig> Open()
        {
            await Provider.PollAsync(default); await Provider.PollAsync(default);
            var setup = Driver.DescribeSetup();
            // ASRock SDK counts are firmware extents, not detected physical accessories.
            // Preparation resolves that shape internally; the fixture supplies only its exit policy.
            Assert.All(setup, p => Assert.Null(p.DetectedLeds));
            Driver.ConfigureCurrentConnection(Driver.BindingCandidate!, null, [],
                setup.Select(p => new LightingEndpointConfiguration(new(Driver.DeviceId, p.EndpointId), false, new(LightingExitAction.Off))), []);
            return this;
        }
        public async Task<DeviceDriverResult> Write()
        {
            var result = await Driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: new("fixed", [new(0, 0, 255)])), default);
            if (!Provider.ResourcesClosed) Assert.True(result.Outcome != Outcome.Rejected, result.Message);
            return result;
        }
        public void Restored()
        {
            Assert.True(Provider.ResourcesClosed); Assert.True(Driver.ResourcesClosed); Assert.True(Native.Scope.ResourcesClosed);
            Assert.True(Native.Child!.HasExited); Assert.Empty(System.IO.Directory.GetFiles(Native.Directory.Path, "*.json"));
            Assert.Equal(0xfd, Native.Boards[0].Device.Order);
            Assert.All(Native.Boards[0].Device.Raw, p => Assert.Equal(AsrockRgbControl.Packet(p.Key, new(0, 0, 0, 0), 0xfd)[4..9], p.Value));
        }
        public async ValueTask DisposeAsync()
        {
            WriteResume?.TrySetResult(); Detection.RejectHandover = false; DropAfterMutation = false;
            if (Native.Child is not null) Native.Child.RefuseExit = false;
            Native.FailRestoreClose = false;
            await Provider.DisposeAsync();
            await Server.DisposeAsync(); await Native.DisposeAsync();
        }
    }
    [Fact] public async Task ManagedLostSdkReplyClosesHelperThenRestoresNativeAndSettlesBothJournals()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true;
        Assert.Equal(Outcome.Unknown, (await r.Write()).Outcome); Assert.Equal(1, r.NativeWrites);
        await r.Provider.DisposeAsync();
        r.Restored(); Assert.Equal(1, r.NativeWrites); Assert.Equal(1, r.Detection.Handovers);
        await r.Driver.DisposeAsync(); await r.Provider.DisposeAsync(); Assert.Equal(1, r.Detection.Handovers);
    }
    [Fact] public async Task ManagedNativeRestoreNeverStartsUntilOwnedHelperExitIsConfirmed()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true;
        await r.Write(); r.Native.Child!.RefuseExit = true;
        await Assert.ThrowsAnyAsync<Exception>(() => r.Provider.DisposeAsync().AsTask());
        Assert.False(r.Provider.ResourcesClosed); Assert.False(r.Driver.ResourcesClosed);
        Assert.Equal(1, r.Native.Factories); Assert.NotEmpty(System.IO.Directory.GetFiles(r.Native.Directory.Path, "openrgb-lighting-*.json"));
        r.Native.Child.RefuseExit = false; await r.Provider.DisposeAsync(); r.Restored(); Assert.Equal(1, r.NativeWrites);
    }
    [Fact] public async Task FailedNativeHandleCloseRetainsJournalsAndRetryOnlyFinishesTheRemainingClosure()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true;
        await r.Write(); r.Native.FailRestoreClose = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Provider.DisposeAsync().AsTask());
        Assert.True(r.Native.Child!.HasExited); Assert.False(r.Driver.ResourcesClosed);
        Assert.Equal(AsrockOpenRgbRecoveryPhase.Restored, ServiceEnvironment.Read<AsrockOpenRgbRecoveryRecord>(r.RecordPath).Phase);
        var writes = r.Native.Boards[0].Device.Writes.Count;
        r.Native.FailRestoreClose = false; await r.Provider.DisposeAsync(); r.Restored();
        Assert.Equal(writes, r.Native.Boards[0].Device.Writes.Count); Assert.Equal(1, r.NativeWrites);
    }
    [Fact] public async Task FailedCoverageDoesNotTerminateHelperOrDiscardSdkObligation()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true;
        await r.Write(); r.Detection.RejectHandover = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Provider.DisposeAsync().AsTask());
        Assert.False(r.Native.Child!.HasExited); Assert.False(r.Driver.ResourcesClosed); Assert.Equal(1, r.Native.Factories);
        r.Detection.RejectHandover = false; await r.Provider.DisposeAsync(); r.Restored();
    }
    [Fact] public async Task NativeShutdownWaitsForInFlightSdkMutationAndPreventsLaterSdkRelease()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true;
        r.WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); r.WriteResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = r.Write(); await r.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = r.Provider.DisposeAsync().AsTask();
        try { Assert.False(closing.IsCompleted); Assert.False(r.Native.Child!.HasExited); Assert.Equal(0, r.Detection.Handovers); }
        finally { r.WriteResume.TrySetResult(); }
        await write; await closing; r.Restored();
        Assert.Equal(Outcome.Rejected, (await r.Write()).Outcome); Assert.Equal(1, r.NativeWrites);
    }
    [Theory] [InlineData("target")] [InlineData("owner")] [InlineData("boot")] [InlineData("hash")] [InlineData("duplicate")]
    public async Task NativeCoverageRejectsAnUnrelatedSdkRecordWithoutOpeningHardware(string change)
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true; await r.Write();
        var path = Assert.Single(System.IO.Directory.GetFiles(r.Native.Directory.Path, "openrgb-lighting-*.json"));
        var record = ServiceEnvironment.Read<OpenRgbRecoveryRecord>(path);
        var modified = change switch
        {
            "target" => record with { Scope = record.Scope with { Target = record.Scope.Target with { Connection = Guid.NewGuid() } } },
            "owner" => record with { Scope = record.Scope with { Owner = Guid.NewGuid().ToString("N") } },
            "boot" => record with { Boot = "other-boot" },
            "hash" => record with { Hash = new('0', 64) },
            _ => record
        };
        var requests = r.Server.Wire.Requests.Count;
        Assert.Throws<IOException>(() => r.Native.Scope.CheckNativeShutdown(change == "duplicate" ? [record, Contract.Copy(record)] : [modified]));
        Assert.False(r.Native.Child!.HasExited); Assert.Equal(1, r.Native.Factories); Assert.Equal(requests, r.Server.Wire.Requests.Count);
        await r.Provider.DisposeAsync(); r.Restored();
    }
    [Fact] public async Task AlreadyExitedHelperStillAllowsVerifiedNativeRecoveryAfterSdkQuiescence()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true; await r.Write();
        r.Native.Child!.Kill(); await r.Provider.DisposeAsync(); r.Restored(); Assert.Equal(1, r.NativeWrites);
    }
    [Fact] public async Task UnknownPhysicalStateRetainsBothObligationsAfterHelperExit()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true; await r.Write();
        var board = r.Native.Boards[0].Device; var known = board.Raw[5].ToArray(); var writes = board.Writes.Count;
        board.Set(5, new(3, 27, 83, 171, 88));
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => r.Provider.DisposeAsync().AsTask());
            Assert.True(r.Native.Child!.HasExited); Assert.False(r.Provider.ResourcesClosed); Assert.False(r.Driver.ResourcesClosed);
            Assert.Equal(writes, board.Writes.Count); Assert.NotEmpty(System.IO.Directory.GetFiles(r.Native.Directory.Path, "openrgb-lighting-*.json"));
        }
        finally { board.Raw[5] = known; }
        await r.Provider.DisposeAsync(); r.Restored();
    }
    [Fact] public async Task LockedSdkCleanupKeepsDriverUnclosedAndNeverReplaysNativeWrites()
    {
        await using var r = await new ManagedRig().Open(); r.DropAfterMutation = true; await r.Write();
        var path = Assert.Single(System.IO.Directory.GetFiles(r.Native.Directory.Path, "openrgb-lighting-*.json"));
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<AggregateException>(() => r.Provider.DisposeAsync().AsTask());
            Assert.False(r.Driver.ResourcesClosed); Assert.True(r.Native.Child!.HasExited);
            Assert.Equal(AsrockOpenRgbRecoveryPhase.Restored, ServiceEnvironment.Read<AsrockOpenRgbRecoveryRecord>(r.RecordPath).Phase);
            var requests = r.Server.Wire.Requests.Count;
            Assert.All(await r.Driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default), row => Assert.Equal(Outcome.Unknown, row.Result.Outcome));
            await Assert.ThrowsAsync<IOException>(() => r.Driver.DisposeAsync().AsTask()); Assert.Equal(requests, r.Server.Wire.Requests.Count);
        }
        var writes = r.Native.Boards[0].Device.Writes.Count;
        await r.Provider.DisposeAsync(); r.Restored(); Assert.Equal(writes, r.Native.Boards[0].Device.Writes.Count);
    }
    [Fact] public async Task RuntimeCanInvokeTheOwningNativeCoordinatorAfterDriverSdkDisposalFails()
    {
        await using var r = await new ManagedRig().Open();
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), new TestClock(), providers: [r.Provider]);
        var hello = runtime.OpenSession("user"); var state = runtime.State();
        var target = state.Endpoints.Single(e => e.Target == new EndpointReference(r.Driver.DeviceId, "device"));
        Assert.Empty(r.Provider.ReadShutdownRecoveries());
        r.DropAfterMutation = true;
        var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test",
            DeviceOperation.SetLighting, state.Inventory.TopologyRevision, [new(target.Target, target.Generation, target.PolicyRevision)],
            Lighting: new("fixed", [new(0, 0, 255)])));
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        Assert.True(runtime.State().Endpoints.Single(e => e.Target == target.Target).Owned);
        await runtime.DisposeAsync(); Assert.True(runtime.ResourcesClosed); r.Restored();
        // Resource closure must not turn an earlier SDK failure into invented current telemetry.
        var closed = runtime.State(); Assert.Null(closed.LifecycleError);
        Assert.All(closed.Endpoints, endpoint => { Assert.False(endpoint.Owned); Assert.Null(endpoint.ReportedLighting); });
        Assert.All(closed.Inventory.Devices, device => Assert.False(device.Connected));
        var evidence = Assert.Single(r.Provider.ReadShutdownRecoveries()); Assert.All(evidence.Results, e => Assert.Equal(Outcome.Confirmed, e.Result.Outcome));
        evidence.Results[0] = new("changed-copy", new(Outcome.Confirmed, "changed"));
        Assert.DoesNotContain(r.Provider.ReadShutdownRecoveries().Single().Results, e => e.EndpointId == "changed-copy");
    }
}
