using FlowDeck.Core;

namespace FlowDeck.Tests;

public class DeviceConfigurationTests
{
    private static readonly EndpointReference Cpu = new("sensors", "cpu"), Gpu = new("sensors", "gpu"), Water = new("sensors", "water"),
        Fan = new("controller", "fan1"), Light = new("memory", "leds");
    private static CoolingSafety Safety => new(20, 50, 50, CoolingExitAction.FixedDuty, []);
    private static CoolingTemperatureProfile Profile => new("balanced", "標準", 1,
        [new(Cpu, [new(20, 20), new(100, 100)], 105, 90), new(Gpu, [new(20, 20), new(100, 100)], 105, 90),
         new(Water, [new(20, 20), new(45, 100)], 50, 40)], 3, 2);
    private static DeviceRegistrations Devices => new(2,
        [new("sensors", "温度", "sensors", DeviceKind.Other,
            [new("cpu", "CPU", EndpointKind.Sensor), new("gpu", "GPU", EndpointKind.Sensor), new("water", "水温", EndpointKind.Sensor)]),
         new("controller", "ケースファン", "test", DeviceKind.Controller, [new("fan1", "FAN1", EndpointKind.Cooling, "fan")]),
         new("memory", "メモリ", "test", DeviceKind.Memory, [new("leds", "照明", EndpointKind.Lighting)])]);
    private static DeviceBindings Bindings => new(2, "b9b8f0e5454d4b8396cd0c0d8d230c50", [new("controller", "serial:1", "known-v1", new('a', 64))]);
    private static CoolingConfiguration Cooling => new(2, [new(Fan, true, Safety, [Profile])], []);
    private static LightingConfiguration Lighting => new(2, [], [], [], [], []);
    [Fact]
    public void ArbitraryTemperatureSourcesUseMaximumDemandRatherThanHighestTemperature()
    {
        var values = new Dictionary<EndpointReference, double> { [Cpu] = 80, [Gpu] = 50, [Water] = 40 };
        Assert.Equal(84, DeviceConfigurationRules.Calculate(Profile, Safety, values));
        values.Remove(Water); Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Calculate(Profile, Safety, values));
        values[Water] = double.NaN; Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Calculate(Profile, Safety, values));
    }
    [Fact]
    public void RequestedGroupDutyIsClampedIndependentlyAndDoesNotImplyFanStop()
    {
        Assert.Equal(50, DeviceConfigurationRules.EffectiveGroupDuty(50, Safety));
        Assert.Equal(60, DeviceConfigurationRules.EffectiveGroupDuty(50, Safety with { Minimum = 60, Fallback = 100, Exit = 70 }));
        Assert.Equal(20, DeviceConfigurationRules.EffectiveGroupDuty(0, Safety));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.EffectiveGroupDuty(101, Safety));
    }
    [Fact]
    public void ValidGraphRetainsAnUnboundRgbOnlyDeviceWithoutInventingTemperature()
    {
        DeviceConfigurationRules.Validate(Devices, Bindings, Cooling, Lighting,
            new(2, [new(Cpu, DeviceFeature.Temperature, "CPU", "temperature"), new(Fan, DeviceFeature.Rpm, "ケース", "fan")]));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings, Cooling, Lighting,
            new(2, [new(Light, DeviceFeature.Temperature, "メモリ温度", "temperature")])));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings, Cooling, Lighting,
            new(2, [new(Light, DeviceFeature.Rpm, "メモリRPM", "fan")])));
    }
    [Fact]
    public void InvalidReferenceOrEndpointKindCannotBecomeAControlTarget()
    {
        var bad = Cooling with { Endpoints = [Cooling.Endpoints[0] with { Target = Light }] };
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings, bad, Lighting, new(2, [])));
        var missing = Profile with { Curves = [Profile.Curves[0] with { Sensor = new("unknown", "temperature") }] };
        bad = Cooling with { Endpoints = [Cooling.Endpoints[0] with { Profiles = [missing] }] };
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings, bad, Lighting, new(2, [])));
    }
    [Fact]
    public void DraftsCanBeSavedButCannotResumeUntilFullyConfiguredAndBound()
    {
        var draft = Profile with { Curves = [], DescentDelaySeconds = null };
        var cooling = Cooling with { Endpoints = [Cooling.Endpoints[0] with { Profiles = [draft], WiringConfirmed = false }] };
        DeviceConfigurationRules.Validate(Devices, new(2, null, []), cooling, Lighting, new(2, []));
        Assert.NotNull(DeviceConfigurationRules.ProfileReadiness(draft, Safety));
        cooling = cooling with { Endpoints = [cooling.Endpoints[0] with { SavedPolicy = new(FanMode.Auto, Profile: draft), ResumeOnStart = true }] };
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, new(2, null, []), cooling, Lighting, new(2, [])));
        cooling = Cooling with { Endpoints = [Cooling.Endpoints[0] with { SavedPolicy = new(FanMode.Auto, Profile: Profile, SafetySnapshot: Safety), ResumeOnStart = true }] };
        DeviceConfigurationRules.Validate(Devices, Bindings, cooling, Lighting, new(2, []));
    }
    [Fact]
    public void SceneGroupsAndBrightnessCannotContainAnUnrelatedOrDuplicateEndpoint()
    {
        var program = new LightingProgram("breathing", [new(20, 50, 200)]);
        var scene = new DeviceLightingScene("blue", "ブルー", [], [new([Light], program)]);
        var group = new EndpointGroup("memory", "メモリ", EndpointKind.Lighting, [Light]);
        var lighting = Lighting with { Scenes = [scene], Groups = [group], Adjustments = [new(group.Id, 50, false, scene)] };
        DeviceConfigurationRules.Validate(Devices, Bindings, Cooling, lighting, new(2, []));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings, Cooling,
            lighting with { Scenes = [scene with { Individual = [new(Light, program)] }] }, new(2, [])));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings, Cooling,
            lighting with { Groups = [group with { Targets = [Fan] }] }, new(2, [])));
    }
    [Fact]
    public void AStoredPolicyNeedsItsOwnMatchingSafetySnapshotBeforeStartupResume()
    {
        var endpoint = Cooling.Endpoints[0] with { SavedPolicy = new(FanMode.Manual, 60) };
        Assert.NotNull(DeviceConfigurationRules.SavedPolicyReadiness(endpoint));
        DeviceConfigurationRules.Validate(Devices, Bindings, Cooling with { Endpoints = [endpoint] }, Lighting, new(2, []));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings,
            Cooling with { Endpoints = [endpoint with { ResumeOnStart = true }] }, Lighting, new(2, [])));
        endpoint = endpoint with { SavedPolicy = endpoint.SavedPolicy with { SafetySnapshot = Safety } };
        Assert.Null(DeviceConfigurationRules.SavedPolicyReadiness(endpoint));
        endpoint = endpoint with { Safety = Safety with { Minimum = 40 } };
        Assert.NotNull(DeviceConfigurationRules.SavedPolicyReadiness(endpoint));
        DeviceConfigurationRules.Validate(Devices, Bindings, Cooling with { Endpoints = [endpoint] }, Lighting, new(2, []));
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings,
            Cooling with { Endpoints = [endpoint with { ResumeOnStart = true }] }, Lighting, new(2, [])));
    }
    [Fact]
    public void SavedSafetyCannotReferToAnAbsentSensorEvenWhenTheEditedSafetyDoesNotUseIt()
    {
        var saved = Safety with { ManualProtection = [new(new("missing", "temperature"), 80, 70)] };
        var endpoint = Cooling.Endpoints[0] with { SavedPolicy = new(FanMode.Manual, 60, SafetySnapshot: saved) };
        Assert.Throws<ArgumentException>(() => DeviceConfigurationRules.Validate(Devices, Bindings,
            Cooling with { Endpoints = [endpoint] }, Lighting, new(2, [])));
    }
    [Fact]
    public void ARunningPolicyCapturesSafetyAndRejectsAConflictingSavedSnapshot()
    {
        var safety = Safety with { ManualProtection = [new(Cpu, 80, 70)], Stop = new([new(Gpu, 40)], 70, 2) };
        var engine = new DeviceCoolingPolicy(new(FanMode.Manual, 60), safety, 45);
        safety.ManualProtection[0] = new(Gpu, 95, 90); safety.Stop.Temperatures[0] = new(Cpu, 90);
        Assert.Equal(Cpu, engine.Desired.SafetySnapshot!.ManualProtection[0].Sensor);
        Assert.Equal(Gpu, engine.Desired.SafetySnapshot.Stop!.Temperatures[0].Sensor);
        Assert.Throws<ArgumentException>(() => new DeviceCoolingPolicy(engine.Desired, safety, 45));
        Assert.Null(DeviceCoolingPolicy.Validate(engine.Desired, engine.Safety));
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1d)]
    public void InvalidTimingNeverBecomesReady(double delay)
        => Assert.False(DeviceConfigurationRules.ValidProfileDraft(Profile with { DescentDelaySeconds = delay }));
}
