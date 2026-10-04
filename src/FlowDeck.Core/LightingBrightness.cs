using System.Text.Json;

namespace FlowDeck.Core;

public record LightingAdjustmentState(int Version = 1, LightingScene? BaseScene = null, int Brightness = 100, bool IsOff = false, bool Applied = false);

public static class LightingBrightness
{
    public static string? Validate(LightingAdjustmentState? value) => value is null || value.Version != 1 || value.Brightness is < 1 or > 100 ||
        (value.BaseScene is null ? value.IsOff || value.Applied : LightingScenes.Validate(value.BaseScene) is not null) ? "明るさの保存形式が不正です" : null;

    public static string[] Unsupported(LightingScene scene) => scene.Hub.Where(h => h.Effect.Mode is "super-rainbow" or "spectrum-wave")
        .Select(h => "NZXT / " + h.Channel.ToUpperInvariant() + " · " + HubLighting.Name(h.Effect.Mode))
        .Concat(scene.Board.Where(b => b.Effect.Mode != 0 && BoardLighting.Effects.Single(e => e.Id == b.Effect.Mode).Color == false)
            .Select(b => "マザー / " + b.Zone + " · " + BoardLighting.Name(b.Effect.Mode))).ToArray();

    public static LightingScene Build(LightingScene original, int brightness, bool off = false)
    {
        if (LightingScenes.Validate(original) is { } invalid) throw new ArgumentException(invalid);
        if (brightness is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(brightness));
        if (!off && brightness < 100 && Unsupported(original) is { Length: > 0 } unsupported)
            throw new InvalidOperationException("明るさ調整に非対応: " + string.Join("、", unsupported));
        var scale = brightness / 100d;
        return Contract.Copy(original with
        {
            Hub = original.Hub.Select(h => h with { Effect = off ? new("off", []) : h.Effect with { Colors = h.Effect.Colors.Select(c => c.Scale(scale)).ToArray() } }).ToArray(),
            Board = original.Board.Select(b => b with { Effect = off ? b.Effect with { Mode = 0 } : b.Effect with
                { Red = (int)Math.Round(b.Effect.Red * scale), Green = (int)Math.Round(b.Effect.Green * scale), Blue = (int)Math.Round(b.Effect.Blue * scale),
                  MemoryPalette = b.Effect.MemoryPalette is null ? null : MemoryLighting.Palette(MemoryLighting.Colors(b.Effect).Select(c => (byte)Math.Round(c * scale)).ToArray()) } }).ToArray(),
            Sync = original.Sync is { } s ? s with { Mode = off ? "off" : s.Mode, Color = off ? new(0, 0, 0) : s.Color.Scale(scale) } : null
        });
    }

    // Fixed/off synchronization finishes after one write and is then represented by native settings.
    public static bool SameOutput(LightingScene left, LightingScene right)
    {
        static string Canonical(LightingScene scene)
        {
            var hub = scene.Hub.ToList(); var board = scene.Board.ToList(); var sync = scene.Sync;
            if (sync is { Mode: "fixed" or "off" })
            {
                var color = sync.Mode == "off" ? new RgbColor(0, 0, 0) : sync.Color;
                hub.AddRange(sync.Channels.Select(c => new SceneHubLight(sync.HubId, c, new("fixed", [color]))));
                board.AddRange(LightingGroups.SyncTargets(sync).Select(z => new SceneBoardLight(z.Identity, z.Zone, new(1, color.R, color.G, color.B))));
                sync = null;
            }
            return JsonSerializer.Serialize(new
            {
                Hub = hub.OrderBy(h => h.HubId).ThenBy(h => h.Channel).Select(h => h with { Effect = h.Effect.Mode is "fixed" or "off" ? h.Effect with { Speed = 2, Reverse = false } : h.Effect }),
                Board = board.OrderBy(b => b.Identity).ThenBy(b => b.Zone).Select(b => b with { Effect = b.Effect.Mode is 0 or 1 ? b.Effect with { Speed = 224 } : b.Effect }),
                Sync = sync is null ? null : sync with { Channels = sync.Channels.Order().ToArray(), Zones = sync.Zones.Order().ToArray(), Memory=sync.Memory?.Order().ToArray() }
            }, Contract.Json);
        }
        return Canonical(left) == Canonical(right);
    }
}
