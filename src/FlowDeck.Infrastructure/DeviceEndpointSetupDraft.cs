using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceEndpointSetupValues(string Name, bool Confirmed, CoolingSafety? Safety = null,
    int? LedCount = null, LightingExitPolicy? LightingExit = null);
public sealed record DeviceEndpointSetupEdit(string Epoch, string Generation, long Topology, long DeviceGeneration,
    EndpointReference Target, DeviceConfigurationPackage Configuration, string[] Changes, bool Removing = false)
{
    public bool Matches(DeviceWorkspaceSnapshot? snapshot)
    {
        if (snapshot is not { Connected: true, ExitAttempted: false } || snapshot.State.Releasing || snapshot.State.ConfigurationChanging ||
            snapshot.State.Inventory.Epoch != Epoch || snapshot.Configuration.Generation != Generation || snapshot.State.Inventory.TopologyRevision != Topology) return false;
        var actual = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == Target.DeviceId);
        return Removing ? (actual?.Generation ?? 0) == DeviceGeneration : actual is { Connected: true } && actual.Generation == DeviceGeneration;
    }
}
public sealed record DeviceEndpointSetupPreview(DeviceEndpointSetupEdit Edit, ServiceConfigurationPreview Transfer);

/// <summary>One explicitly selected physical endpoint. Polling never rebases setup confirmations.</summary>
public sealed class DeviceEndpointSetupDraft
{
    private readonly DeviceWorkspaceSnapshot original;
    private readonly DeviceEndpointSetupValues? savedConfirmation;
    public DeviceSetupCandidate Candidate { get; }
    public PhysicalDeviceBinding Binding { get; }
    public DeviceEndpointSetupValues Values { get; private set; }
    public bool Conflict { get; private set; }
    public bool Available { get; private set; }
    public bool Cooling => Candidate.Options.CoolingExitActions.Length > 0;
    public bool ReusesSavedConfirmation => Available && !Conflict && Values.Confirmed && savedConfirmation is not null &&
        SameConfirmationInputs(savedConfirmation, Values);
    public DeviceEndpointSetupDraft(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        original = Contract.Copy(snapshot);
        if (snapshot.Configuration.TopologyRevision is { } topology && topology != snapshot.State.Inventory.TopologyRevision)
            throw new IOException("設定候補の機器構成が古くなっています。再取得してください");
        Candidate = original.Configuration.Setup?.SingleOrDefault(c => c.Target == target) ?? throw new IOException("設定に対応する端子を再取得してください");
        if (!DeviceSetup.Valid(Candidate)) throw new IOException("設定候補が不正です");
        Binding = original.Configuration.Candidates.SingleOrDefault(c => c.DeviceId == target.DeviceId) ?? throw new IOException("実機の個体を確認できません");
        var config = original.Configuration.Configuration;
        var actual = original.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == target.DeviceId);
        var endpoint = actual?.Device.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId) ?? throw new IOException("端子を取得できません");
        var saved = config.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId);
        var bound = config.Bindings.Devices.Contains(Binding);
        var cooling = config.Cooling.Endpoints.SingleOrDefault(e => e.Target == target);
        var lighting = config.Lighting.Endpoints?.SingleOrDefault(e => e.Target == target);
        if (Cooling)
        {
            var safety = cooling?.Safety ?? new(100, 100, Candidate.Options.CoolingExitActions.Contains(CoolingExitAction.FixedDuty) ? 100 : null,
                Candidate.Options.CoolingExitActions[0], []);
            Values = new(saved?.Name ?? endpoint.Name, bound && cooling?.WiringConfirmed == true, safety);
        }
        else Values = new(saved?.Name ?? endpoint.Name, bound && lighting?.WiringConfirmed == true && saved?.Setup?.LedCountConfirmed == true,
            LedCount: saved?.Setup?.LedCount ?? Candidate.Options.DetectedLeds ?? Candidate.Options.MinimumLeds,
            LightingExit: lighting?.Exit ?? new(Candidate.Options.LightingExitActions.Contains(LightingExitAction.RestoreOriginal) ?
                LightingExitAction.RestoreOriginal : LightingExitAction.Off));
        savedConfirmation = Values.Confirmed ? Contract.Copy(Values) : null;
        Update(snapshot);
    }
    public void Change(DeviceEndpointSetupValues value) { Values = Contract.Copy(value); }
    // Confirmation concerns physical setup (and cooling safety), not names or lighting exit colors.
    // Only a saved confirmation for the already matched device may be restored after editing values back.
    public void ChangeInputs(DeviceEndpointSetupValues value)
    {
        var confirmed = Values.Confirmed && SameConfirmationInputs(Values, value) ||
            savedConfirmation is not null && SameConfirmationInputs(savedConfirmation, value);
        Change(value with { Confirmed = confirmed });
    }
    private bool SameConfirmationInputs(DeviceEndpointSetupValues left, DeviceEndpointSetupValues right) => Cooling
        ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(left.Safety, Contract.Json).AsSpan()
            .SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(right.Safety, Contract.Json))
        : left.LedCount == right.LedCount;
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        Conflict |= snapshot.State.Inventory.Epoch != original.State.Inventory.Epoch || snapshot.Configuration.Generation != original.Configuration.Generation ||
            snapshot.State.Inventory.TopologyRevision != original.State.Inventory.TopologyRevision;
        var current = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == Candidate.Target.DeviceId);
        var descriptor = snapshot.Configuration.Setup?.SingleOrDefault(c => c.Target == Candidate.Target);
        Conflict |= current is null || current.Generation != Candidate.Generation || descriptor is null ||
            !System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(descriptor, Contract.Json).AsSpan().SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Candidate, Contract.Json)) ||
            !snapshot.Configuration.Candidates.Contains(Binding);
        Available = snapshot.Connected && !snapshot.ExitAttempted && !snapshot.State.Releasing && !snapshot.State.ConfigurationChanging && current?.Connected == true;
    }
    public string? Problem
    {
        get
        {
            if (Conflict) return "構成が変わりました。再取得してください";
            if (!Available) return "機器へ接続されていません";
            if (!DeviceConfigurationRules.Text(Values.Name, 80)) return "端子名を入力してください";
            var config = original.Configuration.Configuration;
            if (DeviceControlCapacity.ReplacementProblem(config.Devices, config.Cooling, config.Lighting, Candidate.Target, Cooling, Values.LedCount) is { } limit) return limit;
            if (!Values.Confirmed) return Cooling ? "配線と出力値の確認が必要です" : "対象とLED数の確認が必要です";
            if (Cooling)
            {
                if (Values.LedCount is not null || Values.LightingExit is not null || Values.Safety is not { } safety) return "冷却設定が不正です";
                if (!Candidate.Options.CoolingExitActions.Contains(safety.ExitAction)) return "対応する終了方法を選択してください";
                return DeviceConfigurationRules.SafetyReadiness(safety);
            }
            if (Values.Safety is not null || Values.LedCount is not { } count || count < Candidate.Options.MinimumLeds || count > Candidate.Options.MaximumLeds ||
                Candidate.Options.DetectedLeds is { } detected && count != detected) return "機器に合うLED数を指定してください";
            if (!DeviceLightingConfiguration.ValidExit(Values.LightingExit) || !Candidate.Options.LightingExitActions.Contains(Values.LightingExit!.Action))
                return "対応する照明の終了方法を選択してください";
            return null;
        }
    }
    public DeviceEndpointSetupEdit Capture()
    {
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        var config = original.Configuration.Configuration; var target = Candidate.Target;
        var actual = original.State.Inventory.Devices.Single(d => d.Device.Id == target.DeviceId).Device;
        var endpoint = actual.Endpoints.Single(e => e.Id == target.EndpointId);
        var prior = config.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId);
        if (prior is not null && (prior.Provider != actual.Provider || prior.Kind != actual.Kind ||
            prior.Endpoints.Any(e => e.Id == endpoint.Id && e.Kind != endpoint.Kind))) throw new IOException("登録した機器・端子の種類が一致しません");
        var registered = prior ?? new(actual.Id, DeviceMonitoringDraft.DisplayDeviceName(actual, original.State.Inventory), actual.Provider, actual.Kind, []);
        var rebind = !config.Bindings.Devices.Contains(Binding);
        var revoked = rebind && (config.Cooling.Endpoints.Any(e => e.Target.DeviceId == target.DeviceId && e.Target != target && e.WiringConfirmed) ||
            (config.Lighting.Endpoints ?? []).Any(e => e.Target.DeviceId == target.DeviceId && e.Target != target && e.WiringConfirmed));
        if (rebind) registered = registered with { Endpoints = registered.Endpoints.Select(e => e.Id != target.EndpointId && e.Setup is { } setup ?
            e with { Setup = setup with { LedCountConfirmed = false } } : e).ToArray() };
        var savedEndpoint = registered.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id) ?? new(endpoint.Id, Values.Name, endpoint.Kind, endpoint.Role ?? "");
        savedEndpoint = savedEndpoint with { Name = Values.Name, Setup = Cooling ? savedEndpoint.Setup :
            new(Candidate.Options.Accessory ?? savedEndpoint.Setup?.Accessory ?? "", Values.LedCount, true) };
        registered = registered with { Endpoints = Replace(registered.Endpoints, savedEndpoint, e => e.Id == endpoint.Id) };
        // A new whole-device fingerprint cannot silently reapprove other ports on that device.
        var cooling = config.Cooling.Endpoints.Select(e => rebind && e.Target.DeviceId == target.DeviceId ?
            e with { WiringConfirmed = false, SavedPolicy = null, ResumeOnStart = false } : e).ToArray();
        var lighting = (config.Lighting.Endpoints ?? []).Select(e => rebind && e.Target.DeviceId == target.DeviceId ? e with { WiringConfirmed = false } : e).ToArray();
        if (Cooling)
        {
            var old = cooling.SingleOrDefault(e => e.Target == target);
            cooling = Replace(cooling, new(target, true, Values.Safety!, old?.Profiles ?? [], null, false), e => e.Target == target);
        }
        else lighting = Replace(lighting, new(target, true, Values.LightingExit), e => e.Target == target);
        var next = config with {
            Devices = config.Devices with { Devices = Replace(config.Devices.Devices, registered, d => d.Id == actual.Id) },
            Bindings = config.Bindings with { Devices = Replace(config.Bindings.Devices, Binding, b => b.DeviceId == actual.Id) },
            Cooling = config.Cooling with { Endpoints = cooling }, Lighting = config.Lighting with { Endpoints = lighting }
        };
        DeviceConfigurationCodec.Validate(next);
        static string Duty(int? value) => value is null ? "未設定" : value + "%";
        static string FanExit(CoolingSafety? safety) => safety is null ? "未設定" : safety.ExitAction == CoolingExitAction.ReleaseToDevice ? "機器管理" : Duty(safety.Exit);
        static string LightExit(LightingExitPolicy? exit) => exit is null ? "未設定" : exit.Action == LightingExitAction.RestoreOriginal ? "元の設定" :
            exit.Action == LightingExitAction.Off ? "消灯" : $"#{exit.Color!.R:X2}{exit.Color.G:X2}{exit.Color.B:X2}";
        var changes = new List<string> { registered.Name + " / " + Values.Name };
        var previousName = prior?.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id)?.Name;
        if (previousName != Values.Name) changes.Add("名前: " + (previousName ?? "未登録") + " → " + Values.Name);
        if (rebind) changes.Add("実機の関連付け: 未確認 → 現在の個体・構成");
        if (Cooling)
        {
            var previous = config.Cooling.Endpoints.SingleOrDefault(e => e.Target == target)?.Safety;
            changes.Add($"最低: {Duty(previous?.Minimum)} → {Duty(Values.Safety!.Minimum)} / 異常時: {Duty(previous?.Fallback)} → {Duty(Values.Safety.Fallback)}");
            changes.Add($"終了時: {FanExit(previous)} → {FanExit(Values.Safety)}");
            if (config.Cooling.Endpoints.Any(e => e.Target == target && (e.SavedPolicy is not null || e.ResumeOnStart))) changes.Add("この端子の保存済み制御方針・自動再開を解除");
        }
        else
        {
            changes.Add($"LED数: {prior?.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id)?.Setup?.LedCount?.ToString() ?? "未設定"} → {Values.LedCount}");
            changes.Add($"終了時: {LightExit(config.Lighting.Endpoints?.SingleOrDefault(e => e.Target == target)?.Exit)} → {LightExit(Values.LightingExit)}");
        }
        if (revoked) changes.Add("実機との一致が未確認のため、この機器の他端子は再確認が必要");
        changes.Add(Cooling ? "変更する端子の制御を終了して保存。ほかのファンの制御は継続（実機の関連付けを更新する機器を除く）" : "この端子の照明だけ保存済みの終了方法で終了して保存。ファンとほかの照明は継続（実機の関連付けを更新する機器を除く）");
        return new(original.State.Inventory.Epoch, original.Configuration.Generation, original.State.Inventory.TopologyRevision,
            Candidate.Generation, target, Contract.Copy(next), changes.ToArray());
    }
    private static T[] Replace<T>(T[] source, T value, Func<T, bool> matches) => source.Any(matches) ? source.Select(x => matches(x) ? value : x).ToArray() : [.. source, value];
}
