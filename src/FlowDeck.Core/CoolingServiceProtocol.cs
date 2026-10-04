using System.Text.Json;

namespace FlowDeck.Core;

public record ServiceFanConfiguration(HardwareSettings Hardware, FanLimits Limits, string? CpuSensorId, string? GpuSensorId,
    AutoProfile[] Profiles, CpuFanSettings CpuFan, CpuFanSettings ChassisFan, Dictionary<string, CpuFanSettings> BoardFans)
{
    public static ServiceFanConfiguration From(AppSettings s) => Contract.Copy(new ServiceFanConfiguration(s.Hardware, s.Limits,
        s.CpuSensorId, s.GpuSensorId, s.Profiles, s.CpuFan, s.ChassisFan, s.BoardFans));
    public AppSettings Merge(AppSettings local) => local with { Hardware = Hardware, Limits = Limits, CpuSensorId = CpuSensorId,
        GpuSensorId = GpuSensorId, Profiles = Profiles, CpuFan = CpuFan, ChassisFan = ChassisFan, BoardFans = BoardFans };
}
public record ServiceHello(int Version, string Epoch, string Session, long SettingsRevision, int LeaseSeconds, bool Demo, string[]? Capabilities = null);
public record ServiceCoolingState(ControlSnapshot Control, ServiceFanConfiguration Configuration, long SettingsRevision,
    HubStatus Hub, Dictionary<string, HubPortLighting> LightingPorts, BoardSnapshot Board, Dictionary<string, CpuFanState> Fans,
    Dictionary<string, string?> ProfileReadiness, string? LifecycleError, string WorkerVersion, long StateSequence = 0, FanPolicy? LastConfirmedPolicy = null, string? SleepResumeToken = null);
public record CoolingServiceRequest(int Version, string Epoch, string Session, string Id, long Sequence, string Command, JsonElement Payload);
public record CoolingServiceReply(int Version, string Epoch, string Session, string Id, long Sequence, bool Success,
    string? Error, JsonElement Payload);
public record ServiceCoolingUpdate(long Sequence, ServiceCoolingState State, OperationResult[] Operations);
public record CoolingServiceMessage(int Version, string Epoch, string Session, string Kind,
    CoolingServiceReply? Reply, ServiceCoolingUpdate? Update);
public record ServiceConfigure(ServiceFanConfiguration Configuration, long ExpectedRevision);
public record ServiceConfigurationImport(ServiceFanConfiguration Configuration, long ExpectedRevision, LightingState Lighting, Dictionary<string, HubPortLighting> LightingPorts);
public record ServiceProfileChange(AutoProfile Profile, long ExpectedProfileRevision, long ExpectedSettingsRevision);
public record ServiceOpenHub(string HubId);
public record ServiceBoardCommand(string Port, long ExpectedSettingsRevision, int? Duty = null);
public record ServiceLightFrame(LightingLease Lease, RgbColor Color);
public record ServiceLightTarget(string HubId, string[] Channels);
public record ServiceLightBegin(string HubId, string[] Channels, long ExpectedRevision, long ExpectedGeneration);
public record ServiceResultQuery(string RequestId);
public record ServiceStartupResume(long ExpectedRevision, string PolicyHash);
public record ServiceSleepResume(string Token, long ExpectedRevision);

public static class CoolingServiceProtocol
{
    public const int Version = 1, MaximumFrameBytes = 512 * 1024, LeaseSeconds = 12;
    public const string PipeName = "FlowDeck.Cooling.v1", ServiceName = "FlowDeckCooling";
    public static readonly string[] Commands = ["state", "heartbeat", "renew", "probe", "open", "configure", "import-configuration", "profile", "control", "stop", "board-manual", "board-auto",
        "board-test", "board-restore", "board-restore-all", "light-target", "light-begin", "light-valid", "light-end", "light-frame", "memory-rgb-read", "memory-rgb-apply", "memory-stream-begin", "memory-stream-frame", "memory-stream-end", "suspend", "resume", "restore-startup", "restore-sleep", "result", "release"];
    public static bool Id(string? value) => Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    public static JsonElement Payload<T>(T value) => JsonSerializer.SerializeToElement(value, Contract.Json);
    public static T Body<T>(CoolingServiceRequest request) => request.Payload.Deserialize<T>(Contract.Json) ?? throw new JsonException("要求本文が空です");
    public static CoolingServiceRequest Read(byte[] bytes)
    {
        if (bytes.Length > MaximumFrameBytes) throw new IOException("サービス要求が大きすぎます");
        using var doc = JsonDocument.Parse(bytes); CheckKeys(doc.RootElement);
        var request = JsonSerializer.Deserialize<CoolingServiceRequest>(bytes, Contract.Json);
        if (request is null || request.Version != Version || !Id(request.Epoch) || !Id(request.Session) || !Id(request.Id) || request.Sequence <= 0 ||
            !Commands.Contains(request.Command) || request.Payload.ValueKind == JsonValueKind.Undefined)
            throw new IOException("サービス要求の形式が不正です");
        return request;
    }
    public static CoolingServiceMessage ReadMessage(byte[] bytes, ServiceHello hello)
    {
        if (bytes.Length > MaximumFrameBytes) throw new IOException("サービス応答が大きすぎます");
        using var doc = JsonDocument.Parse(bytes); CheckKeys(doc.RootElement);
        var message = JsonSerializer.Deserialize<CoolingServiceMessage>(bytes, Contract.Json);
        if (message is null || message.Version != Version || message.Epoch != hello.Epoch || message.Session != hello.Session)
            throw new IOException("古い起動または接続の応答を破棄しました");
        if (message.Kind == "reply" && message.Update is null && message.Reply is { } reply &&
            reply.Version == Version && reply.Epoch == hello.Epoch && reply.Session == hello.Session && Id(reply.Id) && reply.Sequence > 0)
            return message;
        if (message.Kind == "update" && message.Reply is null && message.Update is { Sequence: > 0, State: not null, Operations: not null } update &&
            update.State.Control is not null && update.State.Control.ControllerEpoch == hello.Epoch && update.Operations.Length <= 32 &&
            update.Operations.All(o => o is not null)) return message;
        throw new IOException("サービス応答の形式が不正です");
    }
    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw new JsonException("要求の項目が重複しています"); CheckKeys(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }
}
