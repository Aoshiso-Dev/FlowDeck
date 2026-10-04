using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record DeploymentConfigurationSnapshot(int Version, string TransactionId, string SelectionHash, ConfigurationPointer Generation);
internal sealed record DeploymentConfigurationSnapshotDocument(DeploymentConfigurationSnapshot State, string Sha256);

public sealed partial class ServiceDeployment
{
    internal const string ConfigurationSnapshotName = "configuration-v2.json", ConfigurationSnapshotDirectory = "configuration-v2";
    private string ConfigurationBackup(ServiceDeploymentState state) => Path.Combine(Backup(state), ConfigurationSnapshotDirectory);
    private bool PendingConfigurationRecovery()
    {
        var root = ServiceRuntimeSelection.ConfigurationDirectory(data);
        var transition = Path.Combine(root, DeviceProviderTransition.FileName); ServiceEnvironment.NoLinks(transition);
        if (File.Exists(transition)) return true; // Only the service that owns the route can finalize it.
        var application = Path.Combine(root, "configuration-application.json"); ServiceEnvironment.NoLinks(application);
        if (!File.Exists(application)) return false;
        var value = ServiceEnvironment.Read<ConfigurationApplication>(application);
        static bool Pointer(ConfigurationPointer? p) => p is { Version: 1 } && DeviceServiceProtocol.Id(p.Id) && DeviceConfigurationProtocol.Digest(p.ManifestHash);
        if (value is not { Version: 1 } || !DeviceServiceProtocol.Id(value.RequestId) || !Pointer(value.Before) || !Pointer(value.After) ||
            value.Phase is not ("accepted" or "applied" or "rolled-back" or "failed" or "interrupted" or "superseded"))
            throw new IOException("設定反映の記録が不正です。更新では上書きしません");
        return value.Phase == "accepted";
    }
    private void SnapshotDeviceConfiguration(ServiceDeploymentState state, Dictionary<string, string?> backups)
    {
        if (backups[ServiceRuntimeSelection.FileName] is not { } hash) return;
        var selection = ServiceRuntimeSelection.Read(Backup(state)) ?? throw new IOException("退避した起動方式がありません");
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(data));
        var pointer = store.SnapshotActiveTo(ConfigurationBackup(state));
        var active = DeviceConfigurationCodec.Store(ConfigurationBackup(state)).ReadActive()!;
        ProtectCreatedTree(ConfigurationBackup(state));
        if (active.Pointer != pointer || DeviceConfigurationCodec.Read(active.Files).Bindings.InstallationId != selection.InstallationId)
            throw new IOException("退避した設定と起動方式のインストール識別が一致しません");
        // Keep the deployment v1 journal readable by the old service during rollback. This protected
        // sidecar binds the snapshot to the same transaction and the already-hashed runtime selection.
        var snapshot = new DeploymentConfigurationSnapshot(1, state.Id, hash, pointer);
        Save(Path.Combine(Backup(state), ConfigurationSnapshotName), new DeploymentConfigurationSnapshotDocument(snapshot, SettingsTransfer.Hash(snapshot)));
        AfterStep?.Invoke("configuration-backed-up");
    }
    private DeploymentConfigurationSnapshot? ValidateDeviceSnapshot(ServiceDeploymentState state)
    {
        if (state.Backups?.GetValueOrDefault(ServiceRuntimeSelection.FileName) is not { } hash) return null;
        var document = ServiceEnvironment.Read<DeploymentConfigurationSnapshotDocument>(Path.Combine(Backup(state), ConfigurationSnapshotName));
        var snapshot = document.State;
        if (snapshot is not { Version: 1, Generation: not null } || snapshot.TransactionId != state.Id || snapshot.SelectionHash != hash ||
            document.Sha256 != SettingsTransfer.Hash(snapshot)) throw new IOException("汎用設定の退避記録が更新対象と一致しません");
        var selection = ServiceRuntimeSelection.Read(Backup(state)) ?? throw new IOException("退避した起動方式がありません");
        var active = DeviceConfigurationCodec.Store(ConfigurationBackup(state)).ReadActive() ?? throw new IOException("退避した設定世代がありません");
        if (active.Pointer != snapshot.Generation || DeviceConfigurationCodec.Read(active.Files).Bindings.InstallationId != selection.InstallationId)
            throw new IOException("退避した設定世代またはインストール識別が一致しません");
        return snapshot;
    }
    private void RestoreDeviceConfiguration(ServiceDeploymentState state)
    {
        if (ValidateDeviceSnapshot(state) is not { } snapshot) return;
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(data));
        store.Checkpoint = step => { ProtectCreatedTree(Path.Combine(data, "devices-v2")); AfterStep?.Invoke("configuration-" + step); };
        store.RestoreSnapshotFrom(ConfigurationBackup(state), snapshot.Generation);
        ProtectCreatedTree(store.DirectoryPath);
    }
}
