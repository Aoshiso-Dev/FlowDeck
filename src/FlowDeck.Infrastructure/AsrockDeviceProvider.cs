using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Packaged ASRock USB provider. Construction performs no enumeration or device I/O.</summary>
public sealed partial class AsrockDeviceProvider : IDeviceCacheRetirement
{
    public const string Definition = LightingDeviceDefinitionCatalog.AsrockId;
    internal static LightingDriverDefinition DefinitionData => LightingDeviceDefinitionCatalog.BuiltIn.Get(Definition, "asrock-polychrome-usb");
    private readonly object gate = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private volatile bool closing, closed;
    internal bool ResourcesClosed => closed;
    private readonly string installation, directory, boot;
    private readonly IAsrockDeviceBackend backend;
    private readonly Dictionary<string, AsrockDeviceDriver> drivers = [];
    public string ProviderId => "asrock-usb";
    // Native sessions recursively own the legacy mutex on one transport thread, so they share a scheduling domain.
    public string[] ResourceDomains => DefinitionData.ResourceDomains.ToArray();
    public IDeviceControlDriver[] CachedDrivers { get { lock (gate) return drivers.Values.Cast<IDeviceControlDriver>().ToArray(); } }
    public AsrockDeviceProvider(string installation, string directory, string boot) : this(installation, directory, boot, CreateBackend(installation, boot)) { }
    private static IAsrockDeviceBackend CreateBackend(string installation, string boot)
    {
        if (!Guid.TryParseExact(installation, "N", out _) || !DeviceConfigurationRules.Text(boot, 128)) throw new ArgumentException("ASRockのインストール・起動識別が不正です");
        _ = DefinitionData;
        return new AsrockHidDeviceBackend();
    }
    internal AsrockDeviceProvider(string installation, string directory, string boot, IAsrockDeviceBackend backend)
    {
        if (!Guid.TryParseExact(installation, "N", out _) || !DeviceConfigurationRules.Text(boot, 128)) throw new ArgumentException("ASRockのインストール・起動識別が不正です");
        this.installation = installation; this.directory = directory; this.boot = boot; this.backend = backend;
    }
    public async Task PollAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(closing, this); await PollCoreAsync(ct).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }
    private async Task PollCoreAsync(CancellationToken ct)
    {
        try
        {
            lock (gate) confirmedAbsent.Clear();
            var connections = await backend.DiscoverAsync(ct).ConfigureAwait(false);
            if (connections is not { Length: <= DeviceRegistry.MaximumDevices } || connections.Any(c => c is null ||
                !DeviceConfigurationRules.Text(c.Path, 2048) || !DeviceConfigurationRules.Text(c.NativeIdentity, 1024) || !DeviceServiceProtocol.Id(c.ConnectionId) ||
                !Enum.IsDefined(c.Confidence) || c.DeviceRevision is < 0 or > 65535 || c.Confidence == IdentityConfidence.SerialBound && !c.NativeIdentity.StartsWith("serial:", StringComparison.Ordinal)) ||
                connections.DistinctBy(c => c.Path).Count() != connections.Length || connections.DistinctBy(c => c.NativeIdentity).Count() != connections.Length)
                throw new IOException("ASRock照明の個体識別が重複または不正です");
            var next = connections.ToDictionary(c => DeviceIdentity.Create(installation, ProviderId, c.NativeIdentity));
            string[] missing;
            lock (gate)
            {
                if (next.Keys.Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                {
                    foreach (var driver in drivers.Values) driver.SetConnection(next.GetValueOrDefault(driver.DeviceId));
                    confirmedAbsent = drivers.Values.Where(d => !next.ContainsKey(d.DeviceId)).Cast<IDeviceControlDriver>().ToHashSet();
                    throw new DeviceDiscoveryCapacityException("切断機器を含むASRock照明数が上限に達しました。不要な検出履歴を整理してください");
                }
                missing = next.Keys.Except(drivers.Keys).ToArray();
            }
            // Loading durable obligations must not block the registry's observation lock.
            var additions = missing.Select(id => new AsrockDeviceDriver(id, next[id], directory, boot, backend)).ToArray();
            lock (gate)
            {
                foreach (var driver in additions) drivers.Add(driver.DeviceId, driver);
                foreach (var driver in drivers.Values) driver.SetConnection(next.GetValueOrDefault(driver.DeviceId));
                confirmedAbsent = drivers.Values.Where(d => !next.ContainsKey(d.DeviceId)).Cast<IDeviceControlDriver>().ToHashSet();
            }
        }
        catch (DeviceDiscoveryCapacityException) { throw; }
        catch
        {
            lock (gate) foreach (var driver in drivers.Values) driver.SetConnection(null);
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            await backend.DisposeAsync().ConfigureAwait(false);
            closed = true;
        }
        finally { lifecycle.Release(); }
    }
}

