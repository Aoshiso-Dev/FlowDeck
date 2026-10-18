using System.Security.Principal;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockDeviceDriverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstLaunchUsesCurrentAsrockConnectionWithoutSavingApprovalOrClaimingPhysicalLedDetection(bool serial)
    {
        using var directory = new ServiceTestDirectory();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!.Value;
        var cell = new Cell(1, serial);
        var backend = new Backend(cell);
        var provider = new AsrockDeviceProvider(Installation, directory.Path, Boot, backend);
        await using var runtime = new DeviceControlRuntime([], [user], new TestLog(), demo: true, providers: [provider]);
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(Installation))));
        var authority = new DeviceConfigurationAuthority(store, Installation, store.ReadActive()!);
        runtime.InstallConfigurationHandler(authority);
        await runtime.PollAsync(); await runtime.PollAsync();
        var pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DeviceServicePipeServer(runtime, [user], new TestLog(), pipe).RunAsync(stop.Token, () => ready.TrySetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId));
        try
        {
            await client.ConnectAsync();
            var snapshot = client.Snapshot!;
            var before = SettingsTransfer.Hash(snapshot.Configuration.Configuration);
            var generation = snapshot.Configuration.Generation;
            Assert.NotEmpty(snapshot.Configuration.Setup!);
            Assert.All(snapshot.Configuration.Setup!, candidate => {
                Assert.Null(candidate.Options.DetectedLeds);
                Assert.Equal(cell.Device.Counts[candidate.Target.EndpointId[^1] - '0'], candidate.Options.MinimumLeds);
                Assert.Equal(candidate.Options.MinimumLeds, candidate.Options.MaximumLeds);
            });
            var first = await client.PrepareFirstUseAsync();
            Assert.Null(first.Deferred); Assert.Equal(snapshot.Configuration.Setup!.Length, first.PreparedLighting);
            Assert.Equal(before, SettingsTransfer.Hash(client.Snapshot!.Configuration.Configuration));
            Assert.Equal(generation, client.Snapshot.Configuration.Generation);
            Assert.Empty(cell.Device.Writes);
            Assert.All(client.Snapshot.State.Inventory.Devices.Single().Device.Endpoints, endpoint =>
                Assert.Contains(endpoint.Capabilities, capability => capability.Feature == DeviceFeature.NativeEffect && capability.Usable));
            Assert.Empty(client.Snapshot.Configuration.Configuration.Lighting.Endpoints ?? []);
            Assert.Equal(0, (await client.PrepareFirstUseAsync()).PreparedLighting);
            Assert.Empty(cell.Device.Writes);

            // A later GUI launch reads readiness again without creating setup records or owners.
            await client.DisposeAsync();
            await using var again = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId));
            await again.ConnectAsync();
            Assert.Equal(first.PreparedLighting, (await again.PrepareFirstUseAsync()).PreparedLighting);
            Assert.Equal(before, SettingsTransfer.Hash(again.Snapshot!.Configuration.Configuration));
            Assert.Equal(generation, again.Snapshot.Configuration.Generation);
            Assert.All(again.Snapshot.State.Endpoints, endpoint => Assert.False(endpoint.Owned));
            Assert.Empty(cell.Device.Writes);
        }
        finally
        {
            await client.DisposeAsync(); stop.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
        Assert.Empty(cell.Device.Writes);
    }
}
