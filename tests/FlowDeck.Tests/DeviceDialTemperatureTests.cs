using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

[Collection("Wall-clock transport")]
public class DeviceDialTemperatureTests
{
    private static readonly EndpointReference Water = new("dial-test-board", "water-probe");
    private static readonly EndpointReference OtherWater = new("dial-test-board", "other-probe");
    private static readonly DeviceCapability Temperature = new(DeviceFeature.Temperature, ImplementationSupport.Supported,
        VerificationEvidence.Automated, DeviceAvailability.Ready);
    private static DeviceWorkspaceSnapshot WithTemperatures(DeviceWorkspaceSnapshot s)
    {
        var cpu = s.State.Inventory.Devices.Single(d => d.Device.Kind == DeviceKind.Processor);
        var gpu = s.State.Inventory.Devices.Single(d => d.Device.Kind == DeviceKind.Graphics);
        var cpuTarget = new EndpointReference(cpu.Device.Id, cpu.Device.Endpoints[0].Id);
        var gpuTarget = new EndpointReference(gpu.Device.Id, gpu.Device.Endpoints[0].Id);
        var board = new DeviceObservation(Water.DeviceId, "test-sensors", "Board", DeviceKind.Motherboard, IdentityConfidence.ConnectionBound, "test-board", null, [],
            [new(Water.EndpointId, "Temperature #5", EndpointKind.Sensor, [Temperature]), new(OtherWater.EndpointId, "T_SENSOR1", EndpointKind.Sensor, [Temperature])]);
        var c = s.Configuration.Configuration;
        c = c with { Preferences = c.Preferences with { DialDisplay = new() { Cpu = cpuTarget, Gpu = gpuTarget } },
            Devices = c.Devices with { Devices = [.. c.Devices.Devices, new(Water.DeviceId, "Board", "test-sensors", DeviceKind.Motherboard,
                [new(Water.EndpointId, "Temperature #5", EndpointKind.Sensor), new(OtherWater.EndpointId, "T_SENSOR1", EndpointKind.Sensor)])] },
            Dashboard = c.Dashboard with { Items = [.. c.Dashboard.Items, new(Water, DeviceFeature.Temperature, "T_SENSOR1", "temperature")] } };
        return s with { ElapsedSeconds = 0, Configuration = s.Configuration with { Configuration = c }, State = s.State with {
            Inventory = s.State.Inventory with { Devices = [.. s.State.Inventory.Devices, new(board, 1, true)] },
            Telemetry = [.. s.State.Telemetry!.Select(t => t.Feature == DeviceFeature.Temperature ? t with {
                Value = t.Target == cpuTarget ? 51.26 : t.Target == gpuTarget ? 44.36 : t.Value, AgeSeconds = 0, Stale = false } : t),
                new(Water, DeviceFeature.Temperature, 31.24, 0, false), new(OtherWater, DeviceFeature.Temperature, 99, 0, false)] } };
    }
    [Fact] public async Task ExplicitCpuGpuAndDashboardWaterAreGlobalAndDoNotChangeControlAuthority()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = WithTemperatures(client.Snapshot!);
        var before = DeviceDialBackend.ReadSnapshot(s);
        Assert.Equal((513, 444, 312), (before.CpuTempDeciC, before.GpuTempDeciC, before.WaterTempDeciC));
        s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Select(t => t.Feature == DeviceFeature.Temperature ? t with { Value = t.Value + 0.1 } : t).ToArray() } };
        var next = DeviceDialBackend.ReadSnapshot(s);
        Assert.Equal((514, 445, 313), (next.CpuTempDeciC, next.GpuTempDeciC, next.WaterTempDeciC));
        Assert.Equal(before.Targets, next.Targets); // Version/context/enabled/messages remain identical.
        var disabled = DeviceDialBackend.ReadSnapshot(s, _ => true); Assert.All(disabled.Targets, t => Assert.False(t.Enabled));
        Assert.Equal(next.CpuTempDeciC, disabled.CpuTempDeciC); Assert.Equal(next.WaterTempDeciC, disabled.WaterTempDeciC);
        s = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Where(d => d.Device.Kind != DeviceKind.Controller).ToArray() } } };
        var empty = DeviceDialBackend.ReadSnapshot(s); Assert.Empty(empty.Targets);
        Assert.Equal((514, 445, 313), (empty.CpuTempDeciC, empty.GpuTempDeciC, empty.WaterTempDeciC));
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Theory][InlineData("no-selection")][InlineData("wrong-kind")][InlineData("unregistered")][InlineData("disconnected")]
    public async Task CpuGpuNeverFallBackFromTheirExplicitSelection(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = WithTemperatures(client.Snapshot!); var c = s.Configuration.Configuration;
        var selected = c.Preferences.DialDisplay!;
        if (change == "no-selection") c = c with { Preferences = c.Preferences with { DialDisplay = null } };
        if (change == "wrong-kind") c = c with { Preferences = c.Preferences with { DialDisplay = selected with { Cpu = selected.Gpu, Gpu = selected.Cpu } } };
        if (change == "unregistered") c = c with { Devices = c.Devices with { Devices = c.Devices.Devices.Where(d => d.Id != selected.Cpu!.DeviceId && d.Id != selected.Gpu!.DeviceId).ToArray() } };
        if (change == "disconnected") s = s with { State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d =>
            d.Device.Id == selected.Cpu!.DeviceId || d.Device.Id == selected.Gpu!.DeviceId ? d with { Connected = false } : d).ToArray() } } };
        s = s with { Configuration = s.Configuration with { Configuration = c } }; var view = DeviceDialBackend.ReadSnapshot(s);
        Assert.Null(view.CpuTempDeciC); Assert.Null(view.GpuTempDeciC); Assert.Equal(312, view.WaterTempDeciC);
    }
    [Theory][InlineData("no-alias")][InlineData("case")][InlineData("wrong-measurement")][InlineData("ambiguous")][InlineData("disconnected")]
    [InlineData("unregistered")][InlineData("wrong-kind")][InlineData("unsupported")][InlineData("provider")]
    public async Task WaterRequiresOneExactSavedDashboardTemperatureReference(string change)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = WithTemperatures(client.Snapshot!); var c = s.Configuration.Configuration;
        if (change is "no-alias" or "case" or "wrong-measurement" or "ambiguous") c = c with { Dashboard = c.Dashboard with { Items = change switch {
            "no-alias" => c.Dashboard.Items.Where(i => i.Target != Water).ToArray(),
            "case" => c.Dashboard.Items.Select(i => i.Target == Water ? i with { Name = "t_sensor1" } : i).ToArray(),
            "wrong-measurement" => c.Dashboard.Items.Select(i => i.Target == Water ? i with { Measurement = DeviceFeature.Rpm } : i).ToArray(),
            _ => [.. c.Dashboard.Items, new(OtherWater, DeviceFeature.Temperature, "T_SENSOR1", "temperature")] } } };
        if (change == "unregistered") c = c with { Devices = c.Devices with { Devices = c.Devices.Devices.Where(d => d.Id != Water.DeviceId).ToArray() } };
        s = s with { Configuration = s.Configuration with { Configuration = c }, State = s.State with { Inventory = s.State.Inventory with { Devices = s.State.Inventory.Devices.Select(d =>
            d.Device.Id != Water.DeviceId ? d : change switch {
                "disconnected" => d with { Connected = false }, "wrong-kind" => d with { Device = d.Device with { Kind = DeviceKind.Processor } },
                "provider" => d with { Device = d.Device with { Provider = "other-provider" } },
                "unsupported" => d with { Device = d.Device with { Endpoints = d.Device.Endpoints.Select(e => e.Id == Water.EndpointId ? e with { Capabilities = [] } : e).ToArray() } }, _ => d }).ToArray() } } };
        var view = DeviceDialBackend.ReadSnapshot(s); Assert.Null(view.WaterTempDeciC);
        Assert.Equal(513, view.CpuTempDeciC); Assert.Equal(444, view.GpuTempDeciC);
    }
    [Theory][InlineData(null)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(-1d)][InlineData(0d)][InlineData(150d)][InlineData(151d)]
    public async Task TemperatureRejectsMissingNonFiniteAndOutsideRawCelsiusRange(double? value)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var s = WithTemperatures(client.Snapshot!);
        s = s with { State = s.State with { Telemetry = s.State.Telemetry!.Select(t => t.Feature == DeviceFeature.Temperature ? t with { Value = value } : t).ToArray() } };
        var view = DeviceDialBackend.ReadSnapshot(s); Assert.Null(view.CpuTempDeciC); Assert.Null(view.GpuTempDeciC); Assert.Null(view.WaterTempDeciC);
    }
    [Fact] public async Task TemperatureFreshnessDuplicateMeasurementsAndQuantizationBoundariesAreChecked()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var original = WithTemperatures(client.Snapshot!);
        var sample = original.State.Telemetry!.Single(t => t.Target == Water);
        foreach (var invalid in new[] { sample with { Stale = true }, sample with { AgeSeconds = 3 }, sample with { AgeSeconds = null },
            sample with { AgeSeconds = -1 }, sample with { Issue = DeviceMeasurementIssue.Unavailable }, sample with { Problem = "unavailable" } })
        {
            var s = original with { State = original.State with { Telemetry = original.State.Telemetry!.Select(t => t.Target == Water ? invalid : t).ToArray() } };
            Assert.Null(DeviceDialBackend.ReadSnapshot(s).WaterTempDeciC);
        }
        var duplicate = original with { State = original.State with { Telemetry = [.. original.State.Telemetry!, sample] } };
        Assert.Null(DeviceDialBackend.ReadSnapshot(duplicate).WaterTempDeciC);
        foreach (var (raw, deciC) in new[] { (0.01, 0), (149.99, 1500), (31.26, 313) })
        {
            var s = original with { State = original.State with { Telemetry = original.State.Telemetry!.Select(t => t.Target == Water ? t with { Value = raw } : t).ToArray() } };
            Assert.Equal(deciC, DeviceDialBackend.ReadSnapshot(s).WaterTempDeciC);
        }
        Assert.Null(DeviceDialBackend.ReadSnapshot(original with { ElapsedSeconds = 3 }).WaterTempDeciC);
        Assert.Null(DeviceDialBackend.ReadSnapshot(original with { Connected = false }).CpuTempDeciC);
    }
}
