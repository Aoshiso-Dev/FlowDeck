using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsOpenRgbRuntimeInspection => client.Hello?.Features.Contains(DeviceOpenRgbRuntimeInspection.Feature) == true;
    public async Task<DeviceOpenRgbRuntimeInspection> InspectOpenRgbRuntimeAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!SupportsOpenRgbRuntimeInspection) throw new IOException("OpenRGBの導入確認に対応するサービスの更新が必要です");
            var expected = configuration?.Generation ?? throw new IOException("設定を取得してください");
            ct.ThrowIfCancellationRequested();
            // Once dispatched, preserve the shared connection and unrelated control ownership.
            var reply = await client.SendAsync(client.Request(DeviceOperation.Configuration,configuration:
                new(DeviceConfigurationOperation.InspectOpenRgbRuntime,ExpectedGeneration:expected))).ConfigureAwait(false);
            if (!reply.Success || reply.Configuration is not { OpenRgbRuntime: { } result } body)
                throw new IOException(reply.Error ?? "OpenRGBの導入状態を取得できません");
            if (body.Generation != expected || !DeviceOpenRgbRuntimeInspection.Valid(result))
                throw new IOException("OpenRGBの導入確認対象または設定が一致しません");
            ct.ThrowIfCancellationRequested(); return result with { };
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
