namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private sealed record ShutdownReleaseFailure(string Message, HashSet<string> Endpoints);
    private readonly Dictionary<IDeviceControlDriver, ShutdownReleaseFailure> shutdownReleaseFailures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IDeviceControlDriver, DeviceInstance> shutdownOrigins = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDeviceDriverProvider> shutdownRecoveryReported = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> shutdownNonRecoveryErrors = [];

    private string? ShutdownReleaseErrorCore()
    {
        var messages = shutdownReleaseFailures.Values.Select(f => f.Message).Concat(shutdownNonRecoveryErrors)
            .Concat(providerTransitionError is null ? [] : new[] { providerTransitionError }).Distinct().ToArray();
        return messages.Length == 0 ? null : string.Join(" / ", messages);
    }

    // Runs under the runtime gate, after physical operations have drained and both provider and
    // child disposal succeeded. ReadShutdownRecoveries is an in-memory compiled-provider API.
    private void ApplyShutdownRecoveriesCore(IDeviceProviderShutdownCoordinator provider)
    {
        var reports = provider.ReadShutdownRecoveries();
        if (reports is null || reports.Length > DeviceRegistry.MaximumDevices || reports.Any(r => r is null) ||
            reports.DistinctBy(r => r.Id).Count() != reports.Length || reports.DistinctBy(r => r.DeviceId).Count() != reports.Length)
            throw new IOException("最終復旧結果の対象数・重複が不正です");
        reports = Contract.Copy(reports);
        var owners = new Dictionary<string, (IDeviceControlDriver Driver, DeviceInstance Origin)>();
        // Validate the entire batch before ending even one obligation.
        foreach (var report in reports)
        {
            if (!DeviceServiceProtocol.Id(report.Id) || !DeviceConfigurationRules.Key(report.DeviceId) ||
                !DeviceConfigurationRules.Text(report.Locator, 2048) || report.DefinitionId is not null && !DeviceConfigurationRules.Key(report.DefinitionId) ||
                !drivers.TryGetValue(report.DeviceId, out var driver) || driverProviders.GetValueOrDefault(report.DeviceId) != provider.ProviderId ||
                !shutdownClosedDrivers.Contains(driver) || !shutdownOrigins.TryGetValue(driver, out var origin))
                throw new IOException("最終復旧結果の所有元が一致しません");
            if (report.Results is not { Length: > 0 and <= DeviceRegistry.MaximumEndpoints } || report.Results.Any(r => r is null ||
                !DeviceConfigurationRules.Key(r.EndpointId) || !ValidDriverResult(r.Result) ||
                r.Result.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified) || r.Result.Failure != DeviceDriverFailure.None ||
                r.Result.ReportedDuty is not null || r.Result.ReportedLighting is not null ||
                !origin.Device.Endpoints.Any(e => e.Id == r.EndpointId && e.Kind is EndpointKind.Cooling or EndpointKind.Lighting)) ||
                report.Results.DistinctBy(r => r.EndpointId).Count() != report.Results.Length)
                throw new IOException("最終復旧結果の端子・確認値が不正です");
            owners.Add(report.DeviceId, (driver, origin));
        }
        var inventory = registry.Snapshot;
        foreach (var report in reports)
        {
            var (driver, origin) = owners[report.DeviceId];
            var current = inventory.Devices.SingleOrDefault(d => d.Device.Id == report.DeviceId);
            var matching = report.Locator == origin.Device.Locator && report.DefinitionId == origin.Device.DefinitionId &&
                current is not null && current.Device.Locator == origin.Device.Locator && current.Device.DefinitionId == origin.Device.DefinitionId &&
                (current.Generation == origin.Generation || !current.Connected);
            var applied = new HashSet<string>();
            if (matching)
            {
                foreach (var row in report.Results)
                {
                    var target = new EndpointReference(report.DeviceId, row.EndpointId);
                    if (!states.TryGetValue(target, out var state)) continue;
                    states[target] = state with
                    {
                        PolicyRevision = state.PolicyRevision + 1,
                        // A closed/replaced connection has no live value. Confirmation belongs to
                        // the historical release event in diagnostics, not a fabricated reading.
                        Outcome = state.Generation == origin.Generation ? row.Result.Outcome : Outcome.Unknown,
                        Message = state.Generation == origin.Generation ? row.Result.Message : "以前の接続: " + row.Result.Message + "。現在値は未取得です",
                        RequestedDuty = null, ReportedDuty = null, RequestedLighting = null, ReportedLighting = null,
                        Owned = false, Cooling = null, Lighting = null
                    };
                    recoveryRequired.Remove(target); lightingRecoveryRequired.Remove(target); coolingPolicies.Remove(target);
                    applied.Add(row.EndpointId);
                }
                if (shutdownReleaseFailures.TryGetValue(driver, out var failure))
                {
                    failure.Endpoints.ExceptWith(applied);
                    if (failure.Endpoints.Count == 0 && !states.Values.Any(s => s.Target.DeviceId == report.DeviceId && s.Owned))
                        shutdownReleaseFailures.Remove(driver);
                }
            }
            // Preserve the earlier failure as an event; append the final historical recovery.
            // A stale locator is still historical evidence, but cannot change the newer state.
            RecordDiagnosticRelease(driver, DeviceReleaseReason.ServiceStopping, clock.GetTimestamp(), report.Results, false);
            try { log.Write("deviceShutdownRecovery", new { provider.ProviderId, report, applied = applied.ToArray(), matching }); }
            catch (Exception e) { shutdownNonRecoveryErrors.Add("終了診断の保存に失敗しました: " + e.Message); }
        }
        shutdownRecoveryReported.Add(provider);
    }
}
