using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceRegistryTests
{
    private const string Scope = "2382b12e49c14ef6b9fdd910ff4d9f6e";
    private static DeviceObservation Device(string serial = "a") => new(DeviceIdentity.Create(Scope, "test", serial), "test",
        "Same model", DeviceKind.Controller, IdentityConfidence.SerialBound, "usb:" + serial, "definition-v1", ["usb:" + serial],
        [new("fan1", "Fan", EndpointKind.Cooling, [new(DeviceFeature.FixedDuty, ImplementationSupport.Supported,
            VerificationEvidence.Automated, DeviceAvailability.Ready)])]);
    private static DeviceCommandTarget Target(DeviceInventory inventory, string? id = null)
    {
        var device = id is null ? inventory.Devices[0] : inventory.Devices.Single(d => d.Device.Id == id);
        return new(inventory.Epoch, device.Device.Id, "fan1", device.Generation, DeviceFeature.FixedDuty);
    }
    [Fact]
    public void SameModelReorderingAndRenamingKeepIdentityAndGeneration()
    {
        var registry = new DeviceRegistry(); var a = Device(); var b = Device("b");
        var first = registry.Reconcile([a, b]); var target = Target(first, a.Id);
        var next = registry.Reconcile([b, a with { Name = "Front" }]);
        Assert.Equal(2, next.Devices.Length); Assert.Null(registry.Check(target));
        Assert.Equal(target.Generation, next.Devices.Single(d => d.Device.Id == a.Id).Generation);
        Assert.NotEqual(a.Id, DeviceIdentity.Create(Guid.NewGuid().ToString("N"), "test", "a"));
    }
    [Fact]
    public void DisconnectReconnectAndForgetCannotReuseOldCommands()
    {
        var registry = new DeviceRegistry(); var device = Device(); var old = Target(registry.Reconcile([device]));
        Assert.False(registry.ForgetDisconnected(device.Id));
        registry.Reconcile([]); Assert.NotNull(registry.Check(old));
        var disconnectedGeneration = registry.Snapshot.Devices[0].Generation;
        registry.Reconcile([]); Assert.Equal(disconnectedGeneration, registry.Snapshot.Devices[0].Generation);
        Assert.True(registry.ForgetDisconnected(device.Id));
        var current = Target(registry.Reconcile([device]));
        Assert.True(current.Generation > old.Generation); Assert.NotNull(registry.Check(old)); Assert.Null(registry.Check(current));
        Assert.NotNull(new DeviceRegistry().Check(current));
    }
    [Fact]
    public void TopologyChangesInvalidateTargetsButAvailabilityDoesNot()
    {
        var registry = new DeviceRegistry(); var device = Device(); var target = Target(registry.Reconcile([device]));
        var endpoint = device.Endpoints[0]; var feature = endpoint.Capabilities[0];
        registry.Reconcile([device with { Endpoints = [endpoint with { Capabilities = [feature with { Availability = DeviceAvailability.Conflict }] }] }]);
        Assert.Equal(target.Generation, registry.Snapshot.Devices[0].Generation); Assert.NotNull(registry.Check(target));
        registry.Reconcile([device]); Assert.Null(registry.Check(target));
        registry.Reconcile([device with { Endpoints = [endpoint, endpoint with { Id = "fan2" }] }]);
        Assert.NotNull(registry.Check(target));
    }
    [Theory]
    [InlineData(ImplementationSupport.Unknown)] [InlineData(ImplementationSupport.Unsupported)]
    [InlineData(ImplementationSupport.Candidate)] [InlineData(ImplementationSupport.Experimental)]
    public void UnreleasedFeaturesCannotBecomeWritable(ImplementationSupport support)
    {
        var device = Device(); device.Endpoints[0].Capabilities[0] = device.Endpoints[0].Capabilities[0] with { Support = support };
        var registry = new DeviceRegistry(); Assert.NotNull(registry.Check(Target(registry.Reconcile([device]))));
    }
    [Fact]
    public void CallerMutationAndInvalidInventoryCannotChangeRegisteredState()
    {
        var registry = new DeviceRegistry(); var device = Device(); var snapshot = registry.Reconcile([device]); var target = Target(snapshot);
        device.Endpoints[0].Capabilities[0] = device.Endpoints[0].Capabilities[0] with { Availability = DeviceAvailability.Failed };
        snapshot.Devices[0].Device.Endpoints[0] = snapshot.Devices[0].Device.Endpoints[0] with { Id = "wrong" };
        Assert.Null(registry.Check(target));
        var before = registry.Snapshot.Revision;
        Assert.Throws<ArgumentException>(() => registry.Reconcile([Device(), Device()]));
        Assert.Throws<ArgumentException>(() => registry.Reconcile([Device() with { Endpoints = [new("bad", "Bad", EndpointKind.Sensor,
            [new(DeviceFeature.FixedDuty, ImplementationSupport.Supported, VerificationEvidence.None, DeviceAvailability.Ready)])] }]));
        Assert.Equal(before, registry.Snapshot.Revision); Assert.Null(registry.Check(target));
    }
    [Fact]
    public void HostScopePersistsAndCorruptionDoesNotSilentlyRebind()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = DeviceInstallationIdentity.LoadOrCreate(directory);
            Assert.Equal(id, DeviceInstallationIdentity.LoadOrCreate(directory));
            Assert.NotEqual(id, DeviceInstallationIdentity.LoadOrCreate(Path.Combine(directory, "other")));
            File.WriteAllText(Path.Combine(directory, "device-installation-id"), "invalid");
            Assert.Throws<IOException>(() => DeviceInstallationIdentity.LoadOrCreate(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
