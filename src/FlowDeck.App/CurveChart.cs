using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class CurveChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IEnumerable), typeof(CurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PointsChanged));
    public static readonly DependencyProperty SelectedPointProperty = DependencyProperty.Register(nameof(SelectedPoint), typeof(CurvePointRow), typeof(CurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MinimumDutyProperty = DependencyProperty.Register(nameof(MinimumDuty), typeof(int?), typeof(CurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, LimitsChanged));
    public static readonly DependencyProperty AllowStopProperty = DependencyProperty.Register(nameof(AllowStop), typeof(bool), typeof(CurveChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, LimitsChanged));
    public static readonly DependencyProperty RequestedOutputProperty = DependencyProperty.Register(nameof(RequestedOutput), typeof(bool), typeof(CurveChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, LimitsChanged));
    public static readonly DependencyProperty CurrentTemperatureProperty = DependencyProperty.Register(nameof(CurrentTemperature), typeof(double?), typeof(CurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CurrentDutyProperty = DependencyProperty.Register(nameof(CurrentDuty), typeof(double?), typeof(CurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ThemeProperty = DependencyProperty.Register("Theme", typeof(Brush), typeof(CurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable? Points { get => (IEnumerable?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public CurvePointRow? SelectedPoint { get => (CurvePointRow?)GetValue(SelectedPointProperty); set => SetValue(SelectedPointProperty, value); }
    public int? MinimumDuty { get => (int?)GetValue(MinimumDutyProperty); set => SetValue(MinimumDutyProperty, value); }
    public bool AllowStop { get => (bool)GetValue(AllowStopProperty); set => SetValue(AllowStopProperty, value); }
    public bool RequestedOutput { get => (bool)GetValue(RequestedOutputProperty); set => SetValue(RequestedOutputProperty,value); }
    // Generic curves express demand; the policy applies the endpoint minimum and stop permission later.
    private int? EditingMinimum => RequestedOutput && MinimumDuty is >= 1 and <= 100 ? 1 : MinimumDuty;
    private bool EditingAllowStop => RequestedOutput || AllowStop;
    public double? CurrentTemperature { get => (double?)GetValue(CurrentTemperatureProperty); set => SetValue(CurrentTemperatureProperty, value); }
    public double? CurrentDuty { get => (double?)GetValue(CurrentDutyProperty); set => SetValue(CurrentDutyProperty, value); }
    private readonly List<INotifyPropertyChanged> watched = [];
    private readonly Stack<PointState[]> undo = new();
    private sealed record PointState(CurvePointRow Row, double Temperature, int Duty);
    private CurvePointRow? dragging;
    private IEnumerable? dragOwner;
    private PointState[]? dragBefore;
    private (double Low, double High)? dragRange;
    private bool writing;
    private IList? Editable => Points as IList is { IsReadOnly: false, IsFixedSize: false } list ? list : null;
    private CurvePointRow[] Rows => Points?.OfType<CurvePointRow>().ToArray() ?? [];
    private CurvePoint[] Values => Rows.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray();
    internal bool CanEdit => IsEnabled && Editable is not null && CurveEditing.ValidShape(Values, EditingMinimum, EditingAllowStop);
    internal bool IsDragging => dragging is not null;
    internal bool CanUndo => undo.Count > 0 && Editable is not null;
    public ICommand AddPointCommand { get; }
    public ICommand DeletePointCommand { get; }
    internal Rect Plot => new(38, 38, Math.Max(1, ActualWidth - 54), Math.Max(1, ActualHeight - 80));
    private (double Low, double High) Range
    {
        get
        {
            if (dragRange is { } frozen) return frozen;
            var finite = Rows.Select(p => p.Temperature).Where(double.IsFinite).ToArray();
            return (Math.Min(0, finite.Length > 0 ? Math.Floor(finite.Min() / 10) * 10 : 0), Math.Max(100, finite.Length > 0 ? Math.Ceiling(finite.Max() / 10) * 10 : 100));
        }
    }
    public CurveChart()
    {
        Focusable = true; Cursor = Cursors.Cross; SetResourceReference(ThemeProperty, "AccentBrush");
        AddPointCommand = new AsyncCommand(_ => { AddSuggestedPoint(); return Task.CompletedTask; }, _ => CanEdit);
        DeletePointCommand = new AsyncCommand(_ => { RemoveSelected(); return Task.CompletedTask; }, _ => IsEnabled && Editable is not null && SelectedPoint is not null);
        ToolTip = "ドラッグ・矢印キーで調整 / Shiftで5刻み / PageUp・Downで選択 / ダブルクリックで追加 / Deleteで削除 / Escで取消 / Ctrl+Zで戻す";
        AutomationProperties.SetName(this, "温度とファン出力のカーブ編集");
        Unloaded += (_, _) => CancelEdit(); IsVisibleChanged += (_, _) => { if (!IsVisible) CancelEdit(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelEdit(); };
    }
    private static void LimitsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { var c = (CurveChart)d; c.CancelEdit(); c.undo.Clear(); }
    private static void PointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (CurveChart)d; c.CancelEdit(); c.undo.Clear();
        if (e.OldValue is INotifyCollectionChanged old) CollectionChangedEventManager.RemoveHandler(old, c.CollectionChanged);
        if (e.NewValue is INotifyCollectionChanged next) CollectionChangedEventManager.AddHandler(next, c.CollectionChanged);
        c.Subscribe(); c.ValidateSelection();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { if (!writing) { CancelEdit(); undo.Clear(); } Subscribe(); ValidateSelection(); InvalidateVisual(); }
    private void Subscribe()
    {
        foreach (var item in watched) PropertyChangedEventManager.RemoveHandler(item, ItemChanged, ""); watched.Clear();
        foreach (var item in Rows) { PropertyChangedEventManager.AddHandler(item, ItemChanged, ""); watched.Add(item); }
    }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e) { if (!writing) { CancelEdit(false); undo.Clear(); } InvalidateVisual(); }
    private void ValidateSelection() { if (SelectedPoint is not null && !Rows.Contains(SelectedPoint)) SetCurrentValue(SelectedPointProperty, null); }
    private PointState[] SnapshotPoints() => Rows.Select(p => new PointState(p, p.Temperature, p.Duty)).ToArray();
    private void Remember(PointState[] before)
    {
        if (before.Length == Rows.Length && before.Zip(Rows).All(p => p.First.Row == p.Second && p.First.Temperature == p.Second.Temperature && p.First.Duty == p.Second.Duty)) return;
        if (undo.Count >= 32) undo.Clear(); undo.Push(before);
    }
    internal Point Position(double temperature, double duty)
    { var r = Range; var p = Plot; return new(p.Left + (temperature - r.Low) / (r.High - r.Low) * p.Width, p.Bottom - Math.Clamp(duty, 0, 100) / 100 * p.Height); }
    internal bool BeginEdit(int index)
    {
        CancelEdit(); if (!CanEdit || index < 0 || index >= Rows.Length) return false;
        dragging = Rows[index]; dragOwner = Points; dragBefore = SnapshotPoints(); dragRange = Range;
        SetCurrentValue(SelectedPointProperty, dragging); InvalidateVisual(); return true;
    }
    internal void DragTo(Point position)
    {
        if (dragging is null || dragOwner != Points || !Rows.Contains(dragging)) return;
        var range = Range; var plot = Plot;
        MovePoint(dragging, range.Low + (position.X - plot.Left) / plot.Width * (range.High - range.Low), (plot.Bottom - position.Y) / plot.Height * 100, range);
    }
    private bool MovePoint(CurvePointRow point, double temperature, double duty, (double Low, double High) range)
    {
        var next = CurveEditing.Move(Values, Array.IndexOf(Rows, point), temperature, duty, EditingMinimum, EditingAllowStop, range.Low, range.High);
        if (next is null) return false;
        writing = true; try { point.Temperature = next.Temperature; point.Duty = next.Duty; } finally { writing = false; }
        InvalidateVisual(); return true;
    }
    internal void CommitEdit()
    {
        if (dragOwner == Points && dragBefore is { } before) Remember(before);
        dragging = null; dragBefore = null; dragOwner = null; dragRange = null;
        if (IsMouseCaptured) ReleaseMouseCapture(); InvalidateVisual();
    }
    internal void CancelEdit(bool restore = true)
    {
        if (restore && dragOwner == Points && dragging is { } row && Rows.Contains(row) && dragBefore?.FirstOrDefault(p => p.Row == row) is { } original)
        { writing = true; try { row.Temperature = original.Temperature; row.Duty = original.Duty; } finally { writing = false; } }
        dragging = null; dragOwner = null; dragBefore = null; dragRange = null;
        if (IsMouseCaptured) ReleaseMouseCapture(); InvalidateVisual();
    }
    internal void Undo()
    {
        CancelEdit(); if (!IsEnabled || !CanUndo || Editable is not { } list) return;
        var selected = SelectedPoint; var before = undo.Pop(); writing = true;
        try { list.Clear(); foreach (var value in before) { value.Row.Temperature = value.Temperature; value.Row.Duty = value.Duty; list.Add(value.Row); } }
        finally { writing = false; }
        SetCurrentValue(SelectedPointProperty, selected is not null && Rows.Contains(selected) ? selected : Rows.FirstOrDefault());
        ValidateSelection(); InvalidateVisual();
    }
    private void AddSuggestedPoint()
    {
        var points = Values; var range = Range; double t; double duty;
        if (points.Length == 0) { t = 40; duty = MinimumDuty ?? 100; }
        else if (points.Length == 1) { t = points[0].Temperature + 20 <= range.High ? points[0].Temperature + 20 : points[0].Temperature - 20; duty = points[0].Duty; }
        else
        {
            var gap = Enumerable.Range(1, points.Length - 1).OrderByDescending(i => points[i].Temperature - points[i - 1].Temperature).First();
            t = (points[gap - 1].Temperature + points[gap].Temperature) / 2; duty = FanPolicyEngine.Interpolate(points, t);
        }
        InsertPoint(t, duty);
    }
    internal bool InsertPoint(double temperature, double duty)
    {
        if (!CanEdit || Editable is not { } list) return false;
        var range = Range; var next = CurveEditing.Insert(Values, temperature, duty, EditingMinimum, EditingAllowStop, range.Low, range.High);
        if (next is not { } added) return false;
        var before = SnapshotPoints(); var row = new CurvePointRow(added.Point.Temperature, added.Point.Duty); writing = true;
        try { list.Insert(added.Index, row); } finally { writing = false; }
        SetCurrentValue(SelectedPointProperty, row); Remember(before); InvalidateVisual(); return true;
    }
    internal void RemoveSelected()
    {
        CancelEdit(); if (!IsEnabled || Editable is not { } list || SelectedPoint is not { } row || !Rows.Contains(row)) return;
        var before = SnapshotPoints(); writing = true; try { list.Remove(row); } finally { writing = false; }
        Remember(before); ValidateSelection(); InvalidateVisual();
    }
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape) { CancelEdit(); return true; }
        if (key == Key.Z && modifiers == ModifierKeys.Control) { Undo(); return true; }
        if (key == Key.Delete) { RemoveSelected(); return true; }
        if (Rows.Length == 0) return false;
        var index = Math.Max(0, Array.IndexOf(Rows, SelectedPoint));
        if (key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        { var next = key == Key.Home ? 0 : key == Key.End ? Rows.Length - 1 : (index + (key == Key.PageUp ? -1 : 1) + Rows.Length) % Rows.Length; SetCurrentValue(SelectedPointProperty, Rows[next]); return true; }
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down) || !CanEdit) return false;
        CancelEdit(); var point = Rows[index]; SetCurrentValue(SelectedPointProperty, point);
        var step = modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        var temperature = point.Temperature + (key == Key.Left ? -step : key == Key.Right ? step : 0);
        var duty = point.Duty + (key == Key.Down ? -step : key == Key.Up ? step : 0);
        if (EditingAllowStop && key == Key.Up && point.Duty == 0) duty = EditingMinimum!.Value;
        if (EditingAllowStop && key == Key.Down && point.Duty == EditingMinimum) duty = 0;
        var before = SnapshotPoints(); var r = Range;
        if (MovePoint(point, temperature, duty, (Math.Min(r.Low, temperature), Math.Max(r.High, temperature)))) Remember(before);
        return true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (!CanEdit) return; Focus(); var position = e.GetPosition(this); var rows = Rows;
        var index = Array.FindIndex(rows, p => (Position(p.Temperature, p.Duty) - position).Length <= 12);
        if (index >= 0) { if (BeginEdit(index)) CaptureMouse(); e.Handled = true; }
        else if (e.ClickCount == 2 && Plot.Contains(position))
        { var r = Range; InsertPoint(r.Low + (position.X - Plot.Left) / Plot.Width * (r.High - r.Low), (Plot.Bottom - position.Y) / Plot.Height * 100); e.Handled = true; }
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsDragging) { DragTo(e.GetPosition(this)); e.Handled = true; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if (IsDragging) { CommitEdit(); e.Handled = true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); CancelEdit(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true; }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); CancelEdit(); InvalidateVisual(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);
    private sealed class ChartPeer(CurveChart chart) : FrameworkElementAutomationPeer(chart)
    {
        protected override string GetClassNameCore() => nameof(CurveChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetHelpTextCore() => chart.ToolTip.ToString() ?? "";
    }
}
