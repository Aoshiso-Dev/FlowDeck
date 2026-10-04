using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceM5PortEditor(DeviceM5SettingsEditor owner, DeviceM5PortChoice choice, bool lighting) : Observable
{
    public DeviceM5PortChoice Choice { get; private set; } = choice;
    public string Id => Choice.Id;
    public string Name => Choice.Name;
    public bool Available => Choice.Available || Selected;
    public bool Selected { get => (lighting ? owner.Draft.Settings.Selection.Leds : owner.Draft.Settings.Selection.Fans).Contains(Id); set => owner.SelectPort(Id, lighting, value); }
    public void Publish(DeviceM5PortChoice next) { Choice = next; Refresh(); }
}
public sealed class DeviceM5BoardEditor(DeviceM5SettingsEditor owner, DeviceDashboardItem item) : Observable
{
    public DeviceDashboardItem Item { get; private set; } = item;
    public string Key => DeviceDashboard.Key(Item.Target, Item.Measurement);
    public bool Selected { get => owner.Draft.Settings.Selection.Motherboard.Any(i => DeviceDashboard.Key(i.Target, i.Measurement) == Key); set => owner.SelectBoard(Item, value); }
    public bool CanSelect => Selected || owner.Draft.Settings.Selection.Motherboard.Length < 8;
    public string Name { get => Item.Name; set { if (!owner.Updating && value != Item.Name) { Item = Item with { Name = value }; owner.RenameBoard(Item); } } }
    public string Unit => Item.Measurement == DeviceFeature.Temperature ? "°C" : "RPM";
    public ICommand Up => new AsyncCommand(_ => { owner.MoveBoard(Item, -1); return Task.CompletedTask; }, _ => owner.CanMoveBoard(Item, -1));
    public ICommand Down => new AsyncCommand(_ => { owner.MoveBoard(Item, 1); return Task.CompletedTask; }, _ => owner.CanMoveBoard(Item, 1));
    public void Publish(DeviceDashboardItem next) { Item = next; Refresh(); }
}
public sealed record DeviceM5ProfileChoice(string Id, string Name);
public sealed class DeviceM5ProfileEditor(DeviceM5SettingsEditor owner, string slot, string name) : Observable
{
    public string Slot => slot;
    public string Name => name;
    public DeviceM5ProfileChoice[] Choices { get; private set; } = [];
    public string Selected { get => owner.Draft.Settings.Selection.Profiles.SingleOrDefault(p => p.Slot == Slot)?.ProfileId ?? ""; set => owner.SelectProfile(Slot, value); }
    public int SelectedIndex { get => Array.FindIndex(Choices, c => c.Id == Selected); set { if (value >= 0 && value < Choices.Length) Selected = Choices[value].Id; } }
    private DeviceM5ProfileChoice[] Options(DeviceM5ProfileChoice[] choices) => choices.Any(c => c.Id == Selected) ? choices : [.. choices, new(Selected, "保存済み · 再確認が必要")];
    public bool Matches(DeviceM5ProfileChoice[] choices) => Choices.SequenceEqual(Options(choices));
    public void Publish(DeviceM5ProfileChoice[] choices)
    {
        choices = Options(choices);
        if (!Choices.SequenceEqual(choices)) { Choices = choices; Notify(nameof(Choices)); }
        Notify(nameof(Selected)); Notify(nameof(SelectedIndex));
    }
}
public sealed class DeviceM5SettingsEditor(DeviceM5SettingsDraft draft, Action changed) : Observable
{
    public DeviceM5SettingsDraft Draft => draft;
    internal bool Updating { get; private set; }
    public ObservableCollection<DeviceM5HubChoice> Hubs { get; } = [];
    public ObservableCollection<DeviceM5SensorChoice> Cpus { get; } = [];
    public ObservableCollection<DeviceM5SensorChoice> Gpus { get; } = [];
    public ObservableCollection<DeviceM5PortEditor> Fans { get; } = [];
    public ObservableCollection<DeviceM5PortEditor> Leds { get; } = [];
    public ObservableCollection<DeviceM5BoardEditor> Board { get; } = [];
    public ObservableCollection<DeviceM5ProfileEditor> Profiles { get; } = [];
    public DeviceExitChoice<string>[] Transports { get; } = [new("auto", "自動（USB優先）"), new("usb", "USB"), new("wifi", "Wi-Fi")];
    public string[] Layouts { get; } = ["A", "B"];
    public DeviceM5HubChoice? Hub { get => Hubs.FirstOrDefault(c => c.Binding == draft.Settings.Selection.Hub); set { if (Updating || value is null || value.Binding == draft.Settings.Selection.Hub) return; draft.SelectHub(value.Binding); Changed(); } }
    public DeviceM5SensorChoice? Cpu { get => Cpus.FirstOrDefault(c => c.Target == draft.Settings.Selection.Cpu); set { if (value is not null) Select(draft.Settings.Selection with { Cpu = value.Target }); } }
    public DeviceM5SensorChoice? Gpu { get => Gpus.FirstOrDefault(c => c.Target == draft.Settings.Selection.Gpu); set { if (value is not null) Select(draft.Settings.Selection with { Gpu = value.Target }); } }
    public string Transport { get => draft.Settings.Connection.Transport; set => Connection(draft.Settings.Connection with { Transport = value }); }
    public string Layout { get => draft.Settings.Connection.Layout; set => Connection(draft.Settings.Connection with { Layout = value }); }
    public string Port { get => draft.Settings.Connection.Port ?? ""; set => Connection(draft.Settings.Connection with { Port = value.Length == 0 ? null : value }); }
    public string Address { get => draft.Settings.Connection.WifiAddress ?? ""; set => Connection(draft.Settings.Connection with { WifiAddress = value.Length == 0 ? null : value, WifiInterfaceId = null }); }
    public bool ConnectOnStart { get => draft.Settings.ConnectOnStart; set => Change(draft.Settings with { ConnectOnStart = value }); }
    public bool CanAutoConnect => draft.Settings.Connection.DeviceId is not null || ConnectOnStart;
    public bool HasHub => draft.Settings.Selection.Hub is not null;
    public bool HasBoard => Board.Count > 0;
    public string BoardCount => $"{draft.Settings.Selection.Motherboard.Length} / 8";
    private void Connection(M5Settings connection) => Change(draft.Settings with { Connection = connection });
    private void Select(DeviceM5Selection selection) => Change(draft.Settings with { Selection = selection });
    private void Change(DeviceM5Settings next)
    {
        if (Updating || SettingsTransfer.Hash(next) == SettingsTransfer.Hash(draft.Settings)) return;
        draft.Change(next); Changed();
    }
    private void Changed() { Publish(); changed(); }
    internal void SelectPort(string id, bool lighting, bool selected)
    {
        if (Updating) return;
        var selection = draft.Settings.Selection; var ids = lighting ? selection.Leds : selection.Fans;
        ids = selected ? ids.Append(id).Distinct().Order().ToArray() : ids.Where(i => i != id).ToArray();
        Select(lighting ? selection with { Leds = ids } : selection with { Fans = ids });
    }
    internal void SelectProfile(string slot, string? id)
    {
        if (Updating || id is null) return;
        var profiles = draft.Settings.Selection.Profiles.Where(p => p.Slot != slot).ToArray();
        Select(draft.Settings.Selection with { Profiles = id.Length == 0 ? profiles : [.. profiles, new(slot, id)] });
    }
    internal void SelectBoard(DeviceDashboardItem item, bool selected)
    {
        if (Updating) return;
        var items = draft.Settings.Selection.Motherboard; var key = DeviceDashboard.Key(item.Target, item.Measurement);
        if (selected && (items.Length >= 8 || items.Any(i => DeviceDashboard.Key(i.Target, i.Measurement) == key))) return;
        Select(draft.Settings.Selection with { Motherboard = selected ? [.. items, item] : items.Where(i => DeviceDashboard.Key(i.Target, i.Measurement) != key).ToArray() });
    }
    internal void RenameBoard(DeviceDashboardItem item) => Select(draft.Settings.Selection with { Motherboard = draft.Settings.Selection.Motherboard.Select(i => i.Target == item.Target && i.Measurement == item.Measurement ? item : i).ToArray() });
    internal bool CanMoveBoard(DeviceDashboardItem item, int direction)
    {
        var items = draft.Settings.Selection.Motherboard; var index = Array.FindIndex(items, i => i.Target == item.Target && i.Measurement == item.Measurement);
        return index >= 0 && index + direction >= 0 && index + direction < items.Length;
    }
    internal void MoveBoard(DeviceDashboardItem item, int direction)
    {
        if (Updating || !CanMoveBoard(item, direction)) return;
        var items = draft.Settings.Selection.Motherboard.ToArray(); var index = Array.FindIndex(items, i => i.Target == item.Target && i.Measurement == item.Measurement);
        (items[index], items[index + direction]) = (items[index + direction], items[index]); Select(draft.Settings.Selection with { Motherboard = items });
    }
    public void Publish()
    {
        Updating = true;
        try
        {
            DeviceWorkspaceViewModel.Reconcile(Hubs, draft.Hubs()); DeviceWorkspaceViewModel.Reconcile(Cpus, draft.Sensors(DeviceKind.Processor)); DeviceWorkspaceViewModel.Reconcile(Gpus, draft.Sensors(DeviceKind.Graphics));
            foreach (var lighting in new[] { false, true })
            {
                var rows = lighting ? Leds : Fans;
                var next = draft.Ports(lighting).Select(p => { var row = rows.FirstOrDefault(r => r.Id == p.Id) ?? new(this, p, lighting); row.Publish(p); return row; }).ToArray();
                DeviceWorkspaceViewModel.Reconcile(rows, next);
            }
            DeviceM5ProfileChoice[] profiles = [new("", "未選択"), .. draft.Profiles().Select(p => new DeviceM5ProfileChoice(p.Id, p.Name))];
            // A changed Hub/fan choice set gets a fresh selector, already populated before binding.
            // Keep the existing controls on ordinary polls and preserve all selected IDs in the draft.
            var profileRows = new[] { ("quiet", "静音"), ("balanced", "標準"), ("cool", "冷却優先") }.Select(slot => {
                var row = Profiles.FirstOrDefault(p => p.Slot == slot.Item1);
                if (row is null || !row.Matches(profiles)) row = new(this, slot.Item1, slot.Item2);
                row.Publish(profiles); return row;
            }).ToArray();
            DeviceWorkspaceViewModel.Reconcile(Profiles, profileRows);
            var selected = draft.Settings.Selection.Motherboard;
            var available = draft.MotherboardItems().Where(i => !selected.Any(s => s.Target == i.Target && s.Measurement == i.Measurement));
            var board = selected.Concat(available).Select(i => { var row = Board.FirstOrDefault(r => r.Key == DeviceDashboard.Key(i.Target, i.Measurement)) ?? new(this, i); row.Publish(i); return row; }).ToArray();
            DeviceWorkspaceViewModel.Reconcile(Board, board); Refresh();
        }
        finally { Updating = false; }
    }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceM5SettingsDraft M5Draft { get; } = new();
    public DeviceM5SettingsEditor? M5Editor { get; private set; }
    private bool m5Expanded, m5Saving;
    private string m5Message = "";
    public bool M5Expanded { get => m5Expanded; set => Set(ref m5Expanded, value); }
    public bool M5SettingsSupported => client.SupportsM5Settings;
    public bool CanSaveM5 => SetupIdle && M5SettingsSupported && M5Draft.CanSave;
    public string M5EditStatus => !M5SettingsSupported ? "サービスの更新が必要" : m5Saving ? "保存中" : M5Draft.Problem ?? (M5Draft.Dirty ? "未保存" : "保存済み");
    public string M5Message => m5Message;
    public ICommand SaveM5 => new AsyncCommand(_ => SaveM5Async(), _ => CanSaveM5, e => { m5Message = e.Message; PublishM5(); });
    public ICommand ReloadM5 => new AsyncCommand(_ => ReloadM5Async(), _ => SetupIdle && M5SettingsSupported, e => { m5Message = e.Message; PublishM5(); });
    private void RenderM5Settings(DeviceWorkspaceSnapshot snapshot)
    {
        M5Draft.Update(snapshot); M5Editor ??= new(M5Draft, () => { m5Message = ""; PublishM5(); }); M5Editor.Publish(); PublishM5();
    }
    private void PublishM5()
    {
        foreach (var name in new[] { nameof(M5Editor), nameof(M5SettingsSupported), nameof(CanSaveM5), nameof(M5EditStatus), nameof(M5Message) }) Notify(name);
        PublishM5Connection();
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task SaveM5Async()
    {
        Render(); if (!CanSaveM5) return; var edit = M5Draft.Capture(); setupBusy = m5Saving = true; ClearConfigurationPreviews(); Render();
        try { var result = await client.SaveM5Async(edit); M5Draft.Reset(client.Snapshot!); if (m5Session is not null) await m5Session.PollAsync(); m5Message = result.Message; }
        finally { setupBusy = m5Saving = false; Render(); }
    }
    public async Task ReloadM5Async()
    {
        if (!SetupIdle) return; setupBusy = true; Render();
        try { await client.RefreshAsync(true); M5Draft.Reset(client.Snapshot!); m5Message = "現在の設定を取得しました"; }
        finally { setupBusy = false; Render(); }
    }
}
