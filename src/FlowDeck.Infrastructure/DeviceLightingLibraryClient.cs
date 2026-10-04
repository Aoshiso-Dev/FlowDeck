using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// GUI and M5 adapters use the same revisioned service commands. Saving never implies applying.
public static class DeviceLightingLibraryClient
{
    public static async Task<LightingLibraryReply> SaveLightingLibraryAsync(this DeviceServicePipeClient client,
        LightingLibraryRequest request, CancellationToken ct = default)
    {
        if (client.Hello?.Features.Contains("lighting-library-v2") != true) throw new IOException("照明の保存機能に対応するサービス更新が必要です");
        if (DeviceLightingLibrary.Personalization(request.Operation) && client.Hello.Features.Contains(DeviceLightingLibrary.PersonalizationFeature) != true)
            throw new IOException("照明の名前・色見本に対応するサービス更新が必要です");
        var reply = await client.SendAsync(client.Request(DeviceOperation.LightingLibrary, lightingLibrary: request), ct).ConfigureAwait(false);
        if (!reply.Success || !DeviceLightingLibrary.Valid(reply.LightingLibrary)) throw new IOException(reply.Error ?? "照明設定の保存結果を取得できません");
        return reply.LightingLibrary!;
    }
    public static Task<LightingLibraryReply> SaveLightingGroupAsync(this DeviceServicePipeClient client, ServiceConfigurationSnapshot snapshot,
        EndpointGroup group, CancellationToken ct = default) => client.SaveLightingLibraryAsync(new(LightingLibraryOperation.SaveGroup, snapshot.Generation,
            group.Id, snapshot.Configuration.Lighting.Groups.SingleOrDefault(g => g.Id == group.Id)?.Revision ?? 0, Group: group), ct);
    public static Task<LightingLibraryReply> SaveLightingSceneAsync(this DeviceServicePipeClient client, ServiceConfigurationSnapshot snapshot,
        DeviceLightingScene scene, CancellationToken ct = default) => client.SaveLightingLibraryAsync(new(LightingLibraryOperation.SaveScene, snapshot.Generation,
            scene.Id, snapshot.Configuration.Lighting.Scenes.SingleOrDefault(s => s.Id == scene.Id)?.Revision ?? 0, Scene: scene), ct);
    public static Task<DeviceServiceReply> ApplyLightingGroupAsync(this DeviceServicePipeClient client, ServiceConfigurationSnapshot snapshot,
        DeviceServiceState state, string id, LightingProgram program, CancellationToken ct = default)
    {
        var group = snapshot.Configuration.Lighting.Groups.SingleOrDefault(g => g.Id == id) ?? throw new IOException("照明グループがありません");
        return Apply(client, snapshot, state, group.Targets, new(LightingPresetKind.Group, snapshot.Generation, id, group.Revision, program), ct);
    }
    public static Task<DeviceServiceReply> ApplyLightingSceneAsync(this DeviceServicePipeClient client, ServiceConfigurationSnapshot snapshot,
        DeviceServiceState state, string id, CancellationToken ct = default)
    {
        var scene = snapshot.Configuration.Lighting.Scenes.SingleOrDefault(s => s.Id == id) ?? throw new IOException("照明シーンがありません");
        var targets = scene.Individual.Select(i => i.Target).Concat(scene.Synchronized.SelectMany(g => g.Targets)).ToArray();
        return Apply(client, snapshot, state, targets, new(LightingPresetKind.Scene, snapshot.Generation, id, scene.Revision), ct);
    }
    private static Task<DeviceServiceReply> Apply(DeviceServicePipeClient client, ServiceConfigurationSnapshot snapshot, DeviceServiceState state,
        EndpointReference[] references, LightingPresetRequest preset, CancellationToken ct)
    {
        if (client.Hello?.Features.Contains("lighting-presets-v2") != true) throw new IOException("照明の呼出し機能に対応するサービス更新が必要です");
        if (state.Inventory.Epoch != client.Hello.Epoch) throw new IOException("サービス起動前の機器一覧です。再取得してください");
        var targets = references.Select(r => state.Endpoints.SingleOrDefault(e => e.Target == r) ?? throw new IOException("照明の対象を再取得してください"))
            .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        return client.SendAsync(client.Request(DeviceOperation.ApplyLightingPreset, topology: state.Inventory.TopologyRevision, targets: targets, lightingPreset: preset), ct);
    }
}
