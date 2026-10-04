namespace FlowDeck.App;

public partial class DeviceWorkspaceView : System.Windows.Controls.UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer preview = new() { Interval = TimeSpan.FromMilliseconds(50) };
    public DeviceWorkspaceView()
    {
        InitializeComponent();
        preview.Tick += (_, _) => { if (DataContext is DeviceWorkspaceViewModel model) model.TickLightingPreview(); };
        Loaded += (_, _) => preview.Start(); Unloaded += (_, _) => preview.Stop();
    }
    private void BringSetupIntoView(object sender, System.Windows.RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (DataContext is DeviceWorkspaceViewModel { SelectedPage: 3, SetupExpanded: true })
                SetupSection.BringIntoView(new System.Windows.Rect(0, 0, SetupSection.ActualWidth, Math.Min(200, SetupSection.ActualHeight)));
        }));
    }
}
