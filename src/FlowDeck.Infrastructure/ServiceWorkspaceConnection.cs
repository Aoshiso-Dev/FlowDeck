namespace FlowDeck.Infrastructure;

/// <summary>Selects a protocol only through an authenticated service connection; never opens hardware.</summary>
public sealed class ServiceWorkspaceConnection : IAsyncDisposable
{
    private DeviceWorkspaceClient? devices;
    private CoolingServicePipeClient? legacy;
    public bool UsesDevices => devices is not null;
    private ServiceWorkspaceConnection(DeviceWorkspaceClient? devices, CoolingServicePipeClient? legacy)
    { this.devices = devices; this.legacy = legacy; }
    public DeviceWorkspaceClient TakeDevices() => Interlocked.Exchange(ref devices, null) ?? throw new InvalidOperationException("汎用サービス接続がありません");
    public CoolingServicePipeClient TakeLegacy() => Interlocked.Exchange(ref legacy, null) ?? throw new InvalidOperationException("旧サービス接続がありません");
    public static Task<ServiceWorkspaceConnection> ConnectAsync(CancellationToken ct = default) => ConnectAsync(() => new(), () => new(), ct);
    internal static async Task<ServiceWorkspaceConnection> ConnectAsync(Func<DeviceServicePipeClient> deviceFactory,
        Func<CoolingServicePipeClient> legacyFactory, CancellationToken ct = default)
    {
        var transport = deviceFactory(); var workspace = new DeviceWorkspaceClient(transport);
        try { await workspace.ConnectAsync(ct).ConfigureAwait(false); return new(workspace, null); }
        catch (Exception e)
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            // An authenticated v2 process with a bad hello, unavailable configuration or a deployment
            // gate is still v2. It must never cause construction of the old GUI's local RGB stack.
            if (transport.ServerAuthenticated || e is not (IOException or TimeoutException)) throw;
        }
        ct.ThrowIfCancellationRequested();
        var old = legacyFactory();
        try { await old.ConnectAsync(ct).ConfigureAwait(false); return new(null, old); }
        catch { await old.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref devices, null) is { } current) await current.DisposeAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref legacy, null) is { } old) await old.DisposeAsync().ConfigureAwait(false);
    }
}
