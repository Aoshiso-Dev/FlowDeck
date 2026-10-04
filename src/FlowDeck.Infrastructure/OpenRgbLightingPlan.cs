using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// State excludes serials, locations and device-specific configuration. Those are represented by the binding hash.
internal sealed record OpenRgbModeState(int Active, OpenRgbSdkMode[] Modes);
internal sealed record OpenRgbLightingState(OpenRgbModeState Device, OpenRgbModeState[] Zones, RgbColor[] Colors);
internal sealed record OpenRgbLightingStep(int? Zone, int? ModeIndex = null, OpenRgbSdkMode? Mode = null, RgbColor[]? Colors = null);
internal sealed record OpenRgbLightingMutation(OpenRgbLightingState Before, OpenRgbLightingStep[] Steps, OpenRgbLightingState[] Prefixes)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public OpenRgbLightingState After => Prefixes.Length == 0 ? Before : Prefixes[^1];
}

internal static class OpenRgbLightingPlan
{
    public static OpenRgbLightingState State(OpenRgbSdkController controller) => Contract.Copy(new OpenRgbLightingState(
        new(controller.ActiveMode, controller.Modes), controller.Zones.Select(z => new OpenRgbModeState(z.ActiveMode, z.Modes)).ToArray(), controller.Colors));
    public static bool Same(OpenRgbLightingState a, OpenRgbLightingState b) => SettingsTransfer.Hash(a) == SettingsTransfer.Hash(b);
    public static bool OwnedPrefix(OpenRgbLightingMutation mutation, OpenRgbLightingState state) => Same(mutation.Before, state) || mutation.Prefixes.Any(p => Same(p, state));

    // Some SDK controllers expose Direct only on the whole device. Transport full LED arrays while
    // preserving untouched zones; never assume their native animation can survive a global mode change.
    public static OpenRgbLightingMutation SharedFrame(OpenRgbSdkController controller, IReadOnlyDictionary<int, RgbColor> colors)
    {
        if (!OpenRgbLightingProfile.Partitioned(controller) || colors.Count == 0 || colors.Keys.Any(z => z < 0 || z >= controller.Zones.Length || controller.Zones[z].LedCount == 0) ||
            colors.Values.Any(c => c?.Valid != true)) throw new IOException("OpenRGBの共有ゾーン・色が不正です");
        var index = OpenRgbLightingProfile.Surface(controller, null).DirectMode ?? throw new IOException("機器全体のDirect効果がありません");
        var basis = controller.Modes[index];
        var mode = basis with { ColorMode = 1, Brightness = (basis.Flags & 16) != 0 ? basis.BrightnessMax : basis.Brightness };
        var sameMode = controller.ActiveMode == index && SettingsTransfer.Hash(basis) == SettingsTransfer.Hash(mode);
        for (var z = 0; z < controller.Zones.Length; z++)
        {
            if (colors.ContainsKey(z) || controller.Zones[z].LedCount == 0) continue;
            var zone = controller.Zones[z];
            if (!sameMode) throw new IOException("この機器の照明を変更するには、全ゾーンを選択してください");
            if (zone.ActiveMode >= 0) throw new IOException("対象外の照明の効果が変わるため、全ゾーンを選択してください");
        }
        var steps = new List<OpenRgbLightingStep>();
        foreach (var zone in colors.Keys.Order())
            if (controller.Zones[zone].ActiveMode >= 0) steps.Add(new(zone, -1));
        if (!sameMode) steps.Add(new(null, index, mode));
        var full = controller.Colors.ToArray();
        foreach (var (zone, color) in colors) Array.Fill(full, color, Offset(controller, zone), (int)controller.Zones[zone].LedCount);
        steps.Add(new(null, Colors: full));
        return Build(controller, State(controller), steps.ToArray());
    }

    public static OpenRgbLightingState SharedExit(OpenRgbSdkController shape, OpenRgbLightingMutation mutation, OpenRgbRecoveryRecord? previous,
        IReadOnlyDictionary<int, RgbColor> exits)
    {
        // Unchanged scopes retain their saved exit/background, not a transient animation frame.
        var state = Contract.Copy(mutation.After); var colors = (previous?.Exit.Colors ?? mutation.Before.Colors).ToArray();
        foreach (var (zone, color) in exits)
        {
            if (zone < 0 || zone >= shape.Zones.Length || shape.Zones[zone].LedCount == 0 || color?.Valid != true) throw new IOException("OpenRGBの終了色が不正です");
            Array.Fill(colors, color, Offset(shape, zone), (int)shape.Zones[zone].LedCount);
        }
        return state with { Colors = colors };
    }

