using FlowDeck.Infrastructure;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private sealed class JournalRig : IDisposable
    {
        public const string Installation = "0123456789abcdef0123456789abcdef";
        private readonly ServiceTestDirectory directory = new();
        public readonly ConfigurationGenerationStore Store;
        public readonly ConfigurationPointer Before;
        public readonly ConfigurationActivation After;
        public readonly DeviceProviderTransition Journal;
        public string Path => System.IO.Path.Combine(Store.DirectoryPath, DeviceProviderTransition.FileName);
        public JournalRig()
        {
            Store = DeviceConfigurationCodec.Store(directory.Path);
            var initial = ServiceRuntimeBootstrap.MonitoringConfiguration(Installation);
            var before = Store.Stage(DeviceConfigurationCodec.Write(initial)); Store.Activate(before); Before = before.Target;
            var next = initial with { Preferences = initial.Preferences with { Providers = new(true, 6742) } };
            After = Store.Stage(DeviceConfigurationCodec.Write(next));
            Journal = Reload(); Journal.Begin(Guid.NewGuid().ToString("N"), Before, After.Target);
        }
        public DeviceProviderTransition Reload() => new(Store, Installation, FixtureRoutingDefinition);
        public Task Switch(Rig r, Func<IDeviceDriverProvider[]> create, bool rollback = false) =>
            r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
                (await Journal.SwitchAsync(r.Runtime, r.Hello.Session, rollback ? ["sdk"] : ["direct"], create, ct, rollback))
                    .OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray());
        public Task<DeviceRuntimeStartup> Startup(Rig r, Func<bool, Provider>? create = null, CancellationToken ct = default) =>
            DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(Store, Installation, config =>
            {
                var target = config.Preferences.Providers?.OpenRgbEnabled == true;
                var provider = create?.Invoke(target) ?? r.New(target ? "sdk" : "direct", target ? "external" : "rgb");
                return new([], [provider]);
            }, ["user"], new TestLog(), new TestClock(), demo: true, ct: ct, providerRouting: new RoutingPolicy(r.Events));
        public void Write(ProviderTransitionRecord value)
        {
            value = value with { Hash = SettingsTransfer.Hash(value with { Hash = "" }) };
            ServiceEnvironment.Write(Path, value);
        }
        public void Dispose() => directory.Dispose();
    }

    [Fact]
    public async Task DurableIntentPrecedesTargetFactoryAndCommitRequiresTheNewActiveGeneration()
    {
        await using var r = new Rig(); using var j = new JournalRig(); var prepared = j.Journal.Pending!;
        await j.Switch(r, () =>
        {
            Assert.True(r.Direct.Disposed); Assert.True(r.Direct.Driver.Disposed);
            Assert.Equal(ProviderTransitionPhase.StartingTarget, j.Reload().Pending!.Phase);
            Assert.Equal(j.Before, j.Store.ReadActivePointer()); return [r.New()];
        });
        var ready = j.Journal.Pending!;
        Assert.Equal(ProviderTransitionPhase.TargetReady, ready.Phase);
        Assert.Throws<IOException>(() => j.Journal.Commit(ready));
        Assert.Throws<IOException>(() => j.Journal.Complete(ready));
        j.Store.Activate(j.After);
        Assert.Equal(ProviderTransitionRecovery.Target, j.Reload().Recovery());
        var committed = j.Journal.Commit(ready);
        Assert.Throws<IOException>(() => j.Journal.Check(prepared));
        j.Journal.Complete(committed); Assert.False(File.Exists(j.Path)); Assert.Null(j.Journal.Pending);
    }

    [Fact]
    public async Task FailedFactoryKeepsTheTargetRecoveryObligationAndRefusesASecondForwardSwitch()
    {
        await using var r = new Rig(); using var j = new JournalRig();
        await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => throw new IOException("factory failed")));
        Assert.True(r.Direct.Disposed); Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery());
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => { calls++; return [r.New()]; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AFailedCloseRetainsTheSourceRecordWithoutStartingTarget(bool providerFailure)
    {
        await using var r = new Rig(); using var j = new JournalRig();
        r.Direct.FailDispose = providerFailure; r.Direct.Driver.FailDispose = !providerFailure; var calls = 0;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => { calls++; return [r.New()]; }));
            Assert.Equal(0, calls); Assert.Equal(ProviderTransitionPhase.Prepared, j.Reload().Pending!.Phase);
            Assert.Equal(ProviderTransitionRecovery.Source, j.Reload().Recovery());
        }
        finally { r.Direct.FailDispose = r.Direct.Driver.FailDispose = false; }
    }

    [Fact]
    public async Task AFailedDiscoveryRemainsUncommittedUntilExplicitRollbackClosesTarget()
    {
        await using var r = new Rig(); using var j = new JournalRig(); var next = r.New(); next.FailPoll = true;
        await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => [next]));
        Assert.Equal(ProviderTransitionPhase.StartingTarget, j.Reload().Pending!.Phase);
        await j.Switch(r, () =>
        {
            Assert.True(next.Disposed); Assert.True(next.Driver.Disposed);
            Assert.Equal(ProviderTransitionPhase.StartingSource, j.Reload().Pending!.Phase);
            return [r.New("direct", "rgb")];
        }, rollback: true);
        Assert.Equal(ProviderTransitionPhase.SourceReady, j.Journal.Pending!.Phase);
        var finished = j.Journal.ConfirmRollback(j.Journal.Pending!); j.Journal.Complete(finished);
        Assert.Equal(j.Before, j.Store.ReadActivePointer()); Assert.False(File.Exists(j.Path));
    }

    [Fact]
    public async Task FailedIntentPersistenceCannotInvokeTheTargetFactory()
    {
        await using var r = new Rig(); using var j = new JournalRig(); var calls = 0;
        using (var locked = new FileStream(j.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => { calls++; return [r.New()]; }));
        Assert.Equal(0, calls); Assert.False(r.Direct.Disposed);
        Assert.Equal(ProviderTransitionPhase.Prepared, j.Reload().Pending!.Phase);
    }

    [Fact]
    public async Task FailureToWriteAfterOldHandlesCloseStillPreventsTheNewFactory()
    {
        await using var r = new Rig(); using var j = new JournalRig(); var calls = 0; FileStream? held = null;
        r.Direct.Driver.Closing = () => { held = new(j.Path, FileMode.Open, FileAccess.Read, FileShare.Read); return Task.CompletedTask; };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => j.Switch(r, () => { calls++; return [r.New()]; }));
            Assert.Equal(0, calls); Assert.True(r.Direct.Disposed); Assert.Equal(ProviderTransitionPhase.Prepared, j.Reload().Pending!.Phase);
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task AnInvalidForwardRetryDoesNotCloseTheActiveTarget()
    {
        await using var r = new Rig(); using var j = new JournalRig(); var next = r.New(); await j.Switch(r, () => [next]);
        await r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
        {
            await Assert.ThrowsAsync<IOException>(() => j.Journal.SwitchAsync(r.Runtime, r.Hello.Session, ["sdk"], () => [], ct));
            return Array.Empty<PhysicalDeviceBinding>();
        });
        Assert.False(next.Disposed); Assert.Equal(ProviderTransitionPhase.TargetReady, j.Reload().Pending!.Phase);
    }

    [Fact]
    public async Task AConcurrentRollbackCannotAdvanceTheRecordWhileOldHandlesAreClosing()
    {
        await using var r = new Rig(); using var j = new JournalRig();
        var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var change = j.Switch(r, () => [r.New()]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => j.Journal.SwitchAsync(r.Runtime, r.Hello.Session, ["sdk"], () => [], default, rollback: true));
            Assert.Equal(ProviderTransitionPhase.Prepared, j.Reload().Pending!.Phase);
        }
        finally { resume.TrySetResult(); }
        await change.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(ProviderTransitionPhase.TargetReady, j.Reload().Pending!.Phase);
    }

    [Fact]
    public async Task AStaleActivePointerIsRejectedBeforeOldHandlesAreClosed()
    {
        await using var r = new Rig(); using var j = new JournalRig(); j.Store.Activate(j.After);
        await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => [r.New()]));
        Assert.False(r.Direct.Disposed); Assert.Throws<IOException>(() => j.Reload().Recovery());
    }

    [Fact]
    public async Task CancellationAfterTargetDiscoveryLeavesAConservativeRecord()
    {
        await using var r = new Rig(); using var j = new JournalRig(); var next = r.New();
        var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        next.Reading = async () => { entered.TrySetResult(); await resume.Task; };
        var change = j.Switch(r, () => [next]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var end = r.Runtime.DisconnectAsync(r.Hello.Session); resume.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change.WaitAsync(TimeSpan.FromSeconds(5)));
        await end.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderTransitionPhase.StartingTarget, j.Reload().Pending!.Phase);
    }

    [Fact]
    public async Task DefaultStartupCannotIgnoreAPendingRouteRecord()
    {
        using var j = new JournalRig(); var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateAsync(j.Store, JournalRig.Installation,
            () => { calls++; return new([], []); }, ["user"], new TestLog(), demo: true));
        Assert.Equal(0, calls); Assert.True(File.Exists(j.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ColdStartupRecoversAndClosesUncommittedTargetBeforeSource(bool targetReady)
    {
        await using var r = new Rig(); using var j = new JournalRig();
        if (targetReady) await j.Switch(r, () => [r.New()]);
        else await Assert.ThrowsAsync<IOException>(() => j.Switch(r, () => throw new IOException("interrupted")));
        await r.DisposeAsync();
        var opened = new List<Provider>();
        await using var startup = await j.Startup(r, target =>
        {
            if (opened.Count > 0) { Assert.True(opened[^1].Disposed); Assert.True(opened[^1].Driver.Disposed); }
            Assert.Equal(opened.Count == 0, target);
            if (opened.Count == 1) Assert.Equal(ProviderTransitionPhase.StartingSource, j.Reload().Pending!.Phase);
            var provider = r.New(target ? "sdk" : "direct", target ? "external" : "rgb"); opened.Add(provider); return provider;
        });
        Assert.Equal(3, opened.Count); Assert.False(opened[2].Disposed); Assert.False(File.Exists(j.Path));
        Assert.Equal(j.Before, startup.Generation); Assert.All(opened, p => Assert.Equal(0, p.Driver.Writes));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActiveTargetIsRecoveredWithoutOpeningSourceEvenIfCommitMarkerWasInterrupted(bool committed)
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]);
        j.Store.Activate(j.After); if (committed) j.Journal.Commit(j.Journal.Pending!); await r.DisposeAsync();
        var opened = new List<Provider>();
        await using var startup = await j.Startup(r, target =>
        {
            Assert.True(target); if (opened.Count > 0) Assert.True(opened[0].Disposed);
            var p = r.New(); opened.Add(p); return p;
        });
        Assert.Equal(2, opened.Count); Assert.Equal(j.After.Target, startup.Generation); Assert.False(File.Exists(j.Path));
    }

    [Fact]
    public async Task AnUnstartedTransitionRecoversSourceOnlyAndKeepsSavedPoliciesStopped()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await r.DisposeAsync(); var calls = 0;
        await using var startup = await j.Startup(r, target => { Assert.False(target); calls++; return r.New("direct", "rgb"); });
        Assert.Equal(2, calls); Assert.Null(startup.Runtime.State().LifecycleError); Assert.False(File.Exists(j.Path));
        Assert.All(startup.Runtime.State().Endpoints, e => Assert.False(e.Owned));
    }

    [Theory]
    [InlineData("poll")] [InlineData("release")] [InlineData("driver-close")] [InlineData("provider-close")]
    public async Task RecoveryFailureNeverOpensTheOtherRouteAndPreservesItsJournal(string failure)
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        var calls = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => j.Startup(r, target =>
        {
            Assert.True(target); calls++; var p = r.New(); p.FailPoll = failure == "poll";
            p.Driver.FailRelease = failure == "release"; p.Driver.FailDispose = failure == "driver-close";
            p.FailDispose = failure == "provider-close"; return p;
        }));
        Assert.Equal(1, calls); Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery());
    }

    [Fact]
    public async Task AnInterruptedSourceRecoveryDoesNotReopenTheAlreadyClosedTargetOnRetry()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        var routes = new List<bool>();
        await Assert.ThrowsAsync<IOException>(() => j.Startup(r, target =>
        {
            routes.Add(target); var p = r.New(target ? "sdk" : "direct", target ? "external" : "rgb"); p.FailPoll = !target; return p;
        }));
        Assert.Equal(new[] { true, false }, routes); Assert.Equal(ProviderTransitionPhase.StartingSource, j.Reload().Pending!.Phase);
        routes.Clear();
        await using var startup = await j.Startup(r, target => { routes.Add(target); return r.New("direct", "rgb"); });
        Assert.Equal(new[] { false, false }, routes); Assert.False(File.Exists(j.Path));
    }

    [Fact]
    public async Task AReleaseFailureDuringRecoveryShutdownPreventsSourceStartup()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        var calls = 0; Provider? opened = null;
        await Assert.ThrowsAsync<IOException>(() => j.Startup(r, target =>
        {
            Assert.True(target); calls++; opened = r.New(); opened.Driver.RejectRelease = reason => reason == DeviceReleaseReason.ServiceStopping; return opened;
        }));
        Assert.Equal(1, calls); Assert.True(opened!.Disposed); Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery());
    }

    [Fact]
    public async Task GenerationChangeDuringRecoveryCannotSwitchBackToSource()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => j.Startup(r, target =>
        {
            Assert.True(target); calls++; var p = r.New();
            p.Driver.Closing = () => { j.Store.Activate(j.After); return Task.CompletedTask; }; return p;
        }));
        Assert.Equal(1, calls); Assert.Equal(ProviderTransitionRecovery.Target, j.Reload().Recovery());
        await using var startup = await j.Startup(r, target => { Assert.True(target); return r.New(); });
        Assert.Equal(j.After.Target, startup.Generation); Assert.False(File.Exists(j.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedRecoveryMarkerOrDeletionKeepsTheRecordAndDoesNotOpenSource(bool committed)
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]);
        j.Store.Activate(j.After); if (committed) j.Journal.Commit(j.Journal.Pending!); await r.DisposeAsync();
        FileStream? held = null; var calls = 0;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => j.Startup(r, target =>
            {
                Assert.True(target); calls++; var p = r.New();
                p.Driver.Closing = () => { held = new(j.Path, FileMode.Open, FileAccess.Read, FileShare.Read); return Task.CompletedTask; }; return p;
            }));
            Assert.Equal(1, calls); Assert.True(File.Exists(j.Path));
            Assert.Equal(committed ? ProviderTransitionRecovery.FinalizedTarget : ProviderTransitionRecovery.Target, j.Reload().Recovery());
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task CancelledColdRecoveryClosesTargetAndRetainsItsObligation()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        using var cancellation = new CancellationTokenSource(); Provider? opened = null; var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => j.Startup(r, target =>
        {
            Assert.True(target); calls++; opened = r.New(); opened.Reading = () => { cancellation.Cancel(); return Task.CompletedTask; }; return opened;
        }, cancellation.Token));
        Assert.Equal(1, calls); Assert.True(opened!.Disposed); Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery());
    }

    [Fact]
    public async Task FailedApplicationFinalizationRetainsTheRecoveredRouteUntilBothRecordsAgree()
    {
        await using var r = new Rig(); using var j = new JournalRig(); await r.DisposeAsync();
        var pending = j.Journal.Pending!; var applicationPath = System.IO.Path.Combine(j.Store.DirectoryPath, "configuration-application.json");
        ServiceEnvironment.Write(applicationPath, new ConfigurationApplication(1, pending.Request, pending.Before, pending.After, "accepted"));
        FileStream? held = null; var calls = 0;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => j.Startup(r, target =>
            {
                Assert.False(target); calls++; var p = r.New("direct", "rgb");
                p.Driver.Closing = () => { held = new(applicationPath, FileMode.Open, FileAccess.Read, FileShare.Read); return Task.CompletedTask; }; return p;
            }));
            Assert.Equal(1, calls); Assert.Equal(ProviderTransitionPhase.RecoveredSource, j.Reload().Pending!.Phase);
            Assert.Equal("accepted", ServiceEnvironment.Read<ConfigurationApplication>(applicationPath).Phase);
        }
        finally { held?.Dispose(); }
        await using var startup = await j.Startup(r);
        Assert.Null(j.Reload().Pending); Assert.Equal("rolled-back", ServiceEnvironment.Read<ConfigurationApplication>(applicationPath).Phase);
    }

    [Theory]
    [InlineData("hash")] [InlineData("revision")] [InlineData("phase")] [InlineData("route")]
    [InlineData("installation")] [InlineData("missing-generation")] [InlineData("foreign-active")]
    public async Task InvalidJournalOrConfigurationNeverOpensAnyProvider(string fault)
    {
        await using var r = new Rig(); using var j = new JournalRig(); var value = j.Journal.Pending!;
        switch (fault)
        {
            case "hash": ServiceEnvironment.Write(j.Path, value with { Hash = new('b', 64) }); break;
            case "revision": j.Write(value with { Revision = 3 }); break;
            case "phase": j.Write(value with { Phase = (ProviderTransitionPhase)99 }); break;
            case "route": j.Write(value with { AfterRoute = new('b', 64) }); break;
            case "installation": j.Write(value with { Installation = Guid.NewGuid().ToString("N") }); break;
            case "missing-generation": j.Write(value with { After = value.After with { Id = Guid.NewGuid().ToString("N") } }); break;
            case "foreign-active": j.Store.Activate(j.Store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation)))); break;
        }
        var calls = 0;
        await Assert.ThrowsAnyAsync<IOException>(() => j.Startup(r, _ => { calls++; return r.New(); }));
        Assert.Equal(0, calls); Assert.True(File.Exists(j.Path));
    }

    [Fact]
    public void ASameRouteGenerationCannotBeMisrepresentedAsAProviderTransition()
    {
        using var directory = new ServiceTestDirectory(); var store = DeviceConfigurationCodec.Store(directory.Path);
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation);
        var before = store.Stage(DeviceConfigurationCodec.Write(config)); store.Activate(before);
        var after = store.Stage(DeviceConfigurationCodec.Write(config)); var journal = new DeviceProviderTransition(store, JournalRig.Installation, FixtureRoutingDefinition);
        Assert.Throws<IOException>(() => journal.Begin(Guid.NewGuid().ToString("N"), before.Target, after.Target)); Assert.Null(journal.Pending);
    }
}
