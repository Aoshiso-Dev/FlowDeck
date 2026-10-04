namespace FlowDeck.Core;

// Optional, receive-only projection. It never grants PC lighting ownership.
public sealed record M5BodyLedFrame(string Effect, RgbColor Color, int Brightness, int PeriodMs, double PhaseMs, int ValidForMs)
{
    public bool Valid => Color?.Valid == true && Brightness is >= 0 and <= 100 && ValidForMs is > 0 and <= 3000 &&
        double.IsFinite(PhaseMs) && PhaseMs >= 0 && (Effect is "fixed" or "off" ? PeriodMs == 0 && PhaseMs == 0 :
        Effect is "breathing" or "color-cycle" && PeriodMs is 2000 or 4000 or 6000 or 9000 or 12000 && PhaseMs < PeriodMs);
}
public sealed record M5BodyLedView(M5BodyLedFrame? Frame, string? Error = null);
public interface IM5BodyLedBackend
{
    M5BodyLedView ReadBodyLed(string scopeId);
}
