using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceCatalogPresentationTests
{
    private static DeviceWorkspaceSnapshot Snapshot()
    {
        DeviceEndpoint[] endpoints = [
            new("temp", "CPU", EndpointKind.Sensor, [new(DeviceFeature.Temperature, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)]),
            new("fan1", "FAN1", EndpointKind.Cooling, [new(DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready),
                new(DeviceFeature.FixedDuty, ImplementationSupport.Unsupported, VerificationEvidence.None, DeviceAvailability.Ready, "この端子は回転数の監視のみです")]),
            new("led1", "LED1", EndpointKind.Lighting, [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.SourceReview, DeviceAvailability.NeedsSetup, "LED数を確認してください"),
                new(DeviceFeature.ColorFrames, ImplementationSupport.Supported, VerificationEvidence.FamilyHardware, DeviceAvailability.Conflict, "ほかの照明ソフトが使用しています")])];
        var observed = new DeviceObservation("board", "demo", "元の機種名", DeviceKind.Motherboard, IdentityConfidence.SerialBound,
            "USB/mock", "known-board", ["mock"], endpoints);
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(new string('a', 32));
        config = config with { Devices = new(2, [new("board", "自分のマザー", "demo", DeviceKind.Motherboard,
            endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Id == "fan1" ? "背面ファン" : e.Name, e.Kind)).ToArray())]) };
        return new(new(new("epoch", 1, [new(observed, 1, true)], 1), [], false, null,
            [new(new("board", "temp"), DeviceFeature.Temperature, 52, 0, false), new(new("board", "fan1"), DeviceFeature.Rpm, 900, 0, false)]),
            new("generation", config, []), 0, true, null, Session: "session");
    }
    [Fact] public void SeparatesImplementationEvidenceAndCurrentAvailabilityWithoutInferringControlFromRpm()
    {
        var s = Snapshot(); var row = Assert.Single(DeviceCatalogPresentation.Build(s));
        Assert.Equal("自分のマザー", row.Name); Assert.Contains("温度：利用可", row.Status); Assert.Contains("回転数：利用可", row.Status);
        Assert.Contains("ファン操作：未対応", row.Status); Assert.Contains("照明：要確認", row.Status);
        var fan = row.Endpoints.Single(e => e.Id == "fan1"); Assert.Equal("背面ファン", fan.Name);
        var rpm = fan.Capabilities.Single(c => c.Feature == DeviceFeature.Rpm); Assert.True(rpm.Available); Assert.Equal("自動試験", rpm.Evidence);
        var duty = fan.Capabilities.Single(c => c.Feature == DeviceFeature.FixedDuty);
        Assert.False(duty.Available); Assert.Equal("未対応", duty.Support); Assert.Equal("未検証", duty.Evidence); Assert.NotEqual("利用可", duty.Availability);
        Assert.Equal("この端子は回転数の監視のみです", duty.Reason);
        var light = row.Endpoints.Single(e => e.Id == "led1").Capabilities;
        Assert.Equal("対応定義あり", light[0].Support); Assert.Equal("ソース照合", light[0].Evidence); Assert.Equal("設定待ち", light[0].Availability);
        Assert.Equal("LED数を確認してください", light[0].Reason);
        Assert.Equal("同系統の実機", light[1].Evidence); Assert.Equal("競合", light[1].Availability);
        Assert.Contains("元の機種名", s.State.Inventory.Devices.Select(d => d.Device.Name));
        Assert.Equal("FAN1", s.State.Inventory.Devices[0].Device.Endpoints.Single(e => e.Id == "fan1").Name);
    }
    [Theory]
    [InlineData("stale", "状態の更新待ち")]
    [InlineData("invalid-age", "状態の更新待ち")]
    [InlineData("exit", "終了処理中")]
    [InlineData("releasing", "終了処理中")]
    [InlineData("configuring", "設定の反映中")]
    [InlineData("lifecycle", "サービスの復旧待ち")]
    public void UnavailableSnapshotCannotRetainReadyLabelsButKeepsEvidenceAndLastReasons(string condition, string expected)
    {
        var s = Snapshot(); var next = condition switch {
            "stale" => s with { ElapsedSeconds = 3 },
            "invalid-age" => s with { ElapsedSeconds = double.NaN }, "exit" => s with { ExitAttempted = true },
            "releasing" => s with { State = s.State with { Releasing = true } },
            "configuring" => s with { State = s.State with { ConfigurationChanging = true } },
            _ => s with { State = s.State with { LifecycleError = "mock recovery failure" } } };
        var row = Assert.Single(DeviceCatalogPresentation.Build(next)); Assert.Equal(expected, row.Status);
        Assert.All(row.Endpoints.SelectMany(e => e.Capabilities), c => { Assert.False(c.Available); Assert.Equal(expected, c.Availability); Assert.True(c.HasReason); });
        var light = row.Endpoints.Single(e => e.Id == "led1").Capabilities[0];
        Assert.Equal("ソース照合", light.Evidence); Assert.Contains("前回の理由：LED数を確認してください", light.Reason);
    }
    [Fact] public void MissingMeasurementOrStaleSensorDoesNotReportMonitoringAsReady()
    {
        var s = Snapshot(); s = s with { State = s.State with { Telemetry = [
            new(new("board", "temp"), DeviceFeature.Temperature, 52, 3, false),
            new(new("board", "fan1"), DeviceFeature.Rpm, null, 0, false, "読み取り失敗") ] } };
        var row = Assert.Single(DeviceCatalogPresentation.Build(s)); Assert.Contains("温度：更新停止", row.Status); Assert.Contains("回転数：未取得", row.Status);
        var rpm = row.Endpoints.Single(e => e.Id == "fan1").Capabilities[0]; Assert.False(rpm.Available); Assert.Equal("読み取り失敗", rpm.Reason);
        s = s with { State = s.State with { Telemetry = [new(new("board", "fan1"), DeviceFeature.Rpm, 0, 0, false)] } };
        rpm = Assert.Single(DeviceCatalogPresentation.Build(s)).Endpoints.Single(e => e.Id == "fan1").Capabilities[0];
        Assert.True(rpm.Available); Assert.Equal("利用可", rpm.Availability);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("disconnected")]
    [InlineData("service")]
    public void UnavailableDeviceIsHiddenAndReconnectRestoresSavedNamesWithoutChangingConfiguration(string condition)
    {
        var s = Snapshot(); var before = SettingsTransfer.Hash(s.Configuration.Configuration);
        var next = condition switch {
            "service" => s with { Connected = false },
            "disconnected" => s with { State = s.State with { Inventory = s.State.Inventory with {
                Devices = [s.State.Inventory.Devices[0] with { Connected = false }] } } },
            _ => s with { State = s.State with { Inventory = s.State.Inventory with { Devices = [] } } } };
        Assert.Empty(DeviceCatalogPresentation.Build(next));
        Assert.Equal(before, SettingsTransfer.Hash(next.Configuration.Configuration));
        var restored = Assert.Single(DeviceCatalogPresentation.Build(next with { Connected = true, State = s.State }));
        Assert.Equal("自分のマザー", restored.Name); Assert.Equal("背面ファン", restored.Endpoints.Single(e => e.Id == "fan1").Name);
    }
    [Fact] public void SavedOnlyEndpointsAreHiddenWithoutRemovingTheirConfiguration()
    {
        var s = Snapshot(); var observed = s.State.Inventory.Devices[0];
        var next = s with { State = s.State with { Inventory = s.State.Inventory with {
            Devices = [observed with { Device = observed.Device with { Endpoints = [observed.Device.Endpoints[0]] } }] } } };
        var row = Assert.Single(DeviceCatalogPresentation.Build(next)); Assert.Equal("temp", Assert.Single(row.Endpoints).Id);
        Assert.Equal(3, next.Configuration.Configuration.Devices.Devices[0].Endpoints.Length);
        Assert.Equal(3, Assert.Single(DeviceCatalogPresentation.Build(next with { State = s.State })).Endpoints.Length);
    }
    [Fact] public void UnpluggingOneDeviceDoesNotDowngradeAnotherWithTheSameName()
    {
        var s = Snapshot(); var original = s.State.Inventory.Devices[0];
        var second = original with { Device = original.Device with { Id = "another-board", Name = "自分のマザー", Endpoints = [original.Device.Endpoints[2]] } };
        s = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = [original with { Connected = false }, second] } } };
        var rows = DeviceCatalogPresentation.Build(s); var row = Assert.Single(rows);
        Assert.Equal("another-board", row.Id); Assert.Equal("設定待ち", row.Endpoints[0].Capabilities[0].Availability);
        var reversed = DeviceCatalogPresentation.Build(s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Reverse().ToArray() } } });
        Assert.Equal(rows.Select(d => d.Id), reversed.Select(d => d.Id));
    }
    [Fact] public void RgbOnlyMemoryDoesNotAcquireTemperatureOrFanCapabilitiesAndMixedLightingIsOnlyPartlyReady()
    {
        var s = Snapshot(); var original = s.State.Inventory.Devices[0];
        var memory = original with { Device = original.Device with { Kind = DeviceKind.Memory, Endpoints = [new("led1", "メモリ照明", EndpointKind.Lighting, [
            new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.MatchingHardware, DeviceAvailability.Ready),
            new(DeviceFeature.ColorFrames, ImplementationSupport.Unsupported, VerificationEvidence.None, DeviceAvailability.Ready)])] } };
        s = s with { Configuration = s.Configuration with { Configuration = s.Configuration.Configuration with { Devices = new(2, []) } },
            State = s.State with { Inventory = s.State.Inventory with { Devices = [memory] } } };
        var row = Assert.Single(DeviceCatalogPresentation.Build(s)); Assert.Equal("照明：一部利用可", row.Status);
        var endpoint = Assert.Single(row.Endpoints); Assert.Equal(2, endpoint.Capabilities.Length);
        Assert.DoesNotContain(endpoint.Capabilities, c => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.FixedDuty);
        Assert.Equal("一致構成の実機", endpoint.Capabilities[0].Evidence);
    }
}
