using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceEndpointSetupAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); report.Add("PASS "+label); }
        try
        {
            model.SelectedPage=3; await Settle();
            var setupPanel=Descendants<Expander>(window).Single(e=>Equals(e.Header,"端子の設定"));
            setupPanel.SetCurrentValue(Expander.IsExpandedProperty,true); await Settle();
            Check(model.SetupChoices.Count==396 && model.SetupEditor is null && !model.CanApplySetup,"Setup lists all 396 supported detected ports without automatically choosing one");
            Check(!Descendants<CheckBox>(setupPanel).Any()&&!Descendants<Button>(setupPanel).Any(b=>Equals(b.Content,"差分を確認")),"Endpoint settings contain no approval checkbox or separate review action");
            await RunCoolingSetupNavigationAsync(model, demo, window, output, Check);
            var fan=new EndpointReference(demo.DeviceIds[0],"fan1");
            var selector=Descendants<ComboBox>(window).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="設定する端子");
            var last=model.SetupChoices.Last();
            selector.SetCurrentValue(Selector.SelectedItemProperty,last); await Settle();
            Check(model.SetupEditor?.Draft.Candidate.Target==last.Target&&last.Target.EndpointId=="zone-128","The last candidate beyond 256 is reachable through the bound selector");
            var lastEditor=model.SetupEditor!; lastEditor.Name="最後の照明"; await model.PollAsync(); await Settle();
            Check(ReferenceEquals(lastEditor,model.SetupEditor)&&model.SelectedSetup==last&&lastEditor.Name=="最後の照明","Large candidate list preserves selected endpoint and input through polling");
            Check(model.SetupUsage.Contains("照明 0 / 128")&&demo.Hubs.All(h=>h.Writes==0&&h.LightingWrites==0),"Listing and editing detected candidates neither consumes saved control reservations nor emits output");
            selector.BringIntoView(); await Settle(); Capture(window,output,"endpoint-setup-many-candidates");
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.SetupChoices.Single(c=>c.Target==fan)); await Settle();
            var editor=model.SetupEditor!;
            Check(editor.Cooling&&!model.CanApplySetup&&editor.Draft.Values.Safety is { Minimum:null,Fallback:null,Exit:null },"A new cooling port requires actual output settings rather than approval or assumed values");
            SetupTextBox(window,editor,"端子の名前").SetCurrentValue(TextBox.TextProperty,"背面ファン");
            var minimum=SetupTextBox(window,editor,"最低出力");
            minimum.SetCurrentValue(TextBox.TextProperty,"35"); await Settle();
            Check(!model.CanApplySetup,"Entering only the minimum keeps Save disabled while fallback and exit are unknown");
            SetupTextBox(window,editor,"異常時出力").SetCurrentValue(TextBox.TextProperty,"100");
            SetupTextBox(window,editor,"終了時出力").SetCurrentValue(TextBox.TextProperty,"70");
            await model.PollAsync(); await Settle();
            Check(ReferenceEquals(model.SetupEditor,editor)&&editor.Name=="背面ファン"&&editor.Minimum==35,"Name and numeric input edits survive polling");
            Check(model.CanApplySetup,"Valid actual settings enable a single Save action without approval");
            Check(demo.Hubs.All(h=>h.Writes==0&&h.LightingWrites==0),"Editing complete settings sends no hardware output");
            minimum.SetCurrentValue(TextBox.TextProperty,"101"); await Settle(); await model.ApplySetupAsync();
            Check(!model.CanApplySetup&&demo.Hubs.All(h=>h.Writes==0&&h.LightingWrites==0),"An invalid minimum blocks Save and produces no hardware output");
            minimum.SetCurrentValue(TextBox.TextProperty,"36"); await Settle();
            foreach(var theme in new[]{"light","dark"})
            {
                model.Theme=theme; await Settle();
                Descendants<Button>(setupPanel).Single(b=>Equals(b.Content,model.SetupApplyLabel)).BringIntoView();
                await Settle(); Capture(window,output,"endpoint-setup-"+theme);
            }
            await model.ApplySetupAsync();
            Check(model.SetupEditor is null&&demo.Hubs.All(h=>h.Writes==0&&h.LightingWrites==0),"One Save validates and stores the first setup internally without starting output");
            var row=model.Cooling.Single(r=>r.Draft.Target==fan); row.Mode="固定"; row.Duty=62;
            Check(row.CanApply&&row.CanEdit&&!row.NeedsSetup&&model.Cooling.Where(r=>r!=row).All(r=>!r.CanApply),"Only the configured fan becomes controllable and its setup guidance disappears");
            await model.OperateAsync(row); Check(demo.Hubs[0].Writes==1&&demo.Hubs[1].Writes==0,"Explicit cooling apply targets only the selected device");
            var lightRow=model.Lights.Single(l=>l.Target==new EndpointReference(demo.DeviceIds[0],"led1"));
            model.SelectedPage=2; await Settle();
            var lightSetupButton=Descendants<Button>(window).Single(b=>ReferenceEquals(b.DataContext,lightRow)&&Equals(b.CommandParameter,lightRow));
            Check(lightRow.Ready&&!lightRow.NeedsSetup&&!lightSetupButton.IsVisible,
                "Known Hub RGB is ready on the current connection without a registration or approval action");
            model.SelectLights([lightRow.Target]); await Settle();
            Check(model.LightingDraft.CanApply&&!model.LightHasPendingChanges&&!model.CanApplyLighting&&model.SetupEditor is null,
                "Known lighting is ready for editing without endpoint settings while unchanged Apply stays disabled");
            Descendants<ScrollViewer>(window).Single(s=>s.Name=="WorkspaceScroll").ScrollToTop(); await Settle(); Capture(window,output,"rgb-first-use-ready");
            Check(demo.Hubs[0].Writes==1&&demo.Hubs.All(h=>h.LightingWrites==0),"Automatic lighting readiness does not change the running fan or emit any light");
            model.SelectedPage=3; model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==lightRow.Target); await Settle();
            editor=model.SetupEditor!; Check(editor.Lighting&&editor.LedCount==24&&!editor.CanChangeLedCount&&model.CanApplySetup,"Optional detailed editing uses the detected LED count without asking for approval");
            var exitChoice=Descendants<ComboBox>(window).Single(c=>ReferenceEquals(c.DataContext,editor)&&c.ItemsSource is DeviceExitChoice<LightingExitAction>[]);
            exitChoice.SetCurrentValue(Selector.SelectedValueProperty,LightingExitAction.FixedColor); await Settle();
            Descendants<ColorPicker>(window).Single(c=>ReferenceEquals(c.DataContext,editor)).SetCurrentValue(ColorPicker.SelectedColorProperty,System.Windows.Media.Color.FromRgb(20,40,160));
            await model.PollAsync(); await Settle();
            Check(editor.ColorExit&&editor.Draft.Values.LightingExit!.Color==new RgbColor(20,40,160),"Lighting exit selector and color picker retain their values through polling");
            Check(demo.Hubs[0].Writes==1,"Editing lighting settings leaves existing cooling running");
            Check(model.CanApplySetup&&editor.Draft.Values.LightingExit!.Color==new RgbColor(20,40,160),"The selected exit color is directly saveable without a separate review");
            await Settle(); Capture(window,output,"endpoint-setup-lighting");
            await model.ApplySetupAsync();
            Check(demo.Hubs[0].Writes==1&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==62&&demo.Hubs.All(h=>h.LightingWrites==0),"Lighting setup preserves the running fan and emits no color before explicit apply");
            Check(!lightRow.NeedsSetup&&lightRow.Ready&&model.Lights.Single(l=>l.Target==new EndpointReference(demo.DeviceIds[1],"led1")).Ready,
                "Saving an exit preference preserves the other Hub's existing lighting readiness");
            await RunSavedEndpointEditingAsync(model, demo, window, output, fan, lightRow.Target, Check);
            model.SelectedPage=2; model.SelectAllLights.Execute(null); await Settle();
            Check(model.LightingDraft.Targets.ToHashSet().SetEquals(demo.DeviceIds.Select(id=>new EndpointReference(id,"led1")))&&
                model.LightingDraft.CanApply&&!model.LightHasPendingChanges&&!model.CanApplyLighting,
                "Select usable lighting includes both known Hub ports, excludes list-only fixtures, and waits for an explicit edit");
            model.LightColor=System.Windows.Media.Color.FromRgb(128,64,32); model.LightBrightness=50;
            await model.PollAsync(); await Settle();
            Check(model.LightColor==System.Windows.Media.Color.FromRgb(128,64,32)&&model.LightBrightness==50&&model.CanApplyLighting,"RGB color and brightness edits survive polling");
            Descendants<ScrollViewer>(window).Single(s=>s.Name=="WorkspaceScroll").ScrollToTop(); await Settle(); Capture(window,output,"rgb-ready-editor");
            model.SelectedPage=3;
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==new EndpointReference(demo.DeviceIds[1],"fan1")); await Settle(); editor=model.SetupEditor!;
            editor.Name="切断後も保持する名前"; editor.Minimum=45; editor.Fallback=100; editor.ExitDuty=70;
            demo.Hubs[1].Disconnected=true; await demo.Runtime.PollAsync(); await model.PollAsync();
            await Settle();
            Check(model.SetupEditor is null&&model.SelectedSetup is null&&!model.HasSetupEditor&&
                model.SetupChoices.All(c=>c.Target.DeviceId!=demo.DeviceIds[1])&&editor.Name=="切断後も保持する名前"&&editor.Minimum==45,
                "Disconnect hides the selected endpoint and editor while retaining unsaved name and safety values");
            Check(editor.Draft.Conflict&&!editor.Draft.Available&&!model.CanApplySetup,
                "Disconnect preserves the edited values but prevents saving against the stale generation");
            demo.Hubs[1].Disconnected=false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await model.PollAsync(); await Settle();
            Check(ReferenceEquals(editor,model.SetupEditor)&&model.SelectedSetup?.Target==editor.Draft.Candidate.Target&&
                editor.Name=="切断後も保持する名前"&&editor.Minimum==45&&editor.Draft.Conflict&&!model.CanApplySetup,
                $"Reconnect restores the retained endpoint draft without clearing its generation conflict or enabling Save (same={ReferenceEquals(editor,model.SetupEditor)}, selected={model.SelectedSetup?.Target}, listed={model.SetupChoices.Any(c=>c.Target==editor.Draft.Candidate.Target)}, name={editor.Name}, minimum={editor.Minimum}, conflict={editor.Draft.Conflict}, apply={model.CanApplySetup})");
            await model.ReloadSetupAsync();
            Check(!model.SetupEditor!.Draft.Conflict&&!model.CanApplySetup&&model.SetupEditor.Draft.Values.Safety is { Minimum:null,Fallback:null,Exit:null },"Explicit reload obtains the new generation and missing saved output settings without assuming values");
            Check(model.SetupUsage.Contains("冷却 1 / 64")&&model.SetupUsage.Contains("照明 1 / 128")&&model.SetupUsage.Contains("LED 24 / 4,096"),"Saved control counts remain separate from detected candidates");
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==fan); await Settle();
            Check(model.HasSavedSetup&&model.CanRemoveSetup&&Descendants<Button>(window).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="端子の制御設定を解除").IsEnabled,"Saved connected endpoint exposes the removal action");
            row=model.Cooling.Single(r=>r.Draft.Target==fan); row.Mode="固定"; row.Duty=63; await model.OperateAsync(row);
            var writes=demo.Hubs[0].Writes;
            await model.PollAsync();
            Check(model.CanRemoveSetup&&demo.Hubs[0].Writes==writes,"Opening saved settings and polling preserve the active fan until the explicit delete action");
            await model.ReloadSetupAsync(); Check(model.CanRemoveSetup&&demo.Hubs[0].Writes==writes,"Reloading saved settings does not delete them or change the fan");
            await model.RemoveSetupAsync();
            Check(model.SelectedSetup is null&&demo.Hubs[0].Writes==writes+1&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==70&&demo.Hubs[1].Writes==0,"One delete action internally validates the transaction and uses only the selected fan's saved 70 percent exit");
            row=model.Cooling.Single(r=>r.Draft.Target==fan);
            Check(!row.CanApply&&row.NeedsSetup&&model.SetupUsage.Contains("冷却 0 / 64"),"Deleting cooling settings retains detection but requires the now-missing output values");
            var light=new EndpointReference(demo.DeviceIds[0],"led1");
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==light); await Settle();
            demo.Hubs[0].Disconnected=true;
            await demo.Runtime.PollAsync(); await model.PollAsync();
            await Settle();
            Check(model.SetupChoices.All(c=>c.Target!=light)&&model.SelectedSetup is null&&!model.HasSavedSetup&&!model.HasSetupEditor&&
                !model.CanRemoveSetup&&!model.CanApplySetup&&model.SetupUsage.Contains("照明 1 / 128")&&model.SetupUsage.Contains("LED 24 / 4,096"),
                "Disconnected saved lighting leaves the selector and actions while its saved settings remain intact");
            var remove=Descendants<Button>(window).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="端子の制御設定を解除");
            Check(!remove.IsVisible&&remove.Command?.CanExecute(remove.CommandParameter)==false,
                "The hidden disconnected endpoint cannot invoke saved-settings deletion");
            demo.Hubs[0].Disconnected=false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await model.PollAsync();
            await model.ReloadSetupAsync(); await Settle();
            Check(model.SelectedSetup?.Target==light&&model.HasSavedSetup&&model.HasSetupEditor&&model.CanRemoveSetup&&
                remove.Command?.CanExecute(remove.CommandParameter)==true&&Equals(remove.Content,"設定を削除"),
                "Reconnect restores the saved endpoint and its explicit deletion action");
            foreach(var theme in new[]{"system","light","dark"})
            {
                model.Theme=theme; await Settle(); remove.BringIntoView(); await Settle();
                Check(remove.IsVisible&&remove.IsEnabled&&remove.TranslatePoint(new Point(),window).Y>=0&&remove.TranslatePoint(new Point(0,remove.ActualHeight),window).Y<=window.ActualHeight,"Saved-settings deletion remains visible in "+theme+" theme");
                Capture(window,output,"endpoint-removal-"+theme);
            }
            window.Width=window.MinWidth; window.Height=window.MinHeight; await Settle(); remove.BringIntoView(); await Settle();
            Check(remove.TranslatePoint(new Point(),window).Y>=0&&remove.TranslatePoint(new Point(0,remove.ActualHeight),window).Y<=window.ActualHeight,"Saved-settings deletion remains reachable at minimum window size");
            Capture(window,output,"endpoint-removal-minimum");
            await model.RemoveSetupAsync();
            Check(model.SelectedSetup is null&&model.SetupUsage.Contains("照明 0 / 128")&&model.SetupUsage.Contains("LED 0 / 4,096"),"One connected delete action frees saved lighting and LED reservations");
            await model.ReloadSetupAsync(); model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==light);
            lightRow=model.Lights.Single(l=>l.Target==light);
            Check(model.SetupEditor is not null&&!model.HasSavedSetup&&lightRow.Ready&&!lightRow.NeedsSetup&&demo.Hubs.All(h=>h.LightingWrites==0),"Known lighting stays ready after connected saved-settings deletion without emitting a color or requesting approval");
            Check(await model.CloseAsync(),"Setup workflow exits with confirmed release");
            await File.WriteAllLinesAsync(Path.Combine(output,"endpoint-setup-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); await File.WriteAllLinesAsync(Path.Combine(output,"endpoint-setup-smoke.txt"),report); return 1; }
    }
    private static async Task RunSavedEndpointEditingAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window,
        string output, EndpointReference fan, EndpointReference light, Action<bool, string> check)
    {
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        model.SelectedSetup = model.SetupChoices.Single(c => c.Target == light); await Settle();
        var editor = model.SetupEditor!;
        bool NoApproval() => !Descendants<CheckBox>(window).Any(c => ReferenceEquals(c.DataContext, model.SetupEditor));
        check(model.CanApplySetup && NoApproval(),
            "Reopening saved lighting exposes valid actual settings without an approval checkbox");
        editor.LightingExit = LightingExitAction.Off; editor.LightingExit = LightingExitAction.FixedColor;
        editor.ExitColor = System.Windows.Media.Color.FromRgb(80, 120, 160); await model.PollAsync(); await Settle();
        check(model.CanApplySetup && NoApproval(),
            "Changing the saved lighting exit action or color remains directly saveable");
        check(editor.Draft.Values.LightingExit!.Color==new RgbColor(80,120,160), "The lighting draft retains the selected exit color without a separate review action");
        SetupTextBox(window, editor, "端子の名前").BringIntoView(); await Settle();
        Capture(window, output, "endpoint-saved-lighting-settings");
        await model.ReloadSetupAsync();
        model.SelectedSetup = model.SetupChoices.Single(c => c.Target == fan); await Settle(); editor = model.SetupEditor!;
        var minimum = editor.Minimum;
        check(model.CanApplySetup && NoApproval(),
            "Saved cooling output settings are editable without reapproval");
        editor.Minimum = 101; await Settle();
        check(!model.CanApplySetup && NoApproval(),
            "An out-of-range cooling value disables Save through validation without introducing approval");
        editor.Minimum = minimum; await Settle();
        check(model.CanApplySetup && NoApproval(),
            "Correcting the cooling value restores Save directly");
        editor.Name = "保存した端子の名前変更"; await model.PollAsync(); await Settle();
        check(model.CanApplySetup && NoApproval() && editor.Name=="保存した端子の名前変更",
            "Renaming a saved port and polling retain valid unsaved input without approval");
        SetupTextBox(window, editor, "端子の名前").BringIntoView(); await Settle();
        Capture(window, output, "endpoint-saved-cooling-settings");
        check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes),
            "Editing saved settings and validating values performs no fan or lighting output");
        model.SelectedSetup = null; await Settle();
    }
    private static async Task RunCoolingSetupNavigationAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output, Action<bool,string> check)
    {
        var target = new EndpointReference(demo.DeviceIds[1], "fan1");
        var row = model.Cooling.Single(r => r.Draft.Target == target);
        model.SelectedPage = 1; row.Mode = "固定"; await Settle(); await model.PollAsync(); await Settle();
        var slider = Descendants<Slider>(window).Single(s => ReferenceEquals(s.DataContext, row));
        var button = Descendants<Button>(window).Single(b => ReferenceEquals(b.DataContext, row) && Equals(b.Content, "制御設定"));
        check(row.NeedsSetup && button.IsVisible && button.IsEnabled && !slider.IsEnabled && !row.CanApply,
            $"Selecting Fixed on an unconfigured fan explains the required setup while output stays disabled (needs={row.NeedsSetup}, visible={button.IsVisible}, button={button.IsEnabled}, slider={slider.IsEnabled}, apply={row.CanApply})");
        button.BringIntoView(); await Settle(); Capture(window, output, "cooling-setup-guidance");
        void Click() => ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await model.PollAsync(); await Settle();
        check(model.OpenCoolingSetup.CanExecute(row), $"Fresh service state enables the setup shortcut (supported={model.SetupSupported}, idle={model.SetupIdle}, needs={row.NeedsSetup}, choices={model.SetupChoices.Count}, status={row.Draft.Problem}, notice={model.Notice})");
        Click(); await Settle();
        check(model.SelectedPage == 3 && model.SetupExpanded && model.SelectedSetup?.Target == target && model.SetupEditor?.Draft.Candidate.Target == target,
            $"The setup shortcut opens the exact second Hub FAN1 rather than the same-named first Hub port (page={model.SelectedPage}, selected={model.SelectedSetup?.Target}, notice={model.Notice})");
        var selector = Descendants<ComboBox>(window).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "設定する端子");
        check(selector.TranslatePoint(new Point(), window).Y >= 0 && selector.TranslatePoint(new Point(0,selector.ActualHeight), window).Y <= window.ActualHeight,
            "The shortcut scrolls the endpoint selector into view");
        check(model.SetupEditor?.Draft.Values.Safety is { Minimum:null,Fallback:null,Exit:null } && !model.CanApplySetup && demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0),
            "Opening endpoint setup leaves unknown output settings empty and never saves or writes hardware output");
        var editor = model.SetupEditor!; editor.Name = "入力途中を保持"; editor.Minimum = 45;
        model.SelectedPage = 1; await model.PollAsync(); await Settle(); Click(); await Settle(); await model.PollAsync();
        check(ReferenceEquals(editor, model.SetupEditor) && editor.Name == "入力途中を保持" && editor.Minimum == 45,
            "Reopening the same fan setup and polling preserve existing unsaved edits");
        row.SetBusy(true); check(!model.OpenCoolingSetup.CanExecute(row), "The setup shortcut is disabled during cooling operations"); row.SetBusy(false);
        model.SelectedSetup = null; model.SelectedPage = 3;
    }
    private static DraftTextBox SetupTextBox(Window window,DeviceSetupEditor editor,string name) =>
        Descendants<DraftTextBox>(window).Single(t=>ReferenceEquals(t.DataContext,editor)&&System.Windows.Automation.AutomationProperties.GetName(t)==name);
}
