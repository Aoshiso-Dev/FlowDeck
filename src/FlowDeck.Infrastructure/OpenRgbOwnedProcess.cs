using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;

namespace FlowDeck.Infrastructure;

internal interface IOpenRgbSuspendedProcess : IWorkerProcessLifetime
{
    uint ProcessId { get; }
    bool HasExited { get; }
    int? ExitCode => null;
    IDisposable AttachJob();
    void Resume();
}

// Owns only the child it created. Existing OpenRGB processes are never adopted or terminated.
// Production routing still has to journal detection-time writes and supply the release callback.
internal sealed class OpenRgbOwnedProcess : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IDisposable imageLease;
    private readonly Func<Task> releaseHardware;
    private IOpenRgbSuspendedProcess? child;
    private WorkerProcessCleanup? cleanup;
    private IOpenRgbStartupChannel? startupChannel;
    private IOpenRgbShutdownSignal? shutdownSignal;
    private bool shutdownClosed, shutdownAttempted;
    internal bool GracefulExitConfirmed { get; private set; }
    internal bool ForcedTerminationRequired { get; private set; }
    internal Exception? GracefulShutdownFailure { get; private set; }
    private volatile bool jobAttached, resumeAttempted, hardwareReleased, imageClosed, closing, restoring;
    public uint ProcessId => child?.ProcessId ?? throw new IOException("OpenRGBは起動していません");
    public bool ResourcesClosed => imageClosed && (cleanup?.ResourcesClosed ?? true) && (startupChannel?.ResourcesClosed ?? true)
        && (shutdownSignal is null || shutdownClosed);
    private OpenRgbOwnedProcess(IDisposable imageLease, Func<Task> releaseHardware)
    { this.imageLease = imageLease; this.releaseHardware = releaseHardware; }

    internal static void RequireAcceptedDetection(OpenRgbStartupKind kind)
    {
        if (kind != OpenRgbStartupKind.NoDetection)
            throw new IOException("OpenRGB専用版の実SDK・検出統合の検証が未完了です");
    }

    internal static async Task<OpenRgbOwnedProcess> StartAsync(string runtimeDirectory, string configurationDirectory,
        OpenRgbStartupProfile profile, Func<Task> releaseHardware, IOpenRgbStartupAuthorizationScope? authorization = null)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(releaseHardware);
        // Byte pinning and bootstrap are ready; enabling hardware detection still requires
        // end-to-end SDK/detector acceptance. Product registration also remains closed.
        RequireAcceptedDetection(profile.Kind);
        if (profile.Kind == OpenRgbStartupKind.AsrockPolychromeUsb && authorization is null)
            throw new IOException("ASRockの検出を認可する所有者がありません");
        var start = profile.CreateStartInfo(runtimeDirectory, configurationDirectory);
        var runtime = OpenRgbRuntimePackage.FlowDeckPinned;
        if (runtime.AuthorizationVersion != 1) throw new IOException("OpenRGB専用版の認可通信が一致しません");
        void Validate()
        {
            _ = runtime.Inspect(runtimeDirectory);
            ServiceEnvironment.ValidateTree(configurationDirectory);
            var parent = new DirectoryInfo(configurationDirectory).Parent ?? throw new IOException("OpenRGB設定の親がありません");
            ServiceEnvironment.ValidateSecurity(parent.GetAccessControl());
            var entries = new DirectoryInfo(configurationDirectory).EnumerateFileSystemInfos().ToArray();
            if (entries.Length != 1 || entries[0] is not FileInfo file || file.Name != "OpenRGB.json" || file.Length > 512 * 1024 ||
                !File.ReadAllBytes(file.FullName).AsSpan().SequenceEqual(profile.Settings))
                throw new IOException("OpenRGBの起動用設定が固定条件と一致しません");
        }
        return await StartAsync(start, runtime.AcquireProtected(runtimeDirectory), Validate,
            WindowsSuspendedProcess.Create, releaseHardware, () => AsrockOpenRgbStartupChannel.Create(profile.Kind, authorization),
            () => new OpenRgbShutdownSignal()).ConfigureAwait(false);
    }
    internal static async Task<OpenRgbOwnedProcess> StartAsync(ProcessStartInfo start, IDisposable imageLease,
        Action validateBeforeResume, Func<ProcessStartInfo, IOpenRgbSuspendedProcess> create, Func<Task> releaseHardware,
        Func<IOpenRgbStartupChannel>? createStartupChannel = null, Func<IOpenRgbShutdownSignal>? createShutdownSignal = null)
    {
        var owner = new OpenRgbOwnedProcess(imageLease, releaseHardware);
        try
        {
            owner.startupChannel = createStartupChannel?.Invoke();
            owner.startupChannel?.Prepare(start);
            owner.shutdownSignal = createShutdownSignal?.Invoke();
            owner.shutdownSignal?.Prepare(start);
            validateBeforeResume();
            owner.startupChannel?.CheckStartInfo(start);
            owner.shutdownSignal?.CheckStartInfo(start);
            owner.child = create(start);
            owner.cleanup = new(owner.child);
            owner.cleanup.AttachJob(owner.child.AttachJob());
            owner.jobAttached = true;
            owner.startupChannel?.Bind(owner);
            // No child instruction has run yet. Recheck the fixed configuration after job ownership.
            validateBeforeResume();
            owner.startupChannel?.CheckStartInfo(start);
            owner.shutdownSignal?.CheckStartInfo(start);
            owner.startupChannel?.CheckReady();
            owner.resumeAttempted = true;
            owner.child.Resume();
            owner.CheckRunning(); return owner;
        }
        catch (Exception e)
        {
            await DeviceStartupException.RethrowAfterCleanupAsync(e, owner, () => owner.ResourcesClosed).ConfigureAwait(false);
            throw;
        }
    }
    private void CheckCreated()
    {
        if ((closing && !restoring) || imageClosed || !jobAttached || child is null || child.HasExited)
            throw new IOException("所有しているOpenRGBプロセスが稼働していません");
    }
    public void CheckSuspended()
    {
        CheckCreated();
        if (resumeAttempted) throw new IOException("OpenRGBの実行開始後には事前関連付けできません");
    }
    public void CheckOwned()
    {
        CheckCreated();
        if (resumeAttempted) startupChannel?.CheckReady();
    }
    public void CheckRunning()
    {
        CheckOwned();
        if (!resumeAttempted) throw new IOException("OpenRGBの実行をまだ開始していません");
    }
    public void CheckPeer(IPEndPoint client, IPEndPoint server)
    {
        CheckRunning(); WindowsTcpPeer.Check(ProcessId, client, server); CheckRunning();
    }
    public Task<OpenRgbSdkConnection> ConnectAsync(int port, CancellationToken ct = default, Action? authorizeWrite = null) =>
        OpenRgbSdkConnection.ConnectAsync(port, ct, verifyPeer: CheckPeer, ownedHeadless: true, authorizeWrite: authorizeWrite);
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ResourcesClosed) return;
            closing = true;
            // A failed restore retains process ownership and executable locks for a later retry.
            // It must not hand the same USB device to another provider after an unconfirmed exit.
            if (resumeAttempted && !hardwareReleased)
            {
                restoring = true;
                try { await releaseHardware().ConfigureAwait(false); hardwareReleased = true; }
                finally { restoring = false; }
            }
            if (resumeAttempted && shutdownSignal is not null && !shutdownAttempted)
            {
                // The SDK release callback has finished. Until this point the helper must remain
                // alive to complete restoration. An unresponsive helper still has the owned Job fallback.
                shutdownAttempted = true;
                try
                {
                    if (child is { HasExited: false })
                    {
                        shutdownSignal.Request();
                        var exited = await child.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        ForcedTerminationRequired = !exited;
                        GracefulExitConfirmed = exited && child.ExitCode == 0;
                        if (exited && !GracefulExitConfirmed)
                            GracefulShutdownFailure = new IOException("OpenRGBの正常終了コードを確認できません");
                    }
                }
                catch (Exception error) { GracefulShutdownFailure = error; ForcedTerminationRequired = true; }
            }
            if (cleanup is not null) await cleanup.CloseAsync([]).ConfigureAwait(false);
            // Keep authorization alive through SDK release and confirmed child exit. Closing it
            // earlier would revoke a restoration request that is already in flight.
            if (startupChannel is not null) await startupChannel.DisposeAsync().ConfigureAwait(false);
            if (startupChannel is { ResourcesClosed: false }) throw new IOException("OpenRGBの認可通信の終了が未確認です");
            if (shutdownSignal is not null && !shutdownClosed) { shutdownSignal.Dispose(); shutdownClosed = true; }
            if (!imageClosed) { imageLease.Dispose(); imageClosed = true; }
        }
        finally { gate.Release(); }
    }
}
