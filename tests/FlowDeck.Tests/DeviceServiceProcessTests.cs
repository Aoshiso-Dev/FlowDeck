using FlowDeck.Core;
using FlowDeck.Infrastructure;
using System.Text.Json;
using Child = FlowDeck.Tests.ServiceProcessTests.Child;

namespace FlowDeck.Tests;

public class DeviceServiceProcessTests
{
    private static string Pipe() => "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
    private static Task<Child> Host(string pipe, string directory) => Child.Start("--devices-demo-host", "--pipe", pipe, "--data-dir", directory);
    private static async Task<DeviceWorkspaceClient> Connect(Child host, string pipe)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(pipe, host.Process.Id));
            try { await client.ConnectAsync(); return client; }
            catch when (attempt < 5) { await client.DisposeAsync(); await Task.Delay(50); }
            catch { await client.DisposeAsync(); throw; }
        }
    }
    private static async Task<EndpointReference> SetFan(DeviceWorkspaceClient client)
    {
        var s = client.Snapshot!; var config = s.Configuration.Configuration.Cooling.Endpoints.First(); var draft = new DeviceCoolingDraft(config.Target);
        draft.Update(s.State, s.Configuration.Generation, config, s.ElapsedSeconds, s.Connected); draft.SetDuty(63);
        Assert.Equal(Outcome.Confirmed, (await client.ApplyCoolingAsync(draft.Capture())).Outcome); return config.Target;
    }
    [Fact] public async Task ServiceRestartKeepsDeviceIdentitiesPreferencesAndPoliciesWithoutStartingOutputs()
    {
        using var directory = new ServiceTestDirectory(); var pipe = Pipe(); EndpointReference target; string epoch;
        await using (var host = await Host(pipe, directory.Path))
        {
            Assert.NotEqual(Environment.ProcessId, host.Process.Id); Assert.True(host.Ready.GetProperty("demo").GetBoolean());
            await using var client = await Connect(host, pipe); target = await SetFan(client); var s = client.Snapshot!; epoch = s.State.Inventory.Epoch;
            var next = s.Configuration.Configuration.Dashboard with { Items = s.Configuration.Configuration.Dashboard.Items.Select(i =>
                i.Target == target ? i with { Name = "再起動後も同じ端子", Role = "pump", Indicator = "circle", DisplayMaximum = 4500 } : i).ToArray() };
            await client.SaveDashboardAsync(new(epoch, s.Configuration.Generation, next));
            Assert.True((await client.ReleaseAsync()).Success); await client.DisposeAsync(); await host.Stop();
        }
        await using var restarted = await Host(pipe, directory.Path); await using var second = await Connect(restarted, pipe); var loaded = second.Snapshot!;
        Assert.NotEqual(epoch, loaded.State.Inventory.Epoch);
        Assert.Contains(loaded.Configuration.Configuration.Dashboard.Items, i => i.Target == target && i.Name == "再起動後も同じ端子" && i.Role == "pump" && i.DisplayMaximum == 4500);
        Assert.Contains(loaded.Configuration.Configuration.Cooling.Endpoints, e => e.Target == target && e.SavedPolicy?.Duty == 63);
        Assert.All(loaded.State.Endpoints, e => Assert.False(e.Owned));
        Assert.All(loaded.State.Telemetry!, t => Assert.False(t.Stale));
        Assert.True((await second.ReleaseAsync()).Success); await second.DisposeAsync(); await restarted.Stop();
    }
    [Fact] public async Task KillingAClientProcessReleasesItsCoolingBeforeTheNextOwnerConnects()
    {
        using var directory = new ServiceTestDirectory(); var pipe = Pipe(); await using var host = await Host(pipe, directory.Path);
        await using var owner = await Child.Start("--devices-demo-client", "--pipe", pipe, "--server-pid", host.Process.Id.ToString());
        var target = owner.Ready.GetProperty("target").Deserialize<EndpointReference>(Contract.Json)!;
        await owner.Kill(); await using var observer = await Connect(host, pipe); var s = observer.Snapshot!;
        Assert.False(s.State.Endpoints.Single(e => e.Target == target).Owned);
        Assert.Equal(70, s.State.Telemetry!.Single(t => t.Target == target && t.Feature == DeviceFeature.DutyRead).Value);
        Assert.Empty(new HubEndpointRecoveryJournal(directory.Path, target.DeviceId).Pending);
        Assert.True((await observer.ReleaseAsync()).Success); await observer.DisposeAsync(); await host.Stop();
    }
    [Fact] public async Task KilledServiceRecoversDurableCoolingObligationsBeforeAcceptingNewClient()
    {
        using var directory = new ServiceTestDirectory(); var pipe = Pipe(); EndpointReference target; string epoch;
        await using (var host = await Host(pipe, directory.Path))
        {
            await using var client = await Connect(host, pipe); target = await SetFan(client); epoch = client.Snapshot!.State.Inventory.Epoch;
            Assert.NotEmpty(new HubEndpointRecoveryJournal(directory.Path, target.DeviceId).Pending); await host.Kill();
        }
        await using var restarted = await Host(pipe, directory.Path); await using var next = await Connect(restarted, pipe); var state = next.Snapshot!.State;
        Assert.NotEqual(epoch, state.Inventory.Epoch); Assert.Null(state.LifecycleError); Assert.Empty(new HubEndpointRecoveryJournal(directory.Path, target.DeviceId).Pending);
        Assert.Equal(70, state.Telemetry!.Single(t => t.Target == target && t.Feature == DeviceFeature.DutyRead).Value);
        Assert.All(state.Endpoints, e => Assert.False(e.Owned));
        Assert.True((await next.ReleaseAsync()).Success); await next.DisposeAsync(); await restarted.Stop();
    }
    [Fact] public async Task AnotherDemoServiceCannotOpenTheSameConfigurationAndCorruptionNeverResetsIt()
    {
        using var directory = new ServiceTestDirectory(); var pipe = Pipe();
        await using (var host = await Host(pipe, directory.Path))
        {
            await Assert.ThrowsAsync<IOException>(() => Host(Pipe(), directory.Path)); await host.Stop();
        }
        var pointer = Path.Combine(directory.Path, "configuration", "current.json"); File.WriteAllText(pointer, "{broken");
        await Assert.ThrowsAsync<IOException>(() => Host(pipe, directory.Path)); Assert.Equal("{broken", File.ReadAllText(pointer));
    }
    [Fact] public async Task PersistentDemoDoesNotAdoptAnExistingUnmarkedDirectory()
    {
        using var directory = new ServiceTestDirectory(); var path = Path.Combine(directory.Path, "settings.json"); File.WriteAllText(path, "preserve");
        await Assert.ThrowsAsync<IOException>(() => DemoDeviceWorkspace.CreateAsync(directory.Path, persistent: true));
        Assert.Equal("preserve", File.ReadAllText(path)); Assert.False(File.Exists(Path.Combine(directory.Path, "demo-host.json")));
    }
    [Fact] public async Task ExternalClientRejectsAProcessOtherThanTheSpecifiedServer()
    {
        using var directory = new ServiceTestDirectory(); var pipe = Pipe(); await using var host = await Host(pipe, directory.Path);
        await using var client = DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectAsync()); await host.Stop();
    }
    [Fact] public void ExternalGuiArgumentsRequireDemoBothConnectionFieldsAndNoHardwareFlags()
    {
        var prefix = new[] { "--devices-preview", "--demo", "--data-dir", "demo-data" }; var pipe = Pipe();
        var parsed = DeviceWorkspaceOptions.Parse([.. prefix, "--devices-pipe", pipe, "--server-pid", "42"]);
        Assert.True(parsed.External); Assert.Equal(42, parsed.ServerProcessId); Assert.Equal(pipe, parsed.PipeName);
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--devices-pipe", pipe]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--server-pid", "42"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--devices-pipe", DeviceServiceProtocol.PipeName, "--server-pid", "42"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--asrock-rgb-usb"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--demo"]));
    }
}
