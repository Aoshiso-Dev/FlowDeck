using System.Diagnostics;

namespace FlowDeck.Infrastructure;

internal interface IWorkerProcessLifetime : IDisposable
{
    Task CloseInputAsync();
    Task<bool> WaitForExitAsync(TimeSpan timeout);
    void Kill();
}

internal sealed class WorkerProcessLifetime(Process process) : IWorkerProcessLifetime
{
    // StreamWriter.Close may flush; it must not prevent termination when the child stopped consuming input.
    public Task CloseInputAsync() => Task.Run(() => process.StandardInput.Close());
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        if (process.HasExited) return true;
        using var stop = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return false; }
    }
    public void Kill() { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    public void Dispose() => process.Dispose();
}

/// <summary>Called under the client's serial gate. Failed cleanup keeps the process owner available for retry.</summary>
internal sealed class WorkerProcessCleanup(IWorkerProcessLifetime process)
{
    private IDisposable? job;
    private Task? inputClose, drain;
    private bool exitConfirmed, processClosed, jobClosed;
    public bool ClosingStarted { get; private set; }
    public bool ResourcesClosed => exitConfirmed && processClosed && jobClosed && drain is { IsCompleted: true };
    public void AttachJob(IDisposable value)
    {
        if (ClosingStarted || job is not null) throw new InvalidOperationException("Workerの終了管理は起動時に1回だけ設定してください");
        job = value;
    }
    public async Task CloseAsync(IEnumerable<Task> readers)
    {
        if (ResourcesClosed) return;
        ClosingStarted = true;
        var errors = new List<Exception>();
        if (inputClose is null)
            try { inputClose = process.CloseInputAsync(); }
            catch (Exception e) { inputClose = Task.FromException(e); }
        drain ??= Task.WhenAll(readers.Append(inputClose));
        if (!exitConfirmed)
        {
            try { exitConfirmed = await process.WaitForExitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false); }
            catch (Exception e) { errors.Add(e); }
            if (!exitConfirmed) try { process.Kill(); } catch (Exception e) { errors.Add(e); }
        }
        // Closing the job also terminates owned descendants that could otherwise hold redirected pipes open.
        if (!jobClosed)
            try { job?.Dispose(); jobClosed = true; } catch (Exception e) { errors.Add(e); }
        if (!exitConfirmed)
        {
            try { exitConfirmed = await process.WaitForExitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception e) { errors.Add(e); }
        }
        if (!exitConfirmed)
        {
            errors.Add(new IOException("Workerの終了を確認できません。接続を保持して再試行します"));
            throw new AggregateException("Workerの終了に失敗しました", errors);
        }
        try { await drain.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception e)
        {
            // Task.WhenAll waits for all readers, even if the first one faults. Retain every underlying error.
            if (drain.Exception is { } faults) errors.AddRange(faults.InnerExceptions);
            else errors.Add(e);
        }
        if (drain.IsCompleted && jobClosed && !processClosed)
            try { process.Dispose(); processClosed = true; } catch (Exception e) { errors.Add(e); }
        if (!ResourcesClosed && errors.Count == 0) errors.Add(new IOException("Workerの接続終了が未完了です"));
        if (errors.Count > 0) throw new AggregateException("Workerの終了処理に失敗しました", errors);
    }
}
