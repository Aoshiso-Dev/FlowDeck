namespace FlowDeck.Core;

public record NotificationAddress(string Source, string DeviceId, string SensorId, string Kind);
public record NotificationRule(string Id, NotificationAddress Address, string Name, double Threshold,
    double Hysteresis = 0, bool Enabled = true, bool Missing = true, bool IgnoreStopped = true);
public record NotificationSettings(int Version = 1, bool Enabled = true, bool Desktop = true, bool Recovery = true,
    bool ControlFaults = true, bool Disconnects = true, bool OperationFailures = true,
    int DelaySeconds = 5, int RecoverySeconds = 5, int RepeatSeconds = 0, NotificationRule[]? Rules = null);
public record NotificationSample(NotificationAddress Address, string Name, double? Value, bool IntentionalStop = false);
public record NotificationSignal(string Key, string Name, bool? Abnormal, string Message, bool Immediate = false);
public record NotificationEvent(string Key, string Name, string Kind, string Message, DateTimeOffset Time);

public static class Notifications
{
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    public static bool ValidAddress(NotificationAddress? a) => a is not null && Text(a.DeviceId,2048) && Text(a.SensorId,256) &&
        a.Kind is "temperature" or "fan" && a.Source switch
        {
            "sensor" => a.Kind == "temperature" && a.DeviceId is "cpu" or "gpu",
            "nzxt" => a.Kind == "fan" && a.SensorId is "fan1" or "fan2" or "fan3" or "fan4" or "fan5",
            "motherboard" => true, _ => false
        };
    public static bool ValidRule(NotificationRule? r) => r is not null && Guid.TryParseExact(r.Id,"N",out var id) && id.ToString("N") == r.Id &&
        ValidAddress(r.Address) && Text(r.Name,64) && double.IsFinite(r.Threshold) && double.IsFinite(r.Hysteresis) && r.Hysteresis >= 0 &&
        (r.Address.Kind == "temperature" ? r.Threshold is > -40 and < 150 && r.Hysteresis <= 40 && r.Threshold - r.Hysteresis > -40 :
            r.Threshold is >= 1 and <= 30000 && r.Hysteresis <= 10000 && r.Threshold + r.Hysteresis <= 30000);
    public static string? Validate(NotificationSettings? s) => s is null || s.Version != 1 || s.DelaySeconds is < 0 or > 300 ||
        s.RecoverySeconds is < 0 or > 300 || s.RepeatSeconds is < 0 or > 86400 || s.RepeatSeconds is > 0 and < 30 ||
        (s.Rules ?? []).Length > 256 || (s.Rules ?? []).Any(r => !ValidRule(r)) ||
        (s.Rules ?? []).DistinctBy(r => r.Id).Count() != (s.Rules ?? []).Length ||
        (s.Rules ?? []).DistinctBy(r => r.Address).Count() != (s.Rules ?? []).Length ? "通知の対象・しきい値・時間が不正または重複しています" : null;
}

