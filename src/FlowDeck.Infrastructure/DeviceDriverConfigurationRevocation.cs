using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver : IDeviceConfigurationRevocation
{
    public void RevokeConfiguration()
    {
        lock (gate) { binding = null; cooling.Clear(); lightingBinding = null; lightingSetup.Clear(); }
    }
}

public sealed partial class MotherboardDeviceDriver : IDeviceConfigurationRevocation
{
    public void RevokeConfiguration() { lock (gate) { binding = null; cooling.Clear(); } }
}

internal sealed partial class AsrockDeviceDriver : IDeviceConfigurationRevocation
{
    public void RevokeConfiguration() { lock (gate) { binding = null; settings.Clear(); } }
}

internal sealed partial class EneMemoryDeviceDriver : IDeviceConfigurationRevocation
{
    public void RevokeConfiguration() { lock (gate) { connectionPermission?.Revoke(); binding = null; configuration = null; setup = null; } }
}

internal sealed partial class OpenRgbDeviceDriver : IDeviceConfigurationRevocation
{
    public void RevokeConfiguration() { lock (gate) { binding = null; settings.Clear(); } }
}
