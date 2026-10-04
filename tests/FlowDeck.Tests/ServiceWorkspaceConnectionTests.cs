using System.IO.Pipes;
using System.Security.Principal;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServiceWorkspaceConnectionTests
{
    private static string Sid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
    [Fact] public async Task DeviceServiceIsSelectedWithoutConstructingTheLegacyConnection()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var selected = await ServiceWorkspaceConnection.ConnectAsync(() => DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId),
            () => throw new InvalidOperationException("legacy must not be constructed"));
        Assert.True(selected.UsesDevices); await using var client = selected.TakeDevices(); await selected.DisposeAsync();
        Assert.True(client.Snapshot!.Connected); Assert.True(client.IsDemo); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        Assert.True((await client.ReleaseAsync()).Success);
    }
    [Fact] public async Task MissingV2PipeRequiresAuthenticatedLegacyHelloAndTransfersTheSameSession()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var pipe = "FlowDeck.Cooling.Test." + Guid.NewGuid().ToString("N");
        var server = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), pipe).RunAsync(stop.Token);
        try
        {
            await using var selected = await ServiceWorkspaceConnection.ConnectAsync(
                () => DeviceServicePipeClient.ForDemo("FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), Environment.ProcessId),
                () => CoolingServicePipeClient.ForDemo(pipe, Environment.ProcessId));
            Assert.False(selected.UsesDevices); var client = selected.TakeLegacy(); var session = client.Hello!.Session;
            await using var remote = new RemoteControlCoordinator(DemoHub.CreateSettings(), () => client, new TestLog(), demo: true);
            await selected.DisposeAsync(); await remote.ConnectAsync();
            Assert.True(remote.Connected); Assert.Equal(session, client.Hello!.Session); Assert.Equal(0, f.Hub.Writes);
        }
        finally { stop.Cancel(); await server; }
    }
    [Fact] public async Task AnAuthenticatedButIncompatibleV2ServiceCannotFallBackToLegacyHardware()
    {
        var pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
        using var server = NamedPipeServerStreamAcl.Create(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, CoolingServicePipeServer.Security([Sid]));
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(stop.Token);
            await CoolingPipeFrames.WriteAsync(server, new DeviceServiceHello(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 12, [], true), stop.Token);
        });
        try
        {
            var legacyCalled = false;
            await Assert.ThrowsAsync<IOException>(() => ServiceWorkspaceConnection.ConnectAsync(
                () => DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId), () => { legacyCalled = true; throw new Exception("legacy"); }));
            Assert.False(legacyCalled); await serving;
        }
        finally { stop.Cancel(); }
    }
    [Fact] public async Task WrongV2ServerPidIsAnAuthenticationFailureNotALegacySelection()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path); var legacyCalled = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ServiceWorkspaceConnection.ConnectAsync(
            () => DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId + 10000), () => { legacyCalled = true; throw new Exception("legacy"); }));
        Assert.False(legacyCalled); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task NoAuthenticatedServiceLeavesNoUsableConnectionAndDoesNotInventADirectMode()
    {
        await Assert.ThrowsAsync<IOException>(() => ServiceWorkspaceConnection.ConnectAsync(() => new(() => 0), () => new("unused", () => 0)));
    }
}
