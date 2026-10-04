using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Packaged definitions only. Construction does not open the SMBus or inspect the user's hardware.
public sealed partial class EneMemoryDeviceProvider : IDeviceCacheRetirement, IDeviceOperationPreparation
{
    private readonly object gate = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private volatile bool closing;
    private bool closed;
    private readonly string installation, directory, boot;
    private readonly IEneMemoryDeviceBackend backend;
    private readonly Dictionary<string, EneMemoryDeviceDriver> drivers = [];
    public string ProviderId => "ene-memory";
    public string[] ResourceDomains => EneMemoryDeviceBackend.ResourceDomains;
    public IDeviceControlDriver[] CachedDrivers { get { lock (gate) return drivers.Values.Cast<IDeviceControlDriver>().ToArray(); } }
    public EneMemoryDeviceProvider(string installation, string directory, string boot) : this(installation, directory, boot, CreateBackend(installation, boot)) { }
    private static IEneMemoryDeviceBackend CreateBackend(string installation, string boot)
    {
        ValidateIdentity(installation, boot);
        _ = LightingDeviceDefinitionCatalog.BuiltIn.Get(LightingDeviceDefinitionCatalog.EneId, "ene-dram-rgb");
        return new EneMemoryDeviceBackend();
    }
    private static void ValidateIdentity(string installation, string boot)
    {
        if (!DeviceServiceProtocol.Id(installation) || !DeviceConfigurationRules.Text(boot, 128)) throw new ArgumentException("メモリ照明のインストール・起動識別が不正です");
    }
    internal EneMemoryDeviceProvider(string installation, string directory, string boot, IEneMemoryDeviceBackend backend)
    {
        ValidateIdentity(installation, boot); this.installation = installation; this.directory = directory; this.boot = boot; this.backend = backend;
    }
    public async Task PollAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(closing, this); await PollCoreAsync(ct).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }
    private async Task PollCoreAsync(CancellationToken ct)
    {
        try
        {
            lock (gate) confirmedAbsent.Clear();
            var found = await backend.DiscoverAsync(ct).ConfigureAwait(false);
            if (found is not { Length: <= DeviceRegistry.MaximumDevices } || found.Any(c => !EneMemoryRecovery.ValidConnection(c)) ||
                found.DistinctBy(c => c.NativeIdentity).Count() != found.Length) throw new IOException("メモリ照明の検出結果が不正です");
            var next = found.ToDictionary(c => DeviceIdentity.Create(installation, ProviderId, c.NativeIdentity));
            string[] missing;
            lock (gate)
            {
                if (next.Keys.Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                {
                    foreach (var driver in drivers.Values) driver.SetConnection(next.GetValueOrDefault(driver.DeviceId));
                    confirmedAbsent = drivers.Values.Where(d => !next.ContainsKey(d.DeviceId)).Cast<IDeviceControlDriver>().ToHashSet();
                    throw new DeviceDiscoveryCapacityException("切断機器を含むメモリ照明数が上限に達しました。不要な検出履歴を整理してください");
                }
                missing = next.Keys.Except(drivers.Keys).ToArray();
            }
            var additions = missing.Select(id => new EneMemoryDeviceDriver(id, next[id], directory, boot, backend)).ToArray();
            lock (gate)
            {
                foreach (var d in additions) drivers.Add(d.DeviceId, d);
                foreach (var d in drivers.Values) d.SetConnection(next.GetValueOrDefault(d.DeviceId));
                confirmedAbsent = drivers.Values.Where(d => !next.ContainsKey(d.DeviceId)).Cast<IDeviceControlDriver>().ToHashSet();
            }
        }
        catch (DeviceDiscoveryCapacityException) { throw; }
        catch { lock (gate) foreach (var d in drivers.Values) d.SetConnection(null); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closing = true; }
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (closed) return; await backend.DisposeAsync().ConfigureAwait(false); lock (gate) closed = true; }
        finally { lifecycle.Release(); }
    }
    public Task<T> PrepareOperationAsync<T>(DevicePreparationKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        backend.PrepareOperationAsync(operation, ct);
}
