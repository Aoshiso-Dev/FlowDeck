using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record PackagedRouteInstance(IDeviceDriverProvider Provider, Func<bool> ResourcesClosed);
internal sealed record PackagedProviderFactories(
    Func<DeviceConfigurationPackage, IDeviceDriverProvider> Hub,
    Func<DeviceConfigurationPackage, IDeviceDriverProvider> Sensors,
    Func<DeviceConfigurationPackage, IDeviceDriverProvider> Memory,
    Func<DeviceConfigurationPackage, PackagedRouteInstance> Direct,
    Func<DeviceConfigurationPackage, Action, PackagedRouteInstance> OpenRgb,
    Action RequireOpenRgb);

// The first compiled handover policy covers only ASRock 26ce:01a2 USB RGB. It does not
// authorize arbitrary OpenRGB detectors, SMBus access or replacement of cooling providers.
public sealed class PackagedProviderRouting : IDeviceProviderRouting
{
    internal const string DirectId = "asrock-usb";
    private readonly object gate = new();
    private readonly string installation;
    private readonly PackagedProviderFactories factories;
    private PackagedRouteInstance? active;
    private object? activeToken;
    private DeviceStartupException? pendingRoute, pendingInitial;
    private static string[] ChangedDomains => AsrockDeviceProvider.DefinitionData.ResourceDomains
        .Concat(OpenRgbDeviceProvider.Domains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static readonly DeviceProviderRoutingDefinition Policy = new("asrock-usb-openrgb-v1", 1, SettingsTransfer.Hash(new
    {
        Version = 1, Asrock = AsrockDeviceProvider.DefinitionData.Digest,
        Hub = HubDeviceDriver.DefinitionData.Digest,
        Memory = LightingDeviceDefinitionCatalog.BuiltIn.Get(LightingDeviceDefinitionCatalog.EneId, "ene-dram-rgb").Digest,
        Helper = OpenRgbRuntimePackage.FlowDeckPinned.DefinitionDigest,
        OpenRgbProfile = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6742).SettingsDigest,
        SensorDomains = new[] { "lhm", "isa-bus", "smbus" }, ChangedDomains
    }));
    public DeviceProviderRoutingDefinition Definition => Policy with { };
    public bool SupportsDeviceSelection => true;

