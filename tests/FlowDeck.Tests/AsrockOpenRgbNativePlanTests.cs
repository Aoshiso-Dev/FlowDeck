using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbNativePlanTests
{
    private const string PathName = @"\\?\hid#vid_26ce&pid_01a2#native-plan";
    internal static readonly AsrockOpenRgbRecoveryIdentity Identity = new("d3f516653d764da181e6f5a74bcae5c9", "native-plan",
        "native-plan-boot", "20c980723ffe433a94931b93f3d742b0", 1);
    internal static OpenRgbSdkMode[] Modes()
    {
        // SDK descriptors from pinned RGBController_ASRockPolychromeUSB.cpp. Music is a copied
        // Random entry; speed fields survive while only name, value, flags and color_mode change.
        OpenRgbSdkMode Plain(string name, uint flags = 0, uint color = 0) => new(name, flags, 0, 0, 0, 0, 0, 0, 0, 0, 0, color, []);
        OpenRgbSdkMode Speed(string name, bool color = false) => new(name, color ? 33u : 1u, 255, 0, 0, 0, 0, 0, 224, 0, 0, color ? 1u : 0u, []);
        return [Plain("Off"), Plain("Static", 32, 1), Speed("Breathing", true), Speed("Strobe", true), Speed("Spectrum Cycle"), Speed("Random"),
            Speed("Music") with { Flags = 16 }, Speed("Wave"), Speed("Spring"), Speed("Stack"), Speed("Cram"), Speed("Scan"), Plain("Neon"),
            Speed("Water"), Speed("Rainbow"), Plain("Direct", 32, 1)];
    }
    internal sealed class Rig : IDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly AsrockVolatileTransactionTests.Device Device = new();
        public readonly AsrockOpenRgbDetectionRecovery Journal;
        public readonly AsrockRgbSnapshot Before;
        public readonly OpenRgbSdkController Sdk;
        public readonly AsrockOpenRgbNativePlan Plan;
        public Rig(bool emptyFirst = false, bool emptyMiddle = false)
        {
            Device.Order = 0xdd; // Physical address 5 uses the opposite R/G byte order to 2 and 6.
            Device.Set(2, new(2, 9, 10, 11, 77)); Device.Set(5, new(14, 80, 81, 82, 100)); Device.Set(6, new(3, 21, 22, 23, 12));
            if (emptyFirst) { Device.Available |= 1; Device.Set(0, new(0, 0, 0, 0)); }
            if (emptyMiddle) { Device.Available |= 8; Device.Set(3, new(0, 0, 0, 0)); }
            Journal = new(Directory.Path, Identity.DeviceId);
            Before = Journal.Capture(Identity, PathName, Device.Exchange, () => { });
            var basis = OpenRgbLightingPlanTests.Device();
            var addresses = Enumerable.Range(0, 8).Where(i => (Device.Available & (1 << i)) != 0 && Device.Counts[i] != 0x1e).ToArray();
            var zones = addresses.Select(i => basis.Zones[0] with { Name = AsrockOpenRgbDetectionRecovery.ZoneNames[i], Type = 1,
                LedCount = Device.Counts[i], MinimumLeds = i is 2 or 3 or 7 ? 0u : Device.Counts[i], MaximumLeds = i is 2 or 3 or 7 ? 100u : Device.Counts[i],
                Modes = [], ActiveMode = -1, Matrix = null, Segments = [], Flags = 0 }).ToArray();
            var colors = addresses.SelectMany(i => Enumerable.Repeat(Before.Zones.SingleOrDefault(z => z.Address == i)?.Setting is { } s
                ? new RgbColor(s.Red, s.Green, s.Blue) : new(0, 0, 0), Device.Counts[i])).ToArray();
            Sdk = basis with { Type = 0, Vendor = "ASRock", Description = "ASRock Polychrome USB Device", Serial = "", Location = "HID: " + PathName,
                Zones = zones, Colors = colors, Leds = Enumerable.Repeat("LED", colors.Length).ToArray(), LedDisplayNames = [], Modes = Modes(), ActiveMode = 2 };
            Plan = new(Before, Sdk);
        }
        // Synchronous fixture for the body of upstream DeviceUpdateMode, not an SDK cache emulator.
        public void CompleteModeUpdate(OpenRgbSdkController controller)
        {
            Device.Exchange(AsrockDirectProtocol.ColorOrder(controller.ActiveMode == 15 ? (byte)0 : (byte)0x44));
            var offset = 0;
            foreach (var zone in controller.Zones)
            {
                if (zone.LedCount == 0) continue;
                var physical = (byte)Array.IndexOf(AsrockOpenRgbDetectionRecovery.ZoneNames.ToArray(), zone.Name);
                var color = controller.Colors[offset]; offset += (int)zone.LedCount;
                var value = controller.ActiveMode == 6 ? (byte)5 : (byte)controller.ActiveMode;
                // Encode the pinned C++ WriteZone independently. The application's native-only
                // Packet API correctly refuses Direct and must not be weakened for this fixture.
                var packet = new byte[65]; packet[1] = 0x10; packet[3] = physical; packet[4] = value;
                var effectiveOrder = controller.ActiveMode == 15 ? 0 : 0x44;
                packet[5] = (byte)((effectiveOrder & (1 << physical)) != 0 ? color.R : color.G);
                packet[6] = (byte)((effectiveOrder & (1 << physical)) != 0 ? color.G : color.R);
                packet[7] = (byte)color.B; packet[8] = (byte)controller.Modes[controller.ActiveMode].Speed; packet[9] = 0xff;
                Device.Exchange(packet);
            }
        }
        public void Dispose() => Directory.Dispose();
    }

    [Fact] public void RestoreUsesMixedOriginalNativeModesAndSpeedsInsteadOfSdkGlobalModeAndDefaults()
    {
        using var r = new Rig(); var exit = r.Plan.Exit(new LightingExitPolicy(LightingExitAction.RestoreOriginal));
        Assert.Equal(new BoardLightSetting(2, 9, 10, 11, 77), exit[2]);
        Assert.Equal(new BoardLightSetting(14, 80, 81, 82, 100), exit[5]); Assert.Equal(3, exit[6].Mode);
        Assert.Equal(12, exit[6].Speed); Assert.Equal(2, r.Sdk.ActiveMode); Assert.Equal(224u, r.Sdk.Modes[2].Speed);
    }
    [Fact] public void SdkIndicesMapToPhysicalAddressesAndLeaveUnselectedNativeZonesAlone()
    {
        using var r = new Rig(); var exit = r.Plan.Exit(new Dictionary<int, LightingExitPolicy> { [1] = new(LightingExitAction.FixedColor, new(4, 80, 170)) });
        Assert.Equal(new BoardLightSetting(1, 4, 80, 170), exit[5]);
        Assert.Equal(r.Before.Zones.Single(z => z.Address == 2).Setting, exit[2]); Assert.Equal(3, exit[6].Mode);
    }
    [Fact] public void FixedAndOffAreExplicitEvenWhenSdkWouldReportTheSameBlackPalette()
    {
        using var r = new Rig(); var black = r.Plan.Exit(new LightingExitPolicy(LightingExitAction.FixedColor, new(0, 0, 0)));
        var off = r.Plan.Exit(new LightingExitPolicy(LightingExitAction.Off));
        Assert.All(black.Values, s => Assert.Equal(1, s.Mode)); Assert.All(off.Values, s => Assert.Equal(0, s.Mode));
    }
    [Fact] public void SavedGroupBackgroundDoesNotUseAChangingAnimationFrameAsItsExitColor()
    {
        using var r = new Rig(); Array.Fill(r.Sdk.Colors, new(255, 0, 0));
        var exit = r.Plan.FixedBackground(new Dictionary<int, RgbColor> { [0] = new(0, 0, 128), [2] = new(10, 20, 30) });
        Assert.Equal(new BoardLightSetting(1, 0, 0, 128), exit[2]); Assert.Equal(new BoardLightSetting(1, 10, 20, 30), exit[6]);
        Assert.Equal(14, exit[5].Mode);
    }
    [Fact] public void EmptyPresentZonesKeepTheirSdkIndicesButCannotBeSelectedForOutput()
    {
        using var r = new Rig(emptyFirst: true, emptyMiddle: true);
        var exit = r.Plan.Exit(new Dictionary<int, LightingExitPolicy> { [3] = new(LightingExitAction.Off) });
        Assert.Equal(0, exit[5].Mode); Assert.Equal(2, exit[2].Mode);
        Assert.Throws<IOException>(() => r.Plan.Exit(new Dictionary<int, LightingExitPolicy> { [0] = new(LightingExitAction.Off) }));
        Assert.Throws<IOException>(() => r.Plan.Exit(new Dictionary<int, LightingExitPolicy> { [2] = new(LightingExitAction.Off) }));
    }
    [Theory] [InlineData(-1)] [InlineData(3)] [InlineData(999)]
    public void InvalidSdkIndicesCannotBeReinterpretedAsPhysicalAddresses(int zone)
    {
        using var r = new Rig(); Assert.Throws<IOException>(() => r.Plan.Exit(new Dictionary<int, LightingExitPolicy> { [zone] = new(LightingExitAction.Off) }));
    }
    [Theory] [InlineData("missing-color")] [InlineData("invalid-color")] [InlineData("unknown-action")] [InlineData("empty")]
    public void InvalidExitIntentDoesNotGenerateNativeSettings(string invalid)
    {
        using var r = new Rig(); var policies = new Dictionary<int, LightingExitPolicy> { [0] = invalid switch
        {
            "missing-color" => new(LightingExitAction.FixedColor), "invalid-color" => new(LightingExitAction.FixedColor, new(300, 0, 0)),
            "unknown-action" => new((LightingExitAction)900), _ => new(LightingExitAction.Off)
        } };
        if (invalid == "empty") policies.Clear();
        Assert.Throws<IOException>(() => r.Plan.Exit(policies)); Assert.Empty(r.Device.Writes);
    }
    [Fact] public void CompletedModePredictionUsesTheFirstColorOfEachZoneAndPreservesNativeSpeedBytes()
    {
        using var r = new Rig(); var state = OpenRgbSdkProtocol.Copy(r.Sdk); state.Modes[2] = state.Modes[2] with { Speed = 21 };
        state.Colors[0] = new(10, 40, 80); state.Colors[1] = new(200, 201, 202); state.Colors[80] = new(31, 61, 91);
        var expected = r.Plan.PredictCompletedModeUpdate(state);
        Assert.Equal(0x44, expected.Order); Assert.Equal(new BoardLightSetting(2, 10, 40, 80, 21), expected.Zones[0].Setting);
        Assert.Equal(new BoardLightSetting(2, 31, 61, 91, 21), expected.Zones[1].Setting);
    }
    [Fact] public void PinnedMusicEntryUsesNativeRandomValueFiveRatherThanSdkIndexSix()
    {
        using var r = new Rig(); var expected = r.Plan.PredictCompletedModeUpdate(r.Sdk with { ActiveMode = 6 });
        Assert.All(expected.Zones, z => { Assert.Equal(5, z.Setting.Mode); Assert.Equal(224, z.Setting.Speed); });
    }
    [Fact] public void DirectEntryPredictionPreservesLogicalRgbUnderTheNewZeroOrder()
    {
        using var r = new Rig(); var state = r.Sdk with { ActiveMode = 15 }; var expected = r.Plan.PredictCompletedModeUpdate(state);
        Assert.Equal(0, expected.Order); Assert.Equal(new BoardLightSetting(15, 9, 10, 11, 0), expected.Zones[0].Setting);
        Assert.Equal(new BoardLightSetting(15, 80, 81, 82, 0), expected.Zones[1].Setting);
        r.CompleteModeUpdate(state);
        Assert.Equal(new byte[] { 15, 10, 9, 11, 0 }, r.Device.Raw[2]); Assert.Equal(new byte[] { 15, 81, 80, 82, 0 }, r.Device.Raw[5]);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(6)] [InlineData(15)]
    public void CompletedNativeUpdateAndExplicitExitIntegrateWithDurableReadbackRestoration(int mode)
    {
        using var r = new Rig(); var state = r.Sdk with { ActiveMode = mode };
        var expected = r.Plan.PredictCompletedModeUpdate(state);
        var desired = r.Plan.Exit(new Dictionary<int, LightingExitPolicy> { [0] = new(LightingExitAction.Off), [1] = new(LightingExitAction.FixedColor, new(3, 77, 201)),
            [2] = new(LightingExitAction.RestoreOriginal) });
        r.Journal.PrepareExit(Identity, expected, desired, () => { });
        r.CompleteModeUpdate(state); Assert.True(r.Journal.Restore(Identity, r.Device.Exchange, () => { }).Restored);
        Assert.Equal(0xdd, r.Device.Order); Assert.Null(r.Journal.Pending);
        var restored = new AsrockVolatileTransaction(PathName, r.Device.Exchange).Snapshot;
        Assert.All(restored.Zones, z => Assert.Equal(desired[z.Address], z.Setting));
    }
    [Theory] [InlineData("mode-name")] [InlineData("flags")] [InlineData("speed")] [InlineData("palette")] [InlineData("identity")]
    public void UnknownOrChangedSdkSemanticsCannotPredictHardwareState(string change)
    {
        using var r = new Rig(); var state = OpenRgbSdkProtocol.Copy(r.Sdk);
        if (change == "mode-name") state.Modes[6] = state.Modes[6] with { Name = "Real Music" };
        if (change == "flags") state.Modes[2] = state.Modes[2] with { Flags = 1 };
        if (change == "speed") state.Modes[2] = state.Modes[2] with { Speed = 256 };
        if (change == "palette") state.Modes[2] = state.Modes[2] with { Colors = [new(1, 2, 3)] };
        if (change == "identity") state = state with { Id = state.Id + 1 };
        Assert.Throws<IOException>(() => r.Plan.PredictCompletedModeUpdate(state)); Assert.Empty(r.Device.Writes);
    }
    [Fact] public void InputsAndReturnedExitSettingsCannotMutateTheCapturedBaseline()
    {
        using var r = new Rig(); r.Before.Counts[2] = 1; r.Before.Zones[0] = r.Before.Zones[0] with { Setting = new(0, 0, 0, 0) };
        var exit = r.Plan.Exit(new LightingExitPolicy(LightingExitAction.RestoreOriginal)); exit[2] = new(1, 0, 0, 0);
        Assert.Equal(new BoardLightSetting(2, 9, 10, 11, 77), r.Plan.Exit(new LightingExitPolicy(LightingExitAction.RestoreOriginal))[2]);
    }
}
