using FlowDeck.Core;
using static FlowDeck.Core.DeviceDiagnosticHardwareRules;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver : IDeviceDiagnosticHardwareSource
{
    public DeviceDiagnosticHardware ReadCachedDiagnosticHardware()
    {
        lock (gate)
        {
            if (!connected || disposed || status is null) return new(DeviceDiagnosticReadState.NotObserved, []);
            // The compiled worker only accepts this USB product. Identity descriptions and serials are not model evidence.
            var firmware = TextItem(DeviceDiagnosticHardwareKind.Firmware, status.Firmware);
            if (firmware.State == DeviceDiagnosticReadState.Available && DeviceDiagnostics.NumericVersion(status.Firmware) is null)
                firmware = new(DeviceDiagnosticHardwareKind.Firmware, DeviceDiagnosticReadState.Invalid);
            return new(DeviceDiagnosticReadState.Available, [
                TextItem(DeviceDiagnosticHardwareKind.Model, "NZXT Control Hub", DeviceDiagnosticHardwareEvidence.MatchedDefinition),
                new(DeviceDiagnosticHardwareKind.UsbProduct, DeviceDiagnosticReadState.Available,
                    DeviceDiagnosticHardwareEvidence.MatchedDefinition, VendorId: 0x1E71, ProductId: 0x2022), firmware]);
        }
    }
}

internal sealed partial class AsrockDeviceDriver : IDeviceDiagnosticHardwareSource
{
    public DeviceDiagnosticHardware ReadCachedDiagnosticHardware()
    {
        lock (gate) return disposed || connection is not { } c || observed is null ? new(DeviceDiagnosticReadState.NotObserved, []) :
            new(DeviceDiagnosticReadState.Available, [
                TextItem(DeviceDiagnosticHardwareKind.Model, "ASRock USB RGB", DeviceDiagnosticHardwareEvidence.MatchedDefinition),
                new(DeviceDiagnosticHardwareKind.UsbProduct, DeviceDiagnosticReadState.Available,
                    DeviceDiagnosticHardwareEvidence.MatchedDefinition, VendorId: AsrockRgbMonitor.VendorId, ProductId: AsrockRgbMonitor.ProductId),
                new(DeviceDiagnosticHardwareKind.UsbDeviceRevision, c.DeviceRevision is null ? DeviceDiagnosticReadState.NotObserved : DeviceDiagnosticReadState.Available,
                    Revision: c.DeviceRevision),
                new(DeviceDiagnosticHardwareKind.Firmware, DeviceDiagnosticReadState.NotObserved)]);
    }
}

internal sealed partial class EneMemoryDeviceDriver : IDeviceDiagnosticHardwareSource
{
    public DeviceDiagnosticHardware ReadCachedDiagnosticHardware()
    {
        lock (gate)
        {
            if (disposed || connection is not { } c || observed is null) return new(DeviceDiagnosticReadState.NotObserved, []);
            var definition = EneMemoryDefinition.Find(c.DefinitionId);
            if (definition is null || c.ControllerFingerprint != EneMemoryDefinition.Proof(new(definition.Match.Firmware!, definition.Match.ConfigurationHex!)))
                return new(DeviceDiagnosticReadState.Invalid, []);
            // A controller definition cannot establish the DIMM vendor, model or slot. Export only the matched controller evidence.
            return new(DeviceDiagnosticReadState.Available, [
                TextItem(DeviceDiagnosticHardwareKind.ControlChip, "ENE DRAM RGB", DeviceDiagnosticHardwareEvidence.MatchedDefinition),
                TextItem(DeviceDiagnosticHardwareKind.Firmware, definition.Match.Firmware, DeviceDiagnosticHardwareEvidence.MatchedDefinition),
                new(DeviceDiagnosticHardwareKind.Model, DeviceDiagnosticReadState.NotObserved)]);
        }
    }
}

internal sealed partial class OpenRgbDeviceDriver : IDeviceDiagnosticHardwareSource
{
    public DeviceDiagnosticHardware ReadCachedDiagnosticHardware()
    {
        lock (gate)
        {
            if (disposed || current is not { } c) return new(DeviceDiagnosticReadState.NotObserved, []);
            var raw = c.Snapshot.Controller.Version;
            var version = DeviceDiagnostics.NumericVersion(raw);
            // SDK Version is provider-reported metadata, not a proven firmware version. SDK names may be user-editable.
            return new(DeviceDiagnosticReadState.Available, [
                new(DeviceDiagnosticHardwareKind.ProviderVersion, string.IsNullOrWhiteSpace(raw) ? DeviceDiagnosticReadState.NotObserved :
                    version is null ? DeviceDiagnosticReadState.Invalid : DeviceDiagnosticReadState.Available,
                    DeviceDiagnosticHardwareEvidence.ProviderReported, Value: version),
                new(DeviceDiagnosticHardwareKind.Model, DeviceDiagnosticReadState.NotObserved),
                new(DeviceDiagnosticHardwareKind.Firmware, DeviceDiagnosticReadState.NotObserved)]);
        }
    }
}

public sealed partial class MotherboardDeviceDriver : IDeviceDiagnosticHardwareSource
{
    public DeviceDiagnosticHardware ReadCachedDiagnosticHardware() => sensors.ReadCachedDiagnosticHardware();
}
