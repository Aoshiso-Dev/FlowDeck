using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public async Task<DeviceSettingsArchive> ExportSettingsAsync(bool portable,UiSettings? appearance=null,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); var snapshot=await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
            lock(gate) configuration=snapshot;
            return DeviceSettingsArchives.Create(snapshot.Configuration,portable,appearance);
        }
        catch(Exception e) { if(!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    private void CheckImportVersion(DeviceSettingsImportEdit edit)
    {
        if(client.Hello?.Epoch!=edit.Epoch||configuration?.Generation!=edit.Generation||state?.Inventory.TopologyRevision!=edit.Topology)
            throw new IOException("接続・機器・設定が変更されました。復元内容を再取得してください");
    }
    public async Task<DeviceSettingsImportPreview> PreviewImportAsync(DeviceSettingsImportEdit edit,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckImportVersion(edit);
            var preview=await client.PreviewConfigurationAsync(edit.Generation,edit.Configuration,ct).ConfigureAwait(false);
            await ReadStateAsync(ct).ConfigureAwait(false); CheckImportVersion(edit); return new(Contract.Copy(edit),preview);
        }
        catch(Exception e) { if(!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceConfigurationReply> ApplyImportAsync(DeviceSettingsImportPreview preview,CancellationToken ct=default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); await ReadStateAsync(ct).ConfigureAwait(false); CheckImportVersion(preview.Edit);
            if(preview.Transfer.Generation!=preview.Edit.Generation) throw new IOException("確認した復元内容の世代が一致しません");
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
