using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    private bool connectionLightingPreparationEnabled;
    private string? connectionLightingDeferred;
    private async Task<int> PrepareConnectionLightingCoreAsync(CancellationToken ct)
    {
        if (!connectionLightingPreparationEnabled || client.Hello?.Features.Contains(DeviceConnectionLighting.Feature) != true) return 0;
        try
        {
            var reply = await client.SendAsync(client.Request(DeviceOperation.Configuration,
                configuration: new(DeviceConfigurationOperation.PrepareConnectionLighting)), ct).ConfigureAwait(false);
            if (!reply.Success || reply.Configuration is not { Outcome: Outcome.Confirmed, PreparedLighting: { } count })
                throw new IOException(reply.Error ?? reply.Configuration?.Message ?? "接続限定の照明準備を保留しました");
            connectionLightingDeferred = null; return count;
        }
        catch (IOException e) when (client.Connected) { connectionLightingDeferred = e.Message; return 0; }
    }
    private async Task<int> EnableConnectionLightingAsync(CancellationToken ct)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); connectionLightingPreparationEnabled = true;
            var count = await PrepareConnectionLightingCoreAsync(ct).ConfigureAwait(false);
            await ReadStateAsync(ct).ConfigureAwait(false); return count;
        }
        finally { operations.Release(); }
    }
}
