using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Describes the optional bytes in the reviewed service manifest. It neither selects a route nor
// grants hardware access. Each installed service version still pins its own executable at launch.
public sealed record ServiceOptionalOpenRgb(string DefinitionId, string DefinitionDigest);

public sealed partial class ServicePackage
{
    public const string OpenRgbDirectory = "optional/OpenRGB";
    internal static bool IsOpenRgbFile(string path) => path.StartsWith(OpenRgbDirectory + "/", StringComparison.Ordinal);
    private void ValidateOptionalRuntime()
    {
        var optional = Manifest.Files.Keys.Where(p => p.StartsWith("optional/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (Manifest.OpenRgb is null)
        {
            if (optional.Length != 0) throw new IOException("任意ランタイムのファイルに対応する配布定義がありません");
            return;
        }
        if (!SupportsOptionalOpenRgb || !DeviceConfigurationRules.Key(Manifest.OpenRgb.DefinitionId) ||
            !Manifest.OpenRgb.DefinitionId.StartsWith("flowdeck-openrgb-", StringComparison.Ordinal) ||
            Manifest.OpenRgb.DefinitionDigest is not { } digest || !Hash(digest) || optional.Length is < 1 or > 128 ||
            optional.Any(p => !IsOpenRgbFile(p)) || !Manifest.Files.ContainsKey(OpenRgbDirectory + "/OpenRGB.exe"))
            throw new IOException("OpenRGBの任意配布定義・配置が不正です");
    }
}
