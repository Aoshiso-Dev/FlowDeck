using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Shares the GUI owner and applies one common duty to an explicit connected role group.</summary>
public sealed class DeviceDialBackend(DeviceWorkspaceClient client, Func<EndpointReference, bool>? coolingEdited = null,
    Func<EndpointReference, bool>? lightingEdited = null, Func<bool>? unavailable = null) : IDialControlBackend
{
    private sealed record Member(EndpointReference Target, string Role, int? Value, int Minimum, bool Busy, string? Problem, string Version, string Context);
    private sealed record Candidate(DialTarget Display, string Role, Member[] Members);
    internal static bool Fresh(DeviceWorkspaceSnapshot? s) => s is { Connected: true, Session: not null, ExitAttempted: false } &&
        double.IsFinite(s.ElapsedSeconds) && s.ElapsedSeconds is >= 0 and < 3 && !s.State.Releasing && !s.State.ConfigurationChanging;
    internal static string Role(DeviceWorkspaceSnapshot s, EndpointReference target, RegisteredEndpoint endpoint) =>
        endpoint.Role == "pump" || s.Configuration.Configuration.Dashboard.Items.Any(i => i.Target == target && i.Role == "pump") ? "pump" :
        endpoint.Role == "fan" ? "fan" : "";
    public DialSnapshot Read()
    {
        _ = lightingEdited; // Kept for compatibility with existing GUI construction; lighting is no longer a dial target.
        var view = ReadSnapshot(client.Snapshot, coolingEdited);
        return unavailable?.Invoke() == true ? view with { Available = false, Targets = [], Message = "PCで別の操作を実行しています" } : view;
    }
    internal static DialSnapshot ReadSnapshot(DeviceWorkspaceSnapshot? snapshot, Func<EndpointReference, bool>? edited = null)
    {
        if (!Fresh(snapshot)) return new(false, [], "サービスの状態を更新してください");
        var selection = snapshot!.Configuration.Configuration.Preferences.DialDisplay;
        var view = new DialSnapshot(true, [], CpuTempDeciC: Temperature(snapshot, selection?.Cpu, DeviceKind.Processor),
            GpuTempDeciC: Temperature(snapshot, selection?.Gpu, DeviceKind.Graphics), WaterTempDeciC: Temperature(snapshot, WaterTarget(snapshot), DeviceKind.Motherboard));
        var candidates = Candidates(snapshot!, edited).ToArray();
        if (candidates.Any(c => c.Members.Length > 64)) return view with { Available = false, Message = "一括操作は各64端子までです" };
        return view with { Targets = candidates.Select(c => c.Display).ToArray() };
    }
    private static EndpointReference? WaterTarget(DeviceWorkspaceSnapshot s)
    {
        // T_SENSOR1 is the user's saved dashboard alias, not the provider's Temperature #n.
        // Resolve before checking availability so an offline selection never falls through to another sensor.
        var targets = s.Configuration.Configuration.Dashboard.Items.Where(i => i.Name == "T_SENSOR1" && i.Measurement == DeviceFeature.Temperature)
            .Select(i => i.Target).Distinct().Take(2).ToArray();
        return targets.Length == 1 ? targets[0] : null;
    }
    private static int? Temperature(DeviceWorkspaceSnapshot s, EndpointReference? target, DeviceKind kind)
    {
        if (target is null) return null;
        var registered = s.Configuration.Configuration.Devices.Devices.Where(d => d.Id == target.DeviceId).Take(2).ToArray();
        var observed = s.State.Inventory.Devices.Where(d => d.Device.Id == target.DeviceId).Take(2).ToArray();
        if (registered.Length != 1 || observed.Length != 1 || registered[0].Kind != kind || observed[0].Device.Kind != kind ||
            !observed[0].Connected || registered[0].Provider != observed[0].Device.Provider) return null;
        var saved = registered[0].Endpoints.Where(e => e.Id == target.EndpointId).Take(2).ToArray();
        var live = observed[0].Device.Endpoints.Where(e => e.Id == target.EndpointId).Take(2).ToArray();
        if (saved.Length != 1 || live.Length != 1 || saved[0].Kind != EndpointKind.Sensor || live[0].Kind != EndpointKind.Sensor ||
            !live[0].Capabilities.Any(c => c.Feature == DeviceFeature.Temperature && c.Usable)) return null;
        // Temperature is typed as Celsius by the service contract, not inferred from names or RPM.
        // Fresh enforces finite raw Celsius > 0 and < 150. Rounding can reach wire endpoints 0/1500.
        var sample = DeviceDashboard.Sample(s.State, target, DeviceFeature.Temperature);
        return sample is not null && DeviceDashboard.Fresh(sample, s.ElapsedSeconds) ? (int)Math.Round(sample.Value!.Value * 10) : null;
    }
    internal static EndpointReference[] MemberTargets(DeviceWorkspaceSnapshot s, string role) =>
        Members(s, null).Where(m => m.Role == role).Select(m => m.Target).OrderBy(t => t.DeviceId, StringComparer.Ordinal)
            .ThenBy(t => t.EndpointId, StringComparer.Ordinal).ToArray();
    private static IEnumerable<Member> Members(DeviceWorkspaceSnapshot s, Func<EndpointReference, bool>? edited)
    {
        var configuration = s.Configuration.Configuration;
        foreach (var savedDevice in configuration.Devices.Devices)
        {
            var device = s.State.Inventory.Devices.SingleOrDefault(d => d.Connected && d.Device.Id == savedDevice.Id);
            if (device is null) continue;
            foreach (var savedEndpoint in savedDevice.Endpoints.Where(e => e.Kind == EndpointKind.Cooling))
            {
                var target = new EndpointReference(savedDevice.Id, savedEndpoint.Id);
                var role = Role(s, target, savedEndpoint);
                var endpoint = device.Device.Endpoints.SingleOrDefault(e => e.Id == savedEndpoint.Id && e.Kind == EndpointKind.Cooling);
                var setup = configuration.Cooling.Endpoints.SingleOrDefault(e => e.Target == target);
                // Membership is structural. A temporarily unavailable member blocks the whole group.
                if (role.Length == 0 || endpoint is null || !endpoint.Capabilities.Any(c => c.Feature == DeviceFeature.FixedDuty && c.Support == ImplementationSupport.Supported)) continue;
                var configured = setup is not null && DeviceConfigurationRules.SafetyReadiness(setup.Safety) is null;
                // Explicit pumps remain visible when setup is missing, but can never be operated.
                if (!configured && role != "pump") continue;
                var execution = s.State.Endpoints.SingleOrDefault(e => e.Target == target && e.Generation == device.Generation);
                var sample = DeviceDashboard.Sample(s.State, target, DeviceFeature.DutyRead);
                int? value = execution is { Owned: true, Outcome: Outcome.Confirmed, ReportedDuty: >= 0 and <= 100 } ? execution.ReportedDuty :
                    sample is not null && DeviceDashboard.Fresh(sample, s.ElapsedSeconds) && sample.Value is >= 0 and <= 100 && double.IsFinite(sample.Value.Value) ?
                        (int)Math.Round(sample.Value.Value) : null;
                var minimum = Math.Clamp(setup?.Safety.Minimum ?? 1, 0, 100);
                var isEdited = edited?.Invoke(target) == true;
                var pending = execution?.Outcome == Outcome.Pending;
                var trial = configured ? setup! with { SavedPolicy = new(FanMode.Manual, Math.Max(minimum, value ?? minimum), SafetySnapshot: setup.Safety) } : null;
                var problem = trial is null ? "ポンプの制御設定が必要です" : isEdited ? "PCの未適用編集を確定または取り消してください" : pending ? "冷却の操作中" :
                    value is null ? "全台の現在値を取得してください" : DeviceCoolingStartup.Problem(trial, configuration.Bindings, s.State, s.Configuration.Candidates, s.ElapsedSeconds);
                var version = SettingsTransfer.Hash(new { s.Session, s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision,
                    configuration = s.Configuration.Generation, execution?.Generation, execution?.PolicyRevision, execution?.Owned, execution?.Outcome,
                    mode = execution?.Cooling?.Mode, faulted = execution?.Cooling?.Faulted, persistence = execution?.Persistence?.State, value });
                // Own value/policy/pending transitions do not revoke a gesture; identity, role, safety and GUI edits do.
                var context = SettingsTransfer.Hash(new { s.Session, s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, target, role,
                    device.Generation, faulted = execution?.Cooling?.Faulted == true, isEdited, configuration.Devices, configuration.Bindings,
                    setup?.Safety, setup?.WiringConfirmed, setup?.Profiles });
                yield return new(target, role, value, minimum, pending, problem, version, context);
            }
        }
    }
    private static IEnumerable<Candidate> Candidates(DeviceWorkspaceSnapshot s, Func<EndpointReference, bool>? edited)
    {
        var all = Members(s, edited).ToArray();
        foreach (var role in new[] { "fan", "pump" })
        {
            var members = all.Where(m => m.Role == role).OrderBy(m => m.Target.DeviceId, StringComparer.Ordinal).ThenBy(m => m.Target.EndpointId, StringComparer.Ordinal).ToArray();
            if (members.Length == 0) continue;
            var values = members.Where(m => m.Value is not null).Select(m => m.Value!.Value).Distinct().ToArray();
            var mixed = values.Length > 1;
            var value = values.Length > 0 ? values.Max() : 0;
            var problem = members.Select(m => m.Problem).FirstOrDefault(p => p is not null);
            var context = SettingsTransfer.Hash(members.Select(m => new { m.Target, m.Context, m.Minimum }).ToArray());
            var version = SettingsTransfer.Hash(new { context, versions = members.Select(m => m.Version).ToArray() });
            var message = problem ?? (mixed ? "確認値は混在・最大値を基準に全台を同じ出力へ" : $"確認 {members.Length}/{members.Length}・回転で全台を調整");
            // Measured RPM is display telemetry, never part of the control version/context.
            var rpms = members.Select(m => DeviceDashboard.Sample(s.State, m.Target, DeviceFeature.Rpm))
                .Where(r => r is not null && DeviceDashboard.Fresh(r, s.ElapsedSeconds) && r.Value is >= 0 and <= 1_000_000)
                .Select(r => (int)Math.Round(r!.Value!.Value)).ToArray();
            yield return new(new(role == "fan" ? "cooling:all-fans" : "cooling:pumps", $"{(role == "fan" ? "全ファン" : "ポンプ")}・{members.Length}台", "cooling",
                value, members.Max(m => m.Minimum), 100, 1, problem is null, members.Any(m => m.Busy), message, version, context, mixed, members.Length,
                rpms.Length > 0 ? rpms.Min() : null, rpms.Length > 0 ? rpms.Max() : null, rpms.Length), role, members);
        }
    }
    public async Task<DialAdjustmentResult> AdjustAsync(string targetId, string expectedVersion, int value)
    {
        var snapshot = client.Snapshot;
        if (!Fresh(snapshot) || unavailable?.Invoke() == true) throw new IOException("サービスの状態を更新してください");
        var candidate = Candidates(snapshot!, coolingEdited).SingleOrDefault(c => c.Display.Id == targetId);
        if (candidate is null || !candidate.Display.Enabled || candidate.Display.Busy || candidate.Display.Version != expectedVersion || candidate.Members.Length > 64)
            throw new InvalidOperationException("対象または設定が変更されました。現在の値を確認してください");
        if (value < candidate.Display.Minimum || value > candidate.Display.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
        var targets = candidate.Members.Select(m => { var e = snapshot!.State.Endpoints.Single(e => e.Target == m.Target); return new DeviceTargetVersion(m.Target, e.Generation, e.PolicyRevision); }).ToArray();
        var edit = new DialCoolingBatchEdit(snapshot!.Session!, snapshot.State.Inventory.Epoch, snapshot.State.Inventory.TopologyRevision,
            snapshot.Configuration.Generation, candidate.Role, targets, value);
        bool Current() => unavailable?.Invoke() != true && ReadSnapshot(client.Snapshot, coolingEdited).Targets.Any(t => t.Id == targetId && t.Enabled && !t.Busy && t.Version == expectedVersion);
        var result = await client.ApplyDialCoolingBatchAsync(edit, Current).ConfigureAwait(true);
        var after = client.Snapshot;
        var actual = Fresh(after) && unavailable?.Invoke() != true ? Candidates(after!, coolingEdited).SingleOrDefault(c => c.Display.Id == targetId) : null;
        var confirmed = targets.Count(t =>
        {
            var matches = result.Targets.Where(r => r.Target == t.Target).ToArray();
            var written = matches.Length == 1 ? matches[0] : null;
            var execution = after?.State.Endpoints.SingleOrDefault(e => e.Target == t.Target);
            return written is { Outcome: Outcome.Confirmed } && written.Generation == t.Generation && written.ReportedDuty == value &&
                (written.Persistence is null || written.Persistence.State == DevicePolicySaveState.Saved) &&
                execution is { Owned: true, Outcome: Outcome.Confirmed } && execution.Generation == written.Generation && execution.PolicyRevision == written.PolicyRevision &&
                execution.ReportedDuty == value && (execution.Persistence is null || execution.Persistence.State == DevicePolicySaveState.Saved);
        });
        if (result.Outcome == Outcome.Confirmed && result.Targets.Length == targets.Length && confirmed == targets.Length &&
            actual is not null && actual.Display.Enabled && !actual.Display.Busy && !actual.Display.ValueMixed && actual.Display.Value == value &&
            actual.Display.ControlContext == candidate.Display.ControlContext && actual.Members.Select(m => m.Target).SequenceEqual(candidate.Members.Select(m => m.Target)))
            return new(DialAdjustmentStatus.Confirmed, $"確認 {confirmed}/{targets.Length}・全台に適用しました", actual.Display.Version);
        return new(DialAdjustmentStatus.Rejected, $"確認 {confirmed}/{targets.Length}・対象ごとの結果をPCで確認してください");
    }
}
