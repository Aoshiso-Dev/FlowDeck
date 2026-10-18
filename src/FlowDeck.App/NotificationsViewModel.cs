using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed class NotificationTargetChoice(NotificationAddress address,string name,string status)
{
    public NotificationAddress Address { get; } = address;
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Label => Name + " · " + (Address.Source == "nzxt" ? "NZXT" : Address.Source == "motherboard" ? "マザー" : Address.DeviceId.ToUpperInvariant()) + " · " + Status;
}
public sealed class NotificationRuleChoice(NotificationRule rule)
{
    public NotificationRule Rule { get; } = rule;
    public string Label => (Rule.Enabled ? "" : "無効 · ") + Rule.Name + $" · {Rule.Threshold:0.#}" + (Rule.Address.Kind == "temperature" ? "°C以上" : " RPM未満");
}
public sealed class NotificationRepeatChoice(int seconds,string name)
{
    public int Seconds { get; } = seconds;
    public string Name { get; } = name;
}
public record NotificationHistoryRow(NotificationEvent Event)
{
    public DateTime Time => Event.Time.LocalDateTime;
    public string Message => Event.Message;
    public string Kind => Event.Kind switch { "recovered" => "復帰", "reminder" => "再通知", "test" => "テスト", _ => "警告" };
}
public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<NotificationTargetChoice> NotificationTargets { get; } = [];
    public ObservableCollection<NotificationRuleChoice> NotificationRules { get; } = [];
    public ObservableCollection<NotificationHistoryRow> NotificationHistory { get; } = [];
    public ObservableCollection<NotificationEvent> ActiveNotifications { get; } = [];
    public ObservableCollection<NotificationRepeatChoice> NotificationRepeatChoices { get; } = [];
    private NotificationTargetChoice? notificationTarget;
    private NotificationRuleChoice? notificationRule;
    private bool rebuildingNotifications, notificationBusy;
    private string notificationResult = "", notificationName = "", notificationTargetSignature = "";
    private double notificationThreshold = 85, notificationHysteresis = 2;
    private int notificationRepeat;
    public bool NotificationsEnabled { get; set; }
    public bool NotificationsDesktop { get; set; }
    public bool NotificationsRecovery { get; set; }
    public bool NotificationsFaults { get; set; }
    public bool NotificationsDisconnects { get; set; }
    public bool NotificationsOperations { get; set; }
    public int NotificationDelay { get; set; } = 5;
    public int NotificationRecoveryDelay { get; set; } = 5;
    public int NotificationRepeat { get => notificationRepeat; set { if(Set(ref notificationRepeat,value)) Notify(nameof(SelectedNotificationRepeat)); } }
    public NotificationRepeatChoice? SelectedNotificationRepeat
    {
        get => NotificationRepeatChoices.FirstOrDefault(c=>c.Seconds==notificationRepeat);
        set { if(value is not null) NotificationRepeat = value.Seconds; }
    }
    public bool NotificationRuleEnabled { get; set; } = true;
    public bool NotificationMissing { get; set; } = true;
    public bool NotificationIgnoreStop { get; set; } = true;
    public string NotificationName { get => notificationName; set => Set(ref notificationName,value); }
    public double NotificationThreshold { get => notificationThreshold; set { if(double.IsFinite(value) && Set(ref notificationThreshold,value)) Notify(nameof(NotificationThresholdLabel)); } }
    public double NotificationHysteresis { get => notificationHysteresis; set { if(double.IsFinite(value) && Set(ref notificationHysteresis,value)) Notify(nameof(NotificationHysteresisLabel)); } }
    public bool NotificationIsFan => notificationTarget?.Address.Kind == "fan";
    public double NotificationThresholdMinimum => NotificationIsFan ? 1 : -39;
    public double NotificationThresholdMaximum => NotificationIsFan ? 30000 : 149;
    public double NotificationHysteresisMaximum => NotificationIsFan ? 10000 : 40;
    public string NotificationThresholdLabel => $"{NotificationThreshold:0.#}" + (NotificationIsFan ? " RPM未満" : "°C以上");
    public string NotificationHysteresisLabel => $"{NotificationHysteresis:0.#}" + (NotificationIsFan ? " RPM" : "°C");
    public string NotificationSummary => !host.NotificationOptions.Enabled ? "無効" : ActiveNotifications.Count == 0 ? "通知なし" : $"通知中 · {ActiveNotifications.Count}件";
    public string NotificationResult => host.NotificationError ?? host.NotificationRuntimeError ?? notificationResult;
    public bool NotificationIdle => !notificationBusy;
    public bool CanSaveNotification => NotificationIdle && host.NotificationError is null;
    public NotificationTargetChoice? NotificationTarget
    {
        get => notificationTarget;
        set
        {
            if(rebuildingNotifications || !Set(ref notificationTarget,value)) return;
            if(value is not null)
            {
                NotificationName = value.Name;
                notificationThreshold = value.Address.Kind == "fan" ? 300 : 85;
                notificationHysteresis = value.Address.Kind == "fan" ? 50 : 2;
            }
            Refresh();
        }
    }
    public NotificationRuleChoice? NotificationRule
    {
        get => notificationRule;
        set
        {
            if(rebuildingNotifications || !Set(ref notificationRule,value)) return;
            if(value?.Rule is { } rule)
            {
                EnsureNotificationTarget(rule); notificationTarget = NotificationTargets.Single(t => t.Address == rule.Address);
                NotificationName = rule.Name; notificationThreshold = rule.Threshold; notificationHysteresis = rule.Hysteresis;
                NotificationRuleEnabled = rule.Enabled; NotificationMissing = rule.Missing; NotificationIgnoreStop = rule.IgnoreStopped;
            }
            Refresh();
        }
    }
    public ICommand SaveNotificationSettings { get; private set; } = null!;
    public ICommand ReloadNotifications { get; private set; } = null!;
    public ICommand NewNotificationRule { get; private set; } = null!;
    public ICommand SaveNotificationRule { get; private set; } = null!;
    public ICommand DeleteNotificationRule { get; private set; } = null!;
    public ICommand TestNotification { get; private set; } = null!;
    private void InitializeNotifications()
    {
        async Task Edit(Func<Task> action)
        {
            notificationBusy = true; Refresh();
            try { await action(); }
            catch(Exception e) { notificationResult = e.Message; throw; }
            finally { notificationBusy = false; RefreshNotifications(); Refresh(); }
        }
        SaveNotificationSettings = Command(_ => Edit(async () =>
        {
            await host.SaveNotificationsAsync(host.NotificationOptions with { Enabled = NotificationsEnabled,Desktop = NotificationsDesktop,Recovery = NotificationsRecovery,
                ControlFaults = NotificationsFaults,Disconnects = NotificationsDisconnects,OperationFailures = NotificationsOperations,
                DelaySeconds = NotificationDelay,RecoverySeconds = NotificationRecoveryDelay,RepeatSeconds = NotificationRepeat });
            notificationResult = "保存済み";
        }),_ => CanSaveNotification);
        ReloadNotifications = Command(_ => Edit(async () => { await host.LoadNotificationsAsync(); LoadNotificationSettings(); notificationResult = "再読込済み"; }),_ => NotificationIdle);
        NewNotificationRule = Command(_ =>
        {
            NotificationRule = null; notificationTarget = null; NotificationTarget = NotificationTargets.FirstOrDefault();
            NotificationRuleEnabled = NotificationMissing = NotificationIgnoreStop = true;
            notificationResult = "新規条件"; Refresh(); return Task.CompletedTask;
        },_ => NotificationIdle);
        SaveNotificationRule = Command(_ => Edit(async () =>
        {
            var rule = new NotificationRule(NotificationRule?.Rule.Id ?? Guid.NewGuid().ToString("N"),NotificationTarget!.Address,NotificationName.Trim(),
                NotificationThreshold,NotificationHysteresis,NotificationRuleEnabled,NotificationMissing,NotificationIgnoreStop);
            var rules = (host.NotificationOptions.Rules ?? []).ToList(); var index = rules.FindIndex(r => r.Id == rule.Id);
            if(index < 0) rules.Add(rule); else rules[index] = rule;
            await host.SaveNotificationsAsync(host.NotificationOptions with { Rules = rules.ToArray() });
            LoadNotificationRules(rule.Id); notificationResult = "条件を保存済み";
        }),_ => CanSaveNotification && NotificationTarget is not null && !string.IsNullOrWhiteSpace(NotificationName));
        DeleteNotificationRule = Command(_ => Edit(async () =>
        {
            await host.SaveNotificationsAsync(host.NotificationOptions with { Rules = (host.NotificationOptions.Rules ?? []).Where(r => r.Id != NotificationRule!.Rule.Id).ToArray() });
            LoadNotificationRules(); notificationResult = "条件を削除済み";
        }),_ => CanSaveNotification && NotificationRule is not null);
        TestNotification = Command(_ => { host.TestNotification(); notificationResult = Demo ? "デモ通知を記録済み" : "Windows通知を要求済み"; RefreshNotifications(); Refresh(); return Task.CompletedTask; },
            _ => NotificationIdle && host.NotificationOptions.Enabled && host.NotificationOptions.Desktop);
    }
    private void EnsureNotificationTarget(NotificationRule rule)
    {
        if(NotificationTargets.All(t => t.Address != rule.Address)) NotificationTargets.Add(new(rule.Address,rule.Name,"未取得"));
    }
    private void LoadNotificationRules(string? select = null)
    {
        rebuildingNotifications = true;
        try { NotificationRules.Clear(); foreach(var rule in host.NotificationOptions.Rules ?? []) { NotificationRules.Add(new(rule)); EnsureNotificationTarget(rule); } notificationRule = null; }
        finally { rebuildingNotifications = false; }
        NotificationRule = NotificationRules.FirstOrDefault(r => r.Rule.Id == select); Refresh();
    }
    private void LoadNotificationSettings()
    {
        var s = host.NotificationOptions; NotificationsEnabled = s.Enabled; NotificationsDesktop = s.Desktop; NotificationsRecovery = s.Recovery;
        NotificationsFaults = s.ControlFaults; NotificationsDisconnects = s.Disconnects; NotificationsOperations = s.OperationFailures;
        NotificationDelay = s.DelaySeconds; NotificationRecoveryDelay = s.RecoverySeconds; NotificationRepeat = s.RepeatSeconds;
        NotificationRepeatChoices.Clear();
        foreach(var seconds in new[] {0,60,300,900,3600,s.RepeatSeconds}.Distinct().Order()) NotificationRepeatChoices.Add(new(seconds,seconds == 0 ? "なし" : $"{seconds / 60d:0.#} 分ごと"));
        notificationTargetSignature = ""; NotificationTargets.Clear(); notificationTarget = null; RefreshNotifications();
        LoadNotificationRules(); NotificationTarget = NotificationTargets.FirstOrDefault(); notificationResult = ""; Refresh();
    }
    private void RefreshNotifications()
    {
        Reconcile(NotificationHistory,host.NotificationHistory.Select(e => new NotificationHistoryRow(e)).ToArray());
        Reconcile(ActiveNotifications,host.ActiveNotifications);
        var samples = host.NotificationSamples();
        var targets = samples.Select(s => new NotificationTargetChoice(s.Address,s.Name,s.Value is null ? "未取得" : s.Address.Kind == "fan" ? "RPM" : "温度")).ToList();
        foreach(var rule in host.NotificationOptions.Rules ?? []) if(targets.All(t => t.Address != rule.Address)) targets.Add(new(rule.Address,rule.Name,"未取得"));
        if(notificationTarget is { } oldTarget && targets.All(t => t.Address != oldTarget.Address)) targets.Add(new(oldTarget.Address,oldTarget.Name,"未取得"));
        var signature = System.Text.Json.JsonSerializer.Serialize(targets,Contract.Json);
        if(signature != notificationTargetSignature)
        {
            notificationTargetSignature = signature; var selected = notificationTarget?.Address; rebuildingNotifications = true;
            try { NotificationTargets.Clear(); foreach(var target in targets) NotificationTargets.Add(target); }
            finally { rebuildingNotifications = false; }
            notificationTarget = NotificationTargets.FirstOrDefault(t => t.Address == selected); Notify(nameof(NotificationTarget));
        }
        Notify(nameof(NotificationSummary)); Notify(nameof(NotificationResult));
    }
}
