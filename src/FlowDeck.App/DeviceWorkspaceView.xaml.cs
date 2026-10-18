using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FlowDeck.App;

public partial class DeviceWorkspaceView : UserControl
{
    private readonly DispatcherTimer preview = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private DeviceWorkspaceViewModel? feedbackSource;
    private DispatcherOperation? feedbackUpdate;
    private string announcedStatus = "", announcedWarning = "";
    private IInputElement? detailsReturnFocus;
    private Window? detailsOwner;
    private bool restoreDetailsFocus;

    public DeviceWorkspaceView()
    {
        InitializeComponent();
        preview.Tick += (_, _) => { if (DataContext is DeviceWorkspaceViewModel model) model.TickLightingPreview(); };
        Loaded += (_, _) => { preview.Start(); AttachFeedback(); };
        Unloaded += (_, _) => { preview.Stop(); DetachFeedback(); HideStatusDetails(false); };
        DataContextChanged += (_, _) => { DetachFeedback(); HideStatusDetails(false); if (IsLoaded) AttachFeedback(); };
    }

    private void ChooseAccentColor(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceWorkspaceViewModel model) return;
        using var picker = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.ColorTranslator.FromHtml(model.AccentColor),
            CustomColors = model.AccentChoices.Select(choice => System.Drawing.ColorTranslator.ToWin32(System.Drawing.ColorTranslator.FromHtml(choice.Hex))).ToArray()
        };
        var handle = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(handle);
        try
        {
            if (picker.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                model.AccentColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        }
        finally { owner.ReleaseHandle(); }
    }

    private void AttachFeedback()
    {
        DetachFeedback();
        feedbackSource = DataContext as DeviceWorkspaceViewModel;
        if (feedbackSource is null) return;
        feedbackSource.PropertyChanged += FeedbackChanged;
        QueueFeedback(feedbackSource);
    }

    private void DetachFeedback()
    {
        if (feedbackSource is not null) feedbackSource.PropertyChanged -= FeedbackChanged;
        feedbackSource = null;
        feedbackUpdate?.Abort(); feedbackUpdate = null;
    }

    private void FeedbackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DeviceWorkspaceViewModel source && (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(DeviceWorkspaceViewModel.StatusMessage) or nameof(DeviceWorkspaceViewModel.WarningMessage) or
                nameof(DeviceWorkspaceViewModel.HasStatusDetails))) QueueFeedback(source);
    }

    private void QueueFeedback(DeviceWorkspaceViewModel source)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => QueueFeedback(source)));
            return;
        }
        if (!IsLoaded || !ReferenceEquals(source, feedbackSource) || feedbackUpdate is not null) return;
        // Let bindings update before raising UIA. A poll or repeated identical message
        // may invalidate bindings, but must not announce the same text again.
        feedbackUpdate = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            feedbackUpdate = null;
            if (!IsLoaded || !ReferenceEquals(source, feedbackSource)) return;
            AnnounceChanged(WorkspaceStatusMessage, source.StatusMessage, ref announcedStatus);
            AnnounceChanged(WorkspaceWarningMessage, source.WarningMessage, ref announcedWarning);
            if (!source.HasStatusDetails && StatusDetailsPopup.IsOpen) HideStatusDetails(true);
        }));
    }

    private static void AnnounceChanged(TextBlock element, string text, ref string previous)
    {
        if (text == previous) return;
        previous = text;
        if (string.IsNullOrWhiteSpace(text)) return;
        element.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ToggleStatusDetails(object sender, RoutedEventArgs e)
    {
        if (StatusDetailsButton.IsChecked != true) { HideStatusDetails(true); return; }
        if (DataContext is not DeviceWorkspaceViewModel { HasStatusDetails: true })
        { StatusDetailsButton.IsChecked = false; return; }
        detailsReturnFocus = Keyboard.FocusedElement;
        restoreDetailsFocus = false;
        StatusDetailsPopup.IsOpen = true;
    }

    private void StatusDetailsOpened(object? sender, EventArgs e)
    {
        // Manage dismissal explicitly: Popup's automatic mouse dismissal can
        // close and immediately reopen it when the same toggle is clicked.
        detailsOwner = Window.GetWindow(this);
        if (detailsOwner is not null)
        {
            detailsOwner.PreviewMouseDown += DetailsOutsideMouseDown;
            detailsOwner.PreviewKeyDown += StatusDetailsKeyDown;
            detailsOwner.Deactivated += DetailsOwnerDeactivated;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsLoaded && StatusDetailsPopup.IsOpen) StatusDetailsText.Focus();
        }));
    }

    private void CloseStatusDetails(object sender, RoutedEventArgs e) => HideStatusDetails(true);

    private void DetailsOutsideMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!StatusDetailsButton.IsMouseOver) HideStatusDetails(false);
    }

    private void DetailsOwnerDeactivated(object? sender, EventArgs e) => HideStatusDetails(false);

    private void StatusDetailsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true; HideStatusDetails(true);
    }

    private void HideStatusDetails(bool restoreFocus)
    {
        restoreDetailsFocus = restoreFocus;
        StatusDetailsPopup.IsOpen = false;
        StatusDetailsButton.IsChecked = false;
        if (!restoreFocus) detailsReturnFocus = null;
    }

    private void StatusDetailsClosed(object? sender, EventArgs e)
    {
        if (detailsOwner is not null)
        {
            detailsOwner.PreviewMouseDown -= DetailsOutsideMouseDown;
            detailsOwner.PreviewKeyDown -= StatusDetailsKeyDown;
            detailsOwner.Deactivated -= DetailsOwnerDeactivated;
            detailsOwner = null;
        }
        StatusDetailsButton.IsChecked = false;
        var target = detailsReturnFocus as UIElement;
        detailsReturnFocus = null;
        var restore = restoreDetailsFocus; restoreDetailsFocus = false;
        // Clicking elsewhere already chose a new focus target. Only an explicit
        // close/Escape returns focus, and never activates an inactive app window.
        if (!restore || !IsLoaded || Window.GetWindow(this)?.IsActive != true) return;
        if (target is { IsVisible: true, IsEnabled: true, Focusable: true }) target.Focus();
        else StatusDetailsButton.Focus();
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
