using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task RunCurveEditorChecksAsync(RuntimeHost host, FlowDeckViewModel vm, FlowDeckWindow window, string output, Action<bool, string> check)
    {
        var core = host.Coordinator; var hub = (DemoHub)host.Hub;
        var oldProfile = Contract.Copy(core.Settings.Profiles.Single(p => p.Id == "balanced"));
        vm.CurveProfile = vm.Profiles.Single(p => p.Id == "balanced"); vm.ReloadCurve.Execute(null); vm.SelectedPage = 1;
        vm.UseCpu = true; vm.UseGpu = false; vm.CpuPoints.Clear();
        vm.CpuPoints.Add(new(30, 30)); vm.CpuPoints.Add(new(50, 50)); vm.CpuPoints.Add(new(80, 90));
        vm.CpuOverheat = "95"; vm.CpuResume = "85"; vm.DescentDelay = "0"; vm.DescentRate = "5"; vm.Refresh();
        window.Width = 1200; window.Height = 900; await Settle(); window.PageScroll.ScrollToTop();
        var chart = window.HubCpuCurve; var before = core.Snapshot; var writes = hub.Writes;
        check(chart.CanEdit && chart.MinimumDuty == core.Settings.Limits.Minimum && !Descendants<DataGrid>(window).Any(g => g.IsVisible),
            "Hub curve uses the configured minimum and starts with numeric tables collapsed");
        check(chart.BeginEdit(1), "Curve pointer gesture selects an existing point");
        chart.DragTo(chart.Position(60, 65));
        check(vm.CpuPoints[1].Temperature == 60 && vm.CpuPoints[1].Duty == 65 && vm.SelectedCpuPoint == vm.CpuPoints[1],
            "Dragging changes the bound draft and shares selection with the numeric editor");
        chart.HandleKey(Key.Escape, ModifierKeys.None);
        check(vm.CpuPoints[1].Temperature == 50 && vm.CpuPoints[1].Duty == 50 && !chart.IsDragging, "Escape restores the pre-drag point");
        chart.BeginEdit(1); chart.DragTo(chart.Position(60, 65)); chart.CommitEdit();
        chart.BeginEdit(1); chart.DragTo(chart.Position(200, -20)); chart.CommitEdit();
        check(Math.Abs(vm.CpuPoints[1].Temperature - 79.9) < .0001 && vm.CpuPoints[1].Duty == 30,
            "Dragging past the plot cannot cross the next temperature or the previous output");
        chart.HandleKey(Key.Z, ModifierKeys.Control);
        check(vm.CpuPoints[1].Temperature == 60 && vm.CpuPoints[1].Duty == 65, "Undo restores a committed drag without device commands");
        chart.HandleKey(Key.Right, ModifierKeys.Shift);
        chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Up) { RoutedEvent = Keyboard.KeyDownEvent });
        check(vm.CpuPoints[1].Temperature == 65 && vm.CpuPoints[1].Duty == 66, "Shift arrows use five degrees and the routed Up key uses one percent");
        check(chart.InsertPoint(45, 45) && vm.CpuPoints.Count == 4 && vm.CpuPoints[1].Temperature == 45, "Graph insertion places a point in sorted temperature order");
        chart.HandleKey(Key.Delete, ModifierKeys.None); check(vm.CpuPoints.Count == 3, "Delete removes only the selected curve point");
        chart.Undo(); check(vm.CpuPoints.Count == 4 && vm.CpuPoints[1].Temperature == 45, "Undo restores a removed point at its original position");
        var pointCount = vm.CpuPoints.Count;
        var addButton = Descendants<Button>(window).Single(b => b.IsVisible && ReferenceEquals(b.Command, chart.AddPointCommand));
        ((IInvokeProvider)new ButtonAutomationPeer(addButton).GetPattern(PatternInterface.Invoke)!).Invoke(); await Settle();
        check(vm.CpuPoints.Count == pointCount + 1 && chart.CanUndo, "Visible Add point button uses the graph editor and its undo history");
        chart.Undo();
        chart.BeginEdit(0); chart.DragTo(chart.Position(35, 35));
        chart.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        check(!chart.IsDragging && vm.CpuPoints[0].Temperature == 30 && vm.CpuPoints[0].Duty == 30, "Losing mouse capture cancels an unfinished drag");
        vm.SelectedCpuPoint = vm.CpuPoints[0]; await Settle();
        check(chart.SelectedPoint == vm.CpuPoints[0], "External point selection updates the graph binding");
        var draft = vm.CpuPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray();
        await core.TickAsync(); await Settle();
        check(vm.CpuPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).SequenceEqual(draft) && chart.CurrentTemperature == vm.HubCurveCpuTemperature && chart.CurrentDuty == 59,
            "Live temperature and actual output update without replacing curve drafts");
        host.DemoTemperature!.Missing = true; await core.TickAsync(); await Until(() => chart.CurrentTemperature is null);
        check(chart.CurrentTemperature is null && vm.CpuPoints.Count == 4, "Missing live temperature clears its marker while retaining the curve");
        host.DemoTemperature.Missing = false; await core.TickAsync(); await Until(() => chart.CurrentTemperature is not null);
        check(core.Snapshot.ControlRevision == before.ControlRevision && hub.Writes == writes && core.Snapshot.ConfirmedDuty == 59,
            "Graph edits keyboard undo and telemetry changes never apply fan output");
        foreach (var theme in new[] { "system", "light", "dark" }) { ThemeManager.Apply(theme); await Settle(); Capture(window, output, "curve-editor-" + theme); }
        window.Width = 960; window.Height = 640; await Settle();
        check(chart.ActualWidth >= 240 && chart.Plot.Width > 180 && window.HubGpuCurve.ActualWidth >= 240, "Both Hub graphs remain usable at minimum window width");
        Capture(window, output, "curve-editor-minimum-dark");
        var savedRevision = oldProfile.Revision; vm.SaveCurve.Execute(null);
        await Until(() => core.Settings.Profiles.Single(p => p.Id == oldProfile.Id).Revision > savedRevision);
        await host.FlushSettingsAsync();
        check(core.Snapshot.Mode == FanMode.Manual && core.Snapshot.ConfirmedDuty == 59 &&
            (await host.Store.LoadAsync()).Profiles.Single(p => p.Id == oldProfile.Id).Cpu!.Points.SequenceEqual(draft),
            "Saving a graph persists the edited curve and keeps manual control running");
        vm.SaveCurve.Execute("apply"); await Until(() => core.Snapshot.Mode == FanMode.Auto && !core.Snapshot.FanBusy);
        check(core.Snapshot.LastConfirmed?.Profile?.Cpu?.Points.SequenceEqual(draft) == true && core.Snapshot.ConfirmedDuty is >= 30 and <= 90,
            "Explicit save-and-apply runs the edited Hub curve through the existing coordinator");
        await core.RequestAsync(new(Guid.NewGuid().ToString("N"), "UI curve test restore", core.Epoch, core.Snapshot.ControlRevision, "SetManualDuty", Duty: 59));
        await core.SaveProfileAsync(oldProfile, core.Settings.Profiles.Single(p => p.Id == oldProfile.Id).Revision); vm.ReloadCurve.Execute(null); await Settle();
        var isolated = new ObservableCollection<CurvePointRow> { new(30, 30), new(80, 80) };
        var isolatedChart = new CurveChart { Points = isolated, MinimumDuty = 25 }; isolatedChart.Measure(new(350, 250)); isolatedChart.Arrange(new(0, 0, 350, 250));
        isolatedChart.BeginEdit(0); isolatedChart.DragTo(isolatedChart.Position(40, 40)); isolated.Clear(); isolated.Add(new(10, 50)); isolated.Add(new(90, 90));
        isolatedChart.CancelEdit(); check(isolated[0].Temperature == 10 && !isolatedChart.CanUndo && !isolatedChart.IsDragging,
            "Loading another profile during a drag discards the gesture and its undo history");
        isolatedChart.MinimumDuty = null; check(!isolatedChart.BeginEdit(0), "Unconfigured minimum cannot be guessed by the graph editor");
        isolatedChart.MinimumDuty = 25; isolatedChart.AllowStop = true; isolated[0].Duty = 25; isolatedChart.SelectedPoint = isolated[0];
        isolatedChart.HandleKey(Key.Down, ModifierKeys.None); var stopped = isolated[0].Duty;
        isolatedChart.HandleKey(Key.Up, ModifierKeys.None);
        check(stopped == 0 && isolated[0].Duty == 25, "Stop-enabled keyboard editing jumps between zero and the confirmed running minimum");
        vm.SelectedBoardFan = MotherboardFans.Cpu; vm.CpuFanReload.Execute(null); await Settle();
        var oldBoard = Contract.Copy(MotherboardFans.Settings(core.Settings, MotherboardFans.Cpu.Id));
        vm.SelectedPage = 8; window.CpuFanCurveExpander.IsExpanded = true; await Settle(); window.CpuFanCurveExpander.BringIntoView(); await Settle();
        var boardChart = window.BoardCpuCurve; var revision = core.Snapshot.ControlRevision;
        check(boardChart.Points == vm.CpuFanPoints && boardChart.MinimumDuty == oldBoard.Minimum && boardChart.BeginEdit(0),
            "The selected motherboard fan uses the same editor with its own calibrated minimum");
        boardChart.DragTo(boardChart.Position(45, 45)); boardChart.CommitEdit();
        var boardDraft = vm.CpuFanPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray();
        vm.SelectedBoardFan = MotherboardFans.Chassis; vm.SelectedBoardFan = MotherboardFans.Cpu; await Settle();
        check(vm.CpuFanPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).SequenceEqual(boardDraft) && !boardChart.CanUndo,
            "Switching motherboard fan rows preserves each draft and clears cross-fan undo history");
        var boardSensor = host.Motherboard.Board.Sensors.First(s => s.Kind == "temperature" && s.Value is not null);
        var previousSensor = vm.CpuFanSensorId; vm.CpuFanSensorId = boardSensor.Id; await Settle();
        check(boardChart.CurrentTemperature == boardSensor.Value && boardChart.CurrentDuty == vm.BoardCurveDuty,
            "Motherboard sensor selection displays its temperature and the selected fan's actual output");
        vm.CpuFanSensorId = previousSensor;
        check(!host.CpuFan.State.Managing && core.Snapshot.ControlRevision == revision && core.Snapshot.ConfirmedDuty == 59,
            "Motherboard graph editing and sensor selection leave all fan owners unchanged");
        window.Width = 1200; window.Height = 900; ThemeManager.Apply("light"); await Settle(); window.BoardCpuCurve.BringIntoView(); await Settle(); Capture(window, output, "board-curve-editor-light");
        ThemeManager.Apply("dark"); window.Width = 960; window.Height = 640; await Settle(); window.BoardCpuCurve.BringIntoView(); await Settle(); Capture(window, output, "board-curve-editor-minimum-dark");
        vm.CpuFanSave.Execute(null); await Until(() => vm.BoardFanCanSelect && core.Settings.CpuFan.Profile!.Revision > oldBoard.Profile!.Revision);
        check(!host.CpuFan.State.Managing && core.Settings.CpuFan.Profile!.Cpu!.Points.SequenceEqual(boardDraft), "Saving the motherboard graph does not start control");
        vm.CpuFanAuto.Execute(null); await Until(() => host.CpuFan.State.Mode == "auto" && vm.BoardFanCanSelect);
        check(host.CpuFan.State.Reading?.Duty is >= 30 and <= 90 && core.Snapshot.ConfirmedDuty == 59 && host.BoardFans.States.Where(s => s.Key != MotherboardFans.Cpu.Id).All(s => !s.Value.Managing),
            "Applying the edited motherboard curve controls only that fan");
        vm.CpuFanRestore.Execute(null); await Until(() => !host.CpuFan.State.Managing && vm.BoardFanCanSelect);
        await core.UpdateSettingsAsync(MotherboardFans.WithSettings(core.Settings, MotherboardFans.Cpu.Id, oldBoard), false); await host.FlushSettingsAsync(); vm.CpuFanReload.Execute(null);
        window.CpuFanCurveExpander.IsExpanded = false; await Settle(); vm.RevertFan.Execute(null);
    }
}
