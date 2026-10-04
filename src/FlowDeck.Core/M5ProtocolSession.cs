using System.Text;
using System.Text.Json;

namespace FlowDeck.Core;

/// <summary>Transport-independent protocol; only stateApplied establishes display synchronization.</summary>
public sealed class M5ProtocolSession(IM5ControlBackend coordinator, string? expectedDeviceId = null, string layout = "A", TimeProvider? time = null,
    Func<BoardPanel>? motherboard = null, IM5LightingBackend? lighting = null)
{
    private readonly object sync = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Dictionary<long, (long Fan, long Light)> sent = new();
    private readonly Dictionary<string, (object Request, long Sequence, Task<OperationResult> Task, long Timestamp)> commands = new();
    private CancellationTokenSource sessionStop = new();
    private bool lightingCapable;
    private string lightingScope = "all";
    private long lightingSequence;
    private bool bodyLedCapable;
    private long bodyLedSequence;
    private string? session, boot;
    private long lastReceive, highestCommand;
    private long ackFan = -1, ackLight = -1;
    private bool motherboardCapable;
    private long boardSequence;
    public string? DeviceId { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public string? SessionId { get { lock (sync) return session; } }
    public bool Connected { get { lock (sync) return session is not null && clock.GetElapsedTime(lastReceive).TotalSeconds < 3; } }
    public bool Synchronized { get { lock (sync) { var s = coordinator.Snapshot; return Connected && ackFan == s.ControlRevision && ackLight == s.LightingRevision; } } }
    public event Action<string>? Outgoing;
    public event Action? ConnectionChanged;
    public void Disconnect()
    {
        lock (sync) { sessionStop.Cancel(); session = null; boot = null; motherboardCapable = lightingCapable = bodyLedCapable = false; bodyLedSequence = boardSequence = lightingSequence = 0; lightingScope = "all"; commands.Clear(); sent.Clear(); highestCommand = 0; ackFan = ackLight = -1; }
        ConnectionChanged?.Invoke();
    }
    public void Tick()
    {
        if (!Connected) { Disconnect(); Send(new { v = 1, type = "helloRequest", deviceType = "M5GO" }); return; }
        Send(new { v = 1, type = "heartbeat", sessionId = SessionId, controllerEpoch = coordinator.Epoch });
        SendState();
    }
    public void Receive(string frame)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(frame) + 1 > 4096) return;
            using var document = JsonDocument.Parse(frame); var r = document.RootElement;
            if (r.GetProperty("v").GetInt32() != 1) return;
            var type = r.GetProperty("type").GetString();
            lock (sync)
            {
                if (type == "hello")
                {
                    if (r.GetProperty("deviceType").GetString() != "M5GO") return;
                    var id = r.GetProperty("deviceId").GetString(); var nextBoot = r.GetProperty("bootId").GetString();
                    var firmware = r.GetProperty("firmwareVersion").GetString();
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || string.IsNullOrWhiteSpace(nextBoot) || nextBoot.Length > 64 ||
                        string.IsNullOrWhiteSpace(firmware) || firmware.Length > 64 || expectedDeviceId is not null && id != expectedDeviceId) return;
                    if (boot != nextBoot || !Connected)
                    {
                        sessionStop.Cancel(); sessionStop.Dispose(); sessionStop = new(); lightingScope = "all"; bodyLedSequence = lightingSequence = 0;
                        session = Guid.NewGuid().ToString("N"); boot = nextBoot; commands.Clear(); sent.Clear(); highestCommand = 0; ackFan = ackLight = -1;
                    }
                    DeviceId = id; FirmwareVersion = firmware; lastReceive = clock.GetTimestamp();
                    motherboardCapable = r.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array &&
                        capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "motherboard-v1");
                    lightingCapable = lighting is not null && capabilities.ValueKind == JsonValueKind.Array &&
                        capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "lighting-v1");
                    bodyLedCapable = lightingCapable && lighting is IM5BodyLedBackend &&
                        capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "body-led-v1");
                    Send(new { v = 1, type = "welcome", sessionId = session, controllerEpoch = coordinator.Epoch, layout, protocol = 1,
                        capabilities = bodyLedCapable ? new[] { "lighting-v1", "body-led-v1" } : lightingCapable ? new[] { "lighting-v1" } : Array.Empty<string>() });
                    SendState(); ConnectionChanged?.Invoke(); return;
                }
                if (session is null || r.GetProperty("sessionId").GetString() != session || r.GetProperty("controllerEpoch").GetString() != coordinator.Epoch) return;
                if (!Connected) return;
                switch (type)
                {
                    case "heartbeat": break;
                    case "getState": SendState(); break;
                    case "getLightingState":
                        if (!lightingCapable || !TryScope(r, out var scope)) return;
                        lightingScope = scope; SendLightingState(); break;
                    case "getLightingCatalog":
                        if (!SendLightingCatalog(r)) return;
                        break;
                    case "stateApplied":
                        var n = r.GetProperty("snapshotSequence").GetInt64();
                        var fan = r.GetProperty("controlRevision").GetInt64(); var light = r.GetProperty("lightingRevision").GetInt64();
                        if (!sent.TryGetValue(n, out var revisions) || revisions != (fan, light)) return;
                        ackFan = Math.Max(ackFan, fan); ackLight = Math.Max(ackLight, light); ConnectionChanged?.Invoke(); break;
                    case "getResult":
                        var lookup = r.GetProperty("id").GetString()!;
                        Expire();
                        if (commands.TryGetValue(lookup, out var known)) Reply(lookup, known.Task, session);
                        else SendResult(lookup, "unknown", "結果キャッシュにありません。状態を確認してください", "RESULT_EXPIRED", session);
                        break;
                    case "command": if (!Command(r)) return; break;
                    default: return;
                }
                lastReceive = clock.GetTimestamp();
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException) { }
    }
    private void Expire()
    {
        foreach (var id in commands.Where(c => c.Value.Task.IsCompleted && clock.GetElapsedTime(c.Value.Timestamp).TotalMinutes > 10).Select(c => c.Key).ToArray()) commands.Remove(id);
    }
    private bool Command(JsonElement r)
    {
        var id = r.GetProperty("id").GetString();
        var number = r.GetProperty("commandSequence").GetInt64();
        var revision = r.GetProperty("expectedRevision").GetInt64();
        var name = r.GetProperty("command").GetString();
        var payload = r.GetProperty("payload");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || number < 1 || revision < 0 || payload.ValueKind != JsonValueKind.Object ||
            name is not ("SetManualDuty" or "SelectAutoProfile" or "SetLighting" or "ResumeControl") && !(lightingCapable && M5LightingCommand.IsCommand(name))) return false;
        object request;
        if (M5LightingCommand.IsCommand(name))
        {
            var next = new M5LightingCommand($"{session}/{id}", name!, payload.GetProperty("scopeId").GetString()!,
                payload.GetProperty("expectedStamp").GetString() ?? "",
                name == "ApplyLightingScene" ? payload.GetProperty("sceneId").GetString() : null,
                name == "SetLightingBrightness" ? payload.GetProperty("brightness").GetInt32() : null);
            if (!next.Valid) return false;
            request = next;
        }
        else request = new ControlRequest($"{session}/{id}", "M5GO", coordinator.Epoch, revision, name!,
            name == "SetManualDuty" ? payload.GetProperty("duty").GetInt32() : null,
            name == "SelectAutoProfile" ? payload.GetProperty("profileId").GetString() : null,
            name == "SelectAutoProfile" ? payload.GetProperty("profileRevision").GetInt64() : null,
            name == "SetLighting" ? payload.GetProperty("preset").GetString() : null);
        Expire();
        if (commands.TryGetValue(id, out var known))
        {
            if (Equals(known.Request, request) && known.Sequence == number) Reply(id, known.Task, session!);
            else SendResult(id, "rejected", "同じIDの内容が異なります", "DUPLICATE_ID", session!);
            return true;
        }
        if (number <= highestCommand) { SendResult(id, "unknown", "過去の要求番号は再実行しません", "OLD_SEQUENCE", session!); return true; }
        if (commands.Count >= 256)
        {
            var old = commands.Where(c => c.Value.Task.IsCompleted).OrderBy(c => c.Value.Timestamp).FirstOrDefault();
            if (old.Key is not null) commands.Remove(old.Key);
            else { SendResult(id, "rejected", "処理中の要求が多すぎます", "BUSY", session!); return true; }
        }
        highestCommand = number;
        var token = sessionStop.Token;
        var task = ExecuteSafelyAsync(request, token);
        commands[id] = (request, number, task, clock.GetTimestamp());
        if (task.IsCompleted) Reply(id, task, session!);
        else
        {
            Send(new { v = 1, type = "accepted", id, sessionId = session, controllerEpoch = coordinator.Epoch, outcome = "accepted" });
            var acceptedSession = session!;
            _ = Task.Run(async () => { await task; lock (sync) if (session == acceptedSession && Connected) { Reply(id, task, acceptedSession); SendState(); } });
        }
        return true;
    }
    private void Reply(string id, Task<OperationResult> task, string expectedSession)
    {
        if (!task.IsCompleted) { Send(new { v = 1, type = "accepted", id, sessionId = expectedSession, controllerEpoch = coordinator.Epoch, outcome = "pending" }); return; }
        if (!task.IsCompletedSuccessfully) { SendResult(id, "unknown", "処理結果を確認できません。状態を確認してください", "BACKEND_ERROR", expectedSession); return; }
        var result = task.Result;
        Send(new { v = 1, type = "result", id, sessionId = expectedSession, controllerEpoch = coordinator.Epoch,
            outcome = result.Outcome, message = DisplayText(result.Message, 96), error = DisplayText(result.ErrorCode, 64),
            channels = result.Channels?.Select(c => c with { Error = DisplayText(c.Error, 64) }) });
    }
    private async Task<OperationResult> ExecuteSafelyAsync(object request, CancellationToken token)
    {
        try
        {
            return request is ControlRequest old ? await coordinator.RequestFromM5Async(old, token) :
                await Task.Run(() => lighting!.ExecuteAsync((M5LightingCommand)request, token), CancellationToken.None);
        }
        catch (Exception e)
        {
            var s = coordinator.Snapshot;
            return new(request is ControlRequest old ? old.Id : ((M5LightingCommand)request).Id, "M5GO",
                request is ControlRequest c ? c.Command : ((M5LightingCommand)request).Command,
                e is OperationCanceledException ? Outcome.Rejected : Outcome.Unknown, "処理結果を確認できません。状態を確認してください",
                clock.GetUtcNow(), s.ControlRevision, s.LightingRevision, s.DeviceGeneration, ErrorCode: "BACKEND_ERROR");
        }
    }
    private static bool TryScope(JsonElement r, out string scope)
    {
        scope = r.GetProperty("scopeId").GetString() ?? "";
        return M5LightingCommand.ValidScope(scope);
    }
    private void SendLightingState()
    {
        if (!lightingCapable || lighting is null) return;
        var view = lighting.Read(lightingScope);
        Send(new { v = 1, type = "lightingState", sessionId = session, controllerEpoch = coordinator.Epoch,
            lightingSequence = ++lightingSequence, stamp = view.Stamp, scopeId = lightingScope,
            scopeName = DisplayText(view.ScopeName, 40), ready = view.Ready, busy = view.Busy,
            error = DisplayText(view.Error, 96), brightness = view.Brightness, off = view.Off,
            canRestore = view.CanRestore, synchronized = view.Synchronized,
            groupCount = view.Groups.Length, sceneCount = view.Scenes.Length });
        if (bodyLedCapable && lighting is IM5BodyLedBackend mirror)
        {
            var body = mirror.ReadBodyLed(lightingScope);
            var frame = body.Frame is { Valid: true } valid ? valid : null;
            Send(new { v = 1, type = "bodyLedState", sessionId = session, controllerEpoch = coordinator.Epoch,
                bodyLedSequence = ++bodyLedSequence, scopeId = lightingScope, ready = frame is not null,
                frame, error = DisplayText(body.Error, 64) });
        }
    }
    private bool SendLightingCatalog(JsonElement r)
    {
        if (!lightingCapable || lighting is null || !TryScope(r, out var scope)) return false;
        var kind = r.GetProperty("kind").GetString(); var page = r.GetProperty("page").GetInt32();
        var requestId = r.GetProperty("requestId").GetString();
        if (kind is not ("groups" or "scenes") || page is < 0 or > 4 || string.IsNullOrEmpty(requestId) || requestId.Length > 32) return false;
        var view = lighting.Read(scope); var items = kind == "groups" ? view.Groups : view.Scenes;
        Send(new { v = 1, type = "lightingCatalog", sessionId = session, controllerEpoch = coordinator.Epoch,
            requestId, kind, scopeId = scope, page, total = items.Length, stamp = view.Stamp,
            items = items.Skip(page * 8).Take(8).Select(i => new M5LightingItem(i.Id, DisplayText(i.Name, 40)!)) });
        return true;
    }
    private void SendResult(string id, string outcome, string message, string error, string expectedSession)
        => Send(new { v = 1, type = "result", id, sessionId = expectedSession, controllerEpoch = coordinator.Epoch, outcome, message, error });
    public void SendState()
    {
        lock (sync)
        {
            if (!Connected) return;
            var view = coordinator.ReadM5State(); var s = view.Snapshot; var settings = view.Settings;
            object Temperature(string? id)
            {
                var reading = s.Sensors.SingleOrDefault(r => r.Id == id);
                return new { celsius = reading?.Celsius, valid = reading?.Celsius is not null,
                    ageMs = reading is null ? (double?)null : Math.Min(999999, clock.GetElapsedTime(reading.Timestamp).TotalMilliseconds) };
            }
            var data = new
            {
                v = 1, type = "state", sessionId = session, controllerEpoch = s.ControllerEpoch,
                snapshotSequence = s.Sequence, controlRevision = s.ControlRevision, lightingRevision = s.LightingRevision,
                hub = new { state = s.HubReady ? "ready" : "disconnected", deviceGeneration = s.DeviceGeneration },
                fan = new { mode = s.Mode, profileId = s.LastConfirmed?.Profile?.Id, requestedDuty = s.TargetDuty,
                    confirmedDuty = s.ConfirmedDuty, confirmation = s.Confirmation, channels = s.Channels,
                    busy = s.FanBusy, minimum = settings.Limits.Minimum, allowStop = settings.Limits.AllowStop,
                    ready = s.Readiness == "制御可能" && !s.Faulted },
                temperature = new { cpu = Temperature(settings.CpuSensorId), gpu = Temperature(settings.GpuSensorId) },
                lighting = new { preset = s.Lighting.LastSent, confirmation = s.Lighting.Outcome, previousConnection = s.Lighting.Generation != s.DeviceGeneration,
                    ready = s.HubReady && settings.Hardware.Leds is { Length: > 0 }, busy = s.LightingBusy },
                profiles = settings.Profiles.Select(p => new { id = p.Id, name = p.Name, profileRevision = p.Revision,
                    ready = view.ReadyProfiles.Contains(p.Id) }),
                recovery = new { faulted = s.Faulted, canResume = s.CanResume, reason = DisplayText(s.Fault, 120) },
                telemetryAgeMs = s.TelemetryTimestamp is { } at ? (double?)Math.Min(999999, clock.GetElapsedTime(at).TotalMilliseconds) : null,
                operation = s.Operation is null ? null : new { id = s.Operation.Id, outcome = s.Operation.Outcome }, layout
            };
            sent[s.Sequence] = (s.ControlRevision, s.LightingRevision);
            while (sent.Count > 64) sent.Remove(sent.Keys.Min());
            Send(data);
            if (motherboardCapable && motherboard is not null)
            {
                var panel = motherboard();
                Send(new { v = 1, type = "motherboardState", sessionId = session, controllerEpoch = coordinator.Epoch,
                    boardSequence = ++boardSequence, name = panel.Name, fresh = panel.Fresh, ageMs = panel.AgeMs, status = panel.Status, rows = panel.Rows });
            }
            SendLightingState();
        }
    }
    private static string? DisplayText(string? text, int length)
    {
        if (text is null || text.Length <= length) return text;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
    private void Send(object data)
    {
        var json = JsonSerializer.Serialize(data, Contract.Json);
        if (Encoding.UTF8.GetByteCount(json) + 1 > 4096) throw new InvalidOperationException("M5GO状態が4096バイト上限を超えました");
        Outgoing?.Invoke(json);
    }
}
