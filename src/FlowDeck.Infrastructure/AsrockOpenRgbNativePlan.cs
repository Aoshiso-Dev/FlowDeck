using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Semantics of 81bbe18 with FlowDeck helper patch 4. SDK indices map through the captured
// physical layout. Predictions require a correlated COMPLETED native operation receipt;
// a generic SDK ACK or cached description alone does not establish completion.
internal sealed partial class AsrockOpenRgbNativePlan
{
    private readonly AsrockRgbSnapshot original;
    private readonly OpenRgbSdkController shape;
    private readonly int[] addresses;
    private readonly string structure;
    private static readonly string[] ModeNames = ["Off", "Static", "Breathing", "Strobe", "Spectrum Cycle", "Random", "Music",
        "Wave", "Spring", "Stack", "Cram", "Scan", "Neon", "Water", "Rainbow", "Direct"];
    private static readonly uint[] ModeFlags = [0, 32, 33, 33, 1, 1, 16, 1, 1, 1, 1, 1, 0, 1, 1, 32];

    internal AsrockOpenRgbNativePlan(AsrockRgbSnapshot before, OpenRgbSdkController controller)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(controller);
        if (before.Counts is not { Length: 8 } || before.Zones is not { Length: > 0 and <= 8 } ||
            !DeviceConfigurationRules.Text(before.Identity, 2048) || before.Zones.DistinctBy(z => z.Address).Count() != before.Zones.Length ||
            before.Zones.Any(z => z.Address >= 8 || z.Count is <= 0 or > 255 || z.Count == 0x1e || before.Counts[z.Address] != z.Count ||
                (before.Available & (1 << z.Address)) == 0 || !BoardLighting.IsValid(z.Setting) || z.Setting.MemoryPalette is not null))
            throw new IOException("ASRockの保存済みNative構成が不正です");
        original = Contract.Copy(before); shape = OpenRgbSdkProtocol.Copy(controller);
        addresses = Enumerable.Range(0, 8).Where(i => (before.Available & (1 << i)) != 0 && before.Counts[i] != 0x1e).ToArray();
        if (addresses.Count(i => before.Counts[i] > 0) != before.Zones.Length ||
            controller is not { Type: 0, Vendor: "ASRock", Description: "ASRock Polychrome USB Device", Serial: "" } ||
            !controller.Location.Equals("HID: " + before.Identity, StringComparison.OrdinalIgnoreCase) ||
            controller.Zones.Length != addresses.Length || controller.Leds.Length != addresses.Sum(i => before.Counts[i]) ||
            controller.Colors.Length != controller.Leds.Length || controller.Colors.Any(c => c?.Valid != true))
            throw new IOException("OpenRGBのASRock機器情報が保存対象と一致しません");
        for (var index = 0; index < addresses.Length; index++)
        {
            var address = addresses[index]; var zone = controller.Zones[index]; var configurable = address is 2 or 3 or 7;
            if (zone.Name != AsrockOpenRgbDetectionRecovery.ZoneNames[address] || zone.Type != 1 || zone.LedCount != before.Counts[address] ||
                zone.MinimumLeds != (configurable ? 0 : before.Counts[address]) || zone.MaximumLeds != (configurable ? 100 : before.Counts[address]) ||
                zone.LedCount > zone.MaximumLeds || zone.Modes.Length != 0 || zone.Segments.Length != 0 || zone.Matrix is not null)
                throw new IOException("OpenRGBのASRockゾーンとLED構成が変わりました");
        }
        structure = OpenRgbSdkProtocol.Structure(shape);
    }
    private byte Address(int sdkZone)
    {
        if (sdkZone < 0 || sdkZone >= addresses.Length || original.Counts[addresses[sdkZone]] == 0)
            throw new IOException("ASRockの終了対象ゾーンがないかLED数が0です");
        return (byte)addresses[sdkZone];
    }
    // RestoreOriginal is deliberately taken from the native capture, never from OpenRGB's single
    // global active_mode or its default speed. Mixed original native effects survive that loss.
    public Dictionary<byte, BoardLightSetting> Exit(IReadOnlyDictionary<int, LightingExitPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Count == 0 || policies.Any(p => !DeviceLightingConfiguration.ValidExit(p.Value)))
            throw new IOException("ASRockの終了方法が不正です");
        var desired = original.Zones.ToDictionary(z => z.Address, z => Contract.Copy(z.Setting));
        foreach (var (zone, policy) in policies)
        {
            var address = Address(zone);
            desired[address] = policy.Action switch
            {
                LightingExitAction.RestoreOriginal => desired[address],
                LightingExitAction.Off => new(0, 0, 0, 0),
                LightingExitAction.FixedColor => new(1, policy.Color!.R, policy.Color.G, policy.Color.B),
                _ => throw new IOException("ASRockの終了方法に対応していません")
            };
        }
        return desired;
    }
    public Dictionary<byte, BoardLightSetting> Exit(LightingExitPolicy wholeDevice) => Exit(addresses
        .Select((address, index) => (address, index)).Where(p => original.Counts[p.address] > 0).ToDictionary(p => p.index, _ => wholeDevice));
    // A shared Direct group's saved background/normal-stop intent is supplied explicitly. The
    // current frame is not an exit policy and cannot replace a saved native setting by inference.
    public Dictionary<byte, BoardLightSetting> FixedBackground(IReadOnlyDictionary<int, RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Values.Any(c => c?.Valid != true)) throw new IOException("ASRockの終了色が不正です");
        return Exit(colors.ToDictionary(p => p.Key, p => new LightingExitPolicy(LightingExitAction.FixedColor, p.Value)));
    }
    internal Dictionary<byte, BoardLightSetting> MergeExit(IReadOnlyDictionary<byte, BoardLightSetting> current, OpenRgbLightingExitIntent? intent)
    {
        if (current.Count != original.Zones.Length || original.Zones.Any(z => !current.TryGetValue(z.Address, out var value) ||
            !BoardLighting.IsValid(value) || value.MemoryPalette is not null)) throw new IOException("ASRockの保存済み終了方法が不正です");
        var result = current.ToDictionary(p => p.Key, p => Contract.Copy(p.Value));
        if (intent is null) return result;
        if (intent.Policy is { } policy && intent.Zones is null)
        {
            if (intent.Zone is null) return Exit(policy);
            var address = Address(intent.Zone.Value);
            result[address] = Exit(new Dictionary<int, LightingExitPolicy> { [intent.Zone.Value] = policy })[address];
            return result;
        }
        if (intent.Policy is not null || intent.Zone is not null || intent.Zones is not { Count: > 0 })
            throw new IOException("ASRockの終了方法と対象が不正です");
        var selected = Exit(intent.Zones);
        foreach (var index in intent.Zones.Keys) { var address = Address(index); result[address] = selected[address]; }
        return result;
    }
    private void CheckCompletedState(OpenRgbSdkController controller)
    {
        if (controller.Id != shape.Id || OpenRgbSdkProtocol.Structure(controller) != structure || controller.ActiveMode is < 0 or > 15 ||
            controller.Modes.Length != 16 || controller.Colors.Length != shape.Colors.Length || controller.Colors.Any(c => c?.Valid != true))
            throw new IOException("ASRockのSDK効果・構成が固定版と一致しません");
        for (var i = 0; i < 16; i++)
        {
            var m = controller.Modes[i]; var speed = (ModeFlags[i] & 1) != 0 || i == 6;
            if (m.Name != ModeNames[i] || m.Flags != ModeFlags[i] || m.SpeedMin != (speed ? 255u : 0u) || m.SpeedMax != 0 ||
                m.Speed > 255 || !speed && m.Speed != 0 || i == 6 && m.Speed != 224 || m.BrightnessMin != 0 || m.BrightnessMax != 0 ||
                m.Brightness != 0 || m.ColorsMin != 0 || m.ColorsMax != 0 || m.Colors.Length != 0 || m.Direction != 0 ||
                m.ColorMode != ((ModeFlags[i] & 32) != 0 ? 1u : 0u))
                throw new IOException("ASRockのSDK効果定義が固定版と一致しません");
        }
        if (controller.Zones.Any(z => z.ActiveMode >= 0 && z.Modes.Length != 0)) throw new IOException("ASRockの個別ゾーン効果は未対応です");
    }
    public AsrockRgbSnapshot PredictCompletedModeUpdate(OpenRgbSdkController controller)
    {
        CheckCompletedState(controller);
        var mode = controller.Modes[controller.ActiveMode];
        // The pinned source pushes a copy of Random as the Music entry, retaining value 5 and
        // speed bounds/default. Do not silently invent value 6 from the SDK index or display name.
        var value = controller.ActiveMode == 6 ? (byte)5 : (byte)controller.ActiveMode;
        var order = value == 15 ? (byte)0 : AsrockOpenRgbDetectionRecovery.DetectionOrder(original);
        var settings = new List<AsrockZoneSnapshot>(); var offset = 0;
        for (var index = 0; index < addresses.Length; index++)
        {
            var address = (byte)addresses[index]; var count = original.Counts[address];
            if (count == 0) continue;
            var color = controller.Colors[offset]; offset += count;
            // Patch 4 encodes against the effective order after SetRGSwap succeeds, so Native
            // readback decodes the same logical RGB even when Direct has changed the order to zero.
            settings.Add(new(address, count, new(value, color.R, color.G, color.B, (int)mode.Speed)));
        }
        return original with { Order = order, Counts = original.Counts.ToArray(), Zones = settings.ToArray() };
    }
}
