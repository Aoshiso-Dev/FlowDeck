using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Owns factory results until a runtime successfully takes them, including invalid registrations.</summary>
internal sealed class DeviceComponentCleanup(DeviceRuntimeComponents components) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly HashSet<IDeviceControlDriver> drivers = new(components.Drivers.Where(d => d is not null), ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceDriverProvider> providers = new(components.Providers.Where(p => p is not null), ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceControlDriver> closedDrivers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceDriverProvider> closedProviders = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IDeviceDriverProvider, HashSet<IDeviceControlDriver>> children = new(ReferenceEqualityComparer.Instance);
    private volatile bool closed;
    public bool ResourcesClosed => closed;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            var errors = new List<Exception>();
            var unreadable = new HashSet<IDeviceDriverProvider>(ReferenceEqualityComparer.Instance);
            foreach (var provider in providers.Where(p => !closedProviders.Contains(p)))
            {
                try
                {
                    var values = provider.CachedDrivers ?? throw new IOException("起動途中の機器一覧を取得できません");
                    if (!children.TryGetValue(provider, out var known)) children[provider] = known = new(ReferenceEqualityComparer.Instance);
                    foreach (var driver in values.Where(d => d is not null)) { known.Add(driver); drivers.Add(driver); }
                }
                catch (Exception e) { errors.Add(e); unreadable.Add(provider); }
            }
            foreach (var driver in drivers.Where(d => !closedDrivers.Contains(d)))
                try { await driver.DisposeAsync().ConfigureAwait(false); closedDrivers.Add(driver); }
                catch (Exception e) { errors.Add(e); }
            foreach (var provider in providers.Where(p => !closedProviders.Contains(p)))
            {
                // A broken getter must not skip independent providers or close a parent before its children.
                if (unreadable.Contains(provider) || children[provider].Any(d => !closedDrivers.Contains(d))) continue;
                try { await provider.DisposeAsync().ConfigureAwait(false); closedProviders.Add(provider); }
                catch (Exception e) { errors.Add(e); }
            }
            closed = drivers.All(closedDrivers.Contains) && providers.All(closedProviders.Contains);
            if (errors.Count != 0) throw new AggregateException("起動途中の機器接続を閉じられません", errors);
        }
        finally { gate.Release(); }
    }
}
