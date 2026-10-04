using System.IO.Pipes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class LightingSetupCompatibilityTests
{
    [Fact]
    public async Task DeviceWideSetupServiceIsRejectedBeforeAnyUpload()
    {
        var name = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var requests = 0;
        var serve = Task.Run(async () => {
            try
            {
                await server.WaitForConnectionAsync(stop.Token);
                await CoolingPipeFrames.WriteAsync(server, new DeviceServiceHello(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 12,
                    ["configuration-transfer-v2", "device-lighting-setup-v2"], true), stop.Token);
                await CoolingPipeFrames.ReadAsync(server, stop.Token); Interlocked.Increment(ref requests);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        });
        await using var client = DeviceServicePipeClient.ForDemo(name, Environment.ProcessId);
        try
        {
            await client.ConnectAsync(renew: false);
            var error = await Assert.ThrowsAsync<IOException>(() => client.PreviewConfigurationAsync("unused", null!, lightingSetupTarget: new("hub", "led1")));
            Assert.Contains("サービスの更新", error.Message);
        }
        finally { stop.Cancel(); await serve; }
        Assert.Equal(0, requests);
    }
}
