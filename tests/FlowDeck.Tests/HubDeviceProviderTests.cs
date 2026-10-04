using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class HubDeviceProviderTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private static readonly HubIdentity A = new("hub-a", "Control Hub", "SERIAL-A", "usb:a"), B = new("hub-b", "Control Hub", "SERIAL-B", "usb:b");
    private sealed class Sensors : IHardwareSensorBackend
    {
        private readonly string connection = Guid.NewGuid().ToString("N");
        public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => Task.FromResult<HardwareSensorDevice[]>([
            new("cpu", "CPU", DeviceKind.Processor, new('a', 64), "cpu-serial", connection, null,
                [new("cpu/temp", "CPU Package", "cpu", DeviceFeature.Temperature, 51, TimeProvider.System.GetTimestamp())])]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Discovery : IHubAdapter
    {
        public HubIdentity[] Found = [];
        public Exception? Failure;
        public int Probes, Disposes;
        public bool FailDispose;
        public Func<Task>? DuringProbe;
        public bool IsDemo => true;
        public string Version => "discovery-test";
        public async Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Probes++;
            if (DuringProbe is { } action) { DuringProbe = null; await action(); }
            if (Failure is not null) throw Failure;
            return Found.ToArray();
        }
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => throw new InvalidOperationException("Discovery must not open hardware");
        public Task<HubStatus> ReadAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task CloseAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { Disposes++; if (FailDispose) throw new IOException("discovery close failed"); return ValueTask.CompletedTask; }
    }
    private sealed class Adapter(Discovery source, HubIdentity identity) : IHubLightingEndpointAdapter
    {
        public DemoHub Inner { get; } = new(identity) { DelayMs = 0 };
        public List<HubIdentity> Opened { get; } = [];
        public int Disposes, Probes, Reads;
        public bool FailDispose, Closed;
        public Func<Task>? DuringRead, AfterLighting, AfterFan, DuringDispose;
        public bool IsDemo => true;
        public string Version => "endpoint-test";
        public bool SupportsFanEndpoints => true;
        public bool SupportsFanDeadlines => true;
        public bool SupportsLightingEndpoints => true;
        public bool SupportsLightingDeadlines => true;
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) { Probes++; return source.ProbeAsync(ct); }
        public Task<HubStatus> OpenAsync(HubIdentity selected, long generation, CancellationToken ct = default)
        { Assert.Equal(identity.Id, selected.Id); Opened.Add(selected); return Inner.OpenAsync(selected, generation, ct); }
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default)
        { Assert.False(Closed); Reads++; if (DuringRead is { } action) { DuringRead = null; await action(); } return await Inner.ReadAsync(ct); }
        public async Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default)
        { var result = await Inner.SetFanEndpointAsync(channel, duty, expected, ct); if (AfterFan is { } action) { AfterFan = null; await action(); } return result; }
        public async Task<HubResult> SetLightingEndpointsAsync(string[] channels, HubLightSetting effect, HubLightingExpectation expected, CancellationToken ct = default)
        { var result = await Inner.SetLightingEndpointsAsync(channels, effect, expected, ct); if (AfterLighting is { } action) { AfterLighting = null; await action(); } return result; }
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task CloseAsync(CancellationToken ct = default) => Inner.CloseAsync(ct);
        public async ValueTask DisposeAsync()
        {
            Disposes++; if (DuringDispose is { } action) await action();
            if (FailDispose) throw new IOException("adapter close failed");
            Closed = true;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceTestDirectory Directory { get; } = new();
        public Discovery Discovery { get; } = new();
        public Dictionary<string, Adapter> Adapters { get; } = [];
        public HubDeviceProvider Provider { get; }
        public Fixture(HubIdentity[] found, string[]? remembered = null)
        {
            Discovery.Found = found;
            Provider = new(Installation, Directory.Path, Discovery, id =>
            {
                var identity = Discovery.Found.SingleOrDefault(h => h.Id == id) ?? new(id, "Control Hub", id, "missing:" + id);
                var adapter = new Adapter(Discovery, identity); Adapters.Add(id, adapter); return adapter;
            }, remembered);
        }
        public HubDeviceDriver Driver(string id) => (HubDeviceDriver)Provider.CachedDrivers.Single(d => d.DeviceId == DeviceIdentity.Create(Installation, HubDeviceDriver.Provider, id));
        public async Task DiscoverAndOpen()
        { await Provider.PollAsync(default); foreach (var driver in Provider.CachedDrivers.Cast<IDevicePollingDriver>()) await driver.PollAsync(default); }
        public async ValueTask DisposeAsync() { try { await Provider.DisposeAsync(); } finally { Directory.Dispose(); } }
    }
    private static void Configure(HubDeviceDriver driver, int exit = 70)
    {
        driver.Configure(driver.BindingCandidate, [new(new(driver.DeviceId, "fan1"), true, new(30, 100, exit, CoolingExitAction.FixedDuty, []), [])]);
        driver.ConfigureLighting(driver.BindingCandidate, [new(new(driver.DeviceId, "led1"), true, new(LightingExitAction.Off))],
            [new("led1", "LED1", EndpointKind.Lighting, Setup: new("DEMO RGB", 24, true))]);
    }

    [Fact] public async Task ReorderedHubsKeepTheirDriversWorkersSettingsAndIndividualOutput()
    {
        await using var f = new Fixture([A, B]); await f.Provider.PollAsync(default);
        Assert.Equal(2, f.Provider.CachedDrivers.Length); Assert.All(f.Adapters.Values, a => Assert.Empty(a.Opened));
        var a = f.Driver(A.Id); var b = f.Driver(B.Id); await f.DiscoverAndOpen(); Configure(a, 60); Configure(b, 80);
        var aLocator = a.Observe()!.Locator; var bLocator = b.Observe()!.Locator;
        f.Discovery.Found = [B, A]; await f.Provider.PollAsync(default);
        Assert.Same(a, f.Driver(A.Id)); Assert.Same(b, f.Driver(B.Id));
        Assert.Equal(aLocator, a.Observe()!.Locator); Assert.Equal(bLocator, b.Observe()!.Locator);
        Assert.Equal(62, (await b.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 62), default)).ReportedDuty);
        Assert.Equal(0, f.Adapters[A.Id].Inner.Writes); Assert.Equal(1, f.Adapters[B.Id].Inner.Writes);
        Assert.Equal(80, Assert.Single(await b.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.ReportedDuty);
        Assert.Equal(0, f.Adapters[A.Id].Inner.Writes);
    }

    [Theory] [InlineData("id")] [InlineData("serial")] [InlineData("path")]
    public async Task AmbiguousEnumerationInvalidatesOldConnectionsAndDoesNotOpenAnAlternative(string duplicate)
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); Configure(driver);
        f.Discovery.Found = [A, duplicate switch { "id" => B with { Id = A.Id }, "serial" => B with { Serial = A.Serial }, _ => B with { Path = A.Path } }];
        await Assert.ThrowsAsync<IOException>(() => f.Provider.PollAsync(default));
        Assert.Null(driver.Observe()); Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 65), default)).Outcome);
        await Assert.ThrowsAsync<IOException>(() => driver.PollAsync(default));
        Assert.Single(f.Adapters[A.Id].Opened); Assert.Equal(0, f.Adapters[A.Id].Inner.Writes); Assert.Single(f.Adapters);
    }

    [Fact] public async Task RemovalAndReplugDuringAReadRejectsItsLateResultEvenAtTheSamePath()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); Configure(driver);
        var old = driver.Observe()!.Locator; var entered = new TaskCompletionSource(); var finish = new TaskCompletionSource();
        f.Adapters[A.Id].DuringRead = async () => { entered.SetResult(); await finish.Task; };
        var read = driver.RefreshAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            f.Discovery.Found = []; await f.Provider.PollAsync(default); Assert.Null(driver.Observe());
            f.Discovery.Found = [A]; await f.Provider.PollAsync(default);
        }
        finally { finish.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => read); Assert.Null(driver.Observe());
        await driver.PollAsync(default); Assert.NotEqual(old, driver.Observe()!.Locator);
        Assert.Equal(2, f.Adapters[A.Id].Opened.Count); Assert.Equal(0, f.Adapters[A.Id].Inner.Writes);
    }

    [Fact] public async Task ChangingUsbLocationKeepsSerialBindingButInvalidatesOldGeneration()
    {
        await using var f = new Fixture([A, B]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); Configure(driver);
        var binding = driver.BindingCandidate; var registry = new DeviceRegistry();
        var old = registry.Reconcile(f.Provider.CachedDrivers.Select(d => d.Observe()!));
        var before = old.Devices.Single(d => d.Device.Id == driver.DeviceId);
        f.Discovery.Found = [B, A with { Path = "usb:moved" }]; await f.Provider.PollAsync(default); Assert.Null(driver.Observe());
        await driver.PollAsync(default); var next = registry.Reconcile(f.Provider.CachedDrivers.Select(d => d.Observe()!));
        Assert.Equal(binding, driver.BindingCandidate); Assert.NotEqual(before.Generation, next.Devices.Single(d => d.Device.Id == driver.DeviceId).Generation);
        Assert.NotNull(registry.Check(new(old.Epoch, driver.DeviceId, "fan1", before.Generation, DeviceFeature.FixedDuty)));
        Assert.Equal("usb:moved", f.Adapters[A.Id].Opened[^1].Path); Assert.Single(f.Adapters[B.Id].Opened);
    }

    [Fact] public async Task HotplugAddsOnlyTheNewWorkerAndKeepsTheMissingDeviceForRecovery()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var first = f.Driver(A.Id); Configure(first);
        await first.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 62), default);
        f.Discovery.Found = [B]; await f.Provider.PollAsync(default); await f.Driver(B.Id).PollAsync(default);
        Assert.Same(first, f.Driver(A.Id)); Assert.Null(first.Observe()); Assert.NotNull(f.Driver(B.Id).Observe());
        Assert.Equal(Outcome.Unknown, Assert.Single(await first.ReleaseAsync(DeviceReleaseReason.DeviceChanged, default)).Result.Outcome);
        Assert.Single(new HubEndpointRecoveryJournal(f.Directory.Path, first.DeviceId).Pending); Assert.Equal(0, f.Adapters[B.Id].Inner.Writes);
        f.Discovery.Found = [A, B]; await f.Provider.PollAsync(default); await first.PollAsync(default);
        Assert.Equal(70, Assert.Single(await first.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.ReportedDuty);
        Assert.Empty(new HubEndpointRecoveryJournal(f.Directory.Path, first.DeviceId).Pending);
    }

    [Fact] public async Task LateLightingResponseDoesNotConfirmAnEarlierConnectionOrEraseItsJournal()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); Configure(driver);
        f.Adapters[A.Id].AfterLighting = async () => { f.Discovery.Found = []; await f.Provider.PollAsync(default); f.Discovery.Found = [A]; await f.Provider.PollAsync(default); };
        var result = await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: new("fixed", [new(0, 0, 255)])), default);
        Assert.Equal(Outcome.Unknown, result.Outcome); Assert.Null(driver.Observe());
        Assert.Single(new HubLightingRecoveryJournal(f.Directory.Path, driver.DeviceId).Pending);
        await driver.PollAsync(default); Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Empty(new HubLightingRecoveryJournal(f.Directory.Path, driver.DeviceId).Pending);
    }

    [Fact] public async Task LateFanResponseRetainsTheRecoveryObligationUntilTheSameHubIsReacquired()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id); Configure(driver);
        f.Adapters[A.Id].AfterFan = async () => { f.Discovery.Found = []; await f.Provider.PollAsync(default); f.Discovery.Found = [A]; await f.Provider.PollAsync(default); };
        Assert.Equal(Outcome.Unknown, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 65), default)).Outcome);
        Assert.Null(driver.Observe()); Assert.Single(new HubEndpointRecoveryJournal(f.Directory.Path, driver.DeviceId).Pending);
        await driver.PollAsync(default);
        Assert.Equal(70, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.ReportedDuty);
        Assert.Empty(new HubEndpointRecoveryJournal(f.Directory.Path, driver.DeviceId).Pending);
    }

    [Fact] public async Task EnumerationFailureBlanksTheCatalogUntilAReadFromANewConnectionSucceeds()
    {
        await using var f = new Fixture([A]); await f.DiscoverAndOpen(); var driver = f.Driver(A.Id);
        f.Discovery.Failure = new IOException("probe failed"); await Assert.ThrowsAsync<IOException>(() => f.Provider.PollAsync(default)); Assert.Null(driver.Observe());
        f.Discovery.Failure = null; await f.Provider.PollAsync(default); Assert.Null(driver.Observe());
        await driver.PollAsync(default); Assert.NotNull(driver.Observe()); Assert.Equal(0, f.Adapters[A.Id].Inner.Writes);
    }

    [Fact] public async Task ReusingAnAdapterIsRejectedAndEveryOwnedAdapterIsDisposedExactlyOnce()
    {
        using var directory = new ServiceTestDirectory(); var discovery = new Discovery { Found = [A, B] }; var adapter = new Adapter(discovery, A);
        var provider = new HubDeviceProvider(Installation, directory.Path, discovery, _ => adapter);
        await Assert.ThrowsAsync<IOException>(() => provider.PollAsync(default)); Assert.Empty(adapter.Opened);
        await provider.DisposeAsync(); await provider.DisposeAsync(); Assert.Equal(1, adapter.Disposes); Assert.Equal(1, discovery.Disposes);
    }

    [Fact] public async Task SavedMissingHubStillLoadsItsRecoveryObligationBeforeStartupAcceptsControl()
    {
        await using var f = new Fixture([], [DemoHub.Identity.Id]);
        var config = await DeviceRuntimeStartupTests.Package(new DemoHub { DelayMs = 0 }, f.Directory.Path);
        var store = DeviceConfigurationCodec.Store(Path.Combine(f.Directory.Path, "configuration")); store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var binding = config.Bindings.Devices[0];
        new HubEndpointRecoveryJournal(f.Directory.Path, binding.DeviceId).Prepare(DemoHub.Identity.Id, binding.Fingerprint,
            ControlCoordinator.WiringSignature(await new DemoHub().ReadAsync()), "fan1", 80, 45, 60);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, () => new([], [f.Provider]), ["user"], new TestLog(), demo: true);
        Assert.NotNull(startup.Runtime.State().LifecycleError); Assert.Single(new HubEndpointRecoveryJournal(f.Directory.Path, binding.DeviceId).Pending);
        Assert.Empty(f.Adapters[DemoHub.Identity.Id].Opened); Assert.Equal(0, f.Adapters[DemoHub.Identity.Id].Inner.Writes);
    }

    [Fact] public async Task HubAbsenceAndProbeFailureDoNotStopOtherSensorsAndNewHubsRemainMonitoringOnly()
    {
        await using var f = new Fixture([]);
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), demo: true,
            providers: [f.Provider, new HardwareSensorProvider(Installation, new Sensors())]);
        await runtime.PollAsync(); await runtime.RecoverStartupAsync();
        Assert.Equal(51, Assert.Single(runtime.State().Telemetry!).Value); Assert.Null(runtime.State().LifecycleError);
        f.Discovery.Failure = new IOException("hub discovery failed"); await runtime.PollAsync();
        var cpu = Assert.Single(runtime.State().Telemetry!); Assert.Equal(51, cpu.Value); Assert.False(cpu.Stale);
        f.Discovery.Failure = null;
        f.Discovery.Found = [B, A]; await runtime.PollAsync(); await runtime.PollAsync(); var state = runtime.State();
        Assert.Equal(3, state.Inventory.Devices.Length); Assert.Equal(21, state.Telemetry!.Length); Assert.All(state.Telemetry, t => Assert.False(t.Stale));
        Assert.All(state.Inventory.Devices.SelectMany(d => d.Device.Endpoints).SelectMany(e => e.Capabilities).Where(c => c.Feature == DeviceFeature.FixedDuty),
            c => Assert.Equal(DeviceAvailability.NeedsSetup, c.Availability));
        Assert.All(f.Adapters.Values, a => { Assert.Equal(0, a.Inner.Writes); Assert.Equal(0, a.Inner.LightingWrites); });
    }
}
