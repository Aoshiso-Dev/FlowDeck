using System.Net.Sockets;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Compiled service dependency. Neither its implementation nor a remote host is supplied by the client.
public interface IDeviceOpenRgbInspector
{
    Task<DeviceOpenRgbInspection> InspectAsync(int port, CancellationToken ct);
}

internal sealed class DeviceOpenRgbInspector : IDeviceOpenRgbInspector
{
    private readonly TimeSpan timeout;
    public DeviceOpenRgbInspector(TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(3);
        if (this.timeout < TimeSpan.FromMilliseconds(20) || this.timeout > TimeSpan.FromSeconds(3)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public async Task<DeviceOpenRgbInspection> InspectAsync(int port, CancellationToken ct)
    {
        if (!DeviceOpenRgbInspection.PortValid(port)) throw new ArgumentOutOfRangeException(nameof(port));
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        var connected = false;
        try
        {
            await using var connection = await OpenRgbSdkConnection.ConnectAsync(port, deadline.Token, timeout).ConfigureAwait(false);
            connected = true;
            var environment = await connection.ReadEnvironmentAsync(deadline.Token).ConfigureAwait(false);
            connection.CheckEnvironment(environment.Stamp); ct.ThrowIfCancellationRequested();
            return Describe(port, environment);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            // Remote JSON, paths, plugin names and arbitrary exception text never enter the GUI/IPC.
            return new(port, connected ? OpenRgbInspectionStatus.SettingsUnavailable : OpenRgbInspectionStatus.ConnectionFailed,
                connected ? "設定を取得できません。再確認してください" : "SDKへ接続できません", [Coexistence()]);
        }
    }
    private static OpenRgbInspectionItem Coexistence() => new("coexistence", "制御の併用条件", "未検証", OpenRgbInspectionCondition.Unverified);
    internal static DeviceOpenRgbInspection Describe(int port, OpenRgbSdkEnvironment environment)
    {
        static bool Property(JsonElement parent, string name, out JsonElement value)
        { value = default; return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value); }
        static OpenRgbInspectionItem Boolean(string key, string name, JsonElement parent, string property) =>
            Property(parent, property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(key, name, value.GetBoolean() ? "有効" : "無効", OpenRgbInspectionCondition.Observed)
                : new(key, name, "未取得", OpenRgbInspectionCondition.Unavailable);
        var detectors = Property(environment.Detectors, "detectors", out var list) && list.ValueKind == JsonValueKind.Object
            ? list.EnumerateObject().ToArray() : null;
        var knownDetectors = detectors is not null && detectors.All(p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        var sleep = Property(environment.Drivers, "smbus_sleep_mode", out var mode) && mode.ValueKind == JsonValueKind.Number && mode.TryGetInt32(out var number) && number is >= 0 and <= 2 ? number : (int?)null;
        return new(port, OpenRgbInspectionStatus.Observed, "SDK接続・設定取得済み", [
            new("detectors", "検出対象の設定", knownDetectors ? $"有効 {detectors!.Count(p => p.Value.GetBoolean())} / {detectors!.Length} 項目" : "未取得",
                knownDetectors ? OpenRgbInspectionCondition.Observed : OpenRgbInspectionCondition.Unavailable),
            Boolean("hid", "HIDセーフモード", environment.Detectors, "hid_safe_mode"),
            Boolean("shared-smbus", "共有SMBus設定", environment.Drivers, "shared_smbus_access"),
            new("smbus-wait", "SMBus待機方式", sleep switch { 0 => "ビジー待機", 1 => "短時間のみビジー待機", 2 => "スリープ待機", _ => "未取得" },
                sleep is not null ? OpenRgbInspectionCondition.Observed : OpenRgbInspectionCondition.Unavailable),
            Boolean("external-controllers", "外部コントローラーの公開", environment.Server, "all_controllers"),
            new("plugins", "プラグイン", environment.Plugins is { } plugins ? $"{plugins.Length} 件" : "未取得",
                environment.Plugins is not null ? OpenRgbInspectionCondition.Observed : OpenRgbInspectionCondition.Unavailable),
            Coexistence()
        ]);
    }
}
