using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceM5SessionTests
{
    [Theory][InlineData("deviceType")][InlineData("missing-boot")][InlineData("blank-boot")][InlineData("long-firmware")]
    [InlineData("foreign-device")][InlineData("overflow")][InlineData("wrong-version")][InlineData("valid")]
    public void UsbRegistrationAndRouteSelectionUseTheSameHelloValidation(string variant)
    {
        var values = new Dictionary<string, object?> { ["v"] = 1, ["type"] = "hello", ["deviceType"] = "M5GO", ["deviceId"] = "panel", ["bootId"] = "boot", ["firmwareVersion"] = "1.3.0" };
        switch (variant)
        {
            case "deviceType": values["deviceType"] = "other"; break;
            case "missing-boot": values.Remove("bootId"); break;
            case "blank-boot": values["bootId"] = " "; break;
            case "long-firmware": values["firmwareVersion"] = new string('x', 65); break;
            case "foreign-device": values["deviceId"] = "another"; break;
            case "overflow": values["v"] = long.MaxValue; break;
            case "wrong-version": values["v"] = 2; break;
        }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(values));
        Assert.Equal(variant == "valid", M5HelloIdentity.TryRead(json.RootElement, "panel", out var identity));
        if (variant == "valid") Assert.Equal(new M5HelloIdentity("panel", "boot", "1.3.0"), identity); else Assert.Null(identity);
    }
    private sealed class Log : IEventLog { public void Write(string category, object data) { } }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public DemoDeviceWorkspace Demo { get; private set; } = null!;
        public DeviceWorkspaceClient Client { get; private set; } = null!;
        public DeviceServicePipeClient Pipe { get; private set; } = null!;
        public DemoM5LinkFactory Factory { get; } = new();
        public DeviceM5Session Session { get; private set; } = null!;
        public async Task Initialize(bool registered = true, bool autoStart = false)
        {
            Demo = await DemoDeviceWorkspace.CreateAsync(directory.Path); Pipe = DeviceServicePipeClient.ForDemo(Demo.PipeName, Environment.ProcessId);
            Client = new(Pipe); await Client.ConnectAsync(); Session = new(Client, Factory);
            var s = Client.Snapshot!;
            await Save(new(new("COM14", registered ? Factory.DeviceId : null, Transport: "auto", WifiAddress: "192.168.1.19"), new() {
                Hub = s.Configuration.Configuration.Bindings.Devices.Single(b => b.DeviceId == Demo.DeviceIds[0]), Fans = ["fan1"], Leds = ["led1"],
                Cpu = s.Configuration.Configuration.Dashboard.Items.Single(i => i.Name == "CPU").Target }, autoStart));
        }
        public DeviceM5Settings Settings => DeviceM5Configuration.Read(Client.Snapshot!.Configuration.Configuration.Preferences);
        public async Task Save(DeviceM5Settings settings)
        {
            var s = Client.Snapshot!; await Client.SaveM5Async(new(s.State.Inventory.Epoch, s.Session!, s.Configuration.Generation, s.State.Inventory.TopologyRevision, settings));
        }
        public async Task Fan(int duty)
        {
            await Client.RefreshAsync(); var s = Client.Snapshot!; var t = new EndpointReference(Demo.DeviceIds[0], "fan1"); var e = s.State.Endpoints.Single(e => e.Target == t);
            Assert.Equal(Outcome.Confirmed, (await Client.ApplyCoolingAsync(new(new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation,
                new(t, e.Generation, e.PolicyRevision)), DeviceOperation.SetDuty, duty))).Outcome);
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Session is not null) await Session.DisposeAsync(); }
            finally
            {
                try { if (Client is not null) await Client.DisposeAsync(); }
                finally { try { if (Demo is not null) await Demo.DisposeAsync(); } finally { directory.Dispose(); } }
            }
        }
    }
    [Fact] public async Task DemoCannotConstructPhysicalConnectionManagerEvenBeforeHello()
    {
        await using var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo("FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), Environment.ProcessId));
        using var dir = new ServiceTestDirectory(); var factory = new DeviceM5LinkFactory(dir.Path, new Log());
        Assert.Equal(Path.Combine(dir.Path, "m5-v2"), factory.IdentityDirectory);
        Assert.Throws<InvalidOperationException>(() => new DeviceM5Session(client, factory)); Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }
    [Fact] public async Task MissingPortIsNotAutomaticallyGuessed()
    {
        await using var f = new Fixture(); await f.Initialize(false); await f.Save(f.Settings with { Connection = new() });
        await Assert.ThrowsAsync<IOException>(() => f.Session.ConnectAsync()); Assert.Empty(f.Factory.Links);
    }
    [Fact] public async Task UnregisteredUsbIsReadOnlyUntilObservedIdIsSavedAndNewSessionIsOpened()
    {
        await using var f = new Fixture(); await f.Initialize(false); await f.Session.ConnectAsync(); var discovery = Assert.Single(f.Factory.Links);
        Assert.True(f.Session.State.CanRegister); Assert.Null(discovery.Control.Selection.Hub); Assert.Null(discovery.Lighting);
        Assert.Empty(discovery.Control.Selection.Fans); Assert.False(discovery.Control.ReadM5State().Snapshot.HubReady);
        await f.Session.RegisterAsync(); Assert.True(discovery.Closed); Assert.True(discovery.Disposed); Assert.Equal(2, f.Factory.Links.Count);
        Assert.Equal(f.Factory.DeviceId, f.Settings.Connection.DeviceId); Assert.False(f.Settings.ConnectOnStart); Assert.False(f.Session.State.NeedsRegistration);
        Assert.Equal(f.Demo.DeviceIds[0], f.Factory.Links.Last().Control.Selection.Hub!.DeviceId); Assert.NotNull(f.Factory.Links.Last().Lighting);
        Assert.All(f.Demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task ManualDisconnectPreservesPcFanOutputAndDoesNotReconnectOnPoll()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Fan(55); var writes = f.Demo.Hubs[0].Writes;
        await f.Session.ConnectAsync(); Assert.True(f.Session.State.Link.Synchronized); await f.Session.DisconnectAsync();
        for (var i = 0; i < 3; i++) await f.Session.PollAsync();
        Assert.False(f.Session.State.Running); Assert.False(f.Session.State.Wanted); Assert.Single(f.Factory.Links); Assert.Equal(writes, f.Demo.Hubs[0].Writes);
        Assert.Equal(55, (await f.Demo.Hubs[0].ReadAsync()).Channels[0].Duty); Assert.Equal(0, f.Demo.Hubs[1].Writes);
    }
    [Fact] public async Task AutomaticStartupIsOneShotAndRespectsAnExplicitDisconnect()
    {
        await using var f = new Fixture(); await f.Initialize(autoStart: true); await f.Session.StartupAsync(); Assert.Single(f.Factory.Links);
        await f.Session.StartupAsync(); await f.Session.DisconnectAsync(); await f.Session.StartupAsync(); await f.Session.PollAsync();
        Assert.Single(f.Factory.Links); Assert.False(f.Session.State.Wanted);
    }
    [Fact] public async Task DisabledStartupDoesNotOpenPortsEvenWithSavedPairing()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Factory.Paired = true; await f.Session.StartupAsync(); await f.Session.PollAsync(); Assert.Empty(f.Factory.Links);
    }
    [Theory][InlineData("port")][InlineData("hub")][InlineData("layout")]
    public async Task ChangedSavedMappingDisconnectsAndRequiresAnExplicitNewConnect(string change)
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync(); var original = Assert.Single(f.Factory.Links);
        var value = f.Settings; value = change switch { "port" => value with { Connection = value.Connection with { Port = "COM15" } },
            "layout" => value with { Connection = value.Connection with { Layout = "B" } }, _ => value with { Selection = value.Selection.WithoutControl() } };
        await f.Save(value); await f.Session.PollAsync(); Assert.True(original.Closed); Assert.False(f.Session.State.Wanted); Assert.Single(f.Factory.Links);
        await f.Session.ConnectAsync(); Assert.Equal(2, f.Factory.Links.Count); Assert.True(f.Session.State.Running); Assert.All(f.Demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task ChangingAutoConnectPreferenceDoesNotInterruptCurrentLink()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync(); await f.Save(f.Settings with { ConnectOnStart = true }); await f.Session.PollAsync();
        Assert.Single(f.Factory.Links); Assert.False(f.Factory.Links[0].Closed); Assert.True(f.Session.State.Link.Connected);
    }
    [Fact] public async Task ReconnectedServiceReplacesTheM5SessionAndRejectsLateOldHellos()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync(); var old = f.Factory.Links[0]; var frames = old.Frames.Count;
        var previousSession = f.Client.Snapshot!.Session!; await f.Pipe.DisposeAsync(); await f.Demo.Runtime.DisconnectAsync(previousSession); await f.Demo.Runtime.SweepAsync();
        await f.Session.PollAsync(); Assert.True(old.Closed); Assert.False(f.Session.State.Running);
        await f.Client.ReconnectAsync(); await f.Session.PollAsync(); Assert.Equal(2, f.Factory.Links.Count); Assert.True(old.Closed);
        foreach (var route in new[] { "usb", "wifi" }) old.Router.Receive(route, JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = f.Factory.DeviceId, bootId = "late", firmwareVersion = "1.3.0" }));
        old.Router.Tick(); Assert.False(old.Router.Protocol.Connected); Assert.False(old.Router.Allows("usb")); Assert.False(old.Router.Allows("wifi"));
        Assert.Equal(frames, old.Frames.Count); Assert.True(f.Session.State.Link.Connected);
    }
    [Fact] public async Task ReleasedServiceCannotContinueAcceptingM5Commands()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync(); await f.Client.ReleaseAsync(); await f.Session.PollAsync();
        Assert.True(f.Factory.Links[0].Closed); Assert.False(f.Session.State.Running);
    }
    [Fact] public async Task PairingRequiresRegisteredUsbAndAnExistingLanThenSwitchesBothWays()
    {
        await using var f = new Fixture(); await f.Initialize(false); await f.Session.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(() => f.Session.PairAsync("demo", "test-password")); Assert.Equal(0, f.Factory.PairCalls);
        await f.Session.RegisterAsync(); f.Factory.Lans = []; await Assert.ThrowsAsync<IOException>(() => f.Session.PairAsync("demo", "test-password"));
        f.Factory.Lans = [new("demo-lan", "192.168.1.19", "255.255.255.0", "DEMO")]; await f.Session.PairAsync("demo", "test-password");
        Assert.Equal(1, f.Factory.PairCalls); Assert.True(f.Session.State.Link.WifiPaired); Assert.Equal("USB", f.Session.State.Link.Route);
        f.Factory.UsbPresent = false; await f.Session.PollAsync(); Assert.Equal("Wi-Fi", f.Session.State.Link.Route); Assert.True(f.Session.State.Link.Synchronized);
        f.Factory.UsbPresent = true; await f.Session.PollAsync(); Assert.Equal("USB", f.Session.State.Link.Route); Assert.True(f.Session.State.Link.Synchronized);
        Assert.All(f.Demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task DifferentUsbDeviceCannotReplaceSavedIdOrPairing()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Factory.DeviceId = "foreign-panel"; await f.Session.ConnectAsync();
        Assert.False(f.Session.State.Link.Connected); Assert.False(f.Session.State.CanRegister); Assert.False(f.Session.State.CanPair);
        await Assert.ThrowsAsync<IOException>(() => f.Session.PairAsync("demo", "test-password")); Assert.Equal("demo-panel", f.Settings.Connection.DeviceId);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DisconnectAndExitCancelPendingPairingWithoutASecondLink(bool exit)
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Factory.BeforePair = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var pair = f.Session.PairAsync("demo", "test-password"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (exit) await f.Session.DisposeAsync(); else await f.Session.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pair); Assert.Single(f.Factory.Links); Assert.True(f.Factory.Links[0].Closed);
        Assert.False(f.Factory.Paired); Assert.False(f.Session.State.Running); Assert.All(f.Demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task FailedStartClosesIngressAndResourcesBeforeRetry()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Factory.FailStart = true;
        await Assert.ThrowsAsync<IOException>(() => f.Session.ConnectAsync()); Assert.True(f.Factory.Links[0].Disposed); Assert.False(f.Session.State.Wanted);
        f.Factory.FailStart = false; await f.Session.ConnectAsync(); Assert.Equal(2, f.Factory.Links.Count); Assert.True(f.Session.State.Link.Connected);
    }
    [Fact] public async Task DisposedSessionIsTerminalAndCannotReopen()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync(); await f.Session.DisposeAsync(); await f.Session.DisposeAsync();
        await f.Session.PollAsync(); await f.Session.StartupAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Session.ConnectAsync());
        Assert.Single(f.Factory.Links); Assert.True(f.Factory.Links[0].Disposed);
    }
    [Fact] public async Task DisconnectCancelsAM5FanCommandWaitingBehindPcControl()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Session.ConnectAsync(); var link = f.Factory.Links[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Demo.Hubs[0].BeforeFanWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var s = f.Client.Snapshot!; var target = new EndpointReference(f.Demo.DeviceIds[0], "fan2"); var e = s.State.Endpoints.Single(e => e.Target == target);
        var pc = f.Client.ApplyCoolingAsync(new(new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation,
            new(target, e.Generation, e.PolicyRevision)), DeviceOperation.SetDuty, 56));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var state = link.Frames.Select(frame => JsonDocument.Parse(frame)).ToArray();
            try
            {
                var snapshot = state.Last(doc => doc.RootElement.GetProperty("type").GetString() == "state").RootElement;
                link.Router.Receive("usb", JsonSerializer.Serialize(new { v = 1, type = "command", sessionId = link.Router.Protocol.SessionId,
                    controllerEpoch = link.Control.Epoch, id = "queued-m5", commandSequence = 1,
                    expectedRevision = snapshot.GetProperty("controlRevision").GetInt64(), command = "SetManualDuty", payload = new { duty = 68 } }));
            }
            finally { foreach (var doc in state) doc.Dispose(); }
            Assert.True(link.Control.Snapshot.FanBusy); await f.Session.DisconnectAsync(); Assert.True(link.Closed);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(Outcome.Confirmed, (await pc).Outcome); f.Demo.Hubs[0].BeforeFanWrite = null;
        for (var i = 0; i < 100 && link.Control.Snapshot.FanBusy; i++) await Task.Delay(10);
        Assert.False(link.Control.Snapshot.FanBusy); Assert.Equal(1, f.Demo.Hubs[0].Writes);
        var reading = await f.Demo.Hubs[0].ReadAsync(); Assert.Equal(45, reading.Channels[0].Duty); Assert.Equal(56, reading.Channels[1].Duty);
    }
}
