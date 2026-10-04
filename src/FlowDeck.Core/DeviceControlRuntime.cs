using System.Security.Cryptography;
using System.Text.Json;

namespace FlowDeck.Core;

/// <summary>One expiring authenticated owner, a common device registry, and per-endpoint revisioned execution.</summary>
public sealed partial class DeviceControlRuntime : IAsyncDisposable
{
    private sealed record RequestEntry(string Hash, Task<DeviceServiceReply> Completion, DeviceOperationResult Pending);
    private readonly object gate = new();
    private readonly DeviceRegistry registry = new();
    private readonly DeviceResourceScheduler scheduler;
    private readonly Dictionary<string, IDeviceControlDriver> drivers;
    private IDeviceDriverProvider[] providers;
    private readonly Dictionary<string, string> driverProviders = [];
    private Dictionary<string, PhysicalDeviceBinding>? physicalBindings;
    private readonly HashSet<string> users;
    private readonly Dictionary<EndpointReference, DeviceExecutionState> states = [];
    private readonly HashSet<EndpointReference> recoveryRequired = [];
    private readonly Dictionary<string, string> observationErrors = [];
    private readonly Dictionary<string, Task> polling = [];
    private readonly Dictionary<string, string> pollingErrors = [];
    private readonly Dictionary<string, RequestEntry> requests = [];
    private readonly HashSet<Task> commandWork = [];
    private readonly Queue<string> requestOrder = [];
    private readonly TimeProvider clock;
    private readonly IEventLog log;
    private readonly bool demo;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? sessionLifetime;
    private Task release = Task.CompletedTask, watchdog = Task.CompletedTask;
    private string? session, lifecycleError;
    private long contact, sequence, renewal;
    private bool releasing, stopping, disposed, started;
    private int pendingCount;
    public string Epoch => registry.Snapshot.Epoch;
    public DeviceControlRuntime(IEnumerable<IDeviceControlDriver> drivers, IEnumerable<string> allowedUsers, IEventLog log,
        TimeProvider? clock = null, DeviceResourceScheduler? scheduler = null, bool demo = false, IEnumerable<IDeviceDriverProvider>? providers = null,
        IEnumerable<PhysicalDeviceBinding>? physicalBindings = null, IDeviceDiagnosticSystemSource? diagnosticSystem = null)
    {
        var values = drivers.Take(DeviceRegistry.MaximumDevices + 1).ToArray();
        if (values.Length > DeviceRegistry.MaximumDevices || values.Any(d => d is null || !DeviceConfigurationRules.Key(d.DeviceId)) ||
            values.DistinctBy(d => d.DeviceId).Count() != values.Length) throw new ArgumentException("機器ドライバーの登録が不正です");
        this.drivers = values.ToDictionary(d => d.DeviceId, StringComparer.Ordinal);
        if (physicalBindings is not null)
        {
            var bindings = physicalBindings.Take(DeviceRegistry.MaximumDevices + 1).ToArray();
            if (bindings.Length > DeviceRegistry.MaximumDevices || bindings.Any(b => b is null || !DeviceConfigurationRules.Key(b.DeviceId) ||
                !DeviceConfigurationRules.Text(b.ProviderIdentity, 2048) || !DeviceConfigurationRules.Key(b.DefinitionId) || b.Fingerprint is not { Length: 64 } || !b.Fingerprint.All(Uri.IsHexDigit)) ||
                bindings.DistinctBy(b => b.DeviceId).Count() != bindings.Length) throw new ArgumentException("サービスの実機関連付けが不正です");
            this.physicalBindings = bindings.ToDictionary(b => b.DeviceId, Contract.Copy);
        }
        this.providers = (providers ?? []).Take(9).ToArray();
        if (this.providers.Length > 8 || this.providers.Any(p => p is null || !DeviceConfigurationRules.Key(p.ProviderId) ||
            p.ResourceDomains is not { Length: > 0 and <= 16 } || p.ResourceDomains.Any(d => !DeviceConfigurationRules.Text(d, 256))) ||
            this.providers.DistinctBy(p => p.ProviderId).Count() != this.providers.Length)
            throw new ArgumentException("機器取得プロバイダーの登録が不正です");
        users = allowedUsers.ToHashSet(StringComparer.Ordinal);
        if (users.Count == 0 || users.Any(u => !DeviceConfigurationRules.Text(u, 256))) throw new ArgumentException("許可ユーザーが必要です");
        this.log = log; this.clock = clock ?? TimeProvider.System; this.scheduler = scheduler ?? new(); this.demo = demo;
        this.diagnosticSystem = diagnosticSystem;
        notifications = new(this.clock);
        lock (gate) RefreshCore();
    }
    // verifiedUser is obtained from the transport's authenticated Windows token, never from a request field.
    public DeviceServiceHello OpenSession(string verifiedUser)
    {
        lock (gate)
        {
            if (!users.Contains(verifiedUser)) throw new UnauthorizedAccessException("このWindowsユーザーには操作権限がありません");
            ExpireCore();
            if (stopping || releasing) throw new IOException("サービスの解除・復旧を確認してください");
            if (session is not null) throw new IOException("別のFlowDeckがサービスを使用しています");
            session = Guid.NewGuid().ToString("N"); sessionLifetime = new(); contact = clock.GetTimestamp(); sequence = renewal = 0;
            requests.Clear(); requestOrder.Clear(); calibrationProofs.Clear(); diagnosticTransfer = null; stateTransfer = null; RefreshCore();
            return new(DeviceServiceProtocol.Version, Epoch, session, DeviceServiceProtocol.LeaseSeconds,
                ["device-catalog-v2", "endpoint-operations-v2", "endpoint-results-v2", "endpoint-telemetry-v2", "cooling-policy-v2", "restore-saved-cooling-v2", "lighting-streams-v2", "native-lighting-details-v2", "cooling-calibration-v2", DeviceLightingIdentification.Feature, DeviceDiagnostics.Feature, DeviceDiagnosticHardwareRules.Feature, DeviceStateTransfer.Feature,
                    .. configurationHandler is null ? Array.Empty<string>() : ["configuration-transfer-v2"],
                    .. configurationHandler is IDeviceDashboardConfigurationHandler ? new[] { "dashboard-settings-v2" } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceCacheRetirementHandler ? new[] { DeviceCacheRetirementTarget.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceNamingConfigurationHandler ? new[] { DeviceNameChange.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceMonitoringConfigurationHandler ? new[] { "monitoring-registration-v2" } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceSetupConfigurationHandler ? new[] { "endpoint-setup-v2" } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceConnectionLightingHandler ? new[] { DeviceConnectionLighting.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceLightingSetupHandler ? new[] { DeviceEndpointSetupScope.LightingFeature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceIndividualCoolingSetupHandler ? new[] { DeviceEndpointSetupScope.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceChunkedConfigurationSnapshotHandler ? new[] { DeviceConfigurationProtocol.SnapshotFeature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceStartupConfigurationHandler ? new[] { "cooling-startup-settings-v2" } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceCoolingGroups ? new[] { "cooling-groups-v2" } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceNotificationConfiguration ? new[] { "notifications-v2", DeviceM5NotificationReport.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceM5ConfigurationHandler ? new[] { "m5-settings-v2" } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceOpenRgbInspectionHandler { SupportsOpenRgbInspection: true } ? new[] { DeviceOpenRgbInspection.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceOpenRgbRuntimeInspectionHandler { SupportsOpenRgbRuntimeInspection: true } ? new[] { DeviceOpenRgbRuntimeInspection.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceProviderRoutingHandler { SupportsProviderRouting: true } ? new[] { DeviceProviderRouteReview.Feature } : Array.Empty<string>(),
                    .. configurationHandler is IDeviceProviderRoutingHandler { SupportsProviderRouting: true, SupportsProviderDeviceSelection: true } ? new[] { DeviceProviderRouteReview.SelectionFeature } : Array.Empty<string>(),
                    .. coolingPersistence is null ? Array.Empty<string>() : ["cooling-policy-persistence-v2"],
                    .. lightingPersistence is null ? Array.Empty<string>() : ["lighting-policy-persistence-v2"],
                    .. lightingAdjustments is null || lightingPersistence is null ? Array.Empty<string>() : ["lighting-adjustments-v2"],
                    .. lightingLibrary is null ? Array.Empty<string>() : ["lighting-library-v2", "lighting-presets-v2"],
                    .. lightingLibrary is IDeviceLightingPersonalization ? new[] { DeviceLightingLibrary.PersonalizationFeature } : Array.Empty<string>()], demo);
        }
    }
    public DeviceServiceState State()
    {
        lock (gate)
        {
            RefreshCore(); var inventory = registry.Snapshot; var sampledAt = clock.GetTimestamp();
            return new(inventory, Contract.Copy(states.Values.Select(s=>s with { CalibrationReady=CalibrationReadyCore(s),
                Lighting = LightingMirrorCore(s, sampledAt) }).ToArray()), releasing, lifecycleError, TelemetryCore(inventory), configurationChanging);
        }
    }
    private DeviceTelemetry[] TelemetryCore(DeviceInventory inventory)
    {
        var values = new List<DeviceTelemetry>();
        foreach (var instance in inventory.Devices)
        {
            DeviceSample[] samples = []; string? problem = null;
            try
            {
                if (drivers.GetValueOrDefault(instance.Device.Id) is IDeviceTelemetrySource source && !shutdownClosedDrivers.Contains((IDeviceControlDriver)source)) samples = source.ReadCachedSamples();
                if (samples is null || samples.Length > DeviceRegistry.MaximumEndpoints * 3 || samples.Any(s => s is null ||
                    !DeviceConfigurationRules.Key(s.EndpointId) || !Enum.IsDefined(s.Issue) || s.Feature is not (DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.DutyRead)) ||
                    samples.DistinctBy(s => (s.EndpointId, s.Feature)).Count() != samples.Length)
                    throw new IOException("機器の測定値が不正です");
            }
            catch (Exception) { samples = []; problem = "測定値を取得できません"; }
            foreach (var endpoint in instance.Device.Endpoints)
            foreach (var capability in endpoint.Capabilities.Where(c => c.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.DutyRead))
            {
                var sample = samples.SingleOrDefault(s => s.EndpointId == endpoint.Id && s.Feature == capability.Feature);
                double? age = sample is null ? null : clock.GetElapsedTime(sample.Timestamp).TotalSeconds;
                var issue = sample?.Issue ?? DeviceMeasurementIssue.Unavailable;
                if (issue == DeviceMeasurementIssue.None)
                    issue = sample?.Value is not { } v ? DeviceMeasurementIssue.Unavailable :
                        DeviceMeasurement.InRange(capability.Feature, v) ? DeviceMeasurementIssue.None : DeviceMeasurementIssue.OutOfRange;
                var validValue = issue == DeviceMeasurementIssue.None;
                var stale = !instance.Connected || !capability.Usable || !validValue || age is null or < 0 or >= 3 || sample?.Problem is not null;
                values.Add(new(new(instance.Device.Id, endpoint.Id), capability.Feature, validValue ? sample!.Value : null,
                    age is >= 0 && double.IsFinite(age.Value) ? age : null, stale,
                    problem ?? sample?.Problem ?? (!instance.Connected ? "機器が切断されています" :
                        issue == DeviceMeasurementIssue.OutOfRange ? "測定値が範囲外です" : !validValue ? "測定値がありません" : stale ? "測定値が古くなっています" : null), issue));
            }
        }
        return values.ToArray();
    }
    /// <summary>One queued/in-flight poll per driver. The watchdog never waits for monitoring I/O.</summary>
    public Task PollAsync()
    {
        lock (gate)
        {
            if (stopping || releasing || configurationChanging && providerIsolation is null && endpointSetupScope is null) return Task.CompletedTask;
            foreach (var driver in drivers.Values.OfType<IDevicePollingDriver>())
            {
                if (ConfigurationBlocksDevice(driver.DeviceId)) continue;
                var pollKey = "driver/" + driver.DeviceId;
                if (polling.TryGetValue(pollKey, out var prior) && !prior.IsCompleted) continue;
                var domains = registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == driver.DeviceId)?.Device.ResourceDomains ?? ["unopened:" + driver.DeviceId];
                polling[pollKey] = Task.Run(async () =>
                {
                    try
                    {
                        await scheduler.RunPreparedAsync(domains, HardwarePriority.Monitoring, driver, DevicePreparationKind.Polling, async token =>
                        { await driver.PollAsync(token).ConfigureAwait(false); return true; }, lifetime.Token).ConfigureAwait(false);
                        lock (gate) pollingErrors.Remove(driver.DeviceId);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (Exception e)
                    {
                        lock (gate) if (pollingErrors.GetValueOrDefault(driver.DeviceId) != e.Message)
                        {
                            pollingErrors[driver.DeviceId] = e.Message;
                            try { log.Write("devicePollingFailed", new { driver.DeviceId, error = e.Message }); } catch { }
                        }
                    }
                    finally { lock (gate) RefreshCore(); }
                });
            }
            foreach (var provider in providers)
            {
                if (ConfigurationBlocksProvider(provider.ProviderId)) continue;
                var key = "provider/" + provider.ProviderId;
                if (polling.TryGetValue(key, out var previous) && !previous.IsCompleted) continue;
                polling[key] = Task.Run(async () =>
                {
                    try
                    {
                        await scheduler.RunPreparedAsync(provider.ResourceDomains, HardwarePriority.Monitoring, provider, DevicePreparationKind.Polling, async token =>
                        { await provider.PollAsync(token).ConfigureAwait(false); return true; }, lifetime.Token).ConfigureAwait(false);
                        lock (gate) pollingErrors.Remove(key);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (Exception e)
                    {
                        lock (gate) if (pollingErrors.GetValueOrDefault(key) != e.Message)
                        { pollingErrors[key] = e.Message; try { log.Write("deviceProviderFailed", new { provider.ProviderId, error = e.Message }); } catch { } }
                    }
                    finally { lock (gate) RefreshCore(); }
                });
            }
            return Task.WhenAll(polling.Values);
        }
    }
    public void Refresh() { lock (gate) RefreshCore(); }
    private void RefreshCore()
    {
        foreach (var provider in providers)
        {
            if (shutdownClosedProviders.Contains(provider)) continue;
            var key = "provider/" + provider.ProviderId;
            registryCapacityBlockedProviders.Remove(provider.ProviderId);
            try
            {
                var additions = provider.CachedDrivers;
                if (additions is null || additions.Length > DeviceRegistry.MaximumDevices ||
                    additions.Any(d => d is null || !DeviceConfigurationRules.Key(d.DeviceId)) || additions.DistinctBy(d => d.DeviceId).Count() != additions.Length ||
                    additions.Any(d => drivers.TryGetValue(d.DeviceId, out var old) && !ReferenceEquals(old, d)))
                    throw new IOException("取得プロバイダーの機器登録が重複または上限を超えています");
                if (additions.Select(d => d.DeviceId).Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                {
                    registryCapacityBlockedProviders.Add(provider.ProviderId);
                    throw new IOException("取得プロバイダーの機器登録が上限を超えています");
                }
                if (additions.Any(d => !drivers.ContainsKey(d.DeviceId))) EnsureRegistryCapacityCore(additions.Select(d => d.DeviceId));
                foreach (var driver in additions)
                {
                    if (drivers.TryAdd(driver.DeviceId, driver)) driverProviders.Add(driver.DeviceId, provider.ProviderId);
                    closedRegistryDevices.Remove(driver.DeviceId);
                }
                observationErrors.Remove(key);
            }
            catch (Exception e)
            {
                if (observationErrors.GetValueOrDefault(key) != e.Message)
                { observationErrors[key] = e.Message; try { log.Write("deviceProviderRegistrationFailed", new { provider.ProviderId, error = e.Message }); } catch { } }
            }
        }
        var observed = new List<DeviceObservation>();
        foreach (var driver in drivers.Values)
        {
            if (shutdownClosedDrivers.Contains(driver)) continue;
            try
            {
                var device = driver.Observe();
                if (device is not null)
                {
                    device = DeviceSnapshotCopy.Observation(device);
                    if (device.Id != driver.DeviceId || !DeviceRegistry.Valid(device)) throw new InvalidOperationException("ドライバーの機器情報が不正です");
                    observed.Add(device);
                }
                observationErrors.Remove(driver.DeviceId);
            }
            catch (Exception e)
            {
                if (observationErrors.GetValueOrDefault(driver.DeviceId) != e.Message)
                {
                    observationErrors[driver.DeviceId] = e.Message;
                    try { log.Write("deviceObservationFailed", new { driver.DeviceId, error = e.Message }); } catch { }
                }
            }
        }
        var inventory = registry.Reconcile(observed);
        foreach (var device in inventory.Devices)
        foreach (var endpoint in device.Device.Endpoints)
        {
            var target = new EndpointReference(device.Device.Id, endpoint.Id);
            if (states.TryGetValue(target, out var previous) && previous.Generation == device.Generation) continue;
            if (previous?.Owned == true) recoveryRequired.Add(target);
            states[target] = new(target, device.Generation, (previous?.PolicyRevision ?? -1) + 1, Outcome.Unknown,
                Message: device.Connected ? "現在の設定は未取得です" : "対象機器が切断されています", Owned: previous?.Owned ?? false);
        }
        // A removed endpoint's obligation outlives its presence in the catalog. Missing restoration results must remain failures.
        var known = inventory.Devices.SelectMany(d => d.Device.Endpoints.Select(e => new EndpointReference(d.Device.Id, e.Id))).ToHashSet();
        foreach (var target in states.Keys.Where(t => !known.Contains(t)).ToArray())
        {
            if (states[target].Owned)
            {
                recoveryRequired.Add(target);
                states[target] = states[target] with { Outcome = Outcome.Unknown, Message = "削除された端子の制御終了が未確認です" };
            }
            else { states.Remove(target); recoveryRequired.Remove(target); }
        }
    }
    public Task<DeviceServiceReply> ExecuteAsync(DeviceServiceRequest incoming)
    {
        var request = DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(incoming, Contract.Json));
        lock (gate)
        {
            ExpireCore();
            if (stopping || releasing || session is null || request.Epoch != Epoch || request.Session != session)
                return Task.FromResult(Reject(request, "接続セッションが無効です"));
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json)));
            if (requests.TryGetValue(request.Id, out var previous))
                return previous.Hash == hash ? CopyReplyAsync(previous.Completion) : Task.FromResult(Reject(request, "同じ要求IDの内容が異なります"));
            if (request.Operation == DeviceOperation.Renew)
            {
                if (request.Sequence <= renewal) return Task.FromResult(Reject(request, "古い生存確認番号です"));
                renewal = request.Sequence; contact = clock.GetTimestamp(); var reply = Reply(request);
                requests[request.Id] = new(hash, Task.FromResult(reply), new(request.Id, request.Source, Outcome.Confirmed, clock.GetUtcNow(), [], "生存確認"));
                requestOrder.Enqueue(request.Id); TrimRequests(); return Task.FromResult(Contract.Copy(reply));
            }
            if (request.Sequence <= sequence) return Task.FromResult(Reject(request, "古い要求番号です"));
            sequence = request.Sequence; contact = clock.GetTimestamp();
            if (pendingCount >= 64) return Task.FromResult(Reject(request, "待機中の要求が多すぎます"));
            // Snapshot chunks are immutable bytes. Do not re-enumerate every device for every chunk.
            if (request.Operation != DeviceOperation.ReadStateSnapshot &&
                !(request.Operation == DeviceOperation.Configuration && request.Configuration?.Operation == DeviceConfigurationOperation.ReadSnapshot)) RefreshCore();
            var completion = new TaskCompletionSource<DeviceServiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new DeviceOperationResult(request.Id, request.Source, Outcome.Accepted, clock.GetUtcNow(), [], "受付しました");
            var tracked = TrackNotificationCompletionAsync(request, completion.Task, clock.GetTimestamp());
            requests[request.Id] = new(hash, tracked, pending); requestOrder.Enqueue(request.Id); TrimRequests();
            if(request.Operation==DeviceOperation.CalibrateCooling) QueueCalibrationCore(request,completion);
            else if(request.Operation==DeviceOperation.CancelCoolingCalibration) completion.SetResult(CancelCalibrationCore(request));
            else if(request.Operation==DeviceOperation.IdentifyLighting) QueueIdentificationCore(request,completion);
            else if(request.Operation==DeviceOperation.CancelLightingIdentification) completion.SetResult(CancelIdentificationCore(request));
            else if (DeviceServiceProtocol.IsWrite(request.Operation))
            {
                string? error; LightingPresetPlan? lightingPlan = null; LightingAdjustmentPlan? adjustmentPlan = null; CoolingGroupPlan? coolingGroup = null;
                try
                {
                    if (IdentificationConflict(request)) error = "この機器の識別を中止し、復元が終わるまでお待ちください";
                    else if (LightingTransitionBusy(request)) error = "照明構成の変更が完了するまでお待ちください";
                    else if (request.Operation == DeviceOperation.ApplyLightingPreset)
                    { lightingPlan = ResolveLightingPlan(request); error = CheckLightingPlan(request, lightingPlan); }
                    else if (request.Operation == DeviceOperation.AdjustLighting)
                    { adjustmentPlan = ResolveLightingAdjustment(request); error = CheckLightingAdjustment(request, adjustmentPlan); }
                    else
                    {
                        if(request.CoolingGroup is { } selection)
                        {
                            coolingGroup=(configurationHandler as IDeviceCoolingGroups)?.ResolveCoolingGroup(selection)??throw new IOException("冷却グループを利用できません");
                            if(!DeviceCoolingGroups.Valid(coolingGroup.Group)||!coolingGroup.Group.Targets.ToHashSet().SetEquals(request.Targets!.Select(t=>t.Target)))
                                throw new IOException("表示した対象と保存した冷却グループが一致しません");
                        }
                        error = CheckBatch(request);
                    }
                }
                catch (Exception e) { error = "操作条件を確認できません: " + e.Message; }
                if (error is not null) completion.SetResult(Reject(request, error));
                else
                {
                    var previousLighting = request.Targets!.ToDictionary(t => t.Target, t => states[t.Target].RequestedLighting);
                    var versions = request.Targets!.Select(t =>
                    {
                        var value = states[t.Target] with { PolicyRevision = checked(states[t.Target].PolicyRevision + 1), Outcome = Outcome.Pending,
                            RequestedDuty = request.Duty, RequestedLighting = adjustmentPlan?.Parts.Single(p => p.Targets.Contains(t.Target)).Program ??
                                lightingPlan?.Parts.Single(p => p.Targets.Contains(t.Target)).Program ?? request.Lighting, Message = "処理中", Persistence = null, Identification = null };
                        states[t.Target] = value; return t with { ExpectedPolicyRevision = value.PolicyRevision };
                    }).ToArray();
                    requests[request.Id] = requests[request.Id] with { Pending = pending with { Outcome = Outcome.Pending, Message = "処理中",
                        Targets = versions.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Pending, "処理中", request.Duty)).ToArray() } };
                    pendingCount++;
                    var token = sessionLifetime!.Token;
                    var lightingOrder = request.Operation is DeviceOperation.SetLighting or DeviceOperation.ApplyLightingPreset or DeviceOperation.AdjustLighting ? checked(++lightingSaveOrder) : 0;
                    if (lightingOrder > 0) foreach (var target in versions) lightingSelectionsPending[target.Target] = request.Id;
                    var intensityUpdate = request.Operation == DeviceOperation.StopLighting ? QueueSelectedLightingStopCore(request, versions, token) :
                        adjustmentPlan is not null ? QueueLightingAdjustmentCore(request, versions, adjustmentPlan, token) :
                        lightingPlan is not null ? QueueLightingPresetUpdate(request, versions, lightingPlan, previousLighting) : QueueLightingIntensityUpdateCore(request, versions);
                    if (intensityUpdate is null && request.Operation == DeviceOperation.SetLighting)
                        intensityUpdate = QueueLightingJoin(request, versions, versions.ToDictionary(t => t.Target, _ => request.Lighting!), previousLighting, token);
                    var work = Task.Run(() => ExecuteBatchAsync(request, versions, token, completion, lightingPlan, lightingOrder, intensityUpdate, adjustmentPlan, coolingGroup)); commandWork.Add(work);
                    _ = work.ContinueWith(t => { lock (gate) commandWork.Remove(t); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            else if (request.Operation == DeviceOperation.LightingLibrary && lightingLibrary is not null)
            {
                pendingCount++; var token = sessionLifetime!.Token;
                var work = Task.Run(() => ExecuteLightingLibraryAsync(request, completion, token)); lightingLibraryWork.Add(work);
                _ = work.ContinueWith(t => { lock (gate) lightingLibraryWork.Remove(t); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else if (request.Operation == DeviceOperation.Configuration && configurationHandler is not null)
            {
                pendingCount++; var token = sessionLifetime!.Token;
                var work = Task.Run(() => ExecuteConfigurationAsync(request, completion, token)); configurationWork.Add(work);
                _ = work.ContinueWith(t => { lock (gate) configurationWork.Remove(t); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else if (request.Operation == DeviceOperation.ReadDiagnostics) QueueDiagnosticsCore(request, completion);
            else if (request.Operation == DeviceOperation.ReadStateSnapshot) QueueStateSnapshotCore(request, completion);
            else if (request.Operation == DeviceOperation.State) completion.SetResult(LegacyStateReply(request));
            else if (request.Operation is DeviceOperation.ReadNotifications or DeviceOperation.TestNotification or DeviceOperation.ReportM5Connection)
                completion.SetResult(ExecuteNotificationCore(request));
            else if (request.Operation == DeviceOperation.Result)
            {
                if (!requests.TryGetValue(request.ResultId!, out var value) || request.ResultId == request.Id)
                    completion.SetResult(Reject(request, "この接続の要求結果が見つかりません"));
                else completion.SetResult(Reply(request, result: value.Completion.IsCompletedSuccessfully ? value.Completion.Result.Result : value.Pending));
            }
            else if (request.Operation == DeviceOperation.ReleaseSession)
            {
                var releaseTask = BeginReleaseCore(DeviceReleaseReason.NormalExit);
                _ = CompleteReleaseAsync(request, releaseTask, completion);
            }
            else completion.SetResult(Reject(request, "未対応の操作です"));
            return CopyReplyAsync(tracked);
        }
    }
    private void TrimRequests()
    {
        // Never evict in-flight operations. A monotonic sequence prevents replay after completed entries expire from this bounded cache.
        var attempts = requestOrder.Count;
        while (requests.Count > 256 && attempts-- > 0)
        {
            var id = requestOrder.Dequeue();
            if (requests[id].Completion.IsCompleted) requests.Remove(id); else requestOrder.Enqueue(id);
        }
    }
    private string? CheckBatch(DeviceServiceRequest request, bool checkLightingStream = true,
        IReadOnlyDictionary<EndpointReference, LightingProgram>? staticPrograms = null)
    {
        if (configurationChanging) return "設定の反映中です";
        if (lifecycleError is not null && request.Operation is not (DeviceOperation.ReleaseCooling or DeviceOperation.StopLighting)) return lifecycleError;
        if (request.TopologyRevision != registry.Snapshot.TopologyRevision) return "機器構成が変更されました。対象を再取得してください";
        if (request.Operation == DeviceOperation.StopLighting && CheckSelectedLightingStop(request) is { } stopError) return stopError;
        if (checkLightingStream && request.Operation == DeviceOperation.SetLighting)
        {
            var programs = request.Targets!.ToDictionary(t => t.Target, _ => request.Lighting!);
            if (LightingUpdateGroup(request, request.Targets!, programs) is { } active) return CheckLightingUpdate(request, active, programs);
            var sources = LightingJoinSources(request.Targets!, programs);
            if (sources.Length > 0) return CheckLightingJoin(request, programs, sources);
        }
        foreach (var target in request.Targets!)
        {
            if (!states.TryGetValue(target.Target, out var state) || state.PolicyRevision != target.ExpectedPolicyRevision) return "操作対象の設定が変更されました";
            var item = staticPrograms is null ? request : request with { Lighting = staticPrograms[target.Target] };
            if (CheckTarget(item, target, batchLighting: BatchedLighting(item, target)) is { } error) return error;
        }
        if (CheckStaticLightingBatches(request, staticPrograms) is { } batchError) return batchError;
        return checkLightingStream ? CheckLightingBatch(request) : null;
    }
    private string? CheckTarget(DeviceServiceRequest request, DeviceTargetVersion target, bool identification = false, bool batchLighting = false)
    {
        if (!identification && IdentificationConflict(request)) return "この機器の照明を識別中です";
        if(calibrationTrials.ContainsKey(target.Target)) return "この端子の試験が終了するまでお待ちください";
        if (request.Operation == DeviceOperation.SetLighting && lightingRecoveryRequired.Any(t => t.DeviceId == target.Target.DeviceId))
            return "この機器の照明の復元を確認してから操作してください";
        if (request.Operation is not (DeviceOperation.ReleaseCooling or DeviceOperation.StopLighting) && PhysicalBindingError(target.Target.DeviceId) is { } physicalError)
            return physicalError;
        if (request.Operation is not (DeviceOperation.ReleaseCooling or DeviceOperation.StopLighting) &&
            recoveryRequired.Any(t => t.DeviceId == target.Target.DeviceId)) return "接続・構成変更前の制御を終了してから操作してください";
        var error = registry.Check(new(Epoch, target.Target.DeviceId, target.Target.EndpointId, target.Generation, DeviceServiceProtocol.Feature(request)));
        if (error is not null) return error;
        if (request.Operation == DeviceOperation.SetLighting && DeviceServiceProtocol.Feature(request) == DeviceFeature.NativeEffect)
        {
            var endpoint = registry.Snapshot.Devices.Single(d => d.Device.Id == target.Target.DeviceId).Device.Endpoints.Single(e => e.Id == target.Target.EndpointId);
            if (endpoint.NativeLightingEffects is { } effects && !effects.Any(e => DeviceNativeLighting.Accepts(e, request.Lighting)))
                return "機器固有の照明効果・色数・速度・方向・明るさが能力と一致しません";
        }
        if (request.Operation == DeviceOperation.SetLighting && DeviceLighting.Animated(request.Lighting))
            return drivers[target.Target.DeviceId] is IDeviceLightingDriver ? null : "この機器の連続フレーム送信は未対応です";
        if (drivers[target.Target.DeviceId] is IDeviceCoolingDriver cooling && IsCoolingPolicy(request.Operation))
            return CheckCoolingRequest(request, target, cooling);
        if (request.Profile is { } profile)
            foreach (var curve in profile.Curves)
            {
                var sensor = registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == curve.Sensor.DeviceId);
                if (sensor is null || registry.Check(new(Epoch, curve.Sensor.DeviceId, curve.Sensor.EndpointId, sensor.Generation, DeviceFeature.Temperature)) is not null)
                    return "必須の温度センサーを利用できません";
            }
        return batchLighting ? null : drivers[target.Target.DeviceId].Validate(Action(request, target));
    }
    private static DeviceEndpointAction Action(DeviceServiceRequest r, DeviceTargetVersion t) =>
        new(t.Target.EndpointId, r.Operation, r.Duty, r.Profile, r.Lighting);
    private async Task ExecuteBatchAsync(DeviceServiceRequest request, DeviceTargetVersion[] targets, CancellationToken ct, TaskCompletionSource<DeviceServiceReply> completion,
        LightingPresetPlan? lightingPlan = null, long lightingOrder = 0, Task<DeviceTargetResult[]>? intensityUpdate = null, LightingAdjustmentPlan? adjustmentPlan = null, CoolingGroupPlan? coolingGroup = null)
    {
        DeviceServiceReply reply;
        try
        {
            var stoppedLights = lightingPlan is null && intensityUpdate is null ? await StopOverlappingLightingAsync(request, targets).ConfigureAwait(false) : [];
            DeviceTargetResult[] results;
            if (intensityUpdate is not null) results = await intensityUpdate.ConfigureAwait(false);
            else if (request.Operation == DeviceOperation.SetLighting && stoppedLights.Values.Any(r => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified)))
                results = targets.Select(t => new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, Outcome.Rejected, "以前の同期の停止を確認できません")).ToArray();
            else if (request.Operation == DeviceOperation.SetLighting && DeviceLighting.Animated(request.Lighting))
                results = await StartLightingGroupAsync(request, targets, ct).ConfigureAwait(false);
            else if (request.Operation == DeviceOperation.SetLighting)
                results = await ExecuteStaticLightingAsync(request, targets, targets.ToDictionary(t => t.Target, _ => request.Lighting!), ct).ConfigureAwait(false);
            else results = await Task.WhenAll(targets.Select(t => request.Operation == DeviceOperation.StopLighting && stoppedLights.TryGetValue(t.Target, out var stopped)
                ? Task.FromResult(new DeviceTargetResult(t.Target, t.Generation, t.ExpectedPolicyRevision, stopped.Outcome, stopped.Message, ReportedLighting: stopped.ReportedLighting))
                : ExecuteTargetAsync(request, t, ct, coolingGroup is null?null:()=>((IDeviceCoolingGroups)configurationHandler!).CheckCoolingGroup(coolingGroup)))).ConfigureAwait(false);
            results = await PersistLightingSelectionAsync(request, targets, lightingPlan, results, lightingOrder, adjustmentPlan).ConfigureAwait(false);
            var outcome = Aggregate(results.Select(r => r.Outcome));
            if (outcome is Outcome.Confirmed or Outcome.WrittenUnverified && results.Any(r => r.Persistence?.State == DevicePolicySaveState.Failed)) outcome = Outcome.Partial;
            var result = new DeviceOperationResult(request.Id, request.Source, outcome, clock.GetUtcNow(), results,
                outcome == Outcome.Partial ? "一部の対象に失敗しました" : outcome == Outcome.Confirmed ? "設定を確認しました" :
                outcome == Outcome.WrittenUnverified ? "送信しました。読み戻しはできません" : "対象ごとの結果を確認してください");
            log.Write("deviceOperation", result);
            reply = Reply(request, result: result);
        }
        catch (Exception e) { reply = Reject(request, e.Message); }
        finally
        {
            lock (gate)
            {
                foreach (var target in targets) if (lightingSelectionsPending.GetValueOrDefault(target.Target) == request.Id) lightingSelectionsPending.Remove(target.Target);
                pendingCount--; TrimRequests();
            }
        }
        completion.TrySetResult(reply);
    }
    private async Task<DeviceTargetResult> ExecuteTargetAsync(DeviceServiceRequest request, DeviceTargetVersion target, CancellationToken ct, Func<string?>? precondition = null)
    {
        DeviceDriverResult result; var issued = false; int? requestedDuty = request.Duty;
        string? saveTicket = null; DevicePolicySaveResult? saved = null;
        try
        {
            CoolingPolicyChange? change;
            lock (gate) change = PrepareCoolingChangeCore(request, target);
            if (change is not null)
            {
                saveTicket = await coolingPersistence!.BeginCoolingChangeAsync(change, ct).ConfigureAwait(false);
                // Resume was durably disabled before I/O. Only this exact in-flight request may use its approved saved snapshot.
                if(request.Operation==DeviceOperation.RestoreSavedCooling)
                    lock(gate) coolingRestorePermits[(request.Id,target.Target)]=new(request.Session,target,CoolingPolicyKey(change.Policy!));
            }
            string[] domains; IDeviceControlDriver preparedDriver;
            lock (gate)
            {
                domains = registry.Snapshot.Devices.Single(d => d.Device.Id == target.Target.DeviceId).Device.ResourceDomains;
                preparedDriver = drivers[target.Target.DeviceId];
            }
            result = await scheduler.RunPreparedAsync(domains, request.Operation is DeviceOperation.ReleaseCooling or DeviceOperation.StopLighting ? HardwarePriority.Recovery : HardwarePriority.Manual,
                preparedDriver, DevicePreparationKind.Command,
                async token =>
                {
                    lock (gate)
                    {
                        RefreshCore();
                        if (token.IsCancellationRequested || request.Session != session || stopping || releasing || precondition?.Invoke() is not null ||
                            request.TopologyRevision != registry.Snapshot.TopologyRevision || !states.TryGetValue(target.Target, out var current) ||
                            current.PolicyRevision != target.ExpectedPolicyRevision || CheckTarget(request, target) is not null)
                            return new DeviceDriverResult(Outcome.Rejected, "実行前に対象・所有・設定が変更されたため未実行です");
                        if (request.Operation is DeviceOperation.SetDuty or DeviceOperation.SetAutomatic or DeviceOperation.SetLighting or DeviceOperation.ResumeCooling or DeviceOperation.RestoreSavedCooling)
                            states[target.Target] = current with { Owned = true };
                        if (request.Operation == DeviceOperation.RestoreSavedCooling && drivers[target.Target.DeviceId] is IDeviceCoolingDriver configured)
                            requestedDuty = CoolingConfiguration(configured, target.Target.EndpointId)?.SavedPolicy?.Duty;
                        issued = true;
                    }
                    // Once hardware begins, an exception/cancellation cannot prove that no bytes reached the device.
                    try
                    {
                        if (drivers[target.Target.DeviceId] is IDeviceCoolingDriver cooling && IsCoolingPolicy(request.Operation))
                            return await ApplyCoolingRequestAsync(request, target, cooling, token, precondition).ConfigureAwait(false);
                        return await drivers[target.Target.DeviceId].ApplyAsync(Action(request, target), token).ConfigureAwait(false);
                    }
                    catch (Exception e) { return new DeviceDriverResult(Outcome.Unknown, e.Message); }
                }, ct, authorize: () =>
                {
                    lock (gate)
                    {
                        RefreshCore();
                        if (ct.IsCancellationRequested || request.Session != session || stopping || releasing || configurationChanging || precondition?.Invoke() is not null ||
                            request.TopologyRevision != registry.Snapshot.TopologyRevision || !states.TryGetValue(target.Target, out var current) ||
                            current.Generation != target.Generation || current.PolicyRevision != target.ExpectedPolicyRevision)
                            throw new IOException("通信前に対象・所有・設定が変更されました");
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new(Outcome.Rejected, "操作権が終了したため未実行です"); }
        catch (Exception e) { result = new(issued ? Outcome.Failed : Outcome.Rejected, e.Message); }
        finally { lock(gate) coolingRestorePermits.Remove((request.Id,target.Target)); }
        if (!ValidDriverResult(result))
            result = new(Outcome.Unknown, "ドライバーから有効な確認結果を取得できませんでした");
        lock (gate)
        {
            RefreshCore();
            if (request.Session != session || !states.TryGetValue(target.Target, out var state) || state.Generation != target.Generation || state.PolicyRevision != target.ExpectedPolicyRevision)
                result = issued ? new(Outcome.Unknown, "結果が届く前に対象・所有・設定が変更されました") : result;
            else
            {
                states[target.Target] = state with { Outcome = result.Outcome, ReportedDuty = result.ReportedDuty,
                ReportedLighting = result.ReportedLighting, Message = result.Message,
                // A successful replacement supersedes the stopped stream's fault and mirror.
                Lighting = request.Operation == DeviceOperation.SetLighting && result.Outcome is (Outcome.Confirmed or Outcome.WrittenUnverified) &&
                    !lightingOwners.ContainsKey(target.Target) ? null : state.Lighting,
                Owned = request.Operation is DeviceOperation.ReleaseCooling or DeviceOperation.StopLighting ?
                    result.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified) && state.Owned :
                    result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified or Outcome.Unknown || state.Owned };
                if (!states[target.Target].Owned) { recoveryRequired.Remove(target.Target); lightingRecoveryRequired.Remove(target.Target); }
                CoolingRequestFinishedCore(request, target, result);
            }
        }
        if (saveTicket is not null)
        {
            try { saved = await coolingPersistence!.FinishCoolingChangeAsync(saveTicket, result.Outcome == Outcome.Confirmed).ConfigureAwait(false); }
            catch (Exception e) { saved = new(DevicePolicySaveState.Failed, "冷却設定を保存できませんでした: " + e.Message); }
            lock (gate)
                if (request.Session == session && states.TryGetValue(target.Target, out var current) && current.Generation == target.Generation && current.PolicyRevision == target.ExpectedPolicyRevision)
                    states[target.Target] = current with { Persistence = saved };
        }
        return new(target.Target, target.Generation, target.ExpectedPolicyRevision, result.Outcome, result.Message, requestedDuty, result.ReportedDuty, result.ReportedLighting, saved);
    }
    public static Outcome Aggregate(IEnumerable<Outcome> outcomes)
    {
        var values = outcomes.ToArray(); if (values.Length == 0) return Outcome.Confirmed;
        var success = values.Count(v => v is Outcome.Confirmed or Outcome.WrittenUnverified);
        return success == values.Length ? values.Contains(Outcome.WrittenUnverified) ? Outcome.WrittenUnverified : Outcome.Confirmed :
            success > 0 ? Outcome.Partial : values.Contains(Outcome.Unknown) ? Outcome.Unknown : values.All(v => v == Outcome.Rejected) ? Outcome.Rejected : Outcome.Failed;
    }
    private static bool ValidDriverResult(DeviceDriverResult? result) => result is not null &&
        Enum.IsDefined(result.Failure) && (result.Failure == DeviceDriverFailure.None || result.Outcome == Outcome.Rejected) &&
        result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified or Outcome.Failed or Outcome.Unknown or Outcome.Rejected &&
        result.Message is { Length: <= 16384 } && result.ReportedDuty is null or >= 0 and <= 100 &&
        (result.ReportedLighting is null || DeviceConfigurationRules.ValidProgram(result.ReportedLighting));
    private DeviceServiceReply Reply(DeviceServiceRequest request, DeviceOperationResult? result = null, DeviceServiceState? state = null) =>
        new(DeviceServiceProtocol.Version, Epoch, request.Session, request.Id, true, Result: result, State: state);
    private DeviceServiceReply Reject(DeviceServiceRequest request, string error) => new(DeviceServiceProtocol.Version, Epoch, request.Session, request.Id, false, error,
        new(request.Id, request.Source, Outcome.Rejected, clock.GetUtcNow(), [], error));
    private static async Task<DeviceServiceReply> CopyReplyAsync(Task<DeviceServiceReply> task) => Contract.Copy(await task.ConfigureAwait(false));
    private void ExpireCore()
    { if (session is not null && clock.GetElapsedTime(contact).TotalSeconds is < 0 or >= DeviceServiceProtocol.LeaseSeconds) BeginReleaseCore(DeviceReleaseReason.ClientExpired); }
    private Task BeginReleaseCore(DeviceReleaseReason reason)
    {
        if (releasing) return release;
        RevokeConnectionLightingCore(); session = null; releasing = true; var previous = sessionLifetime; sessionLifetime = null;
        Exception? cancellationError = null;
        try { previous?.Cancel(); } catch (Exception e) { cancellationError = e; }
        notifications.ClearM5(); diagnosticTransfer = null; stateTransfer = null;
        var configurationWait = Task.WhenAll(lightingLibraryWork.Append(configurationCompletion?.Task ?? Task.CompletedTask).Append(diagnosticWork).Append(stateSnapshotWork));
        var commands = commandWork.ToArray();
        var trials=calibrationTrials.Values.Select(t=>t.Completion.Task).Concat(lightingIdentifications.Values.Select(t=>t.Completion.Task)).ToArray();
        release = Task.Run(async () =>
        {
            var failures = new List<Exception>();
            if (cancellationError is not null) failures.Add(cancellationError);
            try
            {
                await CaptureShutdownTaskAsync(() => configurationWait, failures).ConfigureAwait(false);
                await CaptureShutdownTaskAsync(() => Task.WhenAll(trials), failures).ConfigureAwait(false);
                await CaptureShutdownTaskAsync(() => ReleaseDriversAsync(reason), failures).ConfigureAwait(false);
                // Release hardware first, but don't give a new owner access while an older policy commit is unfinished.
                await CaptureShutdownTaskAsync(() => Task.WhenAll(commands), failures).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (failures.Count > 0)
                    {
                        lifecycleError = JoinLifecycleErrors(lifecycleError, string.Join(" / ", failures.Select(e => e.Message)));
                        if (stopping) shutdownNonRecoveryErrors.UnionWith(failures.Select(e => e.Message));
                    }
                    releasing = false;
                }
                previous?.Dispose();
            }
            if (failures.Count > 0)
            {
                throw new AggregateException("制御終了の待機処理に失敗しました", failures);
            }
        });
        return release;
    }
    private async Task ReleaseDriversAsync(DeviceReleaseReason reason, IDeviceControlDriver[]? selected = null)
    {
        await StopAllLightingAsync(reason, selected).ConfigureAwait(false);
        IDeviceControlDriver[] releasingDrivers;
        lock (gate) { RefreshCore(); releasingDrivers = selected ?? drivers.Values.ToArray(); }
            var errors = new List<string>();
            try
            {
                await Task.WhenAll(releasingDrivers.Select(async driver =>
                {
                    var diagnosticStart = clock.GetTimestamp(); DeviceDriverReleaseResult[]? diagnosticResults = null; var diagnosticFailed = false;
                    Dictionary<string, long> expected = []; ShutdownReleaseFailure? shutdownFailure = null;
                    HashSet<string> releaseTargets = [];
                    var resultsHandled = false; string? nonRecoveryError = null;
                    try
                    {
                        string[] domains;
                        lock (gate)
                        {
                            domains = registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == driver.DeviceId)?.Device.ResourceDomains ?? ["unopened:" + driver.DeviceId];
                            expected = states.Values.Where(s => s.Target.DeviceId == driver.DeviceId).ToDictionary(s => s.Target.EndpointId, s => s.Generation);
                            releaseTargets = (registry.Snapshot.Devices.SingleOrDefault(d => d.Device.Id == driver.DeviceId)?.Device.Endpoints ?? [])
                                .Where(e => e.Kind is EndpointKind.Cooling or EndpointKind.Lighting).Select(e => e.Id)
                                .Concat(states.Values.Where(s => s.Target.DeviceId == driver.DeviceId && s.Owned).Select(s => s.Target.EndpointId)).ToHashSet();
                        }
                        var results = await scheduler.RunPreparedAsync(domains, HardwarePriority.Recovery, driver, DevicePreparationKind.Release, token => driver.ReleaseAsync(reason, token)).ConfigureAwait(false);
                        lock (gate)
                        {
                            RefreshCore();
                            if (results is null || results.Length > DeviceRegistry.MaximumEndpoints || results.Any(r => r is null || !ValidDriverResult(r.Result)) ||
                                results.DistinctBy(r => r.EndpointId).Count() != results.Length) throw new IOException("解除結果の形式が不正です");
                            foreach (var r in results)
                            {
                                var target = new EndpointReference(driver.DeviceId, r.EndpointId);
                                if (!expected.TryGetValue(r.EndpointId, out var generation)) throw new IOException("解除結果の対象が不正です");
                                var ok = r.Result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified;
                                // While releasing there is no new owner. A successful old obligation can end without claiming a value for a newer connection.
                                if (!states.TryGetValue(target, out var state)) continue;
                                if (generation != state.Generation)
                                {
                                    states[target] = state with { PolicyRevision = state.PolicyRevision + 1, Outcome = Outcome.Unknown,
                                        ReportedDuty = null, ReportedLighting = null, Owned = !ok && state.Owned,
                                        Message = ok ? "以前の接続の制御を終了しました。現在値は再取得します" : "以前の接続の制御終了が未確認です" };
                                    if (ok) { recoveryRequired.Remove(target); coolingPolicies.Remove(target); }
                                    continue;
                                }
                                states[target] = state with { PolicyRevision = state.PolicyRevision + 1, Outcome = ok ? r.Result.Outcome : Outcome.Unknown,
                                    RequestedDuty = null, RequestedLighting = null, ReportedDuty = r.Result.ReportedDuty, ReportedLighting = r.Result.ReportedLighting,
                                    Message = r.Result.Message, Owned = !ok && state.Owned };
                                if (ok) { recoveryRequired.Remove(target); lightingRecoveryRequired.Remove(target); coolingPolicies.Remove(target); states[target] = states[target] with { Cooling = null }; }
                            }
                            var unfinished = states.Values.Where(s => s.Target.DeviceId == driver.DeviceId && s.Owned).Select(s => s.Target.EndpointId)
                                .Concat(results.Where(r => r.Result.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified)).Select(r => r.EndpointId)).ToHashSet();
                            if (unfinished.Count > 0)
                            {
                                var details = unfinished.Order(StringComparer.Ordinal).Select(endpoint => endpoint + ": " +
                                    (results.SingleOrDefault(r => r.EndpointId == endpoint)?.Result.Message ?? "解除結果が返されませんでした"));
                                var message = driver.DeviceId + ": 解除を確認できません\n" + string.Join("\n", details);
                                errors.Add(message); diagnosticFailed = true; shutdownFailure = new(message, unfinished);
                            }
                            diagnosticResults = results;
                            resultsHandled = true;
                        }
                        log.Write("deviceRelease", new { driver.DeviceId, reason, results });
                    }
                    catch (Exception e)
                    {
                        diagnosticFailed = true; var message = driver.DeviceId + ": " + e.Message;
                        lock (gate) errors.Add(message);
                        if (resultsHandled) nonRecoveryError = message;
                        else shutdownFailure = new(message, releaseTargets);
                    }
                    finally
                    {
                        lock (gate)
                        {
                            if (stopping)
                            {
                                shutdownReleaseAttempted.Add(driver);
                                if (shutdownFailure is not null) shutdownReleaseFailures[driver] = shutdownFailure;
                                else shutdownReleaseFailures.Remove(driver);
                                if (nonRecoveryError is not null) shutdownNonRecoveryErrors.Add(nonRecoveryError);
                            }
                            RecordDiagnosticRelease(driver, reason, diagnosticStart, diagnosticResults, diagnosticFailed);
                        }
                    }
                })).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (providerTransitionError is not null) errors.Add(providerTransitionError);
                    lifecycleError = errors.Count == 0 ? null : string.Join(" / ", errors);
                    // A release reply must remain within the protocol's error limit even with many detailed failures.
                    if (lifecycleError is { Length: > 16384 })
                        lifecycleError = lifecycleError[..(char.IsHighSurrogate(lifecycleError[16382]) ? 16382 : 16383)] + "…";
                }
            }
    }
    private async Task CompleteReleaseAsync(DeviceServiceRequest r, Task task, TaskCompletionSource<DeviceServiceReply> completion)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception e) { lock (gate) { completion.TrySetResult(Reject(r, lifecycleError ?? e.Message)); } return; }
        lock (gate) completion.TrySetResult(lifecycleError is null ? Reply(r) : Reject(r, lifecycleError));
    }
    public Task DisconnectAsync(string owner)
    { lock (gate) return owner == session ? BeginReleaseCore(DeviceReleaseReason.ClientDisconnected) : Task.CompletedTask; }
    /// <summary>Run persisted release obligations before accepting clients. Does not start a new saved policy.</summary>
    public Task RecoverStartupAsync(bool requireSuccessfulDiscovery = false)
    {
        lock (gate)
        {
            if (started || stopping || session is not null || pendingCount != 0 || polling.Values.Any(p => !p.IsCompleted))
                throw new InvalidOperationException("起動時の取得完了後・接続受付前に復旧してください");
            RefreshCore();
            if (requireSuccessfulDiscovery && (pollingErrors.Count != 0 || observationErrors.Count != 0))
                throw new IOException("取得経路の検出・読取りが未確認のため、復旧完了を判定できません");
            return BeginReleaseCore(DeviceReleaseReason.StartupRecovery);
        }
    }
    public Task SweepAsync()
    { lock (gate) { ExpireCore(); return releasing ? release : Task.CompletedTask; } }
    public void Start()
    {
        lock (gate)
        {
            if (started || stopping) throw new InvalidOperationException("サービスは開始済みまたは終了中です"); started = true;
            watchdog = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                try { while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) { Refresh(); await SweepAsync().ConfigureAwait(false); _ = PollAsync(); _ = TickCoolingAsync(); } }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
            lightingWatchdog = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                try { while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) _ = TickLightingAsync(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
            notificationWatchdog = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                try { while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) TickNotifications(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
        }
    }
}
