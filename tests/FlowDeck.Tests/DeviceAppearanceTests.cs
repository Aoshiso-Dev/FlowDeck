using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceAppearanceTests
{
    [Theory]
    [InlineData("circle", 80)]
    [InlineData("bar", 4200)]
    [InlineData("number", 3000)]
    public void PresentationSettingsSurviveSerializationWithoutChangingIdentityOrMeasuredValue(string indicator, double maximum)
    {
        var target = new EndpointReference("hub", "fan1");
        var item = new DeviceDashboardItem(target, DeviceFeature.Rpm, "前面", "fan", Indicator: indicator, DisplayMaximum: maximum);
        var copy = Contract.Copy(item); Assert.Equal(item, copy); Assert.True(DeviceDashboardDraft.Valid(new(2, [copy])));
        DeviceCapability cap = new(DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready);
        var inventory = new DeviceInventory("epoch", 1, [new(new("hub", "demo", "Hub", DeviceKind.Controller, IdentityConfidence.SerialBound,
            "serial", null, ["bus"], [new("fan1", "FAN1", EndpointKind.Cooling, [cap])]), 1, true)], 1);
        var state = new DeviceServiceState(inventory, [], false, null, [new(target, DeviceFeature.Rpm, 6500, 0, false)]);
        var reading = DeviceDashboard.Read(copy, new(2, []), state, 0, true);
        Assert.Equal(6500, reading.Value); Assert.Equal(maximum, reading.ScaleMaximum); Assert.Equal(indicator, reading.Indicator);
        Assert.Equal(DeviceDashboard.Key(target, DeviceFeature.Rpm), reading.Item.SensorId);
        var missing = DeviceDashboard.Read(copy, new(2, []), state with { Telemetry = [] }, 0, true);
        Assert.Null(missing.Value); Assert.Equal(maximum, missing.ScaleMaximum);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1000001)]
    public void InvalidDisplayRangeCannotEnterSavedConfiguration(double maximum)
    {
        var item = new DeviceDashboardItem(new("hub", "fan1"), DeviceFeature.Rpm, "FAN1", "fan", DisplayMaximum: maximum);
        Assert.False(DeviceDashboardDraft.Valid(new(2, [item])));
    }
    [Fact] public void UnconfiguredReadingsKeepOldDefaultsAndScaleNeverRewritesMeasurements()
    {
        var fan = new DashboardReading(new("demo", "fan", "fan", "FAN1"), 7234, null, "取得中", "監視", "DEMO", "key");
        Assert.True(fan.Bar); Assert.Equal(8000, fan.ScaleMaximum);
        var bounded = fan with { DisplayMaximum = 3000, Indicator = "circle" };
        Assert.True(bounded.Circular); Assert.False(bounded.Inline); Assert.Equal(3000, bounded.ScaleMaximum); Assert.Equal(7234, bounded.Value);
        var temperature = new DashboardReading(new("demo", "cpu", "temperature", "CPU"), 52, null, "取得中", "", "CPU", "key");
        Assert.True(temperature.Circular); Assert.Equal(100, temperature.ScaleMaximum);
        Assert.False(DeviceDashboardDraft.Valid(new(2, [new(new("hub", "fan1"), DeviceFeature.Rpm, "fan", "fan", Indicator: "unknown")])));
    }
    [Fact] public async Task LocalThemeAndWindowWinOverServiceSeedsAndDoNotRewriteOtherFiles()
    {
        using var directory = new ServiceTestDirectory(); var path = Path.Combine(directory.Path, "ui.json");
        var store = new DeviceUiPreferencesStore(path);
        Assert.Equal("light", store.Load(new("light")).Theme); Assert.False(File.Exists(path));
        var saved = new UiSettings("dark", 125, 200, 1234, 850);
        await store.SaveAsync(saved); Assert.Equal(saved, new DeviceUiPreferencesStore(path).Load(new("system")));
        Assert.Equal(new[] { "ui.json" }, Directory.GetFiles(directory.Path).Select(Path.GetFileName));
        await store.SaveAsync(saved with { Theme = "system" }); Assert.Equal("system", store.Load().Theme);
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":1,\"version\":1,\"window\":{}}")]
    [InlineData("{\"version\":9,\"window\":{\"theme\":\"light\"}}")]
    public void InvalidLocalFileIsPreservedUntilExplicitSave(string contents)
    {
        using var directory = new ServiceTestDirectory(); var path = Path.Combine(directory.Path, "ui.json"); File.WriteAllText(path, contents);
        var store = new DeviceUiPreferencesStore(path); Assert.Equal("system", store.Load().Theme);
        Assert.NotNull(store.LoadWarning); Assert.Equal(contents, File.ReadAllText(path));
    }
    [Fact] public async Task InvalidLocalThemeOrCoordinatesCannotReplaceTheLastValidPreference()
    {
        using var directory = new ServiceTestDirectory(); var store = new DeviceUiPreferencesStore(Path.Combine(directory.Path, "ui.json"));
        await store.SaveAsync(new("dark"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("unknown")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("light", Left: double.NaN)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("light", Width: double.PositiveInfinity)));
        Assert.Equal("dark", store.Load().Theme);
    }
}
