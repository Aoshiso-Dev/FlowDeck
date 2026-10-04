using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbManagedDeviceProviderTests
{
    private const string Installation = "d3f516653d764da181e6f5a74bcae5c9";
    private sealed class Close(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed class Child(List<string> events) : IOpenRgbSuspendedProcess
    {
        // The SDK below is an in-process model. No OpenRGB executable or hardware is used.
        public uint ProcessId => (uint)Environment.ProcessId;
        public bool HasExited { get; set; }
        public bool RefuseExit, FailResume;
        public IDisposable AttachJob() => new Close(() => { events.Add("job-close"); if (!RefuseExit) HasExited = true; });
        public void Resume() { events.Add("resume"); if (FailResume) throw new IOException("resume failed"); }
        public Task CloseInputAsync() => Task.CompletedTask;
        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(HasExited);
        public void Kill() { events.Add("kill"); if (!RefuseExit) HasExited = true; }
        public void Dispose() => events.Add("child-closed");
    }
    private sealed class Detection(List<string> events, Child child) : IOpenRgbDetectionScope
    {
        public string[] ResourceDomains => ["usb:fixture-board"];
        public bool ResourcesClosed { get; private set; }
        public bool RouteAvailable = true, FailPrepare, FailRestore, RejectInventory, FailBind;
        public int Prepares, Restores, Inventories;
        private bool prepared;
        public void CheckRoute() { if (!RouteAvailable) throw new IOException("native route owns the device"); }
        public void AttachHelper(OpenRgbOwnedProcess helper) { Assert.True(prepared); Assert.Equal(child.ProcessId, helper.ProcessId); }
        public void CheckControl() => CheckRoute();
        public OpenRgbStartupProfile BindStartup(OpenRgbStartupProfile template)
        {
            Assert.True(prepared); events.Add("bind-paths");
            if (FailBind) throw new IOException("captured paths unavailable");
            return template.WithCapturedAsrockPaths([@"\\?\hid#fixture-captured"]);
        }
        public Task PrepareAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Prepares++; events.Add("detection-captured"); prepared = true;
            if (FailPrepare) throw new IOException("snapshot incomplete");
            events.Add("native-handles-closed"); return Task.CompletedTask;
        }
        public void CheckInventory(OpenRgbSdkController[] controllers)
        {
            Assert.True(prepared); Inventories++; events.Add("inventory");
            if (RejectInventory) throw new IOException("unselected conflicting device");
        }
        public ValueTask DisposeAsync()
        {
            if (ResourcesClosed) return ValueTask.CompletedTask;
            if (events.Contains("resume")) Assert.True(child.HasExited);
            Restores++; events.Add("detection-restore");
            if (FailRestore) throw new IOException("native restore pending");
            ResourcesClosed = true; return ValueTask.CompletedTask;
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public readonly List<string> Events = [];
        public readonly Child Child;
        public readonly Detection Detection;
        public readonly OpenRgbSdkModelServer Server;
        public readonly OpenRgbManagedDeviceProvider Provider;
        public int Starts;
        public Rig()
        {
            var device = OpenRgbLightingPlanTests.Split() with { Serial = "FD-MANAGED-001", ActiveMode = 1 };
            Child = new(Events); Detection = new(Events, Child); Server = new(device);
            var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, Server.Port);
            using var settings = JsonDocument.Parse(profile.Settings); var root = settings.RootElement;
            Server.Detectors = root.GetProperty("Detectors").GetRawText(); Server.Drivers = root.GetProperty("Drivers").GetRawText();
            Server.ServerSettings = root.GetProperty("Server").GetRawText();
            foreach (var key in new[] { "Client", "ProfileManager", "QMKOpenRGBDevices", "QMKVialRGBDevices", "ASRock Polychrome USB" })
                Server.StartupSettings[key] = root.GetProperty(key).GetRawText();
            Server.BeforeWrite = _ => { Events.Add("sdk-write"); return Task.CompletedTask; };
            var snapshots = new[] { new OpenRgbSdkSnapshot(new(Guid.Empty, 1, device.Id, OpenRgbSdkProtocol.Structure(device)), device, 1) };
            var selected = OpenRgbDeviceIdentities.Resolve(snapshots, new Dictionary<OpenRgbSdkTarget, string>()).Values
                .Select(d => DeviceIdentity.Create(Installation, OpenRgbDeviceProvider.Provider, d.Native)).ToArray();
            Provider = new(Installation, directory.Path, "managed-boot", profile, Detection, selected, async (bound, release) =>
            {
                using var actual = JsonDocument.Parse(bound.Settings);
                Server.StartupSettings["ASRock Polychrome USB"] = actual.RootElement.GetProperty("ASRock Polychrome USB").GetRawText();
                Starts++; Events.Add("start");
                return await OpenRgbOwnedProcess.StartAsync(new(), new Close(() => Events.Add("image-closed")), () => { }, _ => Child, release);
            });
        }
        public OpenRgbDeviceDriver Driver => Assert.IsType<OpenRgbDeviceDriver>(Assert.Single(Provider.CachedDrivers));
        public async Task Open()
        {
            await Provider.PollAsync(default); await Provider.PollAsync(default);
            var setup = Driver.DescribeSetup();
            Driver.ConfigureLighting(Driver.BindingCandidate,
                setup.Select(p => new LightingEndpointConfiguration(new(Driver.DeviceId, p.EndpointId), true, new(LightingExitAction.RestoreOriginal))),
                setup.Select(p => new RegisteredEndpoint(p.EndpointId, p.EndpointId, EndpointKind.Lighting,
                    Setup: new(LedCount: p.DetectedLeds, LedCountConfirmed: true))));
        }
        public async Task Write()
        {
            var result = await Driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: new("fixed", [new(0, 0, 255)])), default);
            Assert.Equal(Outcome.WrittenUnverified, result.Outcome);
        }
        public async ValueTask DisposeAsync()
        {
            Child.RefuseExit = false; Detection.FailRestore = false; Detection.RouteAvailable = true;
            Server.IgnoreWriteNumber = 0;
            try { await Provider.DisposeAsync(); }
            finally { await Server.DisposeAsync(); directory.Dispose(); }
        }
    }

    [Fact] public async Task ConstructionAndUnusedShutdownNeverCaptureOrStartHardware()
    {
        await using var r = new Rig(); Assert.Empty(r.Events); Assert.Equal(0, r.Starts);
        Assert.Equal(new[] { "sdk:openrgb-loopback", "usb:fixture-board" }, r.Provider.ResourceDomains);
        await r.Provider.DisposeAsync(); await r.Provider.DisposeAsync();
        Assert.True(r.Provider.ResourcesClosed); Assert.Equal(0, r.Starts); Assert.Equal(0, r.Detection.Prepares);
        Assert.Empty(r.Server.Wire.Requests);
    }
    [Fact] public async Task AConflictingDirectRouteIsRejectedBeforeDetectionAndProcessCreation()
    {
        await using var r = new Rig(); r.Detection.RouteAvailable = false;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Equal(0, r.Starts); Assert.Equal(0, r.Detection.Prepares); Assert.Empty(r.Server.Wire.Requests);
    }
    [Fact] public async Task FailedPreparationRetainsRecoveryAndNeverStartsOrRetriesDetection()
    {
        await using var r = new Rig(); r.Detection.FailPrepare = true;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Equal(1, r.Detection.Prepares); Assert.Equal(0, r.Starts);
        await r.Provider.DisposeAsync(); Assert.Equal(1, r.Detection.Restores); Assert.True(r.Provider.ResourcesClosed);
    }
    [Fact] public async Task SdkRestorationPrecedesHelperExitAndNativeDetectionRestoration()
    {
        await using var r = new Rig(); await r.Open(); var original = OpenRgbLightingPlan.State(r.Server.Devices[41]); await r.Write();
        Assert.True(r.Events.IndexOf("native-handles-closed") < r.Events.IndexOf("start"));
        Assert.True(r.Events.IndexOf("native-handles-closed") < r.Events.IndexOf("bind-paths"));
        Assert.True(r.Events.IndexOf("bind-paths") < r.Events.IndexOf("start"));
        Assert.True(r.Events.IndexOf("resume") < r.Events.IndexOf("inventory"));
        await r.Provider.DisposeAsync();
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(r.Server.Devices[41])));
        Assert.True(r.Events.LastIndexOf("sdk-write") < r.Events.IndexOf("kill"));
        Assert.True(r.Events.IndexOf("child-closed") < r.Events.IndexOf("detection-restore"));
        Assert.True(r.Provider.ResourcesClosed); Assert.Equal(1, r.Starts);
    }
    [Fact] public async Task FailedBindingDoesNotStartAndRetainsDetectionRecovery()
    {
        await using var r = new Rig(); r.Detection.FailBind = true;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Equal(1, r.Detection.Prepares); Assert.Equal(0, r.Starts); Assert.Empty(r.Server.Wire.Requests);
        await r.Provider.DisposeAsync(); Assert.Equal(1, r.Detection.Restores);
    }
    [Fact] public async Task FailedSdkRestorationKeepsTheHelperAndDefersNativeRestorationUntilRetry()
    {
        await using var r = new Rig(); await r.Open(); await r.Write(); r.Server.IgnoreWriteNumber = r.Server.WriteCount + 1;
        await Assert.ThrowsAsync<AggregateException>(() => r.Provider.DisposeAsync().AsTask());
        Assert.False(r.Provider.ResourcesClosed); Assert.False(r.Child.HasExited); Assert.Equal(0, r.Detection.Restores);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => r.Provider.PollAsync(default));
        r.Server.IgnoreWriteNumber = 0; await r.Provider.DisposeAsync();
        Assert.True(r.Provider.ResourcesClosed); Assert.Equal(1, r.Starts); Assert.Equal(1, r.Detection.Restores);
    }
    [Fact] public async Task UnknownHelperExitCannotReopenNativeHardwareOrRepeatCompletedSdkRestoration()
    {
        await using var r = new Rig(); await r.Open(); await r.Write(); r.Child.RefuseExit = true;
        await Assert.ThrowsAsync<AggregateException>(() => r.Provider.DisposeAsync().AsTask());
        Assert.False(r.Provider.ResourcesClosed); Assert.Equal(0, r.Detection.Restores); Assert.True(r.Driver.ResourcesClosed);
        var writes = r.Server.WriteCount; r.Child.RefuseExit = false; await r.Provider.DisposeAsync();
        Assert.Equal(writes, r.Server.WriteCount); Assert.True(r.Provider.ResourcesClosed); Assert.Equal(1, r.Starts);
    }
    [Fact] public async Task FailedNativeRestorationRemainsOwnedAfterHelperExitAndCanRetry()
    {
        await using var r = new Rig(); await r.Open(); await r.Write(); r.Detection.FailRestore = true;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.DisposeAsync().AsTask());
        Assert.True(r.Child.HasExited); Assert.False(r.Provider.ResourcesClosed);
        var writes = r.Server.WriteCount; r.Detection.FailRestore = false;
        await Task.WhenAll(r.Provider.DisposeAsync().AsTask(), r.Provider.DisposeAsync().AsTask());
        Assert.True(r.Provider.ResourcesClosed); Assert.Equal(2, r.Detection.Restores); Assert.Equal(writes, r.Server.WriteCount);
    }
    [Fact] public async Task StartupFailureRetainsItsChildUntilExitBeforeRestoringTheDetectionCapture()
    {
        await using var r = new Rig(); r.Child.FailResume = true; r.Child.RefuseExit = true;
        var error = await Assert.ThrowsAsync<DeviceStartupException>(() => r.Provider.PollAsync(default));
        Assert.False(error.ResourcesClosed); Assert.Equal(0, r.Detection.Restores);
        await Assert.ThrowsAnyAsync<Exception>(() => r.Provider.DisposeAsync().AsTask());
        Assert.Equal(0, r.Detection.Restores);
        r.Child.RefuseExit = false; await r.Provider.DisposeAsync();
        Assert.True(error.ResourcesClosed); Assert.True(r.Provider.ResourcesClosed); Assert.Equal(1, r.Starts);
    }
    [Fact] public async Task ChangedStartupSettingsStopNewWritesBeforeAnotherInventoryIsAccepted()
    {
        await using var r = new Rig(); await r.Open(); var inventories = r.Detection.Inventories;
        r.Server.StartupSettings["Client"] = "{\"clients\":[\"remote\"]}";
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        var result = await r.Driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: new("fixed", [new(255, 0, 0)])), default);
        Assert.NotEqual(Outcome.WrittenUnverified, result.Outcome); Assert.Equal(0, r.Server.WriteCount);
        Assert.Equal(inventories, r.Detection.Inventories);
    }
    [Fact] public async Task CompleteInventoryMustBeApprovedEvenIfSelectionIsUnchanged()
    {
        await using var r = new Rig(); r.Detection.RejectInventory = true;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.Equal(0, r.Server.WriteCount); Assert.Equal(1, r.Starts);
    }
    [Fact] public async Task DeadHelperIsNotSilentlyReplacedOnTheNextPoll()
    {
        await using var r = new Rig(); await r.Open(); r.Child.HasExited = true;
        await Assert.ThrowsAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Equal(1, r.Starts); Assert.Equal(1, r.Detection.Prepares); Assert.Equal(0, r.Server.WriteCount);
    }
    [Fact] public async Task ShutdownWaitsForTheActiveWriteBeforeAnyHelperOrNativeRelease()
    {
        await using var r = new Rig(); await r.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Server.BeforeWrite = async _ => { entered.TrySetResult(); await finish.Task; };
        var write = r.Write(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = r.Provider.DisposeAsync().AsTask();
        try
        {
            Assert.False(close.IsCompleted); Assert.False(r.Child.HasExited); Assert.Equal(0, r.Detection.Restores);
        }
        finally { finish.TrySetResult(); }
        await write; await close; Assert.True(r.Provider.ResourcesClosed);
    }
}
