using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class InfrastructureTests
{
    private static string Root
    {
        get { var path=new DirectoryInfo(AppContext.BaseDirectory);while(path is not null && !File.Exists(Path.Combine(path.FullName,"FlowDeck.sln")))path=path.Parent;return path?.FullName??throw new DirectoryNotFoundException(); }
    }
    private static HubWorkerClient Worker(bool endpoints = false)=>new(Path.Combine(Root,".tools","python313","python.exe"),Path.Combine(Root,"tests","worker", endpoints ? "fake_endpoint_worker.py" : "fake_worker.py"),new TestLog(),()=>false);
    [Theory][InlineData(0,false)][InlineData(-1,false)][InlineData(50,true)][InlineData(149,true)][InlineData(150,false)]
    public void UnavailableZeroTemperatureIsNotUsedAsCoolingInput(double raw, bool expected) => Assert.Equal(expected, HardwareTemperatures.IsUsable(raw));
    [Fact] public async Task CompetingControllerBlocksInitialization()
    { await using var worker=new HubWorkerClient("missing-python","missing-worker",new TestLog(),()=>true); await Assert.ThrowsAsync<InvalidOperationException>(()=>worker.OpenAsync(DemoHub.Identity,1)); }
    [Fact] public async Task WorkerHandshakeProbeAndGeneration()
    { await using var worker=Worker();var candidates=await worker.ProbeAsync();Assert.Single(candidates);await worker.OpenAsync(candidates[0],1);Assert.Empty((await worker.ReadAsync()).Channels); }
    [Theory][InlineData(72)][InlineData(73)] public async Task ExitOrStaleResponseIsUnknown(int duty)
    { await using var worker=Worker();var candidates=await worker.ProbeAsync();await worker.OpenAsync(candidates[0],1);await Assert.ThrowsAsync<IOException>(()=>worker.SetFanGroupAsync(["fan1"],duty)); }
    [Fact] public async Task TimeoutTerminatesOwnedWorkerAndRestartsForProbe()
    { await using var worker=Worker();var candidates=await worker.ProbeAsync();await worker.OpenAsync(candidates[0],1);var before=DateTime.UtcNow;await Assert.ThrowsAsync<IOException>(()=>worker.SetFanGroupAsync(["fan1"],71));Assert.True(DateTime.UtcNow-before<TimeSpan.FromSeconds(8));Assert.Single(await worker.ProbeAsync()); }
    [Fact] public async Task StderrFloodAndOldWorkerResponseDoNotBlock()
    { await using var worker=Worker();var candidates=await worker.ProbeAsync();await worker.OpenAsync(candidates[0],1);var result=await worker.SetFanGroupAsync(["fan1"],74);Assert.Equal(Outcome.Confirmed,result.Outcome); }
    [Fact] public async Task LegacyWorkerWithoutEndpointCapabilityCannotReceiveIndividualFanCommands()
    {
        await using var worker = Worker(); var candidate = Assert.Single(await worker.ProbeAsync()); await worker.OpenAsync(candidate, 1);
        Assert.False(worker.SupportsFanEndpoints);
        await Assert.ThrowsAsync<NotSupportedException>(() => worker.SetFanEndpointAsync("fan4", 63, new("fake", "fan4:PWM|led1:RGB", 45)));
        Assert.Single(await worker.ProbeAsync());
    }
    [Fact] public async Task EndpointWorkerNegotiatesCapabilityAndReceivesOnlyTypedExpectedValues()
    {
        await using var worker = Worker(endpoints: true); Assert.False(worker.SupportsFanEndpoints);
        var candidate = Assert.Single(await worker.ProbeAsync()); Assert.True(worker.SupportsFanEndpoints); await worker.OpenAsync(candidate, 1);
        var result = await worker.SetFanEndpointAsync("fan4", 63, new("fake", "fan4:PWM|led1:RGB", 45));
        Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal("fan4", Assert.Single(result.Channels!).Id);
        Assert.Equal(63, Assert.Single(result.Status!.Channels).Duty);
        await worker.DisposeAsync(); Assert.False(worker.SupportsFanEndpoints);
    }
    [Fact] public async Task EndpointOnlyWorkerCannotReceiveADeadlineItDidNotNegotiate()
    {
        await using var worker = Worker(endpoints: true); var candidate = Assert.Single(await worker.ProbeAsync()); await worker.OpenAsync(candidate, 1);
        Assert.False(worker.SupportsFanDeadlines);
        await Assert.ThrowsAsync<NotSupportedException>(() => worker.SetFanEndpointAsync("fan4", 63,
            new("fake", "fan4:PWM|led1:RGB", 45, HardwareWriteDeadline.Now + 1000)));
        Assert.Equal(Outcome.Confirmed, (await worker.SetFanEndpointAsync("fan4", 63, new("fake", "fan4:PWM|led1:RGB", 45))).Outcome);
    }
    [Fact] public async Task NativePythonAndCSharpUseTheSameDeadlineClock()
    {
        await using var worker = new HubWorkerClient(Path.Combine(Root, ".tools", "python313", "python.exe"),
            Path.Combine(Root, "tests", "worker", "fake_deadline_worker.py"), new TestLog(), () => false);
        var candidate = Assert.Single(await worker.ProbeAsync()); await worker.OpenAsync(candidate, 1); Assert.True(worker.SupportsFanDeadlines);
        var expected = new HubEndpointExpectation("fake", "fan4:PWM|led1:RGB", 45, HardwareWriteDeadline.Now + 2000);
        Assert.Equal(Outcome.Confirmed, (await worker.SetFanEndpointAsync("fan4", 63, expected)).Outcome);
        var expired = await worker.SetFanEndpointAsync("fan4", 63, expected with { ValidUntilTick64 = HardwareWriteDeadline.Now - 1 });
        Assert.Equal(Outcome.Rejected, expired.Outcome); Assert.Equal(HardwareWriteDeadline.ExpiredError, expired.Error);
        await worker.DisposeAsync(); Assert.False(worker.SupportsFanDeadlines);
    }
    [Theory] [InlineData("sync", 50, 45)] [InlineData("fan1", 101, 45)] [InlineData("fan1", 50, -1)]
    public async Task InvalidEndpointRequestIsRejectedBeforeStartingWorker(string channel, int duty, int expected)
    {
        await using var worker = new HubWorkerClient("missing-python", "missing-worker", new TestLog(), () => false);
        await Assert.ThrowsAsync<ArgumentException>(() => worker.SetFanEndpointAsync(channel, duty, new("fake", "wire", expected)));
    }
    [Fact] public async Task DemoEndpointPreservesOtherFansAndRejectsStaleReadback()
    {
        await using var hub = new DemoHub { DelayMs = 0 }; var before = await hub.ReadAsync();
        var expected = new HubEndpointExpectation(DemoHub.Identity.Id, ControlCoordinator.WiringSignature(before), 45);
        Assert.Equal(Outcome.Confirmed, (await hub.SetFanEndpointAsync("fan2", 68, expected)).Outcome);
        Assert.Equal(new int?[] { 45, 68, 45, 45, 45 }, (await hub.ReadAsync()).Channels.Select(c => c.Duty));
        var writes = hub.Writes;
        Assert.Equal(Outcome.Rejected, (await hub.SetFanEndpointAsync("fan2", 70, expected)).Outcome);
        Assert.Equal(writes, hub.Writes);
    }
    [Fact] public async Task AtomicSettingsBackupAndModeSeparation()
    {
        var root=Path.Combine(Root,"artifacts","tests","settings-"+Guid.NewGuid().ToString("N"));
        var demo=new JsonSettingsStore(Path.Combine(root,"demo"));var real=new JsonSettingsStore(Path.Combine(root,"hardware"));
        await demo.SaveAsync(DemoHub.CreateSettings());await demo.SaveAsync(DemoHub.CreateSettings() with {Limits=new(30,100,70)});
        Assert.True(File.Exists(Path.Combine(demo.DirectoryPath,"settings.backup.json")));Assert.Equal(30,(await demo.LoadAsync()).Limits.Minimum);Assert.Null((await real.LoadAsync()).Limits.Minimum);
    }
    [Fact] public async Task CorruptSettingsDoNotAutoRestoreOrStartControl()
    {
        var root=Path.Combine(Root,"artifacts","tests","corrupt-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var store=new JsonSettingsStore(root);await File.WriteAllTextAsync(Path.Combine(root,"settings.json"),"{broken");
        var loaded=await store.LoadAsync();Assert.Null(loaded.Hardware.Hub);Assert.False(loaded.Startup.ResumeControl);Assert.NotNull(store.LoadWarning);
    }
}
