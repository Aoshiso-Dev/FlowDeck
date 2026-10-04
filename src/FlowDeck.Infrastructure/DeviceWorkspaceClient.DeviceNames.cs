using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceNameEdit(string Epoch, string Session, string Generation, DeviceNameChange Change);

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsDeviceNames => client.Hello?.Features.Contains(DeviceNameChange.Feature) == true;
    public async Task<DeviceConfigurationReply> SaveDeviceNameAsync(DeviceNameEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (client.Hello?.Epoch != edit.Epoch || client.Hello.Session != edit.Session || configuration?.Generation != edit.Generation)
                throw new IOException("接続または設定が変更されました。現在の名前を読み込んでください");
            var result = await client.SaveDeviceNameAsync(edit.Generation, edit.Change, ct).ConfigureAwait(false);
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
            lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
