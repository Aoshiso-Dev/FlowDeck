namespace FlowDeck.Infrastructure;

/// <summary>Also holds the original mutex so old and renamed versions cannot own hardware together.</summary>
public sealed class ApplicationInstance : IDisposable
{
    private readonly List<Mutex> held = [];
    public bool Acquired { get; private set; }
    public ApplicationInstance(bool smoke, string? testPrefix = null, bool service = false)
    {
        var names = smoke ? new[] { @"Local\FlowDeck-DemoSmoke" } : service ? new[] { @"Local\FlowDeck-ServiceGui" }
            : new[] { @"Global\NzxtControlHub-1E71-2022", @"Global\FlowDeck-1E71-2022" };
        foreach (var name in names)
        {
            var mutex = new Mutex(true, testPrefix is null ? name : testPrefix + name.Replace('\\', '-'), out var created);
            if (!created) { mutex.Dispose(); Dispose(); return; }
            held.Add(mutex);
        }
        Acquired = true;
    }
    public void Dispose()
    {
        foreach (var mutex in held.AsEnumerable().Reverse()) { mutex.ReleaseMutex(); mutex.Dispose(); }
        held.Clear(); Acquired = false;
    }
}