    internal PackagedProviderRouting(string installation, PackagedProviderFactories factories)
    {
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("サービスのインストール識別が不正です");
        this.installation = installation; this.factories = factories ?? throw new ArgumentNullException(nameof(factories));
    }
    public static PackagedProviderRouting ForInstalledService(string installation, string recoveryDirectory, string boot,
        IEventLog log, TimeProvider? clock = null)
    {
        if (!DeviceServiceProtocol.Id(installation) || !DeviceConfigurationRules.Text(boot, 128) || !Path.IsPathFullyQualified(recoveryDirectory))
            throw new ArgumentException("サービスのインストール・復旧領域・起動識別が不正です");
        ArgumentNullException.ThrowIfNull(log);
        var runtime = Path.Combine(ServiceEnvironment.InstallationDirectory, "optional", "OpenRGB");
        var configurationRoot = Path.Combine(recoveryDirectory, "openrgb");
        HubWorkerClient Worker() => new(Path.Combine(ServiceEnvironment.InstallationDirectory, "runtime", "python", "python.exe"),
            Path.Combine(ServiceEnvironment.InstallationDirectory, "worker", "hub_worker.py"), log, isolated: true);
        return new(installation, new(
            config =>
            {
                var remembered = config.Bindings.Devices.Where(b => b.DefinitionId == HubDeviceDriver.Definition).ToArray();
                if (remembered.Any(b => b.DeviceId != DeviceIdentity.Create(installation, HubDeviceDriver.Provider, b.ProviderIdentity)))
                    throw new IOException("保存されたHubと実機識別の対応が不正です");
                return new HubDeviceProvider(installation, recoveryDirectory, Worker(), _ => Worker(), remembered.Select(b => b.ProviderIdentity), clock);
            },
            _ => new HardwareSensorProvider(installation, new HardwareTemperatures(), new(recoveryDirectory, boot, clock)),
            _ => new EneMemoryDeviceProvider(installation, recoveryDirectory, boot),
            _ =>
            {
                var provider = new AsrockDeviceProvider(installation, recoveryDirectory, boot);
                return new(provider, () => provider.ResourcesClosed);
            },
            (config, checkRoute) =>
            {
                var preferences = config.Preferences.Providers ?? new();
                var detection = new AsrockOpenRgbDetectionScope(installation, recoveryDirectory, boot, checkRoute);
                var provider = new OpenRgbManagedDeviceProvider(installation, recoveryDirectory, boot,
                    new(OpenRgbStartupKind.AsrockPolychromeUsb, preferences.OpenRgbPort), detection,
                    preferences.OpenRgbDeviceIds ?? [], runtime, configurationRoot);
                return new(provider, () => provider.ResourcesClosed);
            },
            () =>
            {
                // This rejects before old control is released or native detection capture starts.
                // Actual helper/hardware acceptance remains outstanding; no config flag lifts it.
                OpenRgbOwnedProcess.RequireAcceptedDetection(OpenRgbStartupKind.AsrockPolychromeUsb);
                using var lease = OpenRgbRuntimePackage.FlowDeckPinned.AcquireProtected(runtime);
                ServiceEnvironment.ValidateTree(configurationRoot);
            }));
    }
    private DeviceConfigurationPackage Validate(DeviceConfigurationPackage config)
    {
        var copy = Contract.Copy(config); _ = DeviceConfigurationCodec.Write(copy);
        if (copy.Bindings.InstallationId != installation) throw new IOException("設定のインストール識別が変更されています");
        return copy;
    }
    private static string Route(DeviceConfigurationPackage config) => config.Preferences.Providers?.OpenRgbEnabled == true
        ? OpenRgbDeviceProvider.Provider : DirectId;
    private void CheckPrepared(DeviceConfigurationPackage config)
    { if (Route(config) == OpenRgbDeviceProvider.Provider) factories.RequireOpenRgb(); }
    public DeviceProviderRoutePlan Plan(DeviceConfigurationPackage before, DeviceConfigurationPackage after)
    {
        before = Validate(before); after = Validate(after);
        if ((before.Preferences.Providers ?? new()).EquivalentTo(after.Preferences.Providers ?? new()))
            throw new IOException("取得経路に変更はありません");
        // Check both directions: a rollback may need to recreate the old helper as well.
        CheckPrepared(before); CheckPrepared(after);
        return new([Route(before)], [Route(after)], ChangedDomains);
    }
    private void CheckNoPending()
    {
        if (pendingRoute is { ResourcesClosed: false } || pendingInitial is { ResourcesClosed: false })
            throw new IOException("取得経路の起動途中の接続が未終了です");
    }
    private void CheckOpenRgbRoute(object token)
    {
        lock (gate)
        {
            CheckNoPending();
            if (!ReferenceEquals(activeToken, token) || active is null || active.Provider.ProviderId != OpenRgbDeviceProvider.Provider || active.ResourcesClosed())
                throw new IOException("OpenRGBが所有するマザー照明の経路がありません");
        }
    }
    private IDeviceDriverProvider CreateRoute(DeviceConfigurationPackage config)
    {
        // Called inside the same gate for initial construction, switches and recovery.
        CheckNoPending();
        if (active is not null && !active.ResourcesClosed()) throw new IOException("旧マザー照明経路の終了が未確認です");
        active = null; activeToken = null; pendingRoute = null;
        PackagedRouteInstance? created = null;
        try
        {
            var token = new object();
            created = Route(config) == DirectId ? factories.Direct(config) : factories.OpenRgb(config, () => CheckOpenRgbRoute(token));
            if (created?.Provider is null || created.ResourcesClosed is null) throw new IOException("照明経路の生成結果が不正です");
            var domains = created.Provider.ResourceDomains;
            var expected = Route(config) == DirectId ? AsrockDeviceProvider.DefinitionData.ResourceDomains : ChangedDomains;
            if (created.Provider.ProviderId != Route(config) || created.ResourcesClosed() || domains is null ||
                !domains.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                throw new IOException("照明経路が固定の機器・資源定義と一致しません");
            active = created; activeToken = token; return created.Provider;
        }
        catch (Exception error)
        {
            if (created?.Provider is not null)
            {
                var cleanup = new DeviceComponentCleanup(new([], [created.Provider]));
                pendingRoute = new(error, cleanup, () => cleanup.ResourcesClosed);
                if (error is DeviceStartupException nested) pendingRoute = DeviceStartupException.Combine(error, pendingRoute, nested);
                throw pendingRoute;
            }
            if (error is DeviceStartupException pending) pendingRoute = pending;
            throw;
        }
    }
    public IDeviceDriverProvider[] Create(DeviceConfigurationPackage configuration, string[] providerIds)
    {
        var config = Validate(configuration);
        if (providerIds is not { Length: 1 } || providerIds[0] != Route(config))
            throw new IOException("要求した取得経路は同梱の照明切替範囲外です");
        CheckPrepared(config);
        lock (gate) return [CreateRoute(config)];
    }
    public DeviceRuntimeComponents CreateInitial(DeviceConfigurationPackage configuration)
    {
        var config = Validate(configuration); CheckPrepared(config);
        lock (gate)
        {
            CheckNoPending();
            if (active is not null && !active.ResourcesClosed()) throw new IOException("旧機器構成の終了が未確認です");
            try
            {
                // Constructors are lazy. Only the runtime may begin discovery after old owners close.
                return DeviceRuntimeComponents.ConstructProviders(() => factories.Hub(config), () => factories.Sensors(config),
                    () => CreateRoute(config), () => factories.Memory(config));
            }
            catch (DeviceStartupException pending) { pendingInitial = pending; throw; }
        }
    }
}
