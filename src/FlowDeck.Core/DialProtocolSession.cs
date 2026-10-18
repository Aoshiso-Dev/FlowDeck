using System.Text;
using System.Text.Json;

namespace FlowDeck.Core;

public sealed record DialTarget(string Id, string Label, string Kind, int Value, int Minimum, int Maximum,
    int Step, bool Enabled, bool Busy, string Message, string Version, string ControlContext = "", bool ValueMixed = false, int MemberCount = 1,
    int? RpmMin = null, int? RpmMax = null, int RpmCount = 0);
public sealed record DialSnapshot(bool Available, DialTarget[] Targets, string Message = "",
    int? CpuTempDeciC = null, int? GpuTempDeciC = null, int? WaterTempDeciC = null);
public enum DialAdjustmentStatus { Confirmed, Saved, Unverified, Rejected }
public sealed record DialAdjustmentResult(DialAdjustmentStatus Status, string Message, string? ConfirmedVersion = null);
public interface IDialControlBackend
{
    DialSnapshot Read();
    Task<DialAdjustmentResult> AdjustAsync(string targetId, string expectedVersion, int value);
}

/// <summary>Single-dispatcher protocol. A state token authorizes only its exact current target/version.</summary>
public sealed class DialProtocolSession(IDialControlBackend backend, string? registeredDeviceId = null, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private string? registered = registeredDeviceId, boot, session, selected, signature;
    private string stateId = "", notice = "", operationId = "", ackStatus = "none";
    private string? activeContext;
    private uint sequence, ackSeq;
    private long publishedAt;
    private bool busy, responsive;
    public string? DeviceId { get; private set; }
    public string? Firmware { get; private set; }
    public string? SessionId => session;
    public bool Registered => DeviceId is not null && registered == DeviceId;
    public bool Busy => busy;
    public event Action<string>? Outgoing;
    public void Identify() => Send(new { v = 1, type = "identify" });
    public void Disconnect()
    {
        session = boot = selected = signature = DeviceId = Firmware = null;
        stateId = notice = operationId = ""; activeContext = null; sequence = ackSeq = 0; ackStatus = "none"; responsive = false;
    }
    public void RegisterObservedDevice()
    {
        if (DeviceId is null || session is null || busy) throw new InvalidOperationException("ダイヤルの機器IDを確認してください");
        registered = DeviceId; session = Guid.NewGuid().ToString("N"); signature = null; notice = ""; ResetAcknowledgement();
        Welcome(); Publish();
    }
    private void Welcome() => Send(new { v = 1, type = "welcome", sessionId = session, responsiveInput = responsive ? 1 : 0 });
    private void ResetAcknowledgement() { ackSeq = 0; ackStatus = "none"; operationId = Guid.NewGuid().ToString("N"); }
    private void Reject(uint number)
    {
        ackSeq = number; ackStatus = "rejected"; operationId = Guid.NewGuid().ToString("N"); Publish();
    }
    public async Task ReceiveAsync(string frame)
    {
        if (Encoding.UTF8.GetByteCount(frame) + 1 > 4096) return;
        try
        {
            using var document = JsonDocument.Parse(frame); var r = document.RootElement;
            if (r.GetProperty("v").GetInt32() != 1) return;
            var type = r.GetProperty("type").GetString();
            if (type == "hello")
            {
                if (r.GetProperty("deviceType").GetString() != "FlowDeckDial") return;
                var id = r.GetProperty("deviceId").GetString(); var nextBoot = r.GetProperty("bootId").GetString();
                var firmware = r.GetProperty("firmwareVersion").GetString();
                if (!IdentityText(id) || !IdentityText(nextBoot) || !IdentityText(firmware)) return;
                if (DeviceId != id || boot != nextBoot || session is null)
                {
                    // Never allow a second identity to take over an operation still in flight.
                    if (busy) return;
                    DeviceId = id; boot = nextBoot; sequence = 0; selected = signature = null;
                    session = Guid.NewGuid().ToString("N"); notice = "";
                    responsive = r.TryGetProperty("responsiveInput", out var capability) && capability.TryGetInt32(out var version) && version == 1;
                    ResetAcknowledgement();
                }
                Firmware = firmware; Welcome(); Publish(); return;
            }
            if (type != "input" || session is null || r.GetProperty("sessionId").GetString() != session) return;
            var number = r.GetProperty("seq").GetUInt32();
            if (number == 0 || number <= sequence) return;
            sequence = number;
            var oldState = stateId;
            var recent = signature is not null && clock.GetElapsedTime(publishedAt).TotalSeconds is >= 0 and < 3;
            var view = backend.Read(); var target = Refresh(view);
            if (!recent || !Registered || busy || target is null || !view.Available || target.Busy ||
                stateId != oldState || r.GetProperty("stateId").GetString() != stateId || r.GetProperty("targetId").GetString() != target.Id)
            { Reject(number); return; }
            var action = r.GetProperty("action").GetString();
            var delta = action == "set" ? 0 : r.GetProperty("delta").GetInt32();
            if (action == "press" && delta == 0)
            {
                var index = Array.FindIndex(view.Targets, t => t.Id == target.Id);
                selected = view.Targets[(index + 1) % view.Targets.Length].Id; signature = null; notice = "";
                ackSeq = number; ackStatus = "confirmed"; Publish(); return;
            }
            int value;
            if (!target.Enabled) { Reject(number); return; }
            if (action == "set" && responsive)
            {
                if (!r.TryGetProperty("value", out var desired) || !desired.TryGetInt32(out value) || value < target.Minimum || value > target.Maximum)
                { Reject(number); return; }
            }
            else if (action == "rotate" && delta != 0 && delta is >= -20 and <= 20 && !(target.Value < target.Minimum && delta < 0))
                value = (int)Math.Clamp((long)target.Value + (long)delta * target.Step, target.Minimum, target.Maximum);
            else { Reject(number); return; }
            if (value == target.Value && !target.ValueMixed) { ackSeq = number; ackStatus = target.Kind == "lighting" ? "saved" : "confirmed"; Publish(); return; }
            var acceptedSession = session; var acceptedOperation = operationId;
            activeContext = ContinuationContext(view); busy = true; notice = "適用中"; ackSeq = number; ackStatus = "pending";
            Publish(preserveOperation: true, snapshot: view);
            var result = new DialAdjustmentResult(DialAdjustmentStatus.Rejected, "操作結果を確認できません");
            try { result = await backend.AdjustAsync(target.Id, target.Version, value); }
            catch (Exception e) { result = new(DialAdjustmentStatus.Rejected, e is OperationCanceledException ? "操作を中断しました。状態を確認してください" : e.Message); }
            finally
            {
                busy = false;
                if (session == acceptedSession)
                {
                    var current = backend.Read(); var actual = current.Targets.SingleOrDefault(t => t.Id == target.Id);
                    var canContinue = (result.Status == DialAdjustmentStatus.Confirmed || result.Status == DialAdjustmentStatus.Saved && target.Kind == "lighting") && current.Available &&
                        actual is { Enabled: true, Busy: false, ValueMixed: false } && actual.Value == value && actual.Version == result.ConfirmedVersion &&
                        operationId == acceptedOperation && ContinuationContext(current) == activeContext;
                    if (!canContinue) operationId = Guid.NewGuid().ToString("N");
                    notice = selected == target.Id && actual?.MemberCount == target.MemberCount ? result.Message : ""; ackSeq = number;
                    // A completed operation can remain confirmed/saved even when an external change
                    // revokes the gesture. The changed operationId is authoritative for continuation.
                    ackStatus = result.Status switch { DialAdjustmentStatus.Confirmed => "confirmed", DialAdjustmentStatus.Saved => "saved",
                        DialAdjustmentStatus.Unverified => "unverified", _ => "rejected" };
                    activeContext = null; signature = null; Publish(preserveOperation: true, snapshot: current);
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
    }
    // A pending gesture belongs to the ordered catalog and the same physical/configuration context.
    // Values, policy revisions and busy flags may change as a consequence of this operation itself.
    private static string ContinuationContext(DialSnapshot view) => JsonSerializer.Serialize(new {
        view.Available, targets = view.Targets.Select(t => new { t.Id, t.ControlContext, t.Kind, t.Minimum, t.Maximum, t.Step, t.MemberCount }) });
    private DialTarget? Refresh(DialSnapshot view, bool preserveOperation = false)
    {
        var target = view.Targets.FirstOrDefault(t => t.Id == selected) ?? view.Targets.FirstOrDefault();
        if (target?.Id != selected) notice = "";
        if (target is not null) selected = target.Id;
        // Keep every control field and the ordered catalog. Display-only RPM must not revoke
        // input tokens, an in-flight gesture, or its confirmation message on each sensor update.
        var next = JsonSerializer.Serialize(new { view.Available, targets = view.Targets.Select(t => new {
            t.Id, t.Label, t.Kind, t.Value, t.Minimum, t.Maximum, t.Step, t.Enabled, t.Busy, t.Message,
            t.Version, t.ControlContext, t.ValueMixed, t.MemberCount }), view.Message, selected, registered = Registered, busy });
        if (signature != next)
        {
            signature = next; stateId = Guid.NewGuid().ToString("N");
            if (!preserveOperation && !busy) { operationId = Guid.NewGuid().ToString("N"); notice = ""; }
        }
        if (busy && activeContext is not null && ContinuationContext(view) != activeContext)
        { operationId = Guid.NewGuid().ToString("N"); activeContext = null; }
        return target;
    }
    public void Publish() => Publish(false);
    private void Publish(bool preserveOperation, DialSnapshot? snapshot = null)
    {
        if (session is null) return;
        // Completion must publish the snapshot whose version and continuation were checked.
        // A second read could otherwise attach the old gesture token to an intervening external edit.
        var view = snapshot ?? backend.Read(); var target = Refresh(view, preserveOperation);
        var message = !Registered ? registered is null ? "PCでこのダイヤルを登録してください" : "登録済みのダイヤルと機器IDが異なります" :
            !view.Available ? view.Message : target is null ? "操作できる接続済み機器がありません" :
            notice.Length > 0 && ackStatus is "rejected" or "unverified" ? notice :
            !target.Enabled || target.Busy ? target.Message : notice.Length > 0 ? notice : target.Message;
        Send(new { v = 1, type = "state", sessionId = session, stateId, targetId = target?.Id ?? "",
            label = Utf8Limit(target?.Label ?? "FlowDeck", 192), kind = target?.Kind ?? "cooling", value = target?.Value ?? 0,
            min = target?.Minimum ?? 0, max = target?.Maximum ?? 100, step = target?.Step ?? 1,
            valueMixed = target?.ValueMixed ?? false, memberCount = target?.MemberCount ?? 0,
            rpmMin = target?.RpmMin, rpmMax = target?.RpmMax, rpmCount = target?.RpmCount ?? 0,
            cpuTempDeciC = view.CpuTempDeciC, gpuTempDeciC = view.GpuTempDeciC, waterTempDeciC = view.WaterTempDeciC,
            index = target is null ? 0 : Array.FindIndex(view.Targets, t => t.Id == target.Id) + 1, count = view.Targets.Length,
            enabled = Registered && view.Available && target?.Enabled == true && !busy && !target.Busy,
            busy = busy || target?.Busy == true, message = Utf8Limit(message, 256),
            ackSeq, ackStatus, operationId });
        publishedAt = clock.GetTimestamp();
    }
    public static bool IdentityText(string? value) => value is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static string Utf8Limit(string value, int bytes)
    {
        var result = new StringBuilder(); var length = 0;
        foreach (var source in value.EnumerateRunes())
        {
            var rune = Rune.IsControl(source) ? new Rune(' ') : source;
            if (length + rune.Utf8SequenceLength > bytes) break;
            result.Append(rune); length += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
    private void Send(object value)
    {
        var json = JsonSerializer.Serialize(value, Contract.Json);
        if (Encoding.UTF8.GetByteCount(json) + 1 <= 4096) Outgoing?.Invoke(json);
    }
}
