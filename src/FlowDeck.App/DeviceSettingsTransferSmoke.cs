using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    public static async Task<int> RunDeviceSettingsTransferAsync(DeviceWorkspaceViewModel model,DemoDeviceWorkspace demo,Window window,string output)
    {
        var report=new List<string>();
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); report.Add("PASS "+label); }
        try
        {
            var fan=model.Cooling.Single(r=>r.Draft.Target==new EndpointReference(demo.DeviceIds[0],"fan1"));
            fan.Mode="固定"; fan.Duty=63; await model.OperateAsync(fan);
            model.SelectedPage=3; model.Theme="dark"; await Settle();
            var expander=Descendants<Expander>(window).Single(e=>Equals(e.Header,"書き出し・復元")); expander.IsExpanded=true; await Settle();
            var backup=Path.Combine(output,"backup.flowdeck.json"); await model.ExportSettingsToAsync(backup);
            var archive=await DeviceSettingsArchives.ReadAsync(backup);
            Check(archive.Purpose=="backup"&&archive.Configuration.Preferences.Ui.Theme=="dark","Backup includes the GUI user's current theme and typed configuration");
            Check((await demo.Hubs[0].ReadAsync()).Channels[0].Duty==63,"Export does not release or alter the active fan");
            model.Theme="light"; await model.LoadImportAsync(backup); await Settle();
            Check(model.ImportRows.Count>0&&model.ImportRows.All(r=>r.Selected.Target==r.Source)&&!model.ImportConfirmed&&!model.CanPreviewImport,"Same-PC references are selected but restoration requires explicit confirmation");
            var stable=model.ImportRows.ToArray(); await model.PollAsync(); await Settle();
            Check(stable.SequenceEqual(model.ImportRows),"Telemetry polling preserves mapping rows and choices");
            var confirm=Descendants<CheckBox>(expander).Single(c=>Equals(c.Content,"機器の対応・復元内容を確認済み"));
            confirm.SetCurrentValue(ToggleButton.IsCheckedProperty,true); await model.PreviewImportAsync(); await Settle();
            Check(model.HasImportPreview&&model.CanApplyImport&&model.ImportChanges.Contains("ライト → ダーク")&&model.ImportChanges.Contains("変更前の終了設定"),"Preview lists the appearance change and release behavior");
            Check((await demo.Hubs[0].ReadAsync()).Channels[0].Duty==63,"Preview does not write hardware outputs");
            model.SelectedSetup=model.SetupChoices.Single(c=>c.Target==fan.Draft.Target); model.SetupEditor!.Name="未保存の端子名";
            Check(model.CanApplySetup&&model.HasImportPreview&&model.CanApplyImport,"Editing an endpoint keeps both actual settings and the existing import draft without an endpoint approval step");
            await model.PreviewImportAsync();
            Check(model.HasImportPreview&&model.CanApplySetup&&model.SetupEditor.Name=="未保存の端子名"&&
                (await demo.Hubs[0].ReadAsync()).Channels[0].Duty==63,"An import preview preserves the unsaved endpoint edit and does not write output");
            var save=Descendants<Button>(expander).Single(b=>Equals(b.Content,"制御終了・復元"));
            Check(save.Command?.CanExecute(save.CommandParameter)==true,"Restore button is bound to the validated service transaction");
            foreach(var theme in new[]{"light","dark"}) { model.Theme=theme; await Settle(); Capture(window,output,"settings-transfer-"+theme); }
            save.BringIntoView(); await Settle(); Capture(window,output,"settings-transfer-confirmation");
            window.Width=960; window.Height=640; await Settle();
            var mapping=Descendants<ComboBox>(expander).First(c=>c.DataContext is DeviceImportRow);
            Check(mapping.ActualWidth>250,"Mapping selector remains usable at minimum window width"); Capture(window,output,"settings-transfer-minimum");
            window.Width=1120; window.Height=820; model.Theme="light";
            await model.ApplyImportAsync(); await Settle();
            Check(!model.HasImport&&!model.HasImportPreview&&model.Theme=="dark"&&(await demo.Hubs[0].ReadAsync()).Channels[0].Duty==70,"Restore adopts the local theme and returns the fan to its previous exit duty without replay");
            Check(model.SetupEditor is { Draft.Conflict:true }&&!model.CanApplySetup,"Restoring settings invalidates the older endpoint draft before it can save");
            Check(demo.Runtime.State().Endpoints.All(e=>!e.Owned),"Successful restore leaves every endpoint unowned");
            await model.LoadImportAsync(backup); model.ImportConfirmed=true; await model.PreviewImportAsync();
            var invalid=Path.Combine(output,"invalid.flowdeck.json"); await File.WriteAllTextAsync(invalid,"{\"version\":1,\"settings\":{}}");
            var rejected=false; try { await model.LoadImportAsync(invalid); } catch(IOException) { rejected=true; }
            Check(rejected&&!model.HasImport&&!model.HasImportPreview&&!model.CanApplyImport,"Invalid or old-format selection clears the previous restore draft");
            var portable=Path.Combine(output,"portable.flowdeck.json"); model.PortableExport=true; await model.ExportSettingsToAsync(portable); await model.LoadImportAsync(portable); await Settle();
            Check(model.ImportRows.All(r=>r.Selected.Target is null)&&!model.ImportConfirmed,"Portable settings start with no destination selected");
            var first=model.ImportRows.First(r=>r.Choices.Any(c=>c.Target is not null));
            var list=Descendants<ListBox>(expander).Single(l=>ReferenceEquals(l.ItemsSource,model.ImportRows)); list.ScrollIntoView(first); await Settle();
            var picker=Descendants<ComboBox>(expander).Single(c=>ReferenceEquals(c.DataContext,first));
            picker.SetCurrentValue(Selector.SelectedItemProperty,first.Choices.Single(c=>c.Target==first.Source)); await Settle();
            Check(first.Selected.Target==first.Source&&!model.ImportConfirmed,"Explicit destination selection updates the draft through the ComboBox");
            var duplicate=model.ImportRows.First(r=>r!=first&&r.Choices.Any(c=>c.Target==first.Source));
            duplicate.Selected=duplicate.Choices.Single(c=>c.Target==first.Source);
            Check(duplicate.Selected.Target is null&&model.Notice.Contains("割り当て"),"Duplicate destinations are rejected without losing the existing mapping");
            foreach(var row in model.ImportRows) row.Selected=row.Choices.Single(c=>c.Target==row.Source);
            var portableConfiguration=(await DeviceSettingsArchives.ReadAsync(portable)).Configuration;
            var previewWrites=demo.Hubs.Select(h=>(h.Writes,h.LightingWrites)).ToArray();
            model.ImportConfirmed=true; await model.PreviewImportAsync();
            Check(model.CanApplyImport&&model.HasImportPreview&&!string.IsNullOrWhiteSpace(model.ImportChanges)&&
                portableConfiguration.Bindings.Devices.Length==0&&
                SettingsTransfer.Hash(portableConfiguration.Cooling.Endpoints.Select(e=>new { e.Target,e.Safety,e.Profiles }))==
                    SettingsTransfer.Hash(archive.Configuration.Cooling.Endpoints.Select(e=>new { e.Target,e.Safety,e.Profiles }))&&
                SettingsTransfer.Hash(portableConfiguration.Lighting.Endpoints!.Select(e=>new { e.Target,e.Exit }))==
                    SettingsTransfer.Hash(archive.Configuration.Lighting.Endpoints!.Select(e=>new { e.Target,e.Exit }))&&
                previewWrites.SequenceEqual(demo.Hubs.Select(h=>(h.Writes,h.LightingWrites))),
                "Portable settings retain actual cooling and lighting values without physical bindings, and mapped preview produces a difference without hardware output");
            first.Selected=first.Choices[0]; await Settle();
            Check(!model.ImportConfirmed&&!model.HasImportPreview&&!model.CanApplyImport,"Changing a mapping invalidates both confirmation and preview");
            first.Selected=first.Choices.Single(c=>c.Target==first.Source); model.ImportConfirmed=true; await model.PreviewImportAsync(); await model.ApplyImportAsync();
            var after=Path.Combine(output,"after.flowdeck.json"); model.PortableExport=false; await model.ExportSettingsToAsync(after);
            var restored=(await DeviceSettingsArchives.ReadAsync(after)).Configuration;
            Check(restored.Bindings.Devices.Length==0&&restored.Cooling.Endpoints.All(e=>!e.WiringConfirmed)&&restored.Lighting.Endpoints!.All(e=>!e.WiringConfirmed),"Portable restore preserves settings but grants no physical bindings or output permissions");
            await model.LoadImportAsync(backup); model.ImportConfirmed=true; await model.PreviewImportAsync();
            demo.Hubs[1].Disconnected=true; await demo.Runtime.PollAsync(); await model.PollAsync();
            Check(!model.CanApplyImport&&model.ImportRows.Count==stable.Length&&model.ImportStatus.Contains("構成が変わり"),"Topology change blocks a preview without discarding the mapping draft");
            model.ClearImport.Execute(null); await Settle(); Check(!model.HasImport,"Close discards only the restore draft");
            demo.Hubs[1].Disconnected=false; await demo.Runtime.PollAsync(); await demo.Runtime.PollAsync();
            Check(await model.CloseAsync(),"Normal close completes after settings restoration");
            await File.WriteAllLinesAsync(Path.Combine(output,"settings-transfer-smoke.txt"),report); return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e); await File.WriteAllLinesAsync(Path.Combine(output,"settings-transfer-smoke.txt"),report); return 1; }
    }
}
