using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceDialDisplayEdit(string Epoch, string Session, string Generation, long Topology, DeviceDialDisplaySettings Settings);

public sealed record DialCoolingBatchEdit(string Session, string Epoch, long Topology, string ConfigurationGeneration,
    string Role, DeviceTargetVersion[] Targets, int Duty);

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsDialDisplay => client.Hello?.Features.Contains(DeviceDialDisplaySettings.Feature) == true;
    public async Task<DeviceConfigurationReply> SaveDialDisplayAsync(DeviceDialDisplayEdit edit, CancellationToken ct = default)
    {
        edit = Contract.Copy(edit);
        if (!DeviceDialDisplaySettings.Valid(edit.Settings)) throw new ArgumentException("ダイヤルの温度表示設定が不正です");
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!SupportsDialDisplay) throw new IOException("ダイヤルの温度表示設定に対応するサービスへ更新してください");
            if (client.Hello?.Epoch != edit.Epoch || client.Hello.Session != edit.Session || configuration?.Generation != edit.Generation ||
                Snapshot?.State.Inventory.TopologyRevision != edit.Topology)
                throw new IOException("接続またはダイヤル設定が変更されました。再取得してください");
            var reply = await client.SendAsync(client.Request(DeviceOperation.Configuration, configuration: new(DeviceConfigurationOperation.SaveDialDisplay,
                ExpectedGeneration: edit.Generation, TopologyRevision: edit.Topology, DialDisplay: edit.Settings)), ct).ConfigureAwait(false);
            if (!reply.Success || reply.Configuration is null) throw new IOException(reply.Error ?? "ダイヤルの温度表示を保存できません");
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
            lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false);
            return reply.Configuration;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }

    // Uses the existing multi-target SetDuty protocol and the GUI owner; saved groups are never rewritten.
    internal async Task<DeviceOperationResult> ApplyDialCoolingBatchAsync(DialCoolingBatchEdit edit, Func<bool>? current = null, CancellationToken ct = default)
    {
        edit = Contract.Copy(edit);
        if (edit.Role is not ("fan" or "pump") || edit.Duty is < 0 or > 100 || edit.Targets.Length is < 1 or > 64 ||
            edit.Targets.Select(t => t.Target).Distinct().Count() != edit.Targets.Length) throw new ArgumentException("一括操作の対象が不正です");
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            var snapshot = Snapshot;
            if (!DeviceDialBackend.Fresh(snapshot) || snapshot!.Session != edit.Session || snapshot.State.Inventory.Epoch != edit.Epoch ||
                snapshot.State.Inventory.TopologyRevision != edit.Topology || snapshot.Configuration.Generation != edit.ConfigurationGeneration || current?.Invoke() == false)
                throw new InvalidOperationException("接続または設定が変更されました。現在の設定を読み込んでください");
            var members = DeviceDialBackend.MemberTargets(snapshot, edit.Role);
            if (!members.ToHashSet().SetEquals(edit.Targets.Select(t => t.Target))) throw new InvalidOperationException("一括操作の構成が変更されました");
            foreach (var target in edit.Targets)
            {
                var execution = snapshot.State.Endpoints.SingleOrDefault(e => e.Target == target.Target);
                var setup = snapshot.Configuration.Configuration.Cooling.Endpoints.SingleOrDefault(e => e.Target == target.Target);
                if (execution is null || execution.Generation != target.Generation || execution.PolicyRevision != target.ExpectedPolicyRevision ||
                    setup is null || edit.Duty < (setup.Safety.Minimum ?? 1)) throw new InvalidOperationException("端子または出力範囲が変更されました");
                var trial = setup with { SavedPolicy = new(FanMode.Manual, edit.Duty, SafetySnapshot: setup.Safety) };
                if (DeviceCoolingStartup.Problem(trial, snapshot.Configuration.Configuration.Bindings, snapshot.State, snapshot.Configuration.Candidates, snapshot.ElapsedSeconds) is { } problem)
                    throw new InvalidOperationException(problem);
            }
            var reply = await client.SendAsync(client.Request(DeviceOperation.SetDuty, topology: edit.Topology, targets: edit.Targets, duty: edit.Duty), ct).ConfigureAwait(false);
            var result = reply.Result ?? throw new IOException(reply.Error ?? "操作結果を取得できません");
            if (result.Targets.Any(t => t.Persistence is not null))
            {
                var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
                lock (gate) configuration = next;
            }
            await ReadStateAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
