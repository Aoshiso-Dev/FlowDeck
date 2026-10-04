using System.Text.Json;
using FlowDeck.Core;
namespace FlowDeck.Tests;
public class M5BodyLedProtocolTests
{
    [Fact] public void BodyFirmwareVectorsAgreeWithPcEffects()
    {
        using var source = typeof(M5BodyLedProtocolTests).Assembly.GetManifestResourceStream("FlowDeck.Tests.M5BodyLedVectors.jsonl")!;
        using var reader = new StreamReader(source); var count = 0;
        while (reader.ReadLine() is { } line)
        {
            using var d = JsonDocument.Parse(line); var root = d.RootElement;
            var frame = root.GetProperty("frame").Deserialize<M5BodyLedFrame>(Contract.Json)!;
            Assert.True(frame.Valid); var speed = Array.IndexOf(new[] { 12000, 9000, 6000, 4000, 2000 }, frame.PeriodMs);
            var program = new LightingProgram(frame.Effect, frame.Effect == "off" ? [] : [frame.Color], Math.Max(0, speed), Brightness: frame.Brightness);
            var actual = DeviceLighting.Frame(program, (frame.PhaseMs + root.GetProperty("afterMs").GetInt32()) / 1000);
            var expected = root.GetProperty("expected").EnumerateArray().Select(n => n.GetInt32()).ToArray();
            Assert.InRange(Math.Abs(actual.R - expected[0]), 0, 1); Assert.InRange(Math.Abs(actual.G - expected[1]), 0, 1);
            Assert.InRange(Math.Abs(actual.B - expected[2]), 0, 1); count++;
        }
        Assert.Equal(800, count);
    }
    private sealed class Backend : IM5LightingBackend, IM5BodyLedBackend
    {
        public M5BodyLedView View = new(new("fixed", new(255, 0, 80), 50, 0, 0, 2500));
        public int Writes;
        public M5BodyLedView ReadBodyLed(string scopeId) => View;
        public M5LightingView Read(string scopeId) => new(new('a', 64), scopeId, "対象", true, false, null, 50, false, false, false, [], []);
        public Task<OperationResult> ExecuteAsync(M5LightingCommand request, CancellationToken ct) { Writes++; throw new InvalidOperationException(); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BodyCapabilityIsOptInAndNeverWritesPcLighting(bool capable)
    {
        await using var f = new Fixture(); await f.Initialize(); var backend = new Backend();
        var frames = new List<JsonElement>(); var protocol = new M5ProtocolSession(f.Core, time: f.Clock, lighting: backend);
        protocol.Outgoing += json => { using var d = JsonDocument.Parse(json); frames.Add(d.RootElement.Clone()); };
        protocol.Receive(JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "panel", bootId = "b", firmwareVersion = capable ? "1.4.0" : "1.3.0",
            capabilities = capable ? new[] { "lighting-v1", "body-led-v1" } : new[] { "lighting-v1" } }));
        Assert.Equal(capable, frames.Any(e => e.GetProperty("type").GetString() == "bodyLedState"));
        Assert.False(protocol.Synchronized);
        protocol.SendState(); Assert.Equal(0, backend.Writes);
        if (!capable) return;
        var first = frames.Last(e => e.GetProperty("type").GetString() == "bodyLedState");
        Assert.True(first.GetProperty("ready").GetBoolean()); Assert.Equal("all", first.GetProperty("scopeId").GetString());
        Assert.True(first.GetProperty("bodyLedSequence").GetInt64() > 1);
        backend.View = new(null, "未対応"); protocol.SendState();
        Assert.False(frames.Last(e => e.GetProperty("type").GetString() == "bodyLedState").GetProperty("ready").GetBoolean());
        Assert.All(frames, e => Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(e.GetRawText()) + 1, 1, 4096));
        protocol.Disconnect(); var count = frames.Count; protocol.SendState(); Assert.Equal(count, frames.Count);
    }
    [Theory][InlineData("fixed", 1, 0)][InlineData("off", 0, 1)][InlineData("native", 6000, 0)]
    [InlineData("breathing", 3000, 0)][InlineData("breathing", 6000, 6000)][InlineData("color-cycle", 6000, -1)]
    public void BodyContractRejectsUnsupportedOrInvalidTiming(string effect, int period, double phase) =>
        Assert.False(new M5BodyLedFrame(effect, new(0, 0, 0), 100, period, phase, 3000).Valid);
}
