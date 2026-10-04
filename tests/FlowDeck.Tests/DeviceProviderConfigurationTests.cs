using System.Collections.Concurrent;
using System.Security.Cryptography;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private static readonly DeviceProviderRoutingDefinition FixtureRoutingDefinition = new("fixture-routes", 1, new('a', 64));
    private sealed class RoutingPolicy(ConcurrentQueue<string> events) : IDeviceProviderRouting
    {
        public DeviceProviderRoutingDefinition Definition { get; set; } = FixtureRoutingDefinition;
        public bool SupportsDeviceSelection { get; init; }
        public readonly List<Provider> Created = [];
        public Func<DeviceConfigurationPackage, string[], IDeviceDriverProvider[]>? Factory;
        public Func<DeviceConfigurationPackage, DeviceConfigurationPackage, DeviceProviderRoutePlan>? Planner;
        public bool Reject;
        public DeviceProviderRoutePlan Plan(DeviceConfigurationPackage before, DeviceConfigurationPackage after)
        {
            if (Reject) throw new IOException("resource coexistence not verified");
            return Planner?.Invoke(before, after) ?? new([before.Preferences.Providers?.OpenRgbEnabled == true ? "sdk" : "direct"],
                [after.Preferences.Providers?.OpenRgbEnabled == true ? "sdk" : "direct"]);
        }
        public Provider New(string id)
        {
            var provider = new Provider(id, id == "independent" ? "cooler" : id == "direct" ? "rgb" : "external", events);
            Created.Add(provider); return provider;
        }
        public IDeviceDriverProvider[] Create(DeviceConfigurationPackage config, string[] ids) => Factory?.Invoke(config, ids) ?? ids.Select(New).ToArray();
    }
    private sealed class RoutingRig : IAsyncDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public readonly ConcurrentQueue<string> Events = new();
        public ConfigurationGenerationStore Store = null!;
        public RoutingPolicy Policy = null!;
        public DeviceRuntimeStartup Startup = null!;
        public Provider Direct = null!, Independent = null!;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public string RecordPath => Path.Combine(Store.DirectoryPath, "configuration-application.json");
        public DeviceProviderTransition Journal => new(Store, JournalRig.Installation, FixtureRoutingDefinition);
        public ConfigurationApplication Application => ServiceEnvironment.Read<ConfigurationApplication>(RecordPath);
        public static async Task<RoutingRig> Create(bool routes = true, bool selection = false)
        {
            var r = new RoutingRig(); r.Policy = new(r.Events) { SupportsDeviceSelection = selection };
            r.Direct = r.Policy.New("direct"); r.Independent = r.Policy.New("independent");
            var config = ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation);
            var devices = new[] { r.Direct, r.Independent };
            config = config with {
                Devices = new(2, devices.Select(p => new RegisteredDevice(p.Driver.DeviceId, p.Driver.DeviceId, p.ProviderId, DeviceKind.Controller,
                    [new("fan", "Fan", EndpointKind.Cooling)])).ToArray()),
                Bindings = new(2, JournalRig.Installation, devices.Select(p => p.Driver.BindingCandidate).ToArray()) };
            r.Store = DeviceConfigurationCodec.Store(r.directory.Path); r.Store.Activate(r.Store.Stage(DeviceConfigurationCodec.Write(config)));
            r.Startup = await DeviceRuntimeStartup.CreateAsync(r.Store, JournalRig.Installation, () => new([], devices), ["user"], new TestLog(),
                new TestClock(), demo: true, providerRouting: routes ? r.Policy : null);
            r.Hello = r.Startup.Runtime.OpenSession("user"); return r;
        }
        public DeviceServiceRequest Request(DeviceConfigurationRequest request) => new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"),
            ++sequence, "test", DeviceOperation.Configuration, Configuration: request);
        public Task<DeviceServiceReply> Send(DeviceConfigurationRequest request) => Startup.Runtime.ExecuteAsync(Request(request));
        public DeviceConfigurationPackage Target(bool enabled = true, int port = 6742) => Startup.Configuration with {
            Preferences = Startup.Configuration.Preferences with { Providers = new(enabled, port) } };
        public async Task<string> Upload(DeviceConfigurationPackage config, bool validate = true)
        {
            var bytes = DeviceConfigurationCodec.SerializePackage(config);
            var head = await Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: Startup.Generation.Id,
                TotalBytes: bytes.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))));
            Assert.True(head.Success, head.Error); var id = head.Configuration!.TransferId!;
            for (var offset = 0; offset < bytes.Length;)
            {
                var part = bytes.AsSpan(offset, Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length - offset)).ToArray();
                Assert.True((await Send(new(DeviceConfigurationOperation.Append, id, Offset: offset, Data: part))).Success); offset += part.Length;
            }
            if (validate) { var result = await Send(new(Hello.Features.Contains(DeviceProviderRouteReview.Feature) ? DeviceConfigurationOperation.ValidateProviderRoute : DeviceConfigurationOperation.Validate, id)); Assert.True(result.Success, result.Error); }
            return id;
        }
        public Task<DeviceServiceReply> Apply(string id) => Send(new(DeviceConfigurationOperation.Apply, id,
            TopologyRevision: Startup.Runtime.State().Inventory.TopologyRevision));
        public async Task Restart()
        {
            await Startup.DisposeAsync();
            Startup = await DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(Store, JournalRig.Installation,
                config => new([], Policy.Create(config, [config.Preferences.Providers?.OpenRgbEnabled == true ? "sdk" : "direct", "independent"])),
                ["user"], new TestLog(), new TestClock(), demo: true, providerRouting: Policy);
            Hello = Startup.Runtime.OpenSession("user");
        }
        public async ValueTask DisposeAsync() { await Startup.DisposeAsync(); directory.Dispose(); }
    }

    [Fact]
    public async Task ConfigurationApplySwitchesTheProviderAndCommitsOneGenerationWithoutStartingControls()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation;
        r.Policy.Factory = (config, ids) =>
        {
            Assert.Equal(new[] { "sdk" }, ids); Assert.True(config.Preferences.Providers!.OpenRgbEnabled);
            Assert.True(r.Direct.Disposed); Assert.False(r.Independent.Disposed);
            Assert.Equal(ProviderTransitionPhase.StartingTarget, r.Journal.Pending!.Phase);
            Assert.Equal(before, r.Store.ReadActivePointer()); return [r.Policy.New("sdk")];
        };
        var transfer = await r.Upload(r.Target()); var request = r.Request(new(DeviceConfigurationOperation.Apply, transfer,
            TopologyRevision: r.Startup.Runtime.State().Inventory.TopologyRevision));
        var applied = await r.Startup.Runtime.ExecuteAsync(request); Assert.True(applied.Success, applied.Error);
        Assert.NotEqual(before, r.Startup.Generation); Assert.Equal(r.Startup.Generation, r.Store.ReadActivePointer());
        Assert.True(r.Startup.Configuration.Preferences.Providers!.OpenRgbEnabled); Assert.Null(r.Journal.Pending);
        Assert.Equal("applied", r.Application.Phase); Assert.False(r.Independent.Disposed);
        Assert.True(r.Startup.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == "external").Connected);
        Assert.All(r.Policy.Created, p => Assert.Equal(0, p.Driver.Writes));
        var created = r.Policy.Created.Count; Assert.True((await r.Startup.Runtime.ExecuteAsync(request)).Success);
        Assert.Equal(created, r.Policy.Created.Count);
    }

    [Fact]
    public async Task AServiceWithoutRoutingRejectsAProviderChangeBeforeReleasingHardware()
    {
        await using var r = await RoutingRig.Create(routes: false); var before = r.Startup.Generation; var releases = r.Direct.Driver.Releases;
        var id = await r.Upload(r.Target(), validate: false);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.Validate, id))).Success);
        Assert.False((await r.Apply(id)).Success); Assert.Equal(before, r.Store.ReadActivePointer());
        Assert.Equal(releases, r.Direct.Driver.Releases); Assert.False(r.Direct.Disposed); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public async Task OmittedAndDefaultRoutingSettingsAreEquivalentForOrdinaryConfiguration()
    {
        await using var r = await RoutingRig.Create(routes: false); var config = r.Startup.Configuration;
        var id = await r.Upload(config with { Preferences = config.Preferences with { Providers = null } });
        Assert.True((await r.Apply(id)).Success); Assert.False(r.Direct.Disposed); Assert.Null(r.Journal.Pending);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ResourcePolicyIsRecheckedAtPreviewAndAtApply(bool afterPreview)
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation;
        var id = await r.Upload(r.Target(), validate: afterPreview); r.Policy.Reject = true;
        var result = afterPreview ? await r.Apply(id) : await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id));
        Assert.False(result.Success); Assert.Equal(before, r.Store.ReadActivePointer());
        Assert.False(r.Direct.Disposed); Assert.Null(r.Journal.Pending); Assert.Equal(2, r.Policy.Created.Count);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("empty")] [InlineData("invalid-id")]
    public async Task InvalidCompiledPlansAreRejectedBeforeHardwareChanges(string fault)
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation;
        r.Policy.Planner = (_, _) => fault switch
        {
            "duplicate" => new(["direct"], ["sdk", "sdk"]),
            "empty" => new([], []),
            _ => new([""], ["sdk"])
        };
        var id = await r.Upload(r.Target(), validate: false);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id))).Success);
        Assert.False(r.Direct.Disposed); Assert.Equal(before, r.Store.ReadActivePointer()); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public async Task APlanChangedAfterQuiescenceCannotCloseTheOldRoute()
    {
        await using var r = await RoutingRig.Create(); var calls = 0;
        r.Policy.Planner = (_, _) => new(["direct"], ++calls < 3 ? ["sdk"] : ["sdk", "unexpected"]);
        var result = await r.Apply(await r.Upload(r.Target())); Assert.False(result.Success); Assert.Equal(3, calls);
        Assert.False(r.Direct.Disposed); Assert.Null(r.Journal.Pending); Assert.Equal("failed", r.Application.Phase);
    }

    [Theory]
    [InlineData("factory")] [InlineData("poll")] [InlineData("activation")]
    public async Task AFailedTargetRestoresTheOldRouteAndSettingsBeforeReportingFailure(string fault)
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; Provider? target = null;
        r.Policy.Factory = (_, ids) =>
        {
            if (ids[0] == "sdk")
            {
                if (fault == "factory") throw new IOException("construction failed");
                target = r.Policy.New("sdk"); target.FailPoll = fault == "poll"; return [target];
            }
            if (target is not null) { Assert.True(target.Disposed); Assert.True(target.Driver.Disposed); }
            Assert.Equal(ProviderTransitionPhase.StartingSource, r.Journal.Pending!.Phase); return [r.Policy.New("direct")];
        };
        var failed = false;
        if (fault == "activation") r.Store.Checkpoint = phase => { if (phase == "before-switch" && !failed) { failed = true; throw new IOException("disk failure"); } };
        var id = await r.Upload(r.Target()); var result = await r.Apply(id);
        Assert.False(result.Success); Assert.Equal(Outcome.Failed, result.Configuration!.Outcome);
        Assert.Equal(before, r.Store.ReadActivePointer()); Assert.Equal(before, r.Startup.Generation);
        Assert.Equal("rolled-back", r.Application.Phase); Assert.Null(r.Journal.Pending); Assert.NotNull(r.Startup.Runtime.State().LifecycleError);
        Assert.True(r.Startup.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == "rgb").Connected);
        Assert.False(r.Independent.Disposed);
        r.Policy.Factory = null; r.Store.Checkpoint = null;
        Assert.False((await r.Apply(id)).Success); // Recreated providers have a new connection/topology generation.
        Assert.True((await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id))).Success);
        var retry = await r.Apply(id); Assert.True(retry.Success, retry.Error); Assert.Null(r.Startup.Runtime.State().LifecycleError);
    }

    [Theory]
    [InlineData("release")] [InlineData("close")]
    public async Task FailedTargetReleaseOrClosureCannotStartSourceAndRequiresStartupRecovery(string fault)
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; Provider? target = null; var sourceFactories = 0;
        r.Policy.Factory = (_, ids) =>
        {
            if (ids[0] == "direct") { sourceFactories++; return [r.Policy.New("direct")]; }
            target = r.Policy.New("sdk"); target.Driver.FailRelease = fault == "release"; target.Driver.FailDispose = fault == "close"; return [target];
        };
        if (fault == "close") r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("activation failed"); };
        try
        {
            var id = await r.Upload(r.Target()); var result = await r.Apply(id);
            Assert.False(result.Success); Assert.Equal(Outcome.Unknown, result.Configuration!.Outcome); Assert.Equal(0, sourceFactories);
            Assert.Equal(before, r.Store.ReadActivePointer()); Assert.NotNull(r.Journal.Pending);
            Assert.False((await r.Apply(id)).Success); Assert.Equal(0, sourceFactories);
        }
        finally { if (target is not null) target.Driver.FailRelease = target.Driver.FailDispose = false; r.Store.Checkpoint = null; }
        r.Policy.Factory = null; await r.Restart(); Assert.Null(r.Journal.Pending); Assert.Equal(before, r.Startup.Generation);
    }

    [Fact]
    public async Task AFactoryWithUnexpectedProviderIdsIsClosedBeforeSourceRollback()
    {
        await using var r = await RoutingRig.Create(); Provider? wrong = null;
        r.Policy.Factory = (_, ids) =>
        {
            if (ids[0] == "sdk") { wrong = r.Policy.New("unexpected"); return [wrong]; }
            Assert.True(wrong!.Disposed); Assert.True(wrong.Driver.Disposed); Assert.Equal(0, wrong.Polls);
            return [r.Policy.New("direct")];
        };
        var result = await r.Apply(await r.Upload(r.Target())); Assert.False(result.Success);
        Assert.Equal(Outcome.Failed, result.Configuration!.Outcome); Assert.Null(r.Journal.Pending); Assert.Equal("rolled-back", r.Application.Phase);
    }

    [Fact]
    public async Task DiscoveryCannotAdoptAnOldBindingForADifferentProviderEvenWithTheSameDeviceId()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; Provider? target = null;
        r.Policy.Factory = (_, ids) =>
        {
            if (ids[0] == "sdk") { target = new("sdk", "rgb", r.Events); return [target]; }
            Assert.True(target!.Disposed); return [r.Policy.New("direct")];
        };
        var result = await r.Apply(await r.Upload(r.Target())); Assert.False(result.Success);
        Assert.Equal(Outcome.Failed, result.Configuration!.Outcome); Assert.Equal(before, r.Store.ReadActivePointer());
        Assert.Equal("rolled-back", r.Application.Phase); Assert.Null(r.Journal.Pending); Assert.Equal(0, target!.Driver.Writes);
    }

    [Fact]
    public async Task AnUnclosedSourceNeverConstructsEitherReplacementAndRemainsRecoverableAtStartup()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; var created = r.Policy.Created.Count;
        r.Direct.Driver.FailDispose = true;
        try
        {
            var result = await r.Apply(await r.Upload(r.Target())); Assert.False(result.Success);
            Assert.Equal(Outcome.Unknown, result.Configuration!.Outcome); Assert.Equal(created, r.Policy.Created.Count);
            Assert.Equal(ProviderTransitionPhase.Prepared, r.Journal.Pending!.Phase); Assert.Equal(before, r.Store.ReadActivePointer());
        }
        finally { r.Direct.Driver.FailDispose = false; }
        await r.Restart(); Assert.Null(r.Journal.Pending); Assert.Equal(before, r.Startup.Generation);
    }

    [Fact]
    public async Task ColdRollbackResolvesAnEarlierAppliedMarkerToTheActuallyRecoveredGeneration()
    {
        await using var r = await RoutingRig.Create(); Provider? target = null;
        r.Policy.Factory = (_, ids) => { target = r.Policy.New("sdk"); target.Driver.FailDispose = true; return [target]; };
        r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("pointer not committed"); };
        try
        {
            Assert.False((await r.Apply(await r.Upload(r.Target()))).Success);
            Assert.Equal(ProviderTransitionPhase.TargetReady, r.Journal.Pending!.Phase);
            // Simulate an applied marker left by a process that reverted the pointer, then stopped.
            ServiceEnvironment.Write(r.RecordPath, r.Application with { Phase = "applied" });
        }
        finally { if (target is not null) target.Driver.FailDispose = false; r.Store.Checkpoint = null; r.Policy.Factory = null; }
        await r.Restart(); Assert.Equal("rolled-back", r.Application.Phase); Assert.Null(r.Journal.Pending);
        Assert.Equal(r.Application.Before, r.Startup.Generation);
    }

    [Fact]
    public async Task AConflictingConfigurationApplicationBlocksColdRecoveryBeforeAnyFactory()
    {
        await using var r = await RoutingRig.Create(); r.Direct.Driver.FailDispose = true;
        try { Assert.False((await r.Apply(await r.Upload(r.Target()))).Success); }
        finally { r.Direct.Driver.FailDispose = false; }
        var correct = r.Application;
        ServiceEnvironment.Write(r.RecordPath, correct with { RequestId = Guid.NewGuid().ToString("N") });
        var created = r.Policy.Created.Count;
        await Assert.ThrowsAsync<IOException>(() => r.Restart()); Assert.Equal(created, r.Policy.Created.Count); Assert.NotNull(r.Journal.Pending);
        ServiceEnvironment.Write(r.RecordPath, correct); await r.Restart(); Assert.Null(r.Journal.Pending); Assert.Equal("rolled-back", r.Application.Phase);
    }

    [Fact]
    public async Task ASettingWriteFailureAfterActivationRestoresThePointerAndClosesTargetFirst()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; FileStream? held = null; Provider? target = null;
        r.Policy.Factory = (_, ids) =>
        {
            if (ids[0] == "sdk")
            {
                held = new(r.RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read); target = r.Policy.New("sdk"); return [target];
            }
            Assert.True(target!.Disposed); held!.Dispose(); held = null; return [r.Policy.New("direct")];
        };
        try
        {
            var result = await r.Apply(await r.Upload(r.Target())); Assert.False(result.Success);
            Assert.Equal(Outcome.Failed, result.Configuration!.Outcome); Assert.Equal(before, r.Store.ReadActivePointer());
            Assert.Equal("rolled-back", r.Application.Phase); Assert.Null(r.Journal.Pending);
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task AFailedSourceFactoryLeavesSourceIntentForColdStartupInsteadOfTryingTargetAgain()
    {
        await using var r = await RoutingRig.Create();
        r.Policy.Factory = (_, ids) =>
        {
            if (ids[0] == "direct") throw new IOException("source construction failed");
            var p = r.Policy.New("sdk"); p.FailPoll = true; return [p];
        };
        var result = await r.Apply(await r.Upload(r.Target())); Assert.False(result.Success);
        Assert.Equal(Outcome.Unknown, result.Configuration!.Outcome); Assert.Equal(ProviderTransitionPhase.StartingSource, r.Journal.Pending!.Phase);
        var count = r.Policy.Created.Count; r.Policy.Factory = (_, ids) => { Assert.DoesNotContain("sdk", ids); return ids.Select(r.Policy.New).ToArray(); };
        await r.Restart(); Assert.True(r.Policy.Created.Count > count); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public async Task DisconnectDuringTargetDiscoveryLeavesAnUnambiguousRecoveryRecord()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation;
        var entered = new TaskCompletionSource(); var proceed = new TaskCompletionSource();
        r.Policy.Factory = (_, ids) =>
        {
            Assert.Equal(new[] { "sdk" }, ids); var p = r.Policy.New("sdk");
            p.Reading = async () => { entered.TrySetResult(); await proceed.Task; }; return [p];
        };
        var id = await r.Upload(r.Target()); var apply = r.Apply(id); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnect = r.Startup.Runtime.DisconnectAsync(r.Hello.Session); proceed.TrySetResult();
        var result = await apply.WaitAsync(TimeSpan.FromSeconds(5)); await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success); Assert.Equal(before, r.Store.ReadActivePointer());
        Assert.Equal(ProviderTransitionPhase.StartingTarget, r.Journal.Pending!.Phase);
        r.Policy.Factory = null; await r.Restart(); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public async Task ChangingTheSdkPortReplacesThatRouteEvenWhenTheProviderIdIsUnchanged()
    {
        await using var r = await RoutingRig.Create(); Assert.True((await r.Apply(await r.Upload(r.Target()))).Success);
        var previous = r.Policy.Created.Last(p => p.ProviderId == "sdk"); var generation = r.Startup.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == "external").Generation;
        r.Policy.Factory = (config, ids) => { Assert.True(previous.Disposed); Assert.Equal(6743, config.Preferences.Providers!.OpenRgbPort); return [r.Policy.New("sdk")]; };
        Assert.True((await r.Apply(await r.Upload(r.Target(port: 6743)))).Success);
        Assert.NotEqual(generation, r.Startup.Runtime.State().Inventory.Devices.Single(d => d.Device.Id == "external").Generation);
        Assert.False(r.Independent.Disposed); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public void ThePackagedFactoryCannotSilentlyIgnoreEnabledOpenRgbSettings()
    {
        using var directory = new ServiceTestDirectory(); var config = ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation);
        config = config with { Preferences = config.Preferences with { Providers = new(true) } };
        Assert.Throws<IOException>(() => DeviceRuntimeComponents.Packaged(JournalRig.Installation, directory.Path, "test", config, new TestLog()));
    }
}
