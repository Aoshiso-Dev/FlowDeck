using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class MotherboardDeviceDriver
{
    public PhysicalDeviceBinding? CurrentControlBinding
    { get { lock (gate) return binding == BindingCandidate ? binding : null; } }

    public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature)
    {
        if (feature == DeviceFeature.Temperature) return sensors.AllowsCurrentConnectionControl(endpointId, feature);
        lock (gate) return (feature is DeviceFeature.FixedDuty or DeviceFeature.AutomaticDuty) && Ready(endpointId) is null;
    }

    public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        if (currentBinding != BindingCandidate) throw new IOException("マザーの現在接続が変更されています");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Never replace a running policy's safety or inherit a recovery obligation as a
            // new owner. The original identity and pending-journal checks remain in Ready.
            if (calibrating.Count != 0 || attempted.Count != 0 || journal?.Pending.Length > 0) return;
        }
        var entries = cooling.Where(c => c.Target.DeviceId == DeviceId &&
            Definition?.Ports.Any(p => p.Id == c.Target.EndpointId) == true).Select(c => c with
            {
                SavedPolicy = savedBinding == currentBinding ? c.SavedPolicy : null,
                ResumeOnStart = savedBinding == currentBinding && c.ResumeOnStart
            }).ToArray();
        // Minimum, fallback, stop behavior and release policy are actual settings. Nothing
        // is synthesized for a newly detected fan/pump; firmware keeps managing that port.
        Configure(currentBinding, entries);
    }
}
