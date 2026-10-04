using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceNotificationOptionsEditor(DeviceNotificationDraft draft, Action changed) : Observable
{
    private bool updating;
    private void Change(DeviceNotificationSettings next) { if (updating || next == draft.Settings) return; draft.Change(next); Publish(); changed(); }
    public void Publish() { updating = true; try { Refresh(); } finally { updating = false; } }
    public bool Enabled { get => draft.Settings.Enabled; set => Change(draft.Settings with { Enabled = value }); }
    public bool Desktop { get => draft.Settings.Desktop; set => Change(draft.Settings with { Desktop = value }); }
    public bool Recovery { get => draft.Settings.Recovery; set => Change(draft.Settings with { Recovery = value }); }
    public bool Faults { get => draft.Settings.ControlFaults; set => Change(draft.Settings with { ControlFaults = value }); }
    public bool Disconnects { get => draft.Settings.Disconnects; set => Change(draft.Settings with { Disconnects = value }); }
    public bool Operations { get => draft.Settings.OperationFailures; set => Change(draft.Settings with { OperationFailures = value }); }
    public int Delay { get => draft.Settings.DelaySeconds; set => Change(draft.Settings with { DelaySeconds = value }); }
    public int RecoveryDelay { get => draft.Settings.RecoverySeconds; set => Change(draft.Settings with { RecoverySeconds = value }); }
    public int Repeat { get => draft.Settings.RepeatSeconds; set => Change(draft.Settings with { RepeatSeconds = value }); }
    public DeviceExitChoice<int>[] Repeats => new[] { 0, 60, 300, 900, 3600, Repeat }.Distinct().Order()
        .Select(s => new DeviceExitChoice<int>(s, s == 0 ? "繰り返さない" : $"{s / 60d:0.#}分ごと")).ToArray();
}
public sealed record DeviceNotificationRuleChoice(string Id, string Label);
public sealed class DeviceNotificationRuleEditor(DeviceNotificationDraft draft, DeviceNotificationRule rule, Action changed) : Observable
{
    private bool updating;
    public DeviceNotificationRule Rule { get; private set; } = rule;
    public ObservableCollection<DeviceNotificationTarget> Targets { get; } = [];
    public DeviceNotificationTarget? Target
    {
        get => Targets.FirstOrDefault(t => t.Target == Rule.Target && t.Measurement == Rule.Measurement);
        set
        {
            if (updating || value is null || value.Target == Rule.Target && value.Measurement == Rule.Measurement) return;
            Change(Rule with { Target = value.Target, Measurement = value.Measurement, Name = value.Name.Length <= 64 ? value.Name : value.Name[..64],
                Threshold = value.Measurement == DeviceFeature.Temperature ? 85 : 300, Hysteresis = value.Measurement == DeviceFeature.Temperature ? 2 : 50 });
        }
    }
    public string Name { get => Rule.Name; set => Change(Rule with { Name = value }); }
    public double Threshold { get => Rule.Threshold; set { if (double.IsFinite(value)) Change(Rule with { Threshold = value }); } }
    public double Hysteresis { get => Rule.Hysteresis; set { if (double.IsFinite(value)) Change(Rule with { Hysteresis = value }); } }
    public bool Enabled { get => Rule.Enabled; set => Change(Rule with { Enabled = value }); }
    public bool Missing { get => Rule.Missing; set => Change(Rule with { Missing = value }); }
    public bool IgnoreStop { get => Rule.IgnoreStopped; set => Change(Rule with { IgnoreStopped = value }); }
    public bool IsRpm => Rule.Measurement == DeviceFeature.Rpm;
    public double Minimum => IsRpm ? 1 : -39;
    public double Maximum => IsRpm ? 30000 : 149;
    public double HysteresisMaximum => IsRpm ? 10000 : 40;
    public string ThresholdLabel => $"{Threshold:0.#}" + (IsRpm ? " RPM未満" : " °C以上");
    public string HysteresisLabel => $"{Hysteresis:0.#}" + (IsRpm ? " RPM" : " °C");
    private void Change(DeviceNotificationRule next)
    {
        if (updating || next == Rule) return;
        Rule = next; draft.ChangeRule(next); updating = true;
        try { Refresh(); } finally { updating = false; }
        changed();
    }
    public void Update(DeviceNotificationRule next, DeviceNotificationTarget[] targets)
    {
        updating = true;
        try
        {
            var different = Rule != next; Rule = next;
            DeviceWorkspaceViewModel.Reconcile(Targets, targets);
            if (different) Refresh(); else Notify(nameof(Target));
        }
        finally { updating = false; }
    }
}
public sealed record DeviceNotificationHistoryRow(DeviceNotificationEntry Entry)
{
    public string Time => Entry.Event.Time.LocalDateTime.ToString("MM/dd HH:mm:ss");
    public string Message => Entry.Event.Message;
    public string Kind => Entry.Event.Kind switch { "recovered" => "復帰", "reminder" => "再通知", "test" => "テスト", "operation-failed" => "操作失敗", _ => "警告" };
}

