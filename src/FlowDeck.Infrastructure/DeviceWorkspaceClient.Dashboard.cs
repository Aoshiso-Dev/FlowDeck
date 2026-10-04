using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsMonitoringRegistration => client.Hello?.Features.Contains("monitoring-registration-v2") == true;
    public async Task<DeviceConfigurationReply> RegisterMonitoringAsync(DeviceMonitoringEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (configuration?.Generation != edit.ConfigurationGeneration || client.Hello?.Epoch != edit.Epoch)
                throw new IOException("接続または設定が変更されました。再取得してください");
            var result = await client.RegisterMonitoringAsync(edit, ct).ConfigureAwait(false);
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false); return result;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    public async Task<DeviceConfigurationReply> SaveDashboardAsync(DeviceDashboardEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (configuration?.Generation != edit.Generation || client.Hello?.Epoch != edit.Epoch)
                throw new InvalidOperationException("接続または設定が変更されました。現在の設定を読み込んでください");
            var result = await client.SaveDashboardAsync(edit.Generation, edit.Dashboard, ct).ConfigureAwait(false);
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false);
            lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}
