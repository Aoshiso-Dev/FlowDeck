using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Factories are compiled into the service. A saved file cannot supply executable paths or provider assemblies.
public sealed record DeviceRuntimeComponents(IDeviceControlDriver[] Drivers, IDeviceDriverProvider[] Providers)
{
    /// <summary>Called after the service validates its protected installation and takes hardware ownership. Does not open hardware.</summary>
    public static DeviceRuntimeComponents Packaged(string installation, string recoveryDirectory, string boot,
        DeviceConfigurationPackage configuration, IEventLog log, TimeProvider? clock = null)
        => PackagedProviderRouting.ForInstalledService(installation, recoveryDirectory, boot, log, clock).CreateInitial(configuration);
    internal static DeviceRuntimeComponents ConstructProviders(params Func<IDeviceDriverProvider>[] factories)
    {
        var providers = new List<IDeviceDriverProvider>();
        try
        {
            foreach (var factory in factories) providers.Add(factory());
            return new([], providers.ToArray());
        }
        catch (Exception e)
        {
            var cleanup = new DeviceComponentCleanup(new([], providers.ToArray()));
            var pending = new DeviceStartupException(e, cleanup, () => cleanup.ResourcesClosed);
            throw e is DeviceStartupException nested ? DeviceStartupException.Combine(e, pending, nested) : pending;
        }
    }
}

/// <summary>Cold startup from one validated generation: discover, recover old obligations, then configure without starting policies.</summary>
public sealed partial class DeviceRuntimeStartup : IAsyncDisposable
{
    private readonly DeviceConfigurationAuthority authority;
    public DeviceControlRuntime Runtime { get; }
    public ConfigurationPointer Generation => authority.Generation;
    public DeviceConfigurationPackage Configuration => authority.Configuration;
    public bool SameInstallation { get; }
    private DeviceRuntimeStartup(DeviceControlRuntime runtime, ConfigurationGenerationStore store, string installation, ConfigurationGeneration generation, bool sameInstallation,
        IDeviceProviderRouting? providerRouting, IDeviceOpenRgbInspector? openRgbInspector, IEventLog log, IDeviceOpenRgbRuntimeInspector? openRgbRuntimeInspector)
    {
        Runtime = runtime; authority = new(store, installation, generation, providerRouting, openRgbInspector, log, openRgbRuntimeInspector); SameInstallation = sameInstallation;
        runtime.InstallConfigurationHandler(authority);
    }

    public static Task<DeviceRuntimeStartup> CreateAsync(ConfigurationGenerationStore store, string installation,
        Func<DeviceRuntimeComponents> createComponents, IEnumerable<string> allowedUsers, IEventLog log,
        TimeProvider? clock = null, bool demo = false, CancellationToken ct = default, IDeviceProviderRouting? providerRouting = null,
        IDeviceOpenRgbInspector? openRgbInspector = null, IDeviceDiagnosticSystemSource? diagnosticSystem = null, IDeviceOpenRgbRuntimeInspector? openRgbRuntimeInspector = null)
        => CreateAsync(store, installation, _ => createComponents(), allowedUsers, log, clock, demo, ct, providerRouting, openRgbInspector, diagnosticSystem, openRgbRuntimeInspector);

    public static async Task<DeviceRuntimeStartup> CreateAsync(ConfigurationGenerationStore store, string installation,
        Func<DeviceConfigurationPackage, DeviceRuntimeComponents> createComponents, IEnumerable<string> allowedUsers, IEventLog log,
        TimeProvider? clock = null, bool demo = false, CancellationToken ct = default, IDeviceProviderRouting? providerRouting = null,
        IDeviceOpenRgbInspector? openRgbInspector = null, IDeviceDiagnosticSystemSource? diagnosticSystem = null, IDeviceOpenRgbRuntimeInspector? openRgbRuntimeInspector = null)
    {
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("サービスのインストール識別子が不正です");
        var definition = DeviceProviderRoutingDefinition.Capture(providerRouting);
        if (new DeviceProviderTransition(store, installation, definition).Pending is not null)
            throw new IOException("取得経路の切替記録があります。経路ごとの復旧が必要です");
        // Validate the complete immutable generation before opening any provider or device.
        var generation = store.ReadActive() ?? throw new IOException("汎用設定の採用が完了していません");
        var config = DeviceConfigurationCodec.Read(generation.Files);
        var users = allowedUsers.ToArray();
        if (users.Length == 0 || users.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("許可ユーザーがありません");
        ct.ThrowIfCancellationRequested();
        var sameInstallation = config.Bindings.InstallationId == installation;
        var bindings = sameInstallation ? config.Bindings.Devices : [];
        DeviceRuntimeComponents? components = null;
        DeviceControlRuntime? runtime = null;
        try
        {
            components = createComponents(Contract.Copy(config));
            DeviceProviderRoutingDefinition.Check(providerRouting, definition);
            runtime = new(components.Drivers, users, log, clock, demo: demo, providers: components.Providers, physicalBindings: bindings,
                diagnosticSystem: diagnosticSystem ?? (demo ? null : WindowsDeviceDiagnosticSource.Shared));
            // A provider's first read can discover a cold driver. The second round initializes its own transport.
            await runtime.PollAsync().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            DeviceProviderRoutingDefinition.Check(providerRouting, definition);
            await runtime.PollAsync().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            DeviceProviderRoutingDefinition.Check(providerRouting, definition);
            await runtime.RecoverStartupAsync().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            DeviceProviderRoutingDefinition.Check(providerRouting, definition);
            if (runtime.State().LifecycleError is null)
            {
                _ = DeviceConfigurationAuthority.ConfigureDrivers(config, installation, components.Drivers
                    .Concat(components.Providers.SelectMany(p => p.CachedDrivers)).DistinctBy(d => d.DeviceId).ToArray());
                runtime.Refresh();
            }
            return new(runtime, store, installation, generation, sameInstallation, providerRouting,
                openRgbInspector ?? (demo ? null : new DeviceOpenRgbInspector()), log,
                openRgbRuntimeInspector ?? (demo ? null : DeviceOpenRgbRuntimeInspector.ForInstalledService()));
        }
        catch (Exception e)
        {
            if (runtime is not null)
                await DeviceStartupException.RethrowAfterCleanupAsync(e, runtime, () => runtime.ResourcesClosed).ConfigureAwait(false);
            else if (components is not null)
            {
                var cleanup = new DeviceComponentCleanup(components);
                await DeviceStartupException.RethrowAfterCleanupAsync(e, cleanup, () => cleanup.ResourcesClosed).ConfigureAwait(false);
            }
            else if (e is DeviceStartupException pending)
                await DeviceStartupException.RethrowAfterCleanupAsync(e, pending).ConfigureAwait(false);
            throw;
        }
    }
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}
