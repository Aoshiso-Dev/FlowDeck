using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceNotificationEdit(string Epoch, string Session, string Generation, DeviceNotificationSettings Settings);

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsNotifications => client.Hello?.Features.Contains("notifications-v2") == true;
    private void EnsureNotifications()
    {
        EnsureOpen();
        if (!SupportsNotifications) throw new IOException("サービスの通知機能に対応する更新が必要です");
    }
    public async Task<DeviceNotificationFeed> ReadNotificationsAsync(DeviceNotificationQuery query, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotifications();
            var reply = await client.SendAsync(client.Request(DeviceOperation.ReadNotifications) with { Notifications = query }, ct).ConfigureAwait(false);
            if (!reply.Success || reply.Notifications is null) throw new IOException(reply.Error ?? "通知を取得できません");
            return reply.Notifications;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task TestNotificationAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotifications();
            var reply = await client.SendAsync(client.Request(DeviceOperation.TestNotification), ct).ConfigureAwait(false);
            if (!reply.Success) throw new IOException(reply.Error ?? "通知テストを記録できません");
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceConfigurationReply> SaveNotificationsAsync(DeviceNotificationEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotifications();
            if (client.Hello?.Epoch != edit.Epoch || client.Hello.Session != edit.Session || configuration?.Generation != edit.Generation)
                throw new IOException("接続または通知設定が変更されました。現在の設定を読み込んでください");
            var result = await client.SaveNotificationsAsync(edit.Generation, edit.Settings, ct).ConfigureAwait(false);
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false); return result;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
