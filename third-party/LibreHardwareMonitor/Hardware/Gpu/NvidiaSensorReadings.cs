// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// FlowDeck addition to LibreHardwareMonitor 0.9.6.
using System;
using System.Linq;
using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal static class NvidiaSensorReadings
{
    public static NvApi.NvSensor[] ThermalBindings(NvApi.NvThermalSettings settings, NvApi.NvStatus status) =>
        status == NvApi.NvStatus.OK && settings.Sensor != null && settings.Count <= NvApi.MAX_THERMAL_SENSORS_PER_GPU &&
        settings.Count <= settings.Sensor.Length ? settings.Sensor.Take((int)settings.Count).ToArray() : Array.Empty<NvApi.NvSensor>();

    public static float? Temperature(NvApi.NvSensor binding, NvApi.NvSensor[] original, NvApi.NvSensor[] current)
    {
        if (binding.Target is NvApi.NvThermalTarget.None or NvApi.NvThermalTarget.Unknown or NvApi.NvThermalTarget.All ||
            !Enum.IsDefined(binding.Target)) return null;
        bool Matches(NvApi.NvSensor item) => item.Target == binding.Target && item.Controller == binding.Controller;
        if (original.Count(Matches) != 1) return null;
        var readings = current.Where(Matches).ToArray();
        return readings.Length == 1 ? UsableTemperature(readings[0].CurrentTemp) : null;
    }

    public static int CoreIndex(NvApi.NvSensor[] bindings)
    {
        var indices = Enumerable.Range(0, bindings.Length).Where(i => bindings[i].Target == NvApi.NvThermalTarget.Gpu).ToArray();
        return indices.Length == 1 ? indices[0] : -1;
    }

    public static float? AdditionalTemperature(NvApi.NvThermalSensors readings, NvApi.NvStatus status, int index) =>
        status == NvApi.NvStatus.OK && readings.Temperatures is { Length: NvApi.THERMAL_SENSOR_TEMPERATURE_COUNT } &&
        index >= 0 && index < readings.Temperatures.Length ? UsableTemperature(readings.Temperatures[index] / 256f) : null;

    public static NvApi.NvFanCoolersStatusItem[] FanItems(NvApi.NvFanCoolersStatus readings, NvApi.NvStatus status)
    {
        if (status != NvApi.NvStatus.OK || readings.Items == null || readings.Count > NvApi.MAX_FAN_COOLERS_STATUS_ITEMS ||
            readings.Count > readings.Items.Length) return Array.Empty<NvApi.NvFanCoolersStatusItem>();
        var items = readings.Items.Take((int)readings.Count).ToArray();
        return items.All(i => i.CoolerId <= int.MaxValue) && items.Select(i => i.CoolerId).Distinct().Count() == items.Length
            ? items : Array.Empty<NvApi.NvFanCoolersStatusItem>();
    }

    private static float? UsableTemperature(float value) => float.IsFinite(value) && value > 0 && value < 150 ? value : null;
}
