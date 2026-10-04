using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceM5LightingTests
{
    private static DeviceWorkspaceSnapshot FixedBodySnapshot() { var s = Snapshot(); return s with {
        State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with {
            Owned = true, ReportedLighting = new("fixed", [new(200, 100, 50)], Brightness: 50) }).ToArray() } }; }

    [Fact] public void BodyMirrorUsesReportedColorAndBrightnessAndOff()
    {
        var s = FixedBodySnapshot(); var before = Contract.Copy(s);
        var frame = DeviceM5Lighting.ReadBodyLed(s, "all").Frame!;
        Assert.True(frame.Valid); Assert.Equal(new RgbColor(100, 50, 25), frame.Color); Assert.Equal(100, frame.Brightness);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(s));
        s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with { ReportedLighting = new("off", []) }).ToArray() } };
        Assert.Equal("off", DeviceM5Lighting.ReadBodyLed(s, "all").Frame!.Effect);
    }
    [Theory][InlineData("different")][InlineData("native")][InlineData("unknown")][InlineData("unowned")]
    [InlineData("pending")][InlineData("fault")][InlineData("disconnected")][InlineData("stale")][InlineData("scope")]
    public void BodyMirrorDoesNotGuessOrReviveUnavailableLighting(string change)
    {
        var s = FixedBodySnapshot(); var first = s.State.Endpoints[0];
        first = change switch {
            "different" => first with { ReportedLighting = new("fixed", [new(255, 0, 0)]) },
            "native" => first with { ReportedLighting = new("breathing", [new(255, 0, 0)]) },
            "unknown" => first with { Outcome = Outcome.Unknown }, "unowned" => first with { Owned = false },
            "pending" => first with { Outcome = Outcome.Pending },
            "fault" => first with { Lighting = new(Id('b'), false, 0, 0, null, 0, "failure") }, _ => first };
        s = s with { State = s.State with { Endpoints = [first, s.State.Endpoints[1]] },
            Connected = change != "disconnected", ElapsedSeconds = change == "stale" ? 3 : 0 };
        var view = DeviceM5Lighting.ReadBodyLed(s, change == "scope" ? Id('0') : "all");
        Assert.Null(view.Frame); Assert.NotEmpty(view.Error!);
        if (change == "different") Assert.NotNull(DeviceM5Lighting.ReadBodyLed(s, DeviceM5Lighting.WireId(LightingPresetKind.Group, "one")).Frame);
    }
    [Fact] public void BodyAnimationUsesAppliedPhaseAndExpiresWithoutNewServiceData()
    {
        var s = FixedBodySnapshot(); var mirror = new M5BodyLedFrame("breathing", new(100, 200, 50), 30, 2000, 1800, 3000);
        s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with {
            Lighting = new(Id('b'), true, 4, 0, new(1, 2, 3), 1, Mirror: mirror) }).ToArray() }, ElapsedSeconds = .4 };
        var frame = DeviceM5Lighting.ReadBodyLed(s, "all").Frame!;
        Assert.Equal(200, frame.PhaseMs, 5); Assert.Equal(2600, frame.ValidForMs); Assert.Equal(mirror.Color, frame.Color);
        s = s with { ElapsedSeconds = 2.9 };
        Assert.InRange(DeviceM5Lighting.ReadBodyLed(s, "all").Frame!.ValidForMs, 99, 100);
        s = s with { ElapsedSeconds = 3 }; Assert.Null(DeviceM5Lighting.ReadBodyLed(s, "all").Frame);
    }
    [Fact] public void BodyAnimationRequiresSuccessfulFramesAndSamePhaseAcrossSelection()
    {
        var s = FixedBodySnapshot(); var mirror = new M5BodyLedFrame("color-cycle", new(255, 0, 0), 100, 6000, 100, 1500);
        s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with {
            Lighting = new(Id('b'), true, 1, 0, new(255, 0, 0), 1, Mirror: e.Target == A ? mirror : mirror with { PhaseMs = 200 }) }).ToArray() } };
        Assert.Null(DeviceM5Lighting.ReadBodyLed(s, "all").Frame);
        var scope = DeviceM5Lighting.WireId(LightingPresetKind.Group, "one");
        Assert.NotNull(DeviceM5Lighting.ReadBodyLed(s, scope).Frame);
        Assert.Null(DeviceM5Lighting.ReadBodyLed(s with { ElapsedSeconds = 1.5 }, scope).Frame);
        s = s with { State = s.State with { Endpoints = s.State.Endpoints.Select(e => e with { Lighting = e.Lighting! with { Mirror = null } }).ToArray() } };
        Assert.Null(DeviceM5Lighting.ReadBodyLed(s, scope).Frame);
    }
}
