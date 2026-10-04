using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceCoolingLifecycleAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,DeviceWorkspaceClient client,Window window,string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); report.Add("PASS "+label); }
        try
        {
            Check(!model.StartupEnabled&&model.StartupRows.Count==0,"Fresh setup has no startup policy selected or eligible before an actual apply");
            var target=new EndpointReference(demo.DeviceIds[1],"fan5"); var row=model.Cooling.Single(r=>r.Draft.Target==target);
            model.SelectedPage=1; model.OpenCoolingSettings(row); var editor=row.SettingsEditor!; await Settle();
            var panel=Descendants<DeviceCoolingSettingsView>(window).Single(v=>ReferenceEquals(v.DataContext,editor));
            var stop=Descendants<Expander>(panel).Single(e=>Equals(e.Header,"停止・再始動")); stop.IsExpanded=true; stop.BringIntoView(); await Settle();
            Check(editor.Draft.Pump&&editor.StopSectionVisible&&!editor.StopEnabled&&editor.StopLabel.Contains("ポンプ"),"A pump exposes explicit stop permission and starts disabled");
            var permission=Descendants<CheckBox>(stop).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="停止を許可");
            permission.SetCurrentValue(ToggleButton.IsCheckedProperty,true); editor.Confirmed=true;
            Check(editor.StopEnabled&&!editor.CanPreview,"Stop permission alone cannot save without temperature conditions");
            var selector=Descendants<ComboBox>(stop).Single();
            foreach(var sensor in editor.Sensors)
            { selector.SetCurrentValue(Selector.SelectedItemProperty,sensor); editor.AddStopCondition.Execute(null); }
            await Settle();
            foreach(var slider in Descendants<Slider>(stop).Where(s=>System.Windows.Automation.AutomationProperties.GetName(s)=="停止温度")) slider.SetCurrentValue(Slider.ValueProperty,60d);
            Descendants<Slider>(stop).Single(s=>System.Windows.Automation.AutomationProperties.GetName(s)=="再始動出力").SetCurrentValue(Slider.ValueProperty,80d);
            Descendants<Slider>(stop).Single(s=>System.Windows.Automation.AutomationProperties.GetName(s)=="再始動時間").SetCurrentValue(Slider.ValueProperty,1d);
            await model.PollAsync(); await Settle();
            Check(editor.StopConditions.Count==2&&editor.StopConditions.All(r=>r.Below==60&&r.Temperature.HasValue)&&editor.RestartDuty==80,"Sliders update both stop thresholds and restart output while telemetry leaves the edits intact");
            Check(!editor.AddStopCondition.CanExecute(null)&&!editor.Confirmed,"Duplicate conditions are blocked and edits invalidate confirmation");
            editor.Confirmed=true; await model.PreviewCoolingSettingsAsync(editor);
            Check(editor.HasPreview&&editor.PreviewText.Contains("すべて満たす")&&editor.PreviewText.Contains("80%"),"Review states the all-temperatures condition and restart boost");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; stop.BringIntoView(); await Settle(); Capture(window,output,"cooling-stop-"+theme); }
            await model.PollAsync(); await model.SaveCoolingSettingsAsync(editor); await Settle();
            Check(row.Minimum==0&&demo.Hubs.All(h=>h.Writes==0),"Saving permits zero in the fan slider without changing output");
            row.Mode="固定"; row.Duty=0; await model.OperateAsync(row);
            Check((await demo.Hubs[1].ReadAsync()).Channels[4].Duty==0,"Explicit zero applies only after the selected temperature conditions are satisfied");
            row.Duty=40; await model.OperateAsync(row);
            Check((await demo.Hubs[1].ReadAsync()).Channels[4].Duty==80,"Leaving zero starts at the configured boost");
            await Task.Delay(1100); await demo.Runtime.TickCoolingAsync(); await model.PollAsync();
            Check((await demo.Hubs[1].ReadAsync()).Channels[4].Duty==40,"Confirmed boost duration ends at the requested output");
            model.SelectedPage=3; await Settle(); var startup=Descendants<Expander>(window).Single(e=>Equals(e.Header,"起動時の冷却")); startup.IsExpanded=true; startup.BringIntoView(); await Settle();
            var candidate=model.StartupRows.Single(r=>r.Target==target); var master=Descendants<CheckBox>(startup).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="起動時の冷却を再開");
            master.SetCurrentValue(ToggleButton.IsCheckedProperty,true); await Settle();
            var selected=Descendants<CheckBox>(startup).Single(c=>ReferenceEquals(c.DataContext,candidate)); selected.SetCurrentValue(ToggleButton.IsCheckedProperty,true);
            Check(candidate.Selected&&model.CanSaveStartup&&candidate.Policy.Contains("40%"),"Only the applied policy can be selected for startup");
            var writes=demo.Hubs[1].Writes; await model.SaveStartupAsync(); await Settle();
            Check(!model.CanSaveStartup&&model.StartupEnabled&&demo.Hubs[1].Writes==writes&&(await demo.Hubs[1].ReadAsync()).Channels[4].Duty==40,"Startup preference save does not release the current cooling owner or write hardware");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; startup.BringIntoView(); await Settle(); Capture(window,output,"cooling-startup-"+theme); }
            model.StartupEnabled=false; row.Duty=45; await model.OperateAsync(row);
            Check(!model.CanSaveStartup&&model.StartupStatus.Contains("変わり"),"A changed applied policy invalidates an unsaved startup selection");
            await model.ReloadStartupAsync(); Check(model.StartupEnabled&&model.StartupRows.Single(r=>r.Target==target).Policy.Contains("45%"),"Explicit reload shows the latest confirmed policy");
            Check(await model.CloseAsync(),"Closing the GUI releases cooling before the next simulated launch");
            // The installed host disposes the transport after CloseAsync confirms output release.
            // Match that boundary before opening the next single-client service connection.
            await client.DisposeAsync();
            Check(client.Snapshot is { Connected:false },"The previous launch closes its transport before opening the next connection");
            await using var nextClient=demo.CreateClient(); var next=new DeviceWorkspaceViewModel(nextClient,new DeviceUiPreferencesStore(Path.Combine(output,"next-ui.json")));
            await next.InitializeAsync(); window.DataContext=next; var before=demo.Hubs[1].Writes; await next.RestoreStartupAsync(); await Settle();
            Check(demo.Hubs[1].Writes==before+1&&(await demo.Hubs[1].ReadAsync()).Channels[4].Duty==45&&demo.Hubs[0].Writes==0,"A fresh launch restores exactly the selected pump policy");
            await next.RestoreStartupAsync(); await next.PollAsync(); Check(demo.Hubs[1].Writes==before+1,"Repeated startup calls and polling do not replay output");
            next.SelectedPage=3; await Settle(); var last=Descendants<Expander>(window).Single(e=>Equals(e.Header,"起動時の冷却")); last.IsExpanded=true; last.BringIntoView();
            window.Width=960; window.Height=640; await Settle(); Capture(window,output,"cooling-startup-minimum");
            next.StartupEnabled=false; await next.SaveStartupAsync(); Check(!next.StartupEnabled&&(await demo.Hubs[1].ReadAsync()).Channels[4].Duty==45,"Disabling future startup leaves the running policy unchanged");
            Check(await next.CloseAsync(),"Normal close completes after startup verification");
            await File.WriteAllLinesAsync(Path.Combine(output,"cooling-lifecycle-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); await File.WriteAllLinesAsync(Path.Combine(output,"cooling-lifecycle-smoke.txt"),report); return 1; }
    }
}
