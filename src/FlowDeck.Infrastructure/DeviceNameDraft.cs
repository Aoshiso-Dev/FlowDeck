using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class DeviceNameDraft(string deviceId)
{
    private DeviceWorkspaceSnapshot? baseline, latest;
    private string original = "";
    public string DeviceId { get; } = deviceId;
    public string Name { get; set; } = "";
    public bool Dirty => Name != original;
    public bool Conflict { get; private set; }
    public string? Problem => latest is not { Connected: true, ExitAttempted: false, Session: not null } ? "サービス未接続" :
        Conflict ? "別の操作で変更あり。現在の名前を読み込んでください" :
        latest.State.Releasing || latest.State.ConfigurationChanging ? "サービスの処理中" :
        Registered(latest) is null ? "登録が削除されています" :
        !DeviceNameChange.Valid(new(DeviceId, Name)) ? "名前を1〜160文字で入力してください" : null;
    public bool CanSave => Dirty && Problem is null;
    private RegisteredDevice? Registered(DeviceWorkspaceSnapshot snapshot) =>
        snapshot.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == DeviceId);
    public void Update(DeviceWorkspaceSnapshot snapshot)
    {
        latest = snapshot;
        if (baseline is null) Reset(snapshot);
        else if (!Dirty && !Conflict) Reset(snapshot);
        else if (baseline.State.Inventory.Epoch != snapshot.State.Inventory.Epoch || baseline.Session != snapshot.Session ||
            baseline.Configuration.Generation != snapshot.Configuration.Generation || Registered(snapshot) is null) Conflict = true;
    }
    public void Reset(DeviceWorkspaceSnapshot snapshot)
    {
        baseline = latest = snapshot; Name = original = Registered(snapshot)?.Name ?? ""; Conflict = false;
    }
    public DeviceNameEdit Capture()
    {
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        return new(baseline!.State.Inventory.Epoch, baseline.Session!, baseline.Configuration.Generation, new(DeviceId, Name));
    }
}
