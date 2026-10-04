using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority : IDeviceLightingPersonalization
{
    private sealed record LightingLibraryView(string Generation, LightingConfiguration Lighting, EndpointReference[] Registered);
    private LightingLibraryView lightingLibrary = null!;
    // One immutable publication keeps the generation and catalog consistent for no-I/O runtime validation.
    private void PublishLightingLibrary()
    {
        Volatile.Write(ref lightingLibrary, new(generation.Id, Contract.Copy(configuration.Lighting),
            configuration.Devices.Devices.SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting).Select(e => new EndpointReference(d.Id, e.Id))).ToArray()));
        PublishCoolingGroups();
        MaintainConfigurationHistory();
    }
    private static bool Same<T>(T a, T b) => SettingsTransfer.Canonical(a) == SettingsTransfer.Canonical(b);
    private static LightingConfiguration NormalizeLightingRevisions(LightingConfiguration old, LightingConfiguration next)
    {
        var groups = next.Groups.Select(g =>
        {
            var previous = old.Groups.SingleOrDefault(p => p.Id == g.Id);
            return g with { Revision = previous is null ? 1 : Same(previous with { Revision = 1 }, g with { Revision = 1 }) ? previous.Revision : checked(previous.Revision + 1) };
        }).ToArray();
        var scenes = next.Scenes.Select(s =>
        {
            var previous = old.Scenes.SingleOrDefault(p => p.Id == s.Id);
            return s with { Revision = previous is null ? 1 : Same(previous with { Revision = 1 }, s with { Revision = 1 }) ? previous.Revision : checked(previous.Revision + 1) };
        }).ToArray();
        next = next with { Groups = groups, Scenes = scenes, Revision = old.Revision };
        return Same(old, next) ? next : next with { Revision = checked(old.Revision + 1) };
    }
    private void PersistLightingLibrary(LightingConfiguration lighting) => PersistConfiguration(configuration with { Lighting = lighting }, "照明設定");
    // Caller holds access. Only non-actuating component edits use this path; no driver is reconfigured.
    private void PersistConfiguration(DeviceConfigurationPackage next, string label)
    {
        var files = DeviceConfigurationCodec.Write(next); _ = DeviceConfigurationCodec.Read(files);
        var before = generation; var staged = store.Stage(files);
        if (staged.ExpectedCurrent != before) throw new IOException(label + "の保存世代が競合しています");
        try { store.Activate(staged); }
        catch (Exception original)
        {
            ConfigurationPointer? actual;
            try { actual = store.ReadActive()?.Pointer; }
            catch (Exception read) { failure = label + "の保存状態が不明です。サービス再起動で確認してください"; throw new AggregateException(failure, original, read); }
            if (actual != staged.Target)
            {
                if (actual != before) failure = label + "の保存世代を確認できません。サービス再起動で確認してください";
                throw;
            }
        }
        configuration = Contract.Copy(next); generation = staged.Target; PublishLightingLibrary();
    }
    public async Task<LightingLibraryReply> SaveLightingLibraryAsync(LightingLibraryRequest request, CancellationToken ct)
    {
        if (!DeviceLightingLibrary.Valid(request)) throw new IOException("照明ライブラリの要求が不正です");
        await access.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested(); CheckCurrent();
            if (applyingConfiguration || request.ExpectedGeneration != generation.Id) throw new IOException("編集元の照明設定が変更されました。再取得してください");
            if (DeviceLightingLibrary.Personalization(request.Operation)) return SaveLightingPersonalization(request);
            var old = configuration.Lighting; long previous;
            var groupOperation = request.Operation is LightingLibraryOperation.SaveGroup or LightingLibraryOperation.DeleteGroup;
            previous = groupOperation ? old.Groups.SingleOrDefault(g => g.Id == request.Id)?.Revision ?? 0 :
                old.Scenes.SingleOrDefault(s => s.Id == request.Id)?.Revision ?? 0;
            if (request.ExpectedRevision != previous) throw new IOException("グループまたはシーンの改訂が変更されました");
            if (request.Operation == LightingLibraryOperation.SaveGroup && previous == 0 && old.Groups.Length >= DeviceLightingLibrary.MaximumGroups)
                throw new IOException($"{request.Group!.Name}: グループは{DeviceLightingLibrary.MaximumGroups}件までです。不要なグループを削除してください");
            if (request.Operation == LightingLibraryOperation.SaveScene)
            {
                if (previous == 0 && old.Scenes.Length >= DeviceLightingLibrary.MaximumScenes)
                    throw new IOException($"{request.Scene!.Name}: シーンは{DeviceLightingLibrary.MaximumScenes}件までです。不要なシーンを削除してください");
                if (request.Scene!.ShowOnM5 && old.Scenes.Count(s => s.Id != request.Id && s.ShowOnM5) >= DeviceLightingLibrary.MaximumM5Scenes)
                    throw new IOException($"{request.Scene.Name}: M5GOに表示するシーンは{DeviceLightingLibrary.MaximumM5Scenes}件までです。表示するシーンを減らしてください");
            }
            var revision = checked(previous + 1); LightingConfiguration next;
            if (groupOperation)
            {
                var groups = old.Groups.Where(g => g.Id != request.Id).ToList();
                if (request.Operation == LightingLibraryOperation.SaveGroup) groups.Add(Contract.Copy(request.Group! with { Revision = revision }));
                // A restore baseline belongs to the exact old membership and must never acquire new targets by editing.
                var changedTargets = request.Group is null || !old.Groups.Any(g => g.Id == request.Id && g.Targets.ToHashSet().SetEquals(request.Group.Targets));
                next = old with { Groups = groups.ToArray(), Adjustments = changedTargets ? old.Adjustments.Where(a => a.GroupId != request.Id).ToArray() : old.Adjustments };
            }
            else
            {
                var scenes = old.Scenes.Where(s => s.Id != request.Id).ToList();
                if (request.Operation == LightingLibraryOperation.SaveScene) scenes.Add(Contract.Copy(DeviceLightingSceneRequirements.Stamp(request.Scene! with { Revision = revision })));
                next = old with { Scenes = scenes.ToArray() };
            }
            next = next with { Revision = checked(old.Revision + 1) };
            PersistLightingLibrary(next);
            return new(Outcome.Confirmed, "照明のグループ・シーンを保存しました", generation.Id, request.Id, revision);
        }
        finally { access.Release(); }
    }
    public LightingPresetPlan ResolveLightingPreset(LightingPresetRequest request)
    {
        if (!DeviceLightingLibrary.Valid(request)) throw new IOException("照明の呼出し条件が不正です");
        var view = Volatile.Read(ref lightingLibrary);
        if (failure is not null || request.ExpectedGeneration != view.Generation) throw new IOException(failure ?? "照明設定が変更されました。再取得してください");
        var catalog = view.Lighting; SynchronizedLighting[] parts;
        if (request.Kind == LightingPresetKind.Group)
        {
            var group = catalog.Groups.SingleOrDefault(g => g.Id == request.Id);
            if (group is null || group.Revision != request.ExpectedRevision) throw new IOException("照明グループの改訂が一致しません");
            parts = [new(group.Targets, request.Program!)];
        }
        else
        {
            var scene = catalog.Scenes.SingleOrDefault(s => s.Id == request.Id);
            if (scene is null || scene.Revision != request.ExpectedRevision) throw new IOException("照明シーンの改訂が一致しません");
            parts = [.. scene.Individual.Select(i => new SynchronizedLighting([i.Target], i.Program)), .. scene.Synchronized];
        }
        return new(catalog.Revision, Contract.Copy(request), Contract.Copy(parts));
    }
    public string? CheckLightingPreset(LightingPresetPlan plan) => failure ??
        (Volatile.Read(ref lightingLibrary).Lighting.Revision != plan.LibraryRevision ? "照明グループ・シーンが編集されました。再取得してください" : null);
}
