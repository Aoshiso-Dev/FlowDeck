using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal enum ProviderTransitionPhase { Prepared, StartingTarget, TargetReady, Committed, StartingSource, SourceReady, RolledBack, RecoveredSource }
internal enum ProviderTransitionRecovery { Source, TargetThenSource, Target, FinalizedSource, FinalizedTarget }
internal sealed record ProviderTransitionRecord(int Version, string Installation, string Request, ConfigurationPointer Before,
    ConfigurationPointer After, DeviceProviderRoutingDefinition Definition, string BeforeRoute, string AfterRoute, ProviderTransitionPhase Phase, int Revision, string? PreviousHash, string Hash);

// Service-private intent, written before a replacement factory can run. Configurations are loaded by
// verified generation pointers; this file cannot carry executable paths, factories, or SDK commands.
internal sealed class DeviceProviderTransition
{
    internal const string FileName = "provider-transition.json";
    private readonly object gate = new();
    private readonly ConfigurationGenerationStore store;
    private readonly string installation, path;
    private readonly DeviceProviderRoutingDefinition? definition;
    private ProviderTransitionRecord? pending;
    private bool switching;
    public ProviderTransitionRecord? Pending { get { lock (gate) return pending is null ? null : Contract.Copy(pending); } }
    public DeviceProviderTransition(ConfigurationGenerationStore store, string installation, DeviceProviderRoutingDefinition? definition = null)
    {
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("取得経路のインストール識別が不正です");
        if (definition is not null && !DeviceProviderRoutingDefinition.Valid(definition)) throw new ArgumentException("取得経路の判定方針が不正です");
        this.definition = definition is null ? null : definition with { };
        this.store = store; this.installation = installation; path = Path.Combine(store.DirectoryPath, FileName);
        ServiceEnvironment.NoLinks(path);
        if (!File.Exists(path)) return;
        var value = ServiceEnvironment.Read<ProviderTransitionRecord>(path);
        Validate(value); pending = value;
    }
    private static bool Pointer(ConfigurationPointer? p) => p is { Version: 1 } && DeviceServiceProtocol.Id(p.Id) && DeviceConfigurationProtocol.Digest(p.ManifestHash);
    private static string Hash(ProviderTransitionRecord value) => SettingsTransfer.Hash(value with { Hash = "" });
    private static string Route(DeviceConfigurationPackage config, DeviceProviderRoutingDefinition policy) => SettingsTransfer.Hash(new
        { Format = "provider-routing-v2", Policy = policy, Providers = (config.Preferences.Providers ?? new DeviceProviderPreferences()).Normalize() });
    private DeviceConfigurationPackage Read(ConfigurationPointer pointer)
    {
        var config = DeviceConfigurationCodec.Read(store.Read(pointer).Files);
        if (config.Bindings.InstallationId != installation) throw new IOException("別のインストールの取得経路は使用できません");
        return config;
    }
    private void Validate(ProviderTransitionRecord? value)
    {
        if (value is not { Version: 2 } || !DeviceProviderRoutingDefinition.Valid(value.Definition) ||
            value.Installation != installation || !DeviceServiceProtocol.Id(value.Request) ||
            !Pointer(value.Before) || !Pointer(value.After) || value.Before == value.After || value.BeforeRoute == value.AfterRoute ||
            !DeviceConfigurationProtocol.Digest(value.BeforeRoute) || !DeviceConfigurationProtocol.Digest(value.AfterRoute) ||
            !ValidRevision(value.Phase, value.Revision) ||
            (value.Phase == ProviderTransitionPhase.Prepared ? value.Revision != 1 || value.PreviousHash is not null : !DeviceConfigurationProtocol.Digest(value.PreviousHash)) ||
            value.Hash != Hash(value)) throw new IOException("取得経路の切替記録が不正です");
        if (definition is not null && value.Definition != definition)
            throw new IOException("切替記録とサービスの判定方針が一致しません。記録した方針で復旧してください");
        if (Route(Read(value.Before), value.Definition) != value.BeforeRoute || Route(Read(value.After), value.Definition) != value.AfterRoute)
            throw new IOException("取得経路の切替記録と設定世代が一致しません");
    }
    private void RequireDefinition()
    {
        if (definition is null) throw new IOException("記録と一致する取得経路の判定方針が必要です");
    }
    private static bool ValidRevision(ProviderTransitionPhase phase, int revision) => phase switch
    {
        ProviderTransitionPhase.Prepared => revision == 1,
        ProviderTransitionPhase.StartingTarget => revision == 2,
        ProviderTransitionPhase.TargetReady => revision == 3,
        ProviderTransitionPhase.Committed => revision == 4,
        ProviderTransitionPhase.StartingSource => revision is >= 2 and <= 4,
        ProviderTransitionPhase.SourceReady => revision is >= 3 and <= 5,
        ProviderTransitionPhase.RolledBack => revision is >= 4 and <= 6,
        ProviderTransitionPhase.RecoveredSource => revision is >= 2 and <= 6,
        _ => false
    };
    private ConfigurationPointer Active()
    {
        var active = store.ReadActive() ?? throw new IOException("採用済み設定がありません");
        _ = Read(active.Pointer); return active.Pointer;
    }
    public ProviderTransitionRecord Begin(string request, ConfigurationPointer before, ConfigurationPointer after)
    {
        lock (gate)
        {
            RequireDefinition();
            if (pending is not null || File.Exists(path)) throw new IOException("前の取得経路切替を復旧してください");
            if (!DeviceServiceProtocol.Id(request) || !Pointer(before) || !Pointer(after) || before == after || Active() != before)
                throw new IOException("取得経路の切替元設定が変更されています");
            var value = new ProviderTransitionRecord(2, installation, request, before, after, definition!, Route(Read(before), definition!), Route(Read(after), definition!),
                ProviderTransitionPhase.Prepared, 1, null, "");
            value = value with { Hash = Hash(value) }; Validate(value);
            ServiceEnvironment.Write(path, value); pending = value; return Contract.Copy(value);
        }
    }
    public void Check(ProviderTransitionRecord expected)
    {
        lock (gate)
        {
            RequireDefinition();
            Validate(expected);
            if (pending?.Hash != expected.Hash || !File.Exists(path)) throw new IOException("取得経路の切替記録が変更されています");
            var disk = ServiceEnvironment.Read<ProviderTransitionRecord>(path); Validate(disk);
            if (disk.Hash != expected.Hash) throw new IOException("取得経路の切替記録が変更されています");
        }
    }
    private ProviderTransitionRecord Advance(ProviderTransitionRecord expected, ProviderTransitionPhase phase)
    {
        lock (gate)
        {
            Check(expected);
            var allowed = (expected.Phase, phase) switch
            {
                (ProviderTransitionPhase.Prepared, ProviderTransitionPhase.StartingTarget) => true,
                (ProviderTransitionPhase.StartingTarget, ProviderTransitionPhase.TargetReady) => true,
                (ProviderTransitionPhase.TargetReady, ProviderTransitionPhase.Committed) => true,
                (ProviderTransitionPhase.Prepared or ProviderTransitionPhase.StartingTarget or ProviderTransitionPhase.TargetReady,
                    ProviderTransitionPhase.StartingSource) => true,
                (ProviderTransitionPhase.StartingSource, ProviderTransitionPhase.SourceReady) => true,
                (ProviderTransitionPhase.SourceReady, ProviderTransitionPhase.RolledBack) => true,
                (ProviderTransitionPhase.Prepared or ProviderTransitionPhase.StartingSource or ProviderTransitionPhase.SourceReady,
                    ProviderTransitionPhase.RecoveredSource) => true,
                _ => false
            };
            if (!allowed || Active() != (phase == ProviderTransitionPhase.Committed ? expected.After : expected.Before))
                throw new IOException("取得経路の切替順序または採用設定が一致しません");
            var value = expected with { Phase = phase, Revision = expected.Revision + 1, PreviousHash = expected.Hash, Hash = "" };
            value = value with { Hash = Hash(value) }; Validate(value);
            ServiceEnvironment.Write(path, value); pending = value; return Contract.Copy(value);
        }
    }
    // The source/target hash describes the selected compiled policy, not proof of coexistence.
    // The caller must validate resource exclusions and persist its configuration transaction first.
    public async Task<IDeviceControlDriver[]> SwitchAsync(DeviceControlRuntime runtime, string owner, string[] remove,
        Func<IDeviceDriverProvider[]> create, CancellationToken ct, bool rollback = false, string[]? expectedProviders = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        ProviderTransitionRecord expected;
        lock (gate)
        {
            expected = pending ?? throw new IOException("取得経路の切替記録がありません"); Check(expected);
            if (switching || Active() != expected.Before || (rollback
                ? expected.Phase is not (ProviderTransitionPhase.Prepared or ProviderTransitionPhase.StartingTarget or ProviderTransitionPhase.TargetReady)
                : expected.Phase != ProviderTransitionPhase.Prepared)) throw new IOException("取得経路の切替順序が不正です");
            ct.ThrowIfCancellationRequested(); switching = true;
        }
        try
        {
            var drivers = await runtime.ReplaceProvidersAsync(owner, remove, () =>
            {
                // ReplaceProviders calls this only after all old handles were successfully closed.
                expected = Advance(expected, rollback ? ProviderTransitionPhase.StartingSource : ProviderTransitionPhase.StartingTarget);
                return create();
            }, ct, expectedProviders).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ = Advance(expected, rollback ? ProviderTransitionPhase.SourceReady : ProviderTransitionPhase.TargetReady);
            return drivers;
        }
        finally { lock (gate) switching = false; }
    }
    public ProviderTransitionRecord Commit(ProviderTransitionRecord expected) => Advance(expected, ProviderTransitionPhase.Committed);
    public ProviderTransitionRecord ConfirmRollback(ProviderTransitionRecord expected) => Advance(expected, ProviderTransitionPhase.RolledBack);

