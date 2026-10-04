using System.Security.Principal;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneConnectionLightingTests
{
    private const string Installation = "af34df7628514347b61bebae979da711", Bus = "recording-bus";
    private static string Id(byte address = 0x71) => DeviceIdentity.Create(Installation, "ene-memory",
        "location:" + SettingsTransfer.Hash(new { Bus, Address = address }));
    private static EndpointReference Target(byte address = 0x71) => new(Id(address), "zone:0");
    private static DeviceConfigurationPackage Empty() => ServiceRuntimeBootstrap.MonitoringConfiguration(Installation);
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly TestClock Clock = new();
        public readonly Rack Rack;
        public readonly EneMemoryDeviceBackend Backend;
        public readonly EneMemoryDeviceProvider Provider;
        public readonly DeviceControlRuntime Runtime;
        public readonly ConfigurationGenerationStore Store;
        public readonly DeviceConfigurationAuthority Authority;
        public readonly string User;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public Rig(Rack rack, DeviceConfigurationPackage? configuration = null, string user = "user", IDeviceControlDriver[]? additional = null)
        {
            Rack = rack; User = user; Backend = new([rack.Transport()], Clock);
            Provider = new(Installation, Directory.Path, "test-boot", Backend);
            Runtime = new(additional ?? [], [user], new TestLog(), Clock, demo: true, providers: [Provider],
                physicalBindings: (additional ?? []).OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray());
            Store = DeviceConfigurationCodec.Store(Path.Combine(Directory.Path, "configuration"));
            Store.Activate(Store.Stage(DeviceConfigurationCodec.Write(configuration ?? Empty())));
            Authority = new(Store, Installation, Store.ReadActive()!); Runtime.InstallConfigurationHandler(Authority);
        }
        public async Task Open(bool session = true)
        {
            await Runtime.PollAsync(); await Runtime.PollAsync();
            if (session) Hello = Runtime.OpenSession(User);
        }
        public EneMemoryDeviceDriver Driver(byte address = 0x71) => Assert.IsType<EneMemoryDeviceDriver>(Provider.CachedDrivers.Single(d => d.DeviceId == Id(address)));
        public Task<DeviceServiceReply> Send(DeviceConfigurationRequest request) => Runtime.ExecuteAsync(new(2, Hello.Epoch, Hello.Session,
            Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.Configuration, Configuration: request)).WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<int> Prepare()
        {
            var reply = await Send(new(DeviceConfigurationOperation.PrepareConnectionLighting));
            Assert.True(reply.Success, reply.Error); return Assert.IsType<int>(reply.Configuration!.PreparedLighting);
        }
        public DeviceServiceRequest Request(DeviceOperation operation, LightingProgram? program = null, params byte[] addresses)
        {
            var state = Runtime.State();
            var versions = addresses.Select(a => state.Endpoints.Single(e => e.Target == Target(a)))
                .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
            return new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                operation == DeviceOperation.ReleaseSession ? 0 : state.Inventory.TopologyRevision,
                operation == DeviceOperation.ReleaseSession ? null : versions, Lighting: program);
        }
        public Task<DeviceServiceReply> Color(bool animated = false, params byte[] addresses) => Runtime.ExecuteAsync(Request(DeviceOperation.SetLighting,
            new(animated ? "color-cycle" : "fixed", [new(20, 40, 60)]), addresses.Length == 0 ? [0x71] : addresses)).WaitAsync(TimeSpan.FromSeconds(10));
        public Task<DeviceServiceReply> Stop() => Runtime.ExecuteAsync(Request(DeviceOperation.StopLighting, null, 0x71));
        public Task<DeviceServiceReply> ChangeSettings() => Send(new(DeviceConfigurationOperation.SaveNotifications,
            ExpectedGeneration: Authority.Generation.Id, Notifications: Authority.Configuration.Preferences.Notifications with { Desktop = false }));
        public bool Ready(byte address = 0x71) => Driver(address).Observe()!.Endpoints.Single().Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Usable;
        public string ConfigurationHash() => SettingsTransfer.Hash(Authority.Configuration);
        public async ValueTask DisposeAsync()
        {
            Rack.OnIdentify = null; Rack.BeforeData = null; Rack.FailRead = false; Rack.FailWrite = false;
            await Runtime.DisposeAsync(); Directory.Dispose();
        }
    }

    [Fact] public async Task TwoMemoriesPrepareWithoutBusIoOutputOrPersistentSettings()
    {
        var rack = new Rack(0x71, 0x73); await using var r = new Rig(rack); await r.Open();
        var hash = r.ConfigurationHash(); var pointer = r.Store.ReadActivePointer(); var opens = rack.Opens;
        Assert.False(r.Ready()); Assert.Equal(2, await r.Prepare());
        Assert.Equal(0, await r.Prepare()); Assert.Equal(opens, rack.Opens); Assert.Empty(rack.Writes);
        Assert.True(r.Ready()); Assert.True(r.Ready(0x73)); Assert.Equal(hash, r.ConfigurationHash());
        Assert.Equal(pointer, r.Store.ReadActivePointer()); Assert.Empty(r.Authority.Configuration.Bindings.Devices);
        Assert.All(r.Runtime.State().Endpoints, e => Assert.False(e.Owned));
        Assert.All(r.Runtime.State().Inventory.Devices, d => Assert.Equal(IdentityConfidence.ConnectionBound, d.Device.IdentityConfidence));
        Assert.Null(new EneMemoryRecovery(r.Directory.Path, Id()).Pending);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AppliedColorOrSynchronizationIsConnectionOnlyAndOriginalRestores(bool animated)
    {
        var rack = new Rack(0x71); var original = rack.Registers.ToDictionary(); await using var r = new Rig(rack); await r.Open();
        var hash = r.ConfigurationHash(); var pointer = r.Store.ReadActivePointer(); await r.Prepare();
        var answer = await r.Color(animated); Assert.True(answer.Success, answer.Error);
        Assert.Equal(DevicePolicySaveState.SessionOnly, answer.Result!.Targets.Single().Persistence!.State);
        Assert.Equal(hash, r.ConfigurationHash()); Assert.Equal(pointer, r.Store.ReadActivePointer()); Assert.NotEmpty(rack.Writes);
        var ended = await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseSession)); Assert.True(ended.Success, ended.Error);
        Assert.All(original, pair => Assert.Equal(pair.Value, rack.Registers[pair.Key])); Assert.False(r.Ready());
        Assert.Null(new EneMemoryRecovery(r.Directory.Path, Id()).Pending);
    }

    [Theory] [InlineData("registered")] [InlineData("revoked")] [InlineData("imported")]
    public async Task SavedRegistrationOrExplicitRevocationIsNeverReapproved(string kind)
    {
        var config = Empty(); var setup = kind == "registered" ? null : new EndpointSetup(LedCount: 8, LedCountConfirmed: false);
        config = config with { Devices = new(2, [new(Id(), "Memory", "ene-memory", DeviceKind.Memory,
            [new("zone:0", "RGB", EndpointKind.Lighting, Setup: setup)])]) };
        if (kind == "imported") config = config with { Lighting = config.Lighting with { Endpoints = [new(Target(), false, new(LightingExitAction.Off))] } };
        var rack = new Rack(0x71); await using var r = new Rig(rack, config); await r.Open();
        Assert.Equal(0, await r.Prepare()); Assert.False(r.Ready()); Assert.Empty(rack.Writes);
    }

    [Fact] public async Task NewAuthenticatedSessionRequiresFreshPreparation()
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open(); await r.Prepare();
        var old = r.Request(DeviceOperation.SetLighting, new("fixed", [new(1, 2, 3)]), 0x71);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request(DeviceOperation.ReleaseSession))).Success);
        r.Hello = r.Runtime.OpenSession(r.User); Assert.False(r.Ready());
        Assert.False((await r.Runtime.ExecuteAsync(old)).Success); Assert.Empty(rack.Writes);
        Assert.Equal(1, await r.Prepare()); Assert.True(r.Ready());
    }

    [Theory] [InlineData("disconnect")] [InlineData("platform")] [InlineData("invalidate")]
    public async Task ChangedConnectionRejectsOldRequestAndReevaluatesWithoutInheritedColor(string change)
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open(); await r.Prepare();
        var before = r.Driver().BindingCandidate; var old = r.Request(DeviceOperation.SetLighting, new("fixed", [new(1, 2, 3)]), 0x71);
        if (change == "disconnect") { rack.Present.Clear(); await r.Runtime.PollAsync(); rack.Present.Add(0x71); }
        if (change == "platform") rack.Platform = "replacement-platform";
        if (change == "invalidate") r.Backend.Invalidate();
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
        Assert.NotEqual(before, r.Driver().BindingCandidate); Assert.False(r.Ready());
        Assert.False((await r.Runtime.ExecuteAsync(old)).Success); Assert.Empty(rack.Writes);
        Assert.Equal(1, await r.Prepare()); Assert.True(r.Ready());
        Assert.Null(r.Runtime.State().Endpoints.Single().RequestedLighting);
        Assert.Empty(r.Authority.Configuration.Lighting.Preferences ?? []);
    }

    [Fact] public async Task ServiceRestartCannotLoadConnectionPermissionOrColorFromDisk()
    {
        DeviceConfigurationPackage saved;
        await using (var r = new Rig(new Rack(0x71)))
        {
            await r.Open(); await r.Prepare(); Assert.True((await r.Color()).Success); saved = r.Authority.Configuration;
        }
        await using var restarted = new Rig(new Rack(0x71), saved); await restarted.Open();
        Assert.False(restarted.Ready()); Assert.Empty(restarted.Rack.Writes); Assert.Null(restarted.Runtime.State().Endpoints.Single().RequestedLighting);
        Assert.Equal(1, await restarted.Prepare()); Assert.Empty(restarted.Rack.Writes);
    }

    [Fact] public async Task ConfigurationGenerationChangeRevokesPermissionBeforeFurtherOutput()
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open(); await r.Prepare();
        Assert.True((await r.ChangeSettings()).Success); Assert.False(r.Ready());
        Assert.False((await r.Color()).Success); Assert.Empty(rack.Writes);
        Assert.Equal(1, await r.Prepare()); Assert.True(r.Ready()); Assert.Empty(rack.Writes);
    }

    [Fact] public async Task ExpiredPermissionWithRecoveryPendingCannotBePreparedUntilRestored()
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open(); await r.Prepare();
        Assert.True((await r.Color()).Success); Assert.NotNull(new EneMemoryRecovery(r.Directory.Path, Id()).Pending);
        await r.ChangeSettings(); var writes = rack.Writes.Count;
        Assert.Equal(0, await r.Prepare()); Assert.False((await r.Color()).Success); Assert.Equal(writes, rack.Writes.Count);
        Assert.True((await r.Stop()).Success); Assert.Null(new EneMemoryRecovery(r.Directory.Path, Id()).Pending);
        Assert.Equal(1, await r.Prepare());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidationImmediatelyBeforeDataWritePreventsAllOutput(bool animated)
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open(); await r.Prepare();
        rack.BeforeData = (_, write) => {
            if (!write) return;
            rack.BeforeData = null;
            var changed = r.Authority.ExecuteAsync(new(DeviceConfigurationOperation.SaveNotifications,
                ExpectedGeneration: r.Authority.Generation.Id, Notifications: new(Desktop: false, Rules: [])),
                Guid.NewGuid().ToString("N"), r.Hello.Session, r.Runtime, default).GetAwaiter().GetResult();
            Assert.Equal(Outcome.Confirmed, changed.Outcome);
        };
        Assert.Equal(Outcome.Rejected, (await r.Color(animated)).Result!.Outcome); Assert.Empty(rack.Writes);
        Assert.Null(new EneMemoryRecovery(r.Directory.Path, Id()).Pending);
    }

    [Fact] public async Task ConcurrentRediscoveryCannotGrantAnOldConnectionPermission()
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var resume = new ManualResetEventSlim();
        rack.OnIdentify = () => { rack.OnIdentify = null; entered.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var discovery = r.Backend.DiscoverAsync(default);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, await r.Prepare()); Assert.False(r.Ready()); }
        finally { resume.Set(); await discovery; }
        Assert.Equal(1, await r.Prepare()); Assert.Empty(rack.Writes);
    }

    [Theory] [InlineData("stale")] [InlineData("failed-read")] [InlineData("unknown-definition")]
    public async Task MissingOrStaleEvidenceFailsClosed(string reason)
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open();
        if (reason == "stale") r.Clock.Advance(2.1);
        if (reason == "failed-read") { rack.FailRead = true; await r.Runtime.PollAsync(); }
        if (reason == "unknown-definition") { rack.Identity = null; await r.Runtime.PollAsync(); }
        Assert.Equal(0, await r.Prepare()); Assert.Empty(rack.Writes);
    }

    [Fact] public async Task ExternalConfigurationChangeRevokesPermissionAndRequiresServiceReevaluation()
    {
        var rack = new Rack(0x71); await using var r = new Rig(rack); await r.Open(); await r.Prepare();
        var next = r.Authority.Configuration with { Preferences = r.Authority.Configuration.Preferences with { Notifications = new(Desktop: false, Rules: []) } };
        r.Store.Activate(r.Store.Stage(DeviceConfigurationCodec.Write(next)));
        Assert.False((await r.Send(new(DeviceConfigurationOperation.PrepareConnectionLighting))).Success);
        Assert.False(r.Ready()); Assert.False((await r.Color()).Success); Assert.Empty(rack.Writes);
    }

    [Theory] [InlineData(128, 8)] [InlineData(1, 4096)]
    public async Task SavedCapacityIsReservedBeforeConnectionPreparation(int count, int leds)
    {
        var config = Empty();
        var endpoints = Enumerable.Range(0, count).Select(i => new RegisteredEndpoint("zone" + i, "RGB", EndpointKind.Lighting, Setup: new(LedCount: leds))).ToArray();
        config = config with { Devices = new(2, [new("reserved", "Reserved", "test", DeviceKind.Controller, endpoints)]),
            Lighting = config.Lighting with { Endpoints = endpoints.Select(e => new LightingEndpointConfiguration(new("reserved", e.Id), false)).ToArray() } };
        await using var r = new Rig(new Rack(0x71), config); await r.Open();
        Assert.Equal(0, await r.Prepare()); Assert.Empty(r.Rack.Writes);
    }

    [Fact] public async Task NormalFirstUseAndRefreshPrepareMemoryThroughAuthenticatedDemoPipeOnly()
    {
        using var identity = WindowsIdentity.GetCurrent(); var user = identity.User!.Value;
        await using var r = new Rig(new Rack(0x71), user: user); await r.Open(session: false);
        var pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DeviceServicePipeServer(r.Runtime, [user], new TestLog(), pipe).RunAsync(stop.Token, () => ready.TrySetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId));
        try
        {
            await client.ConnectAsync(); Assert.False(r.Ready()); Assert.Empty(r.Rack.Writes);
            var first = await client.PrepareFirstUseAsync(); Assert.Null(first.Deferred); Assert.Equal(1, first.PreparedLighting); Assert.True(r.Ready());
            var hash = r.ConfigurationHash(); r.Rack.Present.Clear(); await r.Runtime.PollAsync(); await client.RefreshAsync();
            r.Rack.Present.Add(0x71); await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await client.RefreshAsync();
            Assert.True(r.Ready()); Assert.Equal(hash, r.ConfigurationHash()); Assert.Empty(r.Rack.Writes);
        }
        finally { await client.DisposeAsync(); stop.Cancel(); try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    }

    [Theory] [InlineData("disconnect")] [InlineData("expire")]
    public async Task LostOwnerRevokesEvenAnIdlePermission(string boundary)
    {
        await using var r = new Rig(new Rack(0x71)); await r.Open(); await r.Prepare();
        var request = r.Request(DeviceOperation.SetLighting, new("fixed", [new(1, 2, 3)]), 0x71);
        if (boundary == "disconnect") await r.Runtime.DisconnectAsync(r.Hello.Session);
        else r.Clock.Advance(DeviceServiceProtocol.LeaseSeconds + 1);
        Assert.False((await r.Runtime.ExecuteAsync(request)).Success); Assert.False(r.Ready()); Assert.Empty(r.Rack.Writes);
    }

    [Fact] public async Task DisplayedConnectionColorIsDiscardedWhenMemoryIsReplaced()
    {
        await using var r = new Rig(new Rack(0x71)); await r.Open(); await r.Prepare(); Assert.True((await r.Color()).Success);
        var draft = new DeviceLightingDraft(); draft.Update(r.Runtime.State(), r.Authority.Generation.Id, r.Authority.Configuration.Lighting, 0, true);
        draft.Select([Target()]); Assert.Equal(new RgbColor(20, 40, 60), draft.Program.Colors[0]); Assert.False(draft.CanAdjust);
        await r.Stop(); r.Rack.Present.Clear(); await r.Runtime.PollAsync(); r.Rack.Present.Add(0x71);
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync(); await r.Prepare();
        draft.Update(r.Runtime.State(), r.Authority.Generation.Id, r.Authority.Configuration.Lighting, 0, true);
        Assert.Equal(new RgbColor(52, 137, 255), draft.Program.Colors[0]); Assert.Null(r.Runtime.State().Endpoints.Single().RequestedLighting);
    }

    [Fact] public async Task BackendInvalidationRevokesReadinessBeforeProviderPublishesADisconnect()
    {
        await using var r = new Rig(new Rack(0x71)); await r.Open(); await r.Prepare();
        r.Backend.Invalidate(); Assert.False(r.Ready()); Assert.Equal(0, await r.Prepare()); Assert.Empty(r.Rack.Writes);
    }

    [Fact] public void PreparationProtocolRejectsMixedOrUnboundedRepliesAndMutationPayloads()
    {
        Assert.True(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.PrepareConnectionLighting)));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.PrepareConnectionLighting,
            ExpectedGeneration: Guid.NewGuid().ToString("N"))));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.PrepareConnectionLighting,
            LightingSetupTarget: Target())));
        var reply = new DeviceConfigurationReply(Outcome.Confirmed, "prepared", Guid.NewGuid().ToString("N"), PreparedLighting: 2);
        Assert.True(DeviceConfigurationProtocol.Valid(reply));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { PreparedLighting = 129 }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Outcome = Outcome.Unknown }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Data = [1] }));
    }

    [Fact] public async Task RuntimeReconfigurationRevokesPermissionEvenWithoutADurableEdit()
    {
        await using var r = new Rig(new Rack(0x71)); await r.Open(); await r.Prepare();
        await r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision,
            (_, _) => Task.FromResult(Array.Empty<PhysicalDeviceBinding>()));
        Assert.False(r.Ready()); Assert.Empty(r.Rack.Writes); Assert.Equal(1, await r.Prepare());
    }

    private sealed class BoundFan(GenericTestDriver source) : IPhysicalDeviceDriver
    {
        public string DeviceId => source.DeviceId;
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, "demo:" + DeviceId, "test-v1", new('b', 64));
        public DeviceObservation? Observe() => source.Observe();
        public string? Validate(DeviceEndpointAction action) => source.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => source.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => source.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    [Fact] public async Task MemoryPreparationDoesNotTouchAnAlreadyRunningFan()
    {
        var fan = new GenericTestDriver("fan"); await using var r = new Rig(new Rack(0x71), additional: [new BoundFan(fan)]); await r.Open();
        var state = r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "fan");
        var request = r.Request(DeviceOperation.SetDuty) with { Targets = [new(state.Target, state.Generation, state.PolicyRevision)], Duty = 62 };
        Assert.Equal(Outcome.Confirmed, (await r.Runtime.ExecuteAsync(request)).Result!.Outcome);
        var writes = fan.Writes; var releases = fan.Releases;
        Assert.Equal(1, await r.Prepare()); Assert.Equal(writes, fan.Writes); Assert.Equal(releases, fan.Releases); Assert.Equal(62, fan.Duty);
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "fan").Owned); Assert.Empty(r.Rack.Writes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MixedPersistentAndConnectionLightingNeverSavesAPartialSelection(bool animated)
    {
        var light = new DeviceLightingRuntimeTests.Driver("serial"); var target = new EndpointReference("serial", "light1");
        var config = Empty() with { Devices = new(2, [new("serial", "Serial", "demo-rgb", DeviceKind.Memory,
            [new("light1", "RGB", EndpointKind.Lighting, Setup: new(LedCount: 24, LedCountConfirmed: true))])]),
            Bindings = new(2, Installation, [light.BindingCandidate]) };
        config = config with { Lighting = config.Lighting with { Endpoints = [new(target, true, new(LightingExitAction.Off))],
            Preferences = [new(target, new("fixed", [new(9, 8, 7)]))] } };
        await using var r = new Rig(new Rack(0x71), config, additional: [light]); await r.Open(); await r.Prepare();
        var hash = r.ConfigurationHash(); var pointer = r.Store.ReadActivePointer();
        var state = r.Runtime.State(); var versions = state.Endpoints.Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        var request = r.Request(DeviceOperation.SetLighting, new(animated ? "color-cycle" : "fixed", [new(20, 40, 60)]), 0x71) with { Targets = versions };
        var result = await r.Runtime.ExecuteAsync(request); Assert.True(result.Success, result.Error);
        Assert.All(result.Result!.Targets, t => Assert.Equal(DevicePolicySaveState.SessionOnly, t.Persistence!.State));
        Assert.Equal(hash, r.ConfigurationHash()); Assert.Equal(pointer, r.Store.ReadActivePointer());
        var draft = new DeviceLightingDraft(); draft.Update(r.Runtime.State(), r.Authority.Generation.Id, r.Authority.Configuration.Lighting, 0, true);
        draft.Select([target]); Assert.Equal(new RgbColor(20, 40, 60), draft.Program.Colors[0]); Assert.False(draft.CanAdjust);
    }
}
