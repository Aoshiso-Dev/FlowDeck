using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceConfigurationAuthorityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ConfirmedPolicyStoresTheRequestedValueAndSafetyThenRestoresAfterAServiceRestart(int requested)
    {
        await using var r = await Rig.Create(); var request = r.Duty(requested);
        var reply = await r.Startup.Runtime.ExecuteAsync(request);
        Assert.Equal(30, reply.Result!.Targets[0].ReportedDuty);
        Assert.Equal(DevicePolicySaveState.Saved, reply.Result.Targets[0].Persistence!.State);
        var saved = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Cooling.Endpoints[0];
        Assert.Equal(requested, saved.SavedPolicy!.Duty); Assert.True(saved.ResumeOnStart);
        Assert.True(DeviceConfigurationRules.SameSafety(saved.Safety, saved.SavedPolicy.SafetySnapshot!));
        var generation = r.Startup.Generation;
        await r.Startup.Runtime.ExecuteAsync(request); Assert.Equal(generation, r.Startup.Generation); Assert.Equal(1, r.Hub.Writes);
        await r.Startup.DisposeAsync(); var before = r.Hub.Writes;
        r.Driver = new(new(Installation, DemoHub.Identity.Id, r.Hub, r.Directory.Path));
        r.Startup = await DeviceRuntimeStartup.CreateAsync(r.Store, Installation, () => new([r.Driver], []), ["user"], new TestLog(), demo: true);
        Assert.Equal(before, r.Hub.Writes); r.Hello = r.Startup.Runtime.OpenSession("user");
        var restore = r.Duty(0) with { Operation = DeviceOperation.RestoreSavedCooling, Duty = null };
        var result = await r.Startup.Runtime.ExecuteAsync(restore);
        Assert.Equal(requested, result.Result!.Targets[0].RequestedDuty); Assert.Equal(30, result.Result.Targets[0].ReportedDuty);
    }
    [Fact]
    public async Task PreparationFailureRejectsTheOutputAndRetainsTheUnchangedPreviousPolicy()
    {
        await using var r = await Rig.Create(); var before = r.Startup.Generation;
        r.Store.Checkpoint = phase => { if (phase == "before-switch") throw new IOException("disk unavailable"); };
        var reply = await r.Startup.Runtime.ExecuteAsync(r.Duty(70));
        Assert.Equal(Outcome.Rejected, reply.Result!.Targets[0].Outcome); Assert.Equal(0, r.Hub.Writes);
        Assert.Equal(before, r.Startup.Generation); Assert.True(r.Startup.Configuration.Cooling.Endpoints[0].ResumeOnStart);
    }
    [Fact]
    public async Task CommitFailureReportsConfirmedHardwareSeparatelyAndDurablyDisablesOldResume()
    {
        await using var r = await Rig.Create(); var switches = 0;
        r.Store.Checkpoint = phase => { if (phase == "before-switch" && ++switches == 2) throw new IOException("commit failed"); };
        var reply = await r.Startup.Runtime.ExecuteAsync(r.Duty(70));
        Assert.Equal(Outcome.Partial, reply.Result!.Outcome); var target = Assert.Single(reply.Result.Targets);
        Assert.Equal(Outcome.Confirmed, target.Outcome); Assert.Equal(70, target.ReportedDuty);
        Assert.Equal(DevicePolicySaveState.Failed, target.Persistence!.State);
        var saved = DeviceConfigurationCodec.Read(r.Store.ReadActive()!.Files).Cooling.Endpoints[0];
        Assert.Equal(65, saved.SavedPolicy!.Duty); Assert.False(saved.ResumeOnStart);
        Assert.Equal(DevicePolicySaveState.Failed, r.Startup.Runtime.State().Endpoints.Single(e => e.Target == target.Target).Persistence!.State);
        await r.Startup.Runtime.DisconnectAsync(r.Hello.Session); r.Hello = r.Startup.Runtime.OpenSession("user");
        Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Duty(0) with { Operation = DeviceOperation.RestoreSavedCooling, Duty = null })).Success);
    }
    [Fact]
    public async Task APartiallyConfirmedGroupOnlyCommitsItsConfirmedEndpoints()
    {
        await using var r = await Rig.Create(fans: 2);
        r.Driver.MapActuation = (a, result) => a.EndpointId == "fan2" ? new(Outcome.Unknown, "lost acknowledgement") : result;
        var request = r.Duty(70); var state = r.Startup.Runtime.State();
        request = request with { Targets = state.Endpoints.Where(e => e.Target.EndpointId is "fan1" or "fan2")
            .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray() };
        var reply = await r.Startup.Runtime.ExecuteAsync(request); Assert.Equal(Outcome.Partial, reply.Result!.Outcome);
        var configs = r.Startup.Configuration.Cooling.Endpoints;
        Assert.Equal(70, configs[0].SavedPolicy!.Duty); Assert.True(configs[0].ResumeOnStart);
        Assert.Equal(65, configs[1].SavedPolicy!.Duty); Assert.False(configs[1].ResumeOnStart);
    }
    [Fact]
    public async Task ExplicitReleaseClearsSavedControlButNormalExitPreservesIt()
    {
        await using var r = await Rig.Create(); await r.Startup.Runtime.ExecuteAsync(r.Duty(77));
        await r.Startup.Runtime.DisconnectAsync(r.Hello.Session);
        Assert.Equal(77, r.Startup.Configuration.Cooling.Endpoints[0].SavedPolicy!.Duty);
        r.Hello = r.Startup.Runtime.OpenSession("user"); await r.Startup.Runtime.ExecuteAsync(r.Duty(78));
        var reply = await r.Startup.Runtime.ExecuteAsync(r.Duty(0) with { Operation = DeviceOperation.ReleaseCooling, Duty = null });
        Assert.Equal(DevicePolicySaveState.Saved, reply.Result!.Targets[0].Persistence!.State);
        Assert.Null(r.Startup.Configuration.Cooling.Endpoints[0].SavedPolicy); Assert.False(r.Startup.Configuration.Cooling.Endpoints[0].ResumeOnStart);
        Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Duty(0) with { Operation = DeviceOperation.RestoreSavedCooling, Duty = null })).Success);
    }
    [Fact]
    public async Task SlowDurablePreparationDoesNotHoldTheRuntimeLockOrHardwareResources()
    {
        await using var r = await Rig.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim(); var once = false;
        r.Store.Checkpoint = phase => { if (phase == "before-switch" && !once) { once = true; entered.TrySetResult(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var command = r.Startup.Runtime.ExecuteAsync(r.Duty(70));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await r.Renew().WaitAsync(TimeSpan.FromSeconds(1))).Success);
            await r.Startup.Runtime.PollAsync().WaitAsync(TimeSpan.FromSeconds(1)); Assert.Equal(0, r.Hub.Writes);
        }
        finally { proceed.Set(); }
        Assert.Equal(DevicePolicySaveState.Saved, (await command).Result!.Targets[0].Persistence!.State);
    }
    [Fact]
    public async Task ConfigurationApplyWaitsForPolicyCommitWithoutDeadlockAndRejectsItsStaleDraft()
    {
        await using var r = await Rig.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Driver.AfterActuation = _ => { entered.TrySetResult(); return proceed.Task; };
        var command = r.Startup.Runtime.ExecuteAsync(r.Duty(70));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var id = await r.Upload(r.Changed()); var apply = r.Send(r.Apply(id));
        try
        {
            await Task.Run(async () => { while (!r.Startup.Runtime.State().ConfigurationChanging) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await r.Renew()).Success);
        }
        finally { proceed.TrySetResult(); }
        var output = await command.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(DevicePolicySaveState.Saved, output.Result!.Targets[0].Persistence!.State);
        Assert.False((await apply.WaitAsync(TimeSpan.FromSeconds(3))).Success);
        Assert.Equal(70, r.Startup.Configuration.Cooling.Endpoints[0].SavedPolicy!.Duty);
        Assert.Equal(30, r.Startup.Configuration.Cooling.Endpoints[0].Safety.Minimum);
    }
    [Fact]
    public async Task AnOldSnapshotKeepsItsGenerationWhenAConfirmedPolicyAdvancesTheCurrentGeneration()
    {
        await using var r = await Rig.Create(); var snapshot = (await r.Send(new(DeviceConfigurationOperation.Snapshot))).Configuration!;
        await r.Startup.Runtime.ExecuteAsync(r.Duty(70));
        Assert.NotEqual(snapshot.Generation, r.Startup.Generation.Id);
        var part = (await r.Send(new(DeviceConfigurationOperation.ReadSnapshot, snapshot.TransferId, Offset: 0))).Configuration!;
        Assert.Equal(snapshot.Generation, part.Generation);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: snapshot.Generation, TotalBytes: 2, Sha256: new('a', 64)))).Success);
    }
    [Fact]
    public async Task AReplacedCommandCannotOverwriteTheNewerConfirmedPolicy()
    {
        await using var r = await Rig.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var first = true;
        r.Driver.AfterActuation = _ => { if (!first) return Task.CompletedTask; first = false; entered.TrySetResult(); return proceed.Task; };
        var old = r.Startup.Runtime.ExecuteAsync(r.Duty(70)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var newer = r.Startup.Runtime.ExecuteAsync(r.Duty(80)); proceed.SetResult();
        await Task.WhenAll(old, newer).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(80, r.Startup.Configuration.Cooling.Endpoints[0].SavedPolicy!.Duty);
        Assert.True(r.Startup.Configuration.Cooling.Endpoints[0].ResumeOnStart);
        Assert.Equal(DevicePolicySaveState.Saved, (await newer).Result!.Targets[0].Persistence!.State);
    }
}
