namespace FlowDeck.Core;

// Both the local owner and the authenticated service proxy expose the same control contract.
public interface IControlCoordinator : IAsyncDisposable
{
    string Epoch { get; }
    AppSettings Settings { get; }
    ControlSnapshot Snapshot { get; }
    string? ProfileReadiness(string id);
    Task<OperationResult> RequestAsync(ControlRequest request);
    HubStatus LastStatus { get; }
    event Action<ControlSnapshot>? Changed;
    event Action<OperationResult>? OperationCompleted;
    event Action<AppSettings>? SettingsChanged;
    void Start();
    Task<HubIdentity[]> ProbeAsync();
    Task OpenAsync(HubIdentity identity, bool reconnect = false);
    void UpdateSettings(AppSettings next, bool controlConditions = true);
    Task UpdateSettingsAsync(AppSettings next, bool controlConditions = true) { UpdateSettings(next, controlConditions); return Task.CompletedTask; }
    AutoProfile SaveProfile(AutoProfile profile, long expectedProfileRevision);
    Task<AutoProfile> SaveProfileAsync(AutoProfile profile, long expectedProfileRevision) => Task.FromResult(SaveProfile(profile, expectedProfileRevision));
    Task TickAsync();
    Task<OperationResult> StopControlAsync();
    void Suspend();
    Task ResumeFromSleepAsync();
    Task RestoreStartupAsync();
    string? LightingTargetError(string hubId, string[] channels);
    LightingLease BeginLightingStream(string[] channels);
    Task<LightingLease> BeginLightingStreamAsync(string[] channels) => Task.FromResult(BeginLightingStream(channels));
    bool LightingLeaseValid(LightingLease lease);
    void EndLightingStream(LightingLease lease);
    Task EndLightingStreamAsync(LightingLease lease) { EndLightingStream(lease); return Task.CompletedTask; }
    Task<bool> SendLightingFrameAsync(LightingLease lease, Func<RgbColor> sample, Func<RgbColor, Task>? companion = null);
}

public interface IBoardFanController : IAsyncDisposable
{
    CpuFanState State { get; }
    Task ManualAsync(CpuFanSettings settings, int duty);
    Task AutoAsync(CpuFanSettings settings);
    Task TestAsync();
    Task TickAsync();
    Task RestoreAsync();
    Task SuspendAsync();
    void Resume();
    void Start();
}
public interface IBoardFanControllers : IAsyncDisposable
{
    IBoardFanController this[string id] { get; }
    IReadOnlyDictionary<string, CpuFanState> States { get; }
    void Start();
    void Resume();
    Task RestoreAsync();
    Task SuspendAsync();
}
