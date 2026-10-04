using System.IO.Pipes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceServicePipeReleaseTests
{
    private sealed class Server : IAsyncDisposable
    {
        public readonly string Name = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        private readonly NamedPipeServerStream pipe;
        private readonly SemaphoreSlim writer = new(1, 1);
        private readonly Task serving;
        private readonly DeviceServiceHello hello = new(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 12, [], true);
        public readonly TaskCompletionSource<DeviceServiceRequest> Renewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<DeviceServiceRequest> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowRenewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReleaseSuccess = true;
        private int renewals, released;
        public int Renewals => Volatile.Read(ref renewals);
        public Server()
        {
            pipe = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serving = Run();
        }
        public DeviceServicePipeClient Client() => DeviceServicePipeClient.ForDemo(Name, Environment.ProcessId);
        private async Task Run()
        {
            var replies = new List<Task>();
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                await CoolingPipeFrames.WriteAsync(pipe, hello, stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    var request = DeviceServiceProtocol.Read(await CoolingPipeFrames.ReadAsync(pipe, stop.Token));
                    replies.Add(Reply(request));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { stop.Cancel(); await Task.WhenAll(replies); }
        }
        private async Task Reply(DeviceServiceRequest request)
        {
            try
            {
                bool success;
                if (request.Operation == DeviceOperation.Renew)
                {
                    Interlocked.Increment(ref renewals); Renewal.TrySetResult(request);
                    await AllowRenewal.Task.WaitAsync(stop.Token); success = Volatile.Read(ref released) == 0;
                }
                else
                {
                    Assert.Equal(DeviceOperation.ReleaseSession, request.Operation);
                    Interlocked.Exchange(ref released, 1); Release.TrySetResult(request);
                    await AllowRelease.Task.WaitAsync(stop.Token); success = ReleaseSuccess;
                }
                var reply = new DeviceServiceReply(2, hello.Epoch, hello.Session, request.Id, success, success ? null : "session ended / restore pending");
                await writer.WaitAsync(stop.Token);
                try { await CoolingPipeFrames.WriteAsync(pipe, reply, stop.Token); }
                finally { writer.Release(); }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        { stop.Cancel(); pipe.Dispose(); await serving; stop.Dispose(); writer.Dispose(); }
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ExitDrainsInflightRenewalAndKeepsReadingTheDelayedPhysicalRestoreReply(bool success)
    {
        await using var server = new Server { ReleaseSuccess = success }; await using var client = server.Client();
        await client.ConnectAsync(); var renewal = await server.Renewal.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var release = client.SendAsync(client.Request(DeviceOperation.ReleaseSession));
        // The first renewal is still awaiting its reply. Release must not consume the session yet.
        await Assert.ThrowsAsync<TimeoutException>(() => server.Release.Task.WaitAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(release.IsCompleted); server.AllowRenewal.SetResult();
        var request = await server.Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(request.Sequence > renewal.Sequence);
        // Cross another real renewal tick while the server is performing a slow/failed restore.
        // A post-release renewal would be rejected and prematurely cancel the original exit call.
        await Task.Delay(3300); Assert.Equal(1, server.Renewals); Assert.True(client.Connected); Assert.False(release.IsCompleted);
        server.AllowRelease.SetResult(); var reply = await release.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(success, reply.Success); Assert.True(client.Connected);
        if (!success) Assert.Contains("restore pending", reply.Error);
    }
    [Fact] public async Task CancelWhileDrainingRenewalDoesNotSendAnUnobservedRelease()
    {
        await using var server = new Server(); await using var client = server.Client(); await client.ConnectAsync();
        _ = await server.Renewal.Task.WaitAsync(TimeSpan.FromSeconds(8)); using var cancel = new CancellationTokenSource();
        var release = client.SendAsync(client.Request(DeviceOperation.ReleaseSession), cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release); Assert.False(client.Connected); Assert.False(server.Release.Task.IsCompleted);
    }
    [Fact] public async Task AConnectionWithoutAutomaticRenewalCanReleaseImmediately()
    {
        await using var server = new Server(); await using var client = server.Client(); await client.ConnectAsync(renew: false);
        server.AllowRelease.SetResult(); Assert.True((await client.SendAsync(client.Request(DeviceOperation.ReleaseSession))).Success);
        Assert.Equal(0, server.Renewals); Assert.True(client.Connected);
    }
}