    public static OpenRgbLightingMutation Create(OpenRgbSdkController controller, int? zone, LightingProgram program, bool frame = false)
    {
        if (!DeviceConfigurationRules.ValidProgram(program)) throw new IOException("照明設定が不正です");
        var surface = OpenRgbLightingProfile.Surface(controller, zone); var steps = new List<OpenRgbLightingStep>();
        if (surface.LedCount == 0) throw new IOException("OpenRGBのLEDを検出できません");
        if (zone is null)
            for (var i = 0; i < controller.Zones.Length; i++)
                if (controller.Zones[i].Modes.Length > 0 && controller.Zones[i].ActiveMode >= 0) steps.Add(new(i, -1));
        if (zone is { } zoneIndex && controller.ActiveMode >= 0 && controller.ActiveMode < controller.Modes.Length &&
            (controller.Modes[controller.ActiveMode].Flags & 1024) != 0) throw new IOException("機器全体の効果を終了してからゾーンを操作してください");
        if (program.EffectId.StartsWith("native/", StringComparison.Ordinal))
        {
            if (frame) throw new IOException("内蔵効果をソフトウェア同期として送信できません");
            var native = OpenRgbLightingProfile.Native(controller, surface, program);
            steps.Add(new(zone, native.Index, native.Mode)); if (native.Leds is not null) steps.Add(new(zone, Colors: native.Leds));
        }
        else
        {
            if (!DeviceLighting.FrameProgram(program) || DeviceLighting.Animated(program)) throw new IOException("共通効果は計算済みの色を送信してください");
            var index = (frame ? surface.DirectMode : surface.FixedMode) ?? throw new IOException(frame ? "OpenRGBのDirect効果が必要です" : "OpenRGBに対応する固定色効果がありません");
            var basis = (zone is { } z ? controller.Zones[z].Modes : controller.Modes)[index];
            var mode = basis with { ColorMode = 1, Brightness = (basis.Flags & 16) != 0 ? basis.BrightnessMax : basis.Brightness };
            OpenRgbSdkConnection.CheckMode(basis, mode);
            var active = zone is { } scope ? controller.Zones[scope].ActiveMode : controller.ActiveMode;
            if (!frame || active != index || SettingsTransfer.Hash(basis) != SettingsTransfer.Hash(mode)) steps.Add(new(zone, index, mode));
            steps.Add(new(zone, Colors: Enumerable.Repeat(DeviceLighting.StopColor(program), surface.LedCount).ToArray()));
        }
        return Build(controller, State(controller), steps.ToArray());
    }

