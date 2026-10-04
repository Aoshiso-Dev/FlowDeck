namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private IDeviceConfigurationHandler? configurationHandler;
    private IDeviceCoolingPersistence? coolingPersistence;
    private IDeviceLightingLibrary? lightingLibrary;
    private IDeviceLightingPersistence? lightingPersistence;
    private IDeviceLightingAdjustments? lightingAdjustments;
    private long lightingSaveOrder;
    private long coolingSaveOrder;
    private bool configurationChanging;
    private TaskCompletionSource? configurationCompletion;
    public void InstallConfigurationHandler(IDeviceConfigurationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            if (started || session is not null || configurationHandler is not null || stopping) throw new InvalidOperationException("設定窓口は起動時に登録してください");
            configurationHandler = handler;
            coolingPersistence = handler as IDeviceCoolingPersistence;
            lightingLibrary = handler as IDeviceLightingLibrary;
            lightingPersistence = handler as IDeviceLightingPersistence;
            lightingAdjustments = handler as IDeviceLightingAdjustments;
        }
    }
    public PhysicalDeviceBinding[] PhysicalCandidates()
    {
        lock (gate) return drivers.Values.OfType<IPhysicalDeviceDriver>().Select(d => d.BindingCandidate).Where(b => b is not null).Select(b => Contract.Copy(b!)).ToArray();
    }
    public DeviceSetupCandidate[] SetupCandidates()
    {
        lock (gate)
        {
            var inventory = registry.Snapshot;
            var result = new List<DeviceSetupCandidate>();
            foreach (var driver in drivers.Values.OfType<IDeviceSetupDriver>())
            {
                var id = ((IDeviceControlDriver)driver).DeviceId;
                var actual = inventory.Devices.SingleOrDefault(d => d.Device.Id == id && d.Connected);
                if (actual is null) continue;
                foreach (var options in driver.DescribeSetup())
                {
                    var endpoint = actual.Device.Endpoints.SingleOrDefault(e => e.Id == options.EndpointId);
                    var feature = options.CoolingExitActions.Length > 0 ? DeviceFeature.FixedDuty : DeviceFeature.FixedColor;
                    if (endpoint?.Capabilities.Any(c => c.Feature == feature && c.Support == ImplementationSupport.Supported) != true) continue;
                    var candidate = new DeviceSetupCandidate(new(id, options.EndpointId), actual.Generation, options);
                    if (!DeviceSetup.Valid(candidate)) throw new IOException("ドライバーの設定候補が不正です");
                    result.Add(candidate);
                }
            }
            var candidates = result.ToArray();
            if (!DeviceSetup.ValidCandidates(candidates)) throw new IOException("設定候補の数・識別が不正です");
            return Contract.Copy(candidates);
        }
    }
    public (long Topology, PhysicalDeviceBinding[] Bindings, DeviceSetupCandidate[] Setup) CaptureConfigurationCandidates()
    {
        lock (gate)
        {
            RefreshCore(); var inventory = registry.Snapshot;
            var connected = inventory.Devices.Where(d => d.Connected).Select(d => d.Device.Id).ToHashSet();
            return (inventory.TopologyRevision, PhysicalCandidates().Where(b => connected.Contains(b.DeviceId)).ToArray(), SetupCandidates());
        }
    }
    private async Task ExecuteConfigurationAsync(DeviceServiceRequest request, TaskCompletionSource<DeviceServiceReply> completion, CancellationToken ct)
    {
        try
        {
            if (request.Configuration!.Operation == DeviceConfigurationOperation.PrepareConnectionLighting && configurationHandler is not IDeviceConnectionLightingHandler)
                throw new IOException("接続限定の照明準備に対応していません");
            if (request.Configuration!.Operation == DeviceConfigurationOperation.InspectOpenRgbRuntime &&
                configurationHandler is not IDeviceOpenRgbRuntimeInspectionHandler { SupportsOpenRgbRuntimeInspection: true })
                throw new IOException("OpenRGBの導入確認に対応していません");
            if (request.Configuration!.Operation == DeviceConfigurationOperation.InspectOpenRgb &&
                configurationHandler is not IDeviceOpenRgbInspectionHandler { SupportsOpenRgbInspection: true })
                throw new IOException("OpenRGBの接続確認に対応していません");
            if (request.Configuration.Operation == DeviceConfigurationOperation.ValidateProviderRoute &&
                configurationHandler is not IDeviceProviderRoutingHandler { SupportsProviderRouting: true })
                throw new IOException("取得経路の切替確認に対応していません");
            if (request.Configuration.Operation == DeviceConfigurationOperation.ValidateProviderSelection &&
                configurationHandler is not IDeviceProviderRoutingHandler { SupportsProviderRouting: true, SupportsProviderDeviceSelection: true })
                throw new IOException("機器別の選択確認に対応していません");
            if (request.Configuration.Operation is (DeviceConfigurationOperation.PreviewCacheRetirement or DeviceConfigurationOperation.RetireDisconnected) &&
                configurationHandler is not IDeviceCacheRetirementHandler) throw new IOException("検出履歴の整理に対応していません");
            var result = await configurationHandler!.ExecuteAsync(request.Configuration!, request.Id, request.Session, this, ct).ConfigureAwait(false);
            if (!DeviceConfigurationProtocol.Valid(result) ||
                (result.PreparedLighting is not null && request.Configuration.Operation != DeviceConfigurationOperation.PrepareConnectionLighting) ||
                (request.Configuration.Operation == DeviceConfigurationOperation.PrepareConnectionLighting && result.Outcome == Outcome.Confirmed && result.PreparedLighting is null) ||
                result.CacheRetirement is not null && (request.Configuration.Operation is not (DeviceConfigurationOperation.PreviewCacheRetirement or DeviceConfigurationOperation.RetireDisconnected) ||
                    result.CacheRetirement != request.Configuration.CacheRetirement || result.Generation != request.Configuration.ExpectedGeneration) ||
                request.Configuration.Operation is (DeviceConfigurationOperation.PreviewCacheRetirement or DeviceConfigurationOperation.RetireDisconnected) && result.Outcome == Outcome.Confirmed && result.CacheRetirement is null ||
                result.OpenRgbRuntime is not null && (request.Configuration.Operation != DeviceConfigurationOperation.InspectOpenRgbRuntime ||
                    result.Generation != request.Configuration.ExpectedGeneration) ||
                request.Configuration.Operation == DeviceConfigurationOperation.InspectOpenRgbRuntime && result.Outcome == Outcome.Confirmed && result.OpenRgbRuntime is null ||
                result.ProviderRoute is not null && request.Configuration.Operation is not (DeviceConfigurationOperation.ValidateProviderRoute or DeviceConfigurationOperation.ValidateProviderSelection) ||
                result.ProviderRoute?.OpenRgbDeviceIds is not null && request.Configuration.Operation != DeviceConfigurationOperation.ValidateProviderSelection ||
                result.OpenRgbInspection is { } inspection && (request.Configuration!.Operation != DeviceConfigurationOperation.InspectOpenRgb ||
                    inspection.Port != request.Configuration.OpenRgbPort || result.Generation != request.Configuration.ExpectedGeneration) ||
                request.Configuration!.Operation == DeviceConfigurationOperation.InspectOpenRgb && result.Outcome == Outcome.Confirmed && result.OpenRgbInspection is null)
                throw new IOException("設定処理の応答が不正です");
            var success = result.Outcome == Outcome.Confirmed;
            var operation = new DeviceOperationResult(request.Id, request.Source, result.Outcome, clock.GetUtcNow(), [], result.Message);
            completion.TrySetResult(new(2, Epoch, request.Session, request.Id, success, success ? null : result.Message, operation, Configuration: result));
        }
        catch (Exception e) { completion.TrySetResult(Reject(request, e.Message)); }
        finally { lock (gate) { pendingCount--; TrimRequests(); } }
    }
    /// <summary>Quiesce hardware while retaining authenticated keepalives. The trusted callback applies/persists or rolls back one generation.</summary>
    public async Task ReconfigureAsync(string owner, long expectedTopology,
        Func<IDeviceControlDriver[], CancellationToken, Task<PhysicalDeviceBinding[]>> apply,
        DeviceProviderIsolation? isolation = null, DeviceEndpointSetupScope? endpointSetup = null)
    {
        Task[] hardware, monitoring; CancellationToken ct; TaskCompletionSource finished;
        lock (gate)
        {
            ExpireCore(); RefreshCore();
            if (stopping || releasing || configurationChanging || session != owner || registry.Snapshot.TopologyRevision != expectedTopology)
                throw new IOException("設定の反映前に接続・機器構成が変わりました");
            if (isolation is not null) ValidateProviderIsolationCore(isolation);
            if (endpointSetup is not null)
            {
                if (isolation is not null) throw new IOException("取得経路と端子設定は同時に変更できません");
                CheckEndpointSetupScope(endpointSetup);
                if (lifecycleError == endpointSetupFailure && failedEndpointSetupScope is { } failed && failed.Target == endpointSetup.Target && failed.IsLighting == endpointSetup.IsLighting &&
                    (failed.LightingEndpoints ?? []).ToHashSet().SetEquals(endpointSetup.LightingEndpoints ?? []) &&
                    failed.ReboundDevices.ToHashSet().SetEquals(endpointSetup.ReboundDevices))
                { lifecycleError = null; endpointSetupFailure = null; failedEndpointSetupScope = null; }
            }
            endpointSetupScope = endpointSetup is null ? null : Contract.Copy(endpointSetup);
            providerIsolation = isolation is null ? null : Contract.Copy(isolation);
            isolatedDeviceIds.Clear();
            if (providerIsolation is not null)
                isolatedDeviceIds.UnionWith(driverProviders.Where(p => providerIsolation.ProviderIds.Contains(p.Value)).Select(p => p.Key));
            RevokeConnectionLightingCore(); configurationChanging = true;
            foreach (var identification in lightingIdentifications.Values) identification.Cancellation.Cancel();
            configurationCompletion = finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ct = sessionLifetime!.Token;
            hardware = commandWork.Concat(endpointSetupScope is { IsLighting: true }
                ? lightingGroups.Values.Where(g => g.Participants.Any(p => ConfigurationBlocksLighting(p.DeviceId)))
                    .SelectMany(g => new Task[] { g.FrameWork, g.Monitor }) : Array.Empty<Task>()).ToArray();
            monitoring = polling.Where(p => ConfigurationBlocksPoll(p.Key)).Select(p => p.Value)
                .Concat(coolingWork.Where(p => ConfigurationBlocksDevice(p.Key)).Select(p => p.Value)).ToArray();
        }
        try
        {
            await Task.WhenAll(hardware.Concat(monitoring)).ConfigureAwait(false);
            IDeviceControlDriver[]? selected;
            lock (gate) selected = ConfigurationReleaseDrivers();
            if (endpointSetupScope is { } endpointScope) await ReleaseEndpointSetupAsync(endpointScope).ConfigureAwait(false);
            else await ReleaseDriversAsync(DeviceReleaseReason.ConfigurationChanged, selected).ConfigureAwait(false);
            IDeviceControlDriver[] current;
            lock (gate)
            {
                ExpireCore(); RefreshCore(); ct.ThrowIfCancellationRequested();
                if (session != owner || stopping || releasing || lifecycleError is not null || registry.Snapshot.TopologyRevision != expectedTopology)
                    throw new IOException(lifecycleError ?? "設定反映中に接続・機器構成が変わりました");
                current = drivers.Values.ToArray();
                providerChangesAllowed = endpointSetupScope is null; providerReplacementWork = null;
                providerReplacementLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }
            var bindings = await apply(current, ct).ConfigureAwait(false);
            Task? replacement;
            lock (gate) replacement = providerReplacementWork;
            if (replacement is not null) await replacement.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (bindings.Length > DeviceRegistry.MaximumDevices || bindings.Any(b => b is null || !DeviceConfigurationRules.Key(b.DeviceId) ||
                !DeviceConfigurationRules.Text(b.ProviderIdentity, 2048) || !DeviceConfigurationRules.Key(b.DefinitionId) ||
                b.Fingerprint is not { Length: 64 } || !b.Fingerprint.All(Uri.IsHexDigit)) || bindings.DistinctBy(b => b.DeviceId).Count() != bindings.Length)
                throw new IOException("反映した実機関連付けが不正です");
            lock (gate)
            {
                if (providerIsolation is not null)
                {
                    ValidateProviderIsolationCore(providerIsolation);
                    var retained = (physicalBindings ?? []).Values.Where(b => !ConfigurationBlocksDevice(b.DeviceId));
                    if (retained.Any(b => !bindings.Contains(b)) || bindings.Any(b => !ConfigurationBlocksDevice(b.DeviceId) &&
                        physicalBindings?.GetValueOrDefault(b.DeviceId) != b))
                        throw new IOException("継続する機器の関連付けは切替中に変更できません");
                }
                physicalBindings = bindings.ToDictionary(b => b.DeviceId, Contract.Copy);
                foreach (var key in states.Keys.Where(k => endpointSetupScope?.Contains(k) ?? ConfigurationBlocksDevice(k.DeviceId)).ToArray())
                    states[key] = states[key] with { PolicyRevision = checked(states[key].PolicyRevision + 1) };
                RefreshCore();
            }
        }
        catch (Exception e)
        {
            // A partial driver rollback cannot leave old write authority enabled. Recovery/configuration can be retried from monitoring.
            lock (gate)
            {
                providerReplacementLifetime?.Cancel();
                if (endpointSetupScope is { } endpointScope)
                {
                    // Other endpoints retain their existing binding and policy even when the edited endpoint fails.
                    if (physicalBindings is not null) foreach (var id in endpointScope.ReboundDevices) physicalBindings.Remove(id);
                    foreach (var target in states.Keys.Where(endpointScope.Contains)) recoveryRequired.Add(target);
                }
                else if (providerIsolation is null) physicalBindings = [];
                else if (physicalBindings is not null)
                    foreach (var id in physicalBindings.Keys.Where(ConfigurationBlocksDevice).ToArray()) physicalBindings.Remove(id);
                lifecycleError = "設定を反映できません: " + e.Message;
                if (endpointSetupScope is { } failedScope) { failedEndpointSetupScope = failedScope; endpointSetupFailure = lifecycleError; }
            }
            throw;
        }
        finally
        {
            Task? replacement;
            lock (gate) replacement = providerReplacementWork;
            // A compiled callback cannot let an unawaited replacement outlive the quiescent scope.
            if (replacement is not null) try { await replacement.ConfigureAwait(false); } catch { }
            lock (gate)
            {
                providerReplacementLifetime?.Dispose(); providerReplacementLifetime = null;
                providerChangesAllowed = false; configurationChanging = false; providerIsolation = null; endpointSetupScope = null; isolatedDeviceIds.Clear();
                configurationCompletion = null; finished.TrySetResult();
            }
        }
    }
}
