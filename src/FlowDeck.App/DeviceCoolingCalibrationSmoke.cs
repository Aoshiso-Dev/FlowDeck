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
            var editor=model.CalibrationEditor??throw new InvalidOperationException("Calibration editor missing"); Check(model.CanStartCalibration&&!editor.CanReduce&&editor.Duty==100,"A fresh endpoint can start the explicit full-output trial without approval and begins at 100 percent");
            Check(demo.Hubs.All(h=>h.Writes==0),"Selecting a trial does not write output");
            Check(!Descendants<CheckBox>(panel).Any(),"Calibration has no trial or result approval checkbox");
            await model.PollAsync(); await Settle();
            Check(model.CanStartCalibration&&editor.Restoration.Contains("45%"),"Polling preserves trial availability and displays the exact return duty");
            var trial=model.StartCalibrationAsync(); await Begun(0); await model.PollAsync(); await Settle();
            Check(model.CanCancelCalibration&&!model.SetupIdle&&!model.CanStartCalibration&&editor.CurrentDuty=="100%"&&editor.Restoration.Contains("45%"),"An active trial shows changing output, retains the original return duty and allows cancellation while setup is locked");
            await model.CancelCalibrationAsync(); await trial;
            Check(editor.Draft.Result is { Completed:false,Restored:true }&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==45&&!editor.CanReduce,"Cancellation restores output without granting calibration proof");
            await model.StartCalibrationAsync(); await Settle();
            Check(editor.Successful&&editor.CanReduce&&model.CanAdoptCalibration,"A completed and restored full-output test enables lower trials and result adoption without approval");
            var slider=Descendants<Slider>(panel).Single(); slider.SetCurrentValue(Slider.ValueProperty,35d); await model.PollAsync();
            Check(editor.Duty==35&&model.CanStartCalibration&&demo.Hubs[0].Writes==4,"Dragging the trial slider only changes the draft and leaves the explicit trial action available");
            await model.StartCalibrationAsync(); await Settle();
            Check(editor.Draft.Result is { Duty:35,Completed:true,Restored:true }&&editor.ResultRpm.Contains("RPM")&&demo.Hubs[0].Writes==6,"A lower trial reports RPM range and restores the original output");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; panel.BringIntoView(); await Settle(); Capture(window,output,"cooling-calibration-"+theme); }
            window.Width=960; window.Height=640; panel.BringIntoView(); await Settle(); Capture(window,output,"cooling-calibration-minimum");
            Descendants<Button>(panel).Single(b=>b.Content?.ToString()=="最低出力欄に反映").BringIntoView(); await Settle(); Capture(window,output,"cooling-calibration-minimum-result");
            window.Width=1120; window.Height=820; await Settle();
            Check(model.CanAdoptCalibration&&!Descendants<CheckBox>(panel).Any(),"The successful restored measurement enables minimum adoption without a rotation approval checkbox");
            model.AdoptCalibrationMinimum(); await Settle();
            Check(model.SelectedPage==3&&model.SetupExpanded&&model.SetupEditor is { Minimum:35 }&&!model.CanApplySetup&&demo.Hubs[0].Writes==6,"Adoption fills the measured minimum but still requires the unknown fallback and exit settings without saving or actuating");
            model.SetupEditor!.Name="校正済みファン"; model.SetupEditor.Fallback=100; model.SetupEditor.ExitDuty=70;
            Check(model.CanApplySetup&&model.SetupEditor.Minimum==35&&demo.Hubs[0].Writes==6,"Editing the calibrated settings keeps the measured minimum and sends nothing");
            await model.ApplySetupAsync(); var row=model.Cooling.Single(r=>r.Draft.Target==target);
            Check(row.Minimum==35&&demo.Hubs[0].Writes==6&&demo.Hubs[1].Writes==0,"One save validates and stores only the selected endpoint's measured minimum without output changes");
            model.SelectedPage=1; await model.ReloadCalibrationAsync(); editor=model.CalibrationEditor!;
            Check(model.SelectedCalibration?.Name.Contains("校正済みファン")==true,"Explicit reload updates the endpoint name without changing output");
            trial=model.StartCalibrationAsync(); await Begun(6); await model.PollAsync();
            Check(!model.CanAdoptCalibration,"Minimum adoption is disabled during a new trial");
            Check(await model.CloseAsync(),"Closing the application releases an active trial"); await trial;
            Check((await demo.Hubs[0].ReadAsync()).Channels[0].Duty==45,"Closing during calibration restores the pre-test duty instead of the ordinary configured exit");
            await File.WriteAllLinesAsync(Path.Combine(output,"cooling-calibration-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); report.Add("STATE "+model.CalibrationEditor?.Status+" / "+model.Notice); await File.WriteAllLinesAsync(Path.Combine(output,"cooling-calibration-smoke.txt"),report); return 1; }
    }
}
