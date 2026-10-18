using System.Security.Cryptography;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

// Exercise saved configuration through cold startup and the configuration authority, rather than
// manufacturing a stable identity or configuring a driver directly with a replacement binding.
public sealed class EneMemorySavedAuthorityTests
{
    private const string Installation = "af34df7628514347b61bebae979da711", Bus = "recording-bus";
    private static string Id(byte address) => DeviceIdentity.Create(Installation, "ene-memory",
        "location:" + SettingsTransfer.Hash(new { Bus, Address = address }));
    private static EndpointReference Target(byte address) => new(Id(address), "zone:0");
    private static EnePlatformEvidence ZeroSerialPair => EneMemoryBackendTests.Evidence with
    {
        Dimms = [
            new("DIMM 1", "P0 CHANNEL A", "Unknown", "F5-6000J3040G32G", "00000000", 32UL << 30, 34, 4800),
            new("DIMM 1", "P0 CHANNEL B", "Unknown", "F5-6000J3040G32G", "00000000", 32UL << 30, 34, 4800)]
    };

    private sealed class Rig : IAsyncDisposable
    {
        public readonly Rack Rack;
        public readonly TestClock Clock = new();
        public readonly EneMemoryDeviceBackend Backend;
        public readonly EneMemoryDeviceProvider Provider;
        public readonly ConfigurationGenerationStore Store;
        public EnePlatformEvidence Evidence = ZeroSerialPair;
        public DeviceRuntimeStartup Startup = null!;
        private DeviceServiceHello hello = null!;
        private long sequence;
        public DeviceControlRuntime Runtime => Startup.Runtime;
        private Rig(string directory, Rack rack)
        {
            Rack = rack;
            Backend = new([rack.Transport() with { ReadPlatformFingerprint = () => EneMemoryPlatformCapture.Fingerprint(Evidence) }], Clock);
            Provider = new(Installation, directory, "same-windows-boot", Backend);
            Store = DeviceConfigurationCodec.Store(Path.Combine(directory, "configuration"));
            if (Store.ReadActive() is null)
                Store.Activate(Store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(Installation))));
        }
        public static async Task<Rig> Open(string directory, Rack rack)
        {
            var rig = new Rig(directory, rack);
            rig.Startup = await DeviceRuntimeStartup.CreateAsync(rig.Store, Installation, () => new([], [rig.Provider]),
                ["user"], new TestLog(), rig.Clock, demo: true);
            rig.hello = rig.Runtime.OpenSession("user");
            return rig;
        }
        public EneMemoryDeviceDriver Driver(byte address) => Assert.IsType<EneMemoryDeviceDriver>(
            Provider.CachedDrivers.Single(d => d.DeviceId == Id(address)));
        public bool Ready(byte address) => Driver(address).Observe()?.Endpoints.Single().Capabilities
            .Single(c => c.Feature == DeviceFeature.FixedColor).Usable == true;
        public Task<DeviceServiceReply> Send(DeviceConfigurationRequest request) => Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session,
            Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.Configuration, Configuration: request)).WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<int> Prepare()
        {
            var reply = await Send(new(DeviceConfigurationOperation.PrepareConnectionLighting));
            Assert.True(reply.Success, reply.Error);
            return Assert.IsType<int>(reply.Configuration!.PreparedLighting);
        }
        public DeviceServiceRequest ColorRequest(byte address)
        {
            var state = Runtime.State(); var endpoint = state.Endpoints.Single(e => e.Target == Target(address));
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetLighting,
                state.Inventory.TopologyRevision, [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)],
                Lighting: new("fixed", [new(40, 50, 60)]));
        }
        public async Task Save(DeviceConfigurationPackage configuration)
        {
            var bytes = DeviceConfigurationCodec.SerializePackage(configuration);
            var begin = await Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: Startup.Generation.Id,
                TotalBytes: bytes.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))));
            Assert.True(begin.Success, begin.Error); var transfer = begin.Configuration!.TransferId!;
            for (var offset = 0; offset < bytes.Length;)
            {
                var part = bytes.AsSpan(offset, Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length - offset)).ToArray();
                var append = await Send(new(DeviceConfigurationOperation.Append, transfer, Offset: offset, Data: part));
                Assert.True(append.Success, append.Error); offset += part.Length;
            }
            var validation = await Send(new(DeviceConfigurationOperation.Validate, transfer));
            Assert.True(validation.Success, validation.Error);
            var applied = await Send(new(DeviceConfigurationOperation.Apply, transfer, TopologyRevision: Runtime.State().Inventory.TopologyRevision));
            Assert.True(applied.Success, applied.Error);
        }
        public async Task Register(params byte[] addresses)
        {
            var before = Startup.Configuration;
            await Save(before with
            {
                Devices = before.Devices with { Devices = addresses.Select(a => new RegisteredDevice(Id(a), "Memory " + a.ToString("x2"),
                    "ene-memory", DeviceKind.Memory, [new("zone:0", "RGB", EndpointKind.Lighting, Setup: new(LedCount: 8, LedCountConfirmed: true))])).ToArray() },
                Bindings = before.Bindings with { Devices = addresses.Select(a => Driver(a).BindingCandidate!).ToArray() },
                Lighting = before.Lighting with
                {
                    Endpoints = addresses.Select(a => new LightingEndpointConfiguration(Target(a), true,
                        new(a == 0x71 ? LightingExitAction.Off : LightingExitAction.RestoreOriginal))).ToArray(),
                    Preferences = addresses.Select(a => new EndpointLighting(Target(a), new("fixed", [new(90, 20, 10)]))).ToArray()
                }
            });
            Assert.All(addresses, a => Assert.True(Ready(a)));
            Assert.Empty(Rack.Writes);
        }
        public async Task Rediscover() { await Runtime.PollAsync(); await Runtime.PollAsync(); }
        public async ValueTask DisposeAsync()
        {
            Rack.OnIdentify = null; Rack.BeforeData = null; Rack.FailRead = false; Rack.FailWrite = false;
            await Startup.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameBootRestartKeepsZeroSerialSavedBindingsUnchangedAndRequiresANewExplicitOperation()
    {
        using var directory = new ServiceTestDirectory();
        DeviceConfigurationPackage saved; ConfigurationPointer pointer; EneMemoryConnection[] connections; DeviceServiceRequest oldRequest;
        await using (var original = await Rig.Open(directory.Path, new Rack(0x71, 0x73)))
        {
            await original.Register(0x71, 0x73);
            var applied = await original.Runtime.ExecuteAsync(original.ColorRequest(0x71));
            Assert.True(applied.Success, applied.Error); Assert.NotEmpty(original.Rack.Writes);
            var stopped = await original.Runtime.ExecuteAsync(original.ColorRequest(0x71) with { Operation = DeviceOperation.StopLighting, Lighting = null });
            Assert.True(stopped.Success, stopped.Error);
            Assert.Null(new EneMemoryRecovery(directory.Path, Id(0x71)).Pending);
            saved = original.Startup.Configuration; pointer = original.Startup.Generation;
            connections = await original.Backend.DiscoverAsync(default);
            oldRequest = original.ColorRequest(0x71);
        }
        await using var restarted = await Rig.Open(directory.Path, new Rack(0x71, 0x73));
        var current = await restarted.Backend.DiscoverAsync(default);
        Assert.Equal(2, current.Length);
        foreach (var old in connections)
        {
            var next = current.Single(c => c.Address == old.Address);
            Assert.Equal(old.NativeIdentity, next.NativeIdentity);
            Assert.Equal(old.PlatformFingerprint, next.PlatformFingerprint);
            Assert.Equal(old.ControllerFingerprint, next.ControllerFingerprint);
            Assert.NotEqual(old.ConnectionId, next.ConnectionId);
            Assert.NotEqual(saved.Bindings.Devices.Single(b => b.DeviceId == Id(old.Address)), restarted.Driver(old.Address).BindingCandidate);
            Assert.True(restarted.Ready(old.Address));
        }
        Assert.Equal(0, await restarted.Prepare());
        Assert.False((await restarted.Runtime.ExecuteAsync(oldRequest)).Success);
        Assert.Equal(SettingsTransfer.Hash(saved), SettingsTransfer.Hash(restarted.Startup.Configuration));
        Assert.Equal(pointer, restarted.Store.ReadActivePointer());
        Assert.All(restarted.Runtime.State().Inventory.Devices, d => Assert.Equal(IdentityConfidence.ConnectionBound, d.Device.IdentityConfidence));
        Assert.All(restarted.Runtime.State().Endpoints, e => { Assert.False(e.Owned); Assert.Null(e.RequestedLighting); });
        Assert.Empty(restarted.Rack.Writes);
        var selected = await restarted.Runtime.ExecuteAsync(restarted.ColorRequest(0x71));
        Assert.True(selected.Success, selected.Error);
        Assert.Equal(DevicePolicySaveState.Saved, Assert.Single(selected.Result!.Targets).Persistence!.State);
        Assert.Equal(SettingsTransfer.Hash(saved.Bindings), SettingsTransfer.Hash(restarted.Startup.Configuration.Bindings));
        Assert.NotEmpty(restarted.Rack.Writes);
    }

    [Theory]
    [InlineData("invalidate")]
    [InlineData("platform")]
    [InlineData("controller")]
    public async Task EvidenceChangeInvalidatesOldOperationWithoutAdoptingSavedBindingOrReplayingColor(string change)
    {
        using var directory = new ServiceTestDirectory(); await using var rig = await Rig.Open(directory.Path, new Rack(0x71));
        await rig.Register(0x71); var saved = rig.Startup.Configuration; var binding = rig.Driver(0x71).BindingCandidate;
        var oldRequest = rig.ColorRequest(0x71);
        if (change == "invalidate") rig.Backend.Invalidate();
        if (change == "platform") rig.Evidence = rig.Evidence with { BiosVersion = "replacement-BIOS" };
        if (change == "controller")
        {
            var known = rig.Rack.Identity; rig.Rack.Identity = known! with { Firmware = "unrecognized-controller" };
            await rig.Rediscover(); Assert.False(rig.Ready(0x71)); Assert.Equal(0, await rig.Prepare());
            rig.Rack.Identity = known;
        }
        await rig.Rediscover();
        Assert.NotEqual(binding, rig.Driver(0x71).BindingCandidate);
        Assert.Equal(0, await rig.Prepare()); Assert.True(rig.Ready(0x71));
        Assert.False((await rig.Runtime.ExecuteAsync(oldRequest)).Success);
        Assert.Equal(SettingsTransfer.Hash(saved), SettingsTransfer.Hash(rig.Startup.Configuration));
        Assert.Empty(rig.Rack.Writes);
    }

    [Fact]
    public async Task ANewAddressUsesItsOwnCurrentOperationWithoutInheritingRegisteredSettingsOrOutput()
    {
        using var directory = new ServiceTestDirectory(); var rack = new Rack(0x71, 0x73); rack.Present.Remove(0x73);
        await using var rig = await Rig.Open(directory.Path, rack); await rig.Register(0x71);
        var saved = rig.Startup.Configuration;
        rack.Present.Remove(0x71); rack.Present.Add(0x73); await rig.Rediscover();
        Assert.True(rig.Ready(0x73)); Assert.Equal(0, await rig.Prepare());
        Assert.False(rig.Driver(0x73).UsesConnectionLighting);
        Assert.DoesNotContain(rig.Startup.Configuration.Bindings.Devices, b => b.DeviceId == Id(0x73));
        Assert.DoesNotContain(rig.Startup.Configuration.Lighting.Endpoints!, e => e.Target == Target(0x73));
        Assert.DoesNotContain(rig.Startup.Configuration.Lighting.Preferences!, e => e.Target == Target(0x73));
        var current = rig.Runtime.State().Endpoints.Single(e => e.Target == Target(0x73));
        Assert.False(current.Owned); Assert.Null(current.RequestedLighting);
        Assert.Equal(SettingsTransfer.Hash(saved), SettingsTransfer.Hash(rig.Startup.Configuration));
        Assert.Empty(rack.Writes);
        var selected = await rig.Runtime.ExecuteAsync(rig.ColorRequest(0x73));
        Assert.True(selected.Success, selected.Error);
        Assert.Equal(DevicePolicySaveState.SessionOnly, Assert.Single(selected.Result!.Targets).Persistence!.State);
        Assert.Equal(SettingsTransfer.Hash(saved), SettingsTransfer.Hash(rig.Startup.Configuration));
    }

    [Theory]
    [InlineData("led-count")]
    [InlineData("led-confirmation")]
    [InlineData("wiring-confirmation")]
    public async Task FixedEightLedModelUsesActualExtentWithoutPromotingLegacyApprovalMetadata(string change)
    {
        using var directory = new ServiceTestDirectory(); await using var rig = await Rig.Open(directory.Path, new Rack(0x71));
        await rig.Register(0x71); var before = rig.Startup.Configuration; var binding = rig.Driver(0x71).BindingCandidate;
        var next = before with
        {
            Devices = before.Devices with { Devices = before.Devices.Devices.Select(d => d with
            {
                Endpoints = d.Endpoints.Select(e => e with { Setup = e.Setup! with
                {
                    LedCount = change == "led-count" ? 7 : e.Setup!.LedCount,
                    LedCountConfirmed = change != "led-confirmation"
                } }).ToArray()
            }).ToArray() },
            Lighting = before.Lighting with { Endpoints = before.Lighting.Endpoints!.Select(e => e with
                { WiringConfirmed = change != "wiring-confirmation" }).ToArray() }
        };
        await rig.Save(next); await rig.Rediscover(); var saved = rig.Startup.Configuration;
        Assert.Equal(binding, rig.Driver(0x71).BindingCandidate);
        Assert.Equal(0, await rig.Prepare()); Assert.True(rig.Ready(0x71));
        Assert.Equal(SettingsTransfer.Hash(saved), SettingsTransfer.Hash(rig.Startup.Configuration));
        Assert.Empty(rig.Rack.Writes);
        var selected = await rig.Runtime.ExecuteAsync(rig.ColorRequest(0x71));
        Assert.True(selected.Success, selected.Error);
        Assert.Equal(SettingsTransfer.Hash(saved.Devices), SettingsTransfer.Hash(rig.Startup.Configuration.Devices));
        Assert.Equal(SettingsTransfer.Hash(saved.Lighting.Endpoints), SettingsTransfer.Hash(rig.Startup.Configuration.Lighting.Endpoints));
    }
}
