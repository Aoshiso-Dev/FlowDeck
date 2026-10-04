namespace FlowDeck.Core;

public static class NotificationInputs
{
    public static NotificationSample[] Collect(AppSettings settings, ControlSnapshot control, BoardSnapshot board, IReadOnlyDictionary<string,CpuFanState> owners, TimeProvider? time = null)
    {
        var samples = new List<NotificationSample>();
        foreach (var sensor in control.Sensors.Where(s => s.Kind is "cpu" or "gpu"))
            samples.Add(new(new("sensor",sensor.Kind,sensor.Id,"temperature"),sensor.Name,
                DashboardDisplay.Fresh(sensor.Timestamp,time) && MotherboardDisplay.IsUsable("temperature",sensor.Celsius) ? sensor.Celsius : null));
        if (settings.Hardware.Hub is { } hub)
            foreach (var channel in control.Channels)
            {
                var valid = control.HubReady && DashboardDisplay.Fresh(control.TelemetryTimestamp,time) && channel.Presence != Presence.Absent;
                var name = settings.Dashboard.Items?.FirstOrDefault(i => i.Source == "nzxt" && i.SensorId == channel.Id)?.Name ?? "NZXT " + channel.Id.ToUpperInvariant();
                samples.Add(new(new("nzxt",hub.Id,channel.Id,"fan"),name,valid && MotherboardDisplay.IsUsable("fan",channel.Rpm) ? channel.Rpm : null,
                    valid && channel.Duty == 0 && (!control.Managing || control.TargetDuty == 0)));
            }
        foreach (var sensor in board.Sensors.Where(s => s.Kind is "temperature" or "fan"))
        {
            var name = settings.Dashboard.Items?.FirstOrDefault(i => i.Source == "motherboard" && i.SensorId == sensor.Id)?.Name ??
                settings.Motherboard.Sensors?.FirstOrDefault(s => s.Id == sensor.Id)?.DisplayName ?? MotherboardFans.ForSensor(board,sensor.Id)?.Name ?? MotherboardDisplay.DefaultName(sensor);
            var value = board.IsFresh(time) && sensor.Problem is null && MotherboardDisplay.IsUsable(sensor.Kind,sensor.Value) ? sensor.Value : null;
            var stopped = false;
            if (sensor.Kind == "fan" && MotherboardFans.ForSensor(board,sensor.Id) is { } port && owners.TryGetValue(port.Id,out var owner))
            {
                if (owner.Reading is { } reading && DashboardDisplay.Fresh(reading.Timestamp,time))
                {
                    if (value is not null && MotherboardDisplay.IsUsable("fan",reading.Rpm)) value = reading.Rpm;
                    stopped = value is not null && reading.Duty == 0 && (!owner.Managing || owner.Target == 0);
                }
            }
            // A zero RPM reading alone never proves an intentional stop.
            samples.Add(new(new("motherboard",board.Name,sensor.Id,sensor.Kind),name,value,stopped));
        }
        return samples.ToArray();
    }
}
