using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Compiled hardware policy, never supplied by a client. Prepare must durably capture detection-time
// changes and close its native handles before returning. Dispose restores that capture only AFTER
// the owned helper has exited. It must retain unsuccessful recovery and permit a later retry.
internal interface IOpenRgbDetectionScope : IAsyncDisposable
{
    string[] ResourceDomains { get; }
    bool ResourcesClosed { get; }
    void CheckRoute();
    Task PrepareAsync(CancellationToken ct);
    OpenRgbStartupProfile BindStartup(OpenRgbStartupProfile template);
    void AttachHelper(OpenRgbOwnedProcess helper);
    void CheckInventory(OpenRgbSdkController[] controllers);
    void CheckControl();
    IOpenRgbLightingTransport? CreateLightingTransport(OpenRgbBoundDevice device) => null;
}

// Only a compiled scope with a durable, independently verifiable native recovery route may
// authorize closing an SDK connection whose lighting restoration did not complete.
internal interface IOpenRgbNativeShutdownScope
{
    void CheckNativeShutdown(OpenRgbRecoveryRecord[] records);
    DeviceShutdownRecovery[] ReadNativeShutdownRecoveries();
}

// Composes SDK restoration with process and pre-detection ownership. Packaged routing supplies
// the concrete detection scope; actual helper activation still requires product acceptance.
internal sealed partial class OpenRgbManagedDeviceProvider : IDeviceProviderShutdownCoordinator, IDeviceCacheRetirement, IOpenRgbRoutePolicy
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly OpenRgbDeviceProvider sdk;
    private OpenRgbStartupProfile profile;
    private readonly IOpenRgbDetectionScope detection;
    private readonly Func<OpenRgbStartupProfile, Func<Task>, Task<OpenRgbOwnedProcess>> start;
    private readonly string[] domains;
    private OpenRgbOwnedProcess? owner;
    private DeviceStartupException? pendingStart;
    private DeviceShutdownRecovery[] shutdownRecoveries = [];
    private volatile bool closing, closed, startFailed, connectionIssued;

    public string ProviderId => OpenRgbDeviceProvider.Provider;
    public string[] ResourceDomains => domains.ToArray();
    public IDeviceControlDriver[] CachedDrivers => sdk.CachedDrivers;
    internal bool ResourcesClosed => closed;
    public bool CanCompleteDriverShutdown(IDeviceControlDriver[] pending) => detection is IOpenRgbNativeShutdownScope &&
        pending.Length > 0 && pending.All(driver => CachedDrivers.Any(owned => ReferenceEquals(owned, driver)));
    public DeviceShutdownRecovery[] ReadShutdownRecoveries() => closed ? Contract.Copy(shutdownRecoveries) : [];

    internal OpenRgbManagedDeviceProvider(string installation, string recoveryDirectory, string boot,
        OpenRgbStartupProfile profile, IOpenRgbDetectionScope detection, IEnumerable<string> selectedDeviceIds,
        string runtimeDirectory, string configurationRoot)
        : this(installation, recoveryDirectory, boot, profile, detection, selectedDeviceIds,
            ProtectedLauncher(profile, runtimeDirectory, configurationRoot, detection)) { }

    internal OpenRgbManagedDeviceProvider(string installation, string recoveryDirectory, string boot,
        OpenRgbStartupProfile profile, IOpenRgbDetectionScope detection, IEnumerable<string> selectedDeviceIds,
        Func<OpenRgbStartupProfile, Func<Task>, Task<OpenRgbOwnedProcess>> start)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(detection); ArgumentNullException.ThrowIfNull(start);
        this.profile = profile; this.detection = detection; this.start = start;
        var resources = detection.ResourceDomains?.ToArray();
        if (resources is not { Length: > 0 and <= 64 } || resources.Any(s => !DeviceConfigurationRules.Text(s, 256)) ||
            resources.Distinct(StringComparer.Ordinal).Count() != resources.Length)
            throw new ArgumentException("OpenRGBの検出資源が不正です", nameof(detection));
        domains = OpenRgbDeviceProvider.Domains.Union(resources, StringComparer.Ordinal).ToArray();
        sdk = new(installation, recoveryDirectory, boot, ConnectAsync, this, selectedDeviceIds, detection.CreateLightingTransport);
    }

    private static Func<OpenRgbStartupProfile, Func<Task>, Task<OpenRgbOwnedProcess>> ProtectedLauncher(OpenRgbStartupProfile profile,
        string runtimeDirectory, string configurationRoot, IOpenRgbDetectionScope detection)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = profile.CreateStartInfo(runtimeDirectory, configurationRoot);
        var runtime = Path.GetFullPath(runtimeDirectory); var root = Path.GetFullPath(configurationRoot);
        return async (boundProfile, release) =>
        {
            // A new protected directory prevents previous profiles, plugins and auto-connections
            // from entering the helper. Never clean up arbitrary files created by another process.
            ServiceEnvironment.ValidateTree(root);
            var directory = Path.Combine(root, "openrgb-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("OpenRGBの起動領域が既に存在します");
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(Path.Combine(directory, "OpenRGB.json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(boundProfile.Settings); file.Flush(true); }
            ServiceEnvironment.ValidateTree(directory);
            return await OpenRgbOwnedProcess.StartAsync(runtime, directory, boundProfile, release,
                detection as IOpenRgbStartupAuthorizationScope).ConfigureAwait(false);
        };
    }

    private async Task<OpenRgbSdkConnection> ConnectAsync(CancellationToken ct)
    {
        if (startFailed) throw new IOException("OpenRGBの起動失敗後の復旧が必要です");
        detection.CheckRoute();
        if (owner is null)
        {
            try
            {
                // Set no ready flag until both durable preparation and suspended startup succeed.
                await detection.PrepareAsync(ct).ConfigureAwait(false);
                profile = detection.BindStartup(profile);
                detection.CheckRoute(); ct.ThrowIfCancellationRequested();
                owner = await start(profile, () =>
                {
                    // Failed startup can call this while SDK Poll holds its own lock. Never reenter
                    // SDK disposal here; normal shutdown closes it before asking the owner to exit.
                    if (connectionIssued && !sdk.ResourcesClosed && !sdk.NativeConnectionsClosed)
                        throw new IOException("OpenRGBの照明復旧と接続終了が未完了です");
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                detection.AttachHelper(owner);
            }
            catch (Exception e)
            {
                startFailed = true;
                if (e is DeviceStartupException pending) pendingStart = pending;
                throw;
            }
        }
        owner.CheckRunning(); detection.CheckRoute(); ct.ThrowIfCancellationRequested();
        // Connection refusal while the same helper is still starting may be retried. Never launch
        // a second helper to hide a dead process or a failed handshake.
        var connection = await owner.ConnectAsync(profile.Port, ct, detection.CheckControl).ConfigureAwait(false);
        connectionIssued = true;
        return connection;
    }

    void IOpenRgbRoutePolicy.CheckLocalRoute()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (startFailed) throw new IOException("OpenRGBの起動失敗後の復旧が必要です");
        detection.CheckRoute(); owner?.CheckRunning();
    }
    void IOpenRgbRoutePolicy.CheckEnvironment(OpenRgbSdkEnvironment environment) => profile.CheckLiveEnvironment(environment);
    void IOpenRgbRoutePolicy.CheckInventory(OpenRgbSdkController[] controllers) => detection.CheckInventory(controllers);

    public async Task PollAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closing || closed, this);
            await sdk.PollAsync(ct).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            // Do not advance to the next owner on partial success. Each completed step is retained
            // by its owner so retry does not repeat successful restoration or create new devices.
            if (sdk.NativeShutdownStarted)
                await sdk.QuiesceNativeAsync(((IOpenRgbNativeShutdownScope)detection).CheckNativeShutdown).ConfigureAwait(false);
            else
            {
                try { await sdk.DisposeAsync().ConfigureAwait(false); }
                catch (Exception sdkError) when (detection is IOpenRgbNativeShutdownScope)
                {
                    try { await sdk.QuiesceNativeAsync(((IOpenRgbNativeShutdownScope)detection).CheckNativeShutdown).ConfigureAwait(false); }
                    catch (Exception nativeError) { throw new AggregateException("OpenRGBのSDK終了とNative引き渡しが未完了です", sdkError, nativeError); }
                }
            }
            if (!sdk.ResourcesClosed && !sdk.NativeConnectionsClosed) throw new IOException("OpenRGBのSDK接続の終了が未確認です");
            if (pendingStart is not null)
            {
                await pendingStart.DisposeAsync().ConfigureAwait(false);
                if (!pendingStart.ResourcesClosed) throw new IOException("OpenRGBの起動途中の接続が残っています");
            }
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
                if (!owner.ResourcesClosed) throw new IOException("OpenRGBのプロセス終了が未確認です");
            }
            await detection.DisposeAsync().ConfigureAwait(false);
            if (!detection.ResourcesClosed) throw new IOException("OpenRGBの検出前の状態への復旧が未完了です");
            if (sdk.NativeShutdownStarted)
                await sdk.CompleteNativeShutdownAsync(() =>
                {
                    if (!detection.ResourcesClosed || owner is { ResourcesClosed: false })
                        throw new IOException("OpenRGBのNative復旧と所有プロセス終了が未確認です");
                }).ConfigureAwait(false);
            if (!sdk.ResourcesClosed) throw new IOException("OpenRGBの復旧記録とSDK終了が未完了です");
            if (sdk.NativeShutdownStarted)
                shutdownRecoveries = ((IOpenRgbNativeShutdownScope)detection).ReadNativeShutdownRecoveries();
            closed = true;
        }
        finally { lifecycle.Release(); }
    }
}
