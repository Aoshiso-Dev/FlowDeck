using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class OpenRgbDeviceDriver : IConfiguredLightingDriver, IDeviceLightingCompositionDriver,
    IDeviceLightingBatchDriver, IDevicePollingDriver, IDeviceSetupDriver, IDevicePhasedOperation
{
    private sealed class Slot(int key, OpenRgbLightingRecovery journal)
    {
        public readonly int Key = key;
        public readonly OpenRgbLightingRecovery Journal = journal;
        public OpenRgbLightingExecutor? Executor;
        public OpenRgbSdkTarget? Target;
        public PhysicalDeviceBinding? Binding;
        public bool Owned, Faulted;
    }
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly AsyncLocal<IDeviceHardwareExecution?> execution = new();
    private readonly string boot;
    private readonly Action verifyRoute;
    private readonly Func<CancellationToken, Task>? refreshRecoveryPermit;
    private readonly Func<OpenRgbBoundDevice, IOpenRgbLightingTransport?>? createTransport;
    private readonly bool selectedForControl;
    private readonly Dictionary<int, Slot> slots = [];
    private readonly Dictionary<string, Stream> streams = [];
    private Dictionary<string, OpenRgbLightingSurface> ports = [];
    private Dictionary<string, (LightingEndpointConfiguration Config, EndpointSetup? Setup)> settings = [];
    private OpenRgbBoundDevice? current;
    private PhysicalDeviceBinding? binding;
    private readonly string? journalError;
    private string? readError;
    private bool closing, disposed;
    internal bool ResourcesClosed { get { lock (gate) return disposed; } }
    internal void BeginShutdown() { lock (gate) closing = true; }
    public string DeviceId { get; }
    public DeviceLightingLimits LightingLimits => new(100, 500, 4096, EndpointColors: true, ConcurrentEndpointStreams: true);
    public PhysicalDeviceBinding? BindingCandidate { get { lock (gate) return Candidate(); } }
    internal OpenRgbDeviceDriver(string id, string directory, string boot, Action verifyRoute, bool selectedForControl,
        Func<CancellationToken, Task>? refreshRecoveryPermit = null, Func<OpenRgbBoundDevice, IOpenRgbLightingTransport?>? createTransport = null)
    {
        DeviceId = id; this.boot = boot; this.verifyRoute = verifyRoute; this.selectedForControl = selectedForControl;
        this.refreshRecoveryPermit = refreshRecoveryPermit;
        this.createTransport = createTransport;
        try
        {
            // Include removed zones so a changed descriptor cannot hide a durable obligation.
            for (var i = -1; i < 256; i++) slots.Add(i, new(i, new(directory, id, i < 0 ? null : i)));
        }
        catch (Exception e) { journalError = "OpenRGBの復旧記録を読み込めません: " + OpenRgbLightingProfile.Label(e.Message, "読取り失敗", 300); }
    }
    internal void SetConnection(OpenRgbBoundDevice? next)
    {
        lock (gate)
        {
            if (disposed) return;
            if (current?.Snapshot.Target != next?.Snapshot.Target)
            {
                foreach (var slot in slots.Values) { slot.Owned = false; slot.Executor = null; }
                if (next is not null) ports = OpenRgbDeviceIdentities.Surfaces(next.Snapshot.Controller).ToDictionary(s => s.Endpoint);
            }
            current = next; readError = next is null ? "OpenRGBの機器を取得できません" : null;
        }
    }
    private PhysicalDeviceBinding? Candidate() => OpenRgbDeviceIdentities.Binding(DeviceId, current);
    private static int Key(OpenRgbLightingSurface surface) => surface.Zone ?? -1;
    private string Endpoint(int key) => ports.Values.FirstOrDefault(p => Key(p) == key)?.Endpoint ?? (key == -1 ? "device" : "unresolved-zone:" + key);
    private void Guard(OpenRgbBoundDevice expected, PhysicalDeviceBinding expectedBinding)
    {
        verifyRoute();
        lock (gate)
            if (disposed || nativeShutdown || current?.Connection != expected.Connection || current?.Snapshot.Target != expected.Snapshot.Target || Candidate() != expectedBinding)
                throw new IOException("OpenRGBの個体・接続・構成が変更されています");
    }
    private string? Ready(OpenRgbLightingSurface port)
    {
        if (disposed || current is null) return "OpenRGBの機器が切断されています";
        if (closing) return "OpenRGBの終了・復旧中です";
        if (readError is not null) return readError;
        if (journalError is not null) return journalError;
        // This gate applies to new effects, batches and streams. Release/recovery deliberately
        // bypass it, while retaining route, physical identity and journal validation.
        if (!selectedForControl) return "OpenRGBの制御対象に選択されていません";
        if (Candidate() is not { } candidate) return "OpenRGBの物理的な個体を区別できません";
        if (binding != candidate) return "OpenRGBの機器の関連付けを確認してください";
        if (!settings.TryGetValue(port.Endpoint, out var value) || !value.Config.WiringConfirmed || !DeviceLightingConfiguration.ValidExit(value.Config.Exit) ||
            value.Setup is not { LedCountConfirmed: true } setup || setup.LedCount != port.LedCount)
            return "OpenRGBの対象・LED数・終了方法を確認してください";
        if (port.ControllerDirect && port.Native.Length == 0 && value.Config.Exit!.Action == LightingExitAction.RestoreOriginal)
            return "このゾーンの終了方法は固定色または消灯を選んでください";
        var slot = slots[Key(port)];
        if (port.ControllerDirect && shared is not null && (slots[-1].Faulted || slots[-1].Journal.Pending is not null && !slots[-1].Owned))
            return "共有DirectのOpenRGB照明を復旧してください";
        if (slot.Faulted || slot.Journal.Pending is not null && !slot.Owned) return "前回のOpenRGB操作を復旧してください";
        if (slots.Values.Any(s => s.Key != slot.Key && (s.Key == -1 || slot.Key == -1) && s.Journal.Pending is not null &&
                !(s.Key == -1 && port.ControllerDirect && shared is not null)) ||
            streams.Any(pair => pair.Key != port.Endpoint && (pair.Value.Keys[pair.Key] == -1 || slot.Key == -1)))
            return "機器全体と個別ゾーンは同時に操作できません";
        return null;
    }
    public DeviceObservation? Observe()
    {
        lock (gate)
        {
            if (current is not { } d) return null;
            var endpoints = ports.Values.Select(port =>
            {
                var error = Ready(port);
                DeviceCapability Cap(DeviceFeature feature, bool supported, string? reason) => new(feature,
                    supported ? ImplementationSupport.Supported : ImplementationSupport.Unsupported, VerificationEvidence.Automated,
                    reason is null ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup, reason);
                return new DeviceEndpoint(port.Endpoint, port.Name, EndpointKind.Lighting,
                    [Cap(DeviceFeature.FixedColor, port.FixedMode is not null || port.ControllerDirect, error), Cap(DeviceFeature.NativeEffect, port.Native.Length > 0, error),
                     Cap(DeviceFeature.ColorFrames, port.DirectMode is not null || port.ControllerDirect, error), Cap(DeviceFeature.RestoreLighting, true, journalError)],
                    NativeLightingEffects: port.Native.Select(n => n.Effect).ToArray());
            }).ToArray();
            return new(DeviceId, OpenRgbDeviceProvider.Provider, OpenRgbLightingProfile.Label(d.Snapshot.Controller.DisplayName,
                OpenRgbLightingProfile.Label(d.Snapshot.Controller.Name, "OpenRGB機器")), d.Snapshot.Controller.Type switch
                { 0 => DeviceKind.Motherboard, 1 => DeviceKind.Memory, 2 => DeviceKind.Graphics, _ => DeviceKind.Controller }, d.Identity.Confidence,
                OpenRgbDeviceIdentities.Locator(d.Snapshot.Target),
                OpenRgbDeviceProvider.Definition, OpenRgbDeviceProvider.Domains, Contract.Copy(endpoints));
        }
    }
    public EndpointSetupOptions[] DescribeSetup()
    {
        lock (gate) return current is null ? [] : ports.Values.Select(port => new EndpointSetupOptions(port.Endpoint, [],
            port.ControllerDirect && port.Native.Length == 0 ? [LightingExitAction.FixedColor, LightingExitAction.Off] :
                port.FixedMode is not null || port.ControllerDirect ? [LightingExitAction.FixedColor, LightingExitAction.Off, LightingExitAction.RestoreOriginal] : [LightingExitAction.RestoreOriginal],
            port.LedCount, port.LedCount, port.LedCount)).ToArray();
    }
    public void ConfigureLighting(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations, IEnumerable<RegisteredEndpoint> endpoints)
    {
        var values = configurations.Take(257).Select(Contract.Copy).ToArray(); var registered = endpoints.Take(257).Select(Contract.Copy).ToArray();
        if (values.Length > 256 || registered.Length > 256 || values.Any(v => !DeviceLightingConfiguration.ValidEndpoint(v) || v.Target.DeviceId != DeviceId) ||
            values.DistinctBy(v => v.Target).Count() != values.Length || registered.Any(e => e is null || e.Kind != EndpointKind.Lighting) ||
            registered.DistinctBy(e => e.Id).Count() != registered.Length || values.Any(v => !registered.Any(e => e.Id == v.Target.EndpointId)))
            throw new ArgumentException("OpenRGBの照明設定が不正です");
        if (!io.Wait(0)) throw new IOException("OpenRGBの操作完了後に設定してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing || disposed, this);
                if (journalError is not null) throw new IOException(journalError);
                if (streams.Count > 0 || slots.Values.Any(s => s.Journal.Pending is not null)) throw new IOException("OpenRGBの終了・復旧後に設定してください");
                if (binding is not null && binding != Candidate()) throw new IOException("OpenRGBの関連付けが一致しません");
                if (registered.Any(e => !ports.ContainsKey(e.Id))) throw new IOException("OpenRGBの未検出端子です");
                settings = values.ToDictionary(v => v.Target.EndpointId, v => (v, registered.Single(e => e.Id == v.Target.EndpointId).Setup));
                this.binding = binding;
                foreach (var slot in slots.Values) { slot.Faulted = false; slot.Owned = false; slot.Executor = null; }
            }
        }
        finally { io.Release(); }
    }
    public string? Validate(DeviceEndpointAction action)
    {
        lock (gate)
        {
            if (!ports.TryGetValue(action.EndpointId, out var port)) return "未検出のOpenRGB端子です";
            if (action.Operation == DeviceOperation.StopLighting) return nativeShutdown ? "Native復旧と記録整理を待っています" : journalError;
            if (action.Operation != DeviceOperation.SetLighting) return "未対応のOpenRGB操作です";
            if (Ready(port) is { } error) return error;
            if (streams.ContainsKey(port.Endpoint)) return "同期を終了してからOpenRGBの効果を設定してください";
            if (port.ControllerDirect && DeviceLighting.FrameProgram(action.Lighting) && !DeviceLighting.Animated(action.Lighting)) return ValidateShared([port.Endpoint]);
            if (shared is not null) return "この機器の照明制御を終了してから内蔵効果を設定してください";
            try { _ = OpenRgbLightingPlan.Create(current!.Snapshot.Controller, port.Zone, action.Lighting!); return null; }
            catch (Exception e) when (e is IOException or ArgumentException) { return e.Message; }
        }
    }
    private OpenRgbLightingExecutor Executor(Slot slot)
    {
        lock (gate)
        {
            if (current is not { } c || Candidate() is not { } candidate) throw new IOException("OpenRGBの復旧対象を照合できません");
            if (slot.Executor is not null && slot.Target == c.Snapshot.Target && slot.Binding == candidate) return slot.Executor;
            slot.Target = c.Snapshot.Target; slot.Binding = candidate; slot.Owned = false;
            var scope = new OpenRgbRecoveryScope(candidate, c.Snapshot.Target, c.Identity.Confidence, Guid.NewGuid().ToString("N"), slot.Key < 0 ? null : slot.Key);
            return slot.Executor = new(c.Connection, slot.Journal, scope, boot, () => Guard(c, candidate), createTransport?.Invoke(c));
        }
    }
    private void Cache(OpenRgbLightingExecution result)
    {
        lock (gate)
            if (result.Snapshot is { } snapshot && current is { } c && c.Snapshot.Target == snapshot.Target)
            { current = c with { Snapshot = snapshot }; readError = null; }
    }
    private static bool Success(DeviceDriverResult r) => r.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
    private async Task<DeviceDriverResult> ApplyCoreAsync(OpenRgbLightingSurface port, LightingProgram program, Action authorize, bool frame, CancellationToken ct)
    {
        Slot slot; OpenRgbLightingExecutor executor; LightingExitPolicy exit;
        lock (gate)
        {
            if (Ready(port) is { } error) return new(Outcome.Rejected, error);
            slot = slots[Key(port)]; executor = Executor(slot); exit = settings[port.Endpoint].Config.Exit!;
            slot.Owned = true; // Prepared journal hashes can change during an authorized operation.
        }
        var result = await executor.ApplyAsync(program, exit, authorize, frame, ct).ConfigureAwait(false); Cache(result);
        lock (gate) { slot.Owned = slot.Journal.Pending is not null; slot.Faulted = executor.NeedsRecovery; }
        return result.Result;
    }
    public async Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        action = Contract.Copy(action);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Validate(action) is { } error) return new(Outcome.Rejected, error);
            if (action.Operation == DeviceOperation.StopLighting)
                return (await EndCoreAsync([action.EndpointId], DeviceLightingStopReason.Normal, ct).ConfigureAwait(false)).Single().Result;
            OpenRgbLightingSurface port; lock (gate) port = ports[action.EndpointId];
            var hardware = execution.Value;
            void Authorize() { hardware?.CheckAuthorization(); ct.ThrowIfCancellationRequested(); }
            if (port.ControllerDirect && DeviceLighting.FrameProgram(action.Lighting))
            {
                var color = DeviceLighting.StopColor(action.Lighting!);
                var result = await WriteSharedAsync(new Dictionary<string, RgbColor> { [port.Endpoint] = color }, Authorize, ct).ConfigureAwait(false);
                return result with { ReportedLighting = Success(result) ? new("fixed", [color]) : null };
            }
            return await ApplyCoreAsync(port, action.Lighting!, Authorize, false, ct).ConfigureAwait(false);
        }
        catch (Exception e) { return new(Outcome.Unknown, OpenRgbLightingProfile.Label(e.Message, "OpenRGBの操作に失敗しました", 400)); }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverResult> RecoverCoreAsync(Slot slot, LightingProgram? normal, CancellationToken ct)
    {
        if (nativeShutdown) return new(Outcome.Unknown, "Native復旧と記録整理を待っています");
        if (journalError is not null) return new(Outcome.Unknown, journalError);
        if (slot.Journal.Pending is null) { lock (gate) { slot.Owned = false; slot.Faulted = false; } return new(Outcome.Confirmed, "未終了のOpenRGB操作はありません"); }
        try
        {
            if (slot.Journal.Pending is { Phase: OpenRgbRecoveryPhase.Accepted } accepted)
            {
                slot.Journal.Complete(accepted); lock (gate) { slot.Owned = false; slot.Faulted = false; }
                return new(Outcome.WrittenUnverified, "OpenRGBの終了記録を整理しました");
            }
            var executor = Executor(slot);
            lock (gate) if (!slot.Owned || slot.Journal.Pending?.Phase != OpenRgbRecoveryPhase.Active) normal = null;
            var hardware = execution.Value;
            var result = await executor.RecoverAsync(() => { hardware?.CheckAuthorization(); ct.ThrowIfCancellationRequested(); }, ct, normal).ConfigureAwait(false);
            Cache(result);
            lock (gate) { slot.Owned = slot.Journal.Pending is not null; slot.Faulted = !Success(result.Result); }
            return result.Result;
        }
        catch (Exception e) { lock (gate) slot.Faulted = true; return new(Outcome.Unknown, OpenRgbLightingProfile.Label(e.Message, "OpenRGBを復旧できません", 400)); }
    }
    public async Task PollAsync(CancellationToken ct)
    {
        if (!await io.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            OpenRgbBoundDevice? captured; lock (gate) { if (closing || disposed) return; captured = current; }
            if (captured is null) return;
            verifyRoute(); var snapshot = await captured.Connection.ReadAsync(captured.Snapshot.Target, ct, verifyRoute).ConfigureAwait(false); verifyRoute();
            lock (gate) if (current?.Snapshot.Target == captured.Snapshot.Target) { current = captured with { Snapshot = snapshot }; readError = null; }
        }
        catch (Exception e) { lock (gate) readError = OpenRgbLightingProfile.Label(e.Message, "OpenRGBの取得に失敗しました", 400); throw; }
        finally { io.Release(); }
    }
    public async Task<T> RunPhasedOperationAsync<T>(DevicePreparationKind kind, IDeviceHardwareExecution hardware,
        Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var previous = execution.Value; execution.Value = hardware;
        try { return await hardware.RunAsync(operation, ct).ConfigureAwait(false); }
        finally { execution.Value = previous; }
    }
    public async Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReleaseCoreAsync(ct).ConfigureAwait(false); }
        finally { io.Release(); }
    }
    private async Task<DeviceDriverReleaseResult[]> ReleaseCoreAsync(CancellationToken ct)
    {
            if (nativeShutdown) return [new("device", new(Outcome.Unknown, "Native復旧と記録整理を待っています"))];
            if (journalError is not null) return [new("device", new(Outcome.Unknown, journalError))];
            if (shared is not null) return await RecoverSharedAsync(ct).ConfigureAwait(false);
            string[] active; lock (gate) active = streams.Keys.ToArray();
            var results = (await EndCoreAsync(active, DeviceLightingStopReason.ServiceStopping, ct).ConfigureAwait(false)).ToList();
            foreach (var slot in slots.Values.Where(s => s.Journal.Pending is not null))
            {
                string endpoint; lock (gate) endpoint = Endpoint(slot.Key);
                if (results.Any(r => r.EndpointId == endpoint && !Success(r.Result))) continue;
                results.RemoveAll(r => r.EndpointId == endpoint);
                results.Add(new(endpoint, await RecoverCoreAsync(slot, null, ct).ConfigureAwait(false)));
            }
            return results.ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) if (disposed) return;
            if (nativeShutdown) throw new IOException("OpenRGBのNative復旧と記録整理が未完了です");
            // A failed close keeps this driver and its journals retryable. Block new work while
            // retaining the same physical binding and SDK connection for recovery.
            if (journalError is null && slots.Values.Any(s => s.Journal.Pending is not null) && refreshRecoveryPermit is not null)
                await refreshRecoveryPermit(CancellationToken.None).ConfigureAwait(false);
            var results = await ReleaseCoreAsync(CancellationToken.None).ConfigureAwait(false);
            lock (gate)
            {
                if (results.Any(r => !Success(r.Result)) || journalError is not null || streams.Count > 0 || shared is not null ||
                    slots.Values.Any(s => s.Journal.Pending is not null))
                    throw new IOException("OpenRGBの終了・復旧を確認できません。接続と復旧記録を保持しています");
                disposed = true; current = null;
            }
        }
        finally { io.Release(); }
    }
}
