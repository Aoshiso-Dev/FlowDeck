using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    [Fact]
    public void TransitionBindsBothRoutesAndRecordToTheCompiledDefinition()
    {
        using var j = new JournalRig(); var saved = j.Reload().Pending!;
        Assert.Equal(2, saved.Version); Assert.Equal(FixtureRoutingDefinition, saved.Definition);
        var changed = FixtureRoutingDefinition with { Digest = new('b', 64) };
        // Rehashing the outer record cannot rebind route fingerprints to a new policy.
        j.Write(saved with { Definition = changed });
        Assert.Throws<IOException>(() => new DeviceProviderTransition(j.Store, JournalRig.Installation, changed));
        Assert.True(File.Exists(j.Path));
    }

    [Theory]
    [InlineData("id")] [InlineData("revision")] [InlineData("digest")] [InlineData("missing")]
    public async Task AChangedOrMissingPolicyCannotOpenAnyProviderDuringColdRecovery(string change)
    {
        using var j = new JournalRig(); var before = File.ReadAllBytes(j.Path);
        var policy = new RoutingPolicy(new()) { Definition = change switch {
            "id" => FixtureRoutingDefinition with { Id = "different-policy" },
            "revision" => FixtureRoutingDefinition with { Revision = 2 },
            "digest" => FixtureRoutingDefinition with { Digest = new('b', 64) },
            _ => FixtureRoutingDefinition } };
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(j.Store, JournalRig.Installation,
            _ => { calls++; return new([], []); }, ["user"], new TestLog(), demo: true, providerRouting: change == "missing" ? null : policy));
        Assert.Equal(0, calls); Assert.Equal(before, File.ReadAllBytes(j.Path)); Assert.Equal(j.Before, j.Store.ReadActivePointer());
    }

    [Theory]
    [InlineData("id")] [InlineData("revision")] [InlineData("digest")]
    public async Task InvalidCompiledPolicyMetadataIsRejectedBeforeTheInitialFactory(string change)
    {
        using var directory = new ServiceTestDirectory(); var store = DeviceConfigurationCodec.Store(directory.Path);
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(JournalRig.Installation))));
        var policy = new RoutingPolicy(new()) { Definition = change switch {
            "id" => FixtureRoutingDefinition with { Id = "" },
            "revision" => FixtureRoutingDefinition with { Revision = 0 },
            _ => FixtureRoutingDefinition with { Digest = "not-a-digest" } } };
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateAsync(store, JournalRig.Installation,
            () => { calls++; return new([], []); }, ["user"], new TestLog(), demo: true, providerRouting: policy));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AnUnspecifiedPolicyCanInspectIntentButCannotAdvanceOrRecoverIt()
    {
        using var j = new JournalRig(); var inspect = new DeviceProviderTransition(j.Store, JournalRig.Installation);
        Assert.Equal(j.Journal.Pending, inspect.Pending);
        Assert.Throws<IOException>(() => inspect.Check(inspect.Pending!));
        Assert.Throws<IOException>(() => inspect.Recovery());
        Assert.Throws<IOException>(() => inspect.Complete(inspect.Pending!));
        Assert.True(File.Exists(j.Path));
    }

    [Fact]
    public async Task APreviousFormatWithoutAPolicyIsPreservedForTheOriginalServiceToRecover()
    {
        using var j = new JournalRig(); var saved = j.Journal.Pending!;
        j.Write(saved with { Version = 1 }); var before = File.ReadAllBytes(j.Path); var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(j.Store, JournalRig.Installation,
            _ => { calls++; return new([], []); }, ["user"], new TestLog(), demo: true, providerRouting: new RoutingPolicy(new())));
        Assert.Equal(0, calls); Assert.Equal(before, File.ReadAllBytes(j.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PolicyDriftBeforeOrDuringPlanningDoesNotReleaseTheOldHardware(bool duringPlanning)
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; var releases = r.Direct.Driver.Releases;
        var id = await r.Upload(r.Target(), validate: false);
        void Change() => r.Policy.Definition = FixtureRoutingDefinition with { Revision = 2 };
        if (duringPlanning) r.Policy.Planner = (_, _) => { Change(); return new(["direct"], ["sdk"]); };
        else Change();
        var reply = await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id));
        Assert.False(reply.Success); Assert.Equal(releases, r.Direct.Driver.Releases); Assert.False(r.Direct.Disposed);
        Assert.Equal(before, r.Store.ReadActivePointer()); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public async Task PolicyDriftAfterValidationCannotAdoptThePreviouslyValidDraft()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; var releases = r.Direct.Driver.Releases;
        var id = await r.Upload(r.Target()); r.Policy.Definition = FixtureRoutingDefinition with { Digest = new('c', 64) };
        var reply = await r.Apply(id);
        Assert.False(reply.Success); Assert.Equal(releases, r.Direct.Driver.Releases); Assert.False(r.Direct.Disposed);
        Assert.Equal(before, r.Store.ReadActivePointer()); Assert.Null(r.Journal.Pending);
    }

    [Fact]
    public async Task PolicyDriftAfterOldHandlesCloseCannotOpenTargetOrReinterpretRollback()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation; var calls = 0;
        r.Direct.Driver.Closing = () => { r.Policy.Definition = FixtureRoutingDefinition with { Revision = 2 }; return Task.CompletedTask; };
        r.Policy.Factory = (_, _) => { calls++; return []; };
        var id = await r.Upload(r.Target()); var reply = await r.Apply(id);
        Assert.False(reply.Success); Assert.Equal(Outcome.Unknown, reply.Result!.Outcome); Assert.Equal(0, calls);
        Assert.True(r.Direct.Disposed); Assert.False(r.Independent.Disposed); Assert.Equal(before, r.Store.ReadActivePointer());
        Assert.NotNull(r.Journal.Pending); Assert.Equal(FixtureRoutingDefinition, r.Journal.Pending!.Definition);
    }

    [Theory]
    [InlineData("factory")] [InlineData("poll")] [InlineData("close")]
    public async Task PolicyDriftDuringRecoveryClosesTheCurrentInstanceAndNeverStartsTheNextRoute(string point)
    {
        await using var r = new Rig(); using var j = new JournalRig(); await j.Switch(r, () => [r.New()]); await r.DisposeAsync();
        var policy = new RoutingPolicy(r.Events); var calls = 0; Provider? opened = null;
        void Change() => policy.Definition = FixtureRoutingDefinition with { Revision = 2 };
        await Assert.ThrowsAsync<IOException>(() => DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(j.Store, JournalRig.Installation,
            config =>
            {
                Assert.True(config.Preferences.Providers!.OpenRgbEnabled); calls++; opened = r.New();
                if (point == "factory") Change();
                if (point == "poll") opened.Reading = () => { Change(); return Task.CompletedTask; };
                if (point == "close") opened.Driver.Closing = () => { Change(); return Task.CompletedTask; };
                return new([], [opened]);
            }, ["user"], new TestLog(), demo: true, providerRouting: policy));
        Assert.Equal(1, calls); Assert.True(opened!.Disposed); Assert.True(opened.Driver.Disposed);
        Assert.Equal(ProviderTransitionRecovery.TargetThenSource, j.Reload().Recovery()); Assert.Equal(j.Before, j.Store.ReadActivePointer());
    }
}
