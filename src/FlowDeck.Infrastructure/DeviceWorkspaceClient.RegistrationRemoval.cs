using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsRegistrationRemoval => client.Hello?.Features.Contains("configuration-transfer-v2")==true;
    private void CheckRegistrationRemoval(DeviceRegistrationRemovalEdit edit)
    {
        if(!SupportsRegistrationRemoval||client.Hello?.Epoch!=edit.Epoch||!edit.Matches(Snapshot))
            throw new IOException("接続・機器・設定が変更されました。解除内容を確認し直してください");
    }
    public async Task<DeviceRegistrationRemovalPreview> PreviewRegistrationRemovalAsync(DeviceRegistrationRemovalEdit edit,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckRegistrationRemoval(edit);
            var preview=await client.PreviewConfigurationAsync(edit.Generation,edit.Configuration,ct).ConfigureAwait(false);
            await ReadStateAsync(ct).ConfigureAwait(false); CheckRegistrationRemoval(edit);
            return new(Contract.Copy(edit),preview);
        }
        catch(Exception e) { if(!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceConfigurationReply> ApplyRegistrationRemovalAsync(DeviceRegistrationRemovalPreview preview,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckRegistrationRemoval(preview.Edit);
            if(preview.Transfer.Generation!=preview.Edit.Generation) throw new IOException("解除内容の設定世代が一致しません");
            return await client.ApplyConfigurationAsync(preview.Transfer,preview.Edit.Topology,ct).ConfigureAwait(false);
        }
        catch(Exception e) { if(!client.Connected) RecordError(e); throw; }
        finally
        {
            try { if(client.Connected) { var next=await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock(gate) configuration=next; await ReadStateAsync(ct).ConfigureAwait(false); } }
            catch(Exception e) { RecordError(e); }
            operations.Release();
        }
    }
}