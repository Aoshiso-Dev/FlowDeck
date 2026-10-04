using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class HardwareSensorProviderTests
{
    [Fact] public async Task HardwareMetadataUsesOriginalModelsAndDoesNotChangeBindingsOrPollForDiagnostics()
    {
        var hardware = new Hardware { Name = "PRIVATE editable name", HardwareType = HardwareType.Motherboard,
            SubHardware = [new Hardware { HardwareType = HardwareType.SuperIO, Identifier = new("lpc", "nct6799d", "0"), Name = "PRIVATE chip" }] };
        var device = HardwareSensorCapture.Describe(hardware, null, "X870E Taichi White", Guid.NewGuid().ToString("N"), 0, []);
        var metadata = device.DiagnosticHardware!;
        Assert.Contains(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.Model && i.Value == "X870E Taichi White");
        Assert.Contains(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.ControlChip && i.Value == "NCT6799D");
        Assert.Contains(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.BiosVersion && i.State == DeviceDiagnosticReadState.NotObserved);
        var changed = device with { DiagnosticHardware = new(DeviceDiagnosticReadState.Failed, []) };
        Assert.Equal(HardwareSensorProvider.DeviceId(Installation, device), HardwareSensorProvider.DeviceId(Installation, changed));
        Assert.Equal(HardwareSensorDeviceDriver.BindingFor("id", device), HardwareSensorDeviceDriver.BindingFor("id", changed));
        var backend = new Backend { Devices = [device] }; await using var provider = new HardwareSensorProvider(Installation, backend);
        await provider.PollAsync(default); var reads = backend.Reads;
        var cached = Assert.IsAssignableFrom<IDeviceDiagnosticHardwareSource>(Assert.Single(provider.CachedDrivers)).ReadCachedDiagnosticHardware();
        Assert.Equal(reads, backend.Reads); Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(cached, Contract.Json));
        cached.Items[0] = new(DeviceDiagnosticHardwareKind.Model, DeviceDiagnosticReadState.Invalid);
        Assert.Equal(DeviceDiagnosticReadState.Available, ((IDeviceDiagnosticHardwareSource)provider.CachedDrivers[0]).ReadCachedDiagnosticHardware().Items[0].State);
    }
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Backend : IHardwareSensorBackend
    {
        public HardwareSensorDevice[] Devices = [];
        public bool Fail;
        public int Reads, Disposals;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Continue;
        public async Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct)
        { Interlocked.Increment(ref Reads); Entered.TrySetResult(); if (Continue is not null) await Continue.Task; if (Fail) throw new IOException("test read failure"); return Devices; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private static HardwareSensorDevice Cpu(TestClock clock) => new("/cpu/0", "CPU", DeviceKind.Processor, new('a', 64), null,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, [new("/cpu/0/temperature/0", "Package", "/cpu/0", DeviceFeature.Temperature, 60, clock.GetTimestamp())]);
    private static HardwareSensorDevice Gpu(TestClock clock) => new("/gpu/0", "GPU", DeviceKind.Graphics, new('b', 64), null,
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null, [new("/gpu/0/temperature/0", "Core", "/gpu/0", DeviceFeature.Temperature, 50, clock.GetTimestamp()),
        new("/gpu/0/fan/0", "Fan", "/gpu/0", DeviceFeature.Rpm, 0, clock.GetTimestamp())]);
    private static HardwareSensorDevice Board(TestClock clock, bool supported = true) => new("/motherboard", "Board", DeviceKind.Motherboard,
        new('c', 64), "board-physical-serial", "cccccccccccccccccccccccccccccccc", supported ? "asrock-x870e-taichi-white-v1" : null,
        [new(MotherboardFans.Cpu.SensorId, "Fan", MotherboardFans.Cpu.HardwareId, DeviceFeature.Rpm, 900, clock.GetTimestamp()),
         new(MotherboardFans.Cpu.HardwareId + "/control/" + MotherboardFans.Cpu.Channel, "Control", MotherboardFans.Cpu.HardwareId, DeviceFeature.DutyRead, 45, clock.GetTimestamp())]);
    private static DeviceControlRuntime Runtime(Backend backend, TestClock clock, out HardwareSensorProvider provider)
    { provider = new(Installation, backend); return new([], ["user"], new TestLog(), clock, providers: [provider]); }
    [Fact] public async Task OneReadDiscoversSeparatePhysicalDevicesAndReadOnlyQueriesDoNotPollHardware()
    {
        var clock = new TestClock(); var backend = new Backend { Devices = [Cpu(clock), Gpu(clock), Board(clock)] };
        await using var runtime = Runtime(backend, clock, out var provider); Assert.Empty(runtime.State().Inventory.Devices);
        await runtime.PollAsync(); var state = runtime.State(); runtime.State();
        Assert.Equal(1, backend.Reads); Assert.Equal(3, state.Inventory.Devices.Length); Assert.Equal(5, state.Telemetry!.Length);
        Assert.All(state.Telemetry, t => Assert.False(t.Stale));
        Assert.Contains(state.Telemetry, t => t.Feature == DeviceFeature.Rpm && t.Value == 0 && !t.Stale);
        Assert.All(provider.CachedDrivers, d => Assert.DoesNotContain(d.Observe()!.Endpoints.SelectMany(e => e.Capabilities),
            c => c.Feature is DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty));
        var board = state.Inventory.Devices.Single(d => d.Device.Kind == DeviceKind.Motherboard);
        Assert.Equal(MotherboardFans.Cpu.Id, Assert.Single(board.Device.Endpoints).Id);
    }
    [Fact] public async Task UnknownBoardsRetainTheirReadingsWithoutInventingAHeaderMappingOrWriteCapability()
    {
        var clock = new TestClock(); var backend = new Backend { Devices = [Board(clock, supported: false)] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync();
        var board = Assert.Single(runtime.State().Inventory.Devices).Device;
        Assert.Null(board.DefinitionId); Assert.Equal(2, board.Endpoints.Length); Assert.All(board.Endpoints, e => Assert.Equal(EndpointKind.Sensor, e.Kind));
        Assert.DoesNotContain(board.Endpoints, e => e.Id == MotherboardFans.Cpu.Id);
    }
    [Fact] public async Task RepeatedPollRequestsCoalesceAndDoNotBlockSessionExpiry()
    {
        var clock = new TestClock(); var backend = new Backend { Devices = [Cpu(clock)], Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var runtime = Runtime(backend, clock, out _); runtime.OpenSession("user"); var first = runtime.PollAsync();
        try
        {
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var second = runtime.PollAsync(); Assert.Equal(1, backend.Reads);
            clock.Advance(13); await runtime.SweepAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(first.IsCompleted); backend.Continue.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, backend.Reads);
        }
        finally { backend.Continue.TrySetResult(); }
    }
    [Fact] public async Task AFailedReadInvalidatesOldDataWithoutAdvancingItsTimestamp()
    {
        var clock = new TestClock(); var backend = new Backend { Devices = [Cpu(clock)] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync(); var before = Assert.Single(runtime.State().Telemetry!);
        clock.Advance(1); backend.Fail = true; await runtime.PollAsync(); var failed = Assert.Single(runtime.State().Telemetry!);
        Assert.Null(failed.Value); Assert.True(failed.Stale); Assert.Equal(1, failed.AgeSeconds); Assert.NotNull(failed.Problem);
        backend.Fail = false; backend.Devices = [Cpu(clock)]; await runtime.PollAsync(); Assert.False(Assert.Single(runtime.State().Telemetry!).Stale);
        clock.Advance(3); Assert.True(Assert.Single(runtime.State().Telemetry!).Stale);
    }
    [Fact] public async Task AFailedSensorDoesNotHideAnotherDevicesValidMeasurement()
    {
        var clock = new TestClock(); var cpu = Cpu(clock); var backend = new Backend
        { Devices = [cpu with { Sensors = [cpu.Sensors[0] with { Value = null, Problem = "CPU access unavailable" }] }, Gpu(clock)] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync();
        Assert.Single(runtime.State().Telemetry!, t => t.Stale); Assert.Equal(2, runtime.State().Telemetry!.Count(t => !t.Stale));
    }
    [Fact] public async Task RenameDoesNotChangeGenerationButPhysicalFingerprintAndReconnectionDo()
    {
        var clock = new TestClock(); var node = Board(clock); var backend = new Backend { Devices = [node] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync(); var before = Assert.Single(runtime.State().Inventory.Devices);
        backend.Devices = [node with { Name = "Renamed", Sensors = node.Sensors.Select(s => s with { Name = "Reading" }).ToArray() }];
        await runtime.PollAsync(); Assert.Equal(before.Generation, Assert.Single(runtime.State().Inventory.Devices).Generation);
        backend.Devices = [node with { Fingerprint = new('d', 64) }]; await runtime.PollAsync();
        var changed = Assert.Single(runtime.State().Inventory.Devices); Assert.Equal(before.Device.Id, changed.Device.Id); Assert.True(changed.Generation > before.Generation);
        backend.Devices = []; await runtime.PollAsync(); Assert.False(Assert.Single(runtime.State().Inventory.Devices).Connected);
        backend.Devices = [node with { ConnectionId = Guid.NewGuid().ToString("N") }]; await runtime.PollAsync();
        Assert.True(Assert.Single(runtime.State().Inventory.Devices).Generation > changed.Generation);
    }
    [Fact] public async Task AWeakIdentityIsNeverReusedForADifferentConnectionAtTheSameEnumerationIndex()
    {
        var clock = new TestClock(); var node = Gpu(clock); var backend = new Backend { Devices = [node] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync(); var before = Assert.Single(runtime.State().Inventory.Devices);
        backend.Devices = [node with { ConnectionId = Guid.NewGuid().ToString("N") }]; await runtime.PollAsync();
        var devices = runtime.State().Inventory.Devices; Assert.Equal(2, devices.Length); Assert.False(devices.Single(d => d.Device.Id == before.Device.Id).Connected);
        Assert.NotEqual(before.Device.Id, devices.Single(d => d.Connected).Device.Id);
        Assert.All(runtime.State().Telemetry!.Where(t => t.Target.DeviceId == before.Device.Id), t => Assert.True(t.Stale));
    }
    [Fact] public async Task DuplicateSerialsAndInvalidSamplesDoNotPartiallyRegisterOrPublishValues()
    {
        var clock = new TestClock(); var node = Board(clock); var backend = new Backend { Devices = [node] };
        await using var provider = new HardwareSensorProvider(Installation, backend); await provider.PollAsync(default);
        backend.Devices = [node, node with { NativeId = "/second-board", ConnectionId = Guid.NewGuid().ToString("N") }];
        await Assert.ThrowsAsync<IOException>(() => provider.PollAsync(default)); Assert.Single(provider.CachedDrivers);
        Assert.All(((IDeviceTelemetrySource)provider.CachedDrivers[0]).ReadCachedSamples(), s => Assert.Null(s.Value));
        backend.Devices = [node with { Sensors = [node.Sensors[0] with { Value = double.NaN }] }];
        await Assert.ThrowsAsync<IOException>(() => provider.PollAsync(default));
    }
    [Fact] public async Task ProviderOwnsTheSharedBackendAndDisposesItExactlyOnce()
    {
        var clock = new TestClock(); var backend = new Backend { Devices = [Cpu(clock), Gpu(clock)] };
        var runtime = Runtime(backend, clock, out var provider); await runtime.PollAsync();
        foreach (var driver in provider.CachedDrivers) await driver.DisposeAsync(); Assert.Equal(0, backend.Disposals);
        await runtime.DisposeAsync(); await runtime.DisposeAsync(); await provider.DisposeAsync(); Assert.Equal(1, backend.Disposals);
    }
    [Fact] public async Task ASerialBoundDeviceRetainsEndpointIdsAcrossNativeEnumerationChanges()
    {
        var clock = new TestClock(); var gpu = Gpu(clock) with { SerialIdentity = "verified-gpu-serial" }; var backend = new Backend { Devices = [gpu] };
        await using var runtime = Runtime(backend, clock, out _); await runtime.PollAsync(); var before = Assert.Single(runtime.State().Inventory.Devices);
        backend.Devices = [gpu with { NativeId = "/gpu/9", Sensors = gpu.Sensors.Select(s => s with
            { Id = s.Id.Replace("/gpu/0/", "/gpu/9/", StringComparison.Ordinal), HardwareId = "/gpu/9" }).ToArray() }];
        await runtime.PollAsync(); var after = Assert.Single(runtime.State().Inventory.Devices);
        Assert.Equal(before.Device.Id, after.Device.Id); Assert.Equal(before.Device.Endpoints.Select(e => e.Id), after.Device.Endpoints.Select(e => e.Id));
        Assert.True(after.Generation > before.Generation);
    }
    private sealed class Provider(IDeviceControlDriver[] drivers) : IDeviceDriverProvider
    {
        public string ProviderId => "test-provider";
        public string[] ResourceDomains => ["test-resource"];
        public IDeviceControlDriver[] CachedDrivers => drivers;
        public Task PollAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact] public async Task DiscoveryCannotReplaceAnExistingDriverObjectOrPartiallyRegisterADuplicateBatch()
    {
        var original = new GenericTestDriver("owned"); var impostor = new GenericTestDriver("owned") { Locator = "different" };
        var log = new TestLog(); await using var runtime = new DeviceControlRuntime([original], ["user"], log,
            providers: [new Provider([new GenericTestDriver("new"), impostor])]);
        var device = Assert.Single(runtime.State().Inventory.Devices); Assert.Equal("port1", device.Device.Locator);
        await runtime.PollAsync(); Assert.Single(runtime.State().Inventory.Devices); Assert.NotEmpty(log.Entries);
    }
    [Theory] [InlineData("To Be Filled By O.E.M.")] [InlineData("Default string")] [InlineData("00000000")] [InlineData("FFFFFFFF")]
    [InlineData("123456789")] [InlineData(null)]
    public void FirmwarePlaceholdersAreNotPhysicalSerials(string? serial) => Assert.False(HardwareSensorCapture.UsefulSerial(serial));
    [Fact] public void PciConnectionProofIgnoresCaseButRejectsEnumerationIndicesAndIncompleteHardwareIds()
    {
        const string pnp = @"PCI\VEN_10DE&DEV_2B85&SUBSYS_12341043&REV_A1\4&223344&0&0008";
        Assert.NotNull(HardwareSensorCapture.PciLocation(pnp));
        Assert.Equal(HardwareSensorCapture.PciLocation(pnp), HardwareSensorCapture.PciLocation(pnp.ToLowerInvariant()));
        Assert.Null(HardwareSensorCapture.PciLocation("/gpu-nvidia/0")); Assert.Null(HardwareSensorCapture.PciLocation(@"PCI\VEN_10DE&DEV_2B85"));
        Assert.Null(HardwareSensorCapture.PciLocation(@"PCI\VEN_10DE&DEV_2B85\..\1"));
    }
    [Fact] public async Task ConfirmedLocationKeepsRegistrationAcrossConnectionsWithoutClaimingSerialIdentity()
    {
        var clock = new TestClock(); var node = Gpu(clock) with { LocationIdentity = new('e', 64) }; var backend = new Backend { Devices = [node] };
        await using var runtime = Runtime(backend, clock, out var provider); await runtime.PollAsync(); var before = Assert.Single(runtime.State().Inventory.Devices);
        backend.Devices = [node with { ConnectionId = Guid.NewGuid().ToString("N") }]; await runtime.PollAsync();
        var after = Assert.Single(runtime.State().Inventory.Devices); Assert.Equal(before.Device.Id, after.Device.Id); Assert.True(after.Generation > before.Generation);
        Assert.Equal(IdentityConfidence.ConnectionBound, after.Device.IdentityConfidence);
        Assert.StartsWith("location:", Assert.IsType<HardwareSensorDeviceDriver>(provider.CachedDrivers[0]).BindingCandidate!.ProviderIdentity);
        backend.Devices = [node with { LocationIdentity = new('f', 64) }]; await runtime.PollAsync(); Assert.Equal(2, runtime.State().Inventory.Devices.Length);
    }

    private sealed class Hardware : IHardware
    {
        public HardwareType HardwareType { get; init; } = HardwareType.Cpu;
        public Identifier Identifier { get; init; } = new("test-cpu", "0");
        public string Name { get; set; } = "CPU model";
        public IHardware Parent => null!;
        public ISensor[] Sensors { get; set; } = [];
        public IHardware[] SubHardware { get; set; } = [];
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public event SensorEventHandler SensorAdded { add { } remove { } }
        public event SensorEventHandler SensorRemoved { add { } remove { } }
        public string GetReport() => "test";
        public void Update() => throw new InvalidOperationException("Capture must not poll hardware");
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
    }
    private sealed class Sensor(IHardware hardware, SensorType type, int index, float? value) : ISensor
    {
        public IControl Control => null!;
        public IHardware Hardware => hardware;
        public Identifier Identifier { get; } = new(hardware.Identifier, type.ToString().ToLowerInvariant(), index.ToString());
        public int Index => index;
        public bool IsDefaultHidden => false;
        public float? Max => value;
        public float? Min => value;
        public string Name { get; set; } = type.ToString();
        public IReadOnlyList<IParameter> Parameters => [];
        public SensorType SensorType => type;
        public float? Value => value;
        public IEnumerable<SensorValue> Values => [];
        public TimeSpan ValuesTimeWindow { get; set; }
        public void ResetMin() { }
        public void ResetMax() { }
        public void ClearValues() { }
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
    }
    [Fact] public void LhmCaptureRetainsAcquisitionTimeAndSeparatesZeroRpmFromUnavailableTemperature()
    {
        var hardware = new Hardware(); hardware.Sensors = [new Sensor(hardware, SensorType.Temperature, 0, 0),
            new Sensor(hardware, SensorType.Temperature, 1, 62), new Sensor(hardware, SensorType.Fan, 0, 0), new Sensor(hardware, SensorType.Load, 0, 99)];
        var values = new List<HardwareSensorValue>();
        HardwareSensorCapture.AddValues(hardware, values, hardware.Sensors.ToDictionary(s => s, _ => new SensorAcquisition(12345, null)));
        Assert.Equal(3, values.Count); Assert.All(values, v => Assert.Equal(12345, v.Timestamp));
        Assert.Null(values[0].Value); Assert.NotNull(values[0].Problem); Assert.Equal(62, values[1].Value); Assert.Equal(0, values[2].Value);
        values.Clear(); HardwareSensorCapture.AddValues(hardware, values,
            hardware.Sensors.ToDictionary(s => s, _ => new SensorAcquisition(12345, "update failed")));
        Assert.All(values, v => { Assert.Null(v.Value); Assert.Equal(12345, v.Timestamp); });
    }
    [Fact] public void ADisplayRenameDoesNotChangeCapturedModelProofAndAMissingSerialStaysConnectionBound()
    {
        var hardware = new Hardware(); var connection = Guid.NewGuid().ToString("N");
        var before = HardwareSensorCapture.Describe(hardware, null, "CPU model", connection, 1, []);
        hardware.Name = "User label"; var renamed = HardwareSensorCapture.Describe(hardware, null, "CPU model", connection, 1, []);
        Assert.Equal("CPU model", Assert.Single(renamed.DiagnosticHardware!.Items).Value);
        Assert.Equal(before.Fingerprint, renamed.Fingerprint); Assert.Null(renamed.SerialIdentity); Assert.Empty(renamed.Sensors);
        Assert.NotEqual(before.Fingerprint, HardwareSensorCapture.Describe(hardware, null, "CPU model", Guid.NewGuid().ToString("N"), 1, []).Fingerprint);
        var replacement = HardwareSensorCapture.Describe(hardware, null, "Other CPU", connection, 1, []);
        Assert.NotEqual(before.Fingerprint, replacement.Fingerprint);
    }
}