public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceNotificationDraft NotificationDraft { get; } = new();
    public DeviceNotificationInbox NotificationInbox { get; } = new();
    public DeviceNotificationOptionsEditor? NotificationOptions { get; private set; }
    public DeviceNotificationRuleEditor? NotificationRuleEditor { get; private set; }
    public ObservableCollection<DeviceNotificationRuleChoice> NotificationRules { get; } = [];
    public ObservableCollection<NotificationEvent> ActiveNotifications { get; } = [];
    public ObservableCollection<DeviceNotificationHistoryRow> NotificationHistory { get; } = [];
    public event Action<NotificationEvent[]>? DesktopNotifications;
    private DeviceNotificationRuleChoice? selectedNotificationRule;
    private bool notificationUpdating, notificationReading, notificationSaving, notificationExpanded;
    private string notificationMessage = "", notificationReadError = "";
    private int notificationPageOffset;
    public bool NotificationSupported => client.SupportsNotifications;
    public bool NotificationIdle => SetupIdle;
    public bool NotificationExpanded { get => notificationExpanded; set => Set(ref notificationExpanded, value); }
    public DeviceNotificationRuleChoice? SelectedNotificationRule
    {
        get => selectedNotificationRule;
        set
        {
            if (notificationUpdating || value?.Id == selectedNotificationRule?.Id) return;
            selectedNotificationRule = value; NotificationRuleEditor = null; RenderNotifications(client.Snapshot!);
        }
    }
    public bool HasNotificationEditor => NotificationRuleEditor is not null;
    public bool CanSaveNotifications => NotificationIdle && NotificationSupported && NotificationDraft.CanSave;
    public bool CanAddNotification => NotificationIdle && NotificationSupported && (NotificationDraft.Settings.Rules ?? []).Length < 256 &&
        NotificationDraft.Targets().Any(t => !(NotificationDraft.Settings.Rules ?? []).Any(r => r.Target == t.Target && r.Measurement == t.Measurement));
    public bool CanTestNotification => NotificationIdle && NotificationSupported && client.Snapshot?.Configuration.Configuration.Preferences.Notifications is { Enabled: true, Desktop: true };
    public bool HasActiveNotifications => (NotificationInbox.Feed?.ActiveTotal ?? 0) != 0;
    public string NotificationSummary => !NotificationSupported ? "サービスの更新が必要" :
        client.Snapshot?.Connected != true ? "サービス未接続" : NotificationInbox.Feed is not { } f ? "通知を取得中" : !f.Enabled ? "通知は無効" :
            f.ActiveTotal == 0 ? "現在の通知なし" : $"通知 {f.ActiveTotal}件";
    public string NotificationEditStatus => notificationSaving ? "保存中" : NotificationDraft.Problem ?? (NotificationDraft.Dirty ? "未保存" : "保存済み");
    public string NotificationMessage => notificationReadError.Length > 0 ? notificationReadError : NotificationInbox.Feed?.Error ?? notificationMessage;
    public string NotificationHistoryStatus => NotificationInbox.HistoryGap ? "履歴は直近256件です。保管範囲より前の記録は取得できません" : "";
    public string NotificationActivePage => NotificationInbox.Feed is { ActiveTotal: > 0 } f ? $"{f.ActiveOffset + 1}–{f.ActiveOffset + f.Active.Length} / {f.ActiveTotal}件" : "";
    public bool CanPreviousNotification => !notificationReading && (NotificationInbox.Feed?.ActiveOffset ?? 0) > 0;
    public bool CanNextNotification => !notificationReading && NotificationInbox.Feed is { } f && f.ActiveOffset + f.Active.Length < f.ActiveTotal;
    public ICommand SaveNotifications => new AsyncCommand(_ => SaveNotificationsAsync(), _ => CanSaveNotifications, e => NotificationFailed(e));
    public ICommand ReloadNotifications => new AsyncCommand(_ => ReloadNotificationsAsync(), _ => NotificationIdle && NotificationSupported, e => NotificationFailed(e));
    public ICommand AddNotification => new AsyncCommand(_ => { AddNotificationRule(); return Task.CompletedTask; }, _ => CanAddNotification);
    public ICommand DeleteNotification => new AsyncCommand(_ => { DeleteNotificationRule(); return Task.CompletedTask; }, _ => NotificationIdle && HasNotificationEditor);
    public ICommand TestNotification => new AsyncCommand(_ => TestNotificationAsync(), _ => CanTestNotification, e => NotificationFailed(e));
    public ICommand PreviousNotificationPage => new AsyncCommand(_ => ChangeNotificationPageAsync(-1), _ => CanPreviousNotification, e => NotificationFailed(e));
    public ICommand NextNotificationPage => new AsyncCommand(_ => ChangeNotificationPageAsync(1), _ => CanNextNotification, e => NotificationFailed(e));
    public ICommand OpenNotifications => new AsyncCommand(_ => { SelectedPage = 3; NotificationExpanded = true; return Task.CompletedTask; });
    private void NotificationFailed(Exception e) { notificationMessage = e.Message; PublishNotifications(); }
    private void NotificationChanged() { notificationMessage = ""; RenderNotifications(client.Snapshot!); }
    private void RenderNotifications(DeviceWorkspaceSnapshot snapshot)
    {
        NotificationDraft.Update(snapshot); NotificationOptions ??= new(NotificationDraft, NotificationChanged);
        notificationUpdating = true;
        try
        {
            var id = selectedNotificationRule?.Id;
            var rules = NotificationDraft.Settings.Rules ?? [];
            Reconcile(NotificationRules, rules.Select(r => new DeviceNotificationRuleChoice(r.Id,
                (r.Enabled ? "" : "無効 · ") + r.Name + $" · {r.Threshold:0.#}" + (r.Measurement == DeviceFeature.Temperature ? " °C以上" : " RPM未満"))).ToArray());
            selectedNotificationRule = NotificationRules.FirstOrDefault(r => r.Id == id) ?? (id is null ? NotificationRules.FirstOrDefault() : null);
            var selected = rules.FirstOrDefault(r => r.Id == selectedNotificationRule?.Id);
            if (selected is null) NotificationRuleEditor = null;
            else
            {
                if (NotificationRuleEditor?.Rule.Id != selected.Id) NotificationRuleEditor = new(NotificationDraft, selected, NotificationChanged);
                NotificationRuleEditor.Update(selected, NotificationDraft.Targets());
            }
            NotificationOptions.Publish();
        }
        finally { notificationUpdating = false; }
        PublishNotifications();
    }
    private void PublishNotifications()
    {
        var wasUpdating = notificationUpdating; notificationUpdating = true;
        try {
        foreach (var name in new[] { nameof(NotificationOptions), nameof(NotificationSupported), nameof(NotificationIdle), nameof(NotificationRules),
            nameof(SelectedNotificationRule), nameof(NotificationRuleEditor), nameof(HasNotificationEditor), nameof(CanSaveNotifications), nameof(CanAddNotification),
            nameof(CanTestNotification), nameof(HasActiveNotifications), nameof(NotificationSummary), nameof(NotificationEditStatus), nameof(NotificationMessage),
            nameof(NotificationHistoryStatus), nameof(NotificationActivePage), nameof(CanPreviousNotification), nameof(CanNextNotification) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
        } finally { notificationUpdating = wasUpdating; }
    }
    public void AddNotificationRule()
    {
        if (!CanAddNotification) return;
        var target = NotificationDraft.Targets().First(t => !(NotificationDraft.Settings.Rules ?? []).Any(r => r.Target == t.Target && r.Measurement == t.Measurement));
        var rule = NotificationDraft.Add(target); selectedNotificationRule = new(rule.Id, rule.Name); NotificationRuleEditor = null; NotificationChanged();
    }
    public void DeleteNotificationRule()
    {
        if (!NotificationIdle || NotificationRuleEditor is null) return;
        NotificationDraft.Remove(NotificationRuleEditor.Rule.Id); selectedNotificationRule = null; NotificationRuleEditor = null; NotificationChanged();
    }
    public async Task SaveNotificationsAsync()
    {
        Render(); if (!CanSaveNotifications) return;
        var edit = NotificationDraft.Capture(); setupBusy = notificationSaving = true; ClearConfigurationPreviews(); Render();
        try
        {
            var result = await client.SaveNotificationsAsync(edit); NotificationDraft.Reset(client.Snapshot!); notificationMessage = result.Message;
            await PollNotificationsAsync();
        }
        finally { setupBusy = notificationSaving = false; Render(); }
    }
    public async Task ReloadNotificationsAsync()
    {
        if (!NotificationIdle) return; setupBusy = true; Render();
        try { await client.RefreshAsync(true); NotificationDraft.Reset(client.Snapshot!); notificationMessage = "現在の設定を取得しました"; await PollNotificationsAsync(); }
        finally { setupBusy = false; Render(); }
    }
    public async Task TestNotificationAsync()
    {
        if (!CanTestNotification) return;
        await client.TestNotificationAsync(); notificationMessage = client.IsDemo ? "デモ通知を記録しました" : "通知テストを記録しました";
        await PollNotificationsAsync(); PublishNotifications();
    }
    public async Task ChangeNotificationPageAsync(int direction)
    {
        if (notificationReading || NotificationInbox.Feed is not { } f) return;
        notificationPageOffset = Math.Clamp(f.ActiveOffset + direction * DeviceNotificationProtocol.PageSize, 0, Math.Max(0, f.ActiveTotal - 1));
        await PollNotificationsAsync();
    }
    public async Task PollNotificationsAsync()
    {
        if (notificationReading || closing || !NotificationSupported || client.Snapshot is not { Connected: true, ExitAttempted: false } snapshot) return;
        notificationReading = true;
        var epoch = snapshot.State.Inventory.Epoch;
        var delivery = new List<NotificationEvent>();
        try
        {
            if (!NotificationInbox.SameEpoch(epoch)) notificationPageOffset = 0;
            var cursor = NotificationInbox.SameEpoch(epoch) ? NotificationInbox.Cursor : 0;
            // At most the retained history (eight pages); new arrivals remain for the next poll.
            for (var page = 0; page < DeviceNotificationProtocol.HistoryCapacity / DeviceNotificationProtocol.PageSize; page++)
            {
                var feed = await client.ReadNotificationsAsync(new(cursor));
                if (client.Snapshot?.State.Inventory.Epoch != epoch) return;
                delivery.AddRange(NotificationInbox.Accept(epoch, feed).Select(e => e.Event)); cursor = NotificationInbox.Cursor;
                if (cursor >= feed.LatestSequence) break;
            }
            var head = NotificationInbox.Feed!;
            if (notificationPageOffset > 0 && notificationPageOffset < head.ActiveTotal)
            {
                try
                {
                    var page = await client.ReadNotificationsAsync(new(cursor, notificationPageOffset, ActiveRevision: head.ActiveRevision));
                    if (client.Snapshot?.State.Inventory.Epoch != epoch) return;
                    delivery.AddRange(NotificationInbox.Accept(epoch, page).Select(e => e.Event));
                }
                catch (IOException) when (client.Snapshot?.Connected == true) { notificationPageOffset = 0; }
            }
            else notificationPageOffset = 0;
            notificationReadError = "";
        }
        catch (Exception e) { notificationReadError = "通知を取得できません: " + e.Message; }
        finally
        {
            if (!closing && client.Snapshot?.State.Inventory.Epoch == epoch && delivery.Count > 0 && DesktopNotifications is { } subscribers)
                foreach (Action<NotificationEvent[]> subscriber in subscribers.GetInvocationList())
                    try { subscriber(delivery.ToArray()); } catch (Exception e) { notificationMessage = "Windows通知を要求できません: " + e.Message; }
            notificationReading = false;
            Reconcile(NotificationHistory, NotificationInbox.History.Select(e => new DeviceNotificationHistoryRow(e)).ToArray());
            Reconcile(ActiveNotifications, NotificationInbox.Feed?.Active ?? []); PublishNotifications();
        }
    }
}
