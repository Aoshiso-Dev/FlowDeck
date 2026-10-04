using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    // Called under the configuration semaphore. Metadata changes never reconfigure a driver.
    private LightingLibraryReply SaveLightingPersonalization(LightingLibraryRequest request)
    {
        var old = configuration.Lighting;
        if (request.ExpectedRevision != old.Revision) throw new IOException("照明設定の改訂が変更されました");
        var next = configuration; string message;
        if (request.Operation == LightingLibraryOperation.SaveAlias)
        {
            var alias = request.Alias!;
            if (!configuration.Devices.Devices.Any(d => d.Id == alias.Target.DeviceId && d.Endpoints.Any(e => e.Id == alias.Target.EndpointId && e.Kind == EndpointKind.Lighting)))
                throw new IOException("登録済みの照明端子を選択してください");
            var names = old.Names.Where(n => n.Target != alias.Target).ToList();
            if (alias.Name.Length > 0) names.Add(Contract.Copy(alias));
            next = next with { Lighting = old with { Names = names.ToArray() } };
            message = alias.Name.Length == 0 ? "標準の名前に戻しました" : "照明の名前を保存しました";
        }
        else
        {
            var palette = configuration.Preferences.Palette;
            var colors = request.Colors!.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (request.Operation == LightingLibraryOperation.ToggleFavorite)
            {
                var favorites = palette.Favorites.Select(c => c.ToUpperInvariant()).Distinct().ToList(); var color = colors[0];
                if (!favorites.Remove(color))
                {
                    if (favorites.Count >= 32) throw new IOException("お気に入りは32色までです");
                    favorites.Add(color);
                }
                palette = palette with { Favorites = favorites.ToArray() }; message = "お気に入りを保存しました";
            }
            else
            {
                palette = palette with { Recent = colors.Concat(palette.Recent.Select(c => c.ToUpperInvariant())).Distinct().Take(8).ToArray() };
                message = "最近使った色を保存しました";
            }
            next = next with { Preferences = next.Preferences with { Palette = palette } };
        }
        if (old.Revision >= 1 && Same(configuration, next)) return new(Outcome.Confirmed, message, generation.Id, request.Id, old.Revision);
        next = next with { Lighting = next.Lighting with { Revision = checked(old.Revision + 1) } };
        PersistConfiguration(next, "照明の名前・色見本");
        return new(Outcome.Confirmed, message, generation.Id, request.Id, next.Lighting.Revision);
    }
}
