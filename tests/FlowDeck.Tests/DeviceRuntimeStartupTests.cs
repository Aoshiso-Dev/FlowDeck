using System.Text;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceRuntimeStartupTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class ColdProvider(HubDeviceDriver driver) : IDeviceDriverProvider
    {
        public int Reads, Disposes;
        public string ProviderId => "test-discovery";
        public string[] ResourceDomains => ["test-discovery"];
        public IDeviceControlDriver[] CachedDrivers => Reads == 0 ? [] : [driver];
        public Task PollAsync(CancellationToken ct) { Reads++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
    internal static async Task<DeviceConfigurationPackage> Package(DemoHub hub, string directory, bool resume = true)
    {
        await using var inventory = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory);
        await inventory.InitializeAsync(); var observation = inventory.Observe()!;
        var safety = new CoolingSafety(30, 100, 50, CoolingExitAction.FixedDuty, []);
        return new(new(2, [new(inventory.DeviceId, "Hub", observation.Provider, DeviceKind.Controller,
                observation.Endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind)).ToArray())]),
            new(2, Installation, [inventory.BindingCandidate!]),
            new(2, [new(new(inventory.DeviceId, "fan1"), true, safety, [], new(FanMode.Manual, 65, SafetySnapshot: safety), true)], []),
            new(2, [], [], [], [], []), new(2, []), new(2, new(), new(true, resume), new(Rules: []), new([], [])),
            new(2, "native-v2", new('a', 64), [], []));
    }
    private static ConfigurationGenerationStore Save(string directory, DeviceConfigurationPackage config)
    {
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory, "configuration"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config))); return store;
    }
    private static DeviceServiceRequest Request(DeviceRuntimeStartup startup, DeviceServiceHello hello, DeviceOperation operation, long sequence = 1, int? duty = null)
    {
        var state = startup.Runtime.State(); var row = state.Endpoints.Single(e => e.Target.EndpointId == "fan1");
        return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), sequence, "test", operation,
            state.Inventory.TopologyRevision, [new(row.Target, row.Generation, row.PolicyRevision)], duty);
    }
    [Fact]
    public async Task NoAdoptedGenerationNeverOpensHardware()
    {
        using var directory = new ServiceTestDirectory(); var called = false;
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateAsync(DeviceConfigurationCodec.Store(directory.Path), Installation,
            () => { called = true; return new([], []); }, ["user"], new TestLog(), demo: true));
        Assert.False(called);
    }
    [Fact]
    public async Task AnInvalidTypedComponentIsRejectedEvenWhenTheStoreOnlyVerifiedItsHash()
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub() { DelayMs = 0 };
        var files = DeviceConfigurationCodec.Write(await Package(hub, directory.Path));
        files["cooling.json"] = Encoding.UTF8.GetBytes("{\"version\":2,\"endpoints\":[],\"groups\":[],\"driverDll\":\"untrusted.dll\"}");
        var store = new ConfigurationGenerationStore(Path.Combine(directory.Path, "configuration"), _ => { });
        store.Activate(store.Stage(files)); var called = false;
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateAsync(store, Installation,
            () => { called = true; return new([], []); }, ["user"], new TestLog(), demo: true));
        Assert.False(called); Assert.Equal(0, hub.Writes);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupUsesTheAdoptedGenerationAndRequiresBothGlobalAndEndpointResume(bool globalResume)
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub() { DelayMs = 0 };
        var config = await Package(hub, directory.Path, globalResume); var store = Save(directory.Path, config);
        var driver = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, () => new([driver], []), ["user"], new TestLog(), demo: true);
        Assert.Equal(store.ReadActive()!.Pointer, startup.Generation); Assert.True(startup.SameInstallation);
        Assert.Null(startup.Runtime.State().LifecycleError); Assert.Equal(0, hub.Writes);
        Assert.Equal(globalResume, driver.CoolingConfiguration("fan1")!.ResumeOnStart);
        // A consumer cannot mutate the adopted safety snapshot through the returned DTO.
        startup.Configuration.Cooling.Endpoints[0] = config.Cooling.Endpoints[0] with { Safety = config.Cooling.Endpoints[0].Safety with { Minimum = 1 } };
        Assert.Equal(30, startup.Configuration.Cooling.Endpoints[0].Safety.Minimum);
        var hello = startup.Runtime.OpenSession("user");
        var reply = await startup.Runtime.ExecuteAsync(Request(startup, hello, DeviceOperation.RestoreSavedCooling));
        Assert.Equal(globalResume, reply.Success); Assert.Equal(globalResume ? 1 : 0, hub.Writes);
    }
    [Fact]
    public async Task ForeignInstallationTemplatesCannotAdoptSavedPolicyOrWriteToItsConfiguration()
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub() { DelayMs = 0 };
        var config = await Package(hub, directory.Path); var store = Save(directory.Path, config);
        var driver = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Guid.NewGuid().ToString("N"), () => new([driver], []), ["user"], new TestLog(), demo: true);
        Assert.False(startup.SameInstallation); Assert.Equal(10, startup.Runtime.State().Telemetry!.Length);
        var template = driver.CoolingConfiguration("fan1")!;
        Assert.Equal(SettingsTransfer.Hash(config.Cooling.Endpoints.Single().Safety), SettingsTransfer.Hash(template.Safety));
        Assert.Null(template.SavedPolicy); Assert.False(template.ResumeOnStart);
        Assert.Equal(0, hub.Writes); var before = startup.Generation;
        var hello = startup.Runtime.OpenSession("user");
        Assert.False((await startup.Runtime.ExecuteAsync(Request(startup, hello, DeviceOperation.RestoreSavedCooling))).Success);
        Assert.False((await startup.Runtime.ExecuteAsync(Request(startup, hello, DeviceOperation.SetDuty, sequence: 2, duty: 60))).Success);
        Assert.Equal(0, hub.Writes); Assert.Equal(before, store.ReadActivePointer());
        Assert.Equal(SettingsTransfer.Hash(config), SettingsTransfer.Hash(startup.Configuration));
    }
    [Fact]
    public async Task AnOldJournalIsRecoveredBeforeTheNewConfigurationIsApplied()
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub() { DelayMs = 0 };
        var config = await Package(hub, directory.Path); var store = Save(directory.Path, config); var binding = config.Bindings.Devices[0];
        new HubEndpointRecoveryJournal(directory.Path, binding.DeviceId).Prepare(DemoHub.Identity.Id, binding.Fingerprint,
            ControlCoordinator.WiringSignature(await hub.ReadAsync()), "fan1", 80, 45, 60);
        var driver = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, () => new([driver], []), ["user"], new TestLog(), demo: true);
        Assert.Null(startup.Runtime.State().LifecycleError); Assert.Equal(80, driver.CachedStatus!.Channels[0].Duty);
        Assert.Equal(1, hub.Writes); Assert.Empty(new HubEndpointRecoveryJournal(directory.Path, binding.DeviceId).Pending);
        Assert.Equal(50, driver.CoolingConfiguration("fan1")!.Safety.Exit);
        var hello = startup.Runtime.OpenSession("user");
        Assert.Equal(65, (await startup.Runtime.ExecuteAsync(Request(startup, hello, DeviceOperation.RestoreSavedCooling))).Result!.Targets[0].ReportedDuty);
    }
    [Fact]
    public async Task RecoveryFailureDoesNotSilentlyAdoptNewControlSettings()
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub() { DelayMs = 0 };
        var config = await Package(hub, directory.Path); var store = Save(directory.Path, config); var binding = config.Bindings.Devices[0];
        new HubEndpointRecoveryJournal(directory.Path, binding.DeviceId).Prepare(DemoHub.Identity.Id, new('b', 64),
            ControlCoordinator.WiringSignature(await hub.ReadAsync()), "fan1", 80, 45, 60);
        var driver = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, () => new([driver], []), ["user"], new TestLog(), demo: true);
        Assert.NotNull(startup.Runtime.State().LifecycleError); Assert.Null(driver.CoolingConfiguration("fan1"));
        Assert.Single(new HubEndpointRecoveryJournal(directory.Path, binding.DeviceId).Pending); Assert.Equal(0, hub.Writes);
        var hello = startup.Runtime.OpenSession("user");
        Assert.False((await startup.Runtime.ExecuteAsync(Request(startup, hello, DeviceOperation.SetDuty, duty: 60))).Success);
    }
    [Fact]
    public async Task ProviderDiscoveredColdDriverIsInitializedAndConfiguredBeforeClientsConnect()
    {
        using var directory = new ServiceTestDirectory(); var hub = new DemoHub() { DelayMs = 0 };
        var config = await Package(hub, directory.Path); var store = Save(directory.Path, config);
        var driver = new HubDeviceDriver(Installation, DemoHub.Identity.Id, hub, directory.Path); var provider = new ColdProvider(driver);
        await using (var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, () => new([], [provider]), ["user"], new TestLog(), demo: true))
        {
            Assert.Equal(2, provider.Reads); Assert.NotNull(driver.CoolingConfiguration("fan1")); Assert.Equal(0, hub.Writes);
            var hello = startup.Runtime.OpenSession("user");
            Assert.Equal(65, (await startup.Runtime.ExecuteAsync(Request(startup, hello, DeviceOperation.RestoreSavedCooling))).Result!.Targets[0].ReportedDuty);
        }
        Assert.Equal(1, provider.Disposes);
    }
}
