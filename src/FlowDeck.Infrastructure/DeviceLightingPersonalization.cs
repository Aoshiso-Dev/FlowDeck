using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class DeviceLightingPersonalization
{
    public static bool MetadataOnly(DeviceWorkspaceSnapshot before, DeviceWorkspaceSnapshot after)
    {
        if (!after.Connected || after.State.Releasing || after.State.ConfigurationChanging || before.Session != after.Session ||
            before.State.Inventory.Epoch != after.State.Inventory.Epoch || before.State.Inventory.TopologyRevision != after.State.Inventory.TopologyRevision) return false;
        var a = before.Configuration.Configuration; var b = after.Configuration.Configuration;
        b = b with { Lighting = b.Lighting with { Names = a.Lighting.Names, Revision = a.Lighting.Revision },
            Preferences = b.Preferences with { Palette = a.Preferences.Palette } };
        return SettingsTransfer.Canonical(a) == SettingsTransfer.Canonical(b);
    }
    public static void Preserve(DeviceLightingDraft draft, DeviceWorkspaceSnapshot before, DeviceWorkspaceSnapshot after)
    {
        if (!MetadataOnly(before, after)) return;
        DeviceLightingEditVersion? Version(DeviceWorkspaceSnapshot s)
        {
            var values = draft.Targets.Select(t => s.State.Endpoints.SingleOrDefault(e => e.Target == t)).ToArray();
            return values.Any(v => v is null) ? null : new(s.State.Inventory.Epoch, s.State.Inventory.TopologyRevision, s.Configuration.Generation,
                values.Select(v => new DeviceTargetVersion(v!.Target, v.Generation, v.PolicyRevision)).ToArray());
        }
        if (Version(before) is { } a && Version(after) is { } b) draft.AcceptMetadataSave(a, b);
    }
    public static DeviceLightingLibraryEdit Capture(DeviceWorkspaceSnapshot snapshot, LightingLibraryOperation operation, EndpointAlias? alias = null, RgbColor[]? colors = null)
    {
        if (!snapshot.Connected || snapshot.ElapsedSeconds is < 0 or >= 3 || !double.IsFinite(snapshot.ElapsedSeconds) || snapshot.Session is null || snapshot.State.Releasing || snapshot.State.ConfigurationChanging)
            throw new IOException("照明設定を保存する前に接続を更新してください");
        var request = new LightingLibraryRequest(operation, snapshot.Configuration.Generation, operation == LightingLibraryOperation.SaveAlias ? "alias" : "palette",
            snapshot.Configuration.Configuration.Lighting.Revision, Alias: alias, Colors: colors);
        if (!DeviceLightingLibrary.Personalization(operation) || !DeviceLightingLibrary.Valid(request)) throw new ArgumentException("照明の名前・色見本が不正です");
        return new(snapshot.State.Inventory.Epoch, snapshot.Session, Contract.Copy(request));
    }
}

public sealed class DeviceLightAliasDraft
{
    private DeviceWorkspaceSnapshot original, latest;
    private readonly string saved;
    public EndpointReference Target { get; }
    public string Name { get; set; }
    public bool Dirty => Name != saved;
    public bool Conflict { get; private set; }
    public string? Problem => !latest.Connected || latest.ElapsedSeconds is < 0 or >= 3 || !double.IsFinite(latest.ElapsedSeconds) || latest.State.Releasing || latest.State.ConfigurationChanging ? "状態を更新してください" :
        Conflict ? "別の操作で変更あり・現在の名前を読み込んでください" : Name is null || Name.Trim().Length > 0 && !DeviceConfigurationRules.Text(Name.Trim(), 80) ? "名前は80文字以内で入力してください" : null;
    public DeviceLightAliasDraft(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        if (!snapshot.Configuration.Configuration.Devices.Devices.Any(d => d.Id == target.DeviceId && d.Endpoints.Any(e => e.Id == target.EndpointId && e.Kind == EndpointKind.Lighting)))
            throw new ArgumentException("登録済みの照明端子を選択してください");
        original = latest = Contract.Copy(snapshot); Target = target;
        saved = Name = snapshot.Configuration.Configuration.Lighting.Names.SingleOrDefault(n => n.Target == target)?.Name ?? "";
    }
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest = snapshot;
        Conflict |= original.Session != snapshot.Session || original.State.Inventory.Epoch != snapshot.State.Inventory.Epoch || original.Configuration.Generation != snapshot.Configuration.Generation;
    }
    public void AcceptMetadataSave(DeviceWorkspaceSnapshot before, DeviceWorkspaceSnapshot after)
    {
        if (!Conflict && original.Configuration.Generation == before.Configuration.Generation && DeviceLightingPersonalization.MetadataOnly(before, after) &&
            before.Configuration.Configuration.Lighting.Names.SingleOrDefault(n => n.Target == Target) == after.Configuration.Configuration.Lighting.Names.SingleOrDefault(n => n.Target == Target))
        { original = original with { Configuration = after.Configuration }; latest = after; }
    }
    public DeviceLightingLibraryEdit Capture()
    {
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        return DeviceLightingPersonalization.Capture(original with { ElapsedSeconds = latest.ElapsedSeconds, Connected = latest.Connected }, LightingLibraryOperation.SaveAlias, new(Target, Name.Trim()));
    }
}
