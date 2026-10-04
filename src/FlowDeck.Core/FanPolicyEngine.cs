namespace FlowDeck.Core;

public static class FanPolicyEngine
{
    public static string? ValidateLimits(FanLimits limits)
    {
        if (limits.Minimum is not (>= 1 and <= 100)) return "共通最低出力を1〜100%で設定してください";
        if (limits.Fallback is null || limits.Exit is null) return "異常時・終了時出力を設定してください";
        if (limits.Fallback < limits.Minimum || limits.Fallback > 100 || limits.Exit < limits.Minimum || limits.Exit > 100)
            return "異常時・終了時出力は共通最低出力〜100%が必要です";
        if (limits.AllowStop && (limits.Stop is not { } s || !double.IsFinite(s.CpuBelow) || !double.IsFinite(s.GpuBelow)
            || s.RestartDuty < limits.Minimum || s.RestartDuty > 100 || !double.IsFinite(s.RestartSeconds) || s.RestartSeconds <= 0))
            return "停止許可にはCPU/GPU停止温度と、確認済みの再始動出力・時間が必要です";
        if (limits.ProtectManualCpu && !ThresholdsValid(limits.CpuOverheat, limits.CpuResumeBelow) ||
            limits.ProtectManualGpu && !ThresholdsValid(limits.GpuOverheat, limits.GpuResumeBelow))
            return "手動保護の過熱・復帰温度が未設定または不正です";
        return null;
    }
    public static bool ThresholdsValid(double? overheat, double? resume) => overheat is { } o && resume is { } r
        && double.IsFinite(o) && double.IsFinite(r) && r < o;
    public static string? ValidateProfile(AutoProfile p, FanLimits limits)
    {
        if (ValidateLimits(limits) is { } e) return e;
        if (p.Cpu is null && p.Gpu is null) return "使用センサーを1個以上選択してください";
        if (p.DescentDelaySeconds is not { } delay || !double.IsFinite(delay) || delay < 0 ||
            p.DescentPerSecond is not { } rate || !double.IsFinite(rate) || rate <= 0) return "下降待機時間・下降速度を設定してください";
        foreach (var c in new[] { p.Cpu, p.Gpu }.OfType<SensorCurve>())
        {
            if (string.IsNullOrWhiteSpace(c.SensorId) || c.Points.Length < 2) return "センサーと2点以上のカーブが必要です";
            if (!ThresholdsValid(c.Overheat, c.ResumeBelow)) return "過熱温度より低い復帰温度を設定してください";
            for (var i = 0; i < c.Points.Length; i++)
            {
                var point = c.Points[i];
                if (!double.IsFinite(point.Temperature) || !IsDutyAllowed(point.Duty, limits)) return "温度・出力が許容範囲外です";
                if (i > 0 && (point.Temperature <= c.Points[i - 1].Temperature || point.Duty < c.Points[i - 1].Duty))
                    return "温度は重複なしの昇順、出力は非減少にしてください";
            }
        }
        return null;
    }
    public static bool IsDutyAllowed(int duty, FanLimits limits) => duty is >= 0 and <= 100 &&
        (duty >= limits.Minimum || duty == 0 && limits.AllowStop && limits.Stop is not null);
    public static double Interpolate(CurvePoint[] points, double temperature)
    {
        if (temperature <= points[0].Temperature) return points[0].Duty;
        for (var i = 1; i < points.Length; i++)
            if (temperature <= points[i].Temperature)
                return points[i - 1].Duty + (temperature - points[i - 1].Temperature) /
                    (points[i].Temperature - points[i - 1].Temperature) * (points[i].Duty - points[i - 1].Duty);
        return points[^1].Duty;
    }
    public static int Calculate(AutoProfile profile, FanLimits limits, IReadOnlyDictionary<string, double> temperatures)
    {
        var duty = (int)Math.Ceiling(new[] { profile.Cpu, profile.Gpu }.OfType<SensorCurve>()
            .Max(c => Interpolate(c.Points, temperatures[c.SensorId])));
        return duty == 0 && limits.AllowStop ? 0 : Math.Clamp(duty, limits.Minimum!.Value, 100);
    }
}

/// <summary>Rise immediately; hold below-current targets, then descend using elapsed time, retaining fractions.</summary>
public sealed class DescentLimiter
{
    private double value;
    private long? belowSince;
    private long last;
    public void Reset(int current, long now) { value = current; last = now; belowSince = null; }
    public int Next(int target, long now, double delay, double rate, TimeProvider clock)
    {
        var elapsed = Math.Max(0, clock.GetElapsedTime(last, now).TotalSeconds);
        last = now;
        if (target >= value) { value = target; belowSince = null; }
        else
        {
            belowSince ??= now;
            var since = clock.GetElapsedTime(belowSince.Value, now).TotalSeconds;
            if (since > delay) value = Math.Max(target, value - rate * Math.Min(elapsed, since - delay));
        }
        return (int)Math.Ceiling(value);
    }
}
