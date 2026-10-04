using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Not enrolled in Packaged until direct-provider discovery/polling exclusion and route selection are connected.
// Construction is inert. The compiled route guard must also cover reads, not only lighting writes.
internal sealed partial class OpenRgbDeviceProvider : IDeviceCacheRetirement
{
    public const string Definition = "openrgb-sdk6-v1";
    internal const string Provider = "openrgb-sdk";
    internal static string[] Domains => ["sdk:openrgb-loopback"];
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly string installation, directory, boot;
    private readonly Func<CancellationToken, Task<OpenRgbSdkConnection>> connect;
    private readonly IOpenRgbRoutePolicy policy;
    private readonly Func<OpenRgbBoundDevice, IOpenRgbLightingTransport?>? createTransport;
    private readonly HashSet<string> selectedDevices;
    private sealed record Permit(OpenRgbSdkConnection Connection, OpenRgbEnvironmentStamp Stamp);
    private Permit? permit;
    private readonly Dictionary<string, OpenRgbDeviceDriver> drivers = [];
    private readonly Dictionary<OpenRgbSdkTarget, string> unresolved = [];
    private OpenRgbSdkConnection? connection;
    private bool closing, disposed;
    internal bool ResourcesClosed { get { lock (gate) return disposed; } }
    public string ProviderId => Provider;
    public string[] ResourceDomains => Domains;
    public IDeviceControlDriver[] CachedDrivers { get { lock (gate) return drivers.Values.Cast<IDeviceControlDriver>().ToArray(); } }
    public OpenRgbDeviceProvider(string installation, string directory, string boot, int port, IOpenRgbRoutePolicy policy,
        IEnumerable<string> selectedDeviceIds)
        : this(installation, directory, boot, ct => OpenRgbSdkConnection.ConnectAsync(port, ct), policy, selectedDeviceIds)
    { if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port)); }
    internal OpenRgbDeviceProvider(string installation, string directory, string boot,
        Func<CancellationToken, Task<OpenRgbSdkConnection>> connect, IOpenRgbRoutePolicy policy, IEnumerable<string> selectedDeviceIds,
        Func<OpenRgbBoundDevice, IOpenRgbLightingTransport?>? createTransport = null)
    {
        if (!DeviceServiceProtocol.Id(installation) || !DeviceConfigurationRules.Text(boot, 128) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("OpenRGBのインストール・起動・保存先が不正です");
        ArgumentNullException.ThrowIfNull(connect); ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(selectedDeviceIds);
        var selected = selectedDeviceIds.Take(DeviceRegistry.MaximumDevices + 1).ToArray();
        if (!DeviceProviderPreferences.ValidSelection(selected))
            throw new ArgumentException("OpenRGBの選択機器が不正です", nameof(selectedDeviceIds));
        // Selection is immutable for this provider lifetime. Changing it requires the normal
        // release/close/replacement flow; a mutable UI collection cannot widen an active route.
        selectedDevices = new(selected, StringComparer.Ordinal);
        this.installation = installation; this.directory = directory; this.boot = boot; this.connect = connect; this.policy = policy;
        this.createTransport = createTransport;
    }
    private void CheckPermit()
    {
        // Do not take the provider's observation lock here: the SDK calls this immediately before
        // sending, while holding its connection lock. Refresh/disposal revoke the permit atomically.
        var captured = Volatile.Read(ref permit) ?? throw new IOException("OpenRGBの接続条件を確認してください");
        policy.CheckLocalRoute(); captured.Connection.CheckEnvironment(captured.Stamp);
        if (!ReferenceEquals(captured, Volatile.Read(ref permit))) throw new IOException("OpenRGBの接続条件が更新されています");
    }
    private async Task RefreshRecoveryPermitAsync(CancellationToken ct)
    {
        var captured = Volatile.Read(ref permit) ?? throw new IOException("OpenRGBの復旧用接続条件を確認できません");
        policy.CheckLocalRoute();
        var environment = await captured.Connection.ReadEnvironmentAsync(ct, policy.CheckLocalRoute).ConfigureAwait(false);
        policy.CheckEnvironment(environment); policy.CheckLocalRoute();
        // Refresh only the age of a previously accepted route. Never adopt another connection,
        // inventory or setting profile during shutdown, and never scan or open new devices.
        if (environment.Stamp.Connection != captured.Stamp.Connection || environment.Stamp.InventoryRevision != captured.Stamp.InventoryRevision ||
            environment.Stamp.Digest != captured.Stamp.Digest || !ReferenceEquals(captured, Volatile.Read(ref permit)))
            throw new IOException("OpenRGBの復旧前に接続・機器一覧・設定が変わりました");
        captured.Connection.CheckEnvironment(environment.Stamp);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref permit, new(captured.Connection, environment.Stamp), captured), captured))
            throw new IOException("OpenRGBの復旧条件が更新されています");
    }
    public async Task PollAsync(CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        var started = false;
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(closing || disposed, this);
            lock (gate) confirmedAbsent.Clear();
            started = true;
            Volatile.Write(ref permit, null);
            policy.CheckLocalRoute();
            if (connection?.IsConnected != true)
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                connection = await connect(ct).ConfigureAwait(false);
            }
            policy.CheckLocalRoute();
            var before = await connection.ReadEnvironmentAsync(ct, policy.CheckLocalRoute).ConfigureAwait(false);
            policy.CheckEnvironment(before);
            void CheckDiscovery() { policy.CheckLocalRoute(); connection.CheckEnvironment(before.Stamp); }
            var snapshots = await connection.ScanAsync(ct, CheckDiscovery).ConfigureAwait(false);
            // Initial discovery or a changed descriptor can advance the inventory revision. Obtain
            // a new receipt after scanning and reject settings that changed across discovery.
            var after = await connection.ReadEnvironmentAsync(ct, policy.CheckLocalRoute).ConfigureAwait(false);
            if (before.Stamp.Digest != after.Stamp.Digest) throw new OpenRgbSdkObservationChangedException();
            policy.CheckEnvironment(after);
            policy.CheckInventory(snapshots.Select(s => OpenRgbSdkProtocol.Copy(s.Controller)).ToArray());
            policy.CheckLocalRoute(); connection.CheckEnvironment(after.Stamp);
            if (snapshots.Any(s => s.Target.InventoryRevision != after.Stamp.InventoryRevision || s.Target.Connection != after.Stamp.Connection))
                throw new IOException("OpenRGBの機器一覧と接続条件が一致しません");
            var identities = OpenRgbDeviceIdentities.Resolve(snapshots, unresolved);
            var next = snapshots.ToDictionary(s => DeviceIdentity.Create(installation, Provider, identities[s.Target.ControllerId].Native),
                s => new OpenRgbBoundDevice(connection, s, identities[s.Target.ControllerId]));
            string[] missing;
            lock (gate)
            {
                if (next.Keys.Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                {
                    policy.CheckLocalRoute(); connection.CheckEnvironment(after.Stamp);
                    Volatile.Write(ref permit, new(connection, after.Stamp));
                    foreach (var driver in drivers.Values) driver.SetConnection(next.GetValueOrDefault(driver.DeviceId));
                    confirmedAbsent = drivers.Values.Where(d => !next.ContainsKey(d.DeviceId)).Cast<IDeviceControlDriver>().ToHashSet();
                    throw new DeviceDiscoveryCapacityException("OpenRGBの機器数が上限に達しました。不要な検出履歴を整理してください");
                }
                missing = next.Keys.Except(drivers.Keys).ToArray();
            }
            // Journal reads never occur under the registry observation lock.
            // Retain unselected drivers for discovery and durable recovery. Selection does not
            // excuse conflicting server controllers from the complete inventory policy above.
            var additions = missing.Select(id => new OpenRgbDeviceDriver(id, directory, boot, CheckPermit, selectedDevices.Contains(id), RefreshRecoveryPermitAsync, createTransport)).ToArray();
            // Reading recovery files may take time. Never publish a receipt that expired meanwhile.
            policy.CheckLocalRoute(); connection.CheckEnvironment(after.Stamp);
            Volatile.Write(ref permit, new(connection, after.Stamp));
            lock (gate)
            {
                foreach (var driver in additions) drivers.Add(driver.DeviceId, driver);
                foreach (var driver in drivers.Values) driver.SetConnection(next.GetValueOrDefault(driver.DeviceId));
                confirmedAbsent = drivers.Values.Where(d => !next.ContainsKey(d.DeviceId)).Cast<IDeviceControlDriver>().ToHashSet();
            }
        }
        catch (DeviceDiscoveryCapacityException) { throw; }
        catch
        {
            if (started)
            {
                Volatile.Write(ref permit, null);
                lock (gate) foreach (var driver in drivers.Values) driver.SetConnection(null);
            }
            throw;
        }
        finally { io.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; closing = true; }
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) if (disposed) return;
            var pending = CachedDrivers.Cast<OpenRgbDeviceDriver>().ToArray();
            foreach (var driver in pending) driver.BeginShutdown();
            var errors = new List<Exception>();
            foreach (var driver in pending)
                try { await driver.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) { errors.Add(e); }
            if (errors.Count > 0) throw new AggregateException("OpenRGBの終了・復旧が残っています", errors);
            Volatile.Write(ref permit, null);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            lock (gate) { connection = null; disposed = true; }
        }
        finally { io.Release(); }
    }
}
