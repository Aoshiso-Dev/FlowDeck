using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceDriver
{
    public string? ValidateLightingBatch(EndpointLightingProgram[] programs)
    {
        lock (gate)
        {
            if (!DeviceLightingBatch.Valid(programs)) return "OpenRGBの固定色一括設定が不正です";
            if (programs.Length > 1 && programs.Any(p => p.EndpointId == "device")) return "機器全体と個別ゾーンを同時には選択できません";
            foreach (var item in programs)
            {
                if (!ports.TryGetValue(item.EndpointId, out var port)) return "未検出のOpenRGB端子です";
                if (Ready(port) is { } error) return error;
                if (streams.ContainsKey(port.Endpoint)) return "同期を終了してからOpenRGBの固定色を設定してください";
            }
            if (programs.All(p => ports[p.EndpointId].ControllerDirect)) return ValidateShared(programs.Select(p => p.EndpointId).ToArray());
            foreach (var item in programs)
                if (Validate(new(item.EndpointId, DeviceOperation.SetLighting, Lighting: item.Program)) is { } error) return error;
            return null;
        }
    }

    public async Task<DeviceDriverReleaseResult[]> ApplyLightingBatchAsync(EndpointLightingProgram[] programs, CancellationToken ct)
    {
        programs = Contract.Copy(programs);
        await io.WaitAsync(ct).ConfigureAwait(false);
        DeviceDriverReleaseResult[] All(DeviceDriverResult result) => programs.Select(p => new DeviceDriverReleaseResult(p.EndpointId, result)).ToArray();
        try
        {
            if (ValidateLightingBatch(programs) is { } error) return All(new(Outcome.Rejected, error));
            var hardware = execution.Value;
            void Authorize() { hardware?.CheckAuthorization(); ct.ThrowIfCancellationRequested(); }
            bool controllerDirect; lock (gate) controllerDirect = programs.All(p => ports[p.EndpointId].ControllerDirect);
            if (controllerDirect)
            {
                var colors = programs.ToDictionary(p => p.EndpointId, p => DeviceLighting.StopColor(p.Program));
                var result = await WriteSharedAsync(colors, Authorize, ct).ConfigureAwait(false);
                return programs.Select(p => new DeviceDriverReleaseResult(p.EndpointId, result with
                    { ReportedLighting = Success(result) ? new("fixed", [colors[p.EndpointId]]) : null })).ToArray();
            }
            var results = new List<DeviceDriverReleaseResult>(); var stopped = false;
            foreach (var item in programs)
            {
                if (stopped) { results.Add(new(item.EndpointId, new(Outcome.Rejected, "同じ一括設定の送信を中止しました"))); continue; }
                OpenRgbLightingSurface port; lock (gate) port = ports[item.EndpointId];
                var result = await ApplyCoreAsync(port, item.Program, Authorize, false, ct).ConfigureAwait(false);
                results.Add(new(item.EndpointId, result)); stopped = !Success(result);
            }
            return results.ToArray();
        }
        catch (Exception e) { return All(new(Outcome.Unknown, OpenRgbLightingProfile.Label(e.Message, "OpenRGBの一括設定に失敗しました", 400))); }
        finally { io.Release(); }
    }
}
