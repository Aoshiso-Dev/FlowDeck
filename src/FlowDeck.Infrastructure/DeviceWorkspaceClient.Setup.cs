using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsEndpointSetup => client.Hello?.Features.Contains("endpoint-setup-v2") == true;
    private void CheckSetupVersion(DeviceEndpointSetupEdit edit)
    {
        if (!SupportsEndpointSetup || client.Hello?.Epoch != edit.Epoch || !edit.Matches(Snapshot))
            throw new IOException("接続・機器・設定が変更されました。再取得してください");
    }
    public async Task<DeviceEndpointSetupPreview> PreviewSetupAsync(DeviceEndpointSetupEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckSetupVersion(edit);
            var cooling = edit.Configuration.Devices.Devices.Single(d => d.Id == edit.Target.DeviceId).Endpoints
                .Single(e => e.Id == edit.Target.EndpointId).Kind == EndpointKind.Cooling;
            var preview = await client.PreviewConfigurationAsync(edit.Generation, edit.Configuration, ct,
                coolingSetupTarget: cooling ? edit.Target : null, lightingSetupTarget: cooling ? null : edit.Target).ConfigureAwait(false);
            await ReadStateAsync(ct).ConfigureAwait(false); CheckSetupVersion(edit);
            return new(Contract.Copy(edit), preview);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceConfigurationReply> ApplySetupAsync(DeviceEndpointSetupPreview preview, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckSetupVersion(preview.Edit);
            if (preview.Transfer.Generation != preview.Edit.Generation) throw new IOException("差分確認の世代が一致しません");
            return await client.ApplyConfigurationAsync(preview.Transfer, preview.Edit.Topology, ct).ConfigureAwait(false);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally
        {
            // Even a rejected/unknown adoption can have released control. Always fetch the actual state.
            try { if (client.Connected) { var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock (gate) configuration = next; await ReadStateAsync(ct).ConfigureAwait(false); } }
            catch (Exception e) { RecordError(e); }
            operations.Release();
        }
    }
}
