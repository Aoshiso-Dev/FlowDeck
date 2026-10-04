namespace FlowDeck.Core;

public record HubLightAddress(string HubId, string Channel);
public record BoardLightAddress(string Identity, byte Zone);
public record LightingGroup(string Id, string Name, HubLightAddress[] Hub, BoardLightAddress[] Board);
public record LightingGroupCollection(int Version = 1, LightingGroup[]? Groups = null, BoardLightAddress[]? SyncTargets = null);

public static class LightingGroups
{
    public static string? Validate(LightingGroup? value)
    {
        if (value is null || !Guid.TryParseExact(value.Id, "N", out var id) || id.ToString("N") != value.Id || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 40 || value.Name.Any(char.IsControl))
            return "グループの名前または識別子が不正です";
        if (value.Hub is null || value.Board is null || value.Hub.Length > 5 || value.Board.Length > 8 || value.Hub.Length + value.Board.Length == 0)
            return "グループの対象を選択してください";
        if (value.Hub.Any(h => h is null || !Identity(h.HubId) || h.Channel is not ("led1" or "led2" or "led3" or "led4" or "led5")) || value.Hub.Distinct().Count() != value.Hub.Length ||
            value.Board.Any(b => b is null || !Identity(b.Identity) || b.Zone > 7) || value.Board.Distinct().Count() != value.Board.Length)
            return "グループの対象が不正または重複しています";
        return null;
    }
    public static string? Validate(LightingGroupCollection? value) => value?.Version != 1 || value.Groups is null || value.Groups.Length > 32 ||
        (value.SyncTargets is not null && (value.SyncTargets.Length > 8 || value.SyncTargets.Any(b => b is null || !Identity(b.Identity) || b.Zone > 7) || value.SyncTargets.Distinct().Count() != value.SyncTargets.Length)) ||
        value.Groups.Any(g => Validate(g) is not null) || value.Groups.Select(g => g.Id).Distinct().Count() != value.Groups.Length ||
        value.Groups.Select(g => g.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Groups.Length ? "グループ保存形式が不正または重複しています" : null;
    private static bool Identity(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 2048 && !text.Any(char.IsControl);
    public static HubLightAddress[] HubTargets(LightingScene scene) => scene.Hub.Select(h => new HubLightAddress(h.HubId, h.Channel))
        .Concat(scene.Sync is { } s ? s.Channels.Select(c => new HubLightAddress(s.HubId, c)) : []).ToArray();
    public static BoardLightAddress[] BoardTargets(LightingScene scene) => scene.Board.Select(b => new BoardLightAddress(b.Identity, b.Zone))
        .Concat(scene.Sync is { } s ? SyncTargets(s) : []).ToArray();
    public static BoardLightAddress[] SyncTargets(SceneSyncLight sync) => sync.Zones.Select(z => new BoardLightAddress(sync.BoardIdentity,z))
        .Concat((sync.Memory ?? []).Select(id => new BoardLightAddress(id,0))).ToArray();
    public static bool Contains(LightingGroup group, SceneSyncLight sync) => sync.Channels.All(c => group.Hub.Contains(new(sync.HubId, c))) &&
        SyncTargets(sync).All(group.Board.Contains);
    public static bool SameTargets(LightingGroup group, LightingScene scene) => group.Hub.ToHashSet().SetEquals(HubTargets(scene)) && group.Board.ToHashSet().SetEquals(BoardTargets(scene));
    public static bool Overlaps(LightingScene left, LightingScene right) => HubTargets(left).Intersect(HubTargets(right)).Any() || BoardTargets(left).Intersect(BoardTargets(right)).Any();
    public static LightingScene Select(LightingScene source, LightingGroup? group)
    {
        if (LightingScenes.Validate(source) is { } invalid) throw new ArgumentException(invalid);
        if (group is null) return Contract.Copy(source);
        if (Validate(group) is { } invalidGroup) throw new ArgumentException(invalidGroup);
        var sync = source.Sync;
        if (sync is not null)
        {
            var channels = sync.Channels.Where(c => group.Hub.Contains(new(sync.HubId, c))).ToArray();
            var zones = sync.Zones.Where(z => group.Board.Contains(new(sync.BoardIdentity, z))).ToArray();
            var memory = sync.Memory?.Where(m => group.Board.Contains(new(m,0))).ToArray();
            var selectedBoard = zones.Length + (memory?.Length ?? 0);
            if (sync.Channels.Length > 0 && (channels.Length == 0) != (selectedBoard == 0)) throw new InvalidOperationException("同期シーンはNZXTと同期対象を一緒に選択してください");
            sync = channels.Length + selectedBoard == 0 ? null : sync with { HubId = channels.Length == 0 ? "" : sync.HubId,
                Channels = channels, Zones = zones, BoardIdentity=zones.Length == 0 ? "" : sync.BoardIdentity, Memory=memory };
        }
        var selected = source with { Hub = source.Hub.Where(h => group.Hub.Contains(new(h.HubId, h.Channel))).ToArray(),
            Board = source.Board.Where(b => group.Board.Contains(new(b.Identity, b.Zone))).ToArray(), Sync = sync };
        if (LightingScenes.Validate(selected) is { } error) throw new InvalidOperationException("このグループに呼び出せるシーン設定がありません。" + error);
        return Contract.Copy(selected);
    }
}
