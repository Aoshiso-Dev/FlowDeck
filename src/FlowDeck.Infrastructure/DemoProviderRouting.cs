using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Opt-in fixture for the dedicated routing smoke only. No SDK, HID, SMBus or native driver exists here.
public sealed class DemoProviderRouting : IDeviceProviderRouting
{
    public DeviceProviderRoutingDefinition Definition { get; } = new("demo-routing-only", 3, new('e', 64));
    public bool SupportsDeviceSelection => true;
    public bool Reject { get; set; }
    public List<Provider> Created { get; } = [];
    public DeviceProviderRoutePlan Plan(DeviceConfigurationPackage before, DeviceConfigurationPackage after)
    {
        if (Reject) throw new IOException("デモの共存条件が変更されました");
        return new([before.Preferences.Providers?.OpenRgbEnabled == true ? "demo-sdk" : "demo-direct"],
            [after.Preferences.Providers?.OpenRgbEnabled == true ? "demo-sdk" : "demo-direct"],
            ["demo-route-light", "demo:demo-direct-light", .. Enumerable.Range(1, 2).Select(n =>
                "demo:" + DeviceIdentity.Create(after.Bindings.InstallationId!, "openrgb-sdk", "fixture:light:" + n))]);
    }
    public IDeviceDriverProvider[] Create(DeviceConfigurationPackage configuration, string[] providerIds) => providerIds.Select(id =>
    {
        if (id is not ("demo-direct" or "demo-sdk")) throw new IOException("デモ専用の経路が必要です");
        var provider = new Provider(id, configuration.Bindings.InstallationId!, configuration.Preferences.Providers?.OpenRgbDeviceIds ?? []);
        Created.Add(provider); return (IDeviceDriverProvider)provider;
    }).ToArray();
    public sealed class Provider : IDeviceDriverProvider
    {
        private readonly IDeviceControlDriver[] drivers;
        public Provider(string id, string installation, string[] selected)
        {
            ProviderId = id;
            drivers = id == "demo-direct" ? [new DemoEndpointDriver(id + "-light", "切替デモ照明", DeviceKind.Controller, cooling: false)] :
                Enumerable.Range(1, 2).Select(n =>
                {
                    var deviceId = DeviceIdentity.Create(installation, "openrgb-sdk", "fixture:light:" + n);
                    return (IDeviceControlDriver)new SelectedDriver(deviceId, "切替デモ照明 " + n, selected.Contains(deviceId));
                }).ToArray();
        }
        public string ProviderId { get; }
        public string[] ResourceDomains => ["demo-route-light"];
        public IDeviceControlDriver[] CachedDrivers => drivers.ToArray();
        public bool Disposed { get; private set; }
        public Task PollAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Disposed, this); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class SelectedDriver(string id, string name, bool selected) : IPhysicalDeviceDriver
    {
        private readonly DemoEndpointDriver inner = new(id, name, DeviceKind.Controller, cooling: false);
        private const string Unselected = "OpenRGBの制御対象に選択されていません";
        public string DeviceId => id;
        public PhysicalDeviceBinding BindingCandidate => new(id, "fixture:" + id, "demo-rgb-v2", new('b', 64));
        public DeviceObservation Observe()
        {
            var value = inner.Observe();
            return value with { Provider = "openrgb-sdk", DefinitionId = "demo-rgb-v2", Endpoints = value.Endpoints.Select(e => e with {
                Capabilities = e.Capabilities.Where(c => c.Feature != DeviceFeature.ColorFrames).Select(c =>
                    selected || c.Feature == DeviceFeature.RestoreLighting ? c : c with { Availability = DeviceAvailability.NeedsSetup, Reason = Unselected }).ToArray() }).ToArray() };
        }
        public string? Validate(DeviceEndpointAction action) => !selected && action.Operation == DeviceOperation.SetLighting ? Unselected : inner.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => Validate(action) is { } error
            ? Task.FromResult(new DeviceDriverResult(Outcome.Rejected, error)) : inner.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => inner.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
