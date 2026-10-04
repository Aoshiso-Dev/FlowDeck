using System.Windows.Controls;
using System.Windows;

namespace FlowDeck.App;

public partial class DeviceM5SettingsView : UserControl
{
    public DeviceM5SettingsView() => InitializeComponent();
    private async void PairWifi(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceWorkspaceViewModel model) return;
        var password = WifiPassword.Password; WifiPassword.Clear();
        await model.PairM5Async(password);
    }
}
