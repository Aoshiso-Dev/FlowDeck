using System.Globalization;
using System.Text.RegularExpressions;
using FlowDeck.Core;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Infrastructure;

internal static class HardwareSensorDiagnostics
{
    internal static DeviceDiagnosticHardware Describe(IHardware root, SMBios? bios, string originalModel, DeviceDiagnosticHardwareItem? pci = null)
    {
        try { return DescribeCore(root, bios, originalModel, pci); }
        catch { return new(DeviceDiagnosticReadState.Failed, []); }
    }
    private static DeviceDiagnosticHardware DescribeCore(IHardware root, SMBios? bios, string originalModel, DeviceDiagnosticHardwareItem? pci)
    {
        var items = new List<DeviceDiagnosticHardwareItem> {
            DeviceDiagnosticHardwareRules.TextItem(DeviceDiagnosticHardwareKind.Model, originalModel) };
        if (root.HardwareType == HardwareType.Motherboard)
        {
            items.Add(DeviceDiagnosticHardwareRules.TextItem(DeviceDiagnosticHardwareKind.BiosVersion, bios?.Bios?.Version));
            // SuperIO identifiers are generated from LHM's chip enum. Drop bus/instance position and never use editable Name.
            var chips = root.SubHardware.Where(h => h.HardwareType == HardwareType.SuperIO)
                .Select(h => Chip(h.Identifier.ToString())).Where(c => c is not null).Distinct().Take(16).ToArray();
            foreach (var chip in chips) items.Add(DeviceDiagnosticHardwareRules.TextItem(DeviceDiagnosticHardwareKind.ControlChip, chip));
            if (chips.Length == 0) items.Add(new(DeviceDiagnosticHardwareKind.ControlChip, DeviceDiagnosticReadState.NotObserved));
        }
        if (pci is not null) items.Add(pci);
        return DeviceDiagnosticHardwareRules.Normalize(new(DeviceDiagnosticReadState.Available, items.ToArray()));
    }
    internal static string? Chip(string? identifier)
    {
        if (identifier is null || identifier.Length > 80) return null;
        var match = Regex.Match(identifier, @"\A/lpc/((?:nct|it|f|w|sch)[0-9]{3,5}[a-z0-9]{0,4})/[0-9]{1,3}\z", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
    internal static DeviceDiagnosticHardwareItem Pci(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return new(DeviceDiagnosticHardwareKind.PciProduct, DeviceDiagnosticReadState.NotObserved);
        if (HardwareSensorCapture.PciLocation(identity) is null) return new(DeviceDiagnosticHardwareKind.PciProduct, DeviceDiagnosticReadState.Invalid);
        var product = identity.Trim().Split('\\')[1].Split('&');
        return new(DeviceDiagnosticHardwareKind.PciProduct, DeviceDiagnosticReadState.Available,
            VendorId: int.Parse(product[0].AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ProductId: int.Parse(product[1].AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
