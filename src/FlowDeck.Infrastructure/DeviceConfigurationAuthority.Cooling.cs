using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private sealed record CoolingAttempt(string Ticket, CoolingPolicyChange Change, bool Resume);
    private readonly Dictionary<EndpointReference, CoolingAttempt> coolingAttempts = [];
    private readonly Dictionary<EndpointReference, long> coolingOrders = [];
    private Dictionary<EndpointReference, bool> coolingResumeIntent = [];
    public async Task PauseCoolingForCalibrationAsync(EndpointReference target,CancellationToken ct)
    {
        await access.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested(); CheckCurrent();
            var endpoint=configuration.Cooling.Endpoints.SingleOrDefault(c=>c.Target==target);
            if(endpoint is not null) PersistCoolingEndpoint(endpoint with { ResumeOnStart=false });
            coolingAttempts.Remove(target); coolingResumeIntent[target]=false;
        }
        finally { access.Release(); }
    }
    public CoolingEndpointConfiguration? SavedCoolingConfiguration(EndpointReference target)
    {
        var current = configuration;
        var endpoint = current.Cooling.Endpoints.SingleOrDefault(c => c.Target == target);
        return endpoint is null ? null : Contract.Copy(endpoint with { ResumeOnStart = failure is null && endpoint.ResumeOnStart && current.Preferences.Startup.ResumeControl });
    }
    private CoolingEndpointConfiguration CheckCoolingChange(CoolingPolicyChange change)
    {
        if (configuration.Bindings.InstallationId != installation || change.Order < 1) throw new IOException("冷却設定の保存先が一致しません");
        var endpoint = configuration.Cooling.Endpoints.SingleOrDefault(c => c.Target == change.Target);
        if (endpoint is null || !DeviceConfigurationRules.SameSafety(endpoint.Safety, change.Safety) ||
            change.Policy is { } policy && DeviceCoolingPolicy.Validate(policy, endpoint.Safety) is not null)
            throw new IOException("適用した冷却方針・安全条件を確認できません");
        var required = (change.Policy is null ? [] : DeviceCoolingPolicy.Required(change.Policy, change.Safety).Select(s => s.DeviceId))
            .Append(change.Target.DeviceId).Distinct().Order().ToArray();
        var references = (change.Policy is null ? [] : DeviceCoolingPolicy.Required(change.Policy, change.Safety)
                .Select(t => (Target: t, Feature: DeviceFeature.Temperature)))
            .Append((Target: change.Target, Feature: change.Policy?.Mode == FanMode.Auto ? DeviceFeature.AutomaticDuty : DeviceFeature.FixedDuty));
        if (change.Policy is null ? change.Bindings.Length != 0 :
            !required.SequenceEqual(change.Bindings.Select(b => b.DeviceId).Order()) || references.Any(r =>
                change.Bindings.SingleOrDefault(b => b.DeviceId == r.Target.DeviceId) is not { } binding ||
                !configuration.Bindings.Devices.Contains(binding) && !CurrentBindingMatches(r.Target, r.Feature, binding)))
            throw new IOException("適用した冷却方針の実機関連付けが変更されました");
        var candidate = configuration with { Cooling = configuration.Cooling with { Endpoints = configuration.Cooling.Endpoints.Select(c =>
            c.Target == change.Target ? c with { SavedPolicy = change.Policy, ResumeOnStart = false } : c).ToArray() } };
        // Check registration references and serialized component limits before allowing the hardware change.
        _ = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(candidate));
        return endpoint;
    }
    private void PersistCoolingEndpoint(CoolingEndpointConfiguration endpoint)
    {
        var next = configuration with { Cooling = configuration.Cooling with {
            Endpoints = configuration.Cooling.Endpoints.Select(c => c.Target == endpoint.Target ? endpoint : c).ToArray() } };
        if (SettingsTransfer.Canonical(configuration.Cooling) == SettingsTransfer.Canonical(next.Cooling)) return;
        var previous = generation;
        var staged = store.Stage(DeviceConfigurationCodec.Write(next));
        if (staged.ExpectedCurrent != previous) throw new IOException("冷却設定の保存世代が競合しています");
        try { store.Activate(staged); }
        catch (Exception original)
        {
            // A filesystem exception alone cannot prove whether the atomic replacement committed.
            ConfigurationPointer? actual;
            try { actual = store.ReadActive()?.Pointer; }
            catch (Exception read) { failure = "冷却設定の保存状態が不明です。サービス再起動で確認してください"; throw new AggregateException(failure, original, read); }
            if (actual != staged.Target)
            {
                if (actual != previous) failure = "冷却設定の保存世代を確認できません。サービス再起動で確認してください";
                throw;
            }
        }
        configuration = Contract.Copy(next); generation = staged.Target;
        PublishLightingLibrary();
    }
    public async Task<string> BeginCoolingChangeAsync(CoolingPolicyChange change, CancellationToken ct)
    {
        await access.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested(); CheckCurrent(); var endpoint = CheckCoolingChange(change);
            if (coolingOrders.GetValueOrDefault(change.Target) >= change.Order) throw new IOException("新しい冷却要求が優先されています");
            // An interrupted or uncertain hardware change must never resurrect the previously saved policy at startup.
            PersistCoolingEndpoint(endpoint with { ResumeOnStart = false });
            var ticket = Guid.NewGuid().ToString("N");
            coolingOrders[change.Target] = change.Order;
            var resume = coolingResumeIntent.GetValueOrDefault(change.Target) && change.Bindings.All(configuration.Bindings.Devices.Contains);
            coolingAttempts[change.Target] = new(ticket, change, resume);
            return ticket;
        }
        finally { access.Release(); }
    }
    public async Task<DevicePolicySaveResult> FinishCoolingChangeAsync(string ticket, bool confirmed)
    {
        await access.WaitAsync().ConfigureAwait(false);
        try
        {
            var attempt = coolingAttempts.Values.SingleOrDefault(a => a.Ticket == ticket);
            if (attempt is null) return new(DevicePolicySaveState.Superseded, "新しい要求の保存を優先しました");
            CheckCurrent(); var endpoint = CheckCoolingChange(attempt.Change);
            if (!confirmed)
            {
                coolingAttempts.Remove(attempt.Change.Target);
                return new(DevicePolicySaveState.Failed, "出力を確認できないため方針を確定せず、起動時再開を保留しました");
            }
            var next = endpoint with { SavedPolicy = attempt.Change.Policy, ResumeOnStart = attempt.Change.Policy is not null && attempt.Resume };
            PersistCoolingEndpoint(next); coolingAttempts.Remove(attempt.Change.Target);
            if (attempt.Change.Policy is null) coolingResumeIntent[attempt.Change.Target] = false;
            return new(DevicePolicySaveState.Saved, attempt.Change.Policy is null ? "機器管理への復帰を保存しました" : "適用した冷却方針を保存しました");
        }
        catch
        {
            // Begin already persisted ResumeOnStart=false. Keep that durable state when commit fails.
            var failed = coolingAttempts.Values.SingleOrDefault(a => a.Ticket == ticket);
            if (failed is not null) coolingAttempts.Remove(failed.Change.Target);
            throw;
        }
        finally { access.Release(); }
    }
}
