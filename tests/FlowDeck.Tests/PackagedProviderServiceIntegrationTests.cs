using System.Security.Cryptography;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class PackagedProviderRoutingTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ServiceConfigurationUsesCompiledPlanCommitsOrRollsBackAndKeepsUnrelatedProviders(bool failDiscovery)
    {
        using var dir = new ServiceTestDirectory(); var f = new Factories();
        var store = DeviceConfigurationCodec.Store(dir.Path); store.Activate(store.Stage(DeviceConfigurationCodec.Write(Config())));
        await using var startup = await DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(store, Installation, f.Policy.CreateInitial,
            ["user"], new TestLog(), new TestClock(), demo: true, providerRouting: f.Policy);
        var initial = f.Created.ToArray(); var before = startup.Generation;
        var hello = startup.Runtime.OpenSession("user"); long sequence = 0;
        Task<DeviceServiceReply> Send(DeviceConfigurationRequest request) => startup.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session,
            Guid.NewGuid().ToString("N"), ++sequence, "test", DeviceOperation.Configuration, Configuration: request));
        Assert.Contains(DeviceProviderRouteReview.Feature, hello.Features);
        Assert.Contains(DeviceProviderRouteReview.SelectionFeature, hello.Features);
        f.SdkOverride = (config, check) =>
        {
            Assert.True(initial.Single(p => p.ProviderId == PackagedProviderRouting.DirectId).Closed);
            Assert.True(config.Preferences.Providers!.OpenRgbEnabled);
            var provider = f.New(OpenRgbDeviceProvider.Provider, ["usb:asrock-native", "sdk:openrgb-loopback"]);
            provider.CheckRoute = check; provider.FailPoll = failDiscovery; return Factories.Own(provider);
        };
        var target = startup.Configuration with { Preferences = startup.Configuration.Preferences with { Providers = new(true) } };
        var bytes = DeviceConfigurationCodec.SerializePackage(target);
        var begin = await Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: before.Id,
            TotalBytes: bytes.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))));
        Assert.True(begin.Success, begin.Error); var id = begin.Configuration!.TransferId!;
        for (var offset = 0; offset < bytes.Length;)
        {
            var chunk = bytes.AsSpan(offset, Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length-offset)).ToArray();
            var appended = await Send(new(DeviceConfigurationOperation.Append, id, Offset: offset, Data: chunk));
            Assert.True(appended.Success, appended.Error); offset += chunk.Length;
        }
        var preview = await Send(new(DeviceConfigurationOperation.ValidateProviderRoute,id)); Assert.True(preview.Success,preview.Error);
        var plan = preview.Configuration!.ProviderRoute!;
        Assert.Equal(f.Policy.Definition.Digest, plan.PolicyDigest); Assert.False(plan.StopsAllControls);
        Assert.Equal(new[] { HubDeviceDriver.Provider, HardwareSensorProvider.ProviderName, "ene-memory" }.Order(StringComparer.Ordinal), plan.RetainedProviderIds);
        Assert.All(initial, p => Assert.False(p.Closed)); Assert.Equal(before, store.ReadActivePointer());
        var applied = await Send(new(DeviceConfigurationOperation.Apply,id,TopologyRevision:startup.Runtime.State().Inventory.TopologyRevision));
        Assert.Equal(!failDiscovery, applied.Success);
        if (failDiscovery)
        {
            Assert.Equal(Outcome.Failed, applied.Configuration!.Outcome); Assert.Equal(before, startup.Generation);
            Assert.False(startup.Configuration.Preferences.Providers!.OpenRgbEnabled);
            Assert.True(f.Created.Single(p => p.ProviderId == OpenRgbDeviceProvider.Provider).Closed);
            Assert.Equal(2, f.Created.Count(p => p.ProviderId == PackagedProviderRouting.DirectId));
        }
        else
        {
            Assert.NotEqual(before, startup.Generation); Assert.True(startup.Configuration.Preferences.Providers!.OpenRgbEnabled);
        }
        Assert.Equal(startup.Generation, store.ReadActivePointer());
        Assert.Null(new DeviceProviderTransition(store, Installation, f.Policy.Definition).Pending);
        Assert.All(initial.Where(p => p.ProviderId != PackagedProviderRouting.DirectId), p => { Assert.False(p.Closed); Assert.Equal(0,p.Closes); });
        var old = initial.Single(p => p.ProviderId == PackagedProviderRouting.DirectId); var oldPolls = old.Polls;
        await startup.Runtime.PollAsync(); Assert.Equal(oldPolls, old.Polls);
    }

    [Fact]
    public async Task InterruptedPreparedRouteUsesSamePolicyToRecoverCloseAndThenStart()
    {
        using var dir = new ServiceTestDirectory(); var f = new Factories(); var store = DeviceConfigurationCodec.Store(dir.Path);
        var before = store.Stage(DeviceConfigurationCodec.Write(Config())); store.Activate(before);
        var after = store.Stage(DeviceConfigurationCodec.Write(Config(true)));
        var journal = new DeviceProviderTransition(store, Installation, f.Policy.Definition);
        journal.Begin(Guid.NewGuid().ToString("N"), before.Target, after.Target);
        await using var startup = await DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(store, Installation, f.Policy.CreateInitial,
            ["user"], new TestLog(), new TestClock(), demo: true, providerRouting: f.Policy);
        Assert.Equal(before.Target, startup.Generation); Assert.Null(new DeviceProviderTransition(store, Installation, f.Policy.Definition).Pending);
        Assert.Equal(8, f.Created.Count); Assert.All(f.Created.Take(4), p => Assert.True(p.Closed));
        Assert.All(f.Created.Skip(4), p => Assert.False(p.Closed));
        Assert.DoesNotContain(f.Created,p => p.ProviderId == OpenRgbDeviceProvider.Provider);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompiledUsbIsolationKeepsAutomaticCoolingAndIndependentLightingRunningWhileOldRouteCloses(bool failDiscovery)
    {
        var clock = new TestClock(); var sensor = new DeviceProviderIsolationContinuityTests.Sensor(clock);
        var fan = new DeviceProviderIsolationContinuityTests.Fan(clock);
        var otherLight = new DeviceLightingRuntimeTests.Driver("other", "smbus"); var f = new Factories();
        f.Drivers[HubDeviceDriver.Provider] = [fan]; f.Drivers[HardwareSensorProvider.ProviderName] = [sensor]; f.Drivers["ene-memory"] = [otherLight];
        var components = f.Policy.CreateInitial(Config()); var direct = f.Created.Single(p => p.ProviderId == PackagedProviderRouting.DirectId);
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog(), clock, providers: components.Providers,
            physicalBindings: [fan.BindingCandidate, sensor.BindingCandidate, otherLight.BindingCandidate]);
        var hello = runtime.OpenSession("user"); long sequence = 0;
        DeviceServiceRequest Request(DeviceOperation operation, params EndpointReference[] targets)
        {
            var state = runtime.State();
            return new(2,hello.Epoch,hello.Session,Guid.NewGuid().ToString("N"),++sequence,"test",operation,state.Inventory.TopologyRevision,
                targets.Select(t => { var e = state.Endpoints.Single(e => e.Target == t); return new DeviceTargetVersion(t,e.Generation,e.PolicyRevision); }).ToArray());
        }
        var automatic = Request(DeviceOperation.SetAutomatic,new EndpointReference("fan","fan1")) with { Profile = new("curve","CPU",1,
            [new(new("sensor","temperature"),[new(30,30),new(80,80)],95,85)],0,100) };
        var result = await runtime.ExecuteAsync(automatic); Assert.True(result.Success,result.Error);
        var program = new LightingProgram("color-cycle",[new(255,0,0)],4);
        result = await runtime.ExecuteAsync(Request(DeviceOperation.SetLighting,new EndpointReference("other","light1")) with { Lighting = program });
        Assert.True(result.Success,result.Error);
        var lightBefore = runtime.State().Endpoints.Single(e => e.Target.DeviceId == "other");
        var plan = f.Policy.Plan(Config(),Config(true)); direct.Closing = new(); direct.ResumeClose = new();
        f.SdkOverride = (_, check) =>
        {
            Assert.True(direct.Closed); var p = f.New(OpenRgbDeviceProvider.Provider,["usb:asrock-native","sdk:openrgb-loopback"]);
            p.CheckRoute = check; p.FailPoll = failDiscovery; return Factories.Own(p);
        };
        var change = runtime.ReconfigureAsync(hello.Session,runtime.State().Inventory.TopologyRevision,async (_,ct) =>
            (await runtime.ReplaceProvidersAsync(hello.Session,plan.Remove,() => f.Policy.Create(Config(true),plan.Add),ct,plan.Add))
                .OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate!).ToArray(),
            new(plan.Remove.Concat(plan.Add).ToArray(),plan.IsolatedResourceDomains!));
        await direct.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var temperature in new[] {75,98})
            {
                sensor.Temperature = temperature; clock.Advance(1);
                Assert.True((await runtime.ExecuteAsync(Request(DeviceOperation.Renew) with { TopologyRevision = 0, Targets = null })).Success);
                await runtime.PollAsync(); await runtime.TickCoolingAsync(); await runtime.TickLightingAsync();
                Assert.Equal(temperature == 75 ? 75 : 100,fan.Duty);
            }
            Assert.Equal(0,fan.Releases); Assert.Equal(0,sensor.Releases); Assert.Equal(0,otherLight.Ends);
            Assert.Equal(DeviceLighting.Frame(program,2),otherLight.Color);
        }
        finally { direct.ResumeClose.TrySetResult(); }
        if (failDiscovery) await Assert.ThrowsAsync<IOException>(() => change); else await change;
        Assert.Equal(100,fan.Duty); Assert.True(runtime.State().Endpoints.Single(e => e.Target.DeviceId == "fan").Owned);
        var lightAfter = runtime.State().Endpoints.Single(e => e.Target.DeviceId == "other");
        Assert.Equal(lightBefore.PolicyRevision,lightAfter.PolicyRevision); Assert.Equal(lightBefore.Lighting!.GroupId,lightAfter.Lighting!.GroupId);
        Assert.All(f.Created.Where(p => p.ProviderId is not (PackagedProviderRouting.DirectId or OpenRgbDeviceProvider.Provider)),p => Assert.False(p.Closed));
    }
}
