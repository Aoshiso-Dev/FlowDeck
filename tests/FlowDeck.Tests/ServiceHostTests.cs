using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ServiceTestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlowDeck-Service-Test-" + Guid.NewGuid().ToString("N"));
    public ServiceTestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        // Forced process termination can leave a short-lived Windows file-close delay.
        for (var attempt = 0; ; attempt++)
            try { Directory.Delete(Path, true); return; }
            catch (IOException) when (attempt < 19) { Thread.Sleep(50); }
    }
}

public class ServiceHostTests
{
    private sealed class LocatedHub : IHubAdapter
    {
        private readonly DemoHub inner = new() { DelayMs = 0 };
        public HubIdentity[] Detected { get; set; } = [DemoHub.Identity];
        public List<HubIdentity> Opened { get; } = [];
        public bool IsDemo => true;
        public string Version => inner.Version;
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => Task.FromResult(Detected);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default)
        { Opened.Add(identity); return inner.OpenAsync(identity, generation, ct); }
        public Task<HubStatus> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => inner.SetFanGroupAsync(channels, duty, ct);
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => inner.SetLightingAsync(channels, preset, ct);
        public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    [Fact] public async Task EveryServiceHubOpenResolvesTheExactDetectedDeviceAndIgnoresStoredPaths()
    {
        using var dir = new ServiceTestDirectory(); var inner = new LocatedHub();
        await using var adapter = new RecoverableHubAdapter(inner, new(dir.Path), DemoHub.CreateSettings);
        var untrusted = DemoHub.Identity with { Path = "untrusted-stored-path" };
        await adapter.OpenAsync(untrusted, 1); Assert.Equal(DemoHub.Identity, Assert.Single(inner.Opened));
        inner.Detected = [DemoHub.Identity with { Path = "newly-detected-path" }];
        await adapter.OpenAsync(untrusted, 2); Assert.Equal("newly-detected-path", inner.Opened[1].Path);
        inner.Detected = [DemoHub.Identity with { Id = "other-device" }];
        await Assert.ThrowsAsync<IOException>(() => adapter.OpenAsync(untrusted, 3));
        inner.Detected = [DemoHub.Identity, DemoHub.Identity with { Path = "ambiguous-path" }];
        await Assert.ThrowsAsync<IOException>(() => adapter.OpenAsync(untrusted, 4));
        Assert.Equal(2, inner.Opened.Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HubCrashRecoveryHandlesCompleteAndPartialWrites(bool partial)
    {
        using var dir = new ServiceTestDirectory(); var inner = new DemoHub { DelayMs = 0, FailNextFanWrite = partial }; var settings = DemoHub.CreateSettings();
        var journal = new HubFanRecoveryJournal(dir.Path); var first = new RecoverableHubAdapter(inner, journal, () => settings);
        await first.OpenAsync(DemoHub.Identity, 1); await first.SetFanGroupAsync(settings.Hardware.Fans!, 54);
        Assert.NotNull(journal.Pending); Assert.Equal(54, journal.Pending.IntendedDuty);
        var recovered = new HubFanRecoveryJournal(dir.Path); var second = new RecoverableHubAdapter(inner, recovered, () => settings);
        await second.RecoverAsync(); Assert.Null(recovered.Pending); Assert.All((await inner.ReadAsync()).Channels, c => Assert.Equal(70, c.Duty));
        var writes = inner.Writes; await second.RecoverAsync(); Assert.Equal(writes, inner.Writes);
    }
    [Fact] public async Task HubRecoveryDoesNotOverwriteAnotherControllerOrAnotherDevice()
    {
        using var dir = new ServiceTestDirectory(); var inner = new DemoHub { DelayMs = 0 }; var settings = DemoHub.CreateSettings();
        var journal = new HubFanRecoveryJournal(dir.Path); var adapter = new RecoverableHubAdapter(inner, journal, () => settings);
        await adapter.OpenAsync(DemoHub.Identity, 1); await adapter.SetFanGroupAsync(settings.Hardware.Fans!, 54);
        await inner.SetFanGroupAsync(["fan1"], 83); var writes = inner.Writes;
        await Assert.ThrowsAsync<IOException>(() => adapter.RecoverAsync()); Assert.Equal(writes, inner.Writes); Assert.NotNull(journal.Pending);
        var other = new RecoverableHubAdapter(inner, new(dir.Path), () => settings);
        await Assert.ThrowsAsync<IOException>(() => other.OpenAsync(DemoHub.Identity with { Id = "another-hub" }, 2)); Assert.Equal(writes, inner.Writes);
    }
    [Fact] public async Task ConfirmedExitClearsRecoveryButAnUnwritableJournalPreventsHardwareWrites()
    {
        using var dir = new ServiceTestDirectory(); var inner = new DemoHub { DelayMs = 0 }; var settings = DemoHub.CreateSettings();
        var journal = new HubFanRecoveryJournal(dir.Path); var adapter = new RecoverableHubAdapter(inner, journal, () => settings);
        await adapter.OpenAsync(DemoHub.Identity, 1); await adapter.SetFanGroupAsync(settings.Hardware.Fans!, 54);
        await adapter.SetFanGroupAsync(settings.Hardware.Fans!, 70); Assert.Null(new HubFanRecoveryJournal(dir.Path).Pending);
        using var bad = new ServiceTestDirectory(); Directory.CreateDirectory(System.IO.Path.Combine(bad.Path, "hub-fan-recovery.json"));
        var blocked = new RecoverableHubAdapter(inner, new(bad.Path), () => settings); await blocked.OpenAsync(DemoHub.Identity, 2); var writes = inner.Writes;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => blocked.SetFanGroupAsync(settings.Hardware.Fans!, 52)); Assert.Equal(writes, inner.Writes);
    }
    [Fact] public void RecoveryJournalRejectsTamperingAndUnknownTargets()
    {
        using var dir = new ServiceTestDirectory(); var journal = new HubFanRecoveryJournal(dir.Path);
        Assert.Throws<IOException>(() => journal.Save(new("demo", "wire", 70, 54, new() { ["raw-register"] = 45 })));
        journal.Save(new("demo", "wire", 70, 54, new() { ["fan1"] = 45 }));
        var path = System.IO.Path.Combine(dir.Path, "hub-fan-recovery.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"exitDuty\":70", "\"exitDuty\":10"));
        Assert.Throws<IOException>(() => new HubFanRecoveryJournal(dir.Path));
    }
    [Fact] public void ProtectedDeploymentRejectsUserWritableOrUserOwnedFilesAndInteractiveExecution()
    {
        var security = new DirectorySecurity(); var system = new SecurityIdentifier("S-1-5-18");
        security.SetOwner(system); security.AddAccessRule(new(system, FileSystemRights.FullControl, AccessControlType.Allow));
        ServiceEnvironment.ValidateSecurity(security);
        var users = new SecurityIdentifier("S-1-5-32-545");
        security.AddAccessRule(new(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow)); ServiceEnvironment.ValidateSecurity(security);
        security.AddAccessRule(new(users, FileSystemRights.Write, AccessControlType.Allow)); Assert.Throws<UnauthorizedAccessException>(() => ServiceEnvironment.ValidateSecurity(security));
        security = new(); security.SetOwner(new SecurityIdentifier(ServiceFixture.Sid)); security.AddAccessRule(new(system, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => ServiceEnvironment.ValidateSecurity(security));
        var unrestricted = new FileSecurity(); unrestricted.SetSecurityDescriptorSddlForm("O:SYG:SYD:NO_ACCESS_CONTROL");
        Assert.Throws<UnauthorizedAccessException>(() => ServiceEnvironment.ValidateSecurity(unrestricted));
        Assert.Throws<UnauthorizedAccessException>(() => ServiceEnvironment.ValidateInstallation(AppContext.BaseDirectory));
    }
    [Fact] public async Task HardwareLeaseUsesAThreadStableMutexAndExcludesTheOldApplication()
    {
        var prefix = "FlowDeck.Service.Test." + Guid.NewGuid().ToString("N");
        var lease = await ServiceHardwareLease.AcquireAsync(prefix);
        await Task.Run(() => { using var old = new ApplicationInstance(false, prefix); Assert.False(old.Acquired); });
        await Task.Run(async () => await lease.DisposeAsync());
        await Task.Run(() => { using var old = new ApplicationInstance(false, prefix); Assert.True(old.Acquired); });
        // Failure at the second mutex must release the first mutex as well.
        await Task.Run(() =>
        {
            using var second = new Mutex(true, prefix + "Global-FlowDeck-1E71-2022", out var created); Assert.True(created);
            try
            {
                Assert.Throws<IOException>(() => ServiceHardwareLease.AcquireAsync(prefix).GetAwaiter().GetResult());
                using var first = new Mutex(true, prefix + "Global-NzxtControlHub-1E71-2022", out var freed);
                Assert.True(freed); first.ReleaseMutex();
            }
            finally { second.ReleaseMutex(); }
        });
    }
    [Fact] public void ServiceSettingsExcludeUserSecretsExecutablePathsAndDisplayPreferences()
    {
        using var dir = new ServiceTestDirectory(); var store = new ServiceSettingsStore(dir.Path);
        var settings = DemoHub.CreateSettings() with { PythonPath = "untrusted.exe", Ui = new("dark"), Startup = new(true, true) };
        store.Save(settings); var restored = store.Load();
        Assert.Equal(settings.Hardware.Hub, restored.Hardware.Hub); Assert.Equal(settings.Limits, restored.Limits);
        var json = File.ReadAllText(System.IO.Path.Combine(dir.Path, "cooling-settings.json"));
        Assert.DoesNotContain("untrusted", json); Assert.DoesNotContain("private-address", json); Assert.DoesNotContain("COM99", json);
        Assert.Null(restored.PythonPath); Assert.False(restored.Startup.ResumeControl); Assert.Equal("system", restored.Ui.Theme);
    }
    [Fact] public async Task PersistenceFailureStopsCoolingAndRejectsFurtherChangesUntilSavingRecovers()
    {
        var fail = false; var hub = new DemoHub { DelayMs = 0 }; var drivers = MotherboardFans.Ports.ToDictionary(p => p.Id, _ => new ServiceDriver());
        await using var runtime = new CoolingServiceRuntime(hub, new DemoTemperatures(), new DemoMotherboard(), p => drivers[p.Id], DemoHub.CreateSettings(),
            [ServiceFixture.Sid], new TestLog(), persistSettings: _ => { if (fail) throw new IOException("disk unavailable"); });
        await runtime.InitializeAsync(false); var hello = await runtime.OpenSessionAsync(ServiceFixture.Sid); long sequence = 0;
        Task<CoolingServiceReply> Send(int duty) => runtime.ExecuteAsync(new(1, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "control",
            CoolingServiceProtocol.Payload(new ControlRequest(Guid.NewGuid().ToString("N"), "test", hello.Epoch, runtime.Coordinator.Snapshot.ControlRevision, "SetManualDuty", Duty: duty))));
        fail = true; await Send(54); Assert.Contains("保存", runtime.State().LifecycleError);
        Assert.False((await Send(57)).Success); await runtime.SweepAsync(); Assert.False(runtime.Coordinator.Snapshot.Managing); Assert.Equal(70, runtime.Coordinator.Snapshot.ConfirmedDuty);
        fail = false; await runtime.SweepAsync(); Assert.Null(runtime.State().LifecycleError); Assert.True((await Send(55)).Success);
    }
    [Fact] public void BootIdentityIsStableAcrossIndependentQueries()
    {
        var first = ServiceEnvironment.BootIdentity(); var second = ServiceEnvironment.BootIdentity();
        Assert.Equal(first, second); Assert.True(DateTimeOffset.TryParse(first, out _));
    }
    [Fact] public async Task ChangingCoolingConditionsInvalidatesThePreviouslyPersistedResumePolicy()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); await f.Call("control", f.Manual(54));
        Assert.NotNull(f.Runtime.Coordinator.Settings.LastConfirmedPolicy); await f.Call<object?>("stop", null);
        var configuration = ServiceFanConfiguration.From(f.Runtime.Coordinator.Settings);
        Assert.True((await f.Call("configure", new ServiceConfigure(configuration with { Limits = configuration.Limits with { Exit = 80 } }, f.Runtime.SettingsRevision))).Success);
        Assert.Null(f.Runtime.Coordinator.Settings.LastConfirmedPolicy); Assert.Null(f.Runtime.Coordinator.Snapshot.LastConfirmed);
    }
}
