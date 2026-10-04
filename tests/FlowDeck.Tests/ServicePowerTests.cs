using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServicePowerTests
{
    private static CoolingServiceRequest Request<T>(ServiceHello hello, long sequence, string command, T value) =>
        new(1, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), sequence, command, CoolingServiceProtocol.Payload(value));
    private static CoolingServiceRequest Resume(CoolingServiceRuntime runtime, ServiceHello hello, long sequence = 1, string? token = null) =>
        Request(hello, sequence, "restore-sleep", new ServiceSleepResume(token ?? runtime.State().SleepResumeToken!, runtime.SettingsRevision));
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task GuiAndScmCanSuspendInEitherOrderAndResumeConsumesTheConfirmedPolicyOnlyOnce(bool guiFirst)
    {
        await using var f = new ServiceFixture(); await f.Initialize(); await f.Call("control", f.Manual(58));
        await f.Call("board-manual", new ServiceBoardCommand(MotherboardFans.Cpu.Id, f.Runtime.SettingsRevision, 42));
        if (guiFirst) Assert.True((await f.Call<object?>("suspend", null)).Success);
        await f.Runtime.SuspendForPowerAsync(); await f.Runtime.SuspendForPowerAsync();
        if (!guiFirst) Assert.False((await f.Call<object?>("suspend", null)).Success);
        Assert.False(f.Runtime.HasOwner); Assert.Equal(65, f.Drivers[MotherboardFans.Cpu.Id].Duty);
        Assert.All((await f.Hub.ReadAsync()).Channels, c => Assert.Equal(70, c.Duty));
        Assert.Null(f.Runtime.State().SleepResumeToken);
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.OpenSessionAsync(ServiceFixture.Sid));
        await f.Runtime.ResumeForPowerAsync(); var generation = f.Runtime.Coordinator.Snapshot.DeviceGeneration;
        await f.Runtime.ResumeForPowerAsync(); Assert.Equal(generation, f.Runtime.Coordinator.Snapshot.DeviceGeneration);
        var hello = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid); Assert.NotEqual(f.Hello.Session, hello.Session);
        Assert.False(f.Runtime.Coordinator.Snapshot.Managing); Assert.NotNull(f.Runtime.State().SleepResumeToken);
        var resume = Resume(f.Runtime, hello); var first = await f.Runtime.ExecuteAsync(resume);
        Assert.True(first.Success); Assert.Equal(58, f.Runtime.Coordinator.Snapshot.ConfirmedDuty); Assert.True(f.Runtime.Coordinator.Snapshot.Managing);
        Assert.False(f.Runtime.Fans[MotherboardFans.Cpu.Id].State.Managing); Assert.False(f.Runtime.Coordinator.Settings.Startup.ResumeControl);
        var writes = f.Hub.Writes; Assert.Null(f.Runtime.State().SleepResumeToken);
        Assert.True((await f.Runtime.ExecuteAsync(resume)).Success); Assert.Equal(writes, f.Hub.Writes);
        Assert.False((await f.Runtime.ExecuteAsync(resume with { Id = Guid.NewGuid().ToString("N"), Sequence = 2 })).Success); Assert.Equal(writes, f.Hub.Writes);
    }
    [Theory] [InlineData("changed")] [InlineData("expired")] [InlineData("stop")]
    public async Task ChangedSettingsExpiredTicketAndExplicitStopPreventDelayedResume(string reason)
    {
        await using var f = new ServiceFixture(); await f.Initialize(); await f.Call("control", f.Manual(56));
        await f.Runtime.SuspendForPowerAsync(); await f.Runtime.ResumeForPowerAsync(); var hello = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid);
        var token = f.Runtime.State().SleepResumeToken!;
        if (reason == "changed")
            f.Runtime.Coordinator.UpdateSettings(f.Runtime.Coordinator.Settings with { Limits = f.Runtime.Coordinator.Settings.Limits with { Minimum = 24 } });
        if (reason == "expired") f.LeaseClock.Advance(60);
        if (reason == "stop") await f.Runtime.ExecuteAsync(Request<object?>(hello, 1, "stop", null));
        var result = await f.Runtime.ExecuteAsync(Resume(f.Runtime, hello, 2, token));
        Assert.False(result.Success); Assert.False(f.Runtime.Coordinator.Snapshot.Managing); Assert.Equal(2, f.Hub.Writes);
    }
    [Fact] public async Task AnotherAuthorizedUserCannotSeeOrUseTheSleepingUsersTicket()
    {
        var settings = DemoHub.CreateSettings(); var hub = new DemoHub { DelayMs = 0 };
        const string other = "S-1-5-21-111-222-333-1002";
        await using var runtime = new CoolingServiceRuntime(hub, new DemoTemperatures(), new DemoMotherboard(), _ => new ServiceDriver(),
            settings, [ServiceFixture.Sid, other], new TestLog());
        await runtime.InitializeAsync(false); var initial = await runtime.OpenSessionAsync(ServiceFixture.Sid);
        await runtime.ExecuteAsync(Request(initial, 1, "control", new ControlRequest("manual", "PC", runtime.Epoch, runtime.Coordinator.Snapshot.ControlRevision, "SetManualDuty", Duty: 55)));
        // Do not disconnect the original user after resume: doing so would erase the ticket,
        // and a rejection would no longer exercise its owner-SID restriction.
        await runtime.SuspendForPowerAsync(); await runtime.ResumeForPowerAsync();
        Assert.NotNull(runtime.Coordinator.Settings.LastConfirmedPolicy);
        var second = await runtime.OpenSessionAsync(other);
        Assert.Null(runtime.State().SleepResumeToken);
        Assert.False((await runtime.ExecuteAsync(Resume(runtime, second, token: Guid.NewGuid().ToString("N")))).Success);
        Assert.False(runtime.Coordinator.Snapshot.Managing); Assert.Equal(2, hub.Writes);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task UnsafeTemperatureOrMissingReadingPreventsResumingAutomaticControl(bool missing)
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var profile = f.Runtime.Coordinator.Settings.Profiles[1];
        await f.Call("control", f.Manual(55) with { Command = "SelectAutoProfile", ProfileId = profile.Id, ProfileRevision = profile.Revision });
        await f.Runtime.SuspendForPowerAsync(); f.Temperatures.Missing = missing; f.Temperatures.Cpu = missing ? 58 : 110;
        await f.Runtime.ResumeForPowerAsync(); var hello = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid);
        var response = await f.Runtime.ExecuteAsync(Resume(f.Runtime, hello)); Assert.True(response.Success);
        var state = response.Payload.Deserialize<ServiceCoolingState>(Contract.Json)!;
        Assert.True(state.Control.Faulted); Assert.False(state.Control.Managing); Assert.Null(state.SleepResumeToken); Assert.Equal(2, f.Hub.Writes);
    }
    [Fact] public async Task MonitoringAndFaultedControlNeverCreateResumeTickets()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        await f.Runtime.SuspendForPowerAsync(); await f.Runtime.ResumeForPowerAsync(); var hello = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid);
        Assert.Null(f.Runtime.State().SleepResumeToken); Assert.Equal(0, f.Hub.Writes);
        f.Hub.FailNextFanWrite = true;
        await f.Runtime.ExecuteAsync(Request(hello, 1, "control", f.Manual(55))); Assert.True(f.Runtime.Coordinator.Snapshot.Faulted);
        await f.Runtime.SuspendForPowerAsync(); await f.Runtime.ResumeForPowerAsync(); await f.Runtime.OpenSessionAsync(ServiceFixture.Sid);
        Assert.Null(f.Runtime.State().SleepResumeToken);
    }
}
