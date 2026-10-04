using FlowDeck.Core;

namespace FlowDeck.Tests;

public partial class DeviceM5SessionTests
{
    private static async Task EnableM5Notifications(Fixture f, int recovery = 0)
    {
        var s = f.Client.Snapshot!;
        await f.Client.SaveNotificationsAsync(new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, new(DelaySeconds: 0, RecoverySeconds: recovery)));
    }
    private static async Task<DeviceNotificationEntry[]> M5Events(Fixture f) =>
        (await f.Client.ReadNotificationsAsync(new())).Events.Where(e => e.Event.Key == "system:m5-offline").ToArray();
    [Fact] public async Task RegisteredSessionReportsBothDirectionsButAutomaticUsbWifiSwitchIsSilent()
    {
        await using var f = new Fixture(); await f.Initialize(); await EnableM5Notifications(f); f.Factory.Paired = true;
        await f.Session.ConnectAsync(); f.Factory.UsbPresent = false; await f.Session.PollAsync();
        f.Factory.UsbPresent = true; await f.Session.PollAsync(); Assert.Empty(await M5Events(f));
        await f.Fan(55); var writes = f.Demo.Hubs[0].Writes;
        f.Factory.UsbPresent = f.Factory.WifiPresent = false; await f.Session.PollAsync(); await f.Session.PollAsync();
        Assert.Equal("raised", Assert.Single(await M5Events(f)).Event.Kind);
        f.Factory.WifiPresent = true; await f.Session.PollAsync(); Assert.Equal("recovered", (await M5Events(f))[^1].Event.Kind);
        Assert.Equal(writes, f.Demo.Hubs[0].Writes); Assert.Equal(55, (await f.Demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Null(f.Session.State.NotificationError);
    }
    [Theory][InlineData("disconnect")][InlineData("settings")][InlineData("pair")][InlineData("dispose")]
    public async Task IntentionalSessionTransitionsClearAnActiveAlarmWithoutARecoveryEvent(string transition)
    {
        await using var f = new Fixture(); await f.Initialize(); await EnableM5Notifications(f, transition == "pair" ? 5 : 0); await f.Session.ConnectAsync();
        f.Factory.UsbPresent = false; await f.Session.PollAsync(); Assert.Single(await M5Events(f));
        switch (transition)
        {
            case "disconnect": await f.Session.DisconnectAsync(); break;
            case "settings": await f.Save(f.Settings with { Connection = f.Settings.Connection with { Port = "COM15" } }); await f.Session.PollAsync(); break;
            case "dispose": await f.Session.DisposeAsync(); break;
            default:
                f.Factory.UsbPresent = true; await f.Session.PollAsync();
                Assert.Contains((await f.Client.ReadNotificationsAsync(new())).Active, e => e.Key == "system:m5-offline");
                await f.Session.PairAsync("demo", "test-password"); break;
        }
        Assert.Single(await M5Events(f)); Assert.DoesNotContain((await f.Client.ReadNotificationsAsync(new())).Active, e => e.Key == "system:m5-offline");
        Assert.All(f.Demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task UnregisteredDiscoveryAndNeverConnectedRegisteredDeviceDoNotWarn()
    {
        await using var f = new Fixture(); await f.Initialize(false); await EnableM5Notifications(f); await f.Session.ConnectAsync();
        f.Factory.UsbPresent = false; await f.Session.PollAsync(); Assert.Empty(await M5Events(f));
        await f.Save(f.Settings with { Connection = f.Settings.Connection with { DeviceId = "never-connected" } });
        await f.Session.PollAsync(); await f.Session.ConnectAsync(); await f.Session.PollAsync(); Assert.Empty(await M5Events(f));
    }
    [Fact] public async Task NotificationQueueCancellationDoesNotObserveStaleStateOrClosePcControlPipe()
    {
        await using var f = new Fixture(); await f.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Demo.Hubs[0].BeforeFanWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var pc = f.Fan(55); var observed = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var s = f.Client.Snapshot!;
            using var cancel = new CancellationTokenSource();
            var report = f.Client.ReportM5ConnectionAsync(s.State.Inventory.Epoch + "/" + s.Session, () => { observed = true; return new(); }, cancel.Token);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => report);
            Assert.False(observed); Assert.True(f.Client.Snapshot!.Connected);
        }
        finally { release.TrySetResult(); }
        await pc; f.Demo.Hubs[0].BeforeFanWrite = null; await f.Fan(56);
        Assert.Equal(56, (await f.Demo.Hubs[0].ReadAsync()).Channels[0].Duty);
    }
    [Fact] public async Task OldOwnerCannotSubmitANotificationToTheCurrentServiceSession()
    {
        await using var f = new Fixture(); await f.Initialize(); var observed = false;
        await Assert.ThrowsAsync<IOException>(() => f.Client.ReportM5ConnectionAsync(new string('a', 32) + "/" + new string('b', 32),
            () => { observed = true; return new(); }));
        Assert.False(observed); Assert.True(f.Client.Snapshot!.Connected); Assert.Empty(await M5Events(f));
    }
}
