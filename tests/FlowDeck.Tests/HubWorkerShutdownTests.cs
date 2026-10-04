using System.Diagnostics;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class HubWorkerShutdownTests
{
    private static string Root
    {
        get
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "FlowDeck.sln"))) path = path.Parent;
            return path!.FullName;
        }
    }
    private sealed class FailingLog : IEventLog
    { public void Write(string category, object data) { if (category == "worker") throw new InvalidOperationException("log sink failed"); } }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReaderFailureStillTerminatesTheActualChildAndPermanentlyClosesTheClient(bool isolated)
    {
        await using var worker = new HubWorkerClient(Path.Combine(Root, ".tools", "python313", "python.exe"),
            Path.Combine(Root, "tests", "worker", "fake_shutdown_worker.py"), new FailingLog(), () => false, isolated);
        var identity = Assert.Single(await worker.ProbeAsync());
        using var child = Process.GetProcessById(int.Parse(identity.Serial!));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => worker.DisposeAsync().AsTask());
        Assert.Contains("log sink failed", failure.ToString());
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(child.HasExited);
        Assert.Equal("停止", worker.Version); Assert.False(worker.SupportsFanEndpoints);
        await worker.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.ProbeAsync());
    }

    [Fact]
    public async Task CloseOnAnUnopenedClientDoesNotLaunchAWorkerAndDisposePreventsLaterLaunch()
    {
        var worker = new HubWorkerClient("missing-python", "missing-worker", new TestLog(), () => false);
        await worker.CloseAsync(); Assert.Equal("未起動", worker.Version);
        await worker.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.ProbeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.CloseAsync());
    }
}
