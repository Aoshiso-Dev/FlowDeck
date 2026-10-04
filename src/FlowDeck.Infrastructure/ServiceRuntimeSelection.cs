using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Stored only in the protected service data directory. An absent record preserves the legacy runtime.
public sealed record ServiceRuntimeSelection(int Version, string Runtime, string InstallationId)
{
    public const string FileName = "service-runtime.json";
    public static string ConfigurationDirectory(string directory) => Path.Combine(directory, "devices-v2", "configuration");
    public static string RecoveryDirectory(string directory) => Path.Combine(directory, "devices-v2", "recovery");
    public static ServiceRuntimeSelection? Read(string directory)
    {
        var path = Path.Combine(directory, FileName); ServiceEnvironment.NoLinks(path);
        if (!File.Exists(path)) return null;
        var value = ServiceEnvironment.Read<ServiceRuntimeSelection>(path);
        if (value is not { Version: 1, Runtime: "devices-v2" } || !DeviceServiceProtocol.Id(value.InstallationId))
            throw new IOException("サービス起動方式の設定が不正です。旧方式へ自動切替しません");
        return value;
    }
    public DeviceConfigurationPackage ReadConfiguration(string directory)
    {
        if (this != Read(directory)) throw new IOException("サービス起動方式が変更されています");
        var active = DeviceConfigurationCodec.Store(ConfigurationDirectory(directory)).ReadActive()
            ?? throw new IOException("汎用サービスの初期設定がありません");
        var config = DeviceConfigurationCodec.Read(active.Files);
        if (config.Bindings.InstallationId != InstallationId) throw new IOException("汎用設定とサービスのインストール識別が一致しません");
        return config;
    }
}
