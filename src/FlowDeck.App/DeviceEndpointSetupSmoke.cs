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
            Descendants<Expander>(window).Single(e=>Equals(e.Header,"端子の設定")).SetCurrentValue(Expander.IsExpandedProperty,true); await Settle();
            Check(model.SetupChoices.Count==396 && model.SetupEditor is null && !model.CanApplySetup,"Setup lists all 396 supported detected ports without automatically choosing one");
            await RunCoolingSetupNavigationAsync(model, demo, window, output, Check);
            var fan=new EndpointReference(demo.DeviceIds[0],"fan1");
            var selector=Descendants<ComboBox>(window).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="設定する端子");
            var last=model.SetupChoices.Last();
            selector.SetCurrentValue(Selector.SelectedItemProperty,last); await Settle();
            Check(model.SetupEditor?.Draft.Candidate.Target==last.Target&&last.Target.EndpointId=="zone-128","The last candidate beyond 256 is reachable through the bound selector");
            var lastEditor=model.SetupEditor!; lastEditor.Name="最後の照明"; await model.PollAsync(); await Settle();
            Check(ReferenceEquals(lastEditor,model.SetupEditor)&&model.SelectedSetup==last&&lastEditor.Name=="最後の照明","Large candidate list preserves selected endpoint and input through polling");
            Check(!lastEditor.Confirmed&&!model.CanPreviewSetup&&model.SetupUsage.Contains("照明 0 / 128"),"Discovered candidates do not consume saved control reservations or authorize output");
            selector.BringIntoView(); await Settle(); Capture(window,output,"endpoint-setup-many-candidates");
            selector.SetCurrentValue(Selector.SelectedItemProperty,model.SetupChoices.Single(c=>c.Target==fan)); await Settle();
            var editor=model.SetupEditor!;
            Check(editor.Cooling&&!editor.Confirmed&&!model.CanPreviewSetup,"A new cooling port requires confirmation");
            Descendants<DraftTextBox>(window).Single(t=>ReferenceEquals(t.DataContext,editor)).SetCurrentValue(TextBox.TextProperty,"背面ファン");
            var minimum=Descendants<Slider>(window).Single(s=>ReferenceEquals(s.DataContext,editor)&&System.Windows.Automation.AutomationProperties.GetName(s)=="確認済みの最低出力");
            minimum.SetCurrentValue(Slider.ValueProperty,35d); editor.Fallback=100; editor.ExitDuty=70;
            await model.PollAsync(); await Settle();
            Check(ReferenceEquals(model.SetupEditor,editor)&&editor.Name=="背面ファン"&&editor.Minimum==35,"Name and slider edits survive polling");
            Descendants<CheckBox>(window).Single(c=>ReferenceEquals(c.DataContext,editor)).SetCurrentValue(ToggleButton.IsCheckedProperty,true); await Settle();
            await model.PreviewSetupAsync(); Check(model.HasSetupPreview&&model.CanApplySetup,"Confirmed settings produce a service validated preview");
            Check(demo.Hubs.All(h=>h.Writes==0&&h.LightingWrites==0),"Preview sends no hardware output");
            editor.Minimum=36; Check(!model.HasSetupPreview&&!editor.Confirmed,"Changing safety invalidates both preview and confirmation");
            editor.Confirmed=true; await model.PreviewSetupAsync();
            foreach(var theme in new[]{"light","dark"})
            {
                model.Theme=theme; await Settle();
                Descendants<Border>(window).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="端子設定の差分").BringIntoView();
                await Settle(); Capture(window,output,"endpoint-setup-"+theme);
            }
            await model.ApplySetupAsync();
            Check(model.SetupEditor is null&&demo.Hubs.All(h=>h.Writes==0&&h.LightingWrites==0),"Saving first setup returns to monitoring without starting output");
            var row=model.Cooling.Single(r=>r.Draft.Target==fan); row.Mode="固定"; row.Duty=62;
            Check(row.CanApply&&row.CanEdit&&!row.NeedsSetup&&model.Cooling.Where(r=>r!=row).All(r=>!r.CanApply),"Only the configured fan becomes controllable and its setup guidance disappears");
            await model.OperateAsync(row); Check(demo.Hubs[0].Writes==1&&demo.Hubs[1].Writes==0,"Explicit cooling apply targets only the selected device");
            var lightRow=model.Lights.Single(l=>l.Target==new EndpointReference(demo.DeviceIds[0],"led1"));
            model.SelectedPage=2; await Settle();
            var lightSetupButton=Descendants<Button>(window).Single(b=>ReferenceEquals(b.DataContext,lightRow)&&Equals(b.Content,"照明を使う"));
            Check(lightRow.NeedsSetup&&!lightRow.Ready&&lightRow.Status.Contains("24個")&&lightRow.Status.Contains("消灯")&&lightSetupButton.IsVisible&&lightSetupButton.IsEnabled,
                "Detected RGB reports the actual LED count and exit policy and offers first use without a confirmation wizard");
            lightSetupButton.BringIntoView(); await Settle();
            Descendants<ScrollViewer>(window).Single(s=>s.Name=="WorkspaceScroll").ScrollToTop(); await Settle(); Capture(window,output,"rgb-setup-guidance");
            await model.OpenLightingSetupAsync(lightRow); await Settle();
            Check(model.SelectedPage==2&&model.SetupEditor is null&&lightRow.Ready&&!lightRow.NeedsSetup,
                "First use registers detected RGB and remains on the lighting page without opening a setup wizard");
            Check(demo.Hubs[0].Writes==1&&demo.Hubs.All(h=>h.LightingWrites==0),"Automatic lighting registration does not change the running fan or emit any light");
            model.SelectedPage=3; model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==lightRow.Target); await Settle();
            editor=model.SetupEditor!; Check(editor.Lighting&&editor.LedCount==24&&!editor.CanChangeLedCount&&editor.Confirmed&&!editor.NeedsConfirmation,"Later detailed editing reuses the detected LED count without asking for first-use confirmation");
            var exitChoice=Descendants<ComboBox>(window).Single(c=>ReferenceEquals(c.DataContext,editor)&&c.ItemsSource is DeviceExitChoice<LightingExitAction>[]);
            exitChoice.SetCurrentValue(Selector.SelectedValueProperty,LightingExitAction.FixedColor); await Settle();
            Descendants<ColorPicker>(window).Single(c=>ReferenceEquals(c.DataContext,editor)).SetCurrentValue(ColorPicker.SelectedColorProperty,System.Windows.Media.Color.FromRgb(20,40,160));
            await model.PollAsync(); await Settle();
            Check(editor.ColorExit&&editor.Draft.Values.LightingExit!.Color==new RgbColor(20,40,160),"Lighting exit selector and color picker retain their values through polling");
            editor.Confirmed=true; await model.PreviewSetupAsync(); Check(demo.Hubs[0].Writes==1,"Lighting preview leaves existing cooling running");
            Check(model.SetupPreviewText.Contains("#1428A0")&&model.SetupPreviewText.Contains("→"),"Preview shows the saved-to-proposed lighting change");
            await Settle(); Capture(window,output,"endpoint-setup-lighting");
            await model.ApplySetupAsync();
            Check(demo.Hubs[0].Writes==1&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==62&&demo.Hubs.All(h=>h.LightingWrites==0),"Lighting setup preserves the running fan and emits no color before explicit apply");
            Check(!lightRow.NeedsSetup&&lightRow.Ready&&model.Lights.Where(l=>l!=lightRow).All(l=>!l.Ready),"Only the configured RGB port becomes ready");
            await RunSavedEndpointConfirmationAsync(model, demo, window, output, fan, lightRow.Target, Check);
            model.SelectedPage=2; model.SelectAllLights.Execute(null); await Settle();
            Check(model.LightingDraft.Targets.SequenceEqual(new[]{lightRow.Target})&&model.CanApplyLighting,"Select usable lighting excludes unconfigured ports and enables Apply");
            model.LightColor=System.Windows.Media.Color.FromRgb(128,64,32); model.LightBrightness=50;
            await model.PollAsync(); await Settle();
            Check(model.LightColor==System.Windows.Media.Color.FromRgb(128,64,32)&&model.LightBrightness==50&&model.CanApplyLighting,"RGB color and brightness edits survive polling");
            Descendants<ScrollViewer>(window).Single(s=>s.Name=="WorkspaceScroll").ScrollToTop(); await Settle(); Capture(window,output,"rgb-ready-editor");
            model.SelectedPage=3;
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==new EndpointReference(demo.DeviceIds[1],"fan1")); await Settle(); editor=model.SetupEditor!;
            editor.Name="切断後も保持する名前"; editor.Minimum=45; editor.Confirmed=true; await model.PreviewSetupAsync();
            demo.Hubs[1].Disconnected=true; await demo.Runtime.PollAsync(); await model.PollAsync();
            await Settle();
            Check(ReferenceEquals(editor,model.SetupEditor)&&model.SelectedSetup?.Target==editor.Draft.Candidate.Target&&editor.Name=="切断後も保持する名前"&&editor.Minimum==45,
                "Candidate reordering after disconnect preserves the bound selection, editor, name and safety values");
            Check(editor.Draft.Conflict&&!editor.Draft.Available&&!model.CanApplySetup&&editor.Confirmed,
                "Disconnect preserves confirmation but prevents applying the stale preview");
            demo.Hubs[1].Disconnected=false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await model.ReloadSetupAsync();
            Check(!model.SetupEditor!.Draft.Conflict&&!model.SetupEditor.Confirmed&&!model.HasSetupPreview,"Explicit reload obtains the new device generation without reusing old confirmation");
            Check(model.SetupUsage.Contains("冷却 1 / 64")&&model.SetupUsage.Contains("照明 1 / 128")&&model.SetupUsage.Contains("LED 24 / 4,096"),"Saved control counts remain separate from detected candidates");
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==fan); await Settle();
            Check(model.HasSavedSetup&&model.CanRemoveSetup&&Descendants<Button>(window).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="端子の制御設定を解除").IsEnabled,"Saved connected endpoint exposes the removal action");
            row=model.Cooling.Single(r=>r.Draft.Target==fan); row.Mode="固定"; row.Duty=62; await model.OperateAsync(row);
            var writes=demo.Hubs[0].Writes;
            await model.PreviewSetupRemovalAsync(); await model.PollAsync();
            Check(model.HasSetupPreview&&model.CanApplySetup&&model.SetupApplyLabel.Contains("解除")&&demo.Hubs[0].Writes==writes,"Removal preview and polling preserve the active fan");
            Check(model.SetupApplyLabel.Contains("この端子")&&model.SetupPreviewText.Contains("ほかのファンの制御は継続")&&model.SetupPreviewText.Contains("名前・監視表示"),"Cooling removal review explicitly preserves other fan control");
            await model.ReloadSetupAsync(); Check(!model.HasSetupPreview&&demo.Hubs[0].Writes==writes,"Reload cancels removal without changing the fan");
            await model.PreviewSetupRemovalAsync(); await model.ApplySetupAsync();
            Check(demo.Hubs[0].Writes==writes+1&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==70,"Confirmed removal uses the saved 70 percent exit");
            row=model.Cooling.Single(r=>r.Draft.Target==fan);
            Check(!row.CanApply&&model.SetupUsage.Contains("冷却 0 / 64"),"Removed fan remains detected but loses control permission and reservation");
            var light=new EndpointReference(demo.DeviceIds[0],"led1"); demo.Hubs[0].Disconnected=true;
            await demo.Runtime.PollAsync(); await model.PollAsync();
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==light); await model.ReloadSetupAsync(); await Settle();
            Check(model.HasSavedSetup&&!model.HasSetupEditor&&model.CanRemoveSetup&&!model.CanPreviewSetup,"Disconnected saved lighting remains selectable for removal without a live editor");
            await model.PreviewSetupRemovalAsync();
            Check(model.CanApplySetup&&model.SetupPreviewText.Contains("LED数の確認"),"Disconnected lighting produces an applicable removal review");
            var review=Descendants<Border>(window).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="端子設定の差分");
            foreach(var theme in new[]{"system","light","dark"})
            {
                model.Theme=theme; await Settle(); review.BringIntoView(); await Settle();
                Check(review.TranslatePoint(new Point(),window).Y>=0&&review.TranslatePoint(new Point(0,review.ActualHeight),window).Y<=window.ActualHeight,"Removal review is visible in "+theme+" theme");
                Capture(window,output,"endpoint-removal-"+theme);
            }
            window.Width=window.MinWidth; window.Height=window.MinHeight; await Settle(); review.BringIntoView(); await Settle();
            Check(review.TranslatePoint(new Point(),window).Y>=0&&review.TranslatePoint(new Point(0,review.ActualHeight),window).Y<=window.ActualHeight,"Removal review remains reachable at minimum window size");
            Capture(window,output,"endpoint-removal-minimum");
            await model.ApplySetupAsync();
            Check(model.SelectedSetup is null&&!model.HasSetupPreview&&model.SetupUsage.Contains("照明 0 / 128")&&model.SetupUsage.Contains("LED 0 / 4,096"),"Disconnected removal frees lighting and LED reservations");
            demo.Hubs[0].Disconnected=false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync(); await model.PollAsync();
            await model.ReloadSetupAsync(); model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==light);
            Check(model.SetupEditor is { Confirmed:false }&&!model.HasSavedSetup&&demo.Hubs.All(h=>h.LightingWrites==0),"Reconnect requires fresh lighting confirmation without emitting a color");
            Check(await model.CloseAsync(),"Setup workflow exits with confirmed release");
            await File.WriteAllLinesAsync(Path.Combine(output,"endpoint-setup-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); await File.WriteAllLinesAsync(Path.Combine(output,"endpoint-setup-smoke.txt"),report); return 1; }
    }
    private static async Task RunSavedEndpointConfirmationAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window,
        string output, EndpointReference fan, EndpointReference light, Action<bool, string> check)
    {
        var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        model.SelectedSetup = model.SetupChoices.Single(c => c.Target == light); await Settle();
        var editor = model.SetupEditor!;
        CheckBox Confirmation() => Descendants<CheckBox>(window).Single(c => ReferenceEquals(c.DataContext, model.SetupEditor));
        check(editor.Confirmed && !editor.NeedsConfirmation && !Confirmation().IsVisible && editor.Status.Contains("再確認は不要"),
            "Reopening the same saved lighting port reuses confirmation without asking for another checkbox");
        editor.LightingExit = LightingExitAction.Off; editor.LightingExit = LightingExitAction.FixedColor;
        editor.ExitColor = System.Windows.Media.Color.FromRgb(80, 120, 160); await model.PollAsync(); await Settle();
        check(editor.Confirmed && !editor.NeedsConfirmation && model.CanPreviewSetup && !Confirmation().IsVisible,
            "Changing only the saved lighting exit action or color does not repeat physical port confirmation");
        await model.PreviewSetupAsync();
        check(model.CanApplySetup && model.SetupPreviewText.Contains("#5078A0"), "Lighting exit changes still require the normal service validated difference review");
        Descendants<TextBlock>(window).Single(t => ReferenceEquals(t.DataContext, editor) && t.Text == editor.Status).BringIntoView(); await Settle();
        Capture(window, output, "endpoint-saved-lighting-confirmation");
        await model.ReloadSetupAsync();
        model.SelectedSetup = model.SetupChoices.Single(c => c.Target == fan); await Settle(); editor = model.SetupEditor!;
        var minimum = editor.Minimum;
        check(editor.Confirmed && !editor.NeedsConfirmation && !Confirmation().IsVisible,
            "Saved cooling wiring and safety values reuse confirmation when reopened");
        editor.Minimum = minimum + 1; await Settle();
        check(!editor.Confirmed && editor.NeedsConfirmation && Confirmation().IsVisible && !model.CanPreviewSetup,
            "Changing a cooling safety value shows the required confirmation and disables preview");
        editor.Minimum = minimum; await Settle();
        check(editor.Confirmed && !editor.NeedsConfirmation && !Confirmation().IsVisible && model.CanPreviewSetup,
            "Returning to the saved cooling value reuses its previous confirmation");
        editor.Name = "保存済み確認の名前変更"; await model.PollAsync(); await Settle();
        check(editor.Confirmed && !editor.NeedsConfirmation && editor.Status.Contains("再確認は不要"),
            "Renaming a saved port and polling preserve confirmation and explain its reuse");
        Descendants<TextBlock>(window).Single(t => ReferenceEquals(t.DataContext, editor) && t.Text == editor.Status).BringIntoView(); await Settle();
        Capture(window, output, "endpoint-saved-cooling-confirmation");
        check(demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).SequenceEqual(writes),
            "Editing and reviewing saved confirmation performs no fan or lighting output");
        model.SelectedSetup = null; await Settle();
    }
    private static async Task RunCoolingSetupNavigationAsync(DeviceWorkspaceViewModel model, DemoDeviceWorkspace demo, Window window, string output, Action<bool,string> check)
    {
        var target = new EndpointReference(demo.DeviceIds[1], "fan1");
        var row = model.Cooling.Single(r => r.Draft.Target == target);
        model.SelectedPage = 1; row.Mode = "固定"; await Settle(); await model.PollAsync(); await Settle();
        var slider = Descendants<Slider>(window).Single(s => ReferenceEquals(s.DataContext, row));
        var button = Descendants<Button>(window).Single(b => ReferenceEquals(b.DataContext, row) && Equals(b.Content, "制御を設定（任意）"));
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
        check(model.SetupEditor is { Confirmed: false } && !model.CanPreviewSetup && !model.CanApplySetup && demo.Hubs.All(h => h.Writes == 0 && h.LightingWrites == 0),
            "Opening endpoint setup neither confirms nor saves settings and never writes fan or lighting output");
        var editor = model.SetupEditor!; editor.Name = "入力途中を保持"; editor.Minimum = 45;
        model.SelectedPage = 1; await model.PollAsync(); await Settle(); Click(); await Settle(); await model.PollAsync();
        check(ReferenceEquals(editor, model.SetupEditor) && editor.Name == "入力途中を保持" && editor.Minimum == 45,
            "Reopening the same fan setup and polling preserve existing unsaved edits");
        row.SetBusy(true); check(!model.OpenCoolingSetup.CanExecute(row), "The setup shortcut is disabled during cooling operations"); row.SetBusy(false);
        model.SelectedSetup = null; model.SelectedPage = 3;
    }
}
