using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    private static DeviceProviderIsolation Isolation => new(["direct", "sdk"], ["device:rgb", "device:external"]);
    private static Task IsolatedChange(Rig r, Func<IDeviceDriverProvider[]> create) => r.Runtime.ReconfigureAsync(r.Hello.Session,
        r.Runtime.State().Inventory.TopologyRevision, async (_, ct) =>
            (await r.Runtime.ReplaceProvidersAsync(r.Hello.Session, ["direct"], create, ct, ["sdk"]))
                .OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray(), Isolation);

    [Fact]
    public async Task IsolatedReplacementRetainsOwnershipRevisionAndPollingDuringSlowClose()
    {
        await using var r = new Rig(); Assert.True((await r.Runtime.ExecuteAsync(r.Request("cooler"))).Success);
        var before = r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "cooler");
        var entered = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        r.Direct.Driver.Closing = async () => { entered.TrySetResult(); await resume.Task; };
        var next = r.New(); var change = IsolatedChange(r, () => [next]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var polls = r.Independent.Driver.Polls; await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
            Assert.True(r.Independent.Driver.Polls > polls);
            Assert.True(r.Independent.Driver.Owned); Assert.Equal(0, r.Independent.Driver.Releases);
            Assert.Equal(before, r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "cooler"));
        }
        finally { resume.TrySetResult(); }
        await change.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before, r.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "cooler"));
        Assert.Equal(0, r.Independent.Driver.Releases); Assert.Equal(0, r.Independent.Disposes);
    }

    [Theory]
    [InlineData("retained-overlap")] [InlineData("missing-old-domain")] [InlineData("duplicate-domain")]
    public async Task UnprovenIsolationRejectsBeforeAnyRelease(string fault)
    {
        await using var r = new Rig(); var scope = Isolation;
        if (fault == "retained-overlap") r.Independent.Driver.Domain = "device:rgb";
        if (fault == "missing-old-domain") scope = scope with { ResourceDomains = ["device:external"] };
        if (fault == "duplicate-domain") scope = scope with { ResourceDomains = ["device:rgb", "device:rgb"] };
        await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision,
            (_, _) => throw new InvalidOperationException("must not run"), scope));
        Assert.Equal(0, r.Direct.Driver.Releases); Assert.Equal(0, r.Independent.Driver.Releases);
        Assert.False(r.Runtime.State().ConfigurationChanging);
    }

    [Fact]
    public async Task NewProviderOutsideDeclaredResourcesClosesBeforePollingWithoutStoppingIndependentControl()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(r.Request("cooler"));
        var next = r.New(); next.Driver.Domain = "device:cooler";
        await Assert.ThrowsAsync<IOException>(() => IsolatedChange(r, () => [next]));
        Assert.Equal(0, next.Polls); Assert.Equal(0, next.Driver.Polls); Assert.True(next.Disposed);
        Assert.True(r.Independent.Driver.Owned); Assert.Equal(0, r.Independent.Driver.Releases);
        Assert.Equal(Outcome.Confirmed, (await r.Runtime.ExecuteAsync(r.Request("cooler") with { Operation = DeviceOperation.ReleaseCooling, Duty = null })).Result!.Outcome);
    }

    [Fact]
    public async Task DiscoveryCannotExpandIntoAnIndependentDriverBeforeTheNewDevicePoll()
    {
        await using var r = new Rig(); var next = r.New();
        next.Reading = () => { next.Driver.Domain = "device:cooler"; return Task.CompletedTask; };
        await Assert.ThrowsAsync<IOException>(() => IsolatedChange(r, () => [next]));
        Assert.Equal(1, next.Polls); Assert.Equal(0, next.Driver.Polls); Assert.Equal(0, r.Independent.Driver.Releases);
    }

    [Fact]
    public async Task ScopedCallbackCannotReplaceAnotherProviderOrAlterItsBinding()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(r.Request("cooler"));
        await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision,
            async (_, ct) =>
            {
                await Assert.ThrowsAsync<IOException>(() => r.Runtime.ReplaceProvidersAsync(r.Hello.Session, ["independent"], () => [], ct, []));
                return [r.Independent.Driver.BindingCandidate with { Fingerprint = new('b', 64) }];
            }, Isolation));
        Assert.Equal(0, r.Independent.Disposes); Assert.Equal(0, r.Independent.Driver.Releases);
        Assert.True((await r.Runtime.ExecuteAsync(r.Request("cooler") with { Operation = DeviceOperation.ReleaseCooling, Duty = null })).Success);
    }

    [Fact]
    public async Task ScopeArraysAreCopiedBeforeAnAsynchronousCallbackCanMutateThem()
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(r.Request("cooler")); var scope = Isolation;
        await r.Runtime.ReconfigureAsync(r.Hello.Session, r.Runtime.State().Inventory.TopologyRevision, (_, _) =>
        {
            scope.ProviderIds[0] = "independent"; scope.ResourceDomains[0] = "device:cooler";
            return Task.FromResult(new[] { r.Direct.Driver.BindingCandidate, r.Independent.Driver.BindingCandidate });
        }, scope);
        Assert.True(r.Independent.Driver.Owned); Assert.Equal(0, r.Independent.Driver.Releases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedReleaseOrCloseKeepsIndependentControl(bool close)
    {
        await using var r = new Rig(); await r.Runtime.ExecuteAsync(r.Request("rgb")); await r.Runtime.ExecuteAsync(r.Request("cooler"));
        r.Direct.Driver.FailRelease = !close; r.Direct.Driver.FailDispose = close;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => IsolatedChange(r, () => throw new InvalidOperationException("must not open")));
            Assert.True(r.Independent.Driver.Owned); Assert.Equal(0, r.Independent.Driver.Releases);
        }
        finally { r.Direct.Driver.FailRelease = r.Direct.Driver.FailDispose = false; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConfigurationRouteReviewAndRollbackRetainTheIndependentControl(bool failTarget)
    {
        await using var r = await RoutingRig.Create();
        r.Policy.Planner = (_, _) => new(["direct"], ["sdk"], Isolation.ResourceDomains);
        var state = r.Startup.Runtime.State(); var endpoint = state.Endpoints.Single(e => e.Target.DeviceId == "cooler");
        var command = r.Request(new(DeviceConfigurationOperation.Snapshot)) with { Operation = DeviceOperation.SetDuty, Configuration = null,
            TopologyRevision = state.Inventory.TopologyRevision, Targets = [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], Duty = 60 };
        Assert.True((await r.Startup.Runtime.ExecuteAsync(command)).Success);
        var releases = r.Independent.Driver.Releases;
        var prior = r.Startup.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "cooler");
        r.Policy.Factory = (_, ids) => ids.Select(id => { var p = r.Policy.New(id); p.FailPoll = failTarget && id == "sdk"; return (IDeviceDriverProvider)p; }).ToArray();
        var transfer = await r.Upload(r.Target(), validate: false);
        var review = (await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, transfer))).Configuration!.ProviderRoute!;
        Assert.False(review.StopsAllControls); Assert.Equal(["cooler"], review.ContinuingDeviceIds!);
        var result = await r.Apply(transfer); Assert.Equal(!failTarget, result.Success);
        Assert.True(r.Independent.Driver.Owned); Assert.Equal(releases, r.Independent.Driver.Releases);
        Assert.Equal(prior, r.Startup.Runtime.State().Endpoints.Single(e => e.Target.DeviceId == "cooler"));
        Assert.Equal(failTarget ? "rolled-back" : "applied", r.Application.Phase);
    }

    [Fact]
    public async Task CombinedConfigurationChangeRetainsTheFullStopReview()
    {
        await using var r = await RoutingRig.Create(); r.Policy.Planner = (_, _) => new(["direct"], ["sdk"], Isolation.ResourceDomains);
        var target = r.Target(); target = target with { Devices = target.Devices with {
            Devices = target.Devices.Devices.Select(d => d with { Name = d.Name + " edited" }).ToArray() } };
        var transfer = await r.Upload(target, validate: false);
        var result = await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, transfer)); Assert.True(result.Success, result.Error);
        Assert.True(result.Configuration!.ProviderRoute!.StopsAllControls); Assert.Null(result.Configuration.ProviderRoute.ContinuingDeviceIds);
    }

    [Fact]
    public async Task AChangedResourceBoundaryInvalidatesTheReviewBeforeReleaseOrStaging()
    {
        await using var r = await RoutingRig.Create(); var domains = Isolation.ResourceDomains;
        r.Policy.Planner = (_, _) => new(["direct"], ["sdk"], domains);
        var transfer = await r.Upload(r.Target()); var generation = r.Startup.Generation;
        var releases = r.Direct.Driver.Releases; var files = Directory.GetDirectories(r.Store.DirectoryPath).Length;
        domains = [.. domains, "newly-declared-bus"];
        var result = await r.Apply(transfer);
        Assert.False(result.Success); Assert.Equal(generation, r.Startup.Generation); Assert.Null(r.Journal.Pending);
        Assert.Equal(releases, r.Direct.Driver.Releases); Assert.False(r.Direct.Disposed);
        Assert.Equal(files, Directory.GetDirectories(r.Store.DirectoryPath).Length);
    }
}
