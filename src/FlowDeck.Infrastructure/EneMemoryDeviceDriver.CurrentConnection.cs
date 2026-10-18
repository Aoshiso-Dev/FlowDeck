using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed partial class EneMemoryDeviceDriver : IDeviceCurrentConnectionControlDriver
{
    private bool currentConnectionControl;
    // The packaged controller has an actual fixed layout. Saved counts and former
    // confirmation flags describe preferences, never evidence or current authority.
    private bool KnownCurrentModel() => connection is { } current && EneMemoryRecovery.ValidConnection(current) &&
        EneMemoryDefinition.Find(current.DefinitionId)?.Protocol.FixedLeds == 8;
    private LightingExitPolicy CurrentExit => configuration?.Exit ?? new(LightingExitAction.RestoreOriginal);
    public PhysicalDeviceBinding? CurrentControlBinding
    {
        get { lock (gate) return currentConnectionControl && binding is not null && KnownCurrentModel() ? Candidate() : null; }
    }
    public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature)
    {
        lock (gate)
        {
            if (!currentConnectionControl || endpointId != Endpoint) return false;
            if (feature == DeviceFeature.RestoreLighting)
            {
                // Ending a current operation must remain possible after a read error or
                // partial write. The journal itself still proves the exact old operation;
                // nothing here adopts a record belonging to another connection or boot.
                if (disposed || connection is not { } current || CurrentControlBinding is not { } actual || journalError is not null) return false;
                try { backend.Check(current); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { return false; }
                return journal?.Pending is not { } pending || EneMemoryRecovery.Check(pending, boot, actual, current) is null;
            }
            return (feature is DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames) && Ready() is null;
        }
    }
    public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        if (cooling.Any()) throw new ArgumentException("メモリ照明に冷却設定は適用できません");
        lock (gate)
            if (!KnownCurrentModel() || currentBinding != Candidate()) throw new IOException("メモリ照明の現在の接続情報が一致しません");
        // A saved binding is deliberately not promoted to physical identity. Every
        // write still captures this connection and creates/checks its exact journal scope.
        ConfigureLightingCore(currentBinding, lighting, endpoints, true);
    }
}
