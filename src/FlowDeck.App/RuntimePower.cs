using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    private readonly SemaphoreSlim powerEvents = new(1);
    public async Task ServicePowerAsync(bool resume)
    {
        await powerEvents.WaitAsync();
        try
        {
            if (Coordinator is not RemoteControlCoordinator remote) return;
            if (resume)
            {
                try { await remote.ResumeFromSleepAsync(); }
                finally { SuspendNotifications(false); }
            }
            else { SuspendNotifications(true); await remote.PrepareSleepAsync(); }
        }
        catch (Exception e) { Warning = e.Message; Log.Write("servicePowerGui", new { resume, error = e.Message }); }
        finally { powerEvents.Release(); }
    }
}
