namespace FlowDeck.Core;

/// <summary>Editing only. These operations never save a policy or issue a device command.</summary>
public static class CurveEditing
{
    public const double Gap = 0.1;
    public static bool ValidShape(IReadOnlyList<CurvePoint> points, int? minimum, bool allowStop)
    {
        if (minimum is not (>= 1 and <= 100)) return false;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.Temperature) || p.Duty > 100 || p.Duty < minimum && !(allowStop && p.Duty == 0)) return false;
            if (i > 0 && (p.Temperature <= points[i - 1].Temperature || p.Duty < points[i - 1].Duty)) return false;
        }
        return true;
    }
    private static int Duty(double requested, int minimum, bool allowStop, int low, int high)
    {
        var value = (int)Math.Clamp(Math.Round(requested), 0, 100);
        if (value < minimum) value = allowStop && value < minimum / 2d ? 0 : minimum;
        return Math.Clamp(value, low, high);
    }
    public static CurvePoint? Move(IReadOnlyList<CurvePoint> points, int index, double temperature, double duty,
        int? minimum, bool allowStop, double minTemperature, double maxTemperature)
    {
        if (!ValidShape(points, minimum, allowStop) || index < 0 || index >= points.Count || !double.IsFinite(temperature) || !double.IsFinite(duty) ||
            !double.IsFinite(minTemperature) || !double.IsFinite(maxTemperature) || minTemperature >= maxTemperature) return null;
        var low = index == 0 ? minTemperature : Math.Max(minTemperature, points[index - 1].Temperature + Gap);
        var high = index == points.Count - 1 ? maxTemperature : Math.Min(maxTemperature, points[index + 1].Temperature - Gap);
        if (low > high) return null;
        var t = Math.Clamp(Math.Round(temperature, 1), low, high);
        var value = Duty(duty, minimum!.Value, allowStop, index == 0 ? (allowStop ? 0 : minimum.Value) : points[index - 1].Duty,
            index == points.Count - 1 ? 100 : points[index + 1].Duty);
        return new(t, value);
    }
    public static (int Index, CurvePoint Point)? Insert(IReadOnlyList<CurvePoint> points, double temperature, double duty,
        int? minimum, bool allowStop, double minTemperature, double maxTemperature)
    {
        if (!ValidShape(points, minimum, allowStop) || !double.IsFinite(temperature) || !double.IsFinite(duty) ||
            !double.IsFinite(minTemperature) || !double.IsFinite(maxTemperature) || minTemperature >= maxTemperature) return null;
        var t = Math.Clamp(Math.Round(temperature, 1), minTemperature, maxTemperature);
        if (points.Any(p => Math.Abs(p.Temperature - t) < Gap - 1e-8)) return null;
        var index = points.Count(p => p.Temperature < t);
        var value = Duty(duty, minimum!.Value, allowStop, index == 0 ? (allowStop ? 0 : minimum.Value) : points[index - 1].Duty,
            index == points.Count ? 100 : points[index].Duty);
        return (index, new(t, value));
    }
}
