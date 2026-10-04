using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsLightingPersonalization => client.Hello?.Features.Contains(DeviceLightingLibrary.PersonalizationFeature) == true;
    public async Task<DeviceOperationResult> ApplyLightingLevelAsync(DeviceLightingLevelEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (client.Hello?.Session != edit.Session) throw new IOException("明るさを編集した接続が変更されました。再取得してください");
            return await ApplyLightingCoreAsync(edit.Lighting, "PC", ct).ConfigureAwait(false);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceOperationResult> ApplyLightingAsync(DeviceLightingEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyLightingCoreAsync(edit, "PC", ct).ConfigureAwait(false);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    private async Task<DeviceOperationResult> ApplyLightingCoreAsync(DeviceLightingEdit edit, string source, CancellationToken ct)
    {
        EnsureOpen();
        if (configuration?.Generation != edit.Version.ConfigurationGeneration || client.Hello?.Epoch != edit.Version.Epoch)
            throw new InvalidOperationException("接続または照明設定が変更されました。現在の設定を読み込んでください");
        if (edit.Operation is not (DeviceOperation.SetLighting or DeviceOperation.StopLighting or DeviceOperation.AdjustLighting or DeviceOperation.ApplyLightingPreset))
            throw new ArgumentException("照明の操作が不正です");
        var reply = await client.SendAsync(client.Request(edit.Operation, source: source, topology: edit.Version.Topology, targets: edit.Version.Targets,
            lighting: edit.Program, lightingAdjustment: edit.Adjustment, lightingPreset: edit.Preset), ct).ConfigureAwait(false);
        var result = reply.Result ?? throw new IOException(reply.Error ?? "照明の操作結果を取得できません");
        var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
        lock (gate) configuration = next;
        await ReadStateAsync(ct).ConfigureAwait(false);
        return result;
    }
    public async Task<LightingLibraryReply> SaveLightingAsync(LightingLibraryRequest request, CancellationToken ct = default)
    {
        var hello = client.Hello ?? throw new IOException("サービスへ接続されていません");
        return await SaveLightingEditAsync(new(hello.Epoch, hello.Session, request), ct).ConfigureAwait(false);
    }
    public async Task<LightingLibraryReply> SaveLightingEditAsync(DeviceLightingLibraryEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (client.Hello?.Epoch != edit.Epoch || client.Hello.Session != edit.Session)
                throw new IOException("照明を編集した接続が変更されました。再取得してください");
            var request = edit.Request;
            if (configuration?.Generation != request.ExpectedGeneration) throw new InvalidOperationException("照明設定が変更されました。現在の設定を読み込んでください");
            var reply = await client.SaveLightingLibraryAsync(request, ct).ConfigureAwait(false);
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
            lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false);
            return reply;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public static DeviceLightingEdit Preset(DeviceWorkspaceSnapshot snapshot, LightingPresetKind kind, string id, LightingProgram? program = null)
    {
        if (!snapshot.Connected || !double.IsFinite(snapshot.ElapsedSeconds) || snapshot.ElapsedSeconds is < 0 or >= 3 || snapshot.State.Releasing || snapshot.State.ConfigurationChanging)
            throw new IOException("照明を呼び出す前にサービスの状態を更新してください");
        var config = snapshot.Configuration.Configuration.Lighting;
        EndpointReference[] targets; long revision;
        if (kind == LightingPresetKind.Group)
        {
            var group = config.Groups.SingleOrDefault(g => g.Id == id) ?? throw new IOException("照明グループがありません");
            targets = group.Targets; revision = group.Revision;
        }
        else
        {
            var scene = config.Scenes.SingleOrDefault(s => s.Id == id) ?? throw new IOException("照明シーンがありません");
            targets = scene.Individual.Select(i => i.Target).Concat(scene.Synchronized.SelectMany(s => s.Targets)).ToArray(); revision = scene.Revision;
        }
        var versions = targets.Select(t => snapshot.State.Endpoints.SingleOrDefault(e => e.Target == t) ?? throw new IOException("照明対象を再取得してください"))
            .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        return new(new(snapshot.State.Inventory.Epoch, snapshot.State.Inventory.TopologyRevision, snapshot.Configuration.Generation, versions),
            DeviceOperation.ApplyLightingPreset, Preset: new(kind, snapshot.Configuration.Generation, id, revision, program));
    }
}
