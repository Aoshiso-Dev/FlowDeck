using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceLightingIdentificationAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, DeviceWorkspaceClient client, Window window, string output)
    {
        var report = new List<string>();
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); report.Add("PASS " + text); }
        async Task WaitForWhite()
        {
            for (var i=0;i<80;i++) {
                if(demo.Hubs[0].LightEffects.TryGetValue("led1",out var effect) && effect.Colors.SequenceEqual(new[] { new RgbColor(255,255,255) })) return;
                await Task.Delay(25);
            }
            throw new IOException("Identification did not reach the memory-backed Hub");
        }
        try
        {
            model.SelectedPage=2; model.SelectLights(model.Lights.Select(l=>l.Target)); await Settle();
            var expander=Descendants<Expander>(window).Single(e=>Equals(e.Header,"照明の名前")); expander.IsExpanded=true;
            model.LightAliasTarget=model.LightAliasTargets.Single(t=>t.Target==new EndpointReference(demo.DeviceIds[0],"led1"));
            await Settle(); var identify=Descendants<Button>(expander).Single(b=>Equals(b.Content,"3秒点灯"));
            var cancel=Descendants<Button>(expander).Single(b=>Equals(b.Content,"中止"));
            Check(model.LightIdentificationSupported && !model.CanIdentifyLight && !identify.IsEnabled && demo.Hubs.All(h=>h.LightingWrites==0),
                "Identification is advertised but disabled until a restorable output is known");
            var fan=model.Cooling.First(); fan.Mode="固定"; fan.Duty=55; await model.OperateAsync(fan);
            model.LightColor=Color.FromRgb(10,60,200); model.LightBrightness=47; await model.ApplyLightsAsync(); await Settle();
            Check(model.CanIdentifyLight && identify.IsEnabled,"Applying lighting enables the visible identification button");
            var lighting=JsonSerializer.Serialize(client.Snapshot!.Configuration.Configuration.Lighting,Contract.Json);
            var palette=JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration.Preferences.Palette,Contract.Json);
            var expected=JsonSerializer.Serialize(demo.Hubs[0].LightEffects["led1"],Contract.Json);
            var other=demo.Hubs[1].LightingWrites; var watch=Stopwatch.StartNew(); var running=model.IdentifyLightAsync();
            await WaitForWhite(); await model.PollAsync(); await Settle();
            Check(model.LightIdentificationRunning && !model.LightingIdle && !model.CanApplyLighting && !identify.IsEnabled && cancel.IsVisible && cancel.IsEnabled,
                "During identification lighting edits are disabled while the visible cancel button remains operable");
            Check(demo.Hubs[1].LightingWrites==other && model.Lights.Single(l=>l.Target==model.LightAliasTarget.Target).Status=="3秒間点灯中",
                "Only the selected endpoint receives white and its row reports the temporary state");
            fan.Duty=56; await model.OperateAsync(fan);
            Check(!running.IsCompleted && fan.CurrentDuty=="56%","Cooling still responds while the selected controller is identifying lighting");
            model.Theme="dark"; cancel.BringIntoView(); await Settle(); Capture(window,output,"lighting-identification-running-dark");
            await running; await Settle();
            Check(watch.Elapsed.TotalSeconds>=2.7 && watch.Elapsed.TotalSeconds<12 && !model.LightIdentificationRunning && model.CanIdentifyLight,
                "The service ends temporary lighting automatically after its three-second interval");
            Check(JsonSerializer.Serialize(demo.Hubs[0].LightEffects["led1"],Contract.Json)==expected && demo.Hubs[1].LightingWrites==other,
                "Automatic restoration returns the exact scaled fixed output and leaves the other Hub unchanged");
            Check(JsonSerializer.Serialize(client.Snapshot!.Configuration.Configuration.Lighting,Contract.Json)==lighting &&
                JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration.Preferences.Palette,Contract.Json)==palette,
                "Identification preserves saved lighting and colors independently of the concurrent cooling policy save");
            model.LightColor=Colors.Red; await Settle();
            Check(!model.CanIdentifyLight && !identify.IsEnabled && model.LightingDraft.Dirty,"Unsent color editing blocks identification without discarding the draft");
            model.ReloadLights.Execute(null); await model.PollAsync();
            model.LightEffect="native/nzxt-control-hub/covering-marquee"; model.LightColor=Color.FromRgb(200,0,0);
            model.AddLightColor.Execute(null); model.LightColor=Color.FromRgb(0,40,200); model.LightReverse=true; model.LightSpeed=4; model.LightBrightness=50;
            await model.ApplyLightsAsync();
            model.LightAlias="前面・入力中";
            expected=JsonSerializer.Serialize(demo.Hubs[0].LightEffects["led1"],Contract.Json); var settings=JsonSerializer.Serialize(client.Snapshot!.Configuration,Contract.Json);
            running=model.IdentifyLightAsync(); await WaitForWhite(); await Settle();
            cancel.Command.Execute(cancel.CommandParameter); await running; await Settle();
            Check(!model.LightIdentificationRunning && client.Snapshot!.State.Endpoints.Single(e=>e.Target==model.LightAliasTarget.Target).Identification is { Restored:true,Cancelled:true },
                "The visible cancel command requests service-owned early restoration");
            Check(JsonSerializer.Serialize(demo.Hubs[0].LightEffects["led1"],Contract.Json)==expected && JsonSerializer.Serialize(client.Snapshot!.Configuration,Contract.Json)==settings,
                "Early cancellation restores the native palette, direction, speed and brightness without persisting temporary white");
            Check(model.LightAlias=="前面・入力中" && model.CanSaveLightAlias,"Identification preserves the unsaved Japanese alias through refresh and policy changes");
            await model.SaveLightAliasAsync();
            Check(model.LightAliasTarget.Name.EndsWith("前面・入力中") && model.CanIdentifyLight,"The preserved Japanese name can be saved after identification");
            foreach(var theme in new[] { "light","dark" }) { model.Theme=theme; identify.BringIntoView(); await Settle(); Capture(window,output,"lighting-identification-"+theme); }
            model.LightEffect="breathing"; await model.ApplyLightsAsync(); await Settle();
            Check(!model.CanIdentifyLight && !identify.IsEnabled && model.LightIdentificationProblem!.Contains("同期"),
                "An active software synchronization prevents an identification write on its controller");
            model.LightEffect="fixed"; await model.ApplyLightsAsync();
            window.Width=960; window.Height=640; identify.BringIntoView(); await Settle(); Capture(window,output,"lighting-identification-minimum");
            var bounds=identify.TransformToAncestor(window).TransformBounds(new Rect(identify.RenderSize));
            Check(identify.IsEnabled && bounds.Top>=0 && bounds.Bottom<=window.ActualHeight,"Identification remains scroll-accessible at the minimum window size");
            running=model.IdentifyLightAsync(); await WaitForWhite();
            Check(await model.CloseAsync(),"Normal shutdown can finish while temporary lighting is active");
            await running;
            Check(!model.LightIdentificationRunning && demo.Runtime.State().Endpoints.All(e=>!e.Owned),
                "Shutdown ends identification and uses configured device release instead of replaying a stale effect");
            await File.WriteAllLinesAsync(Path.Combine(output,"lighting-identification-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); report.Add("STATE "+model.LightIdentificationMessage+" / "+model.LightIdentificationProblem+" / "+model.Notice); await File.WriteAllLinesAsync(Path.Combine(output,"lighting-identification-smoke.txt"),report); return 1; }
    }
}
