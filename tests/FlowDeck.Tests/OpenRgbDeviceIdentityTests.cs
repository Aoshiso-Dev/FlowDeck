using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbDeviceIdentityTests
{
    private static OpenRgbSdkSnapshot Snapshot(OpenRgbSdkController device) => new(new(Guid.NewGuid(), 1, device.Id, OpenRgbSdkProtocol.Structure(device)), device, 1);
    [Theory]
    [InlineData("")] [InlineData("unknown")] [InlineData("SERIAL")] [InlineData("0000000000")]
    public void PlaceholderSerialUsesLocationOnlyWithinItsConnection(string serial)
    {
        var snapshot = Snapshot(OpenRgbLightingPlanTests.Device() with { Serial = serial });
        var identity = OpenRgbDeviceIdentities.Resolve([snapshot], new Dictionary<OpenRgbSdkTarget, string>())[snapshot.Target.ControllerId];
        Assert.True(identity.Bindable); Assert.Equal(IdentityConfidence.ConnectionBound, identity.Confidence); Assert.StartsWith("location:", identity.Native);
    }
    [Fact]
    public void DuplicateSerialUsesDistinctLocationsWithoutClaimingManufacturedIdentity()
    {
        var a = Snapshot(OpenRgbLightingPlanTests.Device() with { Serial = "FD-0001", Location = "USB-A" });
        var b = Snapshot(a.Controller with { Id = 700, Location = "USB-B" });
        var values = OpenRgbDeviceIdentities.Resolve([a, b], new Dictionary<OpenRgbSdkTarget, string>());
        Assert.NotEqual(values[41].Native, values[700].Native); Assert.All(values.Values, value => Assert.Equal(IdentityConfidence.ConnectionBound, value.Confidence));
    }
    [Fact]
    public void ReorderingZonesPreservesEndpointIdsAndDuplicateNamesAreNotGuessed()
    {
        var device = OpenRgbLightingPlanTests.Split(); var before = OpenRgbDeviceIdentities.Surfaces(device);
        var reordered = OpenRgbDeviceIdentities.Surfaces(device with { Zones = device.Zones.Reverse().ToArray() });
        Assert.Equal(before.Single(p => p.Zone == 0).Endpoint, reordered.Single(p => p.Zone == 1).Endpoint);
        var ambiguous = OpenRgbDeviceIdentities.Surfaces(device with { Zones = [device.Zones[0], device.Zones[0]] });
        Assert.Equal("device", Assert.Single(ambiguous).Endpoint);
    }
    [Fact]
    public void UnknownIdentityIsStableOnlyForTheSameObservedConnectionTarget()
    {
        var device = OpenRgbLightingPlanTests.Device() with { Serial = "", Location = "" }; var first = Snapshot(device);
        var cache = new Dictionary<OpenRgbSdkTarget, string>();
        var a = OpenRgbDeviceIdentities.Resolve([first], cache)[41]; var b = OpenRgbDeviceIdentities.Resolve([first], cache)[41];
        Assert.Equal(a, b); Assert.False(a.Bindable);
        var c = OpenRgbDeviceIdentities.Resolve([Snapshot(device)], cache)[41]; Assert.NotEqual(a.Native, c.Native); Assert.Single(cache);
    }
}
