using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Only the explicitly requested demo fixture uses these in-memory, non-actuating setup candidates.</summary>
internal sealed class DemoSetupProvider : IDeviceDriverProvider
{
    private sealed class PortDevice(int number) : IPhysicalDeviceDriver, IDeviceSetupDriver
    {
        public string DeviceId { get; } = "demo-setup-" + number;
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, DeviceId, "demo-setup", new('a', 64));
        public DeviceObservation Observe() => new(DeviceId, "demo-setup", "照明コントローラー", DeviceKind.Controller, IdentityConfidence.SerialBound,
            DeviceId, "demo-setup", [DeviceId], Enumerable.Range(1, 128).Select(i => new DeviceEndpoint("zone-" + i, "照明 " + i,
                EndpointKind.Lighting, [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.NeedsSetup)])).ToArray());
        public EndpointSetupOptions[] DescribeSetup() => Enumerable.Range(1, 128).Select(i => new EndpointSetupOptions("zone-" + i, [],
            [LightingExitAction.Off], 24, 24, 24, "DEMO RGB")).ToArray();
        public string? Validate(DeviceEndpointAction action) => "DEMO: 候補一覧の試験用端子です";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => Task.FromResult(new DeviceDriverResult(Outcome.Rejected, Validate(action)!));
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public string ProviderId => "demo-setup";
    public string[] ResourceDomains => ["demo-setup-1", "demo-setup-2", "demo-setup-3"];
    public IDeviceControlDriver[] CachedDrivers { get; } = Enumerable.Range(1, 3).Select(i => (IDeviceControlDriver)new PortDevice(i)).ToArray();
    public Task PollAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
