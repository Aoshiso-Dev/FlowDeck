using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Hub = FlowDeck.Tests.HubLightingDriverTests.Hub;

namespace FlowDeck.Tests;

public sealed class HubAutomaticControlTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private static readonly LightingProgram Blue = new("fixed", [new(20, 80, 240)]);
    private static CoolingEndpointConfiguration Fan(HubDeviceDriver d, int? minimum = 30) =>
        new(new(d.DeviceId, "fan1"), false, new(minimum, 100, 65, CoolingExitAction.FixedDuty, []), []);
    private static RegisteredEndpoint Port(string accessory = "RGB", int? count = 24) =>
        new("led1", "Light", EndpointKind.Lighting, Setup: new(accessory, count, false));
    private static void Prepare(HubDeviceDriver driver, CoolingEndpointConfiguration[]? fans = null,
        LightingEndpointConfiguration[]? lights = null, RegisteredEndpoint[]? ports = null,
        PhysicalDeviceBinding? previous = null) => driver.ConfigureCurrentConnection(driver.BindingCandidate!, previous,
            fans ?? [], lights ?? [], ports ?? []);
    private static DeviceAvailability Availability(HubDeviceDriver driver, string endpoint, DeviceFeature feature) =>
        driver.Observe()!.Endpoints.Single(e => e.Id == endpoint).Capabilities.Single(c => c.Feature == feature).Availability;

    [Fact]
    public async Task KnownDetectedLightingIsPreparedWithoutRegistrationConfirmationOrOutput()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver);
        foreach (var channel in new[] { "led1", "led2", "led3" })
        {
            Assert.Equal(DeviceAvailability.Ready, Availability(driver, channel, DeviceFeature.FixedColor));
            Assert.True(driver.AllowsCurrentConnectionControl(channel, DeviceFeature.FixedColor));
            Assert.False(driver.AllowsCurrentConnectionControl(channel, DeviceFeature.FixedDuty));
            Assert.False(driver.AllowsCurrentConnectionControl(channel, DeviceFeature.Temperature));
        }
        Assert.False(driver.AllowsCurrentConnectionControl("fan1", DeviceFeature.FixedDuty));
        Assert.Null(driver.CoolingConfiguration("fan1"));
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes); Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Single(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
        await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default);
        Assert.Equal("off", hub.Colors["led1"].Mode); Assert.False(hub.Colors.ContainsKey("led2"));
    }

    [Fact]
    public async Task SavedActualSettingsDoNotNeedConfirmationFlagsOrAnOldConnectionBinding()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); var current = driver.BindingCandidate!;
        var color = new RgbColor(9, 8, 7);
        Prepare(driver, [Fan(driver) with { ResumeOnStart = true,
            SavedPolicy = new(FanMode.Manual, 75) }],
            [new(new(driver.DeviceId, "led1"), false, new(LightingExitAction.FixedColor, color))], [Port()],
            current with { Fingerprint = new('0', 64) });
        Assert.Equal(current, driver.CurrentControlBinding);
        Assert.True(driver.AllowsCurrentConnectionControl("fan1", DeviceFeature.FixedDuty));
        Assert.True(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.False(driver.AllowsCurrentConnectionControl("fan2", DeviceFeature.FixedDuty));
        Assert.False(driver.CoolingConfiguration("fan1")!.WiringConfirmed);
        Assert.False(driver.CoolingConfiguration("fan1")!.ResumeOnStart);
        Assert.Null(driver.CoolingConfiguration("fan1")!.SavedPolicy);
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
        Assert.Equal(Outcome.Confirmed, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 20), default)).Outcome);
        Assert.Equal(30, (await hub.ReadAsync()).Channels.Single(c => c.Id == "fan1").Duty);
        Assert.Empty(hub.Writes);
        Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default);
        Assert.Equal(color, hub.Colors["led1"].Colors[0]);
        Assert.Equal(65, (await hub.ReadAsync()).Channels.Single(c => c.Id == "fan1").Duty);
    }

    [Fact]
    public async Task ExactSavedBindingKeepsExplicitStartupIntentWithoutStartingItDuringPreparation()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync();
        var saved = Fan(driver) with { ResumeOnStart = true, SavedPolicy = new(FanMode.Manual, 75) };
        Prepare(driver, [saved], previous: driver.BindingCandidate);
        Assert.True(driver.CoolingConfiguration("fan1")!.ResumeOnStart);
        Assert.Equal(saved.SavedPolicy, driver.CoolingConfiguration("fan1")!.SavedPolicy);
        Assert.Equal(0, hub.Fans.Writes); Assert.Empty(hub.Writes); Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task MissingActualCoolingLimitsStayMissingInsteadOfAdoptingOneHundredPercent()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver, [Fan(driver, null)]);
        Assert.Null(driver.CoolingConfiguration("fan1")!.Safety.Minimum);
        Assert.False(driver.AllowsCurrentConnectionControl("fan1", DeviceFeature.FixedDuty));
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default)).Outcome);
        Assert.Equal(0, hub.Fans.Writes); Assert.Empty(hub.Writes);
    }

    [Theory] [InlineData("missing")] [InlineData("wrong-accessory")] [InlineData("too-long")] [InlineData("saved")]
    public async Task UnknownAccessoriesRequireActualCountAndTheEncoderLimitNeverBecomesDetection(string kind)
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        hub.Transform = s => s with { Lighting = [new("led1", "Unknown (0x7f)", kind == "too-long" ? 48 : null)] };
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync();
        Prepare(driver, ports: kind == "missing" ? [] : [Port(kind == "wrong-accessory" ? "Other" : "Unknown (0x7f)")]);
        Assert.Equal(kind == "saved", driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        if (kind != "saved")
            Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task LegacyExplicitConfigurationAlsoUsesActualValuesInsteadOfConfirmationFlags()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync();
        driver.Configure(driver.BindingCandidate, [Fan(driver)]);
        driver.ConfigureLighting(driver.BindingCandidate, [new(new(driver.DeviceId, "led1"), false, new(LightingExitAction.Off))], [Port()]);
        Assert.Null(driver.Validate(new("fan1", DeviceOperation.SetDuty, 60)));
        Assert.Null(driver.Validate(new("led1", DeviceOperation.SetLighting, Lighting: Blue)));
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task ChangedConnectionCannotUseAnOldPreparedBinding()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver); var captured = driver.CurrentControlBinding!;
        hub.Transform = s => s with { Channels = s.Channels.Select(c => c.Id == "fan1" ? c with { ControlMode = "DC" } : c).ToArray() };
        await driver.RefreshAsync();
        Assert.Null(driver.CurrentControlBinding); Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.Throws<IOException>(() => driver.ConfigureCurrentConnection(captured, captured, [], [], []));
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task ReopeningTheSameSerialRequiresNewReadOnlyPreparationAndNeverResumesOutput()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver, [Fan(driver)]); var physical = driver.CurrentControlBinding;
        await driver.InitializeAsync();
        Assert.Equal(physical, driver.BindingCandidate); Assert.Null(driver.CurrentControlBinding);
        Assert.False(driver.AllowsCurrentConnectionControl("fan1", DeviceFeature.FixedDuty));
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default)).Outcome);
        Prepare(driver, [Fan(driver)]);
        Assert.True(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.True(driver.AllowsCurrentConnectionControl("fan1", DeviceFeature.FixedDuty));
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes); Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task AutomaticPreparationCannotReplaceAnOldLightingRecoveryObligation()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver);
        await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default);
        var journal = new HubLightingRecoveryJournal(dir.Path, driver.DeviceId); var old = Assert.Single(journal.Pending);
        hub.Transform = s => s with { Lighting = s.Lighting.Select(c => c.Id == "led1" ? c with { Accessory = "Replacement (0x3f)" } : c).ToArray() };
        await driver.RefreshAsync(); Assert.Throws<IOException>(() => Prepare(driver));
        Assert.Equal(old, Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        var released = Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.DeviceChanged, default));
        Assert.Equal(Outcome.Unknown, released.Result.Outcome); Assert.Single(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task RestoreKeepsCurrentConnectionAuthorityAfterAnUnconfirmedWriteButNotAfterReconnect()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver);
        hub.OmitResults = true;
        Assert.Equal(Outcome.Unknown, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.ColorFrames));
        Assert.True(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.RestoreLighting));
        Assert.False(driver.AllowsCurrentConnectionControl("fan1", DeviceFeature.RestoreLighting));
        Assert.False(driver.AllowsCurrentConnectionControl("missing", DeviceFeature.RestoreLighting));
        var pending = Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        Assert.Single(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
        await driver.InitializeAsync();
        Assert.Null(driver.CurrentControlBinding);
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.RestoreLighting));
        Assert.Equal(pending, Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        Assert.Single(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task RevokedConfigurationCannotRetainCurrentRestorationPermission()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver);
        Assert.True(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.RestoreLighting));
        driver.RevokeConfiguration();
        Assert.Null(driver.CurrentControlBinding);
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.RestoreLighting));
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task RemovingKnownLightingPreferencesKeepsOtherOwnersAndStartsNoOutput()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync();
        Prepare(driver, [Fan(driver)], [
            new(new(driver.DeviceId, "led1"), false, new(LightingExitAction.FixedColor, new(9, 8, 7))),
            new(new(driver.DeviceId, "led2"), false, new(LightingExitAction.FixedColor, new(1, 2, 3)))]);
        await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 60), default);
        await driver.ApplyAsync(new("led2", DeviceOperation.SetLighting, Lighting: Blue), default);
        var lightOwner = Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        var fanOwner = Assert.Single(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        driver.ConfigureLightingEndpoint(driver.BindingCandidate!, "led1", null, Port());
        Assert.True(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.Equal(lightOwner, Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        Assert.Equal(fanOwner, Assert.Single(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        Assert.Single(hub.Writes); Assert.Equal(1, hub.Fans.Writes); Assert.False(hub.Colors.ContainsKey("led1"));
        Assert.Equal(Outcome.WrittenUnverified, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Equal(LightingExitAction.Off, new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending.Single(p => p.Channel == "led1").Exit.Action);
        await driver.ApplyAsync(new("led1", DeviceOperation.StopLighting), default);
        Assert.Equal("off", hub.Colors["led1"].Mode);
        Assert.Equal(lightOwner, Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        Assert.Equal(fanOwner, Assert.Single(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        Assert.Equal(1, hub.Fans.Writes);
    }

    [Theory] [InlineData(null)] [InlineData(48)]
    public async Task RemovingSettingsDoesNotGuessAnUnknownOrUnsupportedLedCount(int? detected)
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        hub.Transform = s => s with { Lighting = [new("led1", "Unknown (0x7f)", detected)] };
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver, ports: [Port("Unknown (0x7f)")]);
        Assert.Equal(detected is null, driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        driver.ConfigureLightingEndpoint(driver.BindingCandidate!, "led1", null, Port("Unknown (0x7f)"));
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task RemovingSettingsCannotReplaceAnOwnedExitOrRestoreAnOldConnection()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path);
        await driver.InitializeAsync(); Prepare(driver);
        await driver.ApplyAsync(new("led1", DeviceOperation.SetLighting, Lighting: Blue), default);
        var original = Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        Assert.Throws<IOException>(() => driver.ConfigureLightingEndpoint(driver.BindingCandidate!, "led1", null, Port()));
        Assert.Equal(original, Assert.Single(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending));
        Assert.Single(hub.Writes);
        await driver.ApplyAsync(new("led1", DeviceOperation.StopLighting), default);
        var writes = hub.Writes.Count;
        await driver.InitializeAsync();
        driver.ConfigureLightingEndpoint(driver.BindingCandidate!, "led1", null, Port());
        Assert.Null(driver.CurrentControlBinding);
        Assert.False(driver.AllowsCurrentConnectionControl("led1", DeviceFeature.FixedColor));
        Assert.Equal(writes, hub.Writes.Count); Assert.Empty(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        Assert.Equal(0, hub.Fans.Writes);
    }

    [Fact]
    public async Task PreparingAnExpiredObservationDoesNotPublishCurrentControl()
    {
        using var dir = new ServiceTestDirectory(); var hub = new Hub(); var clock = new TestClock();
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, dir.Path, clock);
        await driver.InitializeAsync(); clock.Advance(3.1);
        Assert.Throws<IOException>(() => Prepare(driver)); Assert.Null(driver.CurrentControlBinding);
        Assert.Empty(hub.Writes); Assert.Equal(0, hub.Fans.Writes);
    }
}
