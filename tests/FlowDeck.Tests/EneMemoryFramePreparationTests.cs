using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneMemoryFramePreparationTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed class Hooks
    {
        public Func<CancellationToken, Task>? AfterSaved;
        public IPreparedDeviceLightingStream? Stream;
        public Func<DevicePreparationKind, int, CancellationToken, Task>? BeforeHardware, AfterHardware;
    }
    private sealed class Provider(EneMemoryDeviceProvider source, Hooks hooks) : IDeviceDriverProvider, IDeviceOperationPreparation
    {
        private readonly Dictionary<string, Driver> drivers = [];
        public string ProviderId => source.ProviderId;
        public string[] ResourceDomains => source.ResourceDomains;
        public IDeviceControlDriver[] CachedDrivers
        {
            get
            {
                lock (drivers)
                {
                    foreach (var d in source.CachedDrivers.Cast<EneMemoryDeviceDriver>()) if (!drivers.ContainsKey(d.DeviceId)) drivers.Add(d.DeviceId, new(d, hooks));
                    return drivers.Values.Cast<IDeviceControlDriver>().ToArray();
                }
            }
        }
        public Task PollAsync(CancellationToken ct) => source.PollAsync(ct);
        public Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct) => source.PrepareOperationAsync(kind, operation, ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
    private sealed class Driver(EneMemoryDeviceDriver source, Hooks hooks) : IDeviceLightingDriver, IDevicePollingDriver, IPhysicalDeviceDriver, IDeviceOperationPreparation, IDevicePhasedOperation
    {
        public string DeviceId => source.DeviceId;
        public DeviceLightingLimits LightingLimits => source.LightingLimits;
        public PhysicalDeviceBinding? BindingCandidate => source.BindingCandidate;
        public DeviceObservation? Observe() => source.Observe();
        public string? Validate(DeviceEndpointAction action) => source.Validate(action);
        public Task PollAsync(CancellationToken ct) => source.PollAsync(ct);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => source.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => source.ReleaseAsync(reason, ct);
        public string? ValidateLightingStream(string[] endpoints, LightingProgram program) => source.ValidateLightingStream(endpoints, program);
        public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
        {
            var stream = Assert.IsAssignableFrom<IPreparedDeviceLightingStream>(await source.OpenLightingStreamAsync(endpoints, program, ct));
            hooks.Stream = stream; return new Stream(stream, hooks);
        }
        public Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct) => source.PrepareOperationAsync(kind, operation, ct);
        public Task<T> RunPhasedOperationAsync<T>(DevicePreparationKind kind, IDeviceHardwareExecution hardware, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            source.RunPhasedOperationAsync(kind, new Execution(hardware, kind, hooks), operation, ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
    private sealed class Execution(IDeviceHardwareExecution source, DevicePreparationKind kind, Hooks hooks) : IDeviceHardwareExecution
    {
        private int phase;
        public void CheckAuthorization() => source.CheckAuthorization();
        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> hardware, CancellationToken ct)
        {
            var number = ++phase;
            if (hooks.BeforeHardware is { } before) await before(kind, number, ct);
            var result = await source.RunAsync(hardware, ct);
            if (hooks.AfterHardware is { } after) await after(kind, number, ct);
            return result;
        }
    }
    private sealed class Stream(IPreparedDeviceLightingStream source, Hooks hooks) : IPreparedDeviceLightingStream
    {
        public Task<DeviceDriverReleaseResult[]> PrepareFrameAsync(DeviceLightingFrame frame, Func<CancellationToken, Task<DeviceDriverReleaseResult[]>> transmit, CancellationToken ct) =>
            source.PrepareFrameAsync(frame, async token =>
            {
                var pause = hooks.AfterSaved; hooks.AfterSaved = null; if (pause is not null) await pause(token);
                return await transmit(token);
            }, ct);
        public Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct) => source.WriteAsync(frame, ct);
        public Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct) => source.EndAsync(reason, ct);
    }
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Frame-Prepare-" + Guid.NewGuid().ToString("N"));
        public readonly TestClock Clock = new();
        public readonly Rack Rack = new(0x71);
        public readonly Hooks Hooks = new();
        public readonly GenericTestDriver Fan = new("fan", "smbus");
        public readonly EneMemoryDeviceProvider Source;
        public readonly DeviceControlRuntime Runtime;
        public EneMemoryDeviceDriver Memory => Assert.IsType<EneMemoryDeviceDriver>(Assert.Single(Source.CachedDrivers));
        public EneMemoryRecoveryRecord? Pending => new EneMemoryRecovery(Directory, Memory.DeviceId).Pending;
        private DeviceServiceHello hello = null!;
        private long sequence;
        public Rig(Func<EneMemoryTransport, EneMemoryTransport>? transport = null)
        {
            Source = new(Guid.NewGuid().ToString("N"), Directory, "boot", new EneMemoryDeviceBackend([transport?.Invoke(Rack.Transport()) ?? Rack.Transport()], Clock));
            Runtime = new([Fan], ["user"], new TestLog(), Clock, providers: [new Provider(Source, Hooks)]);
        }
        public async Task<Rig> Open()
        {
            await Runtime.PollAsync(); await Runtime.PollAsync();
            Memory.ConfigureLighting(Memory.BindingCandidate, [new(new(Memory.DeviceId, "zone:0"), true, new(LightingExitAction.Off))],
                [new("zone:0", "RGB", EndpointKind.Lighting, Setup: new(LedCount: 8, LedCountConfirmed: true))]);
            Runtime.Refresh(); hello = Runtime.OpenSession("user"); return this;
        }
        public Task<DeviceServiceReply> Command(DeviceOperation operation, string? device = null, LightingProgram? program = null, int? duty = null)
        {
            var state = Runtime.State(); var targets = device is null ? null : state.Endpoints.Where(e => e.Target.DeviceId == device)
                .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            return Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                device is null ? 0 : state.Inventory.TopologyRevision, targets, duty, Lighting: program)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        public Task<DeviceServiceReply> Start() => Command(DeviceOperation.SetLighting, Memory.DeviceId, new("color-cycle", [new(100, 50, 20)], 4));
        public Task Tick() { Clock.Advance(.1); return Runtime.TickLightingAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        public async ValueTask DisposeAsync()
        {
            Hooks.BeforeHardware = Hooks.AfterHardware = null; Rack.BeforeData = null;
            await Runtime.DisposeAsync(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
    [Fact]
    public async Task DurablePreparedFrameWaitsWithoutOwningTheBusAndFanCommandsContinue()
    {
        await using var r = await new Rig().Open(); var saved = Signal(); var resume = Signal(); var opens = 0;
        r.Hooks.AfterSaved = async ct => { opens = r.Rack.Opens; saved.SetResult(); await resume.Task.WaitAsync(ct); };
        var start = r.Start();
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.NotNull(r.Pending); Assert.Empty(r.Rack.Writes);
            Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.SetDuty, r.Fan.DeviceId, duty: 75)).Result!.Outcome);
            Assert.Equal(75, r.Fan.Duty); Assert.Equal(opens, r.Rack.Opens);
            resume.SetResult(); Assert.Equal(Outcome.WrittenUnverified, (await start).Result!.Outcome);
        }
        finally { resume.TrySetResult(); await start; }
    }
    [Fact]
    public async Task ExpiryAfterSavingCancelsTheUnsentRecordBeforeAnyBusAccess()
    {
        await using var r = await new Rig().Open(); await r.Start(); var previous = r.Pending!; var boundary = r.Rack.Writes.Count; var opens = 0;
        r.Hooks.AfterSaved = _ => { opens = r.Rack.Opens; r.Clock.Advance(.6); return Task.CompletedTask; };
        await r.Tick(); Assert.Equal(boundary, r.Rack.Writes.Count); Assert.Equal(opens, r.Rack.Opens); Assert.Equal(previous.Hash, r.Pending!.Hash);
        var state = r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == r.Memory.DeviceId);
        Assert.Equal(1, state.Lighting!.DroppedFrames); Assert.True(state.Lighting.Running);
        await r.Tick(); Assert.True(r.Rack.Writes.Count > boundary);
    }
    [Fact]
    public async Task OwnerLossWhileFirstFrameIsPreparedCancelsWithoutWritingAnExitToAnUnownedDimm()
    {
        await using var r = await new Rig().Open(); var saved = Signal(); var resume = Signal();
        r.Hooks.AfterSaved = async ct => { saved.SetResult(); await resume.Task.WaitAsync(ct); }; var start = r.Start();
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.NotNull(r.Pending);
            var released = await r.Command(DeviceOperation.ReleaseSession); await start;
            Assert.True(released.Success); Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
            Assert.False(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == r.Memory.DeviceId).Owned);
        }
        finally { resume.TrySetResult(); await start; }
    }
    [Fact]
    public async Task ExplicitEndWhilePreparedMakesTheQueuedCallbackUnableToSend()
    {
        await using var r = await new Rig().Open(); var saved = Signal(); var resume = Signal();
        r.Hooks.AfterSaved = async ct => { saved.SetResult(); await resume.Task.WaitAsync(ct); }; var start = r.Start();
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var ended = await r.Hooks.Stream!.EndAsync(DeviceLightingStopReason.Normal, default);
            Assert.Equal(Outcome.Confirmed, Assert.Single(ended).Result.Outcome); Assert.Null(r.Pending); Assert.Empty(r.Rack.Writes);
            resume.SetResult(); await start; Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
        }
        finally { resume.TrySetResult(); await start; }
    }
    [Fact]
    public async Task PreparedHandoffCancellationRestoresNativeOwnershipBeforeRelease()
    {
        await using var r = await new Rig().Open();
        await r.Command(DeviceOperation.SetLighting, r.Memory.DeviceId, new("fixed", [new(11, 22, 33)])); var previous = r.Pending!;
        var saved = Signal(); var resume = Signal(); var boundary = r.Rack.Writes.Count;
        r.Hooks.AfterSaved = async ct => { saved.SetResult(); await resume.Task.WaitAsync(ct); }; var start = r.Start();
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(previous.Original.NativeColors, r.Pending!.Original.NativeColors);
            await r.Command(DeviceOperation.ReleaseSession); await start;
            Assert.Null(r.Pending); Assert.Equal(0, r.Rack.Registers[(0x71, 0x8021)]);
            Assert.DoesNotContain(r.Rack.Writes.Skip(boundary), w => w.Register == 0x8020 && w.Value == 1);
        }
        finally { resume.TrySetResult(); await start; }
    }
}
