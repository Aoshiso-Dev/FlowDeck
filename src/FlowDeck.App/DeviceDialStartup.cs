using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

/// <summary>A single startup attempt for a registered dial; a manual action takes precedence.</summary>
internal sealed class DeviceDialStartup
{
    private bool attempted, suppressed;

    public void Suppress() => suppressed = true;

    public async Task StartAsync(DeviceDialSettings settings, bool demo, Func<bool> canConnect,
        Func<DeviceDialSettings, Func<bool>, Task> connect)
    {
        if (attempted) return;
        attempted = true;
        bool Current() => !suppressed && !demo && DeviceDialSettingsStore.ValidPort(settings.Port) &&
            settings.DeviceId is { } id && DialProtocolSession.IdentityText(id) && canConnect();
        if (Current()) await connect(settings, Current);
    }
}
