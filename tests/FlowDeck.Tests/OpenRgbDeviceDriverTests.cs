using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    private const string Installation = "d3f516653d764da181e6f5a74bcae5c9", Boot = "openrgb-driver-boot";
    private static OpenRgbSdkController Device(uint id = 41, string serial = "FD-UNIT-001") => OpenRgbLightingPlanTests.Split() with
        { Id = id, Serial = serial, Location = "USB-" + id, ActiveMode = 1 };
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 255)]);
    private static readonly LightingProgram Animation = new("color-cycle", [new(100, 50, 20)], Speed: 4);
    // This policy admits only this file's simulated SDK, never real hardware or a packaged service.
    private sealed class TestRoutePolicy(Action checkLocal) : IOpenRgbRoutePolicy
    {
        private static readonly OpenRgbEnvironmentRequirements Rules = new("fixture-only", new('a', 40), [new("Fixture controller", true)], true, true, 2);
        public Action<OpenRgbSdkEnvironment>? EnvironmentCheck;
        public Action<OpenRgbSdkController[]>? InventoryCheck;
        public void CheckLocalRoute() => checkLocal();
        public void CheckEnvironment(OpenRgbSdkEnvironment environment)
        {
            Rules.Check(environment);
            EnvironmentCheck?.Invoke(environment);
        }
        public void CheckInventory(OpenRgbSdkController[] controllers) => InventoryCheck?.Invoke(controllers);
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "FlowDeck-OpenRgb-driver-" + Guid.NewGuid().ToString("N"));
        public readonly OpenRgbSdkModelServer Server;
        public readonly OpenRgbDeviceProvider Provider;
        public readonly DeviceControlRuntime Runtime;
        public readonly TestClock Clock = new();
        public readonly TestClock EnvironmentClock = new();
        public readonly TestRoutePolicy Policy;
        public OpenRgbSdkConnection? Connection;
        public Func<OpenRgbBoundDevice, IOpenRgbLightingTransport?>? TransportFactory;
        public bool RouteAvailable = true;
        public bool ExpectUnresolvedShutdown;
        public DeviceConfigurationAuthority? Authority;
        private ServiceTestDirectory? configurationDirectory;
        private DeviceServiceHello hello = null!;
        public string Owner => hello.Session;
        private long sequence;
        public Rig(params OpenRgbSdkController[] devices) : this(null, devices) { }
        public Rig(string[]? selectedDeviceIds, params OpenRgbSdkController[] devices)
        {
            Server = new(devices.Length == 0 ? [Device()] : devices);
            Policy = new(() => { if (!RouteAvailable) throw new IOException("direct route still owns hardware"); });
            Provider = new(Installation, Directory, Boot, async ct => Connection = await OpenRgbSdkConnection.ConnectAsync(Server.Port, ct, clock: EnvironmentClock),
                Policy, selectedDeviceIds ?? SelectDevices(Server.Devices.Values.ToArray()), device => TransportFactory?.Invoke(device));
            Runtime = new([], ["user"], new TestLog(), Clock, providers: [Provider]);
        }
        public OpenRgbDeviceDriver Driver(uint id = 41)
        {
            var serial = Server.Devices[id].Serial;
            var hash = "serial:" + SettingsTransfer.Hash(new { Server.Devices[id].Vendor, Server.Devices[id].Name, Server.Devices[id].Type, Serial = serial.Trim() });
            return Assert.IsType<OpenRgbDeviceDriver>(Provider.CachedDrivers.Single(d => ((IPhysicalDeviceDriver)d).BindingCandidate?.ProviderIdentity == hash));
        }
        public string Zone(int index, uint id = 41) => OpenRgbDeviceIdentities.Surfaces(Server.Devices[id]).Single(s => s.Zone == index).Endpoint;
        public async Task<Rig> Open(bool configure = true, LightingExitAction exit = LightingExitAction.RestoreOriginal, bool withConfiguration = false)
        {
            await Runtime.PollAsync(); await Runtime.PollAsync();
            if (configure) foreach (var driver in Provider.CachedDrivers.Cast<OpenRgbDeviceDriver>()) Configure(driver, exit);
            if (withConfiguration)
            {
                configurationDirectory = new();
                var devices = Provider.CachedDrivers.Select(d => d.Observe()!).ToArray();
                var package = new DeviceConfigurationPackage(new(2, devices.Select(d => new RegisteredDevice(d.Id, d.Name, d.Provider, d.Kind,
                    d.Endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind)).ToArray())).ToArray()),
                    new(2, Installation, Provider.CachedDrivers.Cast<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray()),
                    new(2, [], []), new(2, [], [], [], [], []), new(2, []), new(2, new(), new(), new(Rules: []), new([], [])),
                    new(2, "native-v2", new('a', 64), [], []));
                var store = DeviceConfigurationCodec.Store(configurationDirectory.Path); store.Activate(store.Stage(DeviceConfigurationCodec.Write(package)));
                Authority = new(store, Installation, store.ReadActive()!); Runtime.InstallConfigurationHandler(Authority);
            }
            Runtime.Refresh(); hello = Runtime.OpenSession("user");
            if (withConfiguration) await Runtime.ReconfigureAsync(hello.Session, Runtime.State().Inventory.TopologyRevision,
                (drivers, _) => Task.FromResult(drivers.Cast<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray()));
            return this;
        }
        public static void Configure(OpenRgbDeviceDriver driver, LightingExitAction exit = LightingExitAction.RestoreOriginal, int? wrongCount = null)
        {
            var options = driver.DescribeSetup();
            driver.ConfigureLighting(driver.BindingCandidate, options.Select(p => new LightingEndpointConfiguration(new(driver.DeviceId, p.EndpointId), true, new(exit))),
                options.Select(p => new RegisteredEndpoint(p.EndpointId, p.EndpointId, EndpointKind.Lighting,
                    Setup: new(LedCount: wrongCount ?? p.DetectedLeds, LedCountConfirmed: true))));
        }
        public DeviceServiceRequest Request(DeviceOperation operation, EndpointReference[] targets, LightingProgram? program = null)
        {
            var state = Runtime.State();
            var versions = targets.Select(t => state.Endpoints.Single(e => e.Target == t)).Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation, state.Inventory.TopologyRevision, versions, Lighting: program);
        }
        public EndpointReference Target(string endpoint, uint id = 41) => new(Driver(id).DeviceId, endpoint);
        public Task<DeviceServiceReply> Command(DeviceOperation operation, EndpointReference[] targets, LightingProgram? program = null) =>
            Runtime.ExecuteAsync(Request(operation, targets, program)).WaitAsync(TimeSpan.FromSeconds(10));
        public async Task Tick(double seconds) { Clock.Advance(seconds); await Runtime.TickLightingAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (ExpectUnresolvedShutdown)
                {
                    // These fixtures intentionally remove hardware or model a crashed owner whose
                    // journal was recovered by another instance. Closing that old owner must fail.
                    await Assert.ThrowsAsync<AggregateException>(() => Runtime.DisposeAsync().AsTask());
                    Assert.False(Runtime.ResourcesClosed);
                    Assert.Contains(Provider.CachedDrivers.Cast<OpenRgbDeviceDriver>(), d => !d.ResourcesClosed);
                }
                else await Runtime.DisposeAsync();
            }
            finally { await Server.DisposeAsync(); configurationDirectory?.Dispose(); }
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory)) File.Delete(file);
            System.IO.Directory.Delete(Directory);
        }
    }
    private static string[] SelectDevices(params OpenRgbSdkController[] devices)
    {
        var snapshots = devices.Select(d => new OpenRgbSdkSnapshot(new(Guid.Empty, 1, d.Id, OpenRgbSdkProtocol.Structure(d)), d, 1)).ToArray();
        return OpenRgbDeviceIdentities.Resolve(snapshots, new Dictionary<OpenRgbSdkTarget, string>()).Values.Where(d => d.Bindable)
            .Select(d => DeviceIdentity.Create(Installation, OpenRgbDeviceProvider.Provider, d.Native)).ToArray();
    }
    private static void Written(DeviceServiceReply reply) => Assert.True(reply.Success && reply.Result?.Outcome == Outcome.WrittenUnverified,
        System.Text.Json.JsonSerializer.Serialize(reply, Contract.Json));

    [Fact]
    public async Task SameModelDevicesHaveSeparateIdentitiesAndDiscoveryDoesNotWrite()
    {
        await using var r = await new Rig(Device(), Device(700, "FD-UNIT-002")).Open(configure: false);
        var inventory = r.Runtime.State().Inventory; Assert.Equal(2, inventory.Devices.Length); Assert.Equal(0, r.Server.WriteCount);
        Assert.All(inventory.Devices, d => { Assert.Equal(DeviceKind.Memory, d.Device.Kind); Assert.Equal(IdentityConfidence.SerialBound, d.Device.IdentityConfidence);
            Assert.Equal(3, d.Device.Endpoints.Length); Assert.All(d.Device.Endpoints, p => Assert.Equal(EndpointKind.Lighting, p.Kind)); });
        Assert.NotEqual(r.Driver().DeviceId, r.Driver(700).DeviceId);
        Assert.All(r.Driver().DescribeSetup(), o => { Assert.Equal(o.MinimumLeds, o.MaximumLeds); Assert.Equal(o.MinimumLeds, o.DetectedLeds); });
    }

    [Fact]
    public async Task CommonServiceWritesOnlySelectedPhysicalDeviceAndZoneThenRestores()
    {
        await using var r = await new Rig(Device(), Device(700, "FD-UNIT-002")).Open();
        var before = OpenRgbLightingPlan.State(r.Server.Devices[41]); var other = OpenRgbLightingPlan.State(r.Server.Devices[700]);
        var target = r.Target(r.Zone(0)); Written(await r.Command(DeviceOperation.SetLighting, [target], Blue));
        Assert.Equal(new RgbColor(0, 0, 255), r.Server.Devices[41].Colors[0]); Assert.Equal(before.Colors[1], r.Server.Devices[41].Colors[1]);
        Assert.True(OpenRgbLightingPlan.Same(other, OpenRgbLightingPlan.State(r.Server.Devices[700])));
        Written(await r.Command(DeviceOperation.StopLighting, [target]));
        Assert.True(OpenRgbLightingPlan.Same(before, OpenRgbLightingPlan.State(r.Server.Devices[41])));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task WholeDeviceAndZoneOwnershipCannotOverlap()
    {
        await using var r = await new Rig().Open(); var zone = r.Target(r.Zone(0)); var whole = r.Target("device");
        Written(await r.Command(DeviceOperation.SetLighting, [zone], Blue)); var count = r.Server.WriteCount;
        Assert.False((await r.Command(DeviceOperation.SetLighting, [whole], Blue)).Success); Assert.Equal(count, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.StopLighting, [zone])); Written(await r.Command(DeviceOperation.SetLighting, [whole], Blue)); count = r.Server.WriteCount;
        Assert.False((await r.Command(DeviceOperation.SetLighting, [zone], Blue)).Success); Assert.Equal(count, r.Server.WriteCount);
    }

    [Fact]
    public async Task NativeEffectUsesCommonCapabilitiesWithoutInventingTemperatureSensors()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver();
        var effect = driver.Observe()!.Endpoints.Single(p => p.Id == "device").NativeLightingEffects![1];
        var original = OpenRgbLightingPlan.State(r.Server.Devices[41]);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], new(effect.Id, [new(100, 0, 0)], Speed: 50, Reverse: true)));
        Assert.Equal(51u, r.Server.Devices[41].Modes[1].Speed); Assert.Equal(1u, r.Server.Devices[41].Modes[1].Direction);
        Assert.Empty(r.Runtime.State().Telemetry ?? []);
        Written(await r.Command(DeviceOperation.StopLighting, [r.Target("device")]));
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(r.Server.Devices[41])));
    }

    [Fact]
    public async Task NativeParameterChangesDoNotChangeCapabilityGeneration()
    {
        await using var r = await new Rig().Open(); var before = r.Runtime.State().Inventory.Devices.Single().Generation;
        var effect = r.Driver().Observe()!.Endpoints.Single(p => p.Id == "device").NativeLightingEffects![1];
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], new(effect.Id, [new(100, 0, 0)], Speed: 80)));
        await r.Runtime.PollAsync(); r.Runtime.Refresh(); Assert.Equal(before, r.Runtime.State().Inventory.Devices.Single().Generation);
    }

    [Fact]
    public async Task CommonAnimationCanStopOneZoneAndContinueTheOther()
    {
        await using var r = await new Rig().Open(); var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        Written(await r.Command(DeviceOperation.SetLighting, [a, b], Animation)); await r.Tick(.5);
        Assert.Equal(r.Server.Devices[41].Colors[0], r.Server.Devices[41].Colors[1]);
        var stop = await r.Command(DeviceOperation.StopLighting, [a]); Assert.True(stop.Success, stop.Error ?? stop.Result?.Message);
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]); await r.Tick(.5);
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]); Assert.NotEqual(r.Server.Devices[41].Colors[0], r.Server.Devices[41].Colors[1]);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target == b).Lighting?.Running);
        Assert.False(r.Runtime.State().Endpoints.Single(e => e.Target == a).Lighting?.Running == true);
        var final = await r.Command(DeviceOperation.StopLighting, [b]);
        Assert.True(final.Success); Assert.True(Assert.Single(final.Result!.Targets).Outcome is Outcome.Confirmed or Outcome.WrittenUnverified);
        Assert.False(r.Runtime.State().Endpoints.Single(e => e.Target == b).Owned);
    }

    [Fact]
    public async Task IndependentZoneStreamsAndNativeSiblingShareTheControllerWithoutOverlappingWrites()
    {
        await using var r = await new Rig().Open(); var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        Written(await r.Command(DeviceOperation.SetLighting, [a], Animation));
        Written(await r.Command(DeviceOperation.SetLighting, [b], Blue)); var sibling = r.Server.Devices[41].Colors[1];
        await r.Tick(.5); Assert.Equal(sibling, r.Server.Devices[41].Colors[1]);
        Written(await r.Command(DeviceOperation.StopLighting, [b])); Written(await r.Command(DeviceOperation.SetLighting, [b], Animation));
        await r.Tick(.5); Assert.All(r.Runtime.State().Endpoints.Where(e => e.Target == a || e.Target == b), e => Assert.True(e.Lighting?.Running));
    }

    [Fact]
    public async Task MissingConfirmationAndWrongLedCountKeepOutputDisabled()
    {
        await using var r = await new Rig().Open(configure: false); var driver = r.Driver(); var target = r.Target("device");
        Assert.False((await r.Command(DeviceOperation.SetLighting, [target], Blue)).Success);
        Rig.Configure(driver, wrongCount: 123); r.Runtime.Refresh(); Assert.False((await r.Command(DeviceOperation.SetLighting, [target], Blue)).Success);
        Assert.Equal(0, r.Server.WriteCount); Rig.Configure(driver); r.Runtime.Refresh(); Written(await r.Command(DeviceOperation.SetLighting, [target], Blue));
    }

    [Fact]
    public async Task ExpiredFrameDoesNotChangeHardwareAndFreshFrameCanFollow()
    {
        await using var r = await new Rig().Open(); var stream = await r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default);
        var rejected = await stream.WriteAsync(new(1, new(0, 0, 255), () => new(Expired: true)), default);
        Assert.Equal(DeviceDriverFailure.LightingFrameExpired, Assert.Single(rejected).Result.Failure); Assert.Equal(0, r.Server.WriteCount);
        var fresh = await stream.WriteAsync(new(2, new(0, 255, 0), () => new(ValidFor: TimeSpan.FromMilliseconds(500))), default);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(fresh).Result.Outcome);
        await stream.EndAsync(DeviceLightingStopReason.ServiceStopping, default);
    }

    [Fact]
    public async Task OldGenerationCannotWriteAfterSdkIdChangesEvenWhenSerialMatches()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver(); var originalId = driver.DeviceId;
        var old = r.Request(DeviceOperation.SetLighting, [r.Target("device")], Blue);
        var device = r.Server.Devices[41] with { Id = 901 }; r.Server.Devices.Remove(41); r.Server.Devices.Add(901, device);
        r.Server.Wire.Devices.Clear(); r.Server.Wire.Devices.Add(901, OpenRgbSdkModelServer.Encode(device));
        var revision = r.Connection!.InventoryRevision; await r.Server.Wire.SendAsync(0, 100, []);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while (r.Connection.InventoryRevision == revision) await Task.Delay(10, timeout.Token);
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
        Assert.Equal(originalId, r.Driver(901).DeviceId); Assert.False((await r.Runtime.ExecuteAsync(old)).Success); Assert.Equal(0, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device", 901)], Blue));
    }

    [Fact]
    public async Task DuplicateSerialAndLocationRemainVisibleButCannotBeBound()
    {
        var first = Device(); var second = first with { Id = 700 };
        await using var r = await new Rig(first, second).Open(configure: false);
        Assert.Equal(2, r.Provider.CachedDrivers.Length); Assert.All(r.Provider.CachedDrivers.Cast<OpenRgbDeviceDriver>(), driver =>
        {
            Assert.Null(driver.BindingCandidate); Assert.Equal(IdentityConfidence.ConnectionBound, driver.Observe()!.IdentityConfidence);
            Assert.Contains(driver.Observe()!.Endpoints.SelectMany(p => p.Capabilities), c => c.Feature == DeviceFeature.FixedColor && !c.Usable);
        });
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task RouteGuardCoversDiscoveryAndEveryOutput()
    {
        await using var r = new Rig(); r.RouteAvailable = false;
        await Assert.ThrowsAnyAsync<IOException>(() => r.Provider.PollAsync(default)); Assert.Empty(r.Server.Wire.Requests);
        r.RouteAvailable = true; await r.Open(); r.RouteAvailable = false;
        Assert.Equal(Outcome.Rejected, (await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue)).Result!.Outcome); Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task RemovedZoneCannotHideItsRecoveryObligation()
    {
        await using var r = await new Rig().Open(); Written(await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(1))], Blue));
        var files = System.IO.Directory.GetFiles(r.Directory); Assert.Single(files);
        var shape = r.Server.Devices[41]; r.Server.Devices[41] = shape with { Zones = [shape.Zones[0] with { LedCount = 2, MaximumLeds = 2 }] };
        await r.Runtime.PollAsync();
        var result = await r.Driver().ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.Contains(result, v => v.Result.Outcome == Outcome.Unknown); Assert.All(files, file => Assert.True(File.Exists(file)));
        r.ExpectUnresolvedShutdown = true;
    }

    [Fact]
    public async Task NativeOnlyDeviceDoesNotAdvertiseDirectSynchronization()
    {
        var source = Device(); source = source with { Modes = [source.Modes[1]], ActiveMode = 0,
            Zones = source.Zones.Select(z => z with { Modes = [z.Modes[1]] }).ToArray() };
        await using var r = await new Rig(source).Open(); var endpoint = r.Driver().Observe()!.Endpoints.Single(p => p.Id == "device");
        Assert.Equal(ImplementationSupport.Unsupported, endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.ColorFrames).Support);
        Assert.Equal(ImplementationSupport.Supported, endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.NativeEffect).Support);
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Animation)).Success); Assert.Equal(0, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], new(endpoint.NativeLightingEffects![0].Id, [new(0, 255, 0)])));
    }

    [Fact]
    public async Task StartingAndEndingUnsentStreamDoesNotLosePreviousFixedControlOnServiceRelease()
    {
        await using var r = await new Rig().Open(); var original = OpenRgbLightingPlan.State(r.Server.Devices[41]);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0))], Blue));
        _ = await r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default);
        var result = await r.Driver().ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.All(result, v => Assert.True(v.Result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified, v.Result.Message));
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(r.Server.Devices[41]))); Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task AckWithoutStateChangeFailsOneDeviceWithoutWritingAnother()
    {
        await using var r = await new Rig(Device(), Device(700, "FD-UNIT-002")).Open(); r.Server.IgnoreWriteNumber = 1;
        var failed = await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue); Assert.Equal(Outcome.Unknown, failed.Result!.Outcome);
        Assert.Equal(1, r.Server.WriteCount); Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Device == 700 && p.Command is 1050 or 1051 or 1101 or 1103);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device", 700)], Blue));
    }

    [Fact]
    public async Task NormalStopRetriesThePersistedColorAfterAPartialFailure()
    {
        await using var r = await new Rig().Open(); var stream = await r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default);
        var sent = await stream.WriteAsync(new(1, new(0, 0, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500))), default);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(sent).Result.Outcome);
        r.Server.IgnoreWriteNumber = r.Server.WriteCount + 2;
        Assert.Equal(Outcome.Unknown, Assert.Single(await stream.EndAsync(DeviceLightingStopReason.Normal, default)).Result.Outcome);
        r.Server.IgnoreWriteNumber = 0;
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await stream.EndAsync(DeviceLightingStopReason.Normal, default)).Result.Outcome);
        Assert.Equal(new RgbColor(100, 50, 20), r.Server.Devices[41].Colors[0]); Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }

    [Fact]
    public async Task StreamOnRemovedZoneDoesNotReportAnUnrelatedEmptySlotAsRestored()
    {
        await using var r = await new Rig().Open(); var endpoint = r.Zone(1);
        var stream = await r.Driver().OpenLightingStreamAsync([endpoint], Animation, default);
        await stream.WriteAsync(new(1, new(0, 0, 255), () => new(ValidFor: TimeSpan.FromMilliseconds(500))), default);
        var count = r.Server.WriteCount; var shape = r.Server.Devices[41];
        r.Server.Devices[41] = shape with { Zones = [shape.Zones[1] with { LedCount = 2, MaximumLeds = 2 }] };
        await r.Provider.PollAsync(default);
        var end = Assert.Single(await stream.EndAsync(DeviceLightingStopReason.DeviceChanged, default));
        Assert.Equal(endpoint, end.EndpointId); Assert.Equal(Outcome.Unknown, end.Result.Outcome);
        Assert.Equal(count, r.Server.WriteCount); Assert.NotEmpty(System.IO.Directory.GetFiles(r.Directory));
        r.ExpectUnresolvedShutdown = true;
    }

    [Fact]
    public async Task ControllerWideNativeEffectRejectsLocalStreamingBeforeOwnershipIsClaimed()
    {
        var device = Device(); device.Modes[1] = device.Modes[1] with { Flags = device.Modes[1].Flags | 1024 };
        await using var r = await new Rig(device).Open();
        Assert.NotNull(r.Driver().ValidateLightingStream([r.Zone(0)], Animation));
        await Assert.ThrowsAnyAsync<IOException>(() => r.Driver().OpenLightingStreamAsync([r.Zone(0)], Animation, default));
        Assert.Equal(0, r.Server.WriteCount);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
    }

    [Fact]
    public async Task SerialBoundRecoveryAfterListChangeUsesOnlyTheFreshSdkTarget()
    {
        await using var r = await new Rig().Open(); var original = OpenRgbLightingPlan.State(r.Server.Devices[41]);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
        var boundary = r.Server.Wire.Requests.Count;
        var changed = r.Server.Devices[41] with { Id = 901 }; r.Server.Devices.Remove(41); r.Server.Devices.Add(901, changed);
        r.Server.Wire.Devices.Clear(); r.Server.Wire.Devices.Add(901, OpenRgbSdkModelServer.Encode(changed));
        var revision = r.Connection!.InventoryRevision; await r.Server.Wire.SendAsync(0, 100, []);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while (r.Connection.InventoryRevision == revision) await Task.Delay(10, timeout.Token);
        await r.Provider.PollAsync(default);
        Assert.NotNull(r.Driver(901).Validate(new("device", DeviceOperation.SetLighting, Lighting: Blue)));
        var released = await r.Driver(901).ReleaseAsync(DeviceReleaseReason.StartupRecovery, default);
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(released).Result.Outcome);
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(r.Server.Devices[901])));
        Assert.All(r.Server.Wire.Requests.Skip(boundary).Where(p => p.Command is 1 or 1050 or 1051 or 1101 or 1103), p => Assert.Equal(901u, p.Device));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory));
    }
}
