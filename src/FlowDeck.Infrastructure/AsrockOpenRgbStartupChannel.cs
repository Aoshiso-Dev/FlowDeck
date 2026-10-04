using System.Diagnostics;
using System.Text.Json;

namespace FlowDeck.Infrastructure;

internal interface IOpenRgbStartupChannel : IAsyncDisposable
{
    bool ResourcesClosed { get; }
    void Prepare(ProcessStartInfo start);
    void CheckStartInfo(ProcessStartInfo start);
    void Bind(OpenRgbOwnedProcess owner);
    void CheckReady();
}

internal interface IOpenRgbStartupAuthorizationScope
{
    void AttachPreparedHelper(OpenRgbOwnedProcess owner);
    void Authorize(AsrockAuthorizationRequest request, CancellationToken ct);
}

// Launch metadata only locates the channel. Authorization depends on both retained process
// identities and the parent's durable hardware policy, never on possession of this environment.
internal sealed class AsrockOpenRgbStartupChannel : IOpenRgbStartupChannel
{
    internal const string EnvironmentName = "FLOWDECK_ASROCK_AUTH_V1";
    private readonly object sync = new();
    private readonly AsrockOpenRgbAuthorizationPipe pipe;
    private readonly Action<OpenRgbOwnedProcess> attach;
    private readonly string metadata;
    private OpenRgbOwnedProcess? owner;
    private Task? running, closing;
    private bool prepared;
    public bool ResourcesClosed => pipe.ResourcesClosed;

    internal static IOpenRgbStartupChannel Create(OpenRgbStartupKind kind, IOpenRgbStartupAuthorizationScope? scope)
    {
        if (kind == OpenRgbStartupKind.NoDetection)
        {
            if (scope is not null) throw new IOException("検出なし起動へ機器の認可方針は渡せません");
            return new AsrockOpenRgbStartupChannel(owner => owner.CheckSuspended(),
                (_, _) => throw new IOException("検出なしのOpenRGBでは機器通信を許可しません"));
        }
        if (kind != OpenRgbStartupKind.AsrockPolychromeUsb || scope is null)
            throw new IOException("OpenRGBの起動に対応する認可方針がありません");
        return new AsrockOpenRgbStartupChannel(scope);
    }

    public AsrockOpenRgbStartupChannel(IOpenRgbStartupAuthorizationScope scope)
        : this(scope.AttachPreparedHelper, scope.Authorize) { }

    internal AsrockOpenRgbStartupChannel(Action<OpenRgbOwnedProcess> attach,
        Action<AsrockAuthorizationRequest, CancellationToken> authorize, int timeoutMilliseconds = 1000)
    {
        ArgumentNullException.ThrowIfNull(attach); ArgumentNullException.ThrowIfNull(authorize);
        this.attach = attach;
        pipe = new((request, ct) =>
        {
            var child = Volatile.Read(ref owner) ?? throw new IOException("OpenRGBの認可対象が未設定です");
            child.CheckRunning(); ct.ThrowIfCancellationRequested();
            authorize(request, ct);
            ct.ThrowIfCancellationRequested(); child.CheckRunning();
        }, TimeSpan.FromMilliseconds(timeoutMilliseconds));
        metadata = JsonSerializer.Serialize(new { version = 1, pipe = pipe.Name, parent = pipe.ParentId,
            created = pipe.ParentCreatedFileTime, timeoutMs = timeoutMilliseconds });
    }

    public void Prepare(ProcessStartInfo start)
    {
        lock (sync)
        {
            if (prepared || closing is not null || ResourcesClosed || start.Environment.ContainsKey(EnvironmentName))
                throw new IOException("OpenRGBの認可起動情報を再利用・上書きできません");
            start.Environment.Add(EnvironmentName, metadata); prepared = true;
        }
    }
    public void CheckStartInfo(ProcessStartInfo start)
    {
        lock (sync)
        {
            if (!prepared || closing is not null || ResourcesClosed ||
                !start.Environment.TryGetValue(EnvironmentName, out var value) || value != metadata)
                throw new IOException("OpenRGBの認可起動情報が変わりました");
        }
    }
    public void Bind(OpenRgbOwnedProcess child)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (sync)
        {
            if (!prepared || closing is not null || ResourcesClosed || owner is not null)
                throw new IOException("OpenRGBの認可通信を再関連付けできません");
            child.CheckSuspended();
            // Keep the exact owner on any later failure, so it must exit before native recovery.
            owner = child;
            using var identity = new AsrockAuthorizationProcess(child.ProcessId);
            pipe.Bind(identity.Id, identity.CreatedFileTime);
            attach(child);
            child.CheckSuspended();
            running = pipe.RunAsync();
            _ = running.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    public void CheckReady()
    {
        lock (sync)
        {
            if (closing is not null || ResourcesClosed || owner is null || running is null || running.IsCompleted || pipe.Failure is not null)
                throw new IOException("OpenRGBの認可通信が利用できません", pipe.Failure);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync) return new(closing ??= pipe.DisposeAsync().AsTask());
    }
}
