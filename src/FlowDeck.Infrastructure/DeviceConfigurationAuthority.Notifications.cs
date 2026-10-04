using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    public DeviceNotificationConfiguration ReadNotificationConfiguration()
    {
        // configuration is published atomically only after a complete generation has been adopted.
        var current = configuration;
        return new(Contract.Copy(current.Preferences.Notifications), Contract.Copy(current.Devices));
    }
}
