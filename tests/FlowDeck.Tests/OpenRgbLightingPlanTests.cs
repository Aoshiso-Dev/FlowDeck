using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbLightingPlanTests
{
    [Fact]
    public void EmptyPhysicalZonesDoNotShiftOtherOffsetsOrRequireImpossibleSharedSelections()
    {
        var device = Split(); var empty = device.Zones[0] with { Name = "Empty", MinimumLeds = 0, LedCount = 0, Modes = [], ActiveMode = -1 };
        device = device with { ActiveMode = 1, Zones = [empty, device.Zones[0] with { Modes = [], ActiveMode = -1 },
            empty with { Name = "Empty middle" }, device.Zones[1] with { Modes = [], ActiveMode = -1 }] };
        Assert.True(OpenRgbLightingProfile.Partitioned(device));
        Assert.Equal(1, OpenRgbLightingPlan.Offset(device, 3));
        Assert.Equal(new int?[] { null, 1, 3 }, OpenRgbDeviceIdentities.Surfaces(device).Select(s => s.Zone));
        var frame = OpenRgbLightingPlan.SharedFrame(device, new Dictionary<int, RgbColor> { [1] = new(1, 2, 3), [3] = new(40, 50, 60) });
        Assert.Equal(new RgbColor[] { new(1, 2, 3), new(40, 50, 60) }, frame.After.Colors);
        var updated = OpenRgbLightingPlan.WithState(device, frame.After);
        var partial = OpenRgbLightingPlan.SharedFrame(updated, new Dictionary<int, RgbColor> { [3] = new(70, 80, 90) });
        Assert.Equal(new RgbColor(1, 2, 3), partial.After.Colors[0]);
        Assert.Throws<IOException>(() => OpenRgbLightingPlan.SharedFrame(updated, new Dictionary<int, RgbColor> { [0] = new(1, 2, 3) }));
        Assert.Throws<IOException>(() => OpenRgbLightingPlan.SharedExit(updated, partial, null, new Dictionary<int, RgbColor> { [0] = new(1, 2, 3) }));
        var restore = OpenRgbLightingPlan.Restore(updated, OpenRgbLightingPlan.State(device), null);
        Assert.DoesNotContain(restore.Steps, s => s.Colors is { Length: 0 });
    }
    internal static OpenRgbSdkController Device() => OpenRgbSdkProtocol.Controller(41, OpenRgbSdkTestServer.Description());
    internal static OpenRgbSdkController Split()
    {
        var device = Device(); var zone = device.Zones[0] with { LedCount = 1, MinimumLeds = 1, MaximumLeds = 1, Matrix = null, Segments = [] };
        return device with { Zones = [zone, zone with { Name = "Second zone", DisplayName = "Second" }] };
    }
    [Fact]
    public void CommonCapabilitiesUseStableQualifiedIdsAndNormalizedControls()
    {
        var device = Device(); var surface = OpenRgbLightingProfile.Surface(device, null);
        Assert.Equal(0, surface.DirectMode); Assert.Equal(0, surface.FixedMode); Assert.Equal(2, surface.Native.Length);
        var native = surface.Native[1].Effect; Assert.True(DeviceNativeLighting.Valid(native)); Assert.True(native.Direction); Assert.True(native.Brightness);
        Assert.Equal(100, native.MaximumSpeed); Assert.Equal(24, native.DefaultSpeed); Assert.StartsWith("native/openrgb/", native.Id);
        Assert.Equal(native.Id, OpenRgbLightingProfile.Surface(device with { DisplayName = "My label", Serial = "OTHER" }, null).Native[1].Effect.Id);
        Assert.NotEqual(native.Id, OpenRgbLightingProfile.Surface(device with { Vendor = "Another vendor" }, null).Native[1].Effect.Id);
        Assert.NotEqual(native.Id, OpenRgbLightingProfile.Surface(device, 0).Native[1].Effect.Id);
    }
    [Theory]
    [InlineData("Custom")] [InlineData("Static")] [InlineData("Wave")]
    public void PerLedCapabilityAloneDoesNotPromiseDirectStreaming(string name)
    {
        var device = Device(); device = device with { Modes = [device.Modes[0] with { Name = name }] };
        var surface = OpenRgbLightingProfile.Surface(device, null); Assert.Null(surface.DirectMode);
        if (name == "Wave") Assert.Null(surface.FixedMode); else Assert.Equal(0, surface.FixedMode);
        Assert.ThrowsAny<IOException>(() => OpenRgbLightingPlan.Create(device, null, new("fixed", [new(0, 0, 255)]), frame: true));
    }
    [Fact]
    public void AutomaticSaveAndUnknownFlagsAreNotAdvertisedForNativeControl()
    {
        var device = Device(); device = device with { Modes = [device.Modes[0] with { Flags = 32 | 512 }, device.Modes[1] with { Flags = 1u << 31 },
            device.Modes[1] with { Flags = 64 | 2048, Direction = 6 }] };
        var surface = OpenRgbLightingProfile.Surface(device, null); Assert.Empty(surface.Native); Assert.Null(surface.DirectMode);
    }
    [Fact]
    public void ZoneOperationCannotActivateControllerWideEffectOrOperateVirtualLayout()
    {
        var device = Device(); device = device with { Modes = [device.Modes[0] with { Flags = 32 | 1024 }, device.Modes[1]] };
        Assert.ThrowsAny<IOException>(() => OpenRgbLightingPlan.Create(device, 0, new("fixed", [new(1, 2, 3)])));
        var virtualDevice = Device(); virtualDevice = virtualDevice with { Zones = [virtualDevice.Zones[0] with { Flags = 1 }] };
        Assert.ThrowsAny<IOException>(() => OpenRgbLightingProfile.Surface(virtualDevice, 0));
        Assert.Equal(2, OpenRgbLightingProfile.Surface(virtualDevice, null).LedCount);
    }
    [Fact]
    public void NativePaletteSpeedDirectionAndSoftwareBrightnessAreTranslated()
    {
        var device = Device(); var surface = OpenRgbLightingProfile.Surface(device, null); var native = surface.Native[1];
        var mutation = OpenRgbLightingPlan.Create(device, null, new(native.Effect.Id, [new(100, 60, 20)], Speed: 50, Reverse: true, Brightness: 50));
        var step = Assert.Single(mutation.Steps); Assert.Equal(1, step.ModeIndex); Assert.Equal(51u, step.Mode!.Speed); Assert.Equal(1u, step.Mode.Direction);
        Assert.Equal(new RgbColor(50, 30, 10), Assert.Single(step.Mode.Colors)); Assert.Equal(80u, step.Mode.Brightness);
        Assert.Equal(1, mutation.After.Device.Active); Assert.Equal(mutation.Before.Colors, mutation.After.Colors);
    }
    [Fact]
    public void HardwareBrightnessDoesNotAlsoDimColors()
    {
        var device = Device(); device = device with { Modes = [device.Modes[0], device.Modes[1] with { Flags = device.Modes[1].Flags | 16,
            BrightnessMin = 64, BrightnessMax = 192, Brightness = 128 }] };
        var native = OpenRgbLightingProfile.Surface(device, null).Native[1];
        var mutation = OpenRgbLightingPlan.Create(device, null, new(native.Effect.Id, [new(100, 60, 20)], Brightness: 75));
        var mode = Assert.Single(mutation.Steps).Mode!; Assert.Equal(160u, mode.Brightness); Assert.Equal(new RgbColor(100, 60, 20), mode.Colors[0]);
    }
    [Fact]
    public void InvertedAndLargeSdkRangesAreNormalizedWithoutOverflow()
    {
        Assert.Equal(100u, OpenRgbLightingProfile.Scale(0, 100, 1)); Assert.Equal(1u, OpenRgbLightingProfile.Scale(100, 100, 1));
        Assert.Equal(2147483648u, OpenRgbLightingProfile.Scale(50, uint.MaxValue, 0)); Assert.Equal(100, OpenRgbLightingProfile.Percent(1, 100, 1));
        Assert.Equal(0, OpenRgbLightingProfile.Percent(5, 5, 5));
    }
    [Fact]
    public void FixedZonePreservesOtherZonesAndRecordsEachPartialPrefix()
    {
        var device = Split(); var original = OpenRgbLightingPlan.State(device);
        var mutation = OpenRgbLightingPlan.Create(device, 1, new("fixed", [new(0, 0, 200)], Brightness: 50), frame: true);
        Assert.Equal(2, mutation.Steps.Length); Assert.All(mutation.Steps, s => Assert.Equal(1, s.Zone));
        Assert.Equal(original.Colors[0], mutation.After.Colors[0]); Assert.Equal(new RgbColor(0, 0, 100), mutation.After.Colors[1]);
        Assert.Equal(-1, mutation.After.Zones[0].Active); Assert.Equal(0, mutation.After.Zones[1].Active);
        Assert.True(OpenRgbLightingPlan.OwnedPrefix(mutation, original));
        Assert.All(mutation.Prefixes, state => Assert.True(OpenRgbLightingPlan.OwnedPrefix(mutation, state)));
        var foreign = Contract.Copy(mutation.After); foreign.Colors[1] = new(1, 2, 3); Assert.False(OpenRgbLightingPlan.OwnedPrefix(mutation, foreign));
    }
    [Fact]
    public void WholeDeviceOperationRemovesLocalModeOverrides()
    {
        var device = Split(); device = device with { Zones = device.Zones.Select(z => z with { ActiveMode = 1 }).ToArray() };
        var mutation = OpenRgbLightingPlan.Create(device, null, new("fixed", [new(0, 0, 255)]));
        Assert.Equal(new int?[] { -1, -1, 0, null }, mutation.Steps.Select(s => s.ModeIndex));
        Assert.All(mutation.After.Zones, z => Assert.Equal(-1, z.Active));
    }
    [Fact]
    public void PerLedNativeUsesSeparateLedPayloadAndRetainsRequiredModePalette()
    {
        var device = Device(); device = device with { Modes = [device.Modes[0], device.Modes[1] with { Flags = device.Modes[1].Flags | 32 }] };
        var native = OpenRgbLightingProfile.Surface(device, null).Native[1]; Assert.Equal(2, native.Effect.LedCount);
        var mutation = OpenRgbLightingPlan.Create(device, null, new(native.Effect.Id, [new(100, 0, 0)], Brightness: 50, LedColors: [new(100, 20, 30), new(60, 80, 100)]));
        Assert.Equal(1u, mutation.Steps[0].Mode!.ColorMode); Assert.Equal(device.Modes[1].Colors, mutation.Steps[0].Mode!.Colors);
        Assert.Equal(new RgbColor(50, 10, 15), mutation.Steps[1].Colors![0]);
    }
    [Fact]
    public void RestoreIncludesChangedInactiveModeParametersAndOriginalActiveMode()
    {
        var device = Device(); var original = OpenRgbLightingPlan.State(device); var native = OpenRgbLightingProfile.Surface(device, null).Native[1];
        var applied = OpenRgbLightingPlan.Create(device, null, new(native.Effect.Id, [new(1, 2, 3)], Speed: 80));
        var current = OpenRgbLightingPlan.WithState(device, applied.After);
        var restore = OpenRgbLightingPlan.Restore(current, original, null);
        Assert.True(OpenRgbLightingPlan.Same(original, restore.After));
        Assert.Contains(restore.Steps, s => s.Zone is null && s.ModeIndex == 1 && s.Mode!.Speed == 25);
        Assert.Contains(restore.Steps, s => s.Zone is null && s.ModeIndex == 0);
        Assert.Contains(restore.Steps, s => s.Zone == 0 && s.ModeIndex == -1 && s.Mode is null);
    }
    [Fact]
    public void RestoreZonePreservesSiblingAndReturnsToInheritance()
    {
        var device = Split(); var original = OpenRgbLightingPlan.State(device);
        var applied = OpenRgbLightingPlan.Create(device, 0, new("fixed", [new(0, 0, 0)]));
        var changed = Contract.Copy(applied.After); changed.Colors[1] = new(1, 2, 3);
        var restore = OpenRgbLightingPlan.Restore(OpenRgbLightingPlan.WithState(device, changed), original, 0);
        Assert.All(restore.Steps, s => Assert.Equal(0, s.Zone)); Assert.Equal(-1, restore.After.Zones[0].Active);
        Assert.Equal(original.Colors[0], restore.After.Colors[0]); Assert.Equal(new RgbColor(1, 2, 3), restore.After.Colors[1]);
    }
    [Fact]
    public void NativeRestoreReactivatesModeAfterColorWrites()
    {
        var device = Device() with { ActiveMode = 1 }; var original = OpenRgbLightingPlan.State(device);
        var applied = OpenRgbLightingPlan.Create(device, null, new("fixed", [new(0, 0, 255)]));
        var restore = OpenRgbLightingPlan.Restore(OpenRgbLightingPlan.WithState(device, applied.After), original, null);
        Assert.NotNull(restore.Steps[0].Colors); Assert.Equal(1, restore.Steps.First(s => s.Zone is null && s.ModeIndex is not null).ModeIndex);
        Assert.DoesNotContain(restore.Steps.Skip(1), s => s.Colors is not null);
        Assert.True(OpenRgbLightingPlan.Same(original, restore.After));
    }
    [Fact]
    public void RepeatedDirectFramesDoNotResetTheHardwareMode()
    {
        var device = Split(); var first = OpenRgbLightingPlan.Create(device, 0, new("fixed", [new(10, 20, 30)]), frame: true);
        var current = OpenRgbLightingPlan.WithState(device, first.After);
        var second = OpenRgbLightingPlan.Create(current, 0, new("fixed", [new(40, 50, 60)]), frame: true);
        var step = Assert.Single(second.Steps); Assert.Null(step.ModeIndex); Assert.Equal(0, step.Zone);
        Assert.Equal(new RgbColor(40, 50, 60), Assert.Single(step.Colors!));
    }
    [Fact]
    public void ConstantHardwareRangesDoNotAdvertiseNonfunctionalSliders()
    {
        var device = Device(); device = device with { Modes = [device.Modes[1] with { Flags = device.Modes[1].Flags | 16,
            SpeedMin = 25, SpeedMax = 25, BrightnessMin = 80, BrightnessMax = 80 }] };
        var effect = Assert.Single(OpenRgbLightingProfile.Surface(device, null).Native).Effect;
        Assert.False(effect.Speed); Assert.Equal(0, effect.MaximumSpeed); Assert.True(effect.Brightness);
        var plan = OpenRgbLightingPlan.Create(device, null, new(effect.Id, [new(100, 60, 20)], Speed: 0, Brightness: 50));
        Assert.Equal(80u, plan.Steps[0].Mode!.Brightness); Assert.Equal(new RgbColor(50, 30, 10), plan.Steps[0].Mode!.Colors[0]);
    }
    [Fact]
    public void LabelsAreBoundedAndDoNotLeakControlCharacters()
    {
        Assert.Equal("LED", OpenRgbLightingProfile.Label("\r\n", "LED")); Assert.Equal("Name", OpenRgbLightingProfile.Label("Na\0me", "LED"));
        Assert.Equal(80, OpenRgbLightingProfile.Label(new string('a', 120), "LED").Length);
    }
    [Fact]
    public async Task InheritanceUsesDedicatedShortSdkPacket()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        await OpenRgbLightingPlan.SendStepAsync(connection, target, new(0, -1), () => { }, default);
        var packet = Assert.Single(server.Requests, p => p.Command == 1103);
        Assert.Equal(new byte[] { 12, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255 }, packet.Data);
    }
}