    public static OpenRgbLightingMutation Restore(OpenRgbSdkController current, OpenRgbLightingState original, int? zone)
    {
        var before = State(current); var steps = new List<OpenRgbLightingStep>();
        if (original.Device.Modes.Length != before.Device.Modes.Length || original.Zones.Length != before.Zones.Length || original.Colors.Length != before.Colors.Length)
            throw new IOException("OpenRGBの復元対象の構成が変わりました");
        void Modes(int? scope, OpenRgbModeState from, OpenRgbModeState to)
        {
            if (from.Modes.Length != to.Modes.Length) throw new IOException("OpenRGBの復元効果数が変わりました");
            if (to.Modes.Length == 0 && from.Active == to.Active) return;
            for (var i = 0; i < from.Modes.Length; i++)
                if (SettingsTransfer.Hash(from.Modes[i]) != SettingsTransfer.Hash(to.Modes[i])) steps.Add(new(scope, i, to.Modes[i]));
            // Reapply the desired active mode even if the SDK's cache already matches it.
            if (to.Active >= 0 || scope is not null)
            {
                if (to.Active < -1 || to.Active >= to.Modes.Length) throw new IOException("OpenRGBの元効果が範囲外です");
                steps.Add(new(scope, to.Active, to.Active >= 0 ? to.Modes[to.Active] : null));
            }
            else if (from.Active != to.Active || steps.Any(s => s.Zone is null && s.ModeIndex is not null)) throw new IOException("OpenRGBの機器全体の元効果を復元できません");
        }
        bool PerLed(int? target)
        {
            var scope = target is { } z && original.Zones[z].Modes.Length > 0 && original.Zones[z].Active >= 0 ? original.Zones[z] : original.Device;
            return scope.Active >= 0 && scope.Active < scope.Modes.Length && scope.Modes[scope.Active].ColorMode == 1;
        }
        if (zone is { } z)
        {
            _ = OpenRgbLightingProfile.Surface(current, zone);
            // A local restore cannot silently alter controller-wide effects or other zones.
            if (SettingsTransfer.Hash(before.Device) != SettingsTransfer.Hash(original.Device)) throw new IOException("機器全体の効果が変更されています");
            var offset = Offset(current, z);
            var colors = original.Colors.Skip(offset).Take((int)current.Zones[z].LedCount).ToArray();
            if (!PerLed(z)) steps.Add(new(z, Colors: colors));
            Modes(z, before.Zones[z], original.Zones[z]);
            if (PerLed(z)) steps.Add(new(z, Colors: colors));
        }
        else
        {
            // Native modes must be reactivated after updating their cached LED colors. Direct zones
            // get a final LED write after mode restoration, without overwriting native siblings.
            steps.Add(new(null, Colors: original.Colors.ToArray()));
            Modes(null, before.Device, original.Device);
            for (var i = 0; i < before.Zones.Length; i++) Modes(i, before.Zones[i], original.Zones[i]);
            if (OpenRgbLightingProfile.Partitioned(current))
            {
                for (var i = 0; i < before.Zones.Length; i++)
                    if (current.Zones[i].LedCount > 0 && PerLed(i)) steps.Add(new(i, Colors: original.Colors.Skip(Offset(current, i)).Take((int)current.Zones[i].LedCount).ToArray()));
            }
            else if (PerLed(null)) steps.Add(new(null, Colors: original.Colors.ToArray()));
        }
        return Build(current, before, steps.ToArray());
    }
    internal static int Offset(OpenRgbSdkController controller, int zone) => checked((int)controller.Zones.Take(zone).Sum(z => (long)z.LedCount));
    public static OpenRgbLightingMutation Build(OpenRgbSdkController controller, OpenRgbLightingState before, OpenRgbLightingStep[] steps)
    {
        if (steps.Length > 512 || !Same(State(controller), before)) throw new IOException("OpenRGBの変更前状態が一致しません");
        var next = Contract.Copy(before); var prefixes = new List<OpenRgbLightingState>();
        foreach (var step in steps)
        {
            if (step.Zone is { } z && (z < 0 || z >= next.Zones.Length)) throw new IOException("OpenRGBのゾーンが範囲外です");
            if (step.ModeIndex is { } index)
            {
                if (step.Colors is not null) throw new IOException("OpenRGBの変更種別が重複しています");
                var scope = step.Zone is { } zone ? next.Zones[zone] : next.Device;
                if (index == -1 && step.Zone is not null && step.Mode is null) scope = scope with { Active = -1 };
                else
                {
                    if (index < 0 || index >= scope.Modes.Length || step.Mode is null) throw new IOException("OpenRGBの変更効果が範囲外です");
                    if (step.Zone is not null && (step.Mode.Flags & 1024) != 0) throw new IOException("機器全体の効果をゾーン操作では適用できません");
                    OpenRgbSdkConnection.CheckMode(scope.Modes[index], step.Mode);
                    var modes = scope.Modes.ToArray(); modes[index] = Contract.Copy(step.Mode); scope = new(index, modes);
                }
                if (step.Zone is { } target) next.Zones[target] = scope; else next = next with { Device = scope };
            }
            else if (step.Mode is null && step.Colors is { } colors && colors.Length > 0 && colors.All(c => c?.Valid == true))
            {
                var surface = OpenRgbLightingProfile.Surface(controller, step.Zone);
                if (colors.Length != surface.LedCount) throw new IOException("OpenRGBの復元色数が一致しません");
                colors.CopyTo(next.Colors, step.Zone is { } target ? Offset(controller, target) : 0);
            }
            else throw new IOException("OpenRGBの変更手順が不正です");
            prefixes.Add(Contract.Copy(next));
        }
        return new(Contract.Copy(before), Contract.Copy(steps), prefixes.ToArray());
    }
    public static OpenRgbSdkController WithState(OpenRgbSdkController shape, OpenRgbLightingState state) => shape with
    { ActiveMode = state.Device.Active, Modes = state.Device.Modes, Colors = state.Colors,
        Zones = shape.Zones.Select((z, i) => z with { ActiveMode = state.Zones[i].Active, Modes = state.Zones[i].Modes }).ToArray() };

    public static async Task SendStepAsync(OpenRgbSdkConnection connection, OpenRgbSdkTarget target, OpenRgbLightingStep step, Action authorize, CancellationToken ct)
    {
        if (step.ModeIndex == -1 && step.Zone is { } inherited) await connection.InheritControllerModeAsync(target, inherited, authorize, ct).ConfigureAwait(false);
        else if (step.ModeIndex is { } index && step.Mode is { } mode) await connection.WriteModeAsync(target, index, mode, authorize, step.Zone, ct).ConfigureAwait(false);
        else if (step.Colors is { } colors) await connection.WriteColorsAsync(target, colors, authorize, step.Zone, ct).ConfigureAwait(false);
        else throw new IOException("OpenRGBの送信手順が不正です");
    }
}
