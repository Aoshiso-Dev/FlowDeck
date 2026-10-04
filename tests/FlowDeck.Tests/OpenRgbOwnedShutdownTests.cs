using System.Diagnostics;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbOwnedProcessTests
{
    private sealed class Shutdown(List<string> events, Action request) : IOpenRgbShutdownSignal
    {
        public void Prepare(ProcessStartInfo start) => events.Add("shutdown-prepare");
        public void CheckStartInfo(ProcessStartInfo start) => events.Add("shutdown-check");
        public void Request() { events.Add("shutdown-request"); request(); }
        public void Dispose() => events.Add("shutdown-close");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OwnedShutdownFollowsRecoveryAndDistinguishesNormalExitFromFallback(bool cooperative)
    {
        var events = new List<string>(); var child = new Child(events); var failRestore = true; var releases = 0;
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => { }, _ => child,
            () => { releases++; events.Add("restore"); if (failRestore) throw new IOException("restore pending"); return Task.CompletedTask; },
            createShutdownSignal: () => new Shutdown(events, () => { if (cooperative) child.HasExited = true; }));
        await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.ResourcesClosed); Assert.DoesNotContain("shutdown-request", events); Assert.DoesNotContain("kill", events);
        failRestore = false; await owner.DisposeAsync();
        Assert.True(owner.ResourcesClosed); Assert.Equal(2, releases);
        Assert.Equal(cooperative, owner.GracefulExitConfirmed); Assert.Equal(!cooperative, owner.ForcedTerminationRequired);
        Assert.True(events.LastIndexOf("restore") < events.IndexOf("shutdown-request"));
        Assert.True(events.IndexOf("process-close") < events.IndexOf("shutdown-close"));
        Assert.True(events.IndexOf("shutdown-close") < events.IndexOf("image-close"));
        Assert.Equal(cooperative ? 0 : 1, events.Count(e => e == "kill"));
        await owner.DisposeAsync(); Assert.Equal(1, events.Count(e => e == "shutdown-request"));
    }
    [Fact]
    public async Task FailedSignalFallsBackOnlyAfterHardwareRelease()
    {
        var events = new List<string>(); var child = new Child(events);
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => { }), () => { }, _ => child,
            () => { events.Add("restore"); return Task.CompletedTask; },
            createShutdownSignal: () => new Shutdown(events, () => throw new IOException("event failed")));
        await owner.DisposeAsync(); Assert.True(owner.ResourcesClosed); Assert.True(owner.ForcedTerminationRequired);
        Assert.False(owner.GracefulExitConfirmed); Assert.IsType<IOException>(owner.GracefulShutdownFailure);
        Assert.True(events.IndexOf("restore") < events.IndexOf("kill"));
    }
    [Fact]
    public async Task ChangedSignalBeforeResumeClosesSuspendedChildWithoutRequestingNormalExit()
    {
        using var signal = new OpenRgbShutdownSignal(); var start = new ProcessStartInfo();
        var events = new List<string>(); var child = new Child(events); var validations = 0;
        await Assert.ThrowsAsync<IOException>(() => OpenRgbOwnedProcess.StartAsync(start, new Close(() => events.Add("image-close")),
            () => { if (++validations == 2) start.Environment[OpenRgbShutdownSignal.EnvironmentName] = "changed"; },
            _ => child, () => throw new Exception("never resumed"), createShutdownSignal: () => signal));
        Assert.DoesNotContain("resume", events); Assert.True(child.HasExited); Assert.Contains("image-close", events);
    }
    [Fact]
    public void PrivateSignalsAreIndependentLatchedAndCannotBeReused()
    {
        using var one = new OpenRgbShutdownSignal(); using var two = new OpenRgbShutdownSignal();
        var a = new ProcessStartInfo(); var b = new ProcessStartInfo(); one.Prepare(a); two.Prepare(b);
        Assert.NotEqual(a.Environment[OpenRgbShutdownSignal.EnvironmentName], b.Environment[OpenRgbShutdownSignal.EnvironmentName]);
        using var first = EventWaitHandle.OpenExisting(a.Environment[OpenRgbShutdownSignal.EnvironmentName]!);
        using var second = EventWaitHandle.OpenExisting(b.Environment[OpenRgbShutdownSignal.EnvironmentName]!);
        Assert.False(first.WaitOne(0)); one.Request(); Assert.True(first.WaitOne(0)); Assert.True(first.WaitOne(0)); Assert.False(second.WaitOne(0));
        one.CheckStartInfo(a); Assert.Throws<IOException>(() => one.Prepare(new()));
        a.Environment.Remove(OpenRgbShutdownSignal.EnvironmentName); Assert.Throws<IOException>(() => one.CheckStartInfo(a));
        one.Dispose(); Assert.Throws<IOException>(one.Request);
    }
    [Fact]
    public void PrivateSignalRejectsInheritedMetadata()
    {
        using var signal = new OpenRgbShutdownSignal(); var start = new ProcessStartInfo();
        start.Environment[OpenRgbShutdownSignal.EnvironmentName] = "unexpected";
        Assert.Throws<IOException>(() => signal.Prepare(start)); Assert.Throws<IOException>(signal.Request);
    }
    [Fact]
    public async Task ExitAfterSignalWithFailureCodeIsNotReportedAsGraceful()
    {
        var events = new List<string>(); var child = new Child(events) { ExitResult = 20 };
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => { }), () => { }, _ => child,
            () => Task.CompletedTask, createShutdownSignal: () => new Shutdown(events, () => child.HasExited = true));
        await owner.DisposeAsync(); Assert.True(owner.ResourcesClosed);
        Assert.False(owner.GracefulExitConfirmed); Assert.False(owner.ForcedTerminationRequired); Assert.NotNull(owner.GracefulShutdownFailure);
        Assert.DoesNotContain("kill", events);
    }
}
