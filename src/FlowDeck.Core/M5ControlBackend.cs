namespace FlowDeck.Core;

// These DTOs describe the existing wire envelope, not a saved-settings import.
public sealed record M5ControlView(AppSettings Settings, ControlSnapshot Snapshot, string[] ReadyProfiles);
public interface IM5ControlBackend
{
    string Epoch { get; }
    AppSettings Settings { get; }
    ControlSnapshot Snapshot { get; }
    string? ProfileReadiness(string id);
    Task<OperationResult> RequestAsync(ControlRequest request);
    Task<OperationResult> RequestFromM5Async(ControlRequest request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return RequestAsync(request); }
    M5ControlView ReadM5State()
    {
        var settings = Settings;
        return new(settings, Snapshot, settings.Profiles.Where(p => ProfileReadiness(p.Id) is null).Select(p => p.Id).ToArray());
    }
}
