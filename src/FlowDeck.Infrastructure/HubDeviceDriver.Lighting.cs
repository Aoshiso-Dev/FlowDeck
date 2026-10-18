using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver
{
    private readonly HubLightingRecoveryJournal lightingJournal;
    private readonly Dictionary<string, HubStream> lightingStreams = [];
    private readonly HashSet<string> lightingFaulted = [];
    private Dictionary<string, (LightingEndpointConfiguration Config, EndpointSetup? Setup)> lightingSetup = [];
    private PhysicalDeviceBinding? lightingBinding;
    private bool lightingPreparing;
    private IHubLightingEndpointAdapter? LightingAdapter => adapter as IHubLightingEndpointAdapter;
    // The pinned encoder sends 24 pixels per port. The rate/age are conservative software limits, not optical measurements.
    public DeviceLightingLimits? LightingLimits => LightingAdapter is { SupportsLightingEndpoints: true, SupportsLightingDeadlines: true }
        ? LightingAdapter.SupportsSmoothLightingFrames
            ? DefinitionData.LightingLimits with { MinimumIntervalMilliseconds = 33 }
            : DefinitionData.LightingLimits : null;

    public void ConfigureLighting(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        var values = configurations.Take(DefinitionData.Lighting.Endpoints.Count + 1).Select(Contract.Copy).ToArray();
        var registered = endpoints.Take(DeviceRegistry.MaximumEndpoints + 1).Select(Contract.Copy).ToArray();
        if (values.Length > DefinitionData.Lighting.Endpoints.Count || values.Any(c => !DeviceLightingConfiguration.ValidEndpoint(c) || c.Target.DeviceId != DeviceId ||
                !DefinitionData.Lighting.Endpoints.Contains(c.Target.EndpointId)) || values.DistinctBy(c => c.Target).Count() != values.Length ||
            registered.Length > DeviceRegistry.MaximumEndpoints || registered.Any(e => e is null || !DeviceConfigurationRules.Key(e.Id)) ||
            registered.DistinctBy(e => e.Id).Count() != registered.Length || values.Any(c => !registered.Any(e => e.Id == c.Target.EndpointId && e.Kind == EndpointKind.Lighting)) ||
            binding is not null && (binding.DeviceId != DeviceId || binding.ProviderIdentity != nativeId || binding.DefinitionId != Definition ||
                binding.Fingerprint?.Length != 64 || !binding.Fingerprint.All(Uri.IsHexDigit)))
            throw new ArgumentException("Hubの照明設定・関連付けが不正です");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lightingPreparing || lightingJournal.Pending.Length != 0 || lightingStreams.Count != 0)
                throw new IOException("端子の制御を終了してから照明設定を変更してください");
            currentControlBinding = null;
            lightingBinding = binding;
            lightingSetup = values.ToDictionary(c => c.Target.EndpointId, c => (c, registered.Single(e => e.Id == c.Target.EndpointId).Setup));
        }
    }
    private string? LightingReady(string channel, bool streaming = false)
    {
        if (disposed) return "Hubドライバーは終了しています";
        if (!connected || status is null) return "Hubが切断されています";
        if (LightingAdapter is not { SupportsLightingEndpoints: true }) return "個体を照合する照明Workerへ更新してください";
        if (streaming && LightingAdapter.SupportsLightingDeadlines != true) return "期限付き照明フレームに未対応です";
        if (lightingBinding is null || lightingBinding != candidate || CurrentControlConnectionChanged) return "Hubの現在の個体・接続情報を再取得してください";
        if (status.Lighting.SingleOrDefault(l => l.Id == channel) is not { } port) return "照明端子を検出できません";
        if (!lightingSetup.TryGetValue(channel, out var value) ||
            value.Setup is not { LedCount: >= 1 } setup || setup.LedCount > DefinitionData.Lighting.MaximumLedsPerEndpoint || setup.Accessory != port.Accessory ||
            port.LedCount is { } observed && observed != setup.LedCount) return "現在の照明アクセサリーのLED数が未設定、非対応、または実機と一致しません";
        if (!DeviceLightingConfiguration.ValidExit(value.Config.Exit) || !DefinitionData.Lighting.ExitActions.Contains(value.Config.Exit!.Action))
            return "このHubでは終了時の指定色または消灯を設定してください";
        if (lightingFaulted.Contains(channel)) return "この照明端子の終了処理を完了してください";
        return null;
    }
    private DeviceEndpoint LightingObservation(LedChannel port)
    {
        DeviceCapability Capability(DeviceFeature feature, bool streaming = false)
        {
            var supported = LightingAdapter is { SupportsLightingEndpoints: true } && (!streaming || LightingAdapter.SupportsLightingDeadlines);
            var reason = feature == DeviceFeature.RestoreLighting ? supported ? null : "照明Workerの更新が必要です" : LightingReady(port.Id, streaming);
            // An unconfirmed write/release needs recovery, not another wiring approval.
            // Keep the explicit restore capability available while output remains blocked.
            var availability = reason is null ? DeviceAvailability.Ready :
                lightingFaulted.Contains(port.Id) ? DeviceAvailability.Failed : DeviceAvailability.NeedsSetup;
            return new(feature, supported ? ImplementationSupport.Supported : ImplementationSupport.Unsupported, VerificationEvidence.Automated,
                availability, reason ??
                    (feature == DeviceFeature.RestoreLighting ? "指定した終了色・消灯を送信します。元設定の読み戻しはできません" : null));
        }
        return new(port.Id, port.Id.ToUpperInvariant(), EndpointKind.Lighting,
            DefinitionData.Lighting.Features.Select(f => Capability(f, f == DeviceFeature.ColorFrames)).ToArray(),
            NativeLightingEffects: LightingAdapter is { SupportsLightingEndpoints: true } ? BuiltInLightingEffects.Hub() : null);
    }
    private static HubLightSetting? HubProgram(LightingProgram? program)
    {
        if (!DeviceConfigurationRules.ValidProgram(program) || program!.LedColors is not null || program.Speed > 4) return null;
        var mode = program.EffectId.StartsWith("native/" + Provider + "/", StringComparison.Ordinal)
            ? program.EffectId[("native/" + Provider + "/").Length..] : program.EffectId is "fixed" or "off" ? program.EffectId : "";
        var effect = HubLighting.Effects.SingleOrDefault(e => e.Id == mode);
        if (effect is null || !DefinitionData.Lighting.NativeEffects.Contains(mode) || program.Reverse && !effect.Direction || !effect.Color && mode != "off" && program.Brightness != 100) return null;
        var result = new HubLightSetting(mode, program.Colors.Select(c => c.Scale(program.Brightness / 100d)).ToArray(), program.Speed, program.Reverse);
        return HubLighting.Valid(result) ? result : null;
    }
    private static LightingProgram Reported(HubLightSetting effect) => new(effect.Mode is "fixed" or "off" ? effect.Mode : "native/" + Provider + "/" + effect.Mode,
        effect.Colors.ToArray(), effect.Speed, effect.Reverse);
    private string? ValidateLightingAction(DeviceEndpointAction action)
    {
        if (action.Operation == DeviceOperation.StopLighting) return action.Duty is null && action.Profile is null && action.Lighting is null ? null : "照明終了の引数が不正です";
        if (action.Operation != DeviceOperation.SetLighting || action.Duty is not null || action.Profile is not null || HubProgram(action.Lighting) is null)
            return "Hubで未対応の照明効果・色・明るさです";
        return LightingReady(action.EndpointId);
    }
    public string? ValidateLightingStream(string[] endpoints, LightingProgram program)
    {
        lock (gate)
        {
            if (endpoints is not { Length: > 0 } || endpoints.Length > DefinitionData.Lighting.Endpoints.Count || endpoints.Any(c => !DefinitionData.Lighting.Endpoints.Contains(c)) ||
                endpoints.Distinct().Count() != endpoints.Length || !DeviceLighting.ValidAnimation(program)) return "Hubの同期対象・効果が不正です";
            return endpoints.Select(c => LightingReady(c, streaming: true)).FirstOrDefault(e => e is not null);
        }
    }
    private HubLightingObligation[] PrepareLighting(string[] channels, string owner)
    {
        HubLightingObligation[] values;
        lock (gate)
        {
            foreach (var channel in channels) if (LightingReady(channel) is { } error) throw new IOException(error);
            values = channels.Select(c => new HubLightingObligation(DeviceId, nativeId, candidate!.Fingerprint,
                ControlCoordinator.WiringSignature(status!), c, owner, lightingSetup[c].Config.Exit!)).ToArray();
            lightingPreparing = true;
        }
        // Disk I/O must not hold the observation lock. Configure refuses while this prepare is in flight.
        try { lightingJournal.Prepare(values); return values; }
        finally { lock (gate) lightingPreparing = false; }
    }
    private async Task<DeviceDriverResult> ApplyLightingAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) if (Validate(action) is { } error) return new(Outcome.Rejected, error);
            if (action.Operation == DeviceOperation.StopLighting)
            {
                HubStream? stream; lock (gate) stream = lightingStreams.GetValueOrDefault(action.EndpointId);
                if (stream is not null) return (await EndStreamCoreAsync(stream, DeviceLightingStopReason.Normal, ct).ConfigureAwait(false))
                    .Single(r => r.EndpointId == action.EndpointId).Result;
                return await ReleaseLightingOneAsync(action.EndpointId, ct).ConfigureAwait(false);
            }
            lock (gate) if (lightingStreams.ContainsKey(action.EndpointId)) return new(Outcome.Rejected, "同期を終了してから照明を変更してください");
            await ReadCoreAsync(ct).ConfigureAwait(false);
            lock (gate) if (Validate(action) is { } error) return new(Outcome.Rejected, error);
            var saved = PrepareLighting([action.EndpointId], Guid.NewGuid().ToString("N"));
            var result = await WriteLightingCoreAsync(saved, HubProgram(action.Lighting)!, null, ct).ConfigureAwait(false);
            return result[0].Result;
        }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverReleaseResult[]> WriteLightingCoreAsync(HubLightingObligation[] values, HubLightSetting effect,
        long? deadline, CancellationToken ct)
    {
        HubResult response;
        try
        {
            lock (gate) CheckDiscovery();
            if (LightingAdapter is not { SupportsLightingEndpoints: true } target) throw new IOException("照明Workerの更新が必要です");
            response = await target.SetLightingEndpointsAsync(values.Select(p => p.Channel).ToArray(), effect,
                new(nativeId, values[0].WiringSignature, deadline), ct).ConfigureAwait(false);
            lock (gate) CheckDiscovery();
        }
        catch (Exception e)
        {
            lock (gate) { connected = false; foreach (var value in values) lightingFaulted.Add(value.Channel); }
            return values.Select(v => new DeviceDriverReleaseResult(v.Channel, new(Outcome.Unknown, "Hub照明の送信結果は未確認です: " + e.Message))).ToArray();
        }
        var rows = response.Channels;
        var validRows = rows is not null && rows.Length == values.Length && rows.All(r => r is not null) &&
            rows.Select(r => r.Id).ToHashSet().SetEquals(values.Select(v => v.Channel)) && rows.DistinctBy(r => r.Id).Count() == rows.Length;
        var results = values.Select(value =>
        {
            var row = validRows ? rows!.Single(r => r.Id == value.Channel) : null;
            var expired = (row?.Error ?? response.Error) == HardwareWriteDeadline.LightingExpiredError;
            var sent = row is { Write: "completed", Confirmation: "writtenUnverified", ReportedDuty: null } && response.Outcome is Outcome.WrittenUnverified or Outcome.Partial;
            var notSent = row is { Write: "notExecuted" } || rows is null && response.Outcome == Outcome.Rejected;
            return new DeviceDriverReleaseResult(value.Channel, sent ? new(Outcome.WrittenUnverified, "照明へ送信しました。発光の読み戻しはできません", ReportedLighting: Reported(effect)) :
                new(notSent ? Outcome.Rejected : Outcome.Unknown, row?.Error ?? response.Error ?? "照明の端子別結果を確認できません",
                    Failure: notSent && expired ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None));
        }).ToArray();
        lock (gate)
            foreach (var result in results)
                if (result.Result.Outcome is not (Outcome.WrittenUnverified or Outcome.Confirmed) && result.Result.Failure != DeviceDriverFailure.LightingFrameExpired)
                    lightingFaulted.Add(result.EndpointId);
        return results;
    }
    public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
    {
        var targets = endpoints.ToArray(); var copied = Contract.Copy(program);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) if (ValidateLightingStream(targets, copied) is { } error) throw new ArgumentException(error);
            await ReadCoreAsync(ct).ConfigureAwait(false);
            HubStream stream;
            lock (gate)
            {
                if (ValidateLightingStream(targets, copied) is { } error) throw new IOException(error);
                if (targets.Any(lightingStreams.ContainsKey)) throw new IOException("対象の照明は既に同期中です");
                stream = new(this, targets, copied, locator, candidate!.Fingerprint)
                { HasWritten = lightingJournal.Pending.Any(p => targets.Contains(p.Channel)) };
            }
            stream.Obligations = PrepareLighting(targets, stream.Id);
            lock (gate) foreach (var channel in targets) lightingStreams.Add(channel, stream);
            return stream;
        }
        finally { io.Release(); }
    }
    private sealed class HubStream(HubDeviceDriver owner, string[] channels, LightingProgram program, string locator, string fingerprint) : ISelectiveDeviceLightingStream
    {
        internal readonly string Id = Guid.NewGuid().ToString("N"), Locator = locator, Fingerprint = fingerprint;
        internal string[] Channels = channels;
        internal readonly LightingProgram Program = program;
        internal HubLightingObligation[] Obligations = [];
        internal bool HasWritten, Ended;
        internal DeviceDriverReleaseResult[]? EndResults;
        internal readonly Dictionary<string, RgbColor> NormalStopColors = [];
        public Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct) => owner.WriteStreamAsync(this, frame, ct);
        public Task<DeviceDriverReleaseResult[]> EndSelectedAsync(string[] endpoints, CancellationToken ct) => owner.EndSelectedStreamAsync(this,endpoints,ct);
        public async Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
        {
            await owner.io.WaitAsync(ct).ConfigureAwait(false);
            try { return await owner.EndStreamCoreAsync(this, reason, ct).ConfigureAwait(false); }
            finally { owner.io.Release(); }
        }
    }
    private async Task<DeviceDriverReleaseResult[]> WriteStreamAsync(HubStream stream, DeviceLightingFrame frame, CancellationToken ct)
    {
        DeviceDriverReleaseResult[] Denied(string error, bool expired = false) => stream.Channels.Select(c => new DeviceDriverReleaseResult(c,
            new(Outcome.Rejected, error, Failure: expired ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None))).ToArray();
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (frame.TargetError(stream.Channels) is { } targetError) return Denied(targetError);
            lock (gate)
            {
                if (stream.Ended || stream.Channels.Any(c => lightingStreams.GetValueOrDefault(c) != stream)) return Denied("以前の照明同期は終了しています");
                if (locator != stream.Locator || candidate?.Fingerprint != stream.Fingerprint) return Denied("同期対象のHubの接続・構成が変更されました");
                if (ValidateLightingStream(stream.Channels, stream.Program) is { } error) return Denied(error);
            }
            // This callback may lock the service; never invoke it while holding the driver's observation lock.
            var started = HardwareWriteDeadline.Now; var allowed = frame.Authorize();
            if (allowed.Error is { } denied) return Denied(denied, allowed.Expired);
            if (allowed.ValidFor is not { } budget) return Denied("照明フレームの有効期限がありません");
            var deadline = HardwareWriteDeadline.FromBudget(started, budget);
            if (HardwareWriteDeadline.Expired(deadline)) return Denied("照明フレームの有効期限を超えました", true);
            var wasWritten = stream.HasWritten; stream.HasWritten = true;
            DeviceDriverReleaseResult[] results;
            if (!frame.HasEndpointColors)
                results = await WriteLightingCoreAsync(stream.Obligations, new("fixed", [frame.Color]), deadline, ct).ConfigureAwait(false);
            else
            {
                var rows = new List<DeviceDriverReleaseResult>();
                foreach (var group in stream.Obligations.GroupBy(o => frame.ColorFor(o.Channel)))
                {
                    var permitted = frame.Authorize();
                    if (permitted.Error is { } error)
                        rows.AddRange(group.Select(o => new DeviceDriverReleaseResult(o.Channel, new(Outcome.Rejected, error,
                            Failure: permitted.Expired ? DeviceDriverFailure.LightingFrameExpired : DeviceDriverFailure.None))));
                    else rows.AddRange(await WriteLightingCoreAsync(group.ToArray(), new("fixed", [group.Key]), deadline, ct).ConfigureAwait(false));
                }
                results = rows.ToArray();
            }
            foreach (var result in results.Where(r => r.Result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified))
                stream.NormalStopColors[result.EndpointId] = frame.NormalStopColorFor(result.EndpointId) ?? stream.Program.Colors[0].Scale(stream.Program.Brightness / 100d);
            if (!wasWritten && results.All(r => r.Result.Outcome == Outcome.Rejected)) stream.HasWritten = false;
            return results;
        }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverResult> ReleaseLightingOneAsync(string channel, CancellationToken ct,
        HubLightingObligation? captured = null, LightingProgram? normalStop = null, string? expectedLocator = null)
    {
        var saved = lightingJournal.Pending.SingleOrDefault(p => p.Channel == channel);
        if (saved is null) { lock (gate) lightingFaulted.Remove(channel); return new(Outcome.Confirmed, "この照明端子の操作は終了しています"); }
        if (captured is not null && saved != captured) return new(Outcome.Unknown, "照明の所有・復旧記録が変更されました");
        await ReadRecoveryCoreAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            if (expectedLocator is not null && locator != expectedLocator || candidate?.Fingerprint != saved.Fingerprint ||
                candidate.ProviderIdentity != saved.HubId || status is null || ControlCoordinator.WiringSignature(status) != saved.WiringSignature)
                return new(Outcome.Unknown, "終了対象のHubの個体・構成が変更されています");
        }
        var program = normalStop ?? DeviceLightingConfiguration.ExitProgram(saved.Exit);
        var result = (await WriteLightingCoreAsync([saved], HubProgram(program)!, null, ct).ConfigureAwait(false))[0].Result;
        if (result.Outcome == Outcome.WrittenUnverified)
        {
            try { lightingJournal.Complete(saved); lock (gate) lightingFaulted.Remove(channel); }
            catch (Exception e)
            {
                lock (gate) lightingFaulted.Add(channel);
                return result with { Outcome = Outcome.Unknown, Message = "終了色は送信しましたが、復旧記録を確定できません: " + e.Message };
            }
        }
        return result;
    }
    private async Task<DeviceDriverReleaseResult[]> EndStreamCoreAsync(HubStream stream, DeviceLightingStopReason reason, CancellationToken ct)
    {
        if (stream.EndResults is not null) return Contract.Copy(stream.EndResults);
        stream.Ended = true;
        var results = new List<DeviceDriverReleaseResult>();
        foreach (var saved in stream.Obligations)
        {
            var normal = reason == DeviceLightingStopReason.Normal ? new LightingProgram("fixed",
                [stream.NormalStopColors.GetValueOrDefault(saved.Channel) ?? stream.Program.Colors[0].Scale(stream.Program.Brightness / 100d)]) : null;
            DeviceDriverResult result;
            try
            {
                if (!stream.HasWritten)
                {
                    lightingJournal.Complete(saved); lock (gate) lightingFaulted.Remove(saved.Channel);
                    result = new(Outcome.Confirmed, "照明出力を変更せず同期準備を終了しました");
                }
                else result = await ReleaseLightingOneAsync(saved.Channel, ct, saved, normal, stream.Locator).ConfigureAwait(false);
            }
            catch (Exception e) { lock (gate) lightingFaulted.Add(saved.Channel); result = new(Outcome.Unknown, "照明の終了を確認できません: " + e.Message); }
            results.Add(new(saved.Channel, result));
            lock (gate) if (lightingStreams.GetValueOrDefault(saved.Channel) == stream) lightingStreams.Remove(saved.Channel);
        }
        stream.EndResults = results.ToArray(); return Contract.Copy(stream.EndResults);
    }
    private async Task<DeviceDriverReleaseResult[]> ReleaseAllLightingCoreAsync(CancellationToken ct)
    {
        var results = new List<DeviceDriverReleaseResult>();
        HubStream[] streams; lock (gate) streams = lightingStreams.Values.Distinct().ToArray();
        foreach (var stream in streams) results.AddRange(await EndStreamCoreAsync(stream, DeviceLightingStopReason.ServiceStopping, ct).ConfigureAwait(false));
        foreach (var saved in lightingJournal.Pending.Where(p => !results.Any(r => r.EndpointId == p.Channel)))
        {
            DeviceDriverResult result;
            try { result = await ReleaseLightingOneAsync(saved.Channel, ct).ConfigureAwait(false); }
            catch (Exception e) { result = new(Outcome.Unknown, "照明の終了を確認できません: " + e.Message); }
            results.Add(new(saved.Channel, result));
        }
        return results.ToArray();
    }
}
