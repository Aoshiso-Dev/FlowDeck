using System.Diagnostics;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbOwnedProcessTests
{
    private sealed class Close(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed class Child(List<string> events) : IOpenRgbSuspendedProcess
    {
        public uint ProcessId => 123;
        public bool HasExited { get; set; }
        public int? ExitCode => HasExited ? ExitResult : null;
        public int ExitResult;
        public bool FailAttach, FailResume, RefuseExit;
        public IDisposable AttachJob()
        {
            events.Add("job"); if (FailAttach) throw new IOException("job failed");
            return new Close(() => { events.Add("job-close"); if (!RefuseExit) HasExited = true; });
        }
        public void Resume() { events.Add("resume"); if (FailResume) throw new IOException("resume failed"); }
        public Task CloseInputAsync() => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(HasExited);
        public void Kill() { events.Add("kill"); if (!RefuseExit) HasExited = true; }
        public void Dispose() => events.Add("process-close");
    }
    [Fact] public async Task OwnershipAndRevalidationPrecedeExecutionAndRecoveryPrecedesTermination()
    {
        var events = new List<string>(); var child = new Child(events); OpenRgbOwnedProcess? owner = null;
        owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => events.Add("validate"),
            _ => { events.Add("create-suspended"); return child; }, () => { owner!.CheckRunning(); events.Add("restore"); return Task.CompletedTask; });
        Assert.Equal(new[] { "validate", "create-suspended", "job", "validate", "resume" }, events);
        owner.CheckRunning(); await owner.DisposeAsync(); Assert.True(owner.ResourcesClosed);
        Assert.True(events.IndexOf("restore") < events.IndexOf("kill"));
        Assert.True(events.IndexOf("process-close") < events.IndexOf("image-close"));
        Assert.Throws<IOException>(owner.CheckRunning);
    }
    [Fact] public async Task InitialConfigurationFailureNeverCreatesAProcess()
    {
        var imageClosed = false; var creates = 0;
        await Assert.ThrowsAsync<IOException>(() => OpenRgbOwnedProcess.StartAsync(new(), new Close(() => imageClosed = true),
            () => throw new IOException("changed"), _ => { creates++; return new Child([]); }, () => throw new Exception("no hardware to restore")));
        Assert.True(imageClosed); Assert.Equal(0, creates);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedJobOrChangedConfigurationCannotRunTheChild(bool failedJob)
    {
        var events = new List<string>(); var child = new Child(events) { FailAttach = failedJob }; var validates = 0;
        await Assert.ThrowsAsync<IOException>(() => OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")),
            () => { if (++validates == 2) throw new IOException("changed"); }, _ => child, () => throw new Exception("must not restore before resume")));
        Assert.DoesNotContain("resume", events); Assert.True(child.HasExited); Assert.Contains("image-close", events);
    }
    [Fact] public async Task AFailedRestoreKeepsTheChildAndImageLeaseForRetry()
    {
        var events = new List<string>(); var child = new Child(events); var fail = true; var restores = 0;
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => { }, _ => child,
            () => { restores++; if (fail) throw new IOException("restore failed"); return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
        Assert.False(child.HasExited); Assert.False(owner.ResourcesClosed); Assert.DoesNotContain("kill", events); Assert.DoesNotContain("image-close", events);
        fail = false; await owner.DisposeAsync(); Assert.Equal(2, restores); Assert.True(owner.ResourcesClosed);
    }
    [Fact] public async Task UnknownExitKeepsOwnershipAndDoesNotRepeatSuccessfulHardwareRelease()
    {
        var events = new List<string>(); var child = new Child(events) { RefuseExit = true }; var releases = 0;
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => { }, _ => child,
            () => { releases++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask()); Assert.False(owner.ResourcesClosed);
        Assert.DoesNotContain("image-close", events); child.RefuseExit = false;
        await owner.DisposeAsync(); Assert.Equal(1, releases); Assert.True(owner.ResourcesClosed);
    }
    [Fact] public async Task FailedResumeCarriesUnfinishedRecoveryOutOfTheStartupCall()
    {
        var events = new List<string>(); var child = new Child(events) { FailResume = true }; var fail = true;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")),
            () => { }, _ => child, () => { if (fail) throw new IOException("restore pending"); return Task.CompletedTask; }));
        Assert.False(error.ResourcesClosed); Assert.DoesNotContain("kill", events);
        fail = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed); Assert.Contains("image-close", events);
    }
    [Fact] public async Task WindowsChildReallyStaysSuspendedUntilJobAttachmentAndResume()
    {
        using var d = new ServiceTestDirectory();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = d.Path };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit 0");
        var child = WindowsSuspendedProcess.Create(start); IDisposable? job = null;
        try
        {
            Assert.False(await child.WaitForExitAsync(TimeSpan.FromMilliseconds(40)));
            Assert.Throws<IOException>(child.Resume); job = child.AttachJob(); child.Resume();
            Assert.True(await child.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { child.Kill(); job?.Dispose(); child.Dispose(); }
    }
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("日本語 path\\", "\"日本語 path\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    public void NativeArgumentsPreserveSpacesQuotesAndTrailingSlashes(string value, string expected) => Assert.Equal(expected, WindowsSuspendedProcess.Quote(value));

    [Fact] public async Task ClosingTheCreationJobTerminatesEvenANeverResumedChild()
    {
        using var d = new ServiceTestDirectory();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = d.Path };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit 0");
        var child = WindowsSuspendedProcess.Create(start); IDisposable? job = null;
        try
        {
            Assert.False(child.HasExited); job = child.AttachJob(); job.Dispose();
            Assert.True(await child.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { child.Kill(); job?.Dispose(); child.Dispose(); }
    }
}
