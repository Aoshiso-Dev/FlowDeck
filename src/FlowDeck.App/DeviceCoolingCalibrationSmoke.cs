using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceCoolingCalibrationAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,Window window,string output)
    {
        var report=new List<string>(); void Check(bool success,string label) { if(!success) throw new InvalidOperationException(label); report.Add("PASS "+label); }
        async Task Begun(int writes)=>await Task.Run(async()=>{ while(demo.Hubs[0].Writes<=writes) await Task.Delay(5); }).WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            model.SelectedPage=1; model.CalibrationExpanded=true; await Settle();
            var panel=Descendants<DeviceCoolingCalibrationView>(window).Single();
            var target=new EndpointReference(demo.DeviceIds[0],"fan1");
            Descendants<ComboBox>(panel).Single().SetCurrentValue(Selector.SelectedItemProperty,model.CalibrationChoices.Single(c=>c.Target==target)); await Settle();
            var editor=model.CalibrationEditor??throw new InvalidOperationException("Calibration editor missing"); Check(!model.CanStartCalibration&&!editor.CanReduce&&editor.Duty==100,"An unconfigured endpoint requires confirmation and begins at 100 percent");
            Check(demo.Hubs.All(h=>h.Writes==0),"Selecting a trial does not write output");
            var confirm=Descendants<CheckBox>(panel).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="端子試験の確認");
            confirm.SetCurrentValue(ToggleButton.IsCheckedProperty,true); await model.PollAsync(); await Settle();
            Check(model.CanStartCalibration&&editor!.Confirmed&&editor.Restoration.Contains("45%"),"Trial confirmation survives polling and displays the exact return duty");
            var trial=model.StartCalibrationAsync(); await Begun(0); await model.PollAsync(); await Settle();
            Check(model.CanCancelCalibration&&!model.SetupIdle&&!model.CanStartCalibration&&editor.CurrentDuty=="100%"&&editor.Restoration.Contains("45%"),"An active trial shows changing output, retains the original return duty and allows cancellation while setup is locked");
            await model.CancelCalibrationAsync(); await trial;
            Check(editor.Draft.Result is { Completed:false,Restored:true }&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==45&&!editor.CanReduce,"Cancellation restores output without granting calibration proof");
            editor.Confirmed=true; await model.StartCalibrationAsync(); await Settle();
            Check(editor.Successful&&editor.CanReduce&&!editor.Confirmed&&!model.CanAdoptCalibration,"A completed full-output test enables the trial slider but does not auto-confirm a minimum");
            var slider=Descendants<Slider>(panel).Single(); slider.SetCurrentValue(Slider.ValueProperty,35d); await model.PollAsync();
            Check(editor.Duty==35&&!editor.Confirmed&&demo.Hubs[0].Writes==4,"Dragging the trial slider only changes the draft");
            editor.Confirmed=true; await model.StartCalibrationAsync(); await Settle();
            Check(editor.Draft.Result is { Duty:35,Completed:true,Restored:true }&&editor.ResultRpm.Contains("RPM")&&demo.Hubs[0].Writes==6,"A lower trial reports RPM range and restores the original output");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; panel.BringIntoView(); await Settle(); Capture(window,output,"cooling-calibration-"+theme); }
            window.Width=960; window.Height=640; panel.BringIntoView(); await Settle(); Capture(window,output,"cooling-calibration-minimum");
            Descendants<Button>(panel).Single(b=>b.Content?.ToString()=="最低出力の編集欄に反映").BringIntoView(); await Settle(); Capture(window,output,"cooling-calibration-minimum-result");
            window.Width=1120; window.Height=820; await Settle();
            var stable=Descendants<CheckBox>(panel).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="最低出力の回転確認");
            stable.SetCurrentValue(ToggleButton.IsCheckedProperty,true); await Settle(); Check(model.CanAdoptCalibration,"An explicit stable-rotation confirmation enables minimum adoption");
            model.AdoptCalibrationMinimum(); await Settle();
            Check(model.SelectedPage==3&&model.SetupExpanded&&model.SetupEditor is { Minimum:35,Confirmed:false }&&!model.CanPreviewSetup,"Adoption opens an unconfirmed settings draft and does not save or actuate");
            model.SetupEditor!.Name="校正済みファン"; model.SetupEditor.Confirmed=true; await model.PreviewSetupAsync();
            Check(model.SetupPreviewText.Contains("35%")&&demo.Hubs[0].Writes==6,"The saved setup preview retains explicit confirmation and sends nothing");
            await model.ApplySetupAsync(); var row=model.Cooling.Single(r=>r.Draft.Target==target);
            Check(row.Minimum==35&&demo.Hubs[0].Writes==6&&demo.Hubs[1].Writes==0,"Only the selected endpoint receives the confirmed minimum without output changes");
            model.SelectedPage=1; await model.ReloadCalibrationAsync(); editor=model.CalibrationEditor!;
            Check(model.SelectedCalibration?.Name.Contains("校正済みファン")==true,"Explicit reload updates the endpoint name without changing output");
            editor.Confirmed=true; trial=model.StartCalibrationAsync(); await Begun(6); await model.PollAsync();
            Check(!model.CanAdoptCalibration,"Minimum adoption is disabled during a new trial");
            Check(await model.CloseAsync(),"Closing the application releases an active trial"); await trial;
            Check((await demo.Hubs[0].ReadAsync()).Channels[0].Duty==45,"Closing during calibration restores the pre-test duty instead of the ordinary configured exit");
            await File.WriteAllLinesAsync(Path.Combine(output,"cooling-calibration-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); report.Add("STATE "+model.CalibrationEditor?.Status+" / "+model.Notice); await File.WriteAllLinesAsync(Path.Combine(output,"cooling-calibration-smoke.txt"),report); return 1; }
    }
}
