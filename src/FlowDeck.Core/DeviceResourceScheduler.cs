namespace FlowDeck.Core;

public enum HardwarePriority { Recovery, Manual, Automatic, Monitoring, LightingFrame }

/// <summary>Bounded, resource-aware scheduling. A running operation holds every resource until its task actually settles.</summary>
public sealed class DeviceResourceScheduler(int capacity = 64)
{
    private sealed class Work(string[] domains, HardwarePriority priority, Func<Task> run, Action cancel)
    {
        public readonly string[] Domains = domains;
        public readonly HardwarePriority Priority = priority;
        public readonly Func<Task> Run = run;
        public readonly Action Cancel = cancel;
        public CancellationTokenRegistration Registration;
    }
    private readonly object gate = new();
    private readonly List<Work> pending = [];
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public Task<T> RunPreparedAsync<T>(IEnumerable<string> resources, HardwarePriority priority, object participant,
        DevicePreparationKind kind, Func<CancellationToken, Task<T>> execute, CancellationToken ct = default, Action? authorize = null)
    {
        var domains = Validate(resources, priority);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("通信準備の種類が不正です");
        ct.ThrowIfCancellationRequested();
        Task<T> Submit(CancellationToken token) => participant is IDevicePhasedOperation phased
            ? RunPhasedAsync(domains, priority, phased, kind, execute, token, authorize)
            : RunWithContextAsync(domains, priority, execute, token);
        return participant is IDeviceOperationPreparation preparation ? preparation.PrepareOperationAsync(kind, Submit, ct) : Submit(ct);
    }
    private async Task<T> RunPhasedAsync<T>(string[] domains, HardwarePriority priority, IDevicePhasedOperation participant,
        DevicePreparationKind kind, Func<CancellationToken, Task<T>> execute, CancellationToken ct, Action? authorize)
    {
        var hardware = new HardwareExecution(this, domains, priority, authorize);
        try { return await participant.RunPhasedOperationAsync(kind, hardware, execute, ct).ConfigureAwait(false); }
        finally { await hardware.CloseAsync().ConfigureAwait(false); }
    }
    private sealed class HardwareExecution(DeviceResourceScheduler scheduler, string[] domains, HardwarePriority priority, Action? authorize) : IDeviceHardwareExecution
    {
        private readonly object gate = new();
        private bool closed;
        private Task? running;
        public void CheckAuthorization()
        {
            lock (gate) if (closed) throw new InvalidOperationException("通信操作のスコープは終了しました");
            authorize?.Invoke();
        }
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> hardware, CancellationToken ct)
        {
            lock (gate)
            {
                if (closed) throw new InvalidOperationException("通信操作のスコープは終了しました");
                if (running is { IsCompleted: false }) throw new InvalidOperationException("前の通信段階を完了してから続けてください");
                var task = scheduler.RunWithContextAsync(domains, priority, token => { CheckAuthorization(); return hardware(token); }, ct);
                running = task; return task;
            }
        }
        public async Task CloseAsync()
        {
            Task? finish;
            lock (gate) { closed = true; finish = running; }
            // A faulty compiled provider cannot make an already-started phase disappear from lifetime accounting.
            if (finish is not null) try { await finish.ConfigureAwait(false); } catch { }
        }
    }
    private Task<T> RunWithContextAsync<T>(string[] domains, HardwarePriority priority, Func<CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        // A queued dispatch may run in a different submitter's ExecutionContext. Preserve the immutable
        // preparation scope belonging to this request rather than the scope of whichever job frees the bus.
        var context = ExecutionContext.Capture();
        return RunAsync(domains, priority, t =>
        {
            if (context is null) return execute(t);
            Task<T>? task = null;
            ExecutionContext.Run(context, _ => task = execute(t), null);
            return task!;
        }, ct);
    }
    public Task<T> RunAsync<T>(IEnumerable<string> resources, HardwarePriority priority, Func<CancellationToken, Task<T>> execute, CancellationToken ct = default)
    {
        var domains = Validate(resources, priority);
        ct.ThrowIfCancellationRequested(); var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(domains, priority, async () =>
        {
            try { completion.TrySetResult(await execute(ct).ConfigureAwait(false)); }
            catch (OperationCanceledException e) { completion.TrySetCanceled(e.CancellationToken); }
            catch (Exception e) { completion.TrySetException(e); }
        }, () => completion.TrySetCanceled(ct));
        lock (gate)
        {
            if (pending.Count >= capacity) throw new IOException("通信の待機要求が上限に達しました");
            pending.Add(work);
            work.Registration = ct.Register(() => { lock (gate) if (pending.Remove(work)) { work.Cancel(); Dispatch(); } });
            Dispatch();
        }
        return completion.Task;
    }
    private string[] Validate(IEnumerable<string> resources, HardwarePriority priority)
    {
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        var domains = resources.Take(17).ToArray();
        if (domains.Length is < 1 or > 16 || domains.Distinct(StringComparer.Ordinal).Count() != domains.Length || domains.Any(d => !DeviceConfigurationRules.Text(d, 256)) || !Enum.IsDefined(priority))
            throw new ArgumentException("通信資源の指定が不正です");
        return domains;
    }
    private void Dispatch()
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var work in pending.OrderBy(w => w.Priority).ToArray())
        {
            // A blocked operation retains its queue position across all required domains; later work cannot repeatedly steal one of them.
            if (work.Domains.Any(d => active.Contains(d) || reserved.Contains(d)))
            { foreach (var domain in work.Domains) reserved.Add(domain); continue; }
            pending.Remove(work); work.Registration.Unregister(); foreach (var resource in work.Domains) active.Add(resource);
            _ = Task.Run(async () =>
            {
                try { await work.Run().ConfigureAwait(false); }
                finally { lock (gate) { foreach (var resource in work.Domains) active.Remove(resource); Dispatch(); } }
            });
        }
    }
}
