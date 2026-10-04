using System.Globalization;
using System.Management;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record EneDimmEvidence(string? Locator, string? Bank, string? Manufacturer, string? Part, string? Serial,
    ulong Capacity, uint MemoryType, uint ConfiguredClock);
internal sealed record EnePlatformEvidence(string? BoardManufacturer, string? BoardProduct, string? BoardVersion, string? BoardSerial,
    string? BiosManufacturer, string? BiosVersion, string? BiosDate, EneDimmEvidence[] Dimms);

internal static class EneMemoryPlatformCapture
{
    private static string? Text(string? value)
    {
        value = value?.Trim(); if (string.IsNullOrEmpty(value)) return null;
        if (!DeviceConfigurationRules.Text(value, 256)) throw new IOException("メモリ構成の識別文字列が不正です");
        return value;
    }
    // A whole-machine change detector, not evidence that an RGB address is a particular physical DIMM.
    internal static string Fingerprint(EnePlatformEvidence evidence)
    {
        if (evidence?.Dimms is not { Length: > 0 and <= 32 }) throw new IOException("DIMM構成を確認できません");
        // SMBIOS may reuse a locator across channels. A known distinct bank disambiguates it.
        // This remains a whole-platform proof, never a mapping from RGB bus address to DIMM.
        var dimms = evidence.Dimms.Select(d => d is null ? throw new IOException("DIMM情報がありません") : d with
        { Locator = Text(d.Locator), Bank = Text(d.Bank), Manufacturer = Text(d.Manufacturer), Part = Text(d.Part), Serial = Text(d.Serial) }).OrderBy(d => d.Locator, StringComparer.Ordinal).ThenBy(d => d.Bank, StringComparer.Ordinal).ToArray();
        var value = evidence with { BoardManufacturer = Text(evidence.BoardManufacturer), BoardProduct = Text(evidence.BoardProduct),
            BoardVersion = Text(evidence.BoardVersion), BoardSerial = Text(evidence.BoardSerial), BiosManufacturer = Text(evidence.BiosManufacturer),
            BiosVersion = Text(evidence.BiosVersion), BiosDate = Text(evidence.BiosDate), Dimms = dimms };
        if (value.BoardManufacturer is null || value.BoardProduct is null || value.BiosVersion is null ||
            dimms.Any(d => d.Locator is null || d.Capacity is 0 or > (1UL << 50)) || dimms.GroupBy(d => d.Locator, StringComparer.Ordinal).Any(group => group.Count() > 1 &&
                (group.Any(d => d.Bank is null) || group.DistinctBy(d => d.Bank, StringComparer.Ordinal).Count() != group.Count())))
            throw new IOException("マザー・BIOS・DIMM配置の照合に必要な情報がありません");
        return SettingsTransfer.Hash(value);
    }
    internal static string ReadFingerprint()
    {
        static Dictionary<string, object?>[] Query(string name, string[] fields, int maximum)
        {
            using var search = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT " + string.Join(',', fields) + " FROM " + name), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) });
            using var values = search.Get(); var result = new List<Dictionary<string, object?>>();
            foreach (ManagementObject item in values)
            {
                using (item) result.Add(fields.ToDictionary(f => f, f => (object?)item[f]));
                if (result.Count > maximum) throw new IOException("メモリ構成の取得件数が不正です");
            }
            return result.ToArray();
        }
        static string? S(Dictionary<string, object?> row, string field) => row[field] as string;
        var boards = Query("Win32_BaseBoard", ["Manufacturer", "Product", "Version", "SerialNumber"], 1);
        var bios = Query("Win32_BIOS", ["Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate"], 1);
        var memory = Query("Win32_PhysicalMemory", ["DeviceLocator", "BankLabel", "Manufacturer", "PartNumber", "SerialNumber", "Capacity", "SMBIOSMemoryType", "ConfiguredClockSpeed"], 32);
        if (boards.Length != 1 || bios.Length != 1) throw new IOException("マザー・BIOSの構成を特定できません");
        var b = boards[0]; var f = bios[0];
        return Fingerprint(new(S(b, "Manufacturer"), S(b, "Product"), S(b, "Version"), S(b, "SerialNumber"),
            S(f, "Manufacturer"), S(f, "SMBIOSBIOSVersion"), S(f, "ReleaseDate"), memory.Select(m => new EneDimmEvidence(
                S(m, "DeviceLocator"), S(m, "BankLabel"), S(m, "Manufacturer"), S(m, "PartNumber"), S(m, "SerialNumber"),
                Convert.ToUInt64(m["Capacity"], CultureInfo.InvariantCulture), Convert.ToUInt32(m["SMBIOSMemoryType"], CultureInfo.InvariantCulture),
                Convert.ToUInt32(m["ConfiguredClockSpeed"], CultureInfo.InvariantCulture))).ToArray()));
    }
}
