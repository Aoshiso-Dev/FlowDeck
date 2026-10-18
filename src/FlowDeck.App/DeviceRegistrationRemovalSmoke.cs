using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceRegistrationRemovalAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,DeviceWorkspaceClient client,Window window,string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label);report.Add("PASS "+label); }
        try
        {
            model.SelectedPage=3;await Settle();
            var panel=Descendants<Expander>(window).Single(e=>Equals(e.Header,"機器の管理"));panel.SetCurrentValue(Expander.IsExpandedProperty,true);await Settle();
            var selector=Descendants<ComboBox>(panel).Single(c=>AutomationProperties.GetName(c)=="名前を変更する機器");
            var previewButton=Descendants<Button>(panel).Single(b=>AutomationProperties.GetName(b)=="機器の登録解除を確認");
            var applyButton=Descendants<Button>(panel).Single(b=>AutomationProperties.GetName(b)=="機器の登録解除を適用");
            var cpu=client.Snapshot!.Configuration.Configuration.Dashboard.Items.Single(i=>i.Name=="CPU").Target.DeviceId;
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==cpu));await Settle();
            Check(!previewButton.IsEnabled&&model.RegistrationRemovalStatus.Contains("温度参照"),"A sensor referenced by fan safety or curves cannot be removed through the bound button");
            var a=demo.DeviceIds[0];var b=demo.DeviceIds[1];
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==a));await Settle();
            // Smoke mode has no polling timer. Layout work may outlive the three-second command freshness window.
            await model.PollAsync();
            var fan=model.Cooling.Single(r=>r.Draft.Target==new EndpointReference(a,"fan1"));fan.Mode="固定";fan.Duty=62;await model.OperateAsync(fan);await Settle();
            var writes=demo.Hubs[0].Writes;Check(writes==1&&previewButton.IsEnabled,$"Configured Hub can be reviewed while its mock fan runs (writes={writes}, enabled={previewButton.IsEnabled}, status={model.RegistrationRemovalStatus}, notice={model.Notice})");
            await model.PreviewRegistrationRemovalAsync();await Settle();
            Check(model.HasRegistrationRemovalPreview&&model.CanApplyRegistrationRemoval&&applyButton.IsEnabled,"A validated review enables the explicit removal button");
            Check(demo.Hubs[0].Writes==writes&&client.Snapshot!.State.Endpoints.Single(e=>e.Target==fan.Draft.Target).Owned,"Review leaves the running fan untouched");
            Check(model.RegistrationRemovalText.Contains("冷却設定 5件")&&model.RegistrationRemovalText.Contains("すべての制御")&&model.RegistrationRemovalText.Contains(a),"Review identifies the exact device, removed controls and exit impact");
            var name=Descendants<DraftTextBox>(panel).Single(t=>AutomationProperties.GetName(t)=="機器の名前");
            name.SetCurrentValue(TextBox.TextProperty,"編集中の日本語名");await Settle();
            Check(!model.HasRegistrationRemovalPreview&&!model.CanPreviewRegistrationRemoval,"Editing the name cancels the review and requires resolving the draft");
            await model.ReloadDeviceNameAsync();await model.PreviewRegistrationRemovalAsync();
            model.CancelRegistrationRemoval.Execute(null);await Settle();
            Check(!model.HasRegistrationRemovalPreview&&demo.Hubs[0].Writes==writes,"Cancel preserves saved registration and active output");
            await model.PreviewRegistrationRemovalAsync();
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==b));await Settle();
            Check(!model.HasRegistrationRemovalPreview&&!model.CanApplyRegistrationRemoval,"Selecting another device cannot apply the earlier review");
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==a));await Settle();await model.PreviewRegistrationRemovalAsync();
            demo.Hubs[1].Disconnected=true;await demo.Runtime.PollAsync();await model.PollAsync();await Settle();
            Check(!model.CanApplyRegistrationRemoval&&!applyButton.IsEnabled&&model.CanPreviewRegistrationRemoval,"Topology changes block stale apply but allow reviewing again");
            await model.ApplyRegistrationRemovalAsync();Check(demo.Hubs[0].Writes==writes,"Invoking stale apply produces no output");
            demo.Hubs[1].Disconnected=false;await demo.Runtime.PollAsync();await demo.Runtime.PollAsync();await model.ReloadDeviceNameAsync();await model.PreviewRegistrationRemovalAsync();
            var review=Descendants<Border>(panel).Single(c=>AutomationProperties.GetName(c)=="機器登録解除の差分");
            foreach(var theme in new[]{"system","light","dark"})
            {
                model.Theme=theme;await Settle();review.BringIntoView();await Settle();
                Check(review.TranslatePoint(new Point(),window).Y>=0&&review.TranslatePoint(new Point(0,review.ActualHeight),window).Y<=window.ActualHeight,"Registration review is reachable in "+theme+" theme");
                Capture(window,output,"registration-removal-"+theme);
            }
            window.Width=window.MinWidth;window.Height=window.MinHeight;await Settle();applyButton.BringIntoView();await Settle();
            Check(applyButton.TranslatePoint(new Point(),window).Y>=0&&applyButton.TranslatePoint(new Point(0,applyButton.ActualHeight),window).Y<=window.ActualHeight,"Apply remains reachable at the minimum window size");
            Capture(window,output,"registration-removal-minimum");
            await model.ApplyRegistrationRemovalAsync();await Settle();
            Check(demo.Hubs[0].Writes==writes+1&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==70,"Registration removal uses the saved 70 percent fan exit");
            Check(model.DeviceNameChoices.All(c=>c.Id!=a)&&model.DashboardFans.Count==5&&!model.HasRegistrationRemovalPreview,"Saved name and monitoring entries are removed together");
            Check(model.Cooling.Where(c=>c.Draft.Target.DeviceId==a).All(c=>!c.CanApply&&c.NeedsSetup)&&model.Devices.Any(c=>c.Id==a)&&
                model.Lights.Where(l=>l.Target.DeviceId==a).All(l=>l.Ready&&!l.NeedsSetup),"Deleting saved settings keeps hardware detected: cooling needs output values while known lighting remains ready without registration");
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==b));await Settle();
            model.EditedDeviceName="切断中も保持する機器名";
            var savedNameDraft=model.SelectedDeviceNameDraft!;
            demo.Hubs[1].Disconnected=true;await demo.Runtime.PollAsync();await model.PollAsync();
            await Settle();
            Check(model.DeviceNameChoices.All(c=>c.Id!=b)&&model.SelectedDeviceName?.Id!=b&&model.DashboardFans.Count==0&&
                client.Snapshot!.Configuration.Configuration.Devices.Devices.Any(d=>d.Id==b)&&savedNameDraft.Name=="切断中も保持する機器名",
                "Disconnect hides the selected device and monitoring while retaining its registration and unsaved name");
            demo.Hubs[1].Disconnected=false;await demo.Runtime.PollAsync();await demo.Runtime.PollAsync();await model.PollAsync();
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==b));await Settle();
            Check(ReferenceEquals(savedNameDraft,model.SelectedDeviceNameDraft)&&model.EditedDeviceName=="切断中も保持する機器名"&&
                !model.CanPreviewRegistrationRemoval&&demo.Hubs[1].Writes==0,
                "Reconnect restores the unsaved name draft and keeps removal blocked until that draft is resolved");
            await model.ReloadDeviceNameAsync();
            Check(model.CanPreviewRegistrationRemoval,"The reconnected saved device supports explicit registration removal after reloading its name");
            await model.PreviewRegistrationRemovalAsync();await model.ApplyRegistrationRemovalAsync();
            Check(model.DeviceNameChoices.All(c=>c.Id!=b)&&model.DashboardFans.Count==0&&demo.Hubs[1].Writes==0,"Explicit removal of the connected unowned device deletes saved entries without hardware output");
            await model.ReloadSetupAsync();
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==new EndpointReference(b,"fan1"));await Settle();
            Check(model.SetupEditor is { Cooling:true } editor&&editor.Draft.Values.Safety is { Minimum:null,Fallback:null,Exit:null }&&!model.CanApplySetup&&
                model.DeviceNameChoices.All(c=>c.Id!=b)&&model.Lights.Where(l=>l.Target.DeviceId==b).All(l=>l.Ready&&!l.NeedsSetup),
                "After removal, the connected device exposes missing cooling output values and ready known lighting without recreating registration");
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.DeviceNameChoices.Single(c=>c.Id==cpu));await Settle();
            Check(model.CanPreviewRegistrationRemoval,"The sensor becomes removable after its dependent fan configurations are removed");
            await model.PreviewRegistrationRemovalAsync();await model.ApplyRegistrationRemovalAsync();
            Check(model.DashboardTemperatures.Count==1&&demo.Hubs.All(h=>h.LightingWrites==0),"Sensor registration removal updates monitoring without lighting output");
            Check(await model.CloseAsync(),"The mock workspace exits with all obligations resolved");
            await File.WriteAllLinesAsync(Path.Combine(output,"registration-removal-smoke.txt"),report);return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e);await File.WriteAllLinesAsync(Path.Combine(output,"registration-removal-smoke.txt"),report);return 1; }
    }
}
