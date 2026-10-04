using FlowDeck.Core;
using FlowDeck.Infrastructure;
using System.Collections.Concurrent;

namespace FlowDeck.Tests;

public sealed partial class PackagedProviderRoutingTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Provider(string id, string[] domains, ConcurrentQueue<string> events) : IDeviceDriverProvider
    {
        public string ProviderId => id;
        public string[] ResourceDomains => domains;
        public IDeviceControlDriver[] CachedDrivers { get; init; } = [];
        public bool Closed, FailClose, FailPoll;
        public int Polls, Closes;
        public Action? CheckRoute;
        public TaskCompletionSource? Closing, ResumeClose;
        public Task PollAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Assert.False(Closed); CheckRoute?.Invoke();
            Polls++; events.Enqueue(id+":poll");
            return FailPoll ? Task.FromException(new IOException("fixture discovery failed")) : Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            Closes++; events.Enqueue(id+":close"); Closing?.TrySetResult();
            if (ResumeClose is not null) await ResumeClose.Task;
            if (FailClose) throw new IOException("fixture still owns handles");
            Closed = true;
        }
    }
    private sealed class Factories
    {
        public readonly ConcurrentQueue<string> Events = [];
        public readonly List<Provider> Created = [];
        public readonly Dictionary<string, IDeviceControlDriver[]> Drivers = [];
        public bool Accepted = true, MemoryFails, WrongSdkDomain;
        public int Preflights;
        public Func<DeviceConfigurationPackage, Action, PackagedRouteInstance>? SdkOverride;
        public PackagedProviderRouting Policy { get; }
        public Factories()
        {
            Policy = new(Installation, new(
                _ => New(HubDeviceDriver.Provider, HubDeviceDriver.DefinitionData.DiscoveryResourceDomains.ToArray()),
                _ => New(HardwareSensorProvider.ProviderName, ["lhm", "isa-bus", "smbus"]),
                _ => MemoryFails ? throw new IOException("memory factory failed") : New("ene-memory", ["smbus"]),
                _ => Own(New(PackagedProviderRouting.DirectId, ["usb:asrock-native"])),
                (config, check) =>
                {
                    if (SdkOverride is not null) return SdkOverride(config, check);
                    var p = New(OpenRgbDeviceProvider.Provider, WrongSdkDomain ? ["smbus"] : ["usb:asrock-native", "sdk:openrgb-loopback"]);
                    p.CheckRoute = check; return Own(p);
                },
                () => { Preflights++; if (!Accepted) throw new IOException("helper unavailable"); }));
        }
        public Provider New(string id, string[] domains)
        { Events.Enqueue(id+":create"); var p = new Provider(id, domains, Events) { CachedDrivers = Drivers.GetValueOrDefault(id) ?? [] }; Created.Add(p); return p; }
        public static PackagedRouteInstance Own(Provider p) => new(p, () => p.Closed);
    }
    private static DeviceConfigurationPackage Config(bool sdk = false, int port = 6742, string[]? selected = null)
    {
        var c = ServiceRuntimeBootstrap.MonitoringConfiguration(Installation);
        return c with { Preferences = c.Preferences with { Providers = new(sdk, port, selected) } };
    }
    private static async Task Close(DeviceRuntimeComponents components)
    { var cleanup = new DeviceComponentCleanup(components); await cleanup.DisposeAsync(); Assert.True(cleanup.ResourcesClosed); }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitialCompositionHasOneLightingOwnerAndRetainsIndependentProviders(bool sdk)
    {
        var f = new Factories(); var components = f.Policy.CreateInitial(Config(sdk));
        try
        {
            Assert.Equal(new[] { HubDeviceDriver.Provider, HardwareSensorProvider.ProviderName,
                sdk ? OpenRgbDeviceProvider.Provider : PackagedProviderRouting.DirectId, "ene-memory" }, components.Providers.Select(p => p.ProviderId));
            Assert.All(f.Created, p => Assert.Equal(0, p.Polls));
            Assert.Equal(sdk ? 1 : 0, f.Preflights);
            Assert.Throws<IOException>(() => f.Policy.CreateInitial(Config(!sdk)));
            if (sdk) f.Created.Single(p => p.ProviderId == OpenRgbDeviceProvider.Provider).CheckRoute!();
        }
        finally { await Close(components); }
    }
    [Theory] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void PlansContainOnlyTheConflictingUsbRoute(bool before, bool after)
    {
        var f = new Factories(); var plan = f.Policy.Plan(Config(before), Config(after, 6750));
        Assert.Equal([before ? OpenRgbDeviceProvider.Provider : PackagedProviderRouting.DirectId], plan.Remove);
        Assert.Equal([after ? OpenRgbDeviceProvider.Provider : PackagedProviderRouting.DirectId], plan.Add);
        Assert.Equal(["sdk:openrgb-loopback", "usb:asrock-native"], Assert.IsType<string[]>(plan.IsolatedResourceDomains));
        Assert.Empty(f.Created); Assert.True(DeviceProviderRoutingDefinition.Valid(f.Policy.Definition));
        Assert.Equal(f.Policy.Definition, new Factories().Policy.Definition);
    }
    [Fact]
    public async Task CloseFailureKeepsBothSameRouteAndOppositeRouteFactoriesBlocked()
    {
        var f = new Factories(); var source = Assert.IsType<Provider>(Assert.Single(f.Policy.Create(Config(), [PackagedProviderRouting.DirectId])));
        source.FailClose = true;
        await Assert.ThrowsAsync<IOException>(() => source.DisposeAsync().AsTask());
        Assert.Throws<IOException>(() => f.Policy.Create(Config(true), [OpenRgbDeviceProvider.Provider]));
        Assert.Throws<IOException>(() => f.Policy.Create(Config(), [PackagedProviderRouting.DirectId])); Assert.Single(f.Created);
        source.FailClose = false; await source.DisposeAsync();
        var next = Assert.IsType<Provider>(Assert.Single(f.Policy.Create(Config(true), [OpenRgbDeviceProvider.Provider])));
        next.CheckRoute!(); await next.DisposeAsync(); Assert.Throws<IOException>(next.CheckRoute!);
        var again = Assert.IsType<Provider>(Assert.Single(f.Policy.Create(Config(true), [OpenRgbDeviceProvider.Provider])));
        again.CheckRoute!(); Assert.Throws<IOException>(next.CheckRoute!); await again.DisposeAsync();
    }
    [Fact]
    public async Task FactoryFailureRetainsEarlierProvidersUntilCleanupActuallySucceeds()
    {
        var f = new Factories { MemoryFails = true };
        var failure = Assert.Throws<DeviceStartupException>(() => f.Policy.CreateInitial(Config()));
        f.Created[0].FailClose = true; await Assert.ThrowsAsync<AggregateException>(() => failure.DisposeAsync().AsTask());
        Assert.False(failure.ResourcesClosed); var count = f.Created.Count;
        Assert.Throws<IOException>(() => f.Policy.CreateInitial(Config())); Assert.Equal(count, f.Created.Count);
        f.Created[0].FailClose = false; await failure.DisposeAsync(); Assert.True(failure.ResourcesClosed);
        f.MemoryFails = false; await Close(f.Policy.CreateInitial(Config()));
    }
    [Fact]
    public async Task WrongReturnedResourcesKeepOwnershipUntilTheRejectedProviderCloses()
    {
        var f = new Factories { WrongSdkDomain = true };
        var failure = Assert.Throws<DeviceStartupException>(() => f.Policy.Create(Config(true), [OpenRgbDeviceProvider.Provider]));
        var provider = Assert.Single(f.Created); provider.FailClose = true;
        await Assert.ThrowsAsync<AggregateException>(() => failure.DisposeAsync().AsTask());
        Assert.Throws<IOException>(() => f.Policy.Create(Config(), [PackagedProviderRouting.DirectId]));
        provider.FailClose = false; await failure.DisposeAsync();
        var direct = Assert.Single(f.Policy.Create(Config(), [PackagedProviderRouting.DirectId])); await direct.DisposeAsync();
    }
    [Theory] [InlineData("foreign")] [InlineData("unrelated-provider")] [InlineData("duplicate")] [InlineData("wrong-route")]
    public void InvalidConfigurationOrFactoryRequestsCannotCreateProviders(string invalid)
    {
        var f = new Factories(); var config = Config();
        if (invalid == "foreign") config = config with { Bindings = config.Bindings with { InstallationId = new('f',32) } };
        var ids = invalid switch
        {
            "unrelated-provider" => new[] { HardwareSensorProvider.ProviderName },
            "duplicate" => new[] { PackagedProviderRouting.DirectId, PackagedProviderRouting.DirectId },
            "wrong-route" => new[] { OpenRgbDeviceProvider.Provider },
            _ => new[] { PackagedProviderRouting.DirectId }
        };
        Assert.Throws<IOException>(() => f.Policy.Create(config, ids)); Assert.Empty(f.Created);
    }
    [Fact]
    public void InstalledPolicyRejectsUnacceptedDetectionBeforeAnyFactoriesOrWrites()
    {
        using var dir = new ServiceTestDirectory();
        var policy = PackagedProviderRouting.ForInstalledService(Installation, dir.Path, "boot", new TestLog());
        var error = Assert.Throws<IOException>(() => policy.Plan(Config(), Config(true)));
        Assert.Contains("検証が未完了", error.Message);
        Assert.Throws<IOException>(() => policy.CreateInitial(Config(true)));
        Assert.Throws<IOException>(() => policy.Create(Config(true), [OpenRgbDeviceProvider.Provider]));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }
    [Fact]
    public async Task AvailabilityIsRecheckedBeforeConstructingTheTarget()
    {
        var f = new Factories(); var old = Assert.Single(f.Policy.Create(Config(), [PackagedProviderRouting.DirectId]));
        _ = f.Policy.Plan(Config(), Config(true)); f.Accepted = false;
        Assert.Throws<IOException>(() => f.Policy.Plan(Config(), Config(true)));
        Assert.Throws<IOException>(() => f.Policy.Create(Config(true), [OpenRgbDeviceProvider.Provider]));
        Assert.False(((Provider)old).Closed); Assert.Single(f.Created); await old.DisposeAsync();
    }
}
