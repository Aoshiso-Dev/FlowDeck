using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceRuntimeStartup
{
    /// <summary>
    /// Cold startup with a compiled, route-aware factory. Each recovery instance is closed before
    /// the next route is constructed. The factory must enforce resource exclusions for each config;
    /// a factory that ignores Providers must not use this API.
    /// </summary>
    public static async Task<DeviceRuntimeStartup> CreateWithProviderRecoveryAsync(ConfigurationGenerationStore store, string installation,
        Func<DeviceConfigurationPackage, DeviceRuntimeComponents> createComponents, IEnumerable<string> allowedUsers, IEventLog log,
        TimeProvider? clock = null, bool demo = false, CancellationToken ct = default, IDeviceProviderRouting? providerRouting = null)
    {
        ArgumentNullException.ThrowIfNull(createComponents);
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("サービスのインストール識別子が不正です");
        var users = allowedUsers.ToArray();
        if (users.Length == 0 || users.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("許可ユーザーがありません");
        var definition = DeviceProviderRoutingDefinition.Capture(providerRouting);
        var transition = new DeviceProviderTransition(store, installation, definition);
        if (transition.Pending is not null)
        {
            _ = ReadProviderApplication(store, transition.Pending);
            await transition.RecoverAsync(async (config, token) =>
            {
                token.ThrowIfCancellationRequested();
                DeviceProviderRoutingDefinition.Check(providerRouting, definition);
                DeviceRuntimeComponents? components = null;
                DeviceControlRuntime? recovering = null;
                try
                {
                    components = createComponents(Contract.Copy(config));
                    DeviceProviderRoutingDefinition.Check(providerRouting, definition);
                    recovering = new(components.Drivers, users, log, clock, demo: demo, providers: components.Providers,
                        physicalBindings: config.Bindings.Devices);
                    await recovering.PollAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
                    DeviceProviderRoutingDefinition.Check(providerRouting, definition);
                    await recovering.PollAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
                    DeviceProviderRoutingDefinition.Check(providerRouting, definition);
                    // A failed discovery must not masquerade as an empty route with no obligations.
                    await recovering.RecoverStartupAsync(requireSuccessfulDiscovery: true).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    DeviceProviderRoutingDefinition.Check(providerRouting, definition);
                    if (recovering.State().LifecycleError is { } error) throw new IOException(error);
                }
                catch (Exception e)
                {
                    if (recovering is not null)
                        await DeviceStartupException.RethrowAfterCleanupAsync(e, recovering, () => recovering.ResourcesClosed).ConfigureAwait(false);
                    else if (components is not null)
                    {
                        var cleanup = new DeviceComponentCleanup(components);
                        await DeviceStartupException.RethrowAfterCleanupAsync(e, cleanup, () => cleanup.ResourcesClosed).ConfigureAwait(false);
                    }
                    else if (e is DeviceStartupException pending)
                        await DeviceStartupException.RethrowAfterCleanupAsync(e, pending).ConfigureAwait(false);
                    throw;
                }
                try { await recovering.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) { throw new DeviceStartupException(e, recovering, () => recovering.ResourcesClosed); }
                // ServiceStopping can itself fail a persisted release after startup recovery.
                if (recovering.State().LifecycleError is { } closingError) throw new IOException(closingError);
                DeviceProviderRoutingDefinition.Check(providerRouting, definition);
            }, ct, record =>
            {
                DeviceProviderRoutingDefinition.Check(providerRouting, definition);
                if (ReadProviderApplication(store, record) is not { } application) return;
                var applied = record.Phase == ProviderTransitionPhase.Committed;
                ServiceEnvironment.Write(Path.Combine(store.DirectoryPath, "configuration-application.json"), application with {
                    Phase = applied ? "applied" : "rolled-back",
                    Error = applied ? null : application.Error ?? "中断した取得経路を旧設定へ復旧しました" });
            }).ConfigureAwait(false);
        }
        DeviceRuntimeComponents Create(DeviceConfigurationPackage config)
        {
            DeviceProviderRoutingDefinition.Check(providerRouting, definition);
            return createComponents(config);
        }
        return await CreateAsync(store, installation, Create, users, log, clock, demo, ct, providerRouting).ConfigureAwait(false);
    }
    private static ConfigurationApplication? ReadProviderApplication(ConfigurationGenerationStore store, ProviderTransitionRecord transition)
    {
        var path = Path.Combine(store.DirectoryPath, "configuration-application.json"); ServiceEnvironment.NoLinks(path);
        // Standalone compiled route operations may have no configuration-application transaction.
        if (!File.Exists(path)) return null;
        var value = ServiceEnvironment.Read<ConfigurationApplication>(path);
        if (value is not { Version: 1 } || value.RequestId != transition.Request || value.Before != transition.Before || value.After != transition.After ||
            value.Phase is not ("accepted" or "applied" or "rolled-back" or "failed" or "interrupted" or "superseded"))
            throw new IOException("取得経路と設定反映の記録が一致しません");
        return value;
    }
}
