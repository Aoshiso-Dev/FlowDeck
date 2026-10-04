using System.Windows;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunExternalDeviceWorkspaceAsync(DeviceWorkspaceViewModel model, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); report.Add("PASS " + message); }
        try
        {
            await Settle();
            Check(model.DashboardTemperatures.Count == 2 && model.Cooling.Count == 10, "External v2 service supplies both temperature and cooling catalogs");
            var snapshot = client.Snapshot!; var device = snapshot.Configuration.Configuration.Devices.Devices.Single(d => d.Name == "ラジエーター");
            var row = model.Cooling.Single(r => r.Draft.Target == new EndpointReference(device.Id, "fan3"));
            row.Mode = "固定"; row.Duty = 57; await model.OperateAsync(row);
            Check(row.CurrentDuty == "57%" && client.Snapshot!.State.Endpoints.Count(e => e.Owned) == 1, "GUI applies only the selected endpoint through the external service");
            model.SelectLights(model.Lights.Select(l => l.Target)); model.LightEffect = "breathing"; await model.ApplyLightsAsync();
            var lights = client.Snapshot!.State.Endpoints.Where(e => e.Lighting?.Running == true).ToArray();
            Check(lights.Length == 2 && lights.Select(e => e.Lighting!.GroupId).Distinct().Count() == 1, "External service owns the common lighting clock");
            var display = model.DashboardSettings.Single(r => r.Item.Target == row.Draft.Target);
            display.Name = "別プロセスのラジエーター"; display.Indicator = "circle"; display.AutomaticRange = false; display.Maximum = 4500;
            await model.SaveDashboardAsync(); await model.ReloadDashboardAsync();
            Check(display.Name == "別プロセスのラジエーター" && model.DashboardFans.Any(r => r.Name == display.Name && r.ScaleMaximum == 4500), "External service persists and returns display edits");
            model.SelectedPage = 1; await Settle(); Capture(window, output, "external-service-cooling");
            model.SelectedPage = 2; await Settle(); Capture(window, output, "external-service-lighting");
            Check(await model.CloseAsync(), "GUI receives confirmed release before closing its external service session");
            await File.WriteAllLinesAsync(Path.Combine(output, "device-external-workspace-smoke.txt"), report); return 0;
        }
        catch (Exception e)
        { report.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "device-external-workspace-smoke.txt"), report); return 1; }
    }
}
