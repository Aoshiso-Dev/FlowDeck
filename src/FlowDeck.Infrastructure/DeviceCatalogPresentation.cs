using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CatalogCapability(DeviceFeature Feature, ImplementationSupport Implementation, string Name, string Support, string Evidence,
    string Availability, string Reason, bool Available)
{
    public bool HasReason => Reason.Length > 0;
    public string AutomationLabel => Name + "、" + Availability + "、" + Support + "、" + Evidence;
}
public sealed record CatalogEndpoint(string Id, string Name, string Kind, CatalogCapability[] Capabilities);
public sealed record CatalogDevice(string Id, string Name, string Status, string IdentityDetails, CatalogEndpoint[] Endpoints);

/// <summary>Read-only projection of the service snapshot. It neither discovers hardware nor infers control from RPM.</summary>
public static class DeviceCatalogPresentation
{
    public static CatalogDevice[] Build(DeviceWorkspaceSnapshot snapshot)
    {
        if (!snapshot.Connected) return [];
        var saved = snapshot.Configuration.Configuration.Devices.Devices;
        var observed = snapshot.State.Inventory.Devices;
        var sourceProblem = snapshot.ExitAttempted || snapshot.State.Releasing ? "終了処理中" :
            snapshot.State.ConfigurationChanging ? "設定の反映中" : snapshot.State.LifecycleError is not null ? "サービスの復旧待ち" :
            !double.IsFinite(snapshot.ElapsedSeconds) || snapshot.ElapsedSeconds is < 0 or >= 3 ? "状態の更新待ち" : null;
        return observed.Where(d => d.Connected)
            .Select(instance => Describe(instance, saved.SingleOrDefault(d => d.Id == instance.Device.Id),
                sourceProblem, snapshot.State, snapshot.ElapsedSeconds)).ToArray();
    }
    public static CatalogDevice Describe(DeviceInstance instance) => Describe(instance, null, null, null, 0);
    private static CatalogDevice Describe(DeviceInstance instance, RegisteredDevice? saved, string? sourceProblem,
        DeviceServiceState? state, double elapsed)
    {
        var observed = instance.Device;
        var id = observed.Id;
        var unavailable = sourceProblem ?? (instance.Connected ? null : "未接続");
        var endpoints = observed.Endpoints.Select(original =>
            {
                var registered = saved?.Endpoints.SingleOrDefault(e => e.Id == original.Id);
                var capabilities = original.Capabilities.Select(c => DescribeCapability(c, unavailable, state,
                    new(id, original.Id), elapsed)).ToArray();
                return new CatalogEndpoint(original.Id, registered?.Name ?? original.Name,
                    original.Kind switch { EndpointKind.Sensor => "センサー", EndpointKind.Cooling => "ファン・ポンプ", _ => "照明" }, capabilities);
            }).ToArray();
        var identity = "機器ID：" + id + "\n取得元：" + observed.Provider +
            "\n対応定義：" + (observed.DefinitionId ?? "未取得") + "\n接続位置：" + observed.Locator +
            "\n接続世代：" + instance.Generation;
        return new(id, saved?.Name ?? observed.Name, unavailable ?? Summary(endpoints), identity, endpoints);
    }
    private static CatalogCapability DescribeCapability(DeviceCapability capability, string? unavailable, DeviceServiceState? state,
        EndpointReference target, double elapsed)
    {
        var support = capability.Support switch {
            ImplementationSupport.Supported => "対応定義あり", ImplementationSupport.Experimental => "試験対応",
            ImplementationSupport.Candidate => "候補", ImplementationSupport.Unsupported => "未対応", _ => "未確認" };
        var evidence = capability.Evidence switch {
            VerificationEvidence.SourceReview => "ソース照合", VerificationEvidence.Automated => "自動試験",
            VerificationEvidence.FamilyHardware => "同系統の実機", VerificationEvidence.MatchingHardware => "一致構成の実機", _ => "未検証" };
        var availability = unavailable ?? (capability.Availability switch {
            DeviceAvailability.Ready => capability.Usable ? "利用可" : "利用不可",
            DeviceAvailability.NeedsSetup => "設定待ち", DeviceAvailability.NeedsPermission => "権限待ち",
            DeviceAvailability.Conflict => "競合", DeviceAvailability.Disconnected => "未接続", _ => "取得不可" });
        var available = unavailable is null && capability.Usable;
        var reason = capability.Reason ?? "";
        if (unavailable is not null)
        {
            var current = unavailable switch {
                "サービス未接続" => "サービスへ再接続してください", "未接続" => "機器の接続を確認してください",
                "サービスの復旧待ち" => "サービスの状態を確認してください", "状態の更新待ち" => "状態を再取得するまで利用可とは表示しません", _ => unavailable };
            reason = current + (reason.Length > 0 ? "\n前回の理由：" + reason : "");
        }
        else if (state is not null && capability.Support == ImplementationSupport.Supported &&
            capability.Availability is DeviceAvailability.Ready or DeviceAvailability.Failed &&
            capability.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.DutyRead)
        {
            var sample = DeviceDashboard.Sample(state, target, capability.Feature);
            var measurement = DeviceMeasurement.State(sample, elapsed);
            if (measurement != DeviceMeasurementState.Valid)
            { available = false; availability = DeviceMeasurement.Label(measurement); reason = sample?.Problem ??
                (measurement == DeviceMeasurementState.OutOfRange ? "測定値が範囲外です" : measurement == DeviceMeasurementState.Stale ?
                "測定値の更新を確認できていません" : "測定値を取得できていません"); }
        }
        if (reason.Length == 0 && !available) reason = capability.Support switch {
            ImplementationSupport.Unsupported => "この機能には対応していません",
            ImplementationSupport.Candidate or ImplementationSupport.Experimental => "この機能の対応確認が済んでいません",
            ImplementationSupport.Unknown => "対応情報を取得できていません",
            _ => capability.Availability switch {
                DeviceAvailability.NeedsSetup => "設定で対象と必要な条件を確認してください",
                DeviceAvailability.NeedsPermission => "サービスの権限を確認してください",
                DeviceAvailability.Conflict => "ほかの制御や機器の対応付けを確認してください",
                DeviceAvailability.Disconnected => "機器の接続を確認してください", _ => "取得状態を確認してください" } };
        return new(capability.Feature, capability.Support, FeatureName(capability.Feature), support, evidence, availability, reason, available);
    }
    private static string Summary(CatalogEndpoint[] endpoints)
    {
        var capabilities = endpoints.SelectMany(e => e.Capabilities).ToArray();
        var groups = new (string Name, DeviceFeature[] Features)[] {
            ("温度", [DeviceFeature.Temperature]), ("回転数", [DeviceFeature.Rpm]), ("出力取得", [DeviceFeature.DutyRead]),
            ("ファン操作", [DeviceFeature.FixedDuty, DeviceFeature.AutomaticDuty, DeviceFeature.ReleaseCooling]),
            ("照明", [DeviceFeature.FixedColor, DeviceFeature.NativeEffect, DeviceFeature.ColorFrames, DeviceFeature.RestoreLighting]) };
        var summaries = new List<string>();
        foreach (var group in groups)
        {
            var matches = capabilities.Where(c => group.Features.Contains(c.Feature)).ToArray();
            if (matches.Length == 0) continue;
            var labels = matches.Select(c => c.Implementation == ImplementationSupport.Supported ? c.Availability : c.Support).Distinct().ToArray();
            var status = labels.Length == 1 ? labels[0] : matches.Any(c => c.Available) ? "一部利用可" : "要確認";
            summaries.Add(group.Name + "：" + status);
        }
        return summaries.Count > 0 ? string.Join(" · ", summaries) : "対応情報なし";
    }
    public static string FeatureName(DeviceFeature feature) => feature switch {
        DeviceFeature.Temperature => "温度", DeviceFeature.Rpm => "回転数", DeviceFeature.DutyRead => "現在出力の取得",
        DeviceFeature.FixedDuty => "固定出力", DeviceFeature.AutomaticDuty => "温度カーブ", DeviceFeature.ReleaseCooling => "機器管理へ戻す",
        DeviceFeature.FixedColor => "固定色", DeviceFeature.NativeEffect => "内蔵効果", DeviceFeature.ColorFrames => "同期効果", _ => "照明の復元" };
}
