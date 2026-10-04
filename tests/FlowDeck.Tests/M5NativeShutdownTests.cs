using System.Reflection;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

/// <summary>Inject only completed/pending worker tasks. No native Start, COM, sockets, or hardware calls.</summary>
public class M5NativeShutdownTests
{
    private sealed class InertBackend(IM5ControlBackend source) : IM5ControlBackend
    {
        public string Epoch => source.Epoch;
        public AppSettings Settings => source.Settings;
        public ControlSnapshot Snapshot => source.Snapshot with { Demo = false };
        public string? ProfileReadiness(string id) => "inert";
        public Task<OperationResult> RequestAsync(ControlRequest request) => throw new InvalidOperationException("No control is allowed in this fixture");
    }
    private static void Set(object owner, string field, object value) =>
        owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static T Get<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static M5WifiServer Wifi(M5TransportRouter router) =>
        new(router, new("server", "panel", "token", "unused"), new("test", "127.0.0.1", "255.0.0.0", "test"), 0, new TestLog());

    [Fact] public async Task WifiListenerFailureStillWaitsForDiscoveryAndCanFinishClosingOnRetry()
    {
        await using var f = new Fixture();
        var router = new M5TransportRouter(f.Core, new()); var wifi = Wifi(router);
        var discovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(wifi, "listenerTask", Task.FromException(new IOException("listener failed")));
        Set(wifi, "discoveryTask", discovery.Task);
        var close = wifi.DisposeAsync().AsTask();
        try
        {
            Assert.False(close.IsCompleted); Assert.True(Get<CancellationTokenSource>(wifi, "stop").IsCancellationRequested);
            Assert.Same(close, wifi.DisposeAsync().AsTask());
        }
        finally { discovery.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => close);
        await wifi.DisposeAsync(); await wifi.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => wifi.Start(false));
    }

    [Fact] public async Task SerialWorkerFaultDetachesTheWriterAndDoesNotKeepReplayingTheFault()
    {
        await using var f = new Fixture();
        var serial = new M5SerialSession(new InertBackend(f.Core), new(Port: "COM14"), new TestLog());
        Set(serial, "loop", Task.FromException(new IOException("USB worker failed")));
        await Assert.ThrowsAsync<IOException>(() => serial.DisposeAsync().AsTask());
        await serial.DisposeAsync(); await serial.DisposeAsync();
        var router = Get<M5TransportRouter>(serial, "router");
        Assert.False(router.Allows("usb")); Assert.False(router.Allows("wifi"));
        var outgoing = typeof(M5TransportRouter).GetField("Outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(router);
        Assert.Null(outgoing);
        Assert.True(Get<M5Shutdown>(serial, "shutdown").Requested);
    }

    [Fact] public async Task ConnectionCollectsIndependentFailuresThenFinishesWithoutReopeningAnyTransport()
    {
        await using var f = new Fixture(); using var directory = new ServiceTestDirectory();
        var connection = new M5Connection(new InertBackend(f.Core), new(Port: "COM14", Transport: "usb"), directory.Path, new TestLog());
        var wifi = Wifi(connection.Router);
        Set(connection, "<Wifi>k__BackingField", wifi);
        Set(connection, "loop", Task.FromException(new IOException("connection worker failed")));
        Set(connection.Serial!, "loop", Task.FromException(new IOException("serial worker failed")));
        Set(wifi, "listenerTask", Task.FromException(new IOException("wifi worker failed")));
        var error = await Assert.ThrowsAsync<AggregateException>(() => connection.DisposeAsync().AsTask());
        Assert.Equal(3, error.Flatten().InnerExceptions.Count);
        Assert.False(connection.Router.Allows("usb")); Assert.False(connection.Router.Allows("wifi"));
        await connection.DisposeAsync(); await connection.DisposeAsync();
        Assert.True(Get<bool>(connection, "resourcesClosed"));
        Assert.True(Get<bool>(connection.Serial!, "resourcesClosed")); Assert.True(Get<bool>(wifi, "resourcesClosed"));
        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Equal(0, f.Hub.Writes);
    }

    [Fact] public async Task ConnectionWaitsForBothTransportWorkersWhenOneFails()
    {
        await using var f = new Fixture(); using var directory = new ServiceTestDirectory();
        var connection = new M5Connection(new InertBackend(f.Core), new(Port: "COM14", Transport: "usb"), directory.Path, new TestLog());
        var wifi = Wifi(connection.Router); Set(connection, "<Wifi>k__BackingField", wifi);
        Set(connection.Serial!, "loop", Task.FromException(new IOException("serial worker failed")));
        var listener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(wifi, "listenerTask", listener.Task);
        var close = connection.DisposeAsync().AsTask();
        try { Assert.False(close.IsCompleted); Assert.Same(close, connection.DisposeAsync().AsTask()); }
        finally { listener.TrySetResult(); }
        await Assert.ThrowsAsync<AggregateException>(() => close);
        await connection.DisposeAsync(); Assert.True(Get<bool>(connection, "resourcesClosed"));
        Assert.Equal(0, f.Hub.Writes);
    }
}
