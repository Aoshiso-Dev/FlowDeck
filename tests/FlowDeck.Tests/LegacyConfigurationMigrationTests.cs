using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LegacyConfigurationMigrationTests
{
    private static (Dictionary<string, byte[]> Files, LegacyMigrationBindings Matching) Sample()
    {
        var settings = DemoHub.CreateSettings(); var memory = MemoryLighting.Identity(0x71);
        settings = settings with { CpuFan = new(true, 40), Startup = new(true, true),
            LastConfirmedPolicy = new(FanMode.Auto, Profile: settings.Profiles[1], Limits: settings.Limits),
            LightingPorts = new() { ["led1"] = new(DemoHub.Identity.Id, HubLighting.Preset("blue"), Outcome.WrittenUnverified) },
            Dashboard = new([new("cpu", "selected", "temperature", "CPU温度"), new("nzxt", "fan1", "fan", "前面"), new("motherboard", "board-rpm", "fan", "ポンプ", Role: "pump")]) };
        var references = new List<MigratedReference>();
        EndpointReference Ref(string source, string legacyDevice, string legacyEndpoint, string device, string endpoint)
        {
            var target = new EndpointReference(device, endpoint); references.Add(new(source, legacyDevice, legacyEndpoint, target)); return target;
        }
        foreach (var id in settings.Hardware.Fans!) Ref("hub", DemoHub.Identity.Id, id, "hub", id);
        Ref("hub", DemoHub.Identity.Id, "led1", "hub", "led1");
        Ref("temperature", "local", "demo/cpu", "sensors", "cpu"); Ref("temperature", "local", "demo/gpu", "sensors", "gpu");
        Ref("board-control", "motherboard", "cpu-fan1", "board", "fan"); Ref("rpm", "motherboard", "board-rpm", "board", "fan");
        Ref("rgb", "board-identity", "zone:2", "board", "argb"); Ref("rgb", memory, "zone:0", "memory", "light");
        var devices = new DeviceRegistrations(2,
            [new("hub", "NZXT", "nzxt-control-hub", DeviceKind.Controller,
                [.. settings.Hardware.Fans.Select(id => new RegisteredEndpoint(id, id, EndpointKind.Cooling)), new("led1", "照明", EndpointKind.Lighting)]),
             new("sensors", "温度", "lhm", DeviceKind.Other, [new("cpu", "CPU", EndpointKind.Sensor), new("gpu", "GPU", EndpointKind.Sensor)]),
             new("board", "マザー", "motherboard", DeviceKind.Motherboard, [new("fan", "CPU_FAN1", EndpointKind.Cooling), new("argb", "ARGB", EndpointKind.Lighting)]),
             new("memory", "メモリ", "ene-memory", DeviceKind.Memory, [new("light", "照明", EndpointKind.Lighting)])]);
        var matching = new LegacyMigrationBindings(devices, new(2, Guid.NewGuid().ToString("N"),
            [new("hub", DemoHub.Identity.Id, "hub-v1", new('a', 64)), new("board", "board-id", "board-v1", new('b', 64))]),
            references.ToArray(), references.Where(r => r.Source == "board-control" || r.Source == "hub" && r.LegacyEndpointId.StartsWith("fan")).Select(r => r.Target).ToArray(), true,
            ConfirmedTemperatureMatches: references.Where(r => r.Source == "temperature").Select(r => r.Target).ToArray());
        var sync = new SceneSyncLight(DemoHub.Identity.Id, ["led1"], "board-identity", [2], "breathing", new(30, 60, 210), 2, [memory]);
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "ブルー", [], [], sync);
        var group = new LightingGroup(Guid.NewGuid().ToString("N"), "全体", [new(DemoHub.Identity.Id, "led1")], [new("board-identity", 2), new(memory, 0)]);
        var files = new Dictionary<string, byte[]>();
        void File<T>(string name, T value) => files[name] = JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json);
        File("settings.json", settings); File("lighting-scenes.json", new LightingSceneCollection(Scenes: [scene]));
        File("lighting-groups.json", new LightingGroupCollection(Groups: [group], SyncTargets: group.Board));
        File("lighting-names.json", new LightingNameCollection(Names: [new(new(Hub: group.Hub[0]), "フロント")]));
        File("lighting-adjustment.json", new LightingAdjustmentState(BaseScene: scene, Brightness: 50));
        return (files, matching);
    }
    [Fact]
    public void VerifiedSameMachineRetainsCalibrationAndSavedProfileSnapshot()
    {
        var sample = Sample(); var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        var hubFans = converted.Cooling.Endpoints.Where(c => c.Target.DeviceId == "hub").ToArray();
        Assert.Equal(5, hubFans.Length); Assert.All(hubFans, f => { Assert.True(f.WiringConfirmed); Assert.True(f.ResumeOnStart); Assert.Equal(25, f.Safety.Minimum); });
        Assert.Equal("balanced", hubFans[0].SavedPolicy!.Profile!.Id);
        Assert.True(DeviceConfigurationRules.SameSafety(hubFans[0].Safety, hubFans[0].SavedPolicy!.SafetySnapshot!));
        var boardFan = converted.Cooling.Endpoints.Single(c => c.Target.DeviceId == "board"); Assert.True(boardFan.WiringConfirmed);
        Assert.Equal(40, boardFan.Safety.Minimum); Assert.Equal(CoolingExitAction.ReleaseToDevice, boardFan.Safety.ExitAction); Assert.Null(boardFan.Safety.Exit);
        Assert.Equal(3, converted.Cooling.ProfileTemplates!.Length);
    }
    [Fact]
    public void EditedLimitsDoNotReplaceTheLastAppliedSnapshotOrSilentlyResumeIt()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        static SensorCurve? EditedCurve(SensorCurve? curve) => curve is null ? null : curve with
            { Points = curve.Points.Select(p => p with { Duty = Math.Max(60, p.Duty) }).ToArray() };
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with { Limits = new(60, 100, 80),
            Profiles = settings.Profiles.Select(p => p with { Cpu = EditedCurve(p.Cpu), Gpu = EditedCurve(p.Gpu) }).ToArray() }, Contract.Json);
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        Assert.All(converted.Cooling.Endpoints.Where(e => e.Target.DeviceId == "hub"), e =>
        {
            Assert.Equal(60, e.Safety.Minimum); Assert.Equal(80, e.Safety.Exit);
            Assert.Equal(25, e.SavedPolicy!.SafetySnapshot!.Minimum); Assert.Equal(70, e.SavedPolicy.SafetySnapshot.Exit);
            Assert.False(e.ResumeOnStart);
        });
        Assert.Contains(converted.Migration.Notices, n => n.Contains("安全条件と現在の設定が異なります"));
        var roundTrip = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(converted));
        Assert.Equal(25, roundTrip.Cooling.Endpoints[0].SavedPolicy!.SafetySnapshot!.Minimum);
    }
    [Fact]
    public void ProtectionAndStopInputsComeFromTheSavedPolicyRatherThanEditedSensorSelections()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        var limits = settings.Limits with { ProtectManualCpu = true, CpuOverheat = 80, CpuResumeBelow = 70,
            AllowStop = true, Stop = new(40, 45, 75, 3) };
        settings = settings with { Limits = limits, CpuSensorId = "demo/gpu", GpuSensorId = "demo/cpu",
            LastConfirmedPolicy = new(FanMode.Manual, 60, Limits: limits, CpuSensorId: "demo/cpu", GpuSensorId: "demo/gpu") };
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings, Contract.Json);
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching); var fan = converted.Cooling.Endpoints[0];
        Assert.Equal(new EndpointReference("sensors", "gpu"), fan.Safety.ManualProtection[0].Sensor);
        Assert.Equal(new EndpointReference("sensors", "cpu"), fan.SavedPolicy!.SafetySnapshot!.ManualProtection[0].Sensor);
        Assert.Equal(new EndpointReference("sensors", "gpu"), fan.SavedPolicy.SafetySnapshot.Stop!.Temperatures[1].Sensor);
        Assert.Equal(45, fan.SavedPolicy.SafetySnapshot.Stop.Temperatures[1].Below); Assert.False(fan.ResumeOnStart);
    }
    [Fact]
    public void LegacyPolicyWithoutLimitsIsKeptAsADraftAndCannotInheritEditedLimitsForStartup()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with { LastConfirmedPolicy = new(FanMode.Manual, 60) }, Contract.Json);
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        Assert.All(converted.Cooling.Endpoints.Where(e => e.Target.DeviceId == "hub"), e =>
        { Assert.Equal(60, e.SavedPolicy!.Duty); Assert.Null(e.SavedPolicy.SafetySnapshot); Assert.False(e.ResumeOnStart); });
        Assert.Contains(converted.Migration.Notices, n => n.Contains("最後に適用した安全条件がありません"));
    }
    [Fact]
    public void SharedSceneGroupAliasesAndDisplayOrderingBecomeEndpointReferences()
    {
        var sample = Sample(); var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        var animation = Assert.Single(Assert.Single(converted.Lighting.Scenes).Synchronized);
        Assert.Equal(3, animation.Targets.Length); Assert.Equal("breathing", animation.Program.EffectId);
        Assert.Equal(animation.Targets.ToHashSet(), converted.Lighting.Groups[0].Targets.ToHashSet());
        Assert.Equal("フロント", converted.Lighting.Names[0].Name); Assert.Equal(50, converted.Lighting.Adjustments[0].Brightness);
        Assert.Equal(new[] { "CPU温度", "前面", "ポンプ" }, converted.Dashboard.Items.Select(i => i.Name));
        Assert.Equal("pump", converted.Dashboard.Items[2].Role);
        Assert.Equal("native/nzxt-control-hub/fixed", Assert.Single(converted.Lighting.Preferences!).Program.EffectId);
    }
    [Fact]
    public void DifferentMachineClearsBindingsAndConfirmationWhileKeepingColorsAndCurves()
    {
        var sample = Sample(); var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching with { SameInstallation = false });
        Assert.Empty(converted.Bindings.Devices); Assert.Null(converted.Bindings.InstallationId);
        Assert.All(converted.Cooling.Endpoints, f => { Assert.False(f.WiringConfirmed); Assert.False(f.ResumeOnStart); Assert.Null(f.SavedPolicy); });
        Assert.False(converted.Preferences.Startup.ResumeControl); Assert.NotEmpty(converted.Lighting.Scenes); Assert.NotEmpty(converted.Cooling.ProfileTemplates!);
    }
    [Fact]
    public void AStoredWiringCheckAloneCannotRetainOutputAuthorization()
    {
        var sample = Sample(); var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching with { ConfirmedCoolingMatches = [] });
        Assert.All(converted.Cooling.Endpoints, f => { Assert.False(f.WiringConfirmed); Assert.False(f.ResumeOnStart); });
        Assert.NotEmpty(converted.Migration.Notices);
    }
    [Fact]
    public void MissingMappingAbortsRatherThanSelectingAnotherDevice()
    {
        var sample = Sample();
        Assert.Throws<IOException>(() => LegacyConfigurationMigration.Convert(sample.Files, sample.Matching with
            { References = sample.Matching.References.Where(r => r.Source != "rgb").ToArray() }));
    }
    [Fact]
    public void LedCountAccessoriesSensorNamesAndM5SelectionSurviveLocalConversion()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with { Motherboard = new(
            [new("board-rpm", "ラジエーターポンプ", true)], true, 6743,
            [new("saved-board-zone", "ケースのARGB", 80, new(14, 11, 22, 33))]) }, Contract.Json);
        var matching = sample.Matching with { References = [.. sample.Matching.References,
            new("board-sensor", "motherboard", "board-rpm", new("board", "fan")),
            new("rgb-zone-setting", "local", "saved-board-zone", new("board", "argb"))],
            ConfirmedLightingMatches = [new("hub", "led1"), new("board", "argb")] };
        var converted = LegacyConfigurationMigration.Convert(sample.Files, matching);
        var led = converted.Devices.Devices.Single(d => d.Id == "hub").Endpoints.Single(e => e.Id == "led1");
        Assert.Equal(new EndpointSetup("DEMO RGB", 24, true), led.Setup);
        var board = converted.Devices.Devices.Single(d => d.Id == "board");
        Assert.Equal(new EndpointSetup("ケースのARGB", 80, true), board.Endpoints.Single(e => e.Id == "argb").Setup);
        var fan = board.Endpoints.Single(e => e.Id == "fan"); Assert.Equal("ラジエーターポンプ", fan.Name); Assert.True(fan.Setup!.ShowOnM5);
        Assert.Equal("native/asrock-usb/14", converted.Lighting.Preferences!.Single(p => p.Target == new EndpointReference("board", "argb")).Program.EffectId);
        Assert.Equal(new DeviceProviderPreferences(true, 6743), converted.Preferences.Providers);
        var portable = DeviceConfigurationCodec.Portable(converted);
        Assert.All(portable.Devices.Devices.SelectMany(d => d.Endpoints).Where(e => e.Setup is not null), e => Assert.False(e.Setup!.LedCountConfirmed));
        Assert.Equal(80, portable.Devices.Devices.Single(d => d.Id == "board").Endpoints.Single(e => e.Id == "argb").Setup!.LedCount);
        Assert.False(portable.Preferences.Providers!.OpenRgbEnabled);
    }
    [Fact]
    public void LedCountWithoutMatchingPhysicalProofRemainsAReferenceValue()
    {
        var sample = Sample(); var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        var led = converted.Devices.Devices.Single(d => d.Id == "hub").Endpoints.Single(e => e.Id == "led1");
        Assert.Equal(24, led.Setup!.LedCount); Assert.False(led.Setup.LedCountConfirmed);
    }
    [Fact]
    public void LegacyGlobalLightingFillsOnlyPortsWithoutAnIndividualSetting()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with { Lighting = new(Effect: HubLighting.Preset("white"), Channels: ["led1"]) }, Contract.Json);
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        Assert.Equal(HubLighting.Preset("white").Colors, converted.Lighting.DefaultProgram!.Colors);
        Assert.Equal(HubLighting.Preset("blue").Colors, Assert.Single(converted.Lighting.Preferences!).Program.Colors);
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with { LightingPorts = [], Lighting = new(Effect: HubLighting.Preset("white"), Channels: ["led1"]) }, Contract.Json);
        converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        Assert.Equal(HubLighting.Preset("white").Colors, Assert.Single(converted.Lighting.Preferences!).Program.Colors);
    }
    [Fact]
    public void UncustomizedDashboardRequiresAndRetainsTheAcquiredDefaultCatalog()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with { Dashboard = new() }, Contract.Json);
        Assert.Throws<IOException>(() => LegacyConfigurationMigration.Convert(sample.Files, sample.Matching));
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching with { DefaultDashboard = settings.Dashboard.Items });
        Assert.Equal(new[] { "CPU温度", "前面", "ポンプ" }, converted.Dashboard.Items.Select(i => i.Name));
    }
    [Fact]
    public void NotificationThresholdsAndMissingBehaviorUseExplicitEndpointReferences()
    {
        var sample = Sample(); var rules = new NotificationRule[] {
            new(Guid.NewGuid().ToString("N"), new("sensor", "cpu", "demo/cpu", "temperature"), "CPU", 85, 4),
            new(Guid.NewGuid().ToString("N"), new("nzxt", DemoHub.Identity.Id, "fan1", "fan"), "前面", 400, 50, Missing: false),
            new(Guid.NewGuid().ToString("N"), new("motherboard", "Taichi", "board-rpm", "fan"), "ポンプ", 600, IgnoreStopped: false) };
        sample.Files["notifications.json"] = JsonSerializer.SerializeToUtf8Bytes(new NotificationSettings(DelaySeconds: 10, Rules: rules), Contract.Json);
        Assert.Throws<IOException>(() => LegacyConfigurationMigration.Convert(sample.Files, sample.Matching));
        var matching = sample.Matching with { References = [.. sample.Matching.References, new("notification", "Taichi", "board-rpm", new("board", "fan"))] };
        var converted = LegacyConfigurationMigration.Convert(sample.Files, matching); var notifications = converted.Preferences.Notifications;
        Assert.Equal(10, notifications.DelaySeconds); Assert.Equal(3, notifications.Rules!.Length);
        Assert.Equal(new EndpointReference("sensors", "cpu"), notifications.Rules[0].Target);
        Assert.Equal(4, notifications.Rules[0].Hysteresis); Assert.False(notifications.Rules[1].Missing); Assert.False(notifications.Rules[2].IgnoreStopped);
        Assert.Equal(DeviceFeature.Rpm, notifications.Rules[2].Measurement);
    }
    [Fact]
    public void AnUnknownPortStateDoesNotResurrectAnOlderGlobalEffect()
    {
        var sample = Sample(); var settings = JsonSerializer.Deserialize<AppSettings>(sample.Files["settings.json"], Contract.Json)!;
        sample.Files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings with {
            Lighting = new(LastSent: "blue", Outcome: Outcome.WrittenUnverified),
            LightingPorts = new() { ["led1"] = new(DemoHub.Identity.Id, null, Outcome.Unknown) } }, Contract.Json);
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        Assert.Empty(converted.Lighting.Preferences!); Assert.Equal(HubLighting.Preset("blue").Colors, converted.Lighting.DefaultProgram!.Colors);
    }
    [Fact]
    public void MemoryPerLedPalettePreservesCanonicalRgbOrderForEveryLed()
    {
        var sample = Sample(); var memory = MemoryLighting.Identity(0x71);
        var wire = Enumerable.Range(0, 8).SelectMany(i => new byte[] { (byte)(10 + i), (byte)(30 + i), (byte)(20 + i) }).ToArray();
        var effect = new BoardLightSetting(1, 10, 20, 30, MemoryPalette: Convert.ToHexString(wire));
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "メモリ", [], [new(memory, 0, effect)]);
        sample.Files["lighting-scenes.json"] = JsonSerializer.SerializeToUtf8Bytes(new LightingSceneCollection(Scenes: [scene]), Contract.Json);
        var converted = LegacyConfigurationMigration.Convert(sample.Files, sample.Matching);
        var program = Assert.Single(Assert.Single(converted.Lighting.Scenes).Individual).Program;
        Assert.Equal("native/ene-memory/1", program.EffectId);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => new RgbColor(10 + i, 20 + i, 30 + i)), program.LedColors!);
    }
    [Fact]
    public void MigrationStagesAWholeValidatedPackageAndOriginalByteBackup()
    {
        var sample = Sample(); var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var activation = LegacyConfigurationMigration.Stage(directory, sample.Files, sample.Matching);
            var store = DeviceConfigurationCodec.Store(directory); Assert.Null(store.ReadActive()); store.Activate(activation);
            var value = DeviceConfigurationCodec.Read(store.ReadActive()!.Files); Assert.Equal(6, value.Cooling.Endpoints.Length);
            foreach (var (name, original) in sample.Files)
                Assert.Equal(original, System.IO.File.ReadAllBytes(Path.Combine(directory, "generations", activation.Target.Id, "legacy", name)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
