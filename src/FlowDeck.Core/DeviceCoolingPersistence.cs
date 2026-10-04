namespace FlowDeck.Core;

public enum DevicePolicySaveState { Saved, Failed, Superseded, SessionOnly }
public sealed record DevicePolicySaveResult(DevicePolicySaveState State, string Message);

// Constructed by the authenticated runtime from its checked policy, never accepted as an IPC payload.
public sealed class CoolingPolicyChange
{
    public EndpointReference Target { get; }
    public long Order { get; }
    public CoolingSafety Safety { get; }
    public CoolingSavedPolicy? Policy { get; }
    public PhysicalDeviceBinding[] Bindings { get; }
    internal CoolingPolicyChange(EndpointReference target, long order, CoolingSafety safety, CoolingSavedPolicy? policy,
        PhysicalDeviceBinding[] bindings)
    { Target = target; Order = order; Safety = Contract.Copy(safety); Policy = policy is null ? null : Contract.Copy(policy); Bindings = Contract.Copy(bindings); }
}
public interface IDeviceCoolingPersistence
{
    CoolingEndpointConfiguration? SavedCoolingConfiguration(EndpointReference target);
    Task<string> BeginCoolingChangeAsync(CoolingPolicyChange change, CancellationToken ct);
    Task<DevicePolicySaveResult> FinishCoolingChangeAsync(string ticket, bool confirmed);
}
