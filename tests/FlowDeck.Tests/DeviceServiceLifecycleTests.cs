using FlowDeck.Core;
using FlowDeck.Infrastructure;
using System.Security.Principal;

namespace FlowDeck.Tests;

public class DeviceServiceLifecycleTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly DemoHub Hub = new() { DelayMs = 0 };
        public readonly string Pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        public DeviceServiceLifecycle? Host;
        public int Starts;
        public bool FailNextStart;
        public volatile bool AllowsClients = true, FailGate;
        public async Task Start()
        {
            using var user = WindowsIdentity.GetCurrent(); var sid = user.User!.Value;
            var store = DeviceConfigurationCodec.Store(Path.Combine(Directory.Path, "configuration"));
            store.Activate(store.Stage(DeviceConfigurationCodec.Write(await DeviceRuntimeStartupTests.Package(Hub, Directory.Path))));
            Host = await DeviceServiceLifecycle.StartAsync(async () =>
            {
                Starts++;
                if (FailNextStart) { FailNextStart = false; throw new IOException("simulated startup failure"); }
                var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation,
                    () => new([new HubDeviceDriver(Installation, DemoHub.Identity.Id, Hub, Directory.Path)], []),
                    [sid], new TestLog(), demo: true);
                return await DeviceServiceHost.StartAsync(startup, [sid], new TestLog(), Pipe,
                    allowsClients: () => FailGate ? throw new IOException("invalid deployment record") : AllowsClients);
            }, new TestLog());
        }
        public DeviceWorkspaceClient Client() => new(DeviceServicePipeClient.ForDemo(Pipe, Environment.ProcessId));
        public async ValueTask DisposeAsync()
        { try { if (Host is not null) await Host.DisposeAsync(); } finally { Directory.Dispose(); } }
    }
    private static async Task<DeviceWorkspaceClient> Control(Fixture f)
    {
        var client = f.Client(); await client.ConnectAsync(); var s = client.Snapshot!; var setting = s.Configuration.Configuration.Cooling.Endpoints[0];
        var draft = new DeviceCoolingDraft(setting.Target); draft.Update(s.State, s.Configuration.Generation, setting, s.ElapsedSeconds, s.Connected);
        draft.SetDuty(63); Assert.Equal(Outcome.Confirmed, (await client.ApplyCoolingAsync(draft.Capture())).Outcome); return client;
    }
    [Fact] public async Task SuspendReleasesOutputAndResumeReopensWithANewEpochWithoutReplayingSavedPolicy()
    {
        await using var f = new Fixture(); await f.Start(); await using var first = await Control(f); var before = first.Snapshot!;
        await f.Host!.PowerAsync(false);
        Assert.Null(f.Host.Runtime); Assert.False(f.Host.Completion.IsCompleted);
        Assert.Equal(50, (await f.Hub.ReadAsync()).Channels[0].Duty);
        Assert.Empty(new HubEndpointRecoveryJournal(f.Directory.Path, before.Configuration.Configuration.Cooling.Endpoints[0].Target.DeviceId).Pending);
        await f.Host.PowerAsync(true); var writes = f.Hub.Writes;
        await using var second = f.Client(); await second.ConnectAsync(); var after = second.Snapshot!;
        Assert.NotEqual(before.State.Inventory.Epoch, after.State.Inventory.Epoch); Assert.Equal(2, f.Starts);
        Assert.All(after.State.Endpoints, e => Assert.False(e.Owned)); Assert.Equal(63, after.Configuration.Configuration.Cooling.Endpoints[0].SavedPolicy!.Duty);
        Assert.Equal(writes, f.Hub.Writes); Assert.Equal(50, (await f.Hub.ReadAsync()).Channels[0].Duty);
        Assert.True((await second.ReleaseAsync()).Success);
    }
    [Fact] public async Task DuplicatePowerEventsDoNotCreateTwoOwnersOrRestartAnAlreadyRunningHost()
    {
        await using var f = new Fixture(); await f.Start(); var epoch = f.Host!.Runtime!.Epoch;
        await Task.WhenAll(f.Host.PowerAsync(true), f.Host.PowerAsync(true)); Assert.Equal(1, f.Starts); Assert.Equal(epoch, f.Host.Runtime!.Epoch);
        await Task.WhenAll(f.Host.PowerAsync(false), f.Host.PowerAsync(false)); Assert.Null(f.Host.Runtime);
        await Task.WhenAll(f.Host.PowerAsync(true), f.Host.PowerAsync(true)); Assert.Equal(2, f.Starts); Assert.NotEqual(epoch, f.Host.Runtime!.Epoch);
        await f.Host.DisposeAsync(); await f.Host.PowerAsync(true); Assert.Equal(2, f.Starts); Assert.True(f.Host.Completion.IsCompletedSuccessfully);
    }
    [Fact] public async Task WorkspaceReconnectsAfterResumeButKeepsOldEditsInvalidAndDoesNotReplayOutput()
    {
        await using var f = new Fixture(); await f.Start(); await using var client = await Control(f);
        var before = client.Snapshot!; var setup = before.Configuration.Configuration.Cooling.Endpoints[0];
        var draft = new DeviceCoolingDraft(setup.Target); draft.Update(before.State, before.Configuration.Generation, setup, before.ElapsedSeconds, true);
        draft.SetDuty(67); var old = draft.Capture(); await f.Host!.PowerAsync(false);
        await Assert.ThrowsAnyAsync<Exception>(() => client.RefreshAsync()); Assert.True(client.NeedsReconnect);
        await f.Host.PowerAsync(true); var writes = f.Hub.Writes; await client.ReconnectAsync(); var after = client.Snapshot!;
        Assert.True(after.Connected); Assert.NotEqual(before.State.Inventory.Epoch, after.State.Inventory.Epoch);
        Assert.Equal(writes, f.Hub.Writes); Assert.Equal(50, (await f.Hub.ReadAsync()).Channels[0].Duty);
        draft.Update(after.State, after.Configuration.Generation, after.Configuration.Configuration.Cooling.Endpoints[0], after.ElapsedSeconds, true);
        Assert.True(draft.Conflict); Assert.Equal(67, draft.Duty); Assert.False(draft.CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyCoolingAsync(old));
        Assert.Equal(writes, f.Hub.Writes); Assert.True((await client.ReleaseAsync()).Success);
        await Assert.ThrowsAsync<IOException>(() => client.ReconnectAsync());
    }
    [Fact] public async Task FailedResumeKeepsTheServiceSuspendedAndCanBeExplicitlyRetried()
    {
        await using var f = new Fixture(); await f.Start(); await f.Host!.PowerAsync(false); f.FailNextStart = true;
        await Assert.ThrowsAsync<IOException>(() => f.Host.PowerAsync(true)); Assert.Null(f.Host.Runtime); Assert.False(f.Host.Completion.IsCompleted);
        await f.Host.PowerAsync(true); Assert.Equal(3, f.Starts); Assert.NotNull(f.Host.Runtime);
        await using var client = f.Client(); await client.ConnectAsync(); Assert.True((await client.ReleaseAsync()).Success);
    }
    [Fact] public async Task DeploymentReadinessDoesNotAllowAClientUntilActivation()
    {
        await using var f = new Fixture { AllowsClients = false }; await f.Start();
        await using var client = f.Client(); var connection = client.ConnectAsync();
        await Task.Delay(100); Assert.False(connection.IsCompleted); Assert.Equal(0, f.Hub.Writes);
        f.AllowsClients = true; await connection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(client.Snapshot); Assert.True((await client.ReleaseAsync()).Success);
    }
    [Fact] public async Task StoppingDuringDeploymentWaitClosesTheServiceWithoutGrantingControl()
    {
        await using var f = new Fixture { AllowsClients = false }; await f.Start();
        await f.Host!.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Host.Completion.IsCompletedSuccessfully); Assert.Null(f.Host.Runtime); Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task InvalidDeploymentGateFaultsTheServiceLifetimeInsteadOfFallingBackToLegacy()
    {
        await using var f = new Fixture { AllowsClients = false }; await f.Start(); f.FailGate = true;
        await Assert.ThrowsAsync<IOException>(() => f.Host!.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<AggregateException>(() => f.Host!.DisposeAsync().AsTask()); Assert.Null(f.Host!.Runtime); Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task UnconfirmedSuspendLeavesTheJournalForRecoveryBeforeResuming()
    {
        await using var f = new Fixture(); await f.Start(); await using var client = await Control(f); var target = client.Snapshot!.Configuration.Configuration.Cooling.Endpoints[0].Target;
        f.Hub.Disconnected = true;
        await Assert.ThrowsAsync<AggregateException>(() => f.Host!.PowerAsync(false)); Assert.Null(f.Host!.Runtime);
        Assert.NotEmpty(new HubEndpointRecoveryJournal(f.Directory.Path, target.DeviceId).Pending);
        f.Hub.Disconnected = false; await f.Host.PowerAsync(true);
        Assert.Empty(new HubEndpointRecoveryJournal(f.Directory.Path, target.DeviceId).Pending); Assert.Equal(50, (await f.Hub.ReadAsync()).Channels[0].Duty);
        Assert.Null(f.Host.Runtime!.State().LifecycleError);
    }
}
