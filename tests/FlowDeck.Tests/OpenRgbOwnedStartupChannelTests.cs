using System.Diagnostics;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

internal sealed class MockOpenRgbStartupChannel : IOpenRgbStartupChannel
{
    public readonly List<string> Events;
    public bool FailPrepare, FailBind, FailReady, FailClose, FailStartInfo;
    public bool ResourcesClosed { get; private set; }
    public Action<OpenRgbOwnedProcess>? OnBind;
    public MockOpenRgbStartupChannel(List<string> events) => Events = events;
    public void Prepare(ProcessStartInfo start) { Events.Add("channel-prepare"); if (FailPrepare) throw new IOException("prepare"); }
    public void CheckStartInfo(ProcessStartInfo start) { Events.Add("channel-info"); if (FailStartInfo) throw new IOException("info"); }
    public void Bind(OpenRgbOwnedProcess owner)
    {
        Events.Add("channel-bind"); owner.CheckSuspended(); Assert.Throws<IOException>(owner.CheckRunning);
        OnBind?.Invoke(owner); if (FailBind) throw new IOException("bind");
    }
    public void CheckReady() { Events.Add("channel-ready"); if (FailReady || ResourcesClosed) throw new IOException("ready"); }
    public ValueTask DisposeAsync()
    {
        Events.Add("channel-close"); if (FailClose) throw new IOException("close");
        ResourcesClosed = true; return ValueTask.CompletedTask;
    }
}

public sealed partial class OpenRgbOwnedProcessTests
{
    [Fact] public async Task AuthorizationIsBoundBeforeResumeAndRetainedThroughRestorationAndExit()
    {
        var events = new List<string>(); var channel = new MockOpenRgbStartupChannel(events); var child = new Child(events);
        OpenRgbOwnedProcess? owner = null;
        owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => events.Add("validate"),
            _ => { events.Add("create"); return child; }, () => { owner!.CheckRunning(); events.Add("restore"); return Task.CompletedTask; }, () => channel);
        Assert.Equal(new[] { "channel-prepare", "validate", "channel-info", "create", "job", "channel-bind", "validate", "channel-info", "channel-ready", "resume", "channel-ready" }, events);
        Assert.Throws<IOException>(owner.CheckSuspended);
        await owner.DisposeAsync(); Assert.True(owner.ResourcesClosed);
        Assert.True(events.IndexOf("restore") < events.IndexOf("process-close"));
        Assert.True(events.IndexOf("process-close") < events.IndexOf("channel-close"));
        Assert.True(events.IndexOf("channel-close") < events.IndexOf("image-close"));
    }

    [Theory] [InlineData("factory")] [InlineData("prepare")] [InlineData("info")] [InlineData("bind")] [InlineData("ready")]
    public async Task FailedStartupAuthorizationCannotResumeOrLeaveResourcesBehind(string failure)
    {
        var events = new List<string>(); var child = new Child(events);
        var channel = new MockOpenRgbStartupChannel(events) { FailPrepare = failure == "prepare", FailStartInfo = failure == "info",
            FailBind = failure == "bind", FailReady = failure == "ready" };
        await Assert.ThrowsAsync<IOException>(() => OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => { },
            _ => { events.Add("create"); return child; }, () => throw new Exception("no hardware before resume"),
            () => failure == "factory" ? throw new IOException("factory") : channel));
        Assert.DoesNotContain("resume", events); Assert.Contains("image-close", events);
        if (failure != "factory") Assert.True(channel.ResourcesClosed);
        if (events.Contains("create")) Assert.True(child.HasExited);
    }

    [Theory] [InlineData("restore")] [InlineData("exit")] [InlineData("channel")]
    public async Task IncompleteShutdownRetainsTheResourcesNeededForRetry(string failure)
    {
        var events = new List<string>(); var child = new Child(events) { RefuseExit = failure == "exit" };
        var channel = new MockOpenRgbStartupChannel(events) { FailClose = failure == "channel" }; var failRestore = failure == "restore";
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")), () => { }, _ => child,
            () => failRestore ? throw new IOException("restore") : Task.CompletedTask, () => channel);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.ResourcesClosed); Assert.False(channel.ResourcesClosed); Assert.DoesNotContain("image-close", events);
        if (failure != "channel") Assert.DoesNotContain("channel-close", events);
        failRestore = child.RefuseExit = channel.FailClose = false;
        await owner.DisposeAsync(); Assert.True(owner.ResourcesClosed);
    }

    [Fact] public async Task FailedChannelHealthRevokesRunningChecksButStillAllowsConfirmedShutdown()
    {
        var channel = new MockOpenRgbStartupChannel([]); var child = new Child([]);
        var owner = await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => { }), () => { }, _ => child, () => Task.CompletedTask, () => channel);
        channel.FailReady = true; Assert.Throws<IOException>(owner.CheckRunning); Assert.Throws<IOException>(owner.CheckOwned);
        await owner.DisposeAsync(); Assert.True(owner.ResourcesClosed);
    }

    [Fact] public async Task StartupFailurePreservesFailedChannelClosureForTheReturnedCleanupOwner()
    {
        var events = new List<string>(); var channel = new MockOpenRgbStartupChannel(events) { FailBind = true, FailClose = true };
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => OpenRgbOwnedProcess.StartAsync(new(), new Close(() => events.Add("image-close")),
            () => { }, _ => new Child(events), () => Task.CompletedTask, () => channel));
        Assert.False(error.ResourcesClosed); Assert.DoesNotContain("resume", events); Assert.DoesNotContain("image-close", events);
        channel.FailClose = false; await error.DisposeAsync(); Assert.True(error.ResourcesClosed);
    }
}
