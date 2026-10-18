using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FlowDeck.App;

public partial class FlowDeckWindow : Window
{
    private readonly FlowDeckApplication app;
    public FlowDeckWindow(FlowDeckApplication app, FlowDeckViewModel model)
    {
        this.app = app; InitializeComponent(); DataContext = model;
        CpuFanCard.DataContext = model;
        BoardFanList.ItemContainerGenerator.StatusChanged += (_, _) => QueueBoardEditor();
        BoardFanList.Loaded += (_, _) => QueueBoardEditor();
        Closing += OnClosing;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlowDeckViewModel.SelectedPage))
            {
                PageScroll.ScrollToTop();
                QueueBoardEditor(model.SelectedPage == 8);
                if (model.SelectedPage == 7 && model.HubControlsOpen)
                    Dispatcher.BeginInvoke(() => HubControlExpander.BringIntoView(new Rect(0, 0, HubControlExpander.ActualWidth, 120)), DispatcherPriority.Loaded);
            }
            if (e.PropertyName is nameof(FlowDeckViewModel.SelectedBoardFan) or nameof(FlowDeckViewModel.CpuFanControlsOpen)) QueueBoardEditor();
        };
    }
    private bool editorUpdateQueued;
    private void QueueBoardEditor(bool reveal = false)
    {
        if (editorUpdateQueued && !reveal) return;
        editorUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            editorUpdateQueued = false;
            if (DataContext is not FlowDeckViewModel model) return;
            var row = BoardFanList.Items.Cast<BoardFanRow>().FirstOrDefault(r => r.Port == model.SelectedBoardFan);
            var container = row is null ? null : BoardFanList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            container?.ApplyTemplate();
            var target = model.CpuFanControlsOpen ? container?.Template.FindName("InlineEditorHost", container) as ContentControl : null;
            if (target is not null && ReferenceEquals(target.Content, CpuFanCard))
            {
                if (reveal && model.SelectedPage is 7 or 8) CpuFanCard.BringIntoView(new Rect(0, 0, CpuFanCard.ActualWidth, 100));
                return;
            }
            // One editor retains keyboard state and unsaved drafts across telemetry updates.
            if (CpuFanCard.Parent is Panel parking) parking.Children.Remove(CpuFanCard);
            else if (CpuFanCard.Parent is ContentControl previous) previous.Content = null;
            if (target is null) BoardEditorParking.Children.Add(CpuFanCard);
            else
            {
                target.Content = CpuFanCard;
                if (reveal && model.SelectedPage is 7 or 8) CpuFanCard.BringIntoView(new Rect(0, 0, CpuFanCard.ActualWidth, 100));
            }
        }, DispatcherPriority.Loaded);
    }
    private void BoardFanRowClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BoardFanRow row || DataContext is not FlowDeckViewModel model || !model.BoardFanCanSelect) return;
        var close = CpuFanCard.Parent is ContentControl { DataContext: BoardFanRow displayed } && displayed.Port == row.Port && model.CpuFanControlsOpen;
        model.SelectedBoardFan = row.Port;
        model.CpuFanControlsOpen = !close;
    }
    private void BoardFanKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.OriginalSource is not ListBoxItem || DataContext is not FlowDeckViewModel model) return;
        model.CpuFanControlsOpen = !model.CpuFanControlsOpen;
        e.Handled = true;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (app.Exiting) return;
        e.Cancel = true; app.HideToTray();
    }
}
