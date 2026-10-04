using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record EneMemoryIdentification(string Firmware, string ConfigurationHex);
internal interface IEneMemoryIdentifyingIo
{
    EneMemoryIdentification? IdentifyController(byte address);
}
internal interface IEneMemoryGuardedIo
{
    byte Read(byte address, ushort register, Action beforeData);
    void Write(byte address, ushort register, byte value, Action beforeData);
    void WritePixel(byte address, int led, byte red, byte blue, byte green, Action beforeData);
}

// Controller identity is independent of bus location and of any unproven DIMM/SPD address correspondence.
internal sealed record EneMemoryDefinition(string Id, string Firmware, string ConfigurationHex, int LedCount)
{
    private static EneMemoryDefinition From(LightingDriverDefinition d) => new(d.Id, d.Match.Firmware!, d.Match.ConfigurationHex!, d.Protocol.FixedLeds!.Value);
    public static EneMemoryDefinition EightLed { get; } = From(LightingDeviceDefinitionCatalog.BuiltIn.Get(LightingDeviceDefinitionCatalog.EneId, "ene-dram-rgb"));
    public static EneMemoryDefinition? Resolve(EneMemoryIdentification? identity) => identity is not null &&
        LightingDeviceDefinitionCatalog.BuiltIn.ResolveEne(identity) is { } definition ? From(definition) : null;
    public static LightingDriverDefinition? Find(string id) => LightingDeviceDefinitionCatalog.BuiltIn.Devices.SingleOrDefault(d => d.Driver == "ene-dram-rgb" && d.Id == id);
    public static string Proof(EneMemoryIdentification identity) => SettingsTransfer.Hash(identity);
}
