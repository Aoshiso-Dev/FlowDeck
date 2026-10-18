using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public class DeviceTransientAvailabilityTests
{
    private static DeviceObservation Light(string id, DeviceAvailability availability) => new(
        id, "test", id, DeviceKind.Memory, IdentityConfidence.ConnectionBound, "connection:" + id,
        "test-light-v1", ["bus:" + id],
        [new("rgb", "RGB", EndpointKind.Lighting,
            [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated, availability, "Readiness unavailable"),
             new(DeviceFeature.NativeEffect, ImplementationSupport.Supported, VerificationEvidence.Automated, availability, "Readiness unavailable"),
             new(DeviceFeature.ColorFrames, ImplementationSupport.Supported, VerificationEvidence.Automated, availability, "Readiness unavailable"),
             new(DeviceFeature.RestoreLighting, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)])]);

    [Fact]
    public void TemporaryReadFailureSurvivesStrictWireValidationAndDoesNotBlockStoppingAFailedPeer()
    {
        var transient = Light("transient", DeviceAvailability.TemporarilyUnavailable);
        var failed = Light("failed", DeviceAvailability.Failed);
        Assert.True(DeviceRegistry.Valid(transient));
        var registry = new DeviceRegistry();
        var inventory = registry.Reconcile([transient, failed]);
        var state = new DeviceServiceState(inventory, inventory.Devices.Select(d => new DeviceExecutionState(
            new(d.Device.Id, "rgb"), d.Generation, 0, Outcome.Unknown, Owned: d.Device.Id == "failed")).ToArray(), false, null);
        var hello = new DeviceServiceHello(2, inventory.Epoch, Guid.NewGuid().ToString("N"), 12, []);
        var reply = new DeviceServiceReply(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), true, State: state);
        var json = JsonSerializer.Serialize(reply, Contract.Json);
        Assert.Contains("\"availability\":\"temporarilyUnavailable\"", json);
        var decoded = DeviceServiceProtocol.ReadReply(Encoding.UTF8.GetBytes(json), hello).State!;
        var decodedTransient = decoded.Inventory.Devices.Single(d => d.Device.Id == "transient");
        Assert.True(DeviceRegistry.Valid(decodedTransient.Device));
        Assert.Equal(DeviceAvailability.TemporarilyUnavailable,
            decodedTransient.Device.Endpoints[0].Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor).Availability);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(Encoding.UTF8.GetBytes(
            json.Replace("\"temporarilyUnavailable\"", "\"unknownAvailability\"", StringComparison.Ordinal)), hello));
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(Encoding.UTF8.GetBytes(
            json.Insert(1, "\"unexpected\":true,")), hello));

        foreach (var feature in new[] { DeviceFeature.FixedColor, DeviceFeature.NativeEffect, DeviceFeature.ColorFrames })
            Assert.NotNull(registry.Check(new(hello.Epoch, "transient", "rgb", decodedTransient.Generation, feature)));
        foreach (var device in inventory.Devices)
            Assert.Null(registry.Check(new(hello.Epoch, device.Device.Id, "rgb", device.Generation, DeviceFeature.RestoreLighting)));

        var draft = new DeviceLightingDraft();
        draft.Update(decoded, new string('a', 32), new(2, [], [], [], [], []), 0, true);
        draft.Select([new("transient", "rgb")]);
        Assert.False(draft.CanApply);
        Assert.Throws<InvalidOperationException>(() => draft.Capture());
        Assert.True(draft.CanStop);
        Assert.Equal(DeviceOperation.StopLighting, draft.CaptureStop().Operation);

        draft.Select([new("transient", "rgb"), new("failed", "rgb")]);
        Assert.False(draft.CanApply);
        Assert.True(draft.CanStop);
        var stop = draft.CaptureStop();
        Assert.Equal(DeviceOperation.StopLighting, stop.Operation);
        Assert.Equal(new[] { new EndpointReference("transient", "rgb"), new EndpointReference("failed", "rgb") },
            stop.Version.Targets.Select(t => t.Target).ToArray());

        // A successful poll changes readiness, not the connection or its command generation.
        var refreshed = registry.Reconcile([Light("transient", DeviceAvailability.Ready), failed]);
        Assert.Equal(decodedTransient.Generation, refreshed.Devices.Single(d => d.Device.Id == "transient").Generation);
        draft.Update(decoded with { Inventory = refreshed }, new string('a', 32), new(2, [], [], [], [], []), 0, true);
        draft.Select([new("transient", "rgb")]);
        Assert.True(draft.CanApply);
        Assert.Equal(DeviceOperation.SetLighting, draft.Capture().Operation);
    }
}
