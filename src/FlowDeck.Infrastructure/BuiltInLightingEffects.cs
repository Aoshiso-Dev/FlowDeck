using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class BuiltInLightingEffects
{
    public static DeviceNativeLightingEffect[] Hub() => HubLighting.Effects.Where(e => HubDeviceDriver.DefinitionData.Lighting.NativeEffects.Contains(e.Id)).Select(e => new DeviceNativeLightingEffect(
        "native/" + HubDeviceDriver.Provider + "/" + e.Id, e.Name, e.Color, e.Color ? 1 : 0, e.Palette ? 8 : e.Color ? 1 : 0,
        0, 4, 2, e.Speed, e.Direction, e.Color || e.Id == "off")).ToArray();
    public static DeviceNativeLightingEffect[] Asrock() => BoardLighting.Effects.Select(e => new DeviceNativeLightingEffect(
        "native/asrock-usb/" + e.Id, e.Name, e.Color, e.Color ? 1 : 0, 1, 0, 255, 224, e.Speed,
        Brightness: e.Color || e.Id == 0)).ToArray();
    public static DeviceNativeLightingEffect[] Memory() => MemoryLighting.Effects.Select(e => new DeviceNativeLightingEffect(
        "native/ene-memory/" + e.Id, e.Name, e.Color, e.Color ? 1 : 0, 1, 0, 255, 224, e.Speed,
        Brightness: e.Color || e.Id == 0, LedCount: e.Color ? 8 : 0)).ToArray();
}
