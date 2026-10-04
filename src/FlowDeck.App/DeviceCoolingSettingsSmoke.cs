using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceCoolingSettingsAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,Window window,string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); report.Add("PASS "+label); }
        try
        {
            model.SelectedPage=1; var row=model.Cooling.First(r=>r.Draft.Target==new EndpointReference(demo.DeviceIds[0],"fan1"));
            row.Mode="固定"; row.Duty=63; await model.OperateAsync(row); model.OpenCoolingSettings(row); await Settle();
            var editor=row.SettingsEditor!; var profile=editor.SelectedProfile!;
            Check(row.ShowSettingsEditor&&editor.Sensors.Length==2&&!editor.Confirmed,"Cooling row opens the configured profile and sensor list without confirming changes");
            var name=Descendants<DraftTextBox>(window).Single(t=>ReferenceEquals(t.DataContext,editor));
            name.SetCurrentValue(TextBox.TextProperty,"静かな冷却"); await model.PollAsync(); await Settle();
            Check(editor.SelectedProfile!.Name=="静かな冷却"&&ReferenceEquals(row.SettingsEditor,editor),"Japanese curve name and editor survive telemetry polling");
            var curve=profile.Curves[0]; var graph=Descendants<CurveChart>(window).Single(c=>ReferenceEquals(c.DataContext,curve));
            Check(graph.IsVisible&&graph.CanEdit&&graph.BeginEdit(1),"Expanded row exposes the interactive graph");
            graph.DragTo(graph.Position(60,65)); graph.CommitEdit();
            Check(curve.Points[1].Duty==65&&!editor.Confirmed,"Dragging updates only the curve draft and requires confirmation");
            graph.BeginEdit(0); graph.DragTo(graph.Position(30,15)); graph.CommitEdit();
            Check(graph.MinimumDuty==30&&graph.RequestedOutput&&curve.Points[0].Duty==15,"Demand curves allow requests below the separately displayed endpoint minimum");
            graph.Undo();
            var gpu=editor.Sensors.Single(s=>s.Name.StartsWith("GPU"));
            var selector=Descendants<ComboBox>(window).Single(c=>ReferenceEquals(c.DataContext,editor)&&System.Windows.Automation.AutomationProperties.GetName(c)=="カーブに追加する温度源");
            selector.SetCurrentValue(Selector.SelectedItemProperty,gpu); editor.AddCurve.Execute(null); await Settle();
            Check(profile.Curves.Count==2&&!editor.AddCurve.CanExecute(null),"Explicitly adding GPU retains CPU and rejects duplicate sources");
            foreach(var point in profile.Curves[1].Points) point.Duty=80;
            await model.PollAsync(); await Settle();
            Check(curve.Temperature==52&&profile.Curves[1].Temperature==43&&curve.CurrentDuty==63,"Each graph follows its own live temperature and the actual fan output");
            Check((await demo.Hubs[0].ReadAsync()).Channels[0].Duty==63,"Graph edits do not alter manual output");
            editor.Confirmed=true; await model.PreviewCoolingSettingsAsync(editor);
            Check(editor.HasPreview&&editor.CanSave&&editor.PreviewText.Contains("GPU"),"Service preview includes both temperature sources");
            var delay=Descendants<Slider>(window).Single(s=>ReferenceEquals(s.DataContext,editor)&&System.Windows.Automation.AutomationProperties.GetName(s)=="出力の下降待ち");
            delay.SetCurrentValue(Slider.ValueProperty,4d); await Settle();
            Check(!editor.Confirmed&&!editor.HasPreview&&profile.DescentDelay==4,"Changing the descent slider invalidates preview and confirmation");
            editor.Confirmed=true; await model.PreviewCoolingSettingsAsync(editor);
            var save=Descendants<Button>(window).Single(b=>b.IsVisible&&Equals(b.Content,"制御を終了して設定を保存"));
            Check(save.Command?.CanExecute(save.CommandParameter)==true,"Visible save action is bound to the service transaction");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; await Settle(); Capture(window,output,"cooling-settings-"+theme); }
            save.BringIntoView(); await Settle(); Capture(window,output,"cooling-settings-confirmation");
            Descendants<ScrollViewer>(window).First(s=>s.Content is Grid).ScrollToTop();
            window.Width=960; window.Height=640; await Settle();
            Check(graph.ActualWidth>350&&Descendants<DeviceCoolingSettingsView>(window).First(v=>v.IsVisible).ActualWidth>400,"Cooling editor fits the minimum window width");
            Capture(window,output,"cooling-settings-minimum"); window.Width=1120; window.Height=820;
            await model.SaveCoolingSettingsAsync(editor); await Settle();
            Check(!row.HasSettingsEditor&&row.Profiles.Any(p=>p.Name=="静かな冷却")&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==70,"Saving returns to monitoring using the old exit output and exposes the renamed profile");
            row.Mode="自動"; row.ProfileId="balanced"; await model.OperateAsync(row);
            Check((await demo.Hubs[0].ReadAsync()).Channels[0].Duty==80&&demo.Hubs[1].Writes==0,"Explicit automatic apply uses the larger GPU demand and leaves the other Hub untouched");
            model.OpenCoolingSettings(row); editor=row.SettingsEditor!;
            editor.ProtectionSensor=editor.Sensors.Single(s=>s.Name.StartsWith("GPU")); editor.AddProtection.Execute(null);
            var rule=editor.Protection.Last(); rule.Overheat=80; rule.ResumeBelow=85; editor.Confirmed=true;
            Check(!editor.CanPreview,"Invalid manual protection thresholds cannot be saved");
            rule.ResumeBelow=70; editor.Confirmed=true; Check(editor.CanPreview,"Correcting protection thresholds restores preview availability");
            model.OpenCoolingSettings(row); Check(!row.ShowSettingsEditor&&ReferenceEquals(row.SettingsEditor,editor),"Collapsing preserves the per-fan draft");
            model.OpenCoolingSettings(row); Check(row.ShowSettingsEditor&&editor.Protection.Count==2,"Reopening retains protection edits");
            demo.Hubs[1].Disconnected=true; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(editor.Draft.Conflict&&!editor.CanSave&&editor.Protection.Count==2,"Topology changes retain drafts while disabling save");
            demo.Hubs[1].Disconnected=false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await model.ReloadCoolingSettingsAsync(editor);
            Check(!row.SettingsEditor!.Draft.Conflict&&row.SettingsEditor.Protection.Count==1,"Explicit reload replaces the draft with the persisted protection");
            Check(await model.CloseAsync(),"Normal close releases the mock cooling owner");
            await File.WriteAllLinesAsync(Path.Combine(output,"cooling-settings-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); await File.WriteAllLinesAsync(Path.Combine(output,"cooling-settings-smoke.txt"),report); return 1; }
    }
}
