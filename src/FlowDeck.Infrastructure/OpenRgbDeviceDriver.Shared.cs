using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceDriver
{
    // A whole-controller journal owns the mode and background. Logical endpoint owners still live
    // in the common runtime. All methods below run under io; observation uses only gate/cached data.
    private sealed class SharedControl(OpenRgbBoundDevice captured, PhysicalDeviceBinding binding)
    {
        public readonly OpenRgbBoundDevice Captured = captured;
        public readonly PhysicalDeviceBinding Binding = binding;
        public readonly Dictionary<string, int> Controlled = [];
        public readonly Dictionary<string, RgbColor> Exits = [];
        public readonly Dictionary<string, LightingExitPolicy> ExitPolicies = [];
    }
    private SharedControl? shared;
    private string? ValidateShared(string[] endpoints)
    {
        if (current is null || endpoints.Length == 0 || endpoints.Any(id => !ports.TryGetValue(id, out var p) || !p.ControllerDirect))
            return "共有Directの対象が不正です";
        if (slots.Values.Any(s => s.Key >= 0 && s.Journal.Pending is not null) || shared is null && slots[-1].Journal.Pending is not null)
            return "以前のOpenRGB照明を終了してから設定してください";
        if (endpoints.Any(id => !settings.TryGetValue(id, out var s) || s.Config.Exit?.Action is not (LightingExitAction.FixedColor or LightingExitAction.Off)))
            return "この機器のゾーン制御では、終了方法を固定色または消灯にしてください";
        try
        {
            _ = OpenRgbLightingPlan.SharedFrame(current.Snapshot.Controller, endpoints.ToDictionary(id => ports[id].Zone!.Value, _ => new RgbColor(0, 0, 0)));
            return null;
        }
        catch (IOException e) { return e.Message; }
    }
    private async Task<DeviceDriverResult> WriteSharedAsync(Dictionary<string, RgbColor> colors, Action authorize, CancellationToken ct,
        Dictionary<string, RgbColor>? newExits = null, Dictionary<string, LightingExitPolicy>? newExitPolicies = null)
    {
        SharedControl control; Slot slot; OpenRgbLightingExecutor executor;
        Dictionary<int, RgbColor> values, exits; Dictionary<string, int> indices; Dictionary<string, RgbColor> chosenExits;
        Dictionary<string, LightingExitPolicy> chosenPolicies;
        lock (gate)
        {
            foreach (var id in colors.Keys)
            {
                if (!ports.TryGetValue(id, out var port)) return new(Outcome.Rejected, "未検出のOpenRGBゾーンです");
                if (Ready(port) is { } error) return new(Outcome.Rejected, error);
            }
            if (ValidateShared(colors.Keys.ToArray()) is { } invalid) return new(Outcome.Rejected, invalid);
            control = shared ??= new(current!, Candidate()!);
            slot = slots[-1]; executor = Executor(slot); slot.Owned = true;
            indices = colors.Keys.ToDictionary(id => id, id => ports[id].Zone!.Value);
            values = colors.ToDictionary(p => indices[p.Key], p => p.Value);
            chosenExits = colors.Keys.ToDictionary(id => id, id => newExits?.GetValueOrDefault(id) ?? control.Exits.GetValueOrDefault(id) ??
                DeviceLighting.StopColor(DeviceLightingConfiguration.ExitProgram(settings[id].Config.Exit!)));
            exits = chosenExits.ToDictionary(p => indices[p.Key], p => p.Value);
            chosenPolicies = colors.Keys.ToDictionary(id => id, id => newExitPolicies?.GetValueOrDefault(id) ??
                (newExits?.ContainsKey(id) == true ? new(LightingExitAction.FixedColor, chosenExits[id]) :
                    control.ExitPolicies.GetValueOrDefault(id) ?? Contract.Copy(settings[id].Config.Exit!)));
        }
        var previousHash = slot.Journal.Pending?.Hash;
        void Check() { Guard(control.Captured, control.Binding); authorize(); }
        var result = await executor.ApplySharedAsync(values, exits, Check, ct, chosenPolicies.ToDictionary(p => indices[p.Key], p => p.Value)).ConfigureAwait(false); Cache(result);
        lock (gate)
        {
            slot.Owned = slot.Journal.Pending is not null; slot.Faulted = executor.NeedsRecovery;
            if (slot.Journal.Pending?.Hash is { } hash && hash != previousHash)
                foreach (var id in colors.Keys) { control.Controlled[id] = indices[id]; control.Exits[id] = chosenExits[id]; control.ExitPolicies[id] = chosenPolicies[id]; }
            if (control.Controlled.Count == 0 && slot.Journal.Pending is null) shared = null;
        }
        return result.Result;
    }
    private void EndSharedTarget(string id, DeviceDriverResult result)
    {
        if (!Success(result)) return;
        if (streams.Remove(id, out var stream)) stream.Ended[id] = result;
        shared?.Controlled.Remove(id); shared?.Exits.Remove(id); shared?.ExitPolicies.Remove(id);
    }
    private async Task<DeviceDriverReleaseResult[]> RecoverSharedAsync(CancellationToken ct)
    {
        string[] targets; Dictionary<string, int> indices;
        var pending = slots[-1].Journal.Pending;
        lock (gate)
        {
            targets = (shared?.Controlled.Keys.AsEnumerable() ?? []).Union(streams.Where(p => p.Value.Shared).Select(p => p.Key)).ToArray();
            indices = targets.ToDictionary(id => id, id => shared is { } s && s.Controlled.TryGetValue(id, out var z) ? z : streams[id].Keys[id]);
        }
        var result = await RecoverCoreAsync(slots[-1], null, ct).ConfigureAwait(false);
        lock (gate)
        {
            // Only SDK-accepted recovery has a report. File-only cleanup uses its persisted accepted
            // state, not a later cache observation. Unsent or failed recovery must not invent a color.
            var reported = pending?.Phase == OpenRgbRecoveryPhase.Accepted ? pending.Mutation.After :
                pending is not null && current is not null ? OpenRgbLightingPlan.State(current.Snapshot.Controller) : null;
            LightingProgram? Program(string id)
            {
                if (!Success(result) || reported is null || pending is null || !indices.TryGetValue(id, out var z) ||
                    z < 0 || z >= pending.ZoneLeds.Length || reported.Zones[z].Active >= 0 ||
                    reported.Device.Active < 0 || reported.Device.Active >= reported.Device.Modes.Length ||
                    !reported.Device.Modes[reported.Device.Active].Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)) return null;
                var offset = checked((int)pending.ZoneLeds.Take(z).Sum(n => (long)n)); var count = checked((int)pending.ZoneLeds[z]);
                var colors = reported.Colors.Skip(offset).Take(count).Distinct().ToArray();
                return colors.Length == 1 ? new("fixed", colors) : null;
            }
            var rows = (targets.Length == 0 ? ["device"] : targets).Select(id => new DeviceDriverReleaseResult(id,
                result with { ReportedLighting = Program(id) })).ToArray();
            if (Success(result)) { foreach (var row in rows) EndSharedTarget(row.EndpointId, row.Result); shared = null; }
            return rows;
        }
    }
    private async Task<DeviceDriverReleaseResult[]> EndSharedAsync(string[] targets, DeviceLightingStopReason reason, CancellationToken ct)
    {
        var results = new List<DeviceDriverReleaseResult>(); SharedControl? control; bool recover;
        lock (gate)
        {
            control = shared;
            foreach (var id in targets.Where(id => control?.Controlled.ContainsKey(id) != true))
            {
                var unchanged = new DeviceDriverResult(Outcome.Confirmed, "未送信のOpenRGB共有同期を終了しました");
                EndSharedTarget(id, unchanged); results.Add(new(id, unchanged));
            }
            recover = control is not null && (slots[-1].Faulted || !slots[-1].Owned || slots[-1].Journal.Pending?.Phase != OpenRgbRecoveryPhase.Active ||
                current?.Snapshot.Target != control.Captured.Snapshot.Target);
        }
        var live = targets.Except(results.Select(r => r.EndpointId)).ToArray();
        if (live.Length == 0) return results.ToArray();
        if (recover)
        {
            var ended = await RecoverSharedAsync(ct).ConfigureAwait(false);
            results.AddRange(live.Select(id => ended.SingleOrDefault(r => r.EndpointId == id) ?? new(id, new(Outcome.Unknown, "OpenRGBの共有対象を復旧できません"))));
            return results.ToArray();
        }
        Dictionary<string, RgbColor> colors;
        Dictionary<string, LightingExitPolicy> policies;
        lock (gate)
        {
            policies = live.ToDictionary(id => id, id => reason == DeviceLightingStopReason.Normal && streams.GetValueOrDefault(id)?.StopColors.GetValueOrDefault(id) is { } normal
                ? new LightingExitPolicy(LightingExitAction.FixedColor, normal) : control!.ExitPolicies[id]);
            colors = policies.ToDictionary(p => p.Key, p => DeviceLighting.StopColor(DeviceLightingConfiguration.ExitProgram(p.Value)));
        }
        var hardware = execution.Value;
        var result = await WriteSharedAsync(colors, () => { hardware?.CheckAuthorization(); ct.ThrowIfCancellationRequested(); }, ct, colors, policies).ConfigureAwait(false);
        if (!Success(result)) return [.. results, .. live.Select(id => new DeviceDriverReleaseResult(id, result))];
        bool last;
        lock (gate) last = shared!.Controlled.Keys.All(live.Contains);
        if (last)
        {
            var ended = await RecoverSharedAsync(ct).ConfigureAwait(false);
            return [.. results, .. live.Select(id => ended.Single(r => r.EndpointId == id))];
        }
        lock (gate)
            foreach (var id in live)
            {
                var value = result with { ReportedLighting = new("fixed", [colors[id]]) }; EndSharedTarget(id, value); results.Add(new(id, value));
            }
        return results.ToArray();
    }
}
