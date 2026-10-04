using System.Windows.Input;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private Func<Task>? serviceUpdate;
    private bool reviewingServiceUpdate;
    public bool ServiceUpdateAvailable => serviceUpdate is not null;
    public ICommand ReviewServiceUpdate => new AsyncCommand(_ => ReviewServiceUpdateAsync(),
        _ => ServiceUpdateAvailable && !reviewingServiceUpdate, e => Notice = e.Message);
    internal void ConfigureServiceUpdate(Func<Task>? action)
    { serviceUpdate = action; Notify(nameof(ServiceUpdateAvailable)); CommandManager.InvalidateRequerySuggested(); }
    internal async Task ReviewServiceUpdateAsync()
    {
        if (serviceUpdate is null || reviewingServiceUpdate) return;
        reviewingServiceUpdate = true; CommandManager.InvalidateRequerySuggested();
        try { await serviceUpdate(); }
        finally { reviewingServiceUpdate = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
