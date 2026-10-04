using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Explicit memory-only UI fixture. No SDK socket or physical provider is constructed.
public sealed class DemoOpenRgbInspector : IDeviceOpenRgbInspector
{
    public int Calls { get; private set; }
    public OpenRgbInspectionStatus Status { get; set; } = OpenRgbInspectionStatus.Observed;
    public Func<CancellationToken, Task>? Before { get; set; }
    public async Task<DeviceOpenRgbInspection> InspectAsync(int port, CancellationToken ct)
    {
        Calls++; if (Before is { } before) await before(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
        var environment = new OpenRgbSdkEnvironment(new(Guid.NewGuid(), Guid.NewGuid(), 0, new('a', 64)), 25,
            Json("{\"hid_safe_mode\":true,\"detectors\":{\"demo\":true}}"), Json("{}"),
            Json("{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2}"), Json("{\"all_controllers\":false}"), []);
        var result = DeviceOpenRgbInspector.Describe(port, environment);
        return Status == OpenRgbInspectionStatus.Observed ? result : result with { Status = Status,
            Message = Status == OpenRgbInspectionStatus.ConnectionFailed ? "SDKへ接続できません" : "設定を取得できません。再確認してください",
            Items = result.Items.Where(i => i.Key == "coexistence").ToArray() };
    }
}
