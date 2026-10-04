namespace FlowDeck.Core;

public record HubPortLighting(string HubId, HubLightSetting? Effect, Outcome Outcome, DateTimeOffset? SentAt = null);
public record SceneHubLight(string HubId, string Channel, HubLightSetting Effect);
public record SceneBoardLight(string Identity, byte Zone, BoardLightSetting Effect);
public record SceneSyncLight(string HubId, string[] Channels, string BoardIdentity, byte[] Zones, string Mode, RgbColor Color, int Speed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? Memory = null);
public record LightingScene(string Id, string Name, SceneHubLight[] Hub, SceneBoardLight[] Board, SceneSyncLight? Sync = null);
public record LightingSceneCollection(int Version = 1, LightingScene[]? Scenes = null);

public static class LightingScenes
{
    public static string? Validate(LightingScene? scene)
    {
        if (scene is null || !Guid.TryParseExact(scene.Id, "N", out _) || string.IsNullOrWhiteSpace(scene.Name) ||
            scene.Name.Length > 40 || scene.Name.Any(char.IsControl)) return "シーンの名前または識別子が不正です";
        if (scene.Hub is null || scene.Board is null || scene.Hub.Length > 5 || scene.Board.Length > 8) return "シーンの対象数が不正です";
        if (scene.Hub.Any(h => h is null || !Identity(h.HubId) || !Channel(h.Channel) || !HubLighting.Valid(h.Effect)) ||
            scene.Hub.Select(h => (h.HubId, h.Channel)).Distinct().Count() != scene.Hub.Length) return "NZXTの設定が不正または重複しています";
        if (scene.Board.Any(b => b is null || !Identity(b.Identity) || b.Zone > 7 || !BoardLighting.IsValid(b.Effect) ||
                (MemoryLighting.IsIdentity(b.Identity) ? b.Zone != 0 || !MemoryLighting.Valid(b.Effect) : b.Effect.MemoryPalette is not null)) ||
            scene.Board.Select(b => (b.Identity, b.Zone)).Distinct().Count() != scene.Board.Length) return "マザーの設定が不正または重複しています";
        if (scene.Sync is { } s)
        {
            if (s.Channels is not { Length: <= 5 } || (s.Channels.Length > 0 ? !Identity(s.HubId) : s.HubId != "") || s.Zones is null || (s.Zones.Length > 0 ? !Identity(s.BoardIdentity) || MemoryLighting.IsIdentity(s.BoardIdentity) : s.BoardIdentity != "") || s.Channels.Any(c => !Channel(c)) ||
                s.Channels.Distinct().Count() != s.Channels.Length || s.Zones.Length + (s.Memory?.Length ?? 0) is < 1 or > 8 || s.Zones.Any(z => z > 7) || s.Zones.Distinct().Count() != s.Zones.Length ||
                (s.Memory is not null && (s.Memory.Length > 2 || s.Memory.Any(m => !MemoryLighting.IsIdentity(m)) || s.Memory.Distinct().Count() != s.Memory.Length)) ||
                s.Mode is not ("breathing" or "color-cycle" or "fixed" or "off") || s.Color is null || !s.Color.Valid || s.Speed is < 0 or > 4)
                return "照明同期の設定が不正です";
            if (scene.Hub.Any(h => h.HubId == s.HubId && s.Channels.Contains(h.Channel)) || scene.Board.Any(b => b.Identity == s.BoardIdentity && s.Zones.Contains(b.Zone) || s.Memory?.Contains(b.Identity) == true))
                return "同期と個別設定の対象が重複しています";
        }
        if (scene.Hub.Length == 0 && scene.Board.Length == 0 && scene.Sync is null) return "保存できる照明設定がありません";
        return null;
    }
    public static string? Validate(LightingSceneCollection? value)
    {
        if (value?.Version != 1 || value.Scenes is null || value.Scenes.Length > 32) return "シーン保存形式が不正です";
        if (value.Scenes.Any(s => Validate(s) is not null) || value.Scenes.Select(s => s.Id).Distinct().Count() != value.Scenes.Length ||
            value.Scenes.Select(s => s.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Scenes.Length) return "シーンが不正または重複しています";
        return null;
    }
    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && !value.Any(char.IsControl);
    private static bool Channel(string? value) => value is "led1" or "led2" or "led3" or "led4" or "led5";
}

public sealed partial class ControlCoordinator
{
    private void MigrateLightingPorts()
    {
        if (settings.LightingPorts.Count != 0 || settings.Hardware.Hub is not { } identity || settings.Lighting.Outcome != Outcome.WrittenUnverified) return;
        var last = settings.Lighting;
        var effect = last.Effect ?? (last.LastSent is "blue" or "white" or "purple" or "off" ? HubLighting.Preset(last.LastSent) : null);
        if (!HubLighting.Valid(effect)) return;
        var ports = (last.Channels ?? settings.Hardware.Leds ?? []).Where(c => settings.Hardware.Leds?.Contains(c) == true).Distinct()
            .ToDictionary(c => c, c => new HubPortLighting(identity.Id, effect, Outcome.WrittenUnverified, last.SentAt));
        settings = settings with { LightingPorts = ports };
    }
    private void RecordLightingPortsLocked(string[] channels, HubLightSetting setting, Outcome outcome)
    {
        if (settings.Hardware.Hub is not { } identity) return;
        var ports = new Dictionary<string, HubPortLighting>(settings.LightingPorts);
        foreach (var channel in channels) ports[channel] = new(identity.Id,
            outcome == Outcome.WrittenUnverified ? Contract.Copy(setting) : null, outcome, clock.GetUtcNow());
        settings = settings with { LightingPorts = ports };
    }
    public string? LightingTargetError(string hubId, string[] channels)
    {
        lock (sync)
        {
            if (settings.Hardware.Hub?.Id != hubId || !ready || !Fresh(telemetryAt) || connecting || stopping || suspended || lightingBusy)
                return "シーンのNZXT機器へ接続できません";
            return ValidLightingChannelsLocked(channels) ? WiringErrorLocked() : "シーンの照明ポートが見つかりません";
        }
    }
}
