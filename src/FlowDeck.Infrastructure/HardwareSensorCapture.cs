using FlowDeck.Core;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
using System.Text.RegularExpressions;

namespace FlowDeck.Infrastructure;

public static class HardwareSensorCapture
{
    // Firmware placeholders are not physical serials. Never turn a model name or enumeration index into one.
    public static bool UsefulSerial(string? serial) => !string.IsNullOrWhiteSpace(serial) && serial.Length <= 256 &&
        !serial.Any(char.IsControl) && serial.Trim().Any(c => c is not ('0' or 'F' or 'f' or '-' or ' ')) &&
        !new[] { "default string", "to be filled by o.e.m.", "to be filled by oem", "unknown", "none", "n/a", "not specified", "system serial number",
            "123456789", "1234567890", "0123456789", "not available", "not applicable" }
            .Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase);
    public static string? PciLocation(string? pnp)
    {
        if (pnp is null || pnp.Length > 1024 || pnp.Any(char.IsControl)) return null;
        var parts = pnp.Trim().ToUpperInvariant().Split('\\');
        if (parts.Length != 3 || parts[0] != "PCI" || !Regex.IsMatch(parts[1], @"^VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&[A-Z0-9_]+)*$") ||
            parts[2].Length is < 1 or > 256 || parts[2].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('&' or '_' or '-'))) return null;
        return SettingsTransfer.Hash(string.Join('\\', parts));
    }
    public static HardwareSensorDevice Describe(IHardware root, SMBios? bios, string model, string connection, int cpuCount, HardwareSensorValue[] values)
    {
        var kind = root.HardwareType == HardwareType.Motherboard ? DeviceKind.Motherboard :
            root.HardwareType == HardwareType.Cpu ? DeviceKind.Processor : DeviceKind.Graphics;
        var board = bios?.Board; var firmware = bios?.Bios;
        var processor = kind == DeviceKind.Processor && cpuCount == 1 && bios?.Processors is { Length: 1 } processors ? processors[0] : null;
        var serial = kind == DeviceKind.Motherboard ? board?.SerialNumber : processor?.Serial;
        var serialIdentity = UsefulSerial(serial) ? SettingsTransfer.Hash(new { kind, Manufacturer = board?.ManufacturerName, model, Serial = serial!.Trim() }) : null;
        string? location = null;
        DeviceDiagnosticHardwareItem? pci = null;
        if (root is GenericGpu gpu)
        {
            // LHM 0.9.6 exposes the Windows PnP identity. It is a connection position, not a manufactured serial.
            try { var pnp = gpu.DeviceId; location = PciLocation(pnp); pci = HardwareSensorDiagnostics.Pci(pnp); }
            catch { pci = new(DeviceDiagnosticHardwareKind.PciProduct, DeviceDiagnosticReadState.Failed); }
        }
        else if (processor is not null && UsefulSerial(board?.SerialNumber) && DeviceConfigurationRules.Text(processor.SocketDesignation, 256))
            location = SettingsTransfer.Hash(new { Board = board!.SerialNumber.Trim(), processor.SocketDesignation });
        var boardProof = kind == DeviceKind.Motherboard ? new
        {
            board?.ManufacturerName, board?.ProductName, board?.Version, board?.SerialNumber,
            BiosVendor = firmware?.Vendor, BiosVersion = firmware?.Version, BiosDate = firmware?.Date,
            Chips = root.SubHardware.Select(h => h.GetType().FullName + ":" + h.Identifier).Order(StringComparer.Ordinal).ToArray()
        } : null;
        var fingerprint = SettingsTransfer.Hash(new { kind, model, Implementation = root.GetType().FullName, Board = boardProof,
            Cpu = processor is null ? null : new { processor.Id, processor.Version, processor.SocketDesignation, processor.ManufacturerName, processor.Serial },
            // A location keeps names/references stable; it cannot prove that the same card still occupies the slot.
            Serial = serialIdentity, Location = location, Connection = serialIdentity is null ? connection : null });
        return new(root.Identifier.ToString(), MotherboardDisplay.ShortText(root.Name, 160), kind, fingerprint, serialIdentity, connection,
            kind == DeviceKind.Motherboard ? MotherboardDefinitionCatalog.BuiltIn.Resolve(model)?.Id : null, values, location,
            HardwareSensorDiagnostics.Describe(root, bios, model, pci));
    }
    internal static void AddValues(IHardware hardware, List<HardwareSensorValue> values, IReadOnlyDictionary<ISensor, SensorAcquisition> acquisitions)
    {
        foreach (var sensor in hardware.Sensors)
        {
            DeviceFeature? feature = sensor.SensorType switch { SensorType.Temperature => DeviceFeature.Temperature,
                SensorType.Fan => DeviceFeature.Rpm, SensorType.Control => DeviceFeature.DutyRead, _ => null };
            if (feature is null) continue;
            if (!acquisitions.TryGetValue(sensor, out var acquisition))
                throw new InvalidOperationException("センサーの取得記録がありません");
            var raw = (double?)sensor.Value;
            var valid = raw is { } number && double.IsFinite(number) && feature switch
            { DeviceFeature.Temperature => HardwareTemperatures.IsUsable(raw), DeviceFeature.Rpm => number is >= 0 and <= 30000,
              _ => number is >= 0 and <= 100 };
            var issue = acquisition.Problem is not null || raw is null ? DeviceMeasurementIssue.Unavailable :
                valid ? DeviceMeasurementIssue.None : DeviceMeasurementIssue.OutOfRange;
            values.Add(new(sensor.Identifier.ToString(), MotherboardDisplay.ShortText(sensor.Name, 160), hardware.Identifier.ToString(),
                feature.Value, acquisition.Problem is null && valid ? raw : null, acquisition.Timestamp,
                acquisition.Problem ?? (issue == DeviceMeasurementIssue.OutOfRange ? "測定値が範囲外です" : valid ? null : "測定値を取得できません"), issue));
        }
    }
}
