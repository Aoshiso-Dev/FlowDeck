namespace FlowDeck.Core;

// Only compiled routing code supplies this scope. Domains include discovery as well as device I/O.
// It is a local scheduling boundary, not evidence that an external process respects those domains.
public sealed record DeviceProviderIsolation(string[] ProviderIds, string[] ResourceDomains);

public sealed partial class DeviceControlRuntime
{
    private DeviceProviderIsolation? providerIsolation;
    private readonly HashSet<string> isolatedDeviceIds = [];
    private bool ConfigurationBlocksDevice(string id) => configurationChanging && (endpointSetupScope is { } endpointScope ? endpointScope.BlocksDevice(id) :
        providerIsolation is null || isolatedDeviceIds.Contains(id) || providerIsolation.ProviderIds.Contains(driverProviders.GetValueOrDefault(id)));
    private bool ConfigurationBlocksProvider(string id) => configurationChanging &&
        (endpointSetupScope is { } endpointScope ? driverProviders.Any(p => p.Value == id && endpointScope.BlocksDevice(p.Key)) :
        providerIsolation is null || providerIsolation.ProviderIds.Contains(id));
    private bool ConfigurationBlocksPoll(string key) => key.StartsWith("provider/", StringComparison.Ordinal)
        ? ConfigurationBlocksProvider(key[9..]) : ConfigurationBlocksDevice(key[7..]);
    private IDeviceControlDriver[]? ConfigurationReleaseDrivers() => providerIsolation is null ? null :
        drivers.Values.Where(d => ConfigurationBlocksDevice(d.DeviceId)).ToArray();

    public string[] InspectProviderIsolation(DeviceProviderIsolation scope)
    {
        lock (gate)
        {
            ValidateProviderIsolationCore(scope);
            return drivers.Keys.Where(id => !scope.ProviderIds.Contains(driverProviders.GetValueOrDefault(id)))
                .Order(StringComparer.Ordinal).ToArray();
        }
    }

    private void ValidateProviderIsolationCore(DeviceProviderIsolation scope)
    {
        if (scope is null || !DeviceProviderRouteReview.Ids(scope.ProviderIds, 16) || scope.ProviderIds.Length == 0 ||
            scope.ResourceDomains is not { Length: > 0 and <= 128 } ||
            scope.ResourceDomains.Any(d => !DeviceConfigurationRules.Text(d, 256)) ||
            scope.ResourceDomains.Distinct(StringComparer.Ordinal).Count() != scope.ResourceDomains.Length)
            throw new IOException("取得経路の分離範囲が不正です");
        if (lightingIdentifications.Count != 0 || calibrationTrials.Count != 0)
            throw new IOException("識別・端子テストの終了後に経路を切り替えてください");
        bool Changing(string id) => scope.ProviderIds.Contains(driverProviders.GetValueOrDefault(id));
        void CheckDomains(string[] domains, bool changing)
        {
            if (domains.Length == 0 || (changing ? domains.Except(scope.ResourceDomains).Any() : domains.Intersect(scope.ResourceDomains).Any()))
                throw new IOException("取得経路と継続する機器の資源を分離できません");
        }
        foreach (var provider in providers) CheckDomains(provider.ResourceDomains, scope.ProviderIds.Contains(provider.ProviderId));
        foreach (var driver in drivers.Values)
        {
            // Undiscovered devices use their provider's declared discovery resources. Before an
            // individual poll or write, its observed resource list is checked again below.
            var observation = driver.Observe();
            if (observation is not null) CheckDomains(observation.ResourceDomains, Changing(driver.DeviceId));
        }
        if (coolingPolicies.Values.Any(c => !Changing(c.Target.DeviceId) && c.Sensors.Keys.Any(s => Changing(s.DeviceId))))
            throw new IOException("継続する冷却が切替対象の温度センサーを使用しています");
        if (lightingGroups.Values.Any(g => g.Participants.Any(p => Changing(p.DeviceId)) && g.Participants.Any(p => !Changing(p.DeviceId))))
            throw new IOException("切替対象と継続対象が同じ照明同期に含まれています。同期を終了してから切り替えてください");
    }

    private void CheckIsolatedReplacement(string[] remove, string[]? expected)
    {
        if (providerIsolation is not { } scope) return;
        if (expected is null || remove.Concat(expected).Any(id => !scope.ProviderIds.Contains(id)))
            throw new IOException("確認した分離範囲外の取得経路へ変更できません");
        ValidateProviderIsolationCore(scope);
    }

    private void CheckIsolatedAdditions(IDeviceDriverProvider[] additions)
    {
        if (providerIsolation is not { } scope) return;
        foreach (var provider in additions)
        {
            if (!scope.ProviderIds.Contains(provider.ProviderId) || provider.ResourceDomains.Except(scope.ResourceDomains).Any() ||
                provider.CachedDrivers.Any(d => d.Observe() is { } o && o.ResourceDomains.Except(scope.ResourceDomains).Any()))
                throw new IOException("新しい取得経路が確認した資源範囲を超えています");
        }
    }
}
