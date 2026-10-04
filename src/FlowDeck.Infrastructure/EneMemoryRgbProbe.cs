using System.Text;

namespace FlowDeck.Infrastructure;

/// <summary>Bounded ENE discovery. The bus exposes signature reads and register-pointer selection only.</summary>
public interface IMemoryRgbProbeBus
{
    byte? ReadSignature(byte address, byte command);
    byte ReadRegister(byte address, ushort register);
}

public record MemoryRgbProbeResult(byte Address, bool SignatureMatched, string? Firmware = null,
    string? ConfigurationHex = null, string? StateHex = null, string? Note = null);

public static class EneMemoryRgbProbe
{
    // Published ENE candidates, excluding the shared/remapping address 0x77 and all SPD/PMIC addresses.
    public static IReadOnlyList<byte> Addresses { get; } = Array.AsReadOnly<byte>(
        [0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x4f, 0x66, 0x67, 0x39, 0x3a, 0x3b, 0x3c, 0x3d]);

    public static void CheckSignatureRequest(byte address, byte command)
    {
        if (!Addresses.Contains(address) || command is < 0xa0 or > 0xaf)
            throw new ArgumentOutOfRangeException(nameof(address), "メモリRGB識別の照会範囲外です");
    }

    public static void CheckRegisterRequest(byte address, ushort register)
    {
        if (!Addresses.Contains(address) || !(register is >= 0x1000 and <= 0x100f or >= 0x1030 and <= 0x103f or
            >= 0x1c00 and <= 0x1c3f or >= 0x8020 and <= 0x8023))
            throw new ArgumentOutOfRangeException(nameof(register), "メモリRGBの読取レジスター範囲外です");
    }

    public static MemoryRgbProbeResult[] Read(IMemoryRgbProbeBus bus, CancellationToken ct = default)
    {
        var result = new List<MemoryRgbProbeResult>();
        foreach (var address in Addresses)
        {
            ct.ThrowIfCancellationRequested();
            var signature = true;
            for (byte command = 0xa0; command <= 0xaf; command++)
            {
                ct.ThrowIfCancellationRequested();
                if (bus.ReadSignature(address, command) != command - 0xa0) { signature = false; break; }
            }
            if (!signature) { result.Add(new(address, false)); continue; }

            byte[] Read(ushort first, int count) => Enumerable.Range(0, count).Select(i =>
            {
                ct.ThrowIfCancellationRequested();
                return bus.ReadRegister(address, checked((ushort)(first + i)));
            }).ToArray();
            static string Text(byte[] data) => Encoding.ASCII.GetString(data).TrimEnd('\0', ' ');
            var micron = Text(Read(0x1030, 16));
            if (micron.StartsWith("Micron", StringComparison.Ordinal))
            {
                result.Add(new(address, true, Note: "Micron機器は対象外")); continue;
            }
            var firmware = Text(Read(0x1000, 16));
            if (firmware.Length == 0 || firmware.Any(c => c < 0x20 || c > 0x7e))
            {
                result.Add(new(address, true, Note: "識別文字列が未対応")); continue;
            }
            result.Add(new(address, true, firmware, Convert.ToHexString(Read(0x1c00, 64)), Convert.ToHexString(Read(0x8020, 4))));
        }
        return result.ToArray();
    }
}
