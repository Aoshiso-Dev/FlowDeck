namespace FlowDeck.Core;

// Acquisition validity is separate from age, connection state and the user's display scale.
public enum DeviceMeasurementIssue { None, Unavailable, OutOfRange }
public enum DeviceMeasurementState { Valid, Unavailable, Stale, OutOfRange }

public static class DeviceMeasurement
{
    public static bool InRange(DeviceFeature feature, double value) => double.IsFinite(value) && feature switch
    {
        DeviceFeature.Temperature => value is > 0 and < 150,
        DeviceFeature.Rpm => value >= 0,
        DeviceFeature.DutyRead => value is >= 0 and <= 100,
        _ => false
    };
    public static DeviceMeasurementState State(DeviceTelemetry? sample, double elapsedSeconds)
    {
        if (sample is null || !Enum.IsDefined(sample.Issue)) return DeviceMeasurementState.Unavailable;
        if (sample.Issue == DeviceMeasurementIssue.OutOfRange) return DeviceMeasurementState.OutOfRange;
        if (sample.Issue == DeviceMeasurementIssue.Unavailable || sample.Value is null) return DeviceMeasurementState.Unavailable;
        if (!InRange(sample.Feature, sample.Value.Value)) return DeviceMeasurementState.OutOfRange;
        if (sample.Stale || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 ||
            sample.AgeSeconds is not { } age || !double.IsFinite(age) || age < 0 || age + elapsedSeconds >= 3)
            return DeviceMeasurementState.Stale;
        return sample.Problem is null ? DeviceMeasurementState.Valid : DeviceMeasurementState.Unavailable;
    }
    public static string Label(DeviceMeasurementState state) => state switch
    {
        DeviceMeasurementState.Valid => "取得中",
        DeviceMeasurementState.Stale => "更新停止",
        DeviceMeasurementState.OutOfRange => "範囲外",
        _ => "未取得"
    };
}