// Pure notification state machine: it has no reference to a controller or hardware writer.
public sealed class NotificationEngine(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private sealed class Cell(string signature)
    {
        public string Signature = signature;
        public bool Active;
        public long? Since, NormalSince, LastSent;
        public NotificationEvent? Event;
    }
    private readonly Dictionary<string,Cell> cells = [];
    private long? previousTick;
    public NotificationEvent[] Active => cells.Values.Where(c => c.Active && c.Event is not null).Select(c => c.Event!).ToArray();
    public void Reset() { cells.Clear(); previousTick = null; }
    internal void Forget(string key) => cells.Remove(key);
    public NotificationEvent[] Evaluate(NotificationSettings settings, NotificationSample[] samples, NotificationSignal[] signals)
    {
        if (Notifications.Validate(settings) is { } invalid) throw new ArgumentException(invalid);
        if (!settings.Enabled) { Reset(); return []; }
        var now = clock.GetTimestamp(); var result = new List<NotificationEvent>(); var seen = new HashSet<string>();
        // A long scheduler/sleep gap is not evidence that a condition held continuously.
        if (previousTick is { } previous && clock.GetElapsedTime(previous,now).TotalSeconds is < 0 or > 3)
            foreach (var cell in cells.Values) { cell.Since = null; cell.NormalSince = null; }
        previousTick = now;
        void Observe(string key, string name, bool? abnormal, string message, string signature, int delay)
        {
            seen.Add(key); signature += $"/{delay}/{settings.RecoverySeconds}/{settings.RepeatSeconds}";
            if (!cells.TryGetValue(key, out var cell) || cell.Signature != signature) cells[key] = cell = new(signature);
            if (abnormal is null)
            {
                cell.Since = null; cell.NormalSince = null;
                if(cell.Active && cell.Event is not null) cell.Event = cell.Event with { Message = name + " · 値未取得（警告を継続）" };
                return;
            }
            if (abnormal.Value)
            {
                cell.NormalSince = null; cell.Since ??= now;
                if (!cell.Active && clock.GetElapsedTime(cell.Since.Value,now).TotalSeconds >= delay)
                {
                    cell.Active = true; cell.LastSent = now; cell.Event = new(key,name,"raised",message,clock.GetUtcNow()); result.Add(cell.Event);
                }
                else if (cell.Active)
                {
                    cell.Event = cell.Event! with { Name = name, Message = message };
                    if (settings.RepeatSeconds > 0 && cell.LastSent is { } sent && clock.GetElapsedTime(sent,now).TotalSeconds >= settings.RepeatSeconds)
                    { cell.LastSent = now; result.Add(cell.Event with { Kind = "reminder", Time = clock.GetUtcNow() }); }
                }
            }
            else
            {
                cell.Since = null; cell.NormalSince ??= now;
                if (cell.Active && clock.GetElapsedTime(cell.NormalSince.Value,now).TotalSeconds >= settings.RecoverySeconds)
                {
                    cell.Active = false; cell.LastSent = null;
                    if (settings.Recovery) result.Add(new(key,name,"recovered",name + " · 復帰しました",clock.GetUtcNow()));
                    cell.Event = null;
                }
            }
        }
        foreach (var rule in (settings.Rules ?? []).Where(r => r.Enabled))
        {
            var sample = samples.FirstOrDefault(s => s.Address == rule.Address);
            var value = MotherboardDisplay.IsUsable(rule.Address.Kind,sample?.Value) ? sample!.Value : null;
            var signature = System.Text.Json.JsonSerializer.Serialize(new { rule.Address,rule.Threshold,rule.Hysteresis,rule.IgnoreStopped },Contract.Json);
            var active = cells.TryGetValue(rule.Id + ":limit",out var cell) && cell.Signature == signature + $"/{settings.DelaySeconds}/{settings.RecoverySeconds}/{settings.RepeatSeconds}" && cell.Active;
            var stopped = rule.IgnoreStopped && sample?.IntentionalStop == true && value == 0;
            bool? abnormal = value is null ? null : !stopped && (rule.Address.Kind == "temperature" ?
                active && rule.Hysteresis > 0 ? value > rule.Threshold - rule.Hysteresis : value >= rule.Threshold :
                active ? value < rule.Threshold + rule.Hysteresis : value < rule.Threshold);
            var unit = rule.Address.Kind == "temperature" ? "°C" : "RPM";
            var message = $"{rule.Name} · {value:0.#} {unit}（{rule.Threshold:0.#} {unit}{(rule.Address.Kind == "temperature" ? "以上" : "未満")}）";
            Observe(rule.Id + ":limit",rule.Name,abnormal,message,signature,settings.DelaySeconds);
            if (rule.Missing) Observe(rule.Id + ":missing",rule.Name,value is null,rule.Name + " · 値を取得できません",signature,settings.DelaySeconds);
        }
        foreach (var signal in signals)
            Observe("system:" + signal.Key,signal.Name,signal.Abnormal,signal.Message,signal.Key,signal.Immediate ? 0 : settings.DelaySeconds);
        foreach (var key in cells.Keys.Where(k => !seen.Contains(k)).ToArray()) cells.Remove(key);
        return result.ToArray();
    }
}
