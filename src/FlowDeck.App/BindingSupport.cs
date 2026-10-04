using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace FlowDeck.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    public void Refresh() => Notify("");
}
public sealed class AsyncCommand(Func<object?, Task> run, Func<object?, bool>? canRun = null, Action<Exception>? onError = null) : ICommand
{
    private bool running;
    public bool CanExecute(object? parameter) => !running && (canRun?.Invoke(parameter) ?? true);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; CommandManager.InvalidateRequerySuggested();
        try { await run(parameter); }
        catch (Exception e) { onError?.Invoke(e); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (parameter?.ToString()?.Split('|').Contains(value?.ToString()) == true) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class ChoiceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}
public sealed class SelectedPageConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && NavPage(values[0]) == NavPage(values[1]);
    private static string? NavPage(object value) => value?.ToString() switch { "8" or "1" => "7", "4" or "5" or "6" or "9" or "10" => "3", var v => v };
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class CurvePointRow(double temperature, int duty) : Observable
{
    private double temperature = temperature;
    private int duty = duty;
    public double Temperature { get => temperature; set => Set(ref temperature, value); }
    public int Duty { get => duty; set => Set(ref duty, value); }
}
