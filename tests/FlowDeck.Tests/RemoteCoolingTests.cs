using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class RemoteCoolingTests
{
    private sealed class Connection : IAsyncDisposable
    {
        public ServiceFixture Fixture { get; }
        public RemoteControlCoordinator Remote { get; }
        public CancellationTokenSource Stop { get; } = new();
        private readonly Task serving;
        public Connection(AppSettings? local = null)
        {
            using var user = WindowsIdentity.GetCurrent(); var sid = user.User!.Value;
            Fixture = new(sid);
            var pipe = "FlowDeck.Cooling.Test." + Guid.NewGuid().ToString("N");
            Remote = new(local ?? new(), () => CoolingServicePipeClient.ForDemo(pipe, Environment.ProcessId), new TestLog(), true);
            serving = ServeAsync(sid, pipe);
        }
        private async Task ServeAsync(string sid, string pipe)
        { await Fixture.Initialize(false); await new CoolingServicePipeServer(Fixture.Runtime, [sid], new TestLog(), pipe).RunAsync(Stop.Token); }
        public async Task Connect() { await Remote.ConnectAsync(); Remote.Start(); }
        public async ValueTask DisposeAsync()
        { Fixture.Hub.DelayMs = 0; await Remote.DisposeAsync(); Stop.Cancel(); await serving; await Fixture.DisposeAsync(); Stop.Dispose(); }
    }
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(20); Assert.True(condition()); }
    [Fact] public async Task ServiceConfigurationAndReadingsMergeWithoutOverwritingUserEnvironment()
    {
        var local = new AppSettings { Ui = new("dark", Width: 1240),
            PythonPath = "C:\\local-only\\python.exe", Motherboard = new([new("test", "ラジエーター")]), Startup = new(true, false) };
        await using var c = new Connection(local); await c.Connect();
        var actual = c.Remote.Settings;
        Assert.Equal(SettingsTransfer.Hash(ServiceFanConfiguration.From(c.Fixture.Runtime.Coordinator.Settings)), SettingsTransfer.Hash(ServiceFanConfiguration.From(actual)));
        Assert.Equal(local.Ui, actual.Ui); Assert.Equal(local.PythonPath, actual.PythonPath);
        Assert.Equal(SettingsTransfer.Hash(local.Motherboard), SettingsTransfer.Hash(actual.Motherboard)); Assert.Equal(local.Startup, actual.Startup);
        Assert.Equal(c.Fixture.Runtime.State().Control.Sensors, c.Remote.Snapshot.Sensors); Assert.True(c.Remote.Snapshot.HubReady);
        var before = c.Fixture.Runtime.SettingsRevision;
        await c.Remote.UpdateSettingsAsync(actual with { Ui = new("light") }, false);
        Assert.Equal(before, c.Fixture.Runtime.SettingsRevision); Assert.Equal("light", c.Remote.Settings.Ui.Theme);
        Assert.Null(c.Fixture.Runtime.Coordinator.Settings.PythonPath); Assert.Equal(0, c.Fixture.Hub.Writes);
    }
    [Fact] public async Task ManualBoardAndProfileChangesUseServerValidationAndFreshRevisions()
    {
        await using var c = new Connection(); await c.Connect(); var remote = c.Remote;
        var result = await remote.RequestAsync(c.Fixture.Manual(53)); Assert.Equal(Outcome.Confirmed, result.Outcome); Assert.Equal(53, remote.Snapshot.ConfirmedDuty);
        var port = MotherboardFans.Chassis.Id; var config = MotherboardFans.Settings(remote.Settings, port);
        await remote.BoardFans[port].ManualAsync(config, 43); Assert.Equal(43, remote.BoardFans[port].State.Target);
        await Assert.ThrowsAsync<IOException>(() => remote.BoardFans[port].ManualAsync(config with { Minimum = 1 }, 1));
        await Assert.ThrowsAsync<IOException>(() => remote.UpdateSettingsAsync(remote.Settings with { ChassisFan = config with { Minimum = 35 } }));
        Assert.Equal(43, c.Fixture.Drivers[port].Duty);
        await remote.BoardFans[port].RestoreAsync(); Assert.False(remote.BoardFans[port].State.Managing); Assert.Equal(65, remote.BoardFans[port].State.Reading!.Duty);
        await remote.StopControlAsync(); await remote.UpdateSettingsAsync(remote.Settings with { ChassisFan = config with { Minimum = 35 } });
        Assert.Equal(35, c.Fixture.Runtime.Coordinator.Settings.ChassisFan.Minimum);
        var profile = remote.Settings.Profiles[1];
        var saved = await remote.SaveProfileAsync(profile with { Name = "リモートで編集" }, profile.Revision);
        Assert.True(saved.Revision > profile.Revision); Assert.Equal(saved.Revision, remote.Settings.Profiles[1].Revision);
        Assert.Equal("リモートで編集", remote.Settings.Profiles[1].Name); Assert.Equal(53, result.Channels![0].ReportedDuty);
    }
    [Fact] public async Task LightingSamplesOnceAndSendsSameColorToTheCompanionWithoutChangingCooling()
    {
        await using var c = new Connection(); await c.Connect(); var remote = c.Remote;
        await remote.RequestAsync(c.Fixture.Manual(54)); var before = remote.Snapshot;
        var lease = await remote.BeginLightingStreamAsync(["led1"]); Assert.True(remote.LightingLeaseValid(lease));
        var count = 0; RgbColor? companion = null; var color = new RgbColor(21, 63, 195);
        Assert.True(await remote.SendLightingFrameAsync(lease, () => { count++; return color; }, value => { companion = value; return Task.CompletedTask; }));
        Assert.Equal(1, count); Assert.Equal(color, companion); Assert.Equal(color, c.Fixture.Hub.LightEffects["led1"].Colors[0]);
        Assert.False(remote.LightingLeaseValid(lease with { Channels = ["led2"] }));
        Assert.Equal(before.ControlRevision, c.Fixture.Runtime.Coordinator.Snapshot.ControlRevision); Assert.Equal(54, c.Fixture.Runtime.Coordinator.Snapshot.ConfirmedDuty);
        await remote.EndLightingStreamAsync(lease); Assert.False(remote.LightingLeaseValid(lease));
        await using var animator = new LightingAnimator(remote, ["led1"], "fixed", new(5, 40, 200), 2, _ => Task.CompletedTask);
        await animator.Started; await animator.Completion; Assert.Null(animator.Error); Assert.Equal(1, animator.Frames);
    }
    [Fact] public async Task LostServiceInvalidatesTelemetryAndLeasesAndNeverSendsLocallyOrReplaysWrites()
    {
        await using var c = new Connection(); await c.Connect(); var remote = c.Remote;
        await remote.RequestAsync(c.Fixture.Manual(57)); var lease = await remote.BeginLightingStreamAsync(["led1"]);
        await remote.BoardFans[MotherboardFans.Cpu.Id].ManualAsync(remote.Settings.CpuFan, 42);
        c.Stop.Cancel(); await Until(() => !remote.Connected);
        Assert.False(remote.Snapshot.HubReady); Assert.Null(remote.Snapshot.TelemetryTimestamp); Assert.Empty(remote.Snapshot.Sensors);
        Assert.Null(remote.Board.Timestamp); Assert.Null(remote.BoardFans[MotherboardFans.Cpu.Id].State.Reading); Assert.False(remote.LightingLeaseValid(lease));
        await Until(() => c.Fixture.Hub.Writes == 2 && c.Fixture.Drivers[MotherboardFans.Cpu.Id].Duty == 65);
        var result = await remote.RequestAsync(c.Fixture.Manual(80)); Assert.Equal(Outcome.Unknown, result.Outcome); Assert.Equal("SERVICE_UNCONFIRMED", result.ErrorCode);
        Assert.Equal(2, c.Fixture.Hub.Writes);
        await remote.UpdateSettingsAsync(remote.Settings with { Ui = new("dark") }, false); Assert.Equal("dark", remote.Settings.Ui.Theme);
        await Assert.ThrowsAsync<IOException>(() => remote.UpdateSettingsAsync(remote.Settings with { Limits = remote.Settings.Limits with { Minimum = 30 } }));
        Assert.Equal(25, remote.Settings.Limits.Minimum);
    }
    [Fact] public async Task StartupResumeRequiresUserOptInAndUsesOnlyTheServiceConfirmedPolicy()
    {
        await using var c = new Connection(); await c.Connect(); var remote = c.Remote;
        await remote.RequestAsync(c.Fixture.Manual(56)); await remote.StopControlAsync(); var count = c.Fixture.Hub.Writes;
        await remote.RestoreStartupAsync(); Assert.Equal(count, c.Fixture.Hub.Writes); Assert.False(remote.Snapshot.Managing);
        await remote.UpdateSettingsAsync(remote.Settings with { Startup = new(ResumeControl: true), LastConfirmedPolicy = new(FanMode.Manual, 99) }, false);
        await remote.RestoreStartupAsync(); Assert.True(remote.Snapshot.Managing); Assert.Equal(56, remote.Snapshot.ConfirmedDuty);
        Assert.False(c.Fixture.Runtime.Coordinator.Settings.Startup.ResumeControl); Assert.True(remote.Settings.Startup.ResumeControl);
    }
    [Fact] public async Task StartupPolicyHashAndRevisionCannotBeForgedOrReusedAfterConfigurationChanges()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); await f.Call("control", f.Manual(54)); await f.Call<object?>("stop", null);
        var policy = f.Runtime.Coordinator.Settings.LastConfirmedPolicy!;
        Assert.False((await f.Call("restore-startup", new ServiceStartupResume(f.Runtime.SettingsRevision, SettingsTransfer.Hash(policy with { ManualDuty = 90 })))).Success);
        var revision = f.Runtime.SettingsRevision;
        var config = ServiceFanConfiguration.From(f.Runtime.Coordinator.Settings);
        Assert.True((await f.Call("configure", new ServiceConfigure(config with { Limits = config.Limits with { Minimum = 24 } }, revision))).Success);
        Assert.False((await f.Call("restore-startup", new ServiceStartupResume(revision, SettingsTransfer.Hash(policy)))).Success);
        Assert.False((await f.Call("restore-startup", new ServiceStartupResume(f.Runtime.SettingsRevision, SettingsTransfer.Hash(policy)))).Success);
        Assert.False(f.Runtime.Coordinator.Snapshot.Managing); Assert.Equal(2, f.Hub.Writes);
    }
    [Fact] public async Task PendingRestoreRetainsImportedSettingsUntilAcknowledgedAndSendsNoOutput()
    {
        var desired = SettingsTransfer.Portable(DemoHub.CreateSettings()) with { ServiceConfigurationPending = true, Limits = new(23, 100, 75),
            Lighting = new(Effect: HubLighting.Preset("purple"), Channels: ["led1"]) };
        await using var c = new Connection(desired); await c.Connect();
        Assert.True(c.Remote.ConfigurationPending); Assert.False(c.Remote.Snapshot.HubReady); Assert.Equal(23, c.Remote.Settings.Limits.Minimum);
        await Assert.ThrowsAsync<IOException>(() => c.Remote.BeginLightingStreamAsync(["led1"]));
        await Assert.ThrowsAsync<IOException>(() => c.Remote.BoardFans[MotherboardFans.Cpu.Id].TestAsync());
        await c.Remote.ApplyPendingConfigurationAsync(); Assert.False(c.Remote.ConfigurationPending); Assert.True(c.Remote.Snapshot.HubReady);
        Assert.Equal(23, c.Fixture.Runtime.Coordinator.Settings.Limits.Minimum); Assert.False(c.Remote.Settings.Hardware.WiringConfirmed);
        Assert.False(c.Remote.Settings.CpuFan.WiringConfirmed); Assert.Null(c.Remote.Settings.LastConfirmedPolicy); Assert.False(c.Remote.Settings.Startup.ResumeControl);
        Assert.Equal(SettingsTransfer.Hash(desired.Lighting), SettingsTransfer.Hash(c.Remote.Settings.Lighting));
        Assert.Equal(0, c.Fixture.Hub.Writes); Assert.Empty(c.Fixture.Hub.LightEffects); Assert.All(c.Fixture.Drivers.Values, d => Assert.Equal(0, d.Writes));
    }
    [Fact] public async Task MissingImportedHubNeverSubstitutesAnotherDeviceOrLosesPendingConfiguration()
    {
        var desired = SettingsTransfer.Portable(DemoHub.CreateSettings()) with { ServiceConfigurationPending = true,
            Hardware = new(new("absent-id", "DEMO Control Hub", null, "untrusted-path")), Limits = new(23, 100, 75) };
        await using var c = new Connection(desired); await c.Connect();
        await Assert.ThrowsAsync<IOException>(() => c.Remote.ApplyPendingConfigurationAsync());
        Assert.True(c.Remote.ConfigurationPending); Assert.Equal("absent-id", c.Remote.Settings.Hardware.Hub!.Id);
        Assert.Equal("demo", c.Fixture.Runtime.Coordinator.Settings.Hardware.Hub!.Id); Assert.Equal(25, c.Fixture.Runtime.Coordinator.Settings.Limits.Minimum);
        Assert.Equal(0, c.Fixture.Hub.Writes);
    }
    [Fact] public async Task EmptyImportedHubClosesOldConnectionAndCannotReconnectWithItsOldIdentity()
    {
        await using var c = new Connection(new AppSettings { ServiceConfigurationPending = true }); await c.Connect();
        await c.Remote.ApplyPendingConfigurationAsync(); await c.Fixture.Runtime.Coordinator.TickAsync(); await c.Remote.TickAsync();
        Assert.Null(c.Remote.Settings.Hardware.Hub); Assert.False(c.Remote.Snapshot.HubReady); Assert.Empty(c.Remote.LastStatus.Channels);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Fixture.Runtime.Coordinator.OpenAsync(DemoHub.Identity, true));
        Assert.Null(c.Fixture.Runtime.Coordinator.Settings.Hardware.Hub); Assert.Equal(0, c.Fixture.Hub.Writes);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ProxyWaitsForScmResumeReconnectsAndRestoresOnlyOnce(bool guiFirst)
    {
        await using var c = new Connection(); await c.Connect(); var remote = c.Remote;
        await remote.RequestAsync(c.Fixture.Manual(57)); await remote.BoardFans[MotherboardFans.Cpu.Id].ManualAsync(remote.Settings.CpuFan, 42);
        if (guiFirst) await remote.PrepareSleepAsync();
        await c.Fixture.Runtime.SuspendForPowerAsync();
        if (!guiFirst) await remote.PrepareSleepAsync();
        var resuming = remote.ResumeFromSleepAsync(); await Task.Delay(100); Assert.False(resuming.IsCompleted);
        await c.Fixture.Runtime.ResumeForPowerAsync(); await resuming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(remote.Connected); Assert.True(remote.Snapshot.Managing); Assert.Equal(57, remote.Snapshot.ConfirmedDuty);
        Assert.False(remote.BoardFans[MotherboardFans.Cpu.Id].State.Managing); Assert.Equal(65, remote.BoardFans[MotherboardFans.Cpu.Id].State.Reading!.Duty);
        var generation = remote.Snapshot.DeviceGeneration; var writes = c.Fixture.Hub.Writes;
        await remote.ResumeFromSleepAsync(); Assert.Equal(generation, remote.Snapshot.DeviceGeneration); Assert.Equal(writes, c.Fixture.Hub.Writes);
    }
    [Fact] public async Task ResumeStillConsultsTheServiceWhenTheGuiMissedTheSuspendNotification()
    {
        await using var c = new Connection(); await c.Connect();
        await c.Remote.RequestAsync(c.Fixture.Manual(58));
        await c.Fixture.Runtime.SuspendForPowerAsync(); await c.Fixture.Runtime.ResumeForPowerAsync();
        Assert.False(c.Fixture.Runtime.Coordinator.Snapshot.Managing);
        await c.Remote.ResumeFromSleepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(c.Remote.Connected); Assert.True(c.Fixture.Runtime.Coordinator.Snapshot.Managing);
        Assert.Equal(58, c.Remote.Snapshot.ConfirmedDuty);
        var writes = c.Fixture.Hub.Writes;
        await c.Remote.ResumeFromSleepAsync(); Assert.Equal(writes, c.Fixture.Hub.Writes);
    }
}
