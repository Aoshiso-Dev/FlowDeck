using System.Windows;
using System.Windows.Controls;

namespace FlowDeck.App;

public sealed class ExitFailureWindow : Window
{
    public string Choice { get; private set; } = "continue";
    public ExitFailureWindow(string message)
    {
        Title = "終了時出力が未確認です"; Width = Math.Min(640, SystemParameters.WorkArea.Width);
        SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 40);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(1, MaxHeight - 140),
            Content = new TextBlock { Text = message + "\n自動制御を終了すると温度に応じた更新が止まります。", TextWrapping = TextWrapping.Wrap } });
        var buttons = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        foreach (var (label, choice) in new[] { ("再試行", "retry"), ("終了を中止", "continue"), ("未確認のまま終了", "force") })
        { var button = new Button { Content = label }; button.Click += (_, _) => { Choice = choice; Close(); }; buttons.Children.Add(button); }
        panel.Children.Add(buttons); Content = panel;
    }
}
