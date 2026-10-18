using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class NativeLightingCurrentGenerationTests
{
    private const string Installation = "af34df7628514347b61bebae979da711";

    // Exercise the runtime's opt-in batch branch using the same real ENE
    // preparation, byte-transfer guard and durable recovery implementation.
    private sealed class Batch(EneMemoryDeviceDriver driver) : IDeviceLightingBatchDriver,
        IDeviceCurrentConnectionControlDriver, IDevicePollingDriver, IDeviceSetupDriver,
        IDeviceOperationPreparation, IDevicePhasedOperation
    {
        public int Calls;
        public string DeviceId => driver.DeviceId;
        public PhysicalDeviceBinding? BindingCandidate => driver.BindingCandidate;
        public PhysicalDeviceBinding? CurrentControlBinding => driver.CurrentControlBinding;
        public DeviceObservation? Observe() => driver.Observe();
        public EndpointSetupOptions[] DescribeSetup() => driver.DescribeSetup();
        public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature) => driver.AllowsCurrentConnectionControl(endpointId, feature);
        public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
            IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
            IEnumerable<RegisteredEndpoint> endpoints) => driver.ConfigureCurrentConnection(currentBinding, savedBinding, cooling, lighting, endpoints);
        public string? Validate(DeviceEndpointAction action) => driver.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => driver.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => driver.ReleaseAsync(reason, ct);
        public Task PollAsync(CancellationToken ct) => driver.PollAsync(ct);
        public Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct) => driver.PrepareOperationAsync(kind, operation, ct);
        public Task<T> RunPhasedOperationAsync<T>(DevicePreparationKind kind, IDeviceHardwareExecution hardware,
            Func<CancellationToken, Task<T>> operation, CancellationToken ct) => driver.RunPhasedOperationAsync(kind, hardware, operation, ct);
        public string? ValidateLightingBatch(EndpointLightingProgram[] programs) =>
            programs.Length == 1 && DeviceLightingBatch.Valid(programs)
                ? driver.Validate(new(programs[0].EndpointId, DeviceOperation.SetLighting, Lighting: programs[0].Program)) : "invalid batch";
        public async Task<DeviceDriverReleaseResult[]> ApplyLightingBatchAsync(EndpointLightingProgram[] programs, CancellationToken ct)
        {
            Calls++;
            return [new(programs[0].EndpointId, await driver.ApplyAsync(new(programs[0].EndpointId,
                DeviceOperation.SetLighting, Lighting: programs[0].Program), ct))];
        }
        public ValueTask DisposeAsync() => driver.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConfigurationInvalidatedAtActualDataBoundaryCannotContinueNativeOrBatchOutput(bool batch, bool afterFirstWrite)
    {
        using var directory = new ServiceTestDirectory(); var rack = new Rack(0x71); var clock = new TestClock();
        await using var backend = new EneMemoryDeviceBackend([rack.Transport()], clock);
        var connection = Assert.Single(await backend.DiscoverAsync(default));
        var id = DeviceIdentity.Create(Installation, "ene-memory", connection.NativeIdentity);
        var driver = new EneMemoryDeviceDriver(id, connection, directory.Path, "test-boot", backend);
        var batched = new Batch(driver);
        await using var runtime = new DeviceControlRuntime([batch ? batched : driver], ["user"], new TestLog(), clock,
            demo: true, physicalBindings: []);
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(Installation))));
        var authority = new DeviceConfigurationAuthority(store, Installation, store.ReadActive()!);
        runtime.InstallConfigurationHandler(authority);
        await runtime.PollAsync(); await runtime.PollAsync();
        var hello = runtime.OpenSession("user"); long sequence = 0;
        DeviceServiceRequest Request(DeviceOperation operation)
        {
            var state = runtime.State(); var target = Assert.Single(state.Endpoints);
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                state.Inventory.TopologyRevision, [new(target.Target, target.Generation, target.PolicyRevision)],
                Lighting: operation == DeviceOperation.SetLighting ? new("fixed", [new(20, 40, 60)]) : null);
        }
        Assert.True(driver.AllowsCurrentConnectionControl("zone:0", DeviceFeature.FixedColor));
        var original = rack.Registers.ToDictionary(); var binding = driver.BindingCandidate;
        EneMemoryRecoveryRecord? Pending() => new EneMemoryRecovery(directory.Path, id).Pending;
        EneMemoryRecoveryRecord? prepared = null; var reached = false;
        rack.BeforeData = (register, write) => {
            if (!write || rack.Writes.Count != (afterFirstWrite ? 1 : 0)) return;
            rack.BeforeData = null; reached = true;
            Assert.Equal((ushort)(afterFirstWrite ? 0x8161 : 0x8160), register);
            prepared = Assert.IsType<EneMemoryRecoveryRecord>(Pending());
            var current = authority.Configuration;
            store.Activate(store.Stage(DeviceConfigurationCodec.Write(current with { Preferences = current.Preferences with {
                Notifications = current.Preferences.Notifications with { Desktop = !current.Preferences.Notifications.Desktop }
            } })));
            Assert.Throws<IOException>(() => authority.ExecuteAsync(new(DeviceConfigurationOperation.PrepareConnectionLighting),
                Guid.NewGuid().ToString("N"), hello.Session, runtime, default).GetAwaiter().GetResult());
            Assert.NotNull(authority.CurrentControlError);
        };
        try
        {
            var reply = await runtime.ExecuteAsync(Request(DeviceOperation.SetLighting)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reached); Assert.Equal(batch ? 1 : 0, batched.Calls);
            Assert.Equal(afterFirstWrite ? Outcome.Unknown : Outcome.Rejected, reply.Result!.Outcome);
            Assert.Equal(afterFirstWrite ? 1 : 0, rack.Writes.Count);
            Assert.Equal(binding, driver.BindingCandidate); backend.Check(connection);
            if (afterFirstWrite)
            {
                var pending = Assert.IsType<EneMemoryRecoveryRecord>(Pending());
                Assert.Equal(prepared!.Hash, pending.Hash); Assert.Equal(prepared.Scope, pending.Scope);
                Assert.True(EneMemoryProtocol.Same(prepared.Original, pending.Original));
                Assert.True(EneMemoryProtocol.Same(prepared.Exit, pending.Exit));
                Assert.True(Assert.Single(runtime.State().Endpoints).Owned);
                // New output stays revoked, but the same owner can explicitly
                // finish the original recovery obligation despite the save fault.
                var stopped = await runtime.ExecuteAsync(Request(DeviceOperation.StopLighting));
                Assert.Equal(Outcome.Confirmed, stopped.Result!.Outcome); Assert.Null(Pending());
                Assert.All(original, pair => Assert.Equal(pair.Value, rack.Registers[pair.Key]));
            }
            else
            {
                Assert.Null(Pending());
                Assert.All(original, pair => Assert.Equal(pair.Value, rack.Registers[pair.Key]));
            }
            Assert.NotNull(authority.CurrentControlError);
        }
        finally { rack.BeforeData = null; }
    }
}
