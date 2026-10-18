using FlowDeck.App;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceDialReconnectTests
{
    private static readonly DeviceDialSettings Saved = new("COM6", "dial-a8a16e8dbd44");

    [Theory]
    [InlineData("COM6", null)]
    [InlineData("COM6", "")]
    [InlineData("COM6", "bad\nidentity")]
    [InlineData("COM0", "dial")]
    [InlineData("COM01", "dial")]
    [InlineData("com6", "dial")]
    [InlineData("COM6/other", "dial")]
    public async Task InvalidOrUnregisteredSettingsNeverOpenAPort(string port, string? id)
    {
        var reconnect = new DeviceDialReconnect();
        reconnect.Enable(new(port, id), false, 0);
        Assert.False(reconnect.Enabled);
        Assert.False(await reconnect.TryAsync(0, true,
            _ => throw new InvalidOperationException("Physical gate reached"),
            (_, _) => throw new InvalidOperationException("Port opened")));
    }

    [Fact]
    public async Task DemoDoesNotEvaluateThePhysicalGate()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, true, 0);
        Assert.False(reconnect.Enabled);
        Assert.False(await reconnect.TryAsync(0, true,
            _ => throw new InvalidOperationException("Physical gate reached"),
            (_, _) => throw new InvalidOperationException("Port opened")));
    }

    [Fact]
    public async Task UnpluggedStartupWaitsForOnlyTheSavedPortWithoutRewritingSettings()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "dial-v1.json");
        File.WriteAllText(path, "{ \"port\": \"COM28\", \"deviceId\": \"registered-dial\" }");
        var original = File.ReadAllBytes(path); var settings = new DeviceDialSettingsStore(path).Load();
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(settings, false, 0);
        var calls = new List<DeviceDialSettings>();
        Task Connect(DeviceDialSettings target, Func<bool> current)
        { Assert.True(current()); calls.Add(target); return Task.CompletedTask; }
        foreach (var now in new long[] { 0, 1_000, 30_000, 600_000 })
            Assert.False(await reconnect.TryAsync(now, false, _ => true, Connect));
        Assert.Empty(calls);
        Assert.True(await reconnect.TryAsync(601_000, true, _ => true, Connect));
        Assert.Equal(settings, Assert.Single(calls)); Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RepeatedOpenFailuresBackOffAndARealReplugRemovesTheLongWait()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        var calls = 0; long now = 0;
        Task Failed(DeviceDialSettings _, Func<bool> __)
        { calls++; throw new IOException("Port busy"); }
        foreach (var delay in new long[] { 2_000, 4_000, 8_000, 16_000, 30_000, 30_000 })
        {
            await Assert.ThrowsAsync<IOException>(() => reconnect.TryAsync(now, true, _ => true, Failed));
            Assert.False(reconnect.Attempting);
            Assert.False(await reconnect.TryAsync(now + delay - 1, true, _ => true, Failed));
            now += delay;
        }
        Assert.Equal(6, calls);
        // A missing -> present transition should not inherit a busy-port retry delay.
        await reconnect.TryAsync(now - 20_000, false, _ => true, Failed);
        await Assert.ThrowsAsync<IOException>(() => reconnect.TryAsync(now - 19_000, true, _ => true, Failed));
        Assert.Equal(7, calls);
    }

    [Fact]
    public async Task ManualDisconnectOrPortEditStopsWaitingUntilExplicitlyEnabledAgain()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        var calls = 0;
        Task Connect(DeviceDialSettings _, Func<bool> __) { calls++; return Task.CompletedTask; }
        await reconnect.TryAsync(0, false, _ => true, Connect);
        reconnect.Suppress();
        Assert.False(reconnect.Enabled);
        Assert.False(await reconnect.TryAsync(1_000, true, _ => true, Connect));
        reconnect.Enable(Saved, false, 2_000);
        Assert.True(await reconnect.TryAsync(2_000, true, _ => true, Connect));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RepeatedTimerTicksDoNotOverlapAPendingConnection()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task Connect(DeviceDialSettings _, Func<bool> current)
        { calls++; entered.SetResult(); await resume.Task; Assert.True(current()); }
        var first = reconnect.TryAsync(0, true, _ => true, Connect);
        await entered.Task; Assert.True(reconnect.Attempting);
        Assert.False(await reconnect.TryAsync(30_000, true, _ => true, Connect));
        Assert.Equal(1, calls);
        resume.SetResult(); Assert.True(await first); Assert.False(reconnect.Attempting);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("exit")]
    [InlineData("port-reserved")]
    [InlineData("new-target")]
    public async Task PendingCleanupRechecksManualExitPortReservationAndTargetGeneration(string change)
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowed = true; var opened = 0;
        var pending = reconnect.TryAsync(0, true, _ => allowed, async (_, current) =>
        { entered.SetResult(); await resume.Task; if (current()) opened++; });
        await entered.Task;
        if (change == "manual") reconnect.Suppress();
        else if (change == "new-target") reconnect.Enable(new("COM28", "different-registered-dial"), false, 1_000);
        else allowed = false;
        resume.SetResult(); await pending;
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task ChoosingANewTargetCannotOverlapThePreviousCleanup()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reconnect.TryAsync(0, true, _ => true, async (_, current) =>
        { entered.SetResult(); await resume.Task; Assert.False(current()); });
        await entered.Task;
        var next = new DeviceDialSettings("COM28", "different-registered-dial");
        reconnect.Enable(next, false, 1_000);
        var opened = new List<DeviceDialSettings>();
        Task Connect(DeviceDialSettings target, Func<bool> current)
        { Assert.True(current()); opened.Add(target); return Task.CompletedTask; }
        Assert.False(await reconnect.TryAsync(1_000, true, _ => true, Connect));
        resume.SetResult(); await pending;
        Assert.True(await reconnect.TryAsync(2_000, true, _ => true, Connect));
        Assert.Equal(next, Assert.Single(opened));
    }

    [Fact]
    public async Task ReservedPortIsNotOpenedButCanReconnectAfterReservationEnds()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        var calls = 0;
        Task Connect(DeviceDialSettings _, Func<bool> current)
        { Assert.True(current()); calls++; return Task.CompletedTask; }
        Assert.False(await reconnect.TryAsync(0, true, _ => false, Connect));
        Assert.True(await reconnect.TryAsync(1_000, true, _ => true, Connect));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RegisteredHandshakeResetsFailedOpenBackoff()
    {
        var reconnect = new DeviceDialReconnect(); reconnect.Enable(Saved, false, 0);
        Task Connect(DeviceDialSettings _, Func<bool> __) => Task.CompletedTask;
        await reconnect.TryAsync(0, true, _ => true, Connect);
        await reconnect.TryAsync(2_000, true, _ => true, Connect);
        await reconnect.TryAsync(6_000, true, _ => true, Connect);
        reconnect.Connected(6_100);
        Assert.True(await reconnect.TryAsync(8_100, true, _ => true, Connect));
        Assert.False(await reconnect.TryAsync(10_099, true, _ => true, Connect));
        Assert.True(await reconnect.TryAsync(10_100, true, _ => true, Connect));
    }
}
