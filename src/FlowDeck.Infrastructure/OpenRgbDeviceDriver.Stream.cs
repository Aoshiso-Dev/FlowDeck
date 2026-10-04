using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceDriver
{
    public string? ValidateLightingStream(string[] endpoints, LightingProgram program)
    {
        lock (gate)
        {
            if (!DeviceLighting.ValidAnimation(program) || endpoints is not { Length: > 0 and <= 256 } || endpoints.Distinct().Count() != endpoints.Length)
                return "OpenRGBの同期設定が不正です";
            if (endpoints.Length > 1 && endpoints.Contains("device")) return "機器全体と個別ゾーンを同時には選択できません";
            foreach (var id in endpoints)
            {
                if (!ports.TryGetValue(id, out var port) || port.DirectMode is null && !port.ControllerDirect) return "OpenRGBのDirect同期に対応していません";
                if (Ready(port) is { } error) return error;
                try { if (!port.ControllerDirect) _ = OpenRgbLightingPlan.Create(current!.Snapshot.Controller, port.Zone, new("fixed", [new(0, 0, 0)]), frame: true); }
                catch (IOException e) { return e.Message; }
            }
            if (endpoints.All(id => ports[id].ControllerDirect)) return ValidateShared(endpoints);
            return null;
        }
    }
    public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
    {
        endpoints = endpoints.ToArray(); program = Contract.Copy(program);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (ValidateLightingStream(endpoints, program) is { } error) throw new IOException(error);
                if (endpoints.Any(streams.ContainsKey)) throw new IOException("OpenRGBの選択対象は同期中です");
                var stream = new Stream(this, current!, Candidate()!, endpoints, program);
                foreach (var id in endpoints) streams.Add(id, stream);
                return stream;
            }
        }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverReleaseResult[]> EndCoreAsync(string[] endpoints, DeviceLightingStopReason reason, CancellationToken ct)
    {
        var results = new List<DeviceDriverReleaseResult>();
        string[] sharedTargets;
        lock (gate) sharedTargets = endpoints.Where(id => streams.GetValueOrDefault(id)?.Shared == true || shared?.Controlled.ContainsKey(id) == true).ToArray();
        if (sharedTargets.Length > 0) results.AddRange(await EndSharedAsync(sharedTargets, reason, ct).ConfigureAwait(false));
        foreach (var endpoint in endpoints.Except(sharedTargets))
        {
            Slot slot; Stream? stream; RgbColor? stop;
            lock (gate)
            {
                stream = streams.GetValueOrDefault(endpoint); stop = stream?.StopColors.GetValueOrDefault(endpoint);
                if (stream is not null) slot = slots[stream.Keys[endpoint]];
                else if (ports.TryGetValue(endpoint, out var port)) slot = slots[Key(port)];
                else { results.Add(new(endpoint, new(Outcome.Unknown, "OpenRGBの端子構成が変わりました"))); continue; }
                if (stream is not null && !stream.Changed.Contains(endpoint))
                {
                    streams.Remove(endpoint); var unchanged = new DeviceDriverResult(Outcome.Confirmed, "未送信のOpenRGB同期を終了しました");
                    stream.Ended[endpoint] = unchanged; results.Add(new(endpoint, unchanged)); continue;
                }
            }
            var result = await RecoverCoreAsync(slot, reason == DeviceLightingStopReason.Normal && stop is not null ? new("fixed", [stop]) : null, ct).ConfigureAwait(false);
            results.Add(new(endpoint, result));
            lock (gate) if (Success(result)) { streams.Remove(endpoint); if (stream is not null) stream.Ended[endpoint] = result; }
        }
        return results.ToArray();
    }
    private sealed class Stream(OpenRgbDeviceDriver driver, OpenRgbBoundDevice captured, PhysicalDeviceBinding binding,
        string[] targets, LightingProgram program) : ISelectiveDeviceLightingStream
    {
        public readonly Dictionary<string, DeviceDriverResult> Ended = [];
        private readonly HashSet<string> detached = [];
        public readonly Dictionary<string, RgbColor> StopColors = [];
        public readonly HashSet<string> Changed = [];
        public readonly Dictionary<string, int> Keys = targets.ToDictionary(id => id, id => Key(driver.ports[id]));
        public readonly bool Shared = targets.All(id => driver.ports[id].ControllerDirect);
        private long sequence = -1;
        public async Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct)
        {
            await driver.io.WaitAsync(ct).ConfigureAwait(false);
            string[] selected;
            lock (driver.gate) selected = targets.Where(t => !Ended.ContainsKey(t)).ToArray();
            DeviceDriverReleaseResult[] Reject(string error, DeviceDriverFailure failure = DeviceDriverFailure.None) =>
                selected.Select(t => new DeviceDriverReleaseResult(t, new(Outcome.Rejected, error, Failure: failure))).ToArray();
            try
            {
                if (selected.Length == 0) return targets.Where(t => !detached.Contains(t)).Select(t => new DeviceDriverReleaseResult(t, new(Outcome.Rejected, "OpenRGBの同期は終了しました"))).ToArray();
                if (frame.TargetError(selected) is { } targetError) return Reject(targetError);
                if (frame.Sequence <= sequence || selected.Any(t => frame.ColorFor(t)?.Valid != true)) return Reject("古いまたは不正なOpenRGBフレームです");
                sequence = frame.Sequence;
                void Authorize()
                {
                    driver.Guard(captured, binding); ct.ThrowIfCancellationRequested();
                    lock (driver.gate) if (driver.closing) throw new IOException("OpenRGBの終了・復旧中です");
                    lock (driver.gate) if (selected.Any(t => driver.streams.GetValueOrDefault(t) != this)) throw new IOException("OpenRGBの同期所有が変更されています");
                    // The service callback is outside locks used by Observe.
                    var authorization = frame.Authorize();
                    if (authorization.Expired || authorization.ValidFor is { } left && left <= TimeSpan.Zero)
                        throw new OpenRgbLightingExpiredException(authorization.Error ?? "照明フレームの期限が切れました");
                    if (authorization.Error is not null) throw new IOException(authorization.Error);
                    if (authorization.ValidFor is null) throw new IOException("照明フレームの期限がありません");
                }
                Authorize();
                if (Shared)
                {
                    var before = driver.slots[-1].Journal.Pending?.Hash;
                    var answer = await driver.WriteSharedAsync(selected.ToDictionary(id => id, frame.ColorFor), Authorize, ct).ConfigureAwait(false);
                    lock (driver.gate)
                    {
                        if (driver.slots[-1].Journal.Pending?.Hash is { } after && after != before) Changed.UnionWith(selected);
                        if (Success(answer)) foreach (var id in selected) StopColors[id] = frame.NormalStopColorFor(id) ?? DeviceLighting.StopColor(program);
                    }
                    return selected.Select(id => new DeviceDriverReleaseResult(id, answer with
                    { ReportedLighting = Success(answer) ? new("fixed", [frame.ColorFor(id)]) : null })).ToArray();
                }
                var results = new List<DeviceDriverReleaseResult>(); var stopped = false;
                foreach (var id in selected)
                {
                    if (stopped) { results.Add(new(id, new(Outcome.Rejected, "同じ同期の送信を中止しました"))); continue; }
                    OpenRgbLightingSurface port; Slot slot; string? before;
                    lock (driver.gate) { port = driver.ports[id]; slot = driver.slots[Key(port)]; before = slot.Journal.Pending?.Hash; }
                    var result = await driver.ApplyCoreAsync(port, new("fixed", [frame.ColorFor(id)]), Authorize, true, ct).ConfigureAwait(false);
                    results.Add(new(id, result));
                    lock (driver.gate)
                    {
                        if (slot.Journal.Pending?.Hash is { } after && after != before) Changed.Add(id);
                        if (Success(result)) StopColors[id] = frame.NormalStopColorFor(id) ?? DeviceLighting.StopColor(program);
                    }
                    stopped = !Success(result);
                }
                return results.ToArray();
            }
            catch (OpenRgbLightingExpiredException e) { return Reject(e.Message, DeviceDriverFailure.LightingFrameExpired); }
            catch (Exception e) { return Reject(OpenRgbLightingProfile.Label(e.Message, "OpenRGBの同期に失敗しました", 400)); }
            finally { driver.io.Release(); }
        }
        public Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct) => EndAsync(null, reason, ct);
        public Task<DeviceDriverReleaseResult[]> EndSelectedAsync(string[] endpoints, CancellationToken ct) => EndAsync(endpoints.ToArray(), DeviceLightingStopReason.Normal, ct);
        private async Task<DeviceDriverReleaseResult[]> EndAsync(string[]? selection, DeviceLightingStopReason reason, CancellationToken ct)
        {
            await driver.io.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string[] requested; lock (driver.gate) requested = selection ?? targets.Where(t => !detached.Contains(t)).ToArray();
                if (requested.Length == 0 && selection is null) return [];
                if (requested.Length == 0 || requested.Distinct().Count() != requested.Length || requested.Except(targets).Any())
                    return requested.Select(t => new DeviceDriverReleaseResult(t, new(Outcome.Rejected, "OpenRGBの終了対象が不正です"))).ToArray();
                string[] live; lock (driver.gate) live = requested.Where(t => !Ended.ContainsKey(t)).ToArray();
                var results = await driver.EndCoreAsync(live, reason, ct).ConfigureAwait(false);
                lock (driver.gate)
                {
                    var complete = requested.Select(t => results.SingleOrDefault(r => r.EndpointId == t) ?? new(t, Contract.Copy(Ended[t]))).ToArray();
                    if (selection is not null) foreach (var result in complete) if (Success(result.Result)) detached.Add(result.EndpointId);
                    return complete;
                }
            }
            finally { driver.io.Release(); }
        }
    }
}
