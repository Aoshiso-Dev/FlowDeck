using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class HubDeviceDriverTests
{
    [Fact] public async Task ExplicitCalibrationCanIdentifyAnUnconfiguredFanAndRestoreItsOriginalOutput()
    {
        using var dir=new ServiceTestDirectory(); var hub=new Hub(); await using var driver=new HubDeviceDriver(Installation,hub.Identity.Id,hub,dir.Path); await driver.InitializeAsync();
        var binding=driver.BindingCandidate!; Assert.NotNull(driver.ValidateCoolingSetup("fan1")); Assert.Null(driver.ValidateCalibrationStart("fan1",binding));
        hub.BeforeWrite=()=>Assert.Single(Journal(driver,dir.Path).Pending);
        var started=await driver.BeginCalibrationAsync(CalibrationTestActuation.Create("fan1",binding,100,45),default);
        Assert.Equal(Outcome.Confirmed,started.Result.Outcome); Assert.Equal(45,started.Before!.Duty);
        Assert.Equal(45,Assert.Single(Journal(driver,dir.Path).Pending).ExitDuty); Assert.NotNull(driver.ValidateCoolingSetup("fan1"));
        Assert.Equal(100,(await driver.ReadCalibrationAsync("fan1",binding,default)).Duty);
        Assert.Equal(Outcome.Confirmed,(await driver.EndCalibrationAsync("fan1",default)).Outcome); Assert.Empty(Journal(driver,dir.Path).Pending);
        Assert.Equal(45,(await hub.ReadAsync()).Channels[0].Duty); Assert.Equal(2,hub.Inner.Writes); Assert.Null(driver.CoolingConfiguration("fan1"));
    }
    [Theory] [InlineData("zero")] [InlineData("identity")] [InlineData("duty")] [InlineData("guard")]
    public async Task CalibrationRejectsAbsentRotationChangedIdentityOrOutputAndRevokedAuthorization(string reason)
    {
        using var dir=new ServiceTestDirectory(); var hub=new Hub(); await using var driver=await Ready(hub,dir.Path); var expected=driver.BindingCandidate!;
        if(reason=="zero") { hub.Transform=s=>s with { Channels=s.Channels.Select(c=>c.Id=="fan1"?c with { Rpm=0 }:c).ToArray() }; await driver.RefreshAsync(); }
        var result=await driver.BeginCalibrationAsync(CalibrationTestActuation.Create("fan1",reason=="identity"?expected with { Fingerprint=new('f',64) }:expected,100,reason=="duty"?20:45,
            reason=="guard"?()=>new("権限終了"):null),default);
        Assert.Equal(Outcome.Rejected,result.Result.Outcome); Assert.Equal(0,hub.Inner.Writes); Assert.Empty(Journal(driver,dir.Path).Pending);
    }
    [Fact] public async Task CalibrationDoesNotReleaseAnExistingOwnerAndOtherFanIoStillRunsBetweenSamples()
    {
        using var dir=new ServiceTestDirectory(); var hub=new Hub(); await using var driver=await Ready(hub,dir.Path);
        await driver.ApplyAsync(new("fan1",DeviceOperation.SetDuty,62),default); var writes=hub.Inner.Writes;
        var rejected=await driver.BeginCalibrationAsync(CalibrationTestActuation.Create("fan1",driver.BindingCandidate!,100,62),default);
        Assert.Equal(Outcome.Rejected,rejected.Result.Outcome); await driver.EndCalibrationAsync("fan1",default); Assert.Equal(writes,hub.Inner.Writes);
        await driver.ApplyAsync(new("fan1",DeviceOperation.ReleaseCooling),default);
        var result=await driver.BeginCalibrationAsync(CalibrationTestActuation.Create("fan1",driver.BindingCandidate!,20,55),default); Assert.Equal(Outcome.Confirmed,result.Result.Outcome);
        Assert.Equal(20,(await driver.ReadCalibrationAsync("fan1",driver.BindingCandidate!,default)).Duty);
        Assert.Equal(Outcome.Confirmed,(await driver.ApplyAsync(new("fan3",DeviceOperation.SetDuty,72),default)).Outcome);
        await driver.EndCalibrationAsync("fan1",default); Assert.Equal(55,(await hub.ReadAsync()).Channels[0].Duty); Assert.Equal(72,(await hub.ReadAsync()).Channels[2].Duty);
    }
    [Fact] public async Task LostCalibrationResponseRetainsRecoveryUntilOriginalOutputIsConfirmed()
    {
        using var dir=new ServiceTestDirectory(); var hub=new Hub(); await using var driver=await Ready(hub,dir.Path); hub.LoseAfterWrite=true;
        var result=await driver.BeginCalibrationAsync(CalibrationTestActuation.Create("fan1",driver.BindingCandidate!,100,45),default); Assert.Equal(Outcome.Unknown,result.Result.Outcome);
        Assert.Single(Journal(driver,dir.Path).Pending); hub.LoseAfterWrite=false;
        Assert.Equal(Outcome.Confirmed,(await driver.EndCalibrationAsync("fan1",default)).Outcome); Assert.Empty(Journal(driver,dir.Path).Pending); Assert.Equal(45,(await hub.ReadAsync()).Channels[0].Duty);
    }
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Hub : IHubLightingEndpointAdapter
    {
        public Hub(string id = "hub-a", bool withoutSerial = false) { Identity = new(id, "同じモデルのHub", withoutSerial ? null : id, "detected:" + id); Inner = new(Identity) { DelayMs = 0 }; }
        public HubIdentity Identity { get; }
        public DemoHub Inner { get; }
        public HubIdentity[]? Candidates { get; set; }
        public Func<HubStatus, HubStatus> Transform { get; set; } = s => s;
        public Action? BeforeWrite { get; set; }
        public bool LoseAfterWrite { get; set; }
        public bool Disconnected { get; set; }
        public bool FailEveryRead { get; set; }
        public int Reads { get; private set; }
        public bool IsDemo => true;
        public bool SupportsFanEndpoints => true;
        public bool SupportsFanDeadlines => true;
        public bool Smooth;
        public bool SupportsLightingEndpoints => Smooth;
        public bool SupportsLightingDeadlines => Smooth;
        public bool SupportsSmoothLightingFrames => Smooth;
        public Task<HubResult> SetLightingEndpointsAsync(string[] channels, HubLightSetting effect, HubLightingExpectation expected, CancellationToken ct = default)
            => throw new InvalidOperationException("Fan test must not write lighting");
        public string Version => "test";
        public List<HubIdentity> Opened { get; } = [];
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => Task.FromResult(Candidates ?? [Identity]);
        public async Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default)
        { Opened.Add(identity); Disconnected = false; return Transform(await Inner.OpenAsync(identity, generation, ct)); }
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default)
        {
            Reads++;
            if (Disconnected || FailEveryRead) throw new IOException("HUB_DISCONNECTED");
            return Transform(await Inner.ReadAsync(ct));
        }
        public async Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default)
        {
            BeforeWrite?.Invoke(); var result = await Inner.SetFanEndpointAsync(channel, duty, expected, ct);
            if (LoseAfterWrite) throw new IOException("response lost after sending");
            return result;
        }
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) => throw new InvalidOperationException("Generic driver must never issue a whole-Hub command");
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => throw new InvalidOperationException("Cooling must not change lighting");
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static CoolingEndpointConfiguration Config(HubDeviceDriver driver, string channel, int minimum = 30, int exit = 65) =>
        new(new(driver.DeviceId, channel), true, new(minimum, 100, exit, CoolingExitAction.FixedDuty, []), []);
    private static async Task<HubDeviceDriver> Ready(Hub hub, string path)
    {
        var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, path); await driver.InitializeAsync();
        driver.Configure(driver.BindingCandidate, [Config(driver, "fan1", 30, 55), Config(driver, "fan3", 50, 80)]); return driver;
    }
    private static HubEndpointRecoveryJournal Journal(HubDeviceDriver driver, string path) => new(path, driver.DeviceId);
    [Fact] public async Task IndividualFansRetainSeparateMinimumAndExitAndDoNotTouchOtherPorts()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        Assert.Equal(0, hub.Inner.Writes);
        Assert.Equal(30, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 20), default)).ReportedDuty);
        Assert.Equal(50, (await driver.ApplyAsync(new("fan3", DeviceOperation.SetDuty, 20), default)).ReportedDuty);
        Assert.Equal(new int?[] { 30, 45, 50, 45, 45 }, (await hub.ReadAsync()).Channels.Select(c => c.Duty));
        var results = await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default);
        Assert.Equal(new[] { "fan1", "fan3" }, results.Select(r => r.EndpointId)); Assert.All(results, r => Assert.Equal(Outcome.Confirmed, r.Result.Outcome));
        Assert.Equal(new int?[] { 55, 45, 80, 45, 45 }, (await hub.ReadAsync()).Channels.Select(c => c.Duty));
        Assert.Empty(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task JournalIsDurableBeforeWriteAndOriginalDutySurvivesSubsequentUpdates()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        hub.BeforeWrite = () => Assert.Single(Journal(driver, dir.Path).Pending);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 61), default);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 72), default);
        var saved = Assert.Single(Journal(driver, dir.Path).Pending);
        Assert.Equal(45, saved.OriginalDuty); Assert.Equal(61, saved.BeforeDuty); Assert.Equal(72, saved.IntendedDuty);
    }
    [Fact] public async Task LostResponseIsRecoveredFromDiskWithoutUsingNewEditedLimits()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var first = await Ready(hub, dir.Path);
        hub.LoseAfterWrite = true;
        Assert.Equal(Outcome.Unknown, (await first.ApplyAsync(new("fan3", DeviceOperation.SetDuty, 67), default)).Outcome);
        Assert.Single(Journal(first, dir.Path).Pending); hub.LoseAfterWrite = false;
        await using var second = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path); await second.InitializeAsync();
        Assert.Throws<IOException>(() => second.Configure(second.BindingCandidate, [Config(second, "fan3", 25, 40)]));
        var result = Assert.Single(await second.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default));
        Assert.Equal(80, result.Result.ReportedDuty); Assert.Empty(Journal(second, dir.Path).Pending);
        // The first object models a dead process and is deliberately never resumed or disposed.
    }
    [Fact] public async Task ForeignOutputOnOnePortDoesNotPreventIndependentRestoration()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 61), default);
        await driver.ApplyAsync(new("fan3", DeviceOperation.SetDuty, 67), default);
        await hub.Inner.SetFanGroupAsync(["fan1"], 99);
        var result = await driver.ReleaseAsync(DeviceReleaseReason.ClientExpired, default);
        Assert.Equal(Outcome.Unknown, result.Single(r => r.EndpointId == "fan1").Result.Outcome);
        Assert.Equal(Outcome.Confirmed, result.Single(r => r.EndpointId == "fan3").Result.Outcome);
        Assert.Equal(new int?[] { 99, 45, 80, 45, 45 }, (await hub.ReadAsync()).Channels.Select(c => c.Duty));
        Assert.Equal("fan1", Assert.Single(Journal(driver, dir.Path).Pending).Channel);
    }
    [Fact] public async Task WorkerRechecksOutputAfterTheDriversPreflight()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        hub.BeforeWrite = () => { hub.BeforeWrite = null; hub.Inner.SetFanGroupAsync(["fan1"], 99).GetAwaiter().GetResult(); };
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 61), default)).Outcome);
        Assert.Equal(1, hub.Inner.Writes); // External writer only.
        Assert.Equal(Outcome.Unknown, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.ClientDisconnected, default)).Result.Outcome);
        Assert.Equal(1, hub.Inner.Writes);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingOrMismatchedPhysicalBindingKeepsTheHubReadOnly(bool mismatched)
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); var binding = driver.BindingCandidate!;
        driver.Configure(mismatched ? binding with { Fingerprint = new('0', 64) } : null, [Config(driver, "fan1")]);
        Assert.NotNull(driver.Validate(new("fan1", DeviceOperation.SetDuty, 60)));
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default)).Outcome);
        Assert.Equal(0, hub.Inner.Writes);
        Assert.Equal(DeviceAvailability.Ready, driver.Observe()!.Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.Rpm).Availability);
    }
    [Fact] public async Task WiringChangesRejectNewOutputAndKeepTheOldObligationForReview()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 61), default);
        hub.Transform = s => s with { Channels = s.Channels.Select(c => c.Id == "fan1" ? c with { ControlMode = "DC" } : c).ToArray() };
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 62), default)).Outcome);
        Assert.Equal(Outcome.Unknown, Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.DeviceChanged, default)).Result.Outcome);
        Assert.Equal(1, hub.Inner.Writes); Assert.Single(Journal(driver, dir.Path).Pending);
    }
    [Fact] public async Task AmbiguousOrMissingNativeIdentityNeverOpensAnAlternativeHub()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        hub.Candidates = [hub.Identity, hub.Identity with { Path = "other-path" }];
        await Assert.ThrowsAsync<IOException>(() => driver.InitializeAsync());
        hub.Candidates = [hub.Identity with { Id = "other-id" }]; await Assert.ThrowsAsync<IOException>(() => driver.InitializeAsync());
        Assert.Empty(hub.Opened); Assert.Null(driver.Observe());
    }
    [Fact] public async Task AHubWithoutSerialCannotReuseTheBindingFromAnEarlierConnection()
    {
        using var dir = new ServiceTestDirectory(); var identity = new HubIdentity("path-bound", "Hub", null, "same-usb-path");
        var adapter = new DemoHub(identity) { DelayMs = 0 };
        await using var driver = new HubDeviceDriver(Installation, identity.Id, adapter, dir.Path);
        await driver.InitializeAsync(); var previous = driver.BindingCandidate!;
        driver.Configure(previous, [Config(driver, "fan1")]); Assert.Null(driver.Validate(new("fan1", DeviceOperation.SetDuty, 60)));
        await driver.InitializeAsync(); Assert.NotEqual(previous, driver.BindingCandidate);
        Assert.NotNull(driver.Validate(new("fan1", DeviceOperation.SetDuty, 60))); Assert.Equal(0, adapter.Writes);
    }
    [Fact] public async Task IdenticalModelsUseDistinctServiceIdsAndOnlyTheSelectedDeviceIsWritten()
    {
        using var dir = new ServiceTestDirectory(); var a = new Hub("serial-a"); var b = new Hub("serial-b");
        var first = await Ready(a, dir.Path); var second = await Ready(b, dir.Path);
        await using var runtime = new DeviceControlRuntime([first, second], ["user"], new TestLog());
        var hello = runtime.OpenSession("user"); var state = runtime.State(); Assert.Equal(2, state.Inventory.Devices.Length);
        var endpoint = state.Endpoints.Single(e => e.Target == new EndpointReference(second.DeviceId, "fan3"));
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetDuty,
            state.Inventory.TopologyRevision, [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], 32);
        var result = await runtime.ExecuteAsync(request);
        Assert.True(result.Success); Assert.Equal(50, Assert.Single(result.Result!.Targets).ReportedDuty);
        Assert.Equal(0, a.Inner.Writes); Assert.Equal(1, b.Inner.Writes);
        await runtime.DisconnectAsync(hello.Session);
        Assert.Equal(0, a.Inner.Writes); Assert.Equal(2, b.Inner.Writes); Assert.Null(runtime.State().LifecycleError);
    }
    [Fact] public void JournalRejectsIdentityChangesTamperingAndStaleClear()
    {
        using var dir = new ServiceTestDirectory(); var journal = new HubEndpointRecoveryJournal(dir.Path, "device"); var fingerprint = new string('a', 64);
        var old = journal.Prepare("hub", fingerprint, "wire", "fan1", 65, 45, 60);
        var current = journal.Prepare("hub", fingerprint, "wire", "fan1", 65, 60, 70);
        Assert.Throws<IOException>(() => journal.ConfirmReleased(old));
        Assert.Throws<IOException>(() => journal.Prepare("hub", fingerprint, "wire", "fan1", 55, 70, 80));
        Assert.Throws<IOException>(() => journal.Prepare("other-hub", fingerprint, "wire", "fan2", 55, 70, 80));
        Assert.Throws<IOException>(() => journal.Prepare("hub", fingerprint, "wire", "fan1", 65, 99, 80));
        Assert.Equal(current, Assert.Single(journal.Pending));
        var path = Assert.Single(Directory.GetFiles(dir.Path));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"exitDuty\":65", "\"exitDuty\":12"));
        Assert.Throws<IOException>(() => new HubEndpointRecoveryJournal(dir.Path, "device"));
    }
    [Fact] public async Task UnwritableRecoveryRecordPreventsAnyOutput()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); await using var driver = await Ready(hub, dir.Path);
        Directory.CreateDirectory(Path.Combine(dir.Path, "hub-endpoints-" + SettingsTransfer.Hash(driver.DeviceId).ToLowerInvariant() + ".json"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default));
        Assert.Equal(0, hub.Inner.Writes);
    }
}
