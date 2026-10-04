using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceProviderRoutePlan(string[] Remove, string[] Add, string[]? IsolatedResourceDomains = null);

// Identity of the compiled resource/exclusion policy, including its dependency and definition pins.
// It is not supplied through IPC, and equality is not evidence of physical coexistence by itself.
public sealed record DeviceProviderRoutingDefinition(string Id, int Revision, string Digest)
{
    internal static bool Valid(DeviceProviderRoutingDefinition? value) => value is { Revision: > 0 } &&
        DeviceConfigurationRules.Key(value.Id) && DeviceConfigurationProtocol.Digest(value.Digest);
    internal static DeviceProviderRoutingDefinition? Capture(IDeviceProviderRouting? routing)
    {
        if (routing is null) return null;
        var value = routing.Definition;
        if (!Valid(value)) throw new IOException("取得経路の判定方針の定義が不正です");
        return value with { };
    }
    internal static void Check(IDeviceProviderRouting? routing, DeviceProviderRoutingDefinition? expected)
    {
        if (Capture(routing) != expected) throw new IOException("取得経路の判定方針が変更されました。サービスを再起動して確認してください");
    }
}

/// <summary>
/// Compiled service policy, never supplied through IPC or configuration files. Plan validates
/// resource exclusions without opening hardware. Create constructs only the requested providers;
/// constructors must be inert and clean up a partially constructed result on failure. Hardware is
/// opened by polling, after the runtime has verified closure of the previous route.
/// </summary>
public interface IDeviceProviderRouting
{
    DeviceProviderRoutingDefinition Definition { get; }
    bool SupportsDeviceSelection => false;
    DeviceProviderRoutePlan Plan(DeviceConfigurationPackage before, DeviceConfigurationPackage after);
    IDeviceDriverProvider[] Create(DeviceConfigurationPackage configuration, string[] providerIds);
}
