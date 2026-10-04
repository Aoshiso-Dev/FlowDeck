using System.Security.Cryptography;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record ConfigurationApplication(int Version, string RequestId, ConfigurationPointer Before, ConfigurationPointer After,
    string Phase, string? Error = null);

/// <summary>Service-owned configuration transfer and transactional adoption. Only one authenticated owner's bounded upload is retained.</summary>
public sealed partial class DeviceConfigurationAuthority : IDeviceCoolingGroups, IDeviceCoolingPersistence, IDeviceCoolingCalibrationPersistence, IDeviceLightingLibrary, IDeviceLightingPersistence, IDeviceLightingAdjustments, IDeviceNotificationConfiguration, IDeviceM5ConfigurationHandler, IDeviceOpenRgbInspectionHandler, IDeviceProviderRoutingHandler, IDeviceNamingConfigurationHandler, IDeviceOpenRgbRuntimeInspectionHandler, IDeviceChunkedConfigurationSnapshotHandler, IDeviceCacheRetirementHandler
{
    private sealed class Upload(string expected, byte[] bytes, string digest)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Expected { get; } = expected;
        public byte[] Bytes { get; } = bytes;
        public string Digest { get; } = digest;
        public int Written;
        public DeviceConfigurationPackage? Validated;
        public DeviceProviderRouteReview? ProviderRoute;
        public DeviceEndpointSetupScope? EndpointSetup;
    }
    private readonly ConfigurationGenerationStore store;
    private readonly IEventLog? retentionLog;
    internal ConfigurationRetentionResult? LastRetention { get; private set; }
    private readonly string installation;
    private readonly IDeviceProviderRouting? providerRouting;
    private readonly DeviceProviderRoutingDefinition? routingDefinition;
    private readonly SemaphoreSlim access = new(1);
    private volatile DeviceConfigurationPackage configuration;
    private volatile ConfigurationPointer generation;
    private string? owner, snapshotId, snapshotGeneration, failure;
    private byte[]? snapshot;
    private Upload? upload;
    private volatile bool applyingConfiguration;
    public DeviceConfigurationPackage Configuration => Contract.Copy(configuration);
    public ConfigurationPointer Generation => generation;
    public DeviceConfigurationAuthority(ConfigurationGenerationStore store, string installation, ConfigurationGeneration adopted, IDeviceProviderRouting? providerRouting = null,
        IDeviceOpenRgbInspector? openRgbInspector = null, IEventLog? retentionLog = null, IDeviceOpenRgbRuntimeInspector? openRgbRuntimeInspector = null)
    {
        this.store = store; this.installation = installation; this.providerRouting = providerRouting; this.retentionLog = retentionLog;
        routingDefinition = DeviceProviderRoutingDefinition.Capture(providerRouting);
        this.openRgbInspector = openRgbInspector;
        this.openRgbRuntimeInspector = openRgbRuntimeInspector;
        configuration = DeviceConfigurationCodec.Read(adopted.Files); generation = adopted.Pointer;
        coolingResumeIntent = configuration.Cooling.Endpoints.ToDictionary(c => c.Target, c => c.ResumeOnStart);
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("インストール識別子が不正です");
        var path = Path.Combine(store.DirectoryPath, "configuration-application.json");
        if (File.Exists(path))
        {
            var previous = ServiceEnvironment.Read<ConfigurationApplication>(path);
            static bool Pointer(ConfigurationPointer p) => p is { Version: 1 } && DeviceServiceProtocol.Id(p.Id) && DeviceConfigurationProtocol.Digest(p.ManifestHash);
            if (previous is not { Version: 1 } || !DeviceServiceProtocol.Id(previous.RequestId) || !Pointer(previous.Before) || !Pointer(previous.After) ||
                previous.Phase is not ("accepted" or "applied" or "rolled-back" or "failed" or "interrupted" or "superseded"))
                throw new IOException("設定反映の記録が不正です");
            if (previous.Phase == "accepted") ServiceEnvironment.Write(path, previous with {
                Phase = generation == previous.After ? "applied" : generation == previous.Before ? "interrupted" : "superseded" });
        }
        PublishLightingLibrary();
    }
    private void MaintainConfigurationHistory()
    {
        // Maintenance never turns an already committed setting into an actuation failure.
        try { LastRetention = store.MaintainHistory(generation); }
        catch (Exception e) { LastRetention = new(0, 0, null, [e.Message]); }
        if (LastRetention.Removed > 0 || LastRetention.Errors.Length > 0)
            try { retentionLog?.Write("configuration-retention", LastRetention); } catch { /* Do not undo committed settings if logging is unavailable. */ }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private DeviceConfigurationReply Reply(string message, string? transfer = null, int? size = null, string? hash = null,
        int? offset = null, byte[]? data = null, string[]? changed = null, DeviceBindingStatus[]? bindings = null) =>
        new(Outcome.Confirmed, message, generation.Id, transfer, size, hash, offset, data, changed, bindings);
    private void CheckCurrent()
    {
        if (failure is not null) throw new IOException(failure);
        try
        {
            if (store.ReadActivePointer() != generation) throw new IOException("サービス外で設定世代が変更されました。再起動して確認してください");
        }
        catch { Interlocked.Increment(ref connectionPreparationRevision); throw; }
    }
    private Upload CurrentUpload(string? id) => upload is { } value && value.Id == id ? value : throw new IOException("この接続の設定転送がありません");
    public async Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string requestId, string owner,
        DeviceControlRuntime runtime, CancellationToken ct)
    {
        if (!DeviceConfigurationProtocol.Valid(request)) throw new IOException("設定要求が不正です");
        if (request.Operation == DeviceConfigurationOperation.InspectOpenRgb) return await InspectOpenRgbAsync(request, ct).ConfigureAwait(false);
        if (request.Operation == DeviceConfigurationOperation.InspectOpenRgbRuntime) return await InspectOpenRgbRuntimeAsync(request, ct).ConfigureAwait(false);
        await access.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (this.owner != owner) { this.owner = owner; upload = null; snapshot = null; snapshotId = null; snapshotGeneration = null; }
            if (!applyingConfiguration && request.Operation is (DeviceConfigurationOperation.Validate or DeviceConfigurationOperation.ValidateProviderRoute or DeviceConfigurationOperation.ValidateProviderSelection or DeviceConfigurationOperation.ValidateCoolingSetup or DeviceConfigurationOperation.ValidateLightingSetup) &&
                upload is { } validationUpload && validationUpload.Id == request.TransferId)
            { validationUpload.Validated = null; validationUpload.ProviderRoute = null; validationUpload.EndpointSetup = null; }
            CheckCurrent();
            if (applyingConfiguration && request.Operation is not (DeviceConfigurationOperation.Snapshot or DeviceConfigurationOperation.SnapshotWithCandidates or DeviceConfigurationOperation.ReadSnapshot))
                throw new IOException("設定の反映中です");
            switch (request.Operation)
            {
                case DeviceConfigurationOperation.PrepareConnectionLighting:
                    return PrepareConnectionLighting(owner, runtime);
                case DeviceConfigurationOperation.PreviewCacheRetirement:
                case DeviceConfigurationOperation.RetireDisconnected:
                    void CheckRetirementConfiguration()
                    {
                        CheckCurrent();
                        if (request.ExpectedGeneration != generation.Id) throw new IOException("設定が変更されました。整理内容を確認し直してください");
                    }
                    CheckRetirementConfiguration();
                    var retirement = request.CacheRetirement!;
                    runtime.CheckCacheRetirement(request.TopologyRevision!.Value, retirement);
                    if (request.Operation == DeviceConfigurationOperation.PreviewCacheRetirement)
                        return Reply("切断済み機器の整理内容を確認しました") with { CacheRetirement = retirement };
                    applyingConfiguration = true; access.Release();
                    try
                    {
                        await runtime.RetireDisconnectedAsync(owner, request.TopologyRevision.Value, retirement.DeviceId, retirement.Generation,
                            CheckRetirementConfiguration).ConfigureAwait(false);
                        return Reply("検出履歴を整理しました。保存した名前・設定は保持されています") with { CacheRetirement = retirement };
                    }
                    finally { await access.WaitAsync().ConfigureAwait(false); applyingConfiguration = false; }
                case DeviceConfigurationOperation.SaveDeviceName:
                    return SaveDeviceName(request);
                case DeviceConfigurationOperation.SaveM5:
                    return SaveM5(request, runtime);
                case DeviceConfigurationOperation.SaveNotifications:
                    if (request.ExpectedGeneration != generation.Id) throw new IOException("編集元の通知設定が変更されました。現在の設定を読み込んでください");
                    if (Same(configuration.Preferences.Notifications, request.Notifications)) return Reply("通知設定に変更はありません", changed: []);
                    PersistConfiguration(configuration with { Preferences = configuration.Preferences with { Notifications = Contract.Copy(request.Notifications!) } }, "通知設定");
                    return Reply("通知設定を保存しました", changed: ["preferences.json"]);
                case DeviceConfigurationOperation.SaveCoolingStartup:
                    return SaveCoolingStartup(request, runtime);
                case DeviceConfigurationOperation.SaveCoolingGroup:
                    return SaveCoolingGroup(request, runtime);
                case DeviceConfigurationOperation.RegisterMonitoring:
                    return RegisterMonitoring(request, runtime);
                case DeviceConfigurationOperation.SaveDashboard:
                    if (request.ExpectedGeneration != generation.Id) throw new IOException("編集元の設定が変更されました。現在の設定を読み込んでください");
                    if (Same(configuration.Dashboard, request.Dashboard)) return Reply("表示設定に変更はありません", changed: []);
                    PersistConfiguration(configuration with { Dashboard = Contract.Copy(request.Dashboard!) }, "表示設定");
                    return Reply("表示設定を保存しました", changed: ["dashboard.json"]);
                case DeviceConfigurationOperation.Snapshot:
                case DeviceConfigurationOperation.SnapshotWithCandidates:
                    var metadata = runtime.CaptureConfigurationCandidates();
                    var combined = request.Operation == DeviceConfigurationOperation.SnapshotWithCandidates;
                    if (!combined && metadata.Setup.Length > 256) throw new IOException("多数の設定候補の分割取得に対応するGUIへ更新してください");
                    var payload = combined ? DeviceConfigurationSnapshotCodec.Serialize(new(generation.Id, configuration, metadata.Bindings, metadata.Setup, metadata.Topology)) :
                        DeviceConfigurationCodec.SerializePackage(configuration);
                    var legacyMetadata = combined ? null : Reply("設定の取得を開始します", new string('a', 32), payload.Length, new string('b', 64)) with {
                        Candidates = metadata.Bindings, Setup = metadata.Setup };
                    if (legacyMetadata is not null && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacyMetadata, Contract.Json).Length > DeviceServiceProtocol.MaximumFrameBytes - 1024)
                        throw new IOException("設定候補の分割取得に対応するGUIへ更新してください");
                    snapshot = payload; snapshotId = Guid.NewGuid().ToString("N"); snapshotGeneration = generation.Id;
                    return Reply("設定の取得を開始します", snapshotId, snapshot.Length, Hash(snapshot)) with {
                        Candidates = combined ? null : metadata.Bindings, Setup = combined ? null : metadata.Setup };
                case DeviceConfigurationOperation.ReadSnapshot:
                    if (snapshot is null || snapshotId != request.TransferId || request.Offset >= snapshot.Length) throw new IOException("設定の取得位置が不正です");
                    var at = request.Offset!.Value; var part = snapshot.AsSpan(at, Math.Min(DeviceConfigurationProtocol.ChunkBytes, snapshot.Length - at)).ToArray();
                    return Reply("設定を取得しました", snapshotId, snapshot.Length, offset: at, data: part) with { Generation = snapshotGeneration! };
                case DeviceConfigurationOperation.BeginUpload:
                    if (request.ExpectedGeneration != generation.Id) throw new IOException("編集元の設定が古くなっています");
                    upload = new(generation.Id, new byte[request.TotalBytes!.Value], request.Sha256!);
                    return Reply("設定転送を受け付けました", upload.Id, upload.Bytes.Length, offset: 0);
                case DeviceConfigurationOperation.Append:
                    var transfer = CurrentUpload(request.TransferId);
                    if (request.Offset != transfer.Written || request.Data!.Length > transfer.Bytes.Length - transfer.Written) throw new IOException("設定転送の順序・長さが不正です");
                    request.Data.CopyTo(transfer.Bytes, transfer.Written); transfer.Written += request.Data.Length; transfer.Validated = null; transfer.ProviderRoute = null; transfer.EndpointSetup = null;
                    return Reply("設定を受信しました", transfer.Id, transfer.Bytes.Length, offset: transfer.Written);
                case DeviceConfigurationOperation.Validate:
                case DeviceConfigurationOperation.ValidateProviderRoute:
                case DeviceConfigurationOperation.ValidateProviderSelection:
                case DeviceConfigurationOperation.ValidateCoolingSetup:
                case DeviceConfigurationOperation.ValidateLightingSetup:
                    var pending = CurrentUpload(request.TransferId);
                    if (pending.Written != pending.Bytes.Length || Hash(pending.Bytes) != pending.Digest) throw new IOException("設定転送が未完了または整合性が不正です");
                    if (pending.Expected != generation.Id) throw new IOException("編集元の設定が変更されました");
                    var draft = DeviceConfigurationCodec.DeserializePackage(pending.Bytes);
                    // The GUI owns per-user appearance/window state; adopting service settings cannot replace it.
                    // Audit metadata is service-owned; references to removed registrations cannot survive a full replacement.
                    var retained = draft.Devices.Devices.SelectMany(d => d.Endpoints.Select(e => new EndpointReference(d.Id, e.Id))).ToHashSet();
                    draft = draft with { Preferences = draft.Preferences with { Ui = configuration.Preferences.Ui },
                        Migration = configuration.Migration with { References = configuration.Migration.References.Where(r => retained.Contains(r.Target)).ToArray() },
                        Lighting = NormalizeLightingRevisions(configuration.Lighting, draft.Lighting) };
                    var statuses = CheckBindings(draft, runtime);
                    var plan = PlanProviderRoutes(configuration, draft);
                    if (plan is not null && request.Operation is not (DeviceConfigurationOperation.ValidateProviderRoute or DeviceConfigurationOperation.ValidateProviderSelection))
                        throw new IOException("取得経路を変更するには、切替対象の専用確認が必要です");
                    if (plan is not null && request.Operation != DeviceConfigurationOperation.ValidateProviderSelection &&
                        !(configuration.Preferences.Providers?.OpenRgbDeviceIds ?? []).Order(StringComparer.Ordinal)
                            .SequenceEqual((draft.Preferences.Providers?.OpenRgbDeviceIds ?? []).Order(StringComparer.Ordinal)))
                        throw new IOException("機器の選択を変更するには、選択対象の専用確認が必要です");
                    var review = plan is null ? null : ReviewProviderRoutes(plan, runtime, draft);
                    var endpointSetup = request.Operation == DeviceConfigurationOperation.ValidateCoolingSetup ? ReviewEndpointSetup(configuration, draft, request.CoolingSetupTarget!) :
                        request.Operation == DeviceConfigurationOperation.ValidateLightingSetup ? ReviewEndpointSetup(configuration, draft, request.LightingSetupTarget!, lighting: true) : null;
                    if (endpointSetup is not null) runtime.CheckEndpointSetupScope(endpointSetup);
                    var oldFiles = DeviceConfigurationCodec.Write(configuration); var newFiles = DeviceConfigurationCodec.Write(draft);
                    var changes = oldFiles.Keys.Where(k => !oldFiles[k].AsSpan().SequenceEqual(newFiles[k])).ToArray();
                    pending.Validated = draft;
                    pending.ProviderRoute = review;
                    pending.EndpointSetup = endpointSetup;
                    var replyReview = request.Operation == DeviceConfigurationOperation.ValidateProviderSelection ? review : review is null ? null : review with { OpenRgbDeviceIds = null };
                    return Reply("設定の差分と機器の対応を確認しました", pending.Id, changed: changes, bindings: statuses) with { ProviderRoute = Contract.Copy(replyReview) };
                case DeviceConfigurationOperation.Apply:
                    var applying = CurrentUpload(request.TransferId);
                    applyingConfiguration = true;
                    // In-flight hardware commands must be able to finish their durable policy records while reconfiguration waits for them.
                    access.Release();
                    try { return await ApplyAsync(applying, requestId, request.TopologyRevision!.Value, owner, runtime, ct).ConfigureAwait(false); }
                    finally { await access.WaitAsync().ConfigureAwait(false); applyingConfiguration = false; }
                case DeviceConfigurationOperation.Discard:
                    _ = CurrentUpload(request.TransferId); upload = null; return Reply("設定の転送を破棄しました");
                default: throw new IOException("未対応の設定操作です");
            }
        }
        finally { access.Release(); }
    }
    private DeviceBindingStatus[] CheckBindings(DeviceConfigurationPackage next, DeviceControlRuntime runtime)
    {
        if (next.Bindings.InstallationId != installation) throw new IOException("別のPCの設定は実機の関連付けを確認してから採用してください");
        var candidates = runtime.PhysicalCandidates().ToDictionary(b => b.DeviceId);
        var observed = runtime.State().Inventory.Devices.Where(d => d.Connected).ToDictionary(d => d.Device.Id);
        var states = new List<DeviceBindingStatus>();
        foreach (var binding in next.Bindings.Devices)
        {
            var matches = candidates.GetValueOrDefault(binding.DeviceId) == binding && observed.ContainsKey(binding.DeviceId);
            if (!matches && !configuration.Bindings.Devices.Contains(binding)) throw new IOException("サービスが確認できない実機の関連付けは採用できません: " + binding.DeviceId);
            if (matches)
            {
                var registered = next.Devices.Devices.Single(d => d.Id == binding.DeviceId); var actual = observed[binding.DeviceId].Device;
                if (registered.Provider != actual.Provider || registered.Kind != actual.Kind || registered.Endpoints.Any(e =>
                    !actual.Endpoints.Any(a => a.Id == e.Id && a.Kind == e.Kind))) throw new IOException("登録した機器・端子が現在の機器と一致しません");
            }
            states.Add(new(binding.DeviceId, matches, matches ? "現在の機器と一致" : "保存した関連付けを保持・制御は無効"));
        }
        states.AddRange(next.Devices.Devices.Where(d => !next.Bindings.Devices.Any(b => b.DeviceId == d.Id))
            .Select(d => new DeviceBindingStatus(d.Id, false, "実機の関連付けが未設定")));
        return states.ToArray();
    }
    internal static PhysicalDeviceBinding[] ConfigureDrivers(DeviceConfigurationPackage value, string installation, IDeviceControlDriver[] drivers,
        IReadOnlySet<string>? continuing = null)
    {
        var expected = value.Bindings.InstallationId == installation ? value.Bindings.Devices : [];
        var active = new List<PhysicalDeviceBinding>();
        foreach (var driver in drivers)
        {
            var candidate = (driver as IPhysicalDeviceDriver)?.BindingCandidate;
            var bound = expected.SingleOrDefault(b => b.DeviceId == driver.DeviceId);
            var matches = bound is not null && bound == candidate && driver.Observe() is not null;
            if (continuing?.Contains(driver.DeviceId) == true)
            {
                if (matches) active.Add(bound!);
                continue;
            }
            if (driver.Observe() is null)
            {
                // Configure may require live definitions or telemetry. Still revoke cached permission on an absent device;
                // reconnecting it must not resurrect settings removed while it was disconnected.
                (driver as IDeviceConfigurationRevocation)?.RevokeConfiguration();
                continue;
            }
            if (driver is IConfiguredCoolingDriver cooling)
            {
                var entries = matches ? value.Cooling.Endpoints.Where(c => c.Target.DeviceId == driver.DeviceId)
                    .Select(c => c with { ResumeOnStart = c.ResumeOnStart && value.Preferences.Startup.ResumeControl }).ToArray() : [];
                cooling.Configure(matches ? bound : null, entries);
            }
            if (driver is IConfiguredLightingDriver lighting)
            {
                var entries = matches ? (value.Lighting.Endpoints ?? []).Where(e => e.Target.DeviceId == driver.DeviceId).ToArray() : [];
                var endpoints = matches ? value.Devices.Devices.Single(d => d.Id == driver.DeviceId).Endpoints : [];
                lighting.ConfigureLighting(matches ? bound : null, entries, endpoints);
            }
            if (matches) active.Add(bound!);
        }
        return active.ToArray();
    }
    private async Task<DeviceConfigurationReply> ApplyAsync(Upload value, string requestId, long topology, string owner,
        DeviceControlRuntime runtime, CancellationToken ct)
    {
        DeviceConfigurationPackage next, old; ConfigurationPointer before; ConfigurationActivation staged; ConfigurationApplication record;
        DeviceProviderRoutePlan? routePlan;
        DeviceProviderRouteReview? currentReview;
        var recordPath = Path.Combine(store.DirectoryPath, "configuration-application.json");
        await access.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckCurrent();
            if (value.Validated is not { } validated || value.Expected != generation.Id) throw new IOException("現在の世代に対する差分確認が必要です");
            next = Contract.Copy(validated); _ = CheckBindings(next, runtime); ct.ThrowIfCancellationRequested();
            before = generation; old = configuration; routePlan = PlanProviderRoutes(old, next);
            currentReview = routePlan is null ? null : ReviewProviderRoutes(routePlan, runtime, next);
            if (!Same(value.ProviderRoute, currentReview) || currentReview is not null && currentReview.TopologyRevision != topology)
            {
                value.Validated = null; value.ProviderRoute = null;
                throw new IOException("確認した取得経路の切替対象・方針・機器構成が変わりました。差分を再確認してください");
            }
            staged = store.Stage(DeviceConfigurationCodec.Write(next));
            if (staged.ExpectedCurrent != before) throw new IOException("設定世代が競合しています");
            record = new(1, requestId, before, staged.Target, "accepted"); ServiceEnvironment.Write(recordPath, record);
        }
        finally { access.Release(); }
        if (routePlan is not null)
            return await ApplyProviderChangeAsync(old, next, before, staged, record, routePlan, currentReview!, owner, topology, runtime).ConfigureAwait(false);
        var rolledBack = false;
        try
        {
            await runtime.ReconfigureAsync(owner, topology, async (drivers, token) =>
            {
                await access.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    CheckCurrent();
                    if (generation != before) throw new IOException("処理中の冷却方針が保存されました。設定の差分を再取得してください");
                    try
                    {
                        _ = CheckBindings(next, runtime);
                        var active = value.EndpointSetup is { } scope ? ConfigureEndpointSetup(next, drivers, scope) : ConfigureDrivers(next, installation, drivers); token.ThrowIfCancellationRequested();
                        store.Activate(staged);
                        ServiceEnvironment.Write(recordPath, record with { Phase = "applied" });
                        configuration = Contract.Copy(next); generation = staged.Target;
                        PublishLightingLibrary();
                        coolingResumeIntent = next.Cooling.Endpoints.ToDictionary(c => c.Target, c => c.ResumeOnStart);
                        return active;
                    }
                    catch (Exception original)
                    {
                        try
                        {
                            var current = store.ReadActive()?.Pointer;
                            if (current == staged.Target) store.Activate(new(before, staged.Target));
                            else if (current != before) throw new IOException("復帰先の設定世代が競合しています");
                            _ = value.EndpointSetup is { } scope ? ConfigureEndpointSetup(old, drivers, scope) : ConfigureDrivers(old, installation, drivers);
                            ServiceEnvironment.Write(recordPath, record with { Phase = "rolled-back", Error = original.Message });
                            rolledBack = true;
                        }
                        catch (Exception rollback)
                        {
                            failure = "設定の復帰を確認できません。サービス再起動で保存世代を確認してください";
                            throw new AggregateException(failure, original, rollback);
                        }
                        throw;
                    }
                }
                finally { access.Release(); }
            }, endpointSetup: value.EndpointSetup).ConfigureAwait(false);
            await access.WaitAsync().ConfigureAwait(false);
            try { upload = null; return Reply(value.EndpointSetup is null ? "設定を反映しました。制御は監視状態です" : value.EndpointSetup.IsLighting ? "照明の設定を保存しました。変更対象外の制御は継続しています" : "冷却端子の設定を保存しました。変更対象外の制御は継続しています", changed: ConfigurationGenerationStore.ComponentNames.ToArray()); }
            finally { access.Release(); }
        }
        catch (Exception e)
        {
            await access.WaitAsync().ConfigureAwait(false);
            try
            {
                if (failure is null && !rolledBack) ServiceEnvironment.Write(recordPath, record with { Phase = "failed", Error = e.Message });
                var message = failure ?? "設定を反映できませんでした: " + e.Message;
                return new(failure is null ? Outcome.Failed : Outcome.Unknown, message.Length > 16384 ? message[..16384] : message, generation.Id);
            }
            finally { access.Release(); }
        }
    }
}
