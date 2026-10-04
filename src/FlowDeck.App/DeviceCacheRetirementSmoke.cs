using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
namespace FlowDeck.App;
public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceCacheRetirementAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,DeviceWorkspaceClient client,Window window,string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label);report.Add("PASS "+label); }
        try
        {
            model.SelectedPage=3;await Settle();
            var panel=Descendants<Expander>(window).Single(e=>Equals(e.Header,"検出履歴"));panel.SetCurrentValue(Expander.IsExpandedProperty,true);await Settle();
            var preview=Descendants<Button>(panel).Single(b=>AutomationProperties.GetName(b)=="検出履歴の整理を確認");
            var apply=Descendants<Button>(panel).Single(b=>AutomationProperties.GetName(b)=="検出履歴の整理を適用");
            Check(model.CacheRetirementSupported&&model.CacheHistoryChoices.Count==0&&!preview.IsEnabled,"Connected devices cannot be selected for history retirement");
            var gpu=client.Snapshot!.State.Inventory.Devices.Single(d=>d.Device.Kind==DeviceKind.Graphics);
            var config=DeviceConfigurationCodec.SerializePackage(client.Snapshot.Configuration.Configuration);
            demo.GpuConnected=false;await demo.Runtime.PollAsync();await model.PollAsync();await Settle();
            Check(model.SelectedCacheHistory?.Id==gpu.Device.Id&&preview.IsEnabled,"An absent device appears in the bound history selector");
            var fan=model.Cooling.First();fan.Mode="固定";fan.Duty=62;await model.OperateAsync(fan);await Settle();
            config=DeviceConfigurationCodec.SerializePackage(client.Snapshot!.Configuration.Configuration);
            var writes=demo.Hubs.Sum(h=>h.Writes);Check(writes==1,"An independent mock fan is controlled before review");
            await model.PreviewCacheRetirementAsync();await Settle();
            Check(model.CanApplyCacheRetirement&&apply.IsEnabled,"A service-validated review enables explicit apply");
            Check(demo.Hubs.Sum(h=>h.Writes)==writes&&client.Snapshot!.State.Endpoints.Single(e=>e.Target==fan.Draft.Target).Owned,"Preview preserves independent control");
            Check(model.CacheRetirementText.Contains(gpu.Device.Id)&&model.CacheRetirementText.Contains("すべての制御")&&model.CacheRetirementText.Contains("設定は残ります"),"Review identifies target, global exit and preserved settings");
            model.CancelCacheRetirement.Execute(null);await Settle();Check(!model.HasCacheRetirementPreview&&demo.Hubs.Sum(h=>h.Writes)==writes,"Cancel has no output or cache mutation");
            await model.PreviewCacheRetirementAsync();demo.GpuConnected=true;await demo.Runtime.PollAsync();await model.PollAsync();await Settle();
            Check(!model.CanApplyCacheRetirement&&!apply.IsEnabled&&model.CacheHistoryChoices.Count==0,"Reconnection invalidates the reviewed retirement");
            demo.GpuConnected=false;await demo.Runtime.PollAsync();await model.PollAsync();await model.PreviewCacheRetirementAsync();await Settle();
            var review=Descendants<Border>(panel).Single(b=>AutomationProperties.GetName(b)=="検出履歴の整理内容");
            foreach(var theme in new[]{"system","light","dark"})
            {
                model.Theme=theme;await Settle();review.BringIntoView();await Settle();
                Check(review.TranslatePoint(new Point(),window).Y>=0&&review.TranslatePoint(new Point(0,review.ActualHeight),window).Y<=window.ActualHeight,"Review is reachable in "+theme+" theme");
                Capture(window,output,"cache-retirement-"+theme);
            }
            window.Width=window.MinWidth;window.Height=window.MinHeight;await Settle();apply.BringIntoView();await Settle();
            Check(apply.TranslatePoint(new Point(0,apply.ActualHeight),window).Y<=window.ActualHeight,"Apply stays reachable at minimum window size");
            Capture(window,output,"cache-retirement-minimum");
            await model.ApplyCacheRetirementAsync();await Settle();
            Check(!model.HasCacheRetirementPreview&&model.CacheHistoryChoices.Count==0&&!apply.IsEnabled,"Successful retirement removes the selected history");
            Check(demo.Hubs.Sum(h=>h.Writes)==writes+1&&!client.Snapshot!.State.Endpoints.Single(e=>e.Target==fan.Draft.Target).Owned,"Apply ends other control through the configured exit");
            Check(config.SequenceEqual(DeviceConfigurationCodec.SerializePackage(client.Snapshot!.Configuration.Configuration)),"Saved names and configuration remain byte equivalent");
            demo.GpuConnected=true;await demo.Runtime.PollAsync();await model.PollAsync();await Settle();
            Check(client.Snapshot!.State.Inventory.Devices.Single(d=>d.Device.Id==gpu.Device.Id).Generation>gpu.Generation,"Returning device receives a fresh generation");
            Check(model.DashboardTemperatures.Count==2&&model.DeviceNameChoices.Any(d=>d.Id==gpu.Device.Id),"Saved sensor name and dashboard recover after discovery");
            Check(demo.Hubs.All(h=>h.LightingWrites==0),"History operations emit no lighting writes");
            Check(await model.CloseAsync(),"Demo closes with all obligations resolved");
            await File.WriteAllLinesAsync(Path.Combine(output,"cache-retirement-smoke.txt"),report);return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e);await File.WriteAllLinesAsync(Path.Combine(output,"cache-retirement-smoke.txt"),report);return 1; }
    }
}
