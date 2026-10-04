using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceConfigurationAuthorityTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class ConfigurableHub(HubDeviceDriver inner) : IConfiguredCoolingDriver, IDevicePollingDriver, IDeviceTelemetrySource, IDeviceCoolingCalibrationDriver
    {
        public string DeviceId => inner.DeviceId;
        public PhysicalDeviceBinding? BindingCandidate => inner.BindingCandidate;
        public Action<CoolingEndpointConfiguration[]>? AfterConfigure;
        public Func<CoolingActuation, Task>? AfterActuation;
        public Func<CoolingActuation, DeviceDriverResult, DeviceDriverResult>? MapActuation;
        public void Configure(PhysicalDeviceBinding? binding, IEnumerable<CoolingEndpointConfiguration> configurations)
        { var values = configurations.ToArray(); inner.Configure(binding, values); AfterConfigure?.Invoke(values); }
        public DeviceObservation? Observe() => inner.Observe();
        public DeviceSample[] ReadCachedSamples() => inner.ReadCachedSamples();
        public Task PollAsync(CancellationToken ct) => inner.PollAsync(ct);
        public CoolingEndpointConfiguration? CoolingConfiguration(string id) => inner.CoolingConfiguration(id);
        public string? ValidateCoolingSetup(string id) => inner.ValidateCoolingSetup(id);
        public string? ValidateCalibrationStart(string id,PhysicalDeviceBinding expected)=>inner.ValidateCalibrationStart(id,expected);
        public Task<CoolingCalibrationStart> BeginCalibrationAsync(CoolingCalibrationActuation actuation,CancellationToken ct)=>inner.BeginCalibrationAsync(actuation,ct);
        public Task<CoolingCalibrationReading> ReadCalibrationAsync(string id,PhysicalDeviceBinding expected,CancellationToken ct)=>inner.ReadCalibrationAsync(id,expected,ct);
        public Task<DeviceDriverResult> EndCalibrationAsync(string id,CancellationToken ct)=>inner.EndCalibrationAsync(id,ct);
        public async Task<DeviceDriverResult> ApplyCoolingAsync(CoolingActuation actuation, CancellationToken ct)
        {
            var result = await inner.ApplyCoolingAsync(actuation, ct);
            if (AfterActuation is not null) await AfterActuation(actuation);
            return MapActuation?.Invoke(actuation, result) ?? result;
        }
        public string? Validate(DeviceEndpointAction action) => inner.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => inner.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => inner.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly DemoHub Hub = new() { DelayMs = 0 };
        public ConfigurationGenerationStore Store = null!;
        public DeviceRuntimeStartup Startup = null!;
        public ConfigurableHub Driver = null!;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public static async Task<Rig> Create(int fans = 1, IDeviceOpenRgbInspector? inspector = null, IDeviceOpenRgbRuntimeInspector? runtimeInspector = null)
        {
            var r = new Rig(); var config = await DeviceRuntimeStartupTests.Package(r.Hub, r.Directory.Path);
            if (fans > 1) config = config with { Cooling = config.Cooling with { Endpoints = Enumerable.Range(1, fans)
                .Select(n => config.Cooling.Endpoints[0] with { Target = config.Cooling.Endpoints[0].Target with { EndpointId = "fan" + n } }).ToArray() } };
            r.Store = DeviceConfigurationCodec.Store(Path.Combine(r.Directory.Path, "configuration"));
            r.Store.Activate(r.Store.Stage(DeviceConfigurationCodec.Write(config)));
            r.Driver = new(new(Installation, DemoHub.Identity.Id, r.Hub, r.Directory.Path));
            r.Startup = await DeviceRuntimeStartup.CreateAsync(r.Store, Installation, () => new([r.Driver], []), ["user"], new TestLog(), demo: true,
                openRgbInspector: inspector, openRgbRuntimeInspector: runtimeInspector);
            r.Hello = r.Startup.Runtime.OpenSession("user"); return r;
        }
        public DeviceServiceRequest Request(DeviceConfigurationRequest request) => new(2, Hello.Epoch, Hello.Session,
            Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.Configuration, Configuration: request);
        public Task<DeviceServiceReply> Send(DeviceConfigurationRequest request) => Startup.Runtime.ExecuteAsync(Request(request));
        public DeviceServiceRequest Duty(int value)
        {
            var state = Startup.Runtime.State(); var row = state.Endpoints.Single(e => e.Target.EndpointId == "fan1");
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.SetDuty,
                state.Inventory.TopologyRevision, [new(row.Target, row.Generation, row.PolicyRevision)], value);
        }
        public Task<DeviceServiceReply> Renew() => Startup.Runtime.ExecuteAsync(new(2, Hello.Epoch, Hello.Session,
            Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.Renew));
        public DeviceConfigurationPackage Changed()
        {
            var old = Startup.Configuration; var safety = old.Cooling.Endpoints[0].Safety with { Minimum = 60, Exit = 80 };
            return old with { Devices = old.Devices with { Devices = old.Devices.Devices.Select(d => d with { Name = "変更後" }).ToArray() },
                Cooling = old.Cooling with { Endpoints = [old.Cooling.Endpoints[0] with { Safety = safety, SavedPolicy = new(FanMode.Manual, 65, SafetySnapshot: safety) }] } };
        }
        public async Task<string> Upload(DeviceConfigurationPackage config, bool validate = true)
        {
            var bytes = DeviceConfigurationCodec.SerializePackage(config);
            var head = (await Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: Startup.Generation.Id,
                TotalBytes: bytes.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))))).Configuration!;
            for (var offset = 0; offset < bytes.Length;)
            {
                var part = bytes.AsSpan(offset, Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length - offset)).ToArray();
                Assert.True((await Send(new(DeviceConfigurationOperation.Append, head.TransferId, Offset: offset, Data: part))).Success); offset += part.Length;
            }
            if (validate) Assert.True((await Send(new(DeviceConfigurationOperation.Validate, head.TransferId))).Success);
            return head.TransferId!;
        }
        public DeviceConfigurationRequest Apply(string transfer) => new(DeviceConfigurationOperation.Apply, transfer,
            TopologyRevision: Startup.Runtime.State().Inventory.TopologyRevision);
        public ConfigurationApplication Application => ServiceEnvironment.Read<ConfigurationApplication>(Path.Combine(Store.DirectoryPath, "configuration-application.json"));
        public async ValueTask DisposeAsync() { await Startup.DisposeAsync(); Directory.Dispose(); }
    }
    [Fact]
    public async Task ApplyEndsOldControlThenAtomicallyAdoptsTheNewGenerationWithoutStartingOutputs()
    {
        await using var r = await Rig.Create(); var before = r.Startup.Generation;
        await r.Startup.Runtime.ExecuteAsync(r.Duty(90)); var obsolete = r.Duty(80);
        var id = await r.Upload(r.Changed()); var request = r.Request(r.Apply(id));
        var result = await r.Startup.Runtime.ExecuteAsync(request);
        Assert.True(result.Success, result.Error); Assert.NotEqual(before, r.Startup.Generation); Assert.Equal(r.Startup.Generation, r.Store.ReadActive()!.Pointer);
        var saved = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files);
        Assert.Equal("変更後", saved.Devices.Devices[0].Name); Assert.Equal(60, saved.Cooling.Endpoints[0].Safety.Minimum);
        Assert.Equal(50, (await r.Hub.ReadAsync()).Channels[0].Duty); Assert.Equal(2, r.Hub.Writes);
        Assert.All(r.Startup.Runtime.State().Endpoints, e => Assert.False(e.Owned)); Assert.Equal("applied", r.Application.Phase);
        await r.Startup.Runtime.ExecuteAsync(request); Assert.Equal(2, r.Hub.Writes);
        Assert.False((await r.Startup.Runtime.ExecuteAsync(obsolete)).Success);
        var clamped = await r.Startup.Runtime.ExecuteAsync(r.Duty(35)); Assert.Equal(60, clamped.Result!.Targets[0].ReportedDuty);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADriverOrAtomicSwitchFailureRestoresTheOldGenerationAndAllowsAnExplicitRetry(bool failSwitch)
    {
        await using var r = await Rig.Create(); var before = r.Startup.Generation;
        var id = await r.Upload(r.Changed()); var failed = false;
        if (failSwitch) r.Store.Checkpoint = phase => { if (phase == "before-switch" && !failed) { failed = true; throw new IOException("switch failed"); } };
        else r.Driver.AfterConfigure = values => { if (values.FirstOrDefault()?.Safety.Minimum == 60 && !failed) { failed = true; throw new IOException("driver configured partially"); } };
        var reply = await r.Send(r.Apply(id)); Assert.False(reply.Success); Assert.True(failed);
        Assert.Equal(Outcome.Failed, reply.Result!.Outcome); Assert.Equal("rolled-back", r.Application.Phase);
        Assert.Equal(before, r.Store.ReadActive()!.Pointer); Assert.Equal(before, r.Startup.Generation);
        Assert.Equal(30, r.Driver.CoolingConfiguration("fan1")!.Safety.Minimum); Assert.NotNull(r.Startup.Runtime.State().LifecycleError);
        Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Duty(80))).Success);
        Assert.True((await r.Send(r.Apply(id))).Success); Assert.Null(r.Startup.Runtime.State().LifecycleError);
        Assert.Equal(60, r.Driver.CoolingConfiguration("fan1")!.Safety.Minimum);
    }
    [Fact]
    public async Task RenewAndStateRemainResponsiveWhileNewWritesAreBlockedDuringApply()
    {
        await using var r = await Rig.Create(); var id = await r.Upload(r.Changed());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var proceed = new ManualResetEventSlim();
        r.Driver.AfterConfigure = values => { if (values.FirstOrDefault()?.Safety.Minimum == 60) { entered.TrySetResult(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var apply = r.Send(r.Apply(id));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(r.Startup.Runtime.State().ConfigurationChanging);
            Assert.True((await r.Renew().WaitAsync(TimeSpan.FromSeconds(1))).Success);
            Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Duty(80))).Success);
        }
        finally { proceed.Set(); }
        Assert.True((await apply).Success); Assert.False(r.Startup.Runtime.State().ConfigurationChanging);
    }
    [Fact]
    public async Task DisconnectDuringDriverConfigurationRollsBackAndReleasesWithoutDeadlock()
    {
        await using var r = await Rig.Create(); var before = r.Startup.Generation; var id = await r.Upload(r.Changed());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var proceed = new ManualResetEventSlim();
        r.Driver.AfterConfigure = values => { if (values.FirstOrDefault()?.Safety.Minimum == 60) { entered.TrySetResult(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var apply = r.Send(r.Apply(id)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disconnect = r.Startup.Runtime.DisconnectAsync(r.Hello.Session); proceed.Set();
        Assert.False((await apply.WaitAsync(TimeSpan.FromSeconds(3))).Success); await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(before, r.Store.ReadActive()!.Pointer); Assert.Equal(0, r.Hub.Writes);
    }
    [Fact]
    public async Task ForgedNewPhysicalBindingCannotBeValidatedAndNoSettingsAreChanged()
    {
        await using var r = await Rig.Create(); var config = r.Changed(); var before = r.Startup.Generation;
        var forged = config with { Bindings = config.Bindings with { Devices = [config.Bindings.Devices[0] with { Fingerprint = new('b', 64) }] } };
        var id = await r.Upload(forged, validate: false);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.Validate, id))).Success);
        Assert.False((await r.Send(r.Apply(id))).Success); Assert.Equal(before, r.Store.ReadActive()!.Pointer); Assert.Equal(0, r.Hub.Writes);
    }
    [Fact]
    public async Task ChunkOrderDigestPreviewAndSessionOwnershipAreRequired()
    {
        await using var r = await Rig.Create(); var bytes = DeviceConfigurationCodec.SerializePackage(r.Changed());
        var head = (await r.Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: r.Startup.Generation.Id,
            TotalBytes: bytes.Length, Sha256: new('0', 64)))).Configuration!;
        Assert.False((await r.Send(new(DeviceConfigurationOperation.Append, head.TransferId, Offset: 1, Data: [1]))).Success);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.Validate, head.TransferId))).Success);
        Assert.False((await r.Send(r.Apply(head.TransferId!))).Success);
        Assert.True((await r.Send(new(DeviceConfigurationOperation.Append, head.TransferId, Offset: 0, Data: bytes))).Success);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.Validate, head.TransferId))).Success);
        await r.Startup.Runtime.DisconnectAsync(r.Hello.Session); r.Hello = r.Startup.Runtime.OpenSession("user");
        Assert.False((await r.Send(new(DeviceConfigurationOperation.Validate, head.TransferId))).Success);
    }
    [Fact]
    public async Task UiSettingsStayLocalAndTheServiceSnapshotCannotBeMutatedByClients()
    {
        await using var r = await Rig.Create(); var value = r.Changed(); var originalTheme = value.Preferences.Ui.Theme;
        var originalMigration = SettingsTransfer.Canonical(value.Migration);
        value = value with { Preferences = value.Preferences with { Ui = value.Preferences.Ui with { Theme = "dark" } },
            Migration = value.Migration with { Notices = ["client cannot replace the migration audit"] } };
        var id = await r.Upload(value); Assert.True((await r.Send(r.Apply(id))).Success);
        Assert.Equal(originalTheme, r.Startup.Configuration.Preferences.Ui.Theme);
        Assert.Equal(originalMigration, SettingsTransfer.Canonical(r.Startup.Configuration.Migration));
        var head = (await r.Send(new(DeviceConfigurationOperation.Snapshot))).Configuration!;
        Assert.Equal(r.Driver.BindingCandidate, Assert.Single(head.Candidates!));
        var reply = (await r.Send(new(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: 0))).Configuration!;
        reply.Data![0] = 0;
        var again = (await r.Send(new(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: 0))).Configuration!;
        Assert.Equal((byte)'{', again.Data![0]);
    }
    [Theory]
    [InlineData(false, "interrupted")]
    [InlineData(true, "applied")]
    public async Task StartupResolvesAnInterruptedApplicationFromTheDurableGenerationPointer(bool committed, string phase)
    {
        await using var r = await Rig.Create(); var before = r.Store.ReadActive()!.Pointer;
        var staged = r.Store.Stage(DeviceConfigurationCodec.Write(r.Changed())); if (committed) r.Store.Activate(staged);
        var record = new ConfigurationApplication(1, Guid.NewGuid().ToString("N"), before, staged.Target, "accepted");
        ServiceEnvironment.Write(Path.Combine(r.Store.DirectoryPath, "configuration-application.json"), record);
        _ = new DeviceConfigurationAuthority(r.Store, Installation, r.Store.ReadActive()!);
        Assert.Equal(phase, r.Application.Phase);
    }
    [Fact]
    public void TransferFieldsAreRejectedOnUnrelatedOperationsAndEachChunkRemainsBelowTheIpcFrameLimit()
    {
        var request = new DeviceServiceRequest(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 1, "test",
            DeviceOperation.Configuration, Configuration: new(DeviceConfigurationOperation.Append, Guid.NewGuid().ToString("N"), Offset: 0, Data: new byte[DeviceConfigurationProtocol.ChunkBytes]));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json); Assert.True(bytes.Length < DeviceServiceProtocol.MaximumFrameBytes);
        Assert.NotNull(DeviceServiceProtocol.Read(bytes).Configuration);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request with { Operation = DeviceOperation.State }, Contract.Json)));
        Assert.False(DeviceConfigurationProtocol.Valid(request.Configuration! with { Data = new byte[DeviceConfigurationProtocol.ChunkBytes + 1] }));
    }
}