internal sealed partial class AsrockDeviceDriver : IConfiguredLightingDriver, IDeviceLightingExpansionDriver, IDevicePollingDriver, ILegacyMigrationSource, IDeviceSetupDriver
{
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1);
    private readonly IAsrockDeviceBackend backend;
    private readonly string directory, boot;
    private readonly AsrockBoundRecovery? journal;
    private readonly string? journalError;
    private AsrockDeviceConnection? connection;
    private AsrockRgbSnapshot? observed;
    private PhysicalDeviceBinding? binding;
    private Dictionary<byte, (LightingEndpointConfiguration Config, EndpointSetup? Setup)> settings = [];
    private Stream? active;
    private string? readError;
    private bool disposed;
    public string DeviceId { get; }
    public DeviceLightingLimits LightingLimits => AsrockDeviceProvider.DefinitionData.Limits;
    public PhysicalDeviceBinding? BindingCandidate { get { lock (gate) return Candidate(); } }
    internal AsrockDeviceDriver(string id, AsrockDeviceConnection connection, string directory, string boot, IAsrockDeviceBackend backend)
    {
        DeviceId = id; this.connection = connection; this.directory = directory; this.boot = boot; this.backend = backend;
        try { journal = new(directory, id); nativeJournal = new(directory, id); }
        catch (Exception e) { journalError = "マザー照明の復旧記録を読み込めません: " + e.Message; }
    }
    internal void SetConnection(AsrockDeviceConnection? next)
    {
        lock (gate)
        {
            if (next != connection) { readError = "照明の接続が変わりました"; observed = null; nativeOwned.Clear(); }
            connection = next;
        }
    }
    private PhysicalDeviceBinding? Candidate() => connection is { } c && observed is { } s ? new(DeviceId, c.NativeIdentity, AsrockDeviceProvider.Definition,
        SettingsTransfer.Hash(new { AsrockDeviceProvider.Definition, DefinitionDigest = AsrockDeviceProvider.DefinitionData.Digest, c.NativeIdentity, Connection = c.Confidence == IdentityConfidence.SerialBound ? null : c.ConnectionId,
            c.DeviceRevision, s.Available, s.Counts })) : null;
    private string? Verify(AsrockDeviceConnection expected, PhysicalDeviceBinding expectedBinding)
    {
        lock (gate) if (connection != expected || Candidate() != expectedBinding) return "照明の個体・接続・構成が変更されています";
        try { backend.Check(expected); return null; } catch (Exception e) { return e.Message; }
    }
    public EndpointSetupOptions[] DescribeSetup()
    {
        lock (gate) return connection is null || observed is null ? [] : observed.Zones.Where(z => z.Count > 0)
            .Select(z => new EndpointSetupOptions("zone:" + z.Address, [],
                [LightingExitAction.FixedColor, LightingExitAction.Off, LightingExitAction.RestoreOriginal], z.Count, z.Count, z.Count)).ToArray();
    }
    private static byte Zone(string id) => id.Length == 6 && id.StartsWith("zone:", StringComparison.Ordinal) && id[5] is >= '0' and <= '7'
        ? (byte)(id[5] - '0') : throw new ArgumentException("照明ゾーンが不正です");
    private string? Ready(byte zone)
    {
        if (disposed || connection is null || observed is null) return "マザー照明を取得できません";
        if (readError is not null) return readError;
        if (journalError is not null) return journalError;
        if (binding is null || binding != Candidate()) return "照明機器の関連付けを確認してください";
        var port = observed.Zones.SingleOrDefault(z => z.Address == zone);
        if (port is null) return "照明ゾーンを検出できません";
        if (!settings.TryGetValue(zone, out var setting) || !setting.Config.WiringConfirmed || !DeviceLightingConfiguration.ValidExit(setting.Config.Exit) ||
            setting.Setup is not { LedCountConfirmed: true } setup || setup.LedCount != port.Count) return "照明の対象・LED数・終了方法を確認してください";
        if (active is null && journal!.Pending is not null) return "前回のマザー照明を復旧してください";
        if (nativeJournal!.Pending is { } native && native.Entries.Any(e => !nativeOwned.Contains(e.Zone))) return "前回のNative照明を復旧してください";
        if (nativeFaulted.Contains(zone)) return "このゾーンのNative照明を復旧してください";
        return null;
    }
    public DeviceObservation? Observe()
    {
        lock (gate)
        {
            if (connection is not { } c || observed is not { } snapshot) return null;
            return new(DeviceId, "asrock-usb", "ASRock USB RGB", DeviceKind.Controller, c.Confidence,
                c.Path + ":" + c.ConnectionId, AsrockDeviceProvider.Definition, AsrockDeviceProvider.DefinitionData.ResourceDomains.ToArray(), snapshot.Zones.Select(z =>
                {
                    var error = Ready(z.Address);
                    DeviceCapability Cap(DeviceFeature f, bool supported, string? reason) => new(f,
                        supported ? ImplementationSupport.Supported : ImplementationSupport.Unsupported, VerificationEvidence.Automated,
                        reason is null ? DeviceAvailability.Ready : DeviceAvailability.NeedsSetup, reason);
                    return new DeviceEndpoint("zone:" + z.Address, "RGB " + (z.Address + 1), EndpointKind.Lighting,
                        [Cap(DeviceFeature.ColorFrames, true, error), Cap(DeviceFeature.RestoreLighting, true, null),
                         Cap(DeviceFeature.FixedColor, true, error), Cap(DeviceFeature.NativeEffect, true, error)], NativeLightingEffects: BuiltInLightingEffects.Asrock());
                }).ToArray());
        }
    }
    private static AsrockRgbSnapshot Read(IAsrockRgbSession session, string identity)
    {
        byte[] Query(byte op, byte selector) { var r = session.Exchange(AsrockRgbMonitor.Query(op, selector)); return AsrockRgbMonitor.Response(r, r.Length, selector); }
        var available = Query(0x14, 1)[4]; var counts = Query(0x14, 2)[4..12]; var order = Query(0x14, 3)[4];
        return new(identity, available, order, counts, Enumerable.Range(0, 8).Where(i => (available & (1 << i)) != 0 && counts[i] is not (0 or 0x1e))
            .Select(i => new AsrockZoneSnapshot((byte)i, counts[i], AsrockRgbControl.Decode(Query(0x11, (byte)i), order))).ToArray());
    }
    private async Task ReadCoreAsync(CancellationToken ct)
    {
        AsrockDeviceConnection c;
        lock (gate) c = connection ?? throw new IOException("ASRock照明は切断されています");
        try
        {
            var result = await Task.Run(() => { ct.ThrowIfCancellationRequested(); using var session = backend.Borrow(c); return Read(session, c.Path); }, ct).ConfigureAwait(false);
            lock (gate)
            {
                if (connection != c) throw new IOException("照明取得中に接続が変わりました");
                observed = result; readError = null;
            }
        }
        catch (Exception e) { lock (gate) readError = e.Message; throw; }
    }
    public async Task PollAsync(CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsrockDeviceConnection? current;
            lock (gate)
            {
                if (disposed) return;
                // RGB configuration queries are HID exchanges, not passive telemetry. Read once per
                // connection so monitoring sends no periodic traffic to a running native effect.
                // SetConnection invalidates this cache. Commands and recovery still read fresh state.
                current = active is not null || observed is not null && readError is null ? connection : null;
            }
            if (current is not null) backend.Check(current); else await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        finally { io.Release(); }
    }
    public void ConfigureLighting(PhysicalDeviceBinding? binding, IEnumerable<LightingEndpointConfiguration> configurations, IEnumerable<RegisteredEndpoint> endpoints)
    {
        var values = configurations.Take(9).Select(Contract.Copy).ToArray(); var registered = endpoints.Take(9).Select(Contract.Copy).ToArray();
        if (values.Length > 8 || registered.Length > 8 || values.Any(v => !DeviceLightingConfiguration.ValidEndpoint(v) || v.Target.DeviceId != DeviceId) ||
            values.DistinctBy(v => v.Target).Count() != values.Length || registered.Any(e => e is null || e.Kind != EndpointKind.Lighting) ||
            registered.DistinctBy(e => e.Id).Count() != registered.Length || values.Any(v => !registered.Any(e => e.Id == v.Target.EndpointId)))
            throw new ArgumentException("マザー照明の設定が不正です");
        if (!io.Wait(0)) throw new IOException("照明処理の完了後に設定してください");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (journalError is not null) throw new IOException(journalError);
                if (active is not null || journal!.Pending is not null || nativeJournal!.Pending is not null) throw new IOException("照明を終了・復旧してから設定してください");
                if (binding is not null && binding != Candidate()) throw new IOException("照明の関連付けが一致しません");
                settings = values.ToDictionary(v => Zone(v.Target.EndpointId), v => (v, registered.Single(e => e.Id == v.Target.EndpointId).Setup));
                this.binding = binding;
            }
        }
        finally { io.Release(); }
    }
    public string? Validate(DeviceEndpointAction action)
    {
        lock (gate)
        {
            try
            {
                var zone = Zone(action.EndpointId);
                if (observed?.Zones.Any(z => z.Address == zone) != true) return "未検出の照明ゾーンです";
                if (action.Operation == DeviceOperation.StopLighting) return null;
                if (action.Operation != DeviceOperation.SetLighting) return "未対応の照明操作です";
                if (Ready(zone) is { } error) return error;
                if (active is not null && !active.Targets.Contains(zone)) return "同じ機器の同期を終了してから設定してください";
                _ = NativeSetting(action.Lighting!); return null;
            }
            catch (ArgumentException e) { return e.Message; }
        }
    }
    public string? ValidateLightingStream(string[] endpoints, LightingProgram program)
    {
        lock (gate)
        {
            if (!DeviceLighting.ValidAnimation(program) || endpoints.Length is < 1 or > 8 || endpoints.Distinct().Count() != endpoints.Length) return "同期設定が不正です";
            try
            {
                var zones = endpoints.Select(Zone).ToArray();
                foreach (var zone in zones) if (Ready(zone) is { } error) return error;
                if (nativeFaulted.Count != 0) return "Native照明の復旧後に同期を開始してください";
                if (active is null && observed!.Zones.Any(z => !zones.Contains(z.Address) && z.Setting.Mode is not (0 or 1)))
                    return "同期対象外の照明を固定色または消灯にしてください";
                if (active is not null && !active.Targets.Intersect(zones).Any()) return "マザー全体の同期が実行中です。同じ機器を別の同期へ分割できません";
                return null;
            }
            catch (ArgumentException e) { return e.Message; }
        }
    }
    public async Task<IDeviceLightingStream> OpenLightingStreamAsync(string[] endpoints, LightingProgram program, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) { if (active is not null) throw new IOException("マザー照明の同期が実行中です"); }
            await ReadCoreAsync(ct).ConfigureAwait(false);
            AsrockDeviceConnection c; PhysicalDeviceBinding b; RgbZone[] zones; Dictionary<byte, LightingExitPolicy> exits;
            lock (gate)
            {
                if (ValidateLightingStream(endpoints, program) is { } error) throw new IOException(error);
                c = connection!; b = Candidate()!;
                zones = endpoints.Select(Zone).Select(z => observed!.Zones.Single(p => p.Address == z))
                    .Select(z => new RgbZone("RGB " + z.Address, 1, (uint)z.Count, null, null, "", z.Address, z.Setting)).ToArray();
                exits = zones.ToDictionary(z => z.HardwareZone!.Value, z => settings[z.HardwareZone!.Value].Config.Exit!);
            }
            var scope = new AsrockRecoveryScope(b, c.ConnectionId, c.Confidence, Guid.NewGuid().ToString("N"));
            var targets = zones.Select(z => z.HardwareZone!.Value).ToArray();
            var before = nativeJournal!.Pending;
            var transferring = before?.Entries.Select(e => e.Zone).Intersect(targets).Order().ToArray() ?? [];
            var handoff = transferring.Length == 0 ? null : new AsrockNativeHandoff(before!, transferring);
            var originals = before?.Entries.Where(e => transferring.Contains(e.Zone)).ToDictionary(e => e.Zone, e => e.Original);
            var transport = await AsrockRgbStream.OpenControlledAsync(c.Path, zones, directory, program, exits, () => Verify(c, b),
                () => backend.Borrow(c), () => boot, backend.CheckConflicts, journal!.ForStream(scope, targets, nativeJournal, handoff), originals).ConfigureAwait(false);
            var stream = new Stream(this, transport, targets);
            lock (gate) active = stream;
            return stream;
        }
        finally { io.Release(); }
    }
    private sealed class Stream(AsrockDeviceDriver driver, AsrockRgbStream transport, byte[] targets,
        AsrockRgbStream.Expansion? expansion = null, Stream? previous = null) : ISelectiveDeviceLightingStream
    {
        public byte[] Targets => targets;
        public AsrockRgbStream Transport => transport;
        private DeviceDriverReleaseResult[]? ended;
        private void Adopt()
        {
            lock (driver.gate)
            {
                if (previous is not null)
                {
                    if (driver.active != previous) throw new IOException("追加元の照明ストリームが変更されています");
                    driver.active = this; previous = null;
                }
                if (driver.active != this) throw new IOException("照明ストリームの操作権がありません");
            }
        }
        public async Task<DeviceDriverReleaseResult[]> WriteAsync(DeviceLightingFrame frame, CancellationToken ct)
        {
            await driver.io.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Adopt();
                var result = await transport.WriteExpandedAsync(frame, expansion, ct).ConfigureAwait(false);
                if (transport.OwnsTargets(targets)) expansion = null;
                return result;
            }
            finally { driver.io.Release(); }
        }
        public async Task<DeviceDriverReleaseResult[]> EndSelectedAsync(string[] endpoints, CancellationToken ct)
        {
            var ending = endpoints.Select(Zone).ToArray();
            if (ending.Length == targets.Length && ending.ToHashSet().SetEquals(targets)) return await EndAsync(DeviceLightingStopReason.Normal, ct).ConfigureAwait(false);
            await driver.io.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Adopt();
                if (ended is not null || expansion is not null || !transport.OwnsTargets(targets)) throw new IOException("照明の構成変更が完了していません");
                var result = await transport.EndSelectedAsync(ending, ct).ConfigureAwait(false);
                var removed = result.Where(r => r.Result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified).Select(r => Zone(r.EndpointId)).ToHashSet();
                if (removed.Count != 0)
                {
                    var saved = driver.journal!.Pending ?? throw new IOException("残った照明の復旧記録がありません");
                    result = result.Select(r => removed.Contains(Zone(r.EndpointId)) ? r with { Result = r.Result with {
                        ReportedLighting = AsrockRgbStream.ReportedProgram(saved.Desired[Zone(r.EndpointId)]) } } : r).ToArray();
                    lock (driver.gate)
                    {
                        targets = targets.Where(z => !removed.Contains(z)).ToArray();
                        driver.nativeOwned.IntersectWith(driver.nativeJournal?.Pending?.Entries.Select(e => e.Zone) ?? []);
                    }
                }
                return result;
            }
            finally { driver.io.Release(); }
        }
        public async Task<DeviceDriverReleaseResult[]> EndAsync(DeviceLightingStopReason reason, CancellationToken ct)
        {
            await driver.io.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ended is not null) return Contract.Copy(ended);
                Adopt();
                var released = await ((IDeviceLightingStream)transport).EndAsync(reason, CancellationToken.None).ConfigureAwait(false);
                ended = targets.Select(z => released.SingleOrDefault(r => r.EndpointId == "zone:" + z) ?? new DeviceDriverReleaseResult("zone:" + z,
                    driver.journal!.Pending?.Targets.Contains(z) == true ? new(Outcome.Unknown, "追加した照明の復旧記録が残っています") :
                    new(Outcome.Confirmed, "この端子へのDirect送信は開始していません"))).ToArray();
                lock (driver.gate) if (driver.active == this) driver.active = null;
                try { await driver.ReadCoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                // If no Direct command ran, the handoff was rolled back. The pre-existing native output still
                // belongs to this failed/stopped group and must be released before its ownership is reported clear.
                var native = await driver.ReleaseNativeCoreAsync(targets).ConfigureAwait(false);
                ended = ended.Select(r => native.SingleOrDefault(n => n.EndpointId == r.EndpointId) is { } n
                    ? r.Result.Outcome == Outcome.Confirmed ? n : new(r.EndpointId, new(Outcome.Unknown,
                        r.Result.Message + " / " + n.Result.Message, ReportedLighting: n.Result.ReportedLighting)) : r).ToArray();
                lock (driver.gate) driver.nativeOwned.IntersectWith(driver.nativeJournal?.Pending?.Entries.Select(e => e.Zone) ?? []);
                return Contract.Copy(ended);
            }
            finally { driver.io.Release(); }
        }
    }
    public async Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct)
    {
        if (Validate(action) is { } invalid) return new(Outcome.Rejected, invalid);
        if (action.Operation == DeviceOperation.SetLighting) return await ApplyNativeAsync(action, ct).ConfigureAwait(false);
        lock (gate) if (active is not null && !active.Targets.Contains(Zone(action.EndpointId)))
            return new(Outcome.Rejected, "同じ機器の同期を終了してからNative照明を終了してください");
        var results = await EndOrRecoverAsync(DeviceLightingStopReason.Normal, ct).ConfigureAwait(false);
        var native = await ReleaseNativeAsync([Zone(action.EndpointId)], ct).ConfigureAwait(false);
        if (native.Length != 0) return native.Single().Result;
        return results.SingleOrDefault(r => r.EndpointId == action.EndpointId)?.Result ?? new(Outcome.Confirmed, "照明の復旧待ちはありません");
    }
    private async Task<DeviceDriverReleaseResult[]> EndOrRecoverAsync(DeviceLightingStopReason reason, CancellationToken ct)
    {
        Stream? stream; lock (gate) stream = active;
        if (stream is not null) return await stream.EndAsync(reason, ct).ConfigureAwait(false);
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (journalError is not null)
            {
                lock (gate) return (observed?.Zones.Select(z => z.Address) ?? []).Select(z =>
                    new DeviceDriverReleaseResult("zone:" + z, new(Outcome.Unknown, journalError))).ToArray();
            }
            var saved = journal!.Pending; if (saved is null) return [];
            DeviceDriverReleaseResult[] Unknown(string message) => saved.Targets.Select(z => new DeviceDriverReleaseResult("zone:" + z, new(Outcome.Unknown, message))).ToArray();
            AsrockDeviceConnection? current; PhysicalDeviceBinding? b;
            lock (gate) { current = connection; b = Candidate(); }
            if (current is null || b is null) return Unknown("復旧する照明機器を取得できません");
            if (AsrockBoundRecovery.Check(saved, boot, b, current.ConnectionId, current.Confidence) is { } mismatch) return Unknown(mismatch);
            try
            {
                journal.CheckCurrent(saved);
                if (saved.Handoff is not null) nativeJournal!.TransferOut(saved.Handoff);
                var restored = await Task.Run(() =>
                {
                    backend.CheckConflicts(); using var session = backend.Borrow(current);
                    byte[] Exchange(byte[] packet)
                    {
                        void Guard()
                        {
                            if (Verify(current, b) is { } error) throw new IOException(error);
                            if (packet[1] is not (0x14 or 0x11))
                            {
                                journal.CheckCurrent(saved);
                                if (saved.Handoff is not null) nativeJournal!.CheckTransferred(saved.Handoff);
                            }
                        }
                        if (session is IAsrockGuardedRgbSession queued) return queued.Exchange(packet, Guard);
                        Guard(); return session.Exchange(packet);
                    }
                    return new AsrockVolatileTransaction(saved.Snapshot, Exchange).Restore(saved.Desired);
                }, CancellationToken.None).ConfigureAwait(false);
                if (restored.Restored)
                {
                    if (saved.Handoff is not null) nativeJournal!.CheckTransferred(saved.Handoff);
                    journal.Complete(saved);
                    lock (gate) nativeOwned.IntersectWith(nativeJournal?.Pending?.Entries.Select(e => e.Zone) ?? []);
                }
                try { await ReadCoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                return AsrockRgbStream.RestoreResults(restored, saved.Targets, null);
            }
            catch (Exception e) { return Unknown("マザー照明の復旧を確認できません: " + e.Message); }
        }
        finally { io.Release(); }
    }
    public async Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
    {
        var stream = await EndOrRecoverAsync(reason switch
        {
            DeviceReleaseReason.NormalExit => DeviceLightingStopReason.SessionEnded, DeviceReleaseReason.Suspended => DeviceLightingStopReason.Suspended,
            DeviceReleaseReason.ConfigurationChanged => DeviceLightingStopReason.ConfigurationChanged, DeviceReleaseReason.ServiceStopping => DeviceLightingStopReason.ServiceStopping,
            _ => DeviceLightingStopReason.OwnerLost
        }, ct).ConfigureAwait(false);
        var native = await ReleaseNativeAsync(null, ct).ConfigureAwait(false);
        return [.. stream, .. native.Where(n => !stream.Any(s => s.EndpointId == n.EndpointId))];
    }
    public MigratedReference[] LegacyReferences()
    {
        lock (gate) return connection is { } c && observed is { } s ? s.Zones.Select(z =>
            new MigratedReference("rgb", c.Path, "zone:" + z.Address, new(DeviceId, "zone:" + z.Address))).ToArray() : [];
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; }
        try { await ReleaseAsync(DeviceReleaseReason.ServiceStopping, CancellationToken.None).ConfigureAwait(false); }
        finally { lock (gate) disposed = true; }
    }
}
