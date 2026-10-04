namespace FlowDeck.Infrastructure;

// Implementations are compiled service policy, never supplied by a client or deserialized from settings.
// SDK observations cannot prove disabled bus probing, process identity, or exclusive ownership.
// The concrete policy must separately establish which local providers may discover/poll hardware.
internal interface IOpenRgbRoutePolicy
{
    void CheckLocalRoute();
    void CheckEnvironment(OpenRgbSdkEnvironment environment);
    void CheckInventory(OpenRgbSdkController[] controllers);
}
