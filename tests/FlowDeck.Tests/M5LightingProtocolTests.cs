using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public class M5LightingProtocolTests
{
    private sealed class Backend(Fixture f) : IM5LightingBackend
    {
        public int Calls;
        public bool Fail;
        public TaskCompletionSource? Hold;
        public CancellationToken LastToken;
        public M5LightingCommand? Last;
        public M5LightingView Read(string scopeId) => new(new string('a', 64), scopeId, new string('照', 40), true, false,
            null, 54, false, true, false,
            new[] { new M5LightingItem("all", "すべて") }.Concat(Enumerable.Range(1, 32).Select(i => new M5LightingItem(i.ToString("x32"), new string('光', 40)))).ToArray(),
            Enumerable.Range(1, 32).Select(i => new M5LightingItem(i.ToString("x32"), new string('色', 40))).ToArray());
        public async Task<OperationResult> ExecuteAsync(M5LightingCommand request, CancellationToken cancellationToken)
        {
            Last = request; LastToken = cancellationToken; Interlocked.Increment(ref Calls);
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("backend failed");
            var s = f.Core.Snapshot;
            return new(request.Id, "M5GO", request.Command, Outcome.WrittenUnverified, "送信済み", f.Clock.GetUtcNow(), s.ControlRevision, s.LightingRevision, s.DeviceGeneration);
        }
    }
    private sealed class Peer
    {
        private readonly Fixture fixture;
        public Backend Backend { get; }
        public M5ProtocolSession Protocol { get; }
        public ConcurrentQueue<JsonElement> Frames { get; } = new();
        public Peer(Fixture f, bool backend = true)
        {
            fixture = f; Backend = new(f);
            Protocol = new(f.Core, time: f.Clock, lighting: backend ? Backend : null);
            Protocol.Outgoing += frame => { using var d = JsonDocument.Parse(frame); Frames.Enqueue(d.RootElement.Clone()); };
        }
        public void Hello(bool capable = true, string boot = "boot") => Protocol.Receive(JsonSerializer.Serialize(new
        { v = 1, type = "hello", deviceType = "M5GO", deviceId = "panel", bootId = boot, firmwareVersion = "1.3.0", capabilities = capable ? new[] { "lighting-v1" } : Array.Empty<string>() }));
        public void Send(string type, Dictionary<string, object?>? values = null)
        {
            values ??= []; values["v"] = 1; values["type"] = type;
            values["sessionId"] = Protocol.SessionId; values["controllerEpoch"] = fixture.Core.Epoch;
            Protocol.Receive(JsonSerializer.Serialize(values));
        }
        public void Command(string name = "LightingOff", string id = "a", long sequence = 1, object? payload = null) => Send("command", new()
        { ["id"] = id, ["commandSequence"] = sequence, ["command"] = name, ["expectedRevision"] = 0,
            ["payload"] = payload ?? new { scopeId = "all", expectedStamp = new string('a', 64) } });
        public bool Error(string value) => Frames.Any(f => f.TryGetProperty("error", out var e) && e.GetString() == value);
        public JsonElement Last(string type) => Frames.Last(f => f.GetProperty("type").GetString() == type);
    }
    private static async Task Wait(Func<bool> ready)
    { for (var i = 0; i < 200; i++) { if (ready()) return; await Task.Delay(10); } throw new TimeoutException(); }

    [Fact] public async Task CapabilityRequiresBothPeersAndLegacyHasNoAdditionalState()
    {
        await using var f = new Fixture(); await f.Initialize();
        foreach (var (backend, capable) in new[] { (false, true), (true, false), (true, true) })
        {
            var p = new Peer(f, backend); p.Hello(capable);
            Assert.Equal(backend && capable, p.Frames.Any(m => m.GetProperty("type").GetString() == "lightingState"));
            Assert.Equal(backend && capable ? 1 : 0, p.Last("welcome").GetProperty("capabilities").GetArrayLength());
            if (!backend || !capable) { p.Command(); Assert.Equal(0, p.Backend.Calls); }
        }
    }
    [Fact] public async Task FullJapaneseCatalogPagesFitFrameLimitAndPreserveAllIds()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Hello();
        foreach (var kind in new[] { "groups", "scenes" })
        {
            var ids = new List<string>();
            for (var page = 0; page <= (kind == "groups" ? 4 : 3); page++)
            {
                p.Send("getLightingCatalog", new() { ["scopeId"] = "all", ["kind"] = kind, ["page"] = page, ["requestId"] = "request-" + page });
                var frame = p.Last("lightingCatalog");
                Assert.Equal(page, frame.GetProperty("page").GetInt32());
                Assert.Equal("request-" + page, frame.GetProperty("requestId").GetString());
                ids.AddRange(frame.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            }
            Assert.Equal(kind == "groups" ? 33 : 32, ids.Distinct().Count());
        }
        Assert.All(p.Frames, m => Assert.InRange(Encoding.UTF8.GetByteCount(m.GetRawText()) + 1, 1, 4096));
    }
    [Fact] public async Task ScopeSelectionOnlyChangesPanelAndReconnectResetsIt()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Hello();
        var group = 1.ToString("x32"); p.Send("getLightingState", new() { ["scopeId"] = group });
        Assert.Equal(group, p.Last("lightingState").GetProperty("scopeId").GetString());
        Assert.Equal(0, p.Backend.Calls); Assert.Equal(0, f.Hub.Writes);
        var seq = p.Last("lightingState").GetProperty("lightingSequence").GetInt64(); p.Protocol.SendState();
        Assert.True(p.Last("lightingState").GetProperty("lightingSequence").GetInt64() > seq);
        p.Hello(boot: "new"); Assert.Equal("all", p.Last("lightingState").GetProperty("scopeId").GetString());
    }
    [Fact] public async Task DuplicatePendingAndCompletedLightingRequestsExecuteOnlyOnce()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Backend.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); p.Hello();
        p.Command(); await Wait(() => p.Backend.Calls == 1); p.Command();
        Assert.Equal(1, p.Backend.Calls); p.Backend.Hold.SetResult();
        await Wait(() => p.Frames.Any(m => m.GetProperty("type").GetString() == "result"));
        p.Command(); p.Send("getResult", new() { ["id"] = "a" });
        Assert.Equal("writtenUnverified", p.Last("result").GetProperty("outcome").GetString()); Assert.Equal(1, p.Backend.Calls);
        Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task LegacyAndNewCommandsShareIdAndSequenceProtection()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Hello();
        p.Command(sequence: 5); await Wait(() => p.Backend.Calls == 1);
        p.Command("SetManualDuty", sequence: 5, payload: new { duty = 50 }); Assert.True(p.Error("DUPLICATE_ID"));
        p.Command(id: "old", sequence: 4); Assert.True(p.Error("OLD_SEQUENCE"));
        Assert.Equal(1, p.Backend.Calls); Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task BackendFailureIsTerminalAndCanBeQueriedWithoutRetry()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Backend.Fail = true; p.Hello(); p.Command();
        await Wait(() => p.Error("BACKEND_ERROR"));
        p.Send("getResult", new() { ["id"] = "a" });
        Assert.Equal("unknown", p.Last("result").GetProperty("outcome").GetString()); Assert.Equal(1, p.Backend.Calls);
    }
    [Fact] public async Task NewSessionCancelsQueuedBackendAndSuppressesOldResult()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Backend.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); p.Hello();
        p.Command(); await Wait(() => p.Backend.Calls == 1); var old = p.Protocol.SessionId;
        p.Hello(boot: "new"); Assert.NotEqual(old, p.Protocol.SessionId); Assert.True(p.Backend.LastToken.IsCancellationRequested);
        await Task.Delay(50); Assert.DoesNotContain(p.Frames, m => m.GetProperty("type").GetString() == "result");
    }
    [Theory][InlineData(0)][InlineData(101)]
    public async Task InvalidBrightnessDoesNotExecuteOrRenewLiveness(int brightness)
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Hello(); f.Clock.Advance(2);
        p.Command("SetLightingBrightness", payload: new { scopeId = "all", expectedStamp = "stamp", brightness });
        f.Clock.Advance(1); Assert.False(p.Protocol.Connected); Assert.Equal(0, p.Backend.Calls);
    }
    [Fact] public async Task InvalidPageAndScopeDoNotRenewLiveness()
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Hello(); f.Clock.Advance(2);
        p.Send("getLightingCatalog", new() { ["scopeId"] = "all", ["kind"] = "groups", ["page"] = -1, ["requestId"] = "a" });
        p.Send("getLightingState", new() { ["scopeId"] = "missing" }); f.Clock.Advance(1); Assert.False(p.Protocol.Connected);
    }
    [Theory][InlineData("ApplyLightingScene")][InlineData("SetLightingBrightness")][InlineData("LightingOff")][InlineData("RestoreLighting")][InlineData("StopLightingSync")]
    public async Task EveryLightingActionUsesHostWithoutFanCommands(string action)
    {
        await using var f = new Fixture(); await f.Initialize(); var p = new Peer(f); p.Hello();
        var payload = new Dictionary<string, object?> { ["scopeId"] = "all", ["expectedStamp"] = new string('a', 64) };
        if (action == "ApplyLightingScene") payload["sceneId"] = 1.ToString("x32");
        if (action == "SetLightingBrightness") payload["brightness"] = 54;
        p.Command(action, payload: payload); await Wait(() => p.Frames.Any(m => m.GetProperty("type").GetString() == "result"));
        Assert.Equal(action, p.Backend.Last!.Command); Assert.Equal(0, f.Hub.Writes);
    }
}
