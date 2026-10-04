using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority : IDeviceDiagnosticConfiguration
{
    public async Task<DeviceDiagnosticConfigurationCapture> ReadDiagnosticsAsync(CancellationToken ct)
    {
        await access.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var c = configuration;
            return new(generation.Id, new(c.Devices.Devices.Length, c.Devices.Devices.Sum(d => d.Endpoints.Length), c.Bindings.Devices.Length,
                c.Dashboard.Items.Length, c.Lighting.Scenes.Length, c.Lighting.Groups.Length, c.Cooling.Groups.Length,
                c.Preferences.Notifications.Enabled, c.Preferences.Providers?.OpenRgbEnabled == true, c.Preferences.M5ConnectOnStart,
                c.Cooling.Endpoints.Select(e => new DeviceDiagnosticCoolingSetting(e.Target, e.WiringConfirmed, e.Safety.Minimum, e.Safety.Fallback,
                    e.Safety.Exit, e.Safety.ExitAction, e.Profiles.Length, e.ResumeOnStart)).ToArray()));
        }
        finally { access.Release(); }
    }
}
