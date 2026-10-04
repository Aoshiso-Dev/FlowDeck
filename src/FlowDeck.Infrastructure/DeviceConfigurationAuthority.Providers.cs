using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    public bool SupportsProviderRouting => providerRouting is not null;
    public bool SupportsProviderDeviceSelection => providerRouting?.SupportsDeviceSelection == true;
    private DeviceProviderRouteReview ReviewProviderRoutes(DeviceProviderRoutePlan plan, DeviceControlRuntime runtime, DeviceConfigurationPackage next)
    {
        DeviceProviderRoutingDefinition.Check(providerRouting, routingDefinition);
        var before = configuration.Preferences.Providers ?? new(); var after = next.Preferences.Providers ?? new();
        var selected = after.OpenRgbDeviceIds ?? [];
        if (selected.Length > 0)
        {
            if (!SupportsProviderDeviceSelection) throw new IOException("このサービスでは機器別の選択に対応していません");
            if (!before.OpenRgbEnabled || before.OpenRgbPort != after.OpenRgbPort)
                throw new IOException("OpenRGBへ接続してから制御する機器を選択してください");
            var candidates = runtime.PhysicalCandidates().Select(c => c.DeviceId).ToHashSet(StringComparer.Ordinal);
            var connected = runtime.State().Inventory.Devices.Where(d => d.Connected && d.Device.Provider == OpenRgbDeviceProvider.Provider &&
                d.Device.Endpoints.Any(e => e.Kind == EndpointKind.Lighting)).Select(d => d.Device.Id).ToHashSet(StringComparer.Ordinal);
            if (selected.Except(before.OpenRgbDeviceIds ?? []).Any(id => !candidates.Contains(id) || !connected.Contains(id)))
                throw new IOException("選択したOpenRGB機器を確認できません。一覧を再取得してください");
        }
        var scope = runtime.InspectProviderReplacement(plan.Remove, plan.Add);
        var isolation = Isolation(plan);
        var continuing = isolation is null ? null : runtime.InspectProviderIsolation(isolation);
        return new(routingDefinition!.Id, routingDefinition.Revision, routingDefinition.Digest,
            plan.Remove.ToArray(), plan.Add.ToArray(), scope.RetainedProviderIds, scope.TopologyRevision, scope.RemovingDeviceIds,
            StopsAllControls: isolation is null,
            OpenRgbDeviceIds: SupportsProviderDeviceSelection ? selected.Order(StringComparer.Ordinal).ToArray() : null,
            ContinuingDeviceIds: continuing, IsolationDigest: isolation is null ? null : SettingsTransfer.Hash(isolation));
    }
    private static DeviceProviderIsolation? Isolation(DeviceProviderRoutePlan plan) => plan.IsolatedResourceDomains is { } domains
        ? new(plan.Remove.Concat(plan.Add).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), domains.ToArray()) : null;
    private DeviceProviderRoutePlan? PlanProviderRoutes(DeviceConfigurationPackage before, DeviceConfigurationPackage after)
    {
        if ((before.Preferences.Providers ?? new()).EquivalentTo(after.Preferences.Providers ?? new())) return null;
        if (providerRouting is null) throw new IOException("このサービス構成では取得経路を切り替えられません");
        DeviceProviderRoutingDefinition.Check(providerRouting, routingDefinition);
        var plan = providerRouting.Plan(Contract.Copy(before), Contract.Copy(after));
        DeviceProviderRoutingDefinition.Check(providerRouting, routingDefinition);
        static bool Ids(string[]? ids) => ids is { Length: <= 8 } && ids.All(DeviceConfigurationRules.Key) && ids.Distinct().Count() == ids.Length;
        if (plan is null || !Ids(plan.Remove) || !Ids(plan.Add) || plan.Remove.Length + plan.Add.Length == 0)
            throw new IOException("取得経路の切替計画が不正です");
        if (plan.IsolatedResourceDomains is { } domains && (domains.Length is < 1 or > 128 ||
            domains.Any(d => !DeviceConfigurationRules.Text(d, 256)) || domains.Distinct(StringComparer.Ordinal).Count() != domains.Length))
            throw new IOException("取得経路の分離資源が不正です");
        // A route-only edit may preserve independent devices. A combined setup/import edit still
        // uses the complete release/apply contract and is reviewed as stopping all controls.
        var routeOnly = Same(before with { Preferences = before.Preferences with { Providers = after.Preferences.Providers } }, after);
        return new(plan.Remove.Order(StringComparer.Ordinal).ToArray(), plan.Add.Order(StringComparer.Ordinal).ToArray(),
            routeOnly ? plan.IsolatedResourceDomains?.Order(StringComparer.Ordinal).ToArray() : null);
    }
    private void PublishProviderConfiguration(DeviceConfigurationPackage config, ConfigurationPointer pointer)
    {
        configuration = Contract.Copy(config); generation = pointer;
        PublishLightingLibrary(); coolingResumeIntent = config.Cooling.Endpoints.ToDictionary(c => c.Target, c => c.ResumeOnStart);
    }
    private async Task<DeviceConfigurationReply> ApplyProviderChangeAsync(DeviceConfigurationPackage old, DeviceConfigurationPackage next,
        ConfigurationPointer before, ConfigurationActivation staged, ConfigurationApplication record, DeviceProviderRoutePlan plan,
        DeviceProviderRouteReview review, string owner, long topology, DeviceControlRuntime runtime)
    {
        var recordPath = Path.Combine(store.DirectoryPath, "configuration-application.json");
        var rolledBack = false; var committed = false;
        var continuing = review.ContinuingDeviceIds?.ToHashSet(StringComparer.Ordinal);
        try
        {
            await runtime.ReconfigureAsync(owner, topology, async (drivers, token) =>
            {
                await access.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    CheckCurrent();
                    if (generation != before || !Same(plan, PlanProviderRoutes(old, next)) || !Same(review, ReviewProviderRoutes(plan, runtime, next)))
                        throw new IOException("取得経路の設定または切替条件が変更されました");
                    _ = CheckBindings(next, runtime);
                    var transition = new DeviceProviderTransition(store, installation, routingDefinition);
                    transition.Begin(record.RequestId, before, staged.Target);
                    try
                    {
                        drivers = await transition.SwitchAsync(runtime, owner, plan.Remove,
                            () => CreateProviderRoute(next, plan.Add), token, expectedProviders: plan.Add).ConfigureAwait(false);
                        DeviceProviderRoutingDefinition.Check(providerRouting, routingDefinition);
                        // Discovery may reveal a different device or a changed capability set.
                        _ = CheckBindings(next, runtime);
                        var bindings = ConfigureDrivers(next, installation, drivers, continuing); token.ThrowIfCancellationRequested();
                        store.Activate(staged);
                        ServiceEnvironment.Write(recordPath, record with { Phase = "applied" });
                        var final = transition.Commit(transition.Pending!); committed = true;
                        PublishProviderConfiguration(next, staged.Target);
                        transition.Complete(final);
                        return bindings;
                    }
                    catch (Exception original)
                    {
                        // A committed transition is durable. A cleanup/publish failure cannot turn
                        // its source pointer back on; cold startup must recover the committed target.
                        if (committed)
                        {
                            failure = "新しい取得経路は保存済みですが、完了処理を確認できません。サービス再起動で復旧してください";
                            throw;
                        }
                        try
                        {
                            var active = store.ReadActivePointer();
                            if (active == staged.Target) store.Activate(new(before, staged.Target));
                            else if (active != before) throw new IOException("復帰先の設定世代が競合しています");
                            // This second replacement independently verifies release of the failed
                            // target before closure; release failure leaves the journal for startup.
                            drivers = await transition.SwitchAsync(runtime, owner, plan.Add,
                                () => CreateProviderRoute(old, plan.Remove), token, rollback: true,
                                expectedProviders: plan.Remove).ConfigureAwait(false);
                            DeviceProviderRoutingDefinition.Check(providerRouting, routingDefinition);
                            _ = ConfigureDrivers(old, installation, drivers, continuing);
                            var final = transition.ConfirmRollback(transition.Pending!);
                            ServiceEnvironment.Write(recordPath, record with { Phase = "rolled-back", Error = original.Message });
                            transition.Complete(final); rolledBack = true;
                        }
                        catch (Exception rollback)
                        {
                            failure = "取得経路の復帰を確認できません。サービス再起動で復旧してください";
                            throw new AggregateException(failure, original, rollback);
                        }
                        throw;
                    }
                }
                finally { access.Release(); }
            }, Isolation(plan)).ConfigureAwait(false);
            await access.WaitAsync().ConfigureAwait(false);
            try { upload = null; return Reply(continuing is null ? "取得経路と設定を反映しました。制御は監視状態です" :
                "取得経路を切り替えました。対象外の制御は継続しています", changed: ConfigurationGenerationStore.ComponentNames.ToArray()); }
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
    private IDeviceDriverProvider[] CreateProviderRoute(DeviceConfigurationPackage config, string[] ids)
    {
        DeviceProviderRoutingDefinition.Check(providerRouting, routingDefinition);
        return providerRouting!.Create(Contract.Copy(config), ids.ToArray());
    }
}
