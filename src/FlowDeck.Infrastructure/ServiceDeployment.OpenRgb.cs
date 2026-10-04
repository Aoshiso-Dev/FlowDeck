namespace FlowDeck.Infrastructure;

public sealed partial class ServiceDeployment
{
    internal static string? OpenRgbRequirementError(string action, string? runtime, ServicePackage? package, bool enabled) =>
        action != "remove" && runtime != "legacy-v1" && enabled && package?.Manifest.OpenRgb is null
            ? "保存された設定がOpenRGBを使用しています。専用OpenRGBを含む配布物を選ぶか、先にサービスで直接経路へ切り替えてください"
            : null;

    private void ConfirmOptionalRuntime(string action, string? runtime, ServicePackage? package)
    {
        if (action == "remove" || runtime == "legacy-v1") return;
        var enabled = ServiceRuntimeSelection.Read(data)?.ReadConfiguration(data).Preferences.Providers?.OpenRgbEnabled == true;
        if (OpenRgbRequirementError(action, runtime, package, enabled) is { } error) throw new IOException(error);
    }
}
