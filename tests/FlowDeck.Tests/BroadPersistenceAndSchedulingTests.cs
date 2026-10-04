using System.Collections.Concurrent;
using System.Text;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class BroadPersistenceAndSchedulingTests
{
    [Theory]
    [InlineData("current.json")]
    [InlineData("current.json.bak")]
    public void ActualWindowsSharingFailurePreservesTheActiveGenerationAndAllowsExactRetry(string lockedFile)
    {
        using var dir = new ServiceTestDirectory();
        var store = new ConfigurationGenerationStore(dir.Path, _ => { });
        Dictionary<string, byte[]> Files(int n) => ConfigurationGenerationStore.ComponentNames.ToDictionary(k => k,
            _ => Encoding.UTF8.GetBytes($$"""{"schemaVersion":2,"value":{{n}}}"""));
        var first = store.Stage(Files(1)); store.Activate(first);
        var second = store.Stage(Files(2)); store.Activate(second);
        var third = store.Stage(Files(3));
        var pointerBefore = File.ReadAllBytes(Path.Combine(dir.Path, "current.json"));
        using (var hold = new FileStream(Path.Combine(dir.Path, lockedFile), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.Activate(third));
            Assert.Equal(pointerBefore, File.ReadAllBytes(Path.Combine(dir.Path, "current.json")));
            Assert.Equal(second.Target, store.ReadActive()!.Pointer);
            Assert.Empty(Directory.GetFiles(dir.Path, "current-*.tmp"));
        }
        store.Activate(third);
        Assert.Equal(third.Target, store.ReadActive()!.Pointer);
        Assert.All(store.ReadActive()!.Files.Values, bytes => Assert.Contains("\"value\":3", Encoding.UTF8.GetString(bytes)));
        store.Activate(store.PrepareRollback(first.Target));
        Assert.All(store.ReadActive()!.Files.Values, bytes => Assert.Contains("\"value\":1", Encoding.UTF8.GetString(bytes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(91)]
    [InlineData(20261002)]
    public async Task MixedPriorityAndCancellationNeverOverlapResourcesOrLoseQueuedWork(int seed)
    {
        var rng = new Random(seed); var scheduler = new DeviceResourceScheduler(128);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var domains = new[] { "a", "b", "c", "d" };
        var blocked = scheduler.RunAsync(domains, HardwarePriority.Manual, async _ => { entered.SetResult(); await release.Task; return -1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = new ConcurrentDictionary<string, int>(); var executed = new ConcurrentDictionary<int, byte>();
        var tokens = new List<CancellationTokenSource>(); var work = new List<Task<int>>();
        try
        {
            for (var i = 0; i < 48; i++)
            {
                var id = i; var selected = domains.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4)).ToArray();
                var token = new CancellationTokenSource(); tokens.Add(token);
                work.Add(scheduler.RunAsync(selected, (HardwarePriority)rng.Next(5), async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var domain in selected) Assert.True(active.TryAdd(domain, id), "Concurrent access: " + domain);
                    try { executed.TryAdd(id, 0); for (var step = 0; step < 4; step++) await Task.Yield(); return id; }
                    finally { foreach (var domain in selected) Assert.True(active.TryRemove(new KeyValuePair<string, int>(domain, id))); }
                }, token.Token));
                if (i % 3 == 0) token.Cancel();
            }
            Assert.Equal(123, await scheduler.RunAsync(["independent"], HardwarePriority.Monitoring, _ => Task.FromResult(123)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(executed);
            release.SetResult(); await blocked;
            for (var i = 0; i < work.Count; i++)
                if (i % 3 == 0) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work[i]);
                else Assert.Equal(i, await work[i].WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(32, executed.Count); Assert.Equal(0, scheduler.PendingCount); Assert.Empty(active);
        }
        finally { release.TrySetResult(); await blocked; foreach (var token in tokens) token.Dispose(); }
    }
}
