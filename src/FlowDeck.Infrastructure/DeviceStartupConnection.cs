using System.Security;

namespace FlowDeck.Infrastructure;

public enum DeviceStartupState { Connected, NotInstalled, WaitingForService, AccessDenied, Unavailable, Incompatible }
public enum DeviceServiceRegistration { Missing, Waiting, Running }

/// <summary>A read-only connection attempt. Owns the authenticated session until the window takes it.</summary>
public sealed class DeviceStartupConnection : IAsyncDisposable
{
    private DeviceWorkspaceClient? client;
    public DeviceStartupState State { get; }
    public string Status => State switch
    {
        DeviceStartupState.Connected => "接続しました",
        DeviceStartupState.NotInstalled => "サービス未導入",
        DeviceStartupState.WaitingForService => "サービスの接続待ち",
        DeviceStartupState.AccessDenied => "接続を確認できません",
        DeviceStartupState.Incompatible => "サービスの更新が必要です",
        _ => "サービスに接続できません"
    };
    public string NextAction => State switch
    {
        DeviceStartupState.Connected => "",
        DeviceStartupState.NotInstalled => "汎用版サービスを導入してから再接続してください。",
        DeviceStartupState.WaitingForService => "サービスが起動したら再接続してください。",
        DeviceStartupState.AccessDenied => "サービスのアクセス権と実行元を確認してください。",
        DeviceStartupState.Incompatible => "このアプリに対応する汎用版サービスへ更新してください。",
        _ => "サービスの状態を確認して再接続してください。"
    };
    private DeviceStartupConnection(DeviceStartupState state, DeviceWorkspaceClient? client = null)
    { State = state; this.client = client; }
    public DeviceWorkspaceClient TakeClient() => Interlocked.Exchange(ref client, null) ?? throw new InvalidOperationException("利用できる接続がありません");
    public static Task<DeviceStartupConnection> ConnectAsync(CancellationToken ct = default) => ConnectAsync(
        () => !WindowsCoolingServiceIdentity.IsInstalled() ? DeviceServiceRegistration.Missing :
            WindowsCoolingServiceIdentity.ProcessId() > 0 ? DeviceServiceRegistration.Running : DeviceServiceRegistration.Waiting,
        () => new DeviceServicePipeClient(), ct);

    // Factories are injected only by tests. The production overload cannot connect to an arbitrary pipe or legacy host.
    public static async Task<DeviceStartupConnection> ConnectAsync(Func<DeviceServiceRegistration> registration,
        Func<DeviceServicePipeClient> factory, CancellationToken ct = default)
    {
        DeviceWorkspaceClient? pending = null; DeviceServicePipeClient? transport = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            ct.ThrowIfCancellationRequested();
            var state = await Task.Run(registration, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (state == DeviceServiceRegistration.Missing) return new(DeviceStartupState.NotInstalled);
            if (state == DeviceServiceRegistration.Waiting) return new(DeviceStartupState.WaitingForService);
            if (state != DeviceServiceRegistration.Running) throw new IOException("サービスの状態が不正です");
            transport = factory(); pending = new(transport);
            await pending.ConnectAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var result = new DeviceStartupConnection(DeviceStartupState.Connected, pending); pending = null;
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(DeviceStartupState.Unavailable); }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { return new(DeviceStartupState.AccessDenied); }
        catch (Exception e) when (e is IOException or TimeoutException or System.Text.Json.JsonException)
        {
            return new(transport?.ServerAuthenticated == true && !DeviceWorkspaceClient.SupportsWorkspace(transport.Hello)
                ? DeviceStartupState.Incompatible : DeviceStartupState.Unavailable);
        }
        finally { if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false); }
    }
    public async ValueTask DisposeAsync()
    {
        if (client is not null) { await client.DisposeAsync().ConfigureAwait(false); client = null; }
    }
}
