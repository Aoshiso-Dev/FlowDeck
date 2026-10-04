using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceCoolingGroupsAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,Window window,string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); report.Add("PASS "+label); }
        try
        {
            model.SelectedPage=1; await Settle();
            var expander=Descendants<Expander>(window).Single(e=>Equals(e.Header,"グループ")); expander.IsExpanded=true; await Settle();
            var panel=Descendants<DeviceCoolingGroupsView>(window).Single();
            Check(model.CoolingGroupsSupported&&model.CoolingGroups.Count==0&&!model.HasCoolingGroup,"Fresh cooling has no implicit group or selected control target");
            model.NewCoolingGroup.Execute(null); await Settle();
            var a=new EndpointReference(demo.DeviceIds[0],"fan1"); var b=new EndpointReference(demo.DeviceIds[1],"fan1"); var c=new EndpointReference(demo.DeviceIds[1],"fan3");
            Descendants<DraftTextBox>(panel).Single().Text="ケース冷却";
            foreach(var target in new[]{a,b}) Descendants<CheckBox>(panel).Single(box=>box.DataContext is CoolingGroupChoiceRow row&&row.Target==target).SetCurrentValue(ToggleButton.IsCheckedProperty,true);
            await model.PollAsync(); await Settle();
            Check(model.GroupEditor!.Name=="ケース冷却"&&model.GroupEditor.Members.Count(r=>r.Selected)==2&&model.CanSaveCoolingGroup,"Japanese group name and endpoint checks survive telemetry polling");
            await model.SaveCoolingGroupAsync(); await Settle(); var id=model.SelectedCoolingGroup!.Id;
            Check(model.CoolingGroups.Count==1&&model.CoolingGroupReadings.Count==2&&demo.Hubs.All(h=>h.Writes==0),"Saving a group selects only its members and writes no output");
            var slider=Descendants<Slider>(panel).Single(s=>System.Windows.Automation.AutomationProperties.GetName(s)=="グループの要求出力"); slider.SetCurrentValue(Slider.ValueProperty,25d);
            await model.PollAsync(); Check(model.CoolingGroupDuty==25&&model.CanApplyCoolingGroup,"A group request below endpoint minima is preserved until explicit apply");
            await model.OperateCoolingGroupAsync(); await Settle();
            Check(model.CoolingGroupDuty==25&&model.CoolingGroupReadings.All(r=>r.Duty=="30%")&&model.Notice.Contains("確認"),"Requested 25 percent remains distinct from both reported 30 percent outputs");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; expander.BringIntoView(); await Settle(); Capture(window,output,"cooling-groups-"+theme); }
            model.EditCoolingGroup.Execute(null); await Settle(); Descendants<DraftTextBox>(panel).Single().Text="天面・ケース";
            Descendants<CheckBox>(panel).Single(box=>box.DataContext is CoolingGroupChoiceRow row&&row.Target==c).SetCurrentValue(ToggleButton.IsCheckedProperty,true);
            var before=demo.Hubs.Select(h=>h.Writes).ToArray(); await model.SaveCoolingGroupAsync();
            Check(model.SelectedCoolingGroup?.Id==id&&model.CoolingGroupReadings.Count==3&&demo.Hubs.Select(h=>h.Writes).SequenceEqual(before)&&(await demo.Hubs[1].ReadAsync()).Channels[2].Duty==45,"Editing active group membership retains selection without sending to added endpoints or releasing existing outputs");
            model.CoolingGroupMode="自動"; model.CoolingGroupProfile=model.CoolingGroupProfiles.First(); await model.OperateCoolingGroupAsync();
            Check(model.CoolingGroupAutomatic&&model.CoolingGroupProfile is not null&&model.CoolingGroupReadings.All(r=>r.Status.Contains("自動")),"One selected curve applies to all group members with per-endpoint execution status");
            model.CoolingGroupMode="固定"; model.CoolingGroupDuty=66; demo.Hubs[1].FailNextFanWrite=true;
            // fan1 is first on that controller, so use a group containing only the intended failure endpoint and the independent controller.
            model.OpenCoolingGroupEditor(id); model.GroupEditor!.Members.Single(r=>r.Target==b).Selected=false; await model.SaveCoolingGroupAsync();
            model.CoolingGroupMode="固定"; model.CoolingGroupDuty=66; await model.OperateCoolingGroupAsync();
            Check(model.Notice.Contains("一部")&&model.CoolingGroupReadings.Single(r=>r.Target==a).Duty=="66%"&&model.CoolingGroupReadings.Single(r=>r.Target==c).Result.Length>0,"Partial failure preserves independent success and displays endpoint results");
            await model.ReloadCoolingGroupsAsync(); await model.OperateCoolingGroupAsync(DeviceOperation.ReleaseCooling);
            Check(model.CoolingGroupReadings.All(r=>r.Duty=="70%")&&model.CoolingGroupDuty==70,"Group release shows each exit result and follows common reported output");
            model.OpenCoolingGroupEditor(id); var name=model.GroupEditor!.Name; model.GroupEditor.Name=name+"編集中";
            var single=model.Cooling.Single(r=>r.Draft.Target==b); single.Mode="固定"; single.Duty=72; await model.OperateAsync(single);
            Check(model.GroupEditor.Name==name+"編集中"&&!model.CanSaveCoolingGroup,"A changed saved policy retains the group name draft and blocks a stale save");
            await model.ReloadCoolingGroupsAsync(); Check(model.GroupEditor!.Name==name&&model.CanSaveCoolingGroup,"Explicit reload restores the persisted group editor");
            window.Width=960; window.Height=640; expander.BringIntoView(); await Settle(); Capture(window,output,"cooling-groups-minimum");
            before=demo.Hubs.Select(h=>h.Writes).ToArray(); await model.SaveCoolingGroupAsync(true);
            Check(model.CoolingGroups.Count==0&&!model.HasCoolingGroup&&demo.Hubs.Select(h=>h.Writes).SequenceEqual(before),"Deleting a group does not alter independently running cooling");
            Check(await model.CloseAsync(),"Normal exit releases all independently owned endpoints");
            await File.WriteAllLinesAsync(Path.Combine(output,"cooling-groups-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); report.Add($"STATE selected={model.SelectedCoolingGroup?.Name} mode={model.CoolingGroupMode} profile={model.CoolingGroupProfile?.Name} status={model.CoolingGroupStatus} notice={model.Notice}"); foreach(var row in model.CoolingGroupReadings) report.Add($"ROW {row.Name}: {row.Status} {row.Duty} {row.Result}"); await File.WriteAllLinesAsync(Path.Combine(output,"cooling-groups-smoke.txt"),report); return 1; }
    }
}
