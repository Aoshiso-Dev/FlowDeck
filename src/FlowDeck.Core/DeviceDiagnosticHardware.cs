namespace FlowDeck.Core;

public enum DeviceDiagnosticHardwareKind { Model, ControlChip, Firmware, BiosVersion, UsbProduct, UsbDeviceRevision, PciProduct, ProviderVersion }
public enum DeviceDiagnosticHardwareEvidence { CachedObservation, MatchedDefinition, ProviderReported }
public sealed record DeviceDiagnosticHardwareItem(DeviceDiagnosticHardwareKind Kind, DeviceDiagnosticReadState State,
    DeviceDiagnosticHardwareEvidence Evidence = DeviceDiagnosticHardwareEvidence.CachedObservation,
    string? Value = null, int? VendorId = null, int? ProductId = null, int? Revision = null);
public sealed record DeviceDiagnosticHardware(DeviceDiagnosticReadState State, DeviceDiagnosticHardwareItem[] Items);

// Pure cached metadata only. Never poll, open hardware, read settings, or use editable display names/serials/paths here.
public interface IDeviceDiagnosticHardwareSource
{
    DeviceDiagnosticHardware ReadCachedDiagnosticHardware();
}

public static class DeviceDiagnosticHardwareRules
{
    public const string Feature = "diagnostics-hardware-v2";
    public const int MaximumItems = 24;
    public static DeviceDiagnosticHardware Capture(object? source)
    {
        if (source is not IDeviceDiagnosticHardwareSource metadata) return new(DeviceDiagnosticReadState.Unsupported, []);
        try { return Normalize(metadata.ReadCachedDiagnosticHardware()); }
        catch { return new(DeviceDiagnosticReadState.Failed, []); }
    }
    public static DeviceDiagnosticHardware Normalize(DeviceDiagnosticHardware? value)
    {
        if (value is null || !Enum.IsDefined(value.State) || value.Items is null || value.Items.Length > MaximumItems ||
            value.Items.Any(i => i is null || !Enum.IsDefined(i.Kind))) return new(DeviceDiagnosticReadState.Invalid, []);
        if (value.State != DeviceDiagnosticReadState.Available) return new(value.State, []);
        return new(value.State, value.Items.Select(NormalizeItem).Distinct().ToArray());
    }
    private static DeviceDiagnosticHardwareItem NormalizeItem(DeviceDiagnosticHardwareItem item)
    {
        var evidence = Enum.IsDefined(item.Evidence) ? item.Evidence : DeviceDiagnosticHardwareEvidence.CachedObservation;
        if (!Enum.IsDefined(item.State) || !Enum.IsDefined(item.Evidence)) return new(item.Kind, DeviceDiagnosticReadState.Invalid, evidence);
        if (item.State != DeviceDiagnosticReadState.Available) return new(item.Kind, item.State, evidence);
        var valid = item.Kind switch
        {
            DeviceDiagnosticHardwareKind.UsbProduct or DeviceDiagnosticHardwareKind.PciProduct => item.VendorId is >= 1 and <= 65535 &&
                item.ProductId is >= 0 and <= 65535 && item.Value is null && item.Revision is null,
            DeviceDiagnosticHardwareKind.UsbDeviceRevision => item.Revision is >= 0 and <= 65535 &&
                item.Value is null && item.VendorId is null && item.ProductId is null,
            _ => Text(item.Value, item.Kind == DeviceDiagnosticHardwareKind.Model ? 160 : 48) &&
                item.VendorId is null && item.ProductId is null && item.Revision is null &&
                (item.Kind is not (DeviceDiagnosticHardwareKind.Firmware or DeviceDiagnosticHardwareKind.BiosVersion or DeviceDiagnosticHardwareKind.ProviderVersion) ||
                    VersionText(item.Value))
        };
        return valid ? item with { Value = item.Value?.Trim() } : new(item.Kind, DeviceDiagnosticReadState.Invalid, evidence);
    }
    // Source implementations select hardware model fields; this bounds their encoding, not their provenance.
    // Free-form provider names, labels, reports, exceptions and connection identities must never be passed here.
    public static bool Text(string? value, int maximum = 160) => value is { Length: > 0 } && value.Length <= maximum &&
        !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '+' or '(' or ')' or '[' or ']' or ',' or '®' or '™');
    public static bool VersionText(string? value) => value is { Length: > 0 and <= 48 } && value.Any(char.IsAsciiDigit) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    public static DeviceDiagnosticHardwareItem TextItem(DeviceDiagnosticHardwareKind kind, string? value,
        DeviceDiagnosticHardwareEvidence evidence = DeviceDiagnosticHardwareEvidence.CachedObservation) => NormalizeItem(new(kind,
            string.IsNullOrWhiteSpace(value) ? DeviceDiagnosticReadState.NotObserved : DeviceDiagnosticReadState.Available, evidence, value));
}
