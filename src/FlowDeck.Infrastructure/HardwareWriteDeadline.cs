using System.Runtime.InteropServices;

namespace FlowDeck.Infrastructure;

/// <summary>Same Windows clock in the C# host and Python worker. No wall-clock or process epoch conversion.</summary>
public static class HardwareWriteDeadline
{
    public const string ExpiredError = "COOLING_INPUT_EXPIRED";
    public const string LightingExpiredError = "LIGHTING_FRAME_EXPIRED";
    // GetTickCount64 has 10–16 ms resolution. Round the allowed interval down and reserve two timer ticks.
    public const int ClockMarginMilliseconds = 32;
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern ulong GetTickCount64();
    public static long Now => checked((long)GetTickCount64());
    public static long FromBudget(long started, TimeSpan validFor) => checked(started +
        (long)Math.Floor(Math.Clamp(validFor.TotalMilliseconds, 0, 3000)) - ClockMarginMilliseconds);
    public static bool Expired(long? deadline) => deadline is { } end && Now >= end;
}
