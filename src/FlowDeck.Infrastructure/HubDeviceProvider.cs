using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Discovery owns no open Hub. Each remembered physical ID receives its own adapter and recovery journal.</summary>
public sealed partial class HubDeviceProvider : IDeviceCacheRetirement
{
    private readonly string installation, directory;
    private readonly IHubAdapter discovery;
    private readonly Func<string, IHubEndpointAdapter> createAdapter;
    private string[] rememberedIds;
    private readonly TimeProvider? clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly Dictionary<string, HubDeviceDriver> drivers = new(StringComparer.Ordinal);
    private readonly HashSet<IHubAdapter> adapters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<HubDeviceDriver, IHubEndpointAdapter> driverAdapters = [];
    private readonly HashSet<IHubAdapter> orphanedAdapters = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceControlDriver> closedDrivers = new(ReferenceEqualityComparer.Instance);
    private bool disposed, stopping, discoveryClosed;
    public string ProviderId => HubDeviceDriver.Provider;
    public string[] ResourceDomains => HubDeviceDriver.DefinitionData.DiscoveryResourceDomains.ToArray();
    public IDeviceControlDriver[] CachedDrivers { get { lock (gate) return drivers.Values.Cast<IDeviceControlDriver>().ToArray(); } }

    public HubDeviceProvider(string installation, string directory, IHubAdapter discovery,
        Func<string, IHubEndpointAdapter> createAdapter, IEnumerable<string>? rememberedIds = null, TimeProvider? clock = null)
    {
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("Hubのインストール識別子が不正です");
        this.rememberedIds = (rememberedIds ?? []).Take(DeviceRegistry.MaximumDevices + 1).ToArray();
        if (this.rememberedIds.Length > DeviceRegistry.MaximumDevices || this.rememberedIds.Any(id => !DeviceConfigurationRules.Text(id, 2048)) ||
            this.rememberedIds.Distinct(StringComparer.Ordinal).Count() != this.rememberedIds.Length)
            throw new ArgumentException("保存済みHubの識別情報が不正です");
        this.installation = installation; this.directory = directory; this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.createAdapter = createAdapter ?? throw new ArgumentNullException(nameof(createAdapter)); this.clock = clock;
        adapters.Add(discovery);
    }

    public async Task PollAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) confirmedAbsent.Clear();
            ObjectDisposedException.ThrowIf(stopping, this);
            if (orphanedAdapters.Count != 0) throw new IOException("生成に失敗したHub通信の終了が未完了です");
            // Register remembered identities even if enumeration fails, so their durable recovery obligations remain visible.
            foreach (var id in rememberedIds) await AddAsync(id).ConfigureAwait(false);
            var found = await discovery.ProbeAsync(ct).ConfigureAwait(false);
            if (found is not { Length: <= DeviceRegistry.MaximumDevices } || found.Any(h => h is null ||
                !DeviceConfigurationRules.Text(h.Id, 2048) || !DeviceConfigurationRules.Text(h.Description, 512) ||
                !DeviceConfigurationRules.Text(h.Path, 2048) || h.Serial is not null && !DeviceConfigurationRules.Text(h.Serial, 1024)) ||
                found.DistinctBy(h => h.Id).Count() != found.Length || found.DistinctBy(h => h.Path).Count() != found.Length ||
                found.Where(h => h.Serial is not null).DistinctBy(h => h.Serial).Count() != found.Count(h => h.Serial is not null))
                throw new IOException("Hubの個体識別が重複または不正です");
            string[] missing;
            lock (gate)
            {
                if (found.Select(h => h.Id).Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                {
                    var observed = found.ToDictionary(h => h.Id, StringComparer.Ordinal);
                    foreach (var entry in drivers) entry.Value.SetDiscovery(observed.GetValueOrDefault(entry.Key));
                    confirmedAbsent = drivers.Where(p => !observed.ContainsKey(p.Key)).Select(p => (IDeviceControlDriver)p.Value).ToHashSet();
                    throw new DeviceDiscoveryCapacityException("切断機器を含むHub数が上限に達しました。不要な検出履歴を整理してください");
                }
                missing = found.Select(h => h.Id).Except(drivers.Keys).ToArray();
            }
            foreach (var id in missing) { ct.ThrowIfCancellationRequested(); await AddAsync(id).ConfigureAwait(false); }
            var next = found.ToDictionary(h => h.Id, StringComparer.Ordinal);
            lock (gate) { foreach (var entry in drivers) entry.Value.SetDiscovery(next.GetValueOrDefault(entry.Key)); confirmedAbsent = drivers.Where(p => !next.ContainsKey(p.Key)).Select(p => (IDeviceControlDriver)p.Value).ToHashSet(); }
        }
        catch (DeviceDiscoveryCapacityException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            lock (gate) foreach (var driver in drivers.Values) driver.SetDiscovery(null);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private async Task AddAsync(string nativeId)
    {
        lock (gate) if (drivers.ContainsKey(nativeId)) return;
        var adapter = createAdapter(nativeId) ?? throw new IOException("Hub用の通信を作成できません");
        // A shared adapter would let opening Hub B silently redirect Hub A's commands.
        if (!adapters.Add(adapter)) throw new IOException("Hubごとに独立した通信が必要です");
        try
        {
            var driver = new HubDeviceDriver(installation, nativeId, adapter, directory, clock);
            driver.SetDiscovery(null);
            lock (gate) { drivers.Add(nativeId, driver); driverAdapters.Add(driver, adapter); }
        }
        catch (Exception original)
        {
            try { await adapter.DisposeAsync().ConfigureAwait(false); adapters.Remove(adapter); }
            catch (Exception closing)
            {
                orphanedAdapters.Add(adapter);
                throw new AggregateException("Hubの生成と通信の後始末に失敗しました", original, closing);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop new polls before waiting for the discovery already in progress.
        Volatile.Write(ref stopping, true);
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            var errors = new List<Exception>();
            // Runtime normally disposes these first; also cover drivers created but not accepted by the registry.
            foreach (var driver in CachedDrivers)
            {
                if (closedDrivers.Contains(driver)) continue;
                try { await driver.DisposeAsync().ConfigureAwait(false); closedDrivers.Add(driver); } catch (Exception e) { errors.Add(e); }
            }
            foreach (var adapter in orphanedAdapters.ToArray())
                try { await adapter.DisposeAsync().ConfigureAwait(false); orphanedAdapters.Remove(adapter); adapters.Remove(adapter); }
                catch (Exception e) { errors.Add(e); }
            if (!discoveryClosed)
                try { await discovery.DisposeAsync().ConfigureAwait(false); discoveryClosed = true; } catch (Exception e) { errors.Add(e); }
            disposed = closedDrivers.Count == CachedDrivers.Length && orphanedAdapters.Count == 0 && discoveryClosed;
            if (errors.Count > 0) throw new AggregateException("Hub取得プロバイダーの終了に失敗しました", errors);
        }
        finally { lifecycle.Release(); }
    }
}
