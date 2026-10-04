using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsOpenRgbInspection => client.Hello?.Features.Contains(DeviceOpenRgbInspection.Feature) == true;
    public async Task<DeviceOpenRgbInspection> InspectOpenRgbAsync(int port, CancellationToken ct = default)
    {
        if (!DeviceOpenRgbInspection.PortValid(port)) throw new ArgumentOutOfRangeException(nameof(port));
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!SupportsOpenRgbInspection) throw new IOException("OpenRGBの接続確認に対応するサービスの更新が必要です");
            var expected = configuration?.Generation ?? throw new IOException("設定を取得してください");
            ct.ThrowIfCancellationRequested();
            // Once sent, let the bounded service operation finish. Cancelling the shared pipe would
            // release unrelated cooling/lighting ownership just to cancel a read-only inspection.
            var reply = await client.SendAsync(client.Request(DeviceOperation.Configuration, configuration:
                new(DeviceConfigurationOperation.InspectOpenRgb, ExpectedGeneration: expected, OpenRgbPort: port))).ConfigureAwait(false);
            if (!reply.Success || reply.Configuration is not { OpenRgbInspection: { } result } body)
                throw new IOException(reply.Error ?? "OpenRGBの確認結果を取得できません");
            if (body.Generation != expected || result.Port != port || !DeviceOpenRgbInspection.Valid(result))
                throw new IOException("OpenRGBの確認対象または設定が一致しません");
            ct.ThrowIfCancellationRequested();
            return Contract.Copy(result);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
