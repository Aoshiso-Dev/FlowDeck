using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record DeviceBootstrapIntent(int Version, string InstallationId, ConfigurationPointer Generation);

/// <summary>Called by setup while the service is stopped and the deployment/hardware leases are held.</summary>
internal sealed class ServiceRuntimeBootstrap(string directory)
{
    internal const string IntentName = "initial-monitoring.json";
    internal Action<string>? Checkpoint { get; set; }
    internal static DeviceConfigurationPackage MonitoringConfiguration(string installation)
    {
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("インストール識別が不正です");
        return new(new(2, []), new(2, installation, []), new(2, [], []), new(2, [], [], [], [], []), new(2, []),
            new(2, new(), new(), new(), new([], []), new()),
            new(2, "native-v2", SettingsTransfer.Hash(new { installation, purpose = "initial-monitoring-v2" }), [], []));
    }
    internal ServiceRuntimeSelection PrepareMonitoring()
    {
        if (ServiceRuntimeSelection.Read(directory) is { } existing)
        { _ = existing.ReadConfiguration(directory); return existing; }
        var root = Path.Combine(directory, "devices-v2"); ServiceEnvironment.NoLinks(root);
        var path = Path.Combine(root, IntentName); ServiceEnvironment.NoLinks(path);
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(directory));
        DeviceBootstrapIntent intent;
        if (File.Exists(path))
        {
            intent = ServiceEnvironment.Read<DeviceBootstrapIntent>(path);
            if (intent is not { Version: 1, Generation: not null } || !DeviceServiceProtocol.Id(intent.InstallationId))
                throw new IOException("汎用設定の初回作成記録が不正です");
        }
        else
        {
            if (store.ReadActive() is not null) throw new IOException("採用済みの汎用設定があります。初回設定で上書きできません");
            var installation = Guid.NewGuid().ToString("N");
            var staged = store.Stage(DeviceConfigurationCodec.Write(MonitoringConfiguration(installation)));
            if (staged.ExpectedCurrent is not null) throw new IOException("作成中に汎用設定が変更されました");
            intent = new(1, installation, staged.Target);
            ServiceEnvironment.Write(path, intent); Checkpoint?.Invoke("intent");
        }
        // The intent binds a retried setup to the same identity and exact immutable generation.
        // A later user-adopted generation must never be silently reactivated after a rollback.
        var prepared = store.Read(intent.Generation);
        if (SettingsTransfer.Hash(DeviceConfigurationCodec.Read(prepared.Files)) != SettingsTransfer.Hash(MonitoringConfiguration(intent.InstallationId)))
            throw new IOException("初回作成記録は監視用設定と一致しません");
        var current = store.ReadActive();
        if (current is null)
        {
            store.Activate(new(intent.Generation, null)); Checkpoint?.Invoke("generation");
            current = store.ReadActive()!;
        }
        if (current.Pointer != intent.Generation || SettingsTransfer.Hash(DeviceConfigurationCodec.Read(current.Files)) !=
            SettingsTransfer.Hash(MonitoringConfiguration(intent.InstallationId)))
            throw new IOException("初回作成後に汎用設定が変更されています。既存設定の採用確認が必要です");
        var selected = new ServiceRuntimeSelection(1, "devices-v2", intent.InstallationId);
        Checkpoint?.Invoke("before-selection");
        ServiceEnvironment.Write(Path.Combine(directory, ServiceRuntimeSelection.FileName), selected);
        Checkpoint?.Invoke("selection");
        _ = selected.ReadConfiguration(directory); return selected;
    }
}
