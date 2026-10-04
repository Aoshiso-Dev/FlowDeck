namespace FlowDeck.Core;

public interface ILightingFrameTarget
{
    Task OpenAsync();
    bool IsCurrent { get; }
    Task<bool> SendAsync(Func<RgbColor> sample);
    Task CloseAsync();
}

/// <summary>For non-Hub writers that already own their target leases and recovery lifecycle.</summary>
public sealed class LightingFrameTarget(Func<RgbColor, Task> write, Func<bool> current) : ILightingFrameTarget
{
    public bool IsCurrent => current();
    public Task OpenAsync() => IsCurrent ? Task.CompletedTask : Task.FromException(new IOException("照明の接続を確認できません"));
    public async Task<bool> SendAsync(Func<RgbColor> sample)
    {
        if (!IsCurrent) throw new IOException("照明の接続が変更されました");
        await write(sample()); return true;
    }
    public Task CloseAsync() => Task.CompletedTask;
}

/// <summary>One monotonic clock and sampled color. Each write completes before the next sample; no frame queue accumulates.</summary>
public sealed class LightingAnimator : IAsyncDisposable
{
    private readonly ILightingFrameTarget target;
    private readonly string mode;
    private readonly RgbColor color;
    private readonly double period;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task loop;
    private readonly TimeProvider clock;
    public bool Running { get; private set; } = true;
    public string? Error { get; private set; }
    public int Frames { get; private set; }
    public int Skipped { get; private set; }
    public double MaximumWriteMilliseconds { get; private set; }
    public Task Completion => loop;
    public Task Started => started.Task;
    public LightingAnimator(IControlCoordinator core, string[] channels, string mode, RgbColor color, int speed,
        Func<RgbColor, Task> companion, TimeProvider? clock = null)
        : this(new HubFrameTarget(core, channels.ToArray(), companion), mode, color, speed, clock) { }
    public LightingAnimator(ILightingFrameTarget target, string mode, RgbColor color, int speed, TimeProvider? clock = null)
    {
        if (mode is not ("breathing" or "color-cycle" or "fixed" or "off") || !color.Valid || speed is < 0 or > 4)
            throw new ArgumentException("同期の設定が不正です");
        this.target = target; this.mode = mode; this.color = mode == "off" ? new(0, 0, 0) : color;
        this.period = HubLighting.Period(speed); this.clock = clock ?? TimeProvider.System;
        loop = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        var origin = clock.GetTimestamp();
        try
        {
            await target.OpenAsync();
            origin = clock.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
            do
            {
                stop.Token.ThrowIfCancellationRequested();
                if (!target.IsCurrent) throw new InvalidOperationException("別の照明操作または接続変更により同期を停止しました");
                var began = clock.GetTimestamp();
                if (await target.SendAsync(() => HubLighting.Frame(mode, color, clock.GetElapsedTime(origin).TotalSeconds, period)))
                {
                    Frames++; MaximumWriteMilliseconds = Math.Max(MaximumWriteMilliseconds, clock.GetElapsedTime(began).TotalMilliseconds);
                    started.TrySetResult();
                    if (mode is "fixed" or "off") break;
                }
                else Skipped++;
                if (Frames == 0 && clock.GetElapsedTime(origin).TotalSeconds > 10) throw new IOException("照明の同期を開始できませんでした");
            } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { Error = e.Message; started.TrySetException(e); }
        finally
        {
            // A deliberate stop settles at the selected solid color. Never overwrite a newer Hub/M5 request.
            if (stop.IsCancellationRequested && Error is null && target.IsCurrent)
            {
                try
                {
                    var settled = false;
                    for (var i = 0; i < 10 && !settled; i++)
                    {
                        settled = await target.SendAsync(() => color);
                        if (!settled) await Task.Delay(100);
                    }
                    if (!settled) Error = "同期は停止しました。固定色への変更を確認できません";
                }
                catch (Exception e) { Error = e.Message; }
            }
            try { await target.CloseAsync(); }
            catch (Exception e) { Error ??= e.Message; }
            Running = false;
            started.TrySetException(new IOException(Error ?? "同期を停止しました"));
        }
    }
    public async Task StopAsync() { stop.Cancel(); await loop; }
    public async ValueTask DisposeAsync() { await StopAsync(); stop.Dispose(); }
    private sealed class HubFrameTarget(IControlCoordinator core, string[] channels, Func<RgbColor, Task> companion) : ILightingFrameTarget
    {
        private LightingLease? lease;
        public async Task OpenAsync() => lease = await core.BeginLightingStreamAsync(channels);
        public bool IsCurrent => lease is not null && core.LightingLeaseValid(lease);
        public Task<bool> SendAsync(Func<RgbColor> sample) => core.SendLightingFrameAsync(lease ?? throw new IOException("同期が未開始です"), sample, companion);
        public Task CloseAsync() => lease is null ? Task.CompletedTask : core.EndLightingStreamAsync(lease);
    }
}