    public ProviderTransitionRecovery Recovery()
    {
        lock (gate)
        {
            var value = pending ?? throw new IOException("取得経路の切替記録がありません"); Check(value); var active = Active();
            if (active != value.Before && active != value.After) throw new IOException("取得経路の切替と異なる設定世代が採用されています");
            if (active == value.After)
                return value.Phase switch
                {
                    ProviderTransitionPhase.TargetReady => ProviderTransitionRecovery.Target,
                    ProviderTransitionPhase.Committed => ProviderTransitionRecovery.FinalizedTarget,
                    _ => throw new IOException("新経路の準備完了前に設定世代が変更されています")
                };
            return value.Phase switch
            {
                ProviderTransitionPhase.Prepared or ProviderTransitionPhase.StartingSource or ProviderTransitionPhase.SourceReady => ProviderTransitionRecovery.Source,
                ProviderTransitionPhase.StartingTarget or ProviderTransitionPhase.TargetReady => ProviderTransitionRecovery.TargetThenSource,
                ProviderTransitionPhase.RolledBack or ProviderTransitionPhase.RecoveredSource => ProviderTransitionRecovery.FinalizedSource,
                _ => throw new IOException("確定した取得経路と採用設定が一致しません")
            };
        }
    }
    // Only the cold-start executor calls this. Its callback must finish persisted obligations AND
    // close every handle before returning. A failed callback never advances to the other route.
    internal async Task RecoverAsync(Func<DeviceConfigurationPackage, CancellationToken, Task> recoverAndClose, CancellationToken ct,
        Action<ProviderTransitionRecord>? finalizeApplication = null)
    {
        ProviderTransitionRecord expected; ProviderTransitionRecovery recovery;
        lock (gate)
        {
            if (switching) throw new IOException("取得経路の切替中です");
            expected = pending ?? throw new IOException("取得経路の切替記録がありません");
            recovery = Recovery(); ct.ThrowIfCancellationRequested(); switching = true;
        }
        try
        {
            async Task RecoverRoute(ConfigurationPointer pointer)
            {
                ct.ThrowIfCancellationRequested(); Check(expected);
                if (Recovery() != recovery) throw new IOException("復旧中に採用設定が変更されています");
                await recoverAndClose(Read(pointer), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); Check(expected);
                if (Recovery() != recovery) throw new IOException("復旧中に採用設定が変更されています");
            }
            if (recovery == ProviderTransitionRecovery.TargetThenSource)
            {
                await RecoverRoute(expected.After).ConfigureAwait(false);
                expected = Advance(expected, ProviderTransitionPhase.StartingSource);
                recovery = Recovery();
            }
            if (recovery is ProviderTransitionRecovery.Target or ProviderTransitionRecovery.FinalizedTarget)
            {
                await RecoverRoute(expected.After).ConfigureAwait(false);
                if (expected.Phase == ProviderTransitionPhase.TargetReady) expected = Commit(expected);
            }
            else
            {
                await RecoverRoute(expected.Before).ConfigureAwait(false);
                if (recovery != ProviderTransitionRecovery.FinalizedSource) expected = Advance(expected, ProviderTransitionPhase.RecoveredSource);
            }
            // The configuration application record must agree before the route intent is removed.
            finalizeApplication?.Invoke(Contract.Copy(expected));
            Complete(expected);
        }
        finally { lock (gate) switching = false; }
    }
    public void Complete(ProviderTransitionRecord expected)
    {
        lock (gate)
        {
            Check(expected);
            if (Recovery() is not (ProviderTransitionRecovery.FinalizedSource or ProviderTransitionRecovery.FinalizedTarget))
                throw new IOException("取得経路の確定を確認するまで切替記録を保持します");
            ServiceEnvironment.NoLinks(path); File.Delete(path); pending = null;
        }
    }
}
