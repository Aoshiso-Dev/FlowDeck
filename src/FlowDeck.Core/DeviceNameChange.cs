namespace FlowDeck.Core;

// Display metadata only. The stored device ID and endpoint identities remain unchanged.
public sealed record DeviceNameChange(string DeviceId, string Name)
{
    public const string Feature = "device-names-v2";
    public const int MaximumLength = 160;
    public static bool Valid(DeviceNameChange? value) => value is not null &&
        DeviceConfigurationRules.Key(value.DeviceId) && DeviceConfigurationRules.Text(value.Name, MaximumLength);
}
