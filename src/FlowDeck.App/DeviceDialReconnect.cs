using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

/// <summary>Single-dispatcher retry policy for one saved identity and port.</summary>
internal sealed class DeviceDialReconnect
{
    private long revision;
    private int failures;
    private bool? present;
    public DeviceDialSettings? Target { get; private set; }
    public bool Enabled => Target is not null;
    public bool Attempting { get; private set; }
    public long NextAttemptAt { get; private set; }

    public void Enable(DeviceDialSettings settings, bool demo, long now)
    {
        Suppress();
        if (demo || !DeviceDialSettingsStore.ValidPort(settings.Port) || !DialProtocolSession.IdentityText(settings.DeviceId)) return;
        Target = settings; NextAttemptAt = now;
    }
    public void Suppress()
    {
        revision++; Target = null; failures = 0; present = null; NextAttemptAt = 0;
    }
    public void Connected(long now)
    {
        if (!Enabled) return;
        failures = 0; NextAttemptAt = now;
    }
    public async Task<bool> TryAsync(long now, bool portPresent, Func<DeviceDialSettings, bool> canConnect,
        Func<DeviceDialSettings, Func<bool>, Task> connect)
    {
        if (Target is not { } saved) return false;
        if (!portPresent) { present = false; return false; }
        if (present != true) { failures = 0; NextAttemptAt = now; }
        present = true;
        if (Attempting || now < NextAttemptAt || !canConnect(saved)) return false;
        var acceptedRevision = revision;
        bool Current() => revision == acceptedRevision && Target == saved && canConnect(saved);
        // Schedule before invoking the callback so synchronous and asynchronous failures both back off.
        var delay = failures < 4 ? 2000L << failures : 30000L;
        failures = Math.Min(failures + 1, 4); NextAttemptAt = now + delay;
        Attempting = true;
        try { await connect(saved, Current); return true; }
        finally { Attempting = false; }
    }
}
