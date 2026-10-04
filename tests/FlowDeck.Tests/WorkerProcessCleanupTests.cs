using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class WorkerProcessCleanupTests
{
    private sealed class Child : IWorkerProcessLifetime
    {
        public readonly List<string> Events = [];
        public int InputCloses, Kills, Waits, Disposes;
        public bool Exited, ExitOnInput = true, FailKill, FailWait, FailDispose;
        public Func<Task>? ClosingInput;
        public Task CloseInputAsync()
        {
            Events.Add("input"); InputCloses++;
            if (ClosingInput is not null) return ClosingInput();
            if (ExitOnInput) Exited = true;
            return Task.CompletedTask;
        }
        public Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            Events.Add("wait"); Waits++;
            if (FailWait) throw new IOException("exit observation failed");
            return Task.FromResult(Exited);
        }
        public void Kill()
        {
            Events.Add("kill"); Kills++;
            if (FailKill) throw new IOException("kill denied");
            Exited = true;
        }
        public void Dispose()
        {
            Events.Add("dispose"); Disposes++;
            Assert.True(Exited);
            if (FailDispose) throw new IOException("process close failed");
        }
    }
    private sealed class Job(Child child) : IDisposable
    {
        public int Closes;
        public bool Fail;
        public Action? Closed;
        public void Dispose()
        {
            child.Events.Add("job"); Closes++;
            if (Fail) throw new IOException("job close failed");
            Closed?.Invoke();
        }
    }

    [Fact]
    public async Task GracefulExitWaitsForBothReadersAndReleasesEachOwnerExactlyOnce()
    {
        var child = new Child(); var job = new Job(child); var cleanup = new WorkerProcessCleanup(child); cleanup.AttachJob(job);
        var output = new TaskCompletionSource(); var error = new TaskCompletionSource();
        var closing = cleanup.CloseAsync([output.Task, error.Task]);
        Assert.True(cleanup.ClosingStarted); Assert.False(cleanup.ResourcesClosed);
        Assert.Equal(1, job.Closes); Assert.Equal(0, child.Disposes);
        output.SetResult(); Assert.False(closing.IsCompleted); error.SetResult();
        await closing; Assert.True(cleanup.ResourcesClosed); Assert.Equal(0, child.Kills);
        await cleanup.CloseAsync([]); Assert.Equal(1, job.Closes); Assert.Equal(1, child.Disposes); Assert.Equal(1, child.InputCloses);
        Assert.True(child.Events.IndexOf("wait") < child.Events.IndexOf("dispose"));
    }

    [Fact]
    public async Task FailedKillDoesNotDiscardALiveProcessAndCanBeRetried()
    {
        var child = new Child { ExitOnInput = false, FailKill = true }; var cleanup = new WorkerProcessCleanup(child);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => cleanup.CloseAsync([]));
        Assert.Contains("kill denied", failure.ToString()); Assert.False(cleanup.ResourcesClosed); Assert.Equal(0, child.Disposes);
        child.FailKill = false; await cleanup.CloseAsync([]);
        Assert.True(cleanup.ResourcesClosed); Assert.Equal(2, child.Kills); Assert.Equal(1, child.Disposes); Assert.Equal(1, child.InputCloses);
    }

    [Fact]
    public async Task JobTerminationIsAttemptedEvenWhenKillingTheParentFails()
    {
        var child = new Child { ExitOnInput = false, FailKill = true }; var job = new Job(child) { Closed = () => child.Exited = true };
        var cleanup = new WorkerProcessCleanup(child); cleanup.AttachJob(job);
        await Assert.ThrowsAsync<AggregateException>(() => cleanup.CloseAsync([]));
        Assert.True(cleanup.ResourcesClosed); Assert.Equal(1, job.Closes); Assert.Equal(1, child.Disposes);
        await cleanup.CloseAsync([]); Assert.Equal(1, child.Kills);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InputCloseFailureStillTerminatesTheChildAndClosesAllOwners(bool synchronous)
    {
        var child = new Child { ExitOnInput = false };
        child.ClosingInput = () => synchronous ? throw new IOException("stdin failed") : Task.FromException(new IOException("stdin failed"));
        var job = new Job(child); var cleanup = new WorkerProcessCleanup(child); cleanup.AttachJob(job);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => cleanup.CloseAsync([]));
        Assert.Contains("stdin failed", failure.ToString()); Assert.True(cleanup.ResourcesClosed);
        Assert.Equal(1, child.Kills); Assert.Equal(1, child.Disposes); Assert.Equal(1, job.Closes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOwnerCloseRetriesOnlyTheUnfinishedOwner(bool jobFails)
    {
        var child = new Child { FailDispose = !jobFails }; var job = new Job(child) { Fail = jobFails };
        var cleanup = new WorkerProcessCleanup(child); cleanup.AttachJob(job);
        await Assert.ThrowsAsync<AggregateException>(() => cleanup.CloseAsync([]));
        Assert.False(cleanup.ResourcesClosed); var waits = child.Waits;
        child.FailDispose = job.Fail = false; await cleanup.CloseAsync([]);
        Assert.True(cleanup.ResourcesClosed); Assert.Equal(waits, child.Waits); Assert.Equal(1, child.InputCloses);
        Assert.Equal(jobFails ? 2 : 1, job.Closes); Assert.Equal(jobFails ? 1 : 2, child.Disposes);
    }

    [Fact]
    public async Task FaultedOutputDoesNotSkipErrorDrainAndEveryReaderFailureIsReported()
    {
        var child = new Child(); var cleanup = new WorkerProcessCleanup(child); var error = new TaskCompletionSource();
        var closing = cleanup.CloseAsync([Task.FromException(new IOException("stdout failed")), error.Task]);
        Assert.False(closing.IsCompleted); Assert.Equal(0, child.Disposes);
        error.SetException(new IOException("stderr failed"));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => closing);
        Assert.Contains("stdout failed", failure.ToString()); Assert.Contains("stderr failed", failure.ToString());
        Assert.True(cleanup.ResourcesClosed); Assert.Equal(1, child.Disposes);
    }

    [Fact]
    public async Task PendingReaderKeepsTheProcessOwnerAfterTimeoutUntilTheReaderFinishes()
    {
        var child = new Child(); var cleanup = new WorkerProcessCleanup(child); var output = new TaskCompletionSource();
        await Assert.ThrowsAsync<AggregateException>(() => cleanup.CloseAsync([output.Task]));
        Assert.False(cleanup.ResourcesClosed); Assert.Equal(0, child.Disposes); var waits = child.Waits;
        output.SetResult(); await cleanup.CloseAsync([output.Task]);
        Assert.True(cleanup.ResourcesClosed); Assert.Equal(waits, child.Waits); Assert.Equal(1, child.InputCloses);
    }

    [Fact]
    public async Task FailureToObserveExitKeepsTheProcessUntilExitCanBeConfirmed()
    {
        var child = new Child { FailWait = true }; var cleanup = new WorkerProcessCleanup(child);
        await Assert.ThrowsAsync<AggregateException>(() => cleanup.CloseAsync([]));
        Assert.False(cleanup.ResourcesClosed); Assert.Equal(0, child.Disposes);
        child.FailWait = false; await cleanup.CloseAsync([]);
        Assert.True(cleanup.ResourcesClosed); Assert.Equal(1, child.Disposes);
    }
}
