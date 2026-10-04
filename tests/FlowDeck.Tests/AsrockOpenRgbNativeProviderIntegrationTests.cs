using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    // Uses the product managed provider AND real scope. Only its protected launcher is
    // substituted, so tests start the mock-HID binary in their own temporary directory.
    private sealed class NativeProviderRig : IAsyncDisposable
    {
        public readonly NativeSdkRig Wire;
        public readonly OpenRgbManagedDeviceProvider Provider;
        public readonly OpenRgbStartupProfile Profile;
        public int Launches;
        public NativeProviderRig(bool selected = true, uint dropCommand = 0, bool invalidSettings = false, bool fullServer = false)
        {
            Wire = new(fullServer: fullServer);
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            Profile = new(OpenRgbStartupKind.AsrockPolychromeUsb, port);
            // The fixed mock DMI returns "Fixture board"; the actual native constructor adds
            // "ASRock ". Only this test identity is predicted; the provider verifies its scan.
            var basis = Wire.Native.Inventory().Single() with { Name = "ASRock Fixture board" };
            var snapshot = new OpenRgbSdkSnapshot(new(Guid.Empty, 1, basis.Id, OpenRgbSdkProtocol.Structure(basis)), basis, 1);
            var identity = OpenRgbDeviceIdentities.Resolve([snapshot], new Dictionary<OpenRgbSdkTarget, string>()).Values.Single();
            var id = DeviceIdentity.Create(Installation, OpenRgbDeviceProvider.Provider, identity.Native);
            Provider = new(Installation, Wire.Native.Directory.Path, "scope-boot", Profile, Wire.Native.Scope, selected ? [id] : [],
                async (bound, release) =>
                {
                    Launches++;
                    if (invalidSettings) OverrideServer();
                    return await Wire.Launch(bound, release, dropCommand, useProfilePort: true);
                });
            // No hardware or child has been opened yet. Native bind failure is surfaced,
            // never hidden by starting another helper or choosing another target.
            reservation.Stop();
        }
        public OpenRgbDeviceDriver Driver => Assert.IsType<OpenRgbDeviceDriver>(Assert.Single(Provider.CachedDrivers));
        public async Task Open()
        {
            await Provider.PollAsync(default);
            var setup = Driver.DescribeSetup();
            Driver.ConfigureLighting(Driver.BindingCandidate,
                setup.Select(p => new LightingEndpointConfiguration(new(Driver.DeviceId, p.EndpointId), true, new(LightingExitAction.Off))),
                setup.Select(p => new RegisteredEndpoint(p.EndpointId, p.EndpointId, EndpointKind.Lighting,
                    Setup: new(LedCount: p.DetectedLeds, LedCountConfirmed: true))));
        }
        public Task<DeviceDriverResult> Write() => Driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: NativeSdkBlue), default);
        public void OverrideServer()
        {
            var root = JsonNode.Parse(Profile.Settings)!; var server = root["Server"]!.DeepClone();
            server["all_controllers"] = true;
            File.WriteAllText(Path.Combine(Wire.Native.Directory.Path, "native-settings-override.json"), new JsonObject { ["Server"] = server }.ToJsonString());
        }
        public async Task<int> NativePacketCount() => (await Wire.ReadFile("native-state.json")).GetProperty("packets").GetArrayLength();
        public void AssertClosed(bool hadWrite)
        {
            Assert.True(Provider.ResourcesClosed); Assert.True(Wire.Native.Scope.ResourcesClosed); Assert.True(Wire.Native.Owner!.ResourcesClosed);
            Assert.All(Provider.CachedDrivers, d => Assert.True(Assert.IsType<OpenRgbDeviceDriver>(d).ResourcesClosed));
            Assert.Empty(System.IO.Directory.GetFiles(Wire.Native.Directory.Path, "openrgb-lighting-*.json"));
            Assert.Empty(System.IO.Directory.GetFiles(Wire.Native.Directory.Path, AsrockOpenRgbDetectionRecovery.FilePrefix + "*.json"));
            Assert.Equal(0xfd, Wire.Native.Boards[0].Device.Order);
            if (hadWrite) Assert.All(Wire.Native.Boards[0].Device.Raw.Values, bytes => Assert.Equal(0, bytes[0]));
        }
        public async ValueTask DisposeAsync()
        {
            Wire.Native.FailRestoreClose = false;
            await Provider.DisposeAsync(); await Wire.DisposeAsync();
        }
    }

    [Fact] public async Task NativeProviderValidatesActualSettingsBeforeAdoptionAndRunsLightingToShutdown()
    {
        await using var r = new NativeProviderRig(); await r.Open();
        Assert.Equal(1, r.Launches); r.Wire.Native.Scope.CheckControl();
        var state = await r.Wire.ReadFile("native-state.json");
        var reads = state.GetProperty("settingsRequests").EnumerateArray().Select(x => x.GetString()).ToArray();
        foreach (var key in new[] { "Detectors", "Detectors/schema", "Drivers", "Server", "Client", "ProfileManager", "QMKOpenRGBDevices", "QMKVialRGBDevices", "ASRock Polychrome USB" })
            Assert.True(reads.Count(x => x == key) >= 4, key + " was not observed twice before and after discovery");
        var result = await r.Write(); Assert.True(result.Outcome == Outcome.WrittenUnverified, JsonSerializer.Serialize(result));
        await r.Provider.PollAsync(default); Assert.Equal(1, r.Launches);
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: true);
    }

    [Fact] public async Task NativeProviderUnselectedDeviceRemainsObservableButCannotWrite()
    {
        await using var r = new NativeProviderRig(selected: false); await r.Open(); var before = await r.NativePacketCount();
        var result = await r.Write(); Assert.Equal(Outcome.Rejected, result.Outcome);
        Assert.Equal(before, await r.NativePacketCount()); Assert.DoesNotContain(r.Wire.Requests, p => p.Origin.Sdk);
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: false);
    }

    [Fact] public async Task NativeProviderRejectsChangedStartupSettingsBeforePublishingInventory()
    {
        await using var r = new NativeProviderRig(invalidSettings: true);
        await Assert.ThrowsAnyAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Empty(r.Provider.CachedDrivers); Assert.Throws<IOException>(r.Wire.Native.Scope.CheckControl);
        var commands = (await r.Wire.ReadFile("native-state.json")).GetProperty("commands").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
        Assert.DoesNotContain(0u, commands); Assert.DoesNotContain(1u, commands);
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: false);
    }

    [Fact] public async Task NativeProviderSettingsChangeRevokesOperationsAndFallsBackToCapturedNativeRecovery()
    {
        await using var r = new NativeProviderRig(); await r.Open();
        Assert.Equal(Outcome.WrittenUnverified, (await r.Write()).Outcome); var before = await r.NativePacketCount();
        r.OverrideServer(); await Assert.ThrowsAnyAsync<IOException>(() => r.Provider.PollAsync(default));
        Assert.Equal(Outcome.Rejected, (await r.Write()).Outcome); Assert.Equal(before, await r.NativePacketCount());
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: true);
        Assert.All(Assert.Single(r.Provider.ReadShutdownRecoveries()).Results, value => Assert.Equal(Outcome.Confirmed, value.Result.Outcome));
    }

    [Fact] public async Task NativeProviderLostAcknowledgementClosesSdkThenChildThenRestoresNative()
    {
        await using var r = new NativeProviderRig(dropCommand: 1101); await r.Open();
        Assert.Equal(Outcome.Unknown, (await r.Write()).Outcome); Assert.NotNull(r.Wire.Record.Operation);
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: true);
        Assert.NotEmpty(r.Provider.ReadShutdownRecoveries());
    }

    [Fact] public async Task NativeProviderRetainsFailedNativeCloseAndRetriesWithoutAnotherHelper()
    {
        await using var r = new NativeProviderRig(dropCommand: 1101); await r.Open();
        Assert.Equal(Outcome.Unknown, (await r.Write()).Outcome); r.Wire.Native.FailRestoreClose = true;
        await Assert.ThrowsAnyAsync<Exception>(() => r.Provider.DisposeAsync().AsTask());
        Assert.False(r.Provider.ResourcesClosed); Assert.True(r.Wire.Native.Owner!.ResourcesClosed);
        Assert.Empty(r.Provider.ReadShutdownRecoveries());
        r.Wire.Native.FailRestoreClose = false;
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: true); Assert.Equal(1, r.Launches);
    }

    [Fact] public async Task ScopeControlRequiresCompletedCaptureDetectionAndAcceptedInventory()
    {
        await using var r = new Rig(); Assert.Throws<IOException>(r.Scope.CheckControl);
        await r.Scope.PrepareAsync(default); Assert.Throws<IOException>(r.Scope.CheckControl);
        r.Scope.AttachHelper(await r.Start()); Assert.Throws<IOException>(r.Scope.CheckControl);
        r.Detect(); Assert.Throws<IOException>(r.Scope.CheckControl);
        r.Scope.CheckInventory(r.Inventory()); r.Scope.CheckControl();
        Assert.Throws<IOException>(() => r.Scope.CheckInventory([])); Assert.Throws<IOException>(r.Scope.CheckControl);
        r.Scope.CheckInventory(r.Inventory()); r.Scope.CheckControl();
        await r.Owner!.DisposeAsync(); Assert.Throws<IOException>(r.Scope.CheckControl);
    }

    [Fact] public async Task AcceptedScopeDoesNotTransientlyRevokeControlDuringAnUnchangedInventoryRecheck()
    {
        await using var r = new Rig(); await r.Open(); r.Scope.CheckInventory(r.Inventory());
        var checkedDuringRevalidation = false;
        r.Enumerating = () =>
        {
            r.Enumerating = null; // Avoid reentering this callback through CheckControl's own guard.
            r.Scope.CheckControl(); checkedDuringRevalidation = true;
        };
        r.Scope.CheckInventory(r.Inventory()); Assert.True(checkedDuringRevalidation); r.Scope.CheckControl();
    }
}
