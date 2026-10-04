using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public partial class HubDeviceDriverTests
{
    [Theory] [InlineData("1.2.3", DeviceDiagnosticReadState.Available)] [InlineData(null, DeviceDiagnosticReadState.NotObserved)]
    [InlineData("1.2 PRIVATE", DeviceDiagnosticReadState.Invalid)] [InlineData(@"C:\PRIVATE\1.2", DeviceDiagnosticReadState.Invalid)]
    public async Task DiagnosticHardwareRetainsOnlyMatchedUsbProductAndValidatedCachedFirmware(string? firmware, DeviceDiagnosticReadState expected)
    {
        using var directory = new ServiceTestDirectory(); var hub = new Hub("PRIVATE-native-id") { Transform = s => s with { Firmware = firmware } };
        await using var driver = new HubDeviceDriver(Installation, hub.Identity.Id, hub, directory.Path);
        Assert.Equal(DeviceDiagnosticReadState.NotObserved, driver.ReadCachedDiagnosticHardware().State);
        await driver.InitializeAsync(); var binding = driver.BindingCandidate;
        hub.Transform = _ => throw new InvalidOperationException("Diagnostic must not read hardware");
        var metadata = DeviceDiagnosticHardwareRules.Capture(driver);
        Assert.Equal(DeviceDiagnosticReadState.Available, metadata.State);
        var usb = Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.UsbProduct);
        Assert.Equal(0x1E71, usb.VendorId); Assert.Equal(0x2022, usb.ProductId);
        Assert.Equal(DeviceDiagnosticHardwareEvidence.MatchedDefinition, usb.Evidence);
        var fw = Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.Firmware);
        Assert.Equal(expected, fw.State); Assert.Equal(expected == DeviceDiagnosticReadState.Available ? firmware : null, fw.Value);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(metadata, Contract.Json)); Assert.Equal(binding, driver.BindingCandidate);
        Assert.Equal(0, hub.Inner.Writes); Assert.Single(hub.Opened);
        driver.SetDiscovery(null); Assert.Empty(driver.ReadCachedDiagnosticHardware().Items);
    }
}

public sealed partial class AsrockDeviceDriverTests
{
    [Theory] [InlineData(null)] [InlineData(0x0123)]
    public async Task DiagnosticUsbRevisionIsNotReportedAsFirmwareAndDoesNotExchangePackets(int? revision)
    {
        var cell = new Cell(1, true); cell.Connection = cell.Connection with { DeviceRevision = revision };
        await using var rig = await new Rig(new(cell)).Open(configure: false);
        var binding = rig.Driver(cell).BindingCandidate;
        rig.Backend.BeforeTransport = _ => throw new InvalidOperationException("Diagnostic must not exchange packets");
        var metadata = DeviceDiagnosticHardwareRules.Capture(rig.Driver(cell));
        Assert.Equal(DeviceDiagnosticReadState.Available, metadata.State);
        var usb = Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.UsbProduct);
        Assert.Equal(0x26CE, usb.VendorId); Assert.Equal(0x01A2, usb.ProductId);
        var usbRevision = Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.UsbDeviceRevision);
        Assert.Equal(revision, usbRevision.Revision); Assert.Equal(revision is null ? DeviceDiagnosticReadState.NotObserved : DeviceDiagnosticReadState.Available, usbRevision.State);
        Assert.Equal(DeviceDiagnosticReadState.NotObserved, Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.Firmware).State);
        Assert.Equal(binding, rig.Driver(cell).BindingCandidate); Assert.Empty(cell.Device.Writes);
        rig.Backend.BeforeTransport = null;
        rig.Driver(cell).SetConnection(null); Assert.Empty(rig.Driver(cell).ReadCachedDiagnosticHardware().Items);
    }
}

public sealed partial class EneMemoryDeviceDriverTests
{
    [Fact] public async Task DiagnosticControllerEvidenceCannotClaimADimmModelOrAccessTheBus()
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false);
        var before = (rack.Opens, rack.Identifications, rack.ShortIdentifications); var binding = rig.Driver().BindingCandidate;
        rack.BeforeData = (_, _) => throw new InvalidOperationException("Diagnostic must not touch registers");
        rack.OnOpen = () => throw new InvalidOperationException("Diagnostic must not open SMBus");
        var metadata = DeviceDiagnosticHardwareRules.Capture(rig.Driver());
        Assert.Equal(DeviceDiagnosticReadState.Available, metadata.State);
        Assert.Equal(DeviceDiagnosticReadState.NotObserved, Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.Model).State);
        var firmware = Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.Firmware);
        Assert.Equal(EneMemoryDefinition.EightLed.Firmware, firmware.Value); Assert.Equal(DeviceDiagnosticHardwareEvidence.MatchedDefinition, firmware.Evidence);
        Assert.Equal(binding, rig.Driver().BindingCandidate); Assert.Equal(before, (rack.Opens, rack.Identifications, rack.ShortIdentifications)); Assert.Empty(rack.Writes);
        var json = JsonSerializer.Serialize(metadata, Contract.Json);
        Assert.DoesNotContain(Bus, json); Assert.DoesNotContain(binding!.Fingerprint, json); Assert.DoesNotContain(binding.ProviderIdentity, json);
        rack.BeforeData = null; rack.OnOpen = null;
        rig.Driver().SetConnection(null); Assert.Empty(rig.Driver().ReadCachedDiagnosticHardware().Items);
    }
}

public sealed partial class OpenRgbDeviceDriverTests
{
    [Theory] [InlineData("1.2.3", DeviceDiagnosticReadState.Available)] [InlineData("", DeviceDiagnosticReadState.NotObserved)]
    [InlineData("1.2 PRIVATE", DeviceDiagnosticReadState.Invalid)]
    public async Task DiagnosticSdkVersionIsProviderReportedAndNeverCopiesNamesOrQueriesTheServer(string version, DeviceDiagnosticReadState state)
    {
        var device = Device() with { Name = "PRIVATE model", DisplayName = "PRIVATE edited name", Vendor = "PRIVATE vendor",
            Description = "PRIVATE description", Serial = "PRIVATE serial", Location = "PRIVATE location", Version = version };
        await using var rig = await new Rig(device).Open(configure: false);
        var driver = rig.Driver(); var requests = rig.Server.Wire.Requests.Count; var binding = driver.BindingCandidate;
        var metadata = DeviceDiagnosticHardwareRules.Capture(driver);
        Assert.Equal(DeviceDiagnosticReadState.Available, metadata.State);
        var reported = Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.ProviderVersion);
        Assert.Equal(state, reported.State); Assert.Equal(DeviceDiagnosticHardwareEvidence.ProviderReported, reported.Evidence);
        Assert.Equal(state == DeviceDiagnosticReadState.Available ? version : null, reported.Value);
        Assert.Equal(DeviceDiagnosticReadState.NotObserved, Assert.Single(metadata.Items, i => i.Kind == DeviceDiagnosticHardwareKind.Firmware).State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(metadata, Contract.Json)); Assert.Equal(binding, driver.BindingCandidate);
        Assert.Equal(requests, rig.Server.Wire.Requests.Count); Assert.Equal(0, rig.Server.WriteCount);
        driver.SetConnection(null); Assert.Empty(driver.ReadCachedDiagnosticHardware().Items);
    }
}
