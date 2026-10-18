using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private readonly Dictionary<EndpointReference, string> unresolvedEndpointWarnings = [];
    private string unresolvedLifecycleWarning = "";
    public string PersistentWarning { get; private set; } = "";

    private void RenderPersistentWarnings(DeviceWorkspaceSnapshot snapshot)
    {
        var fresh = snapshot.Connected && snapshot.Error is null &&
            double.IsFinite(snapshot.ElapsedSeconds) && snapshot.ElapsedSeconds is >= 0 and < 3 &&
            !snapshot.State.Releasing && !snapshot.State.ConfigurationChanging;
        var confirmed = new HashSet<EndpointReference>();
        if (fresh)
        {
            unresolvedLifecycleWarning = snapshot.State.LifecycleError ?? "";
            foreach (var instance in snapshot.State.Inventory.Devices.Where(d => d.Connected))
            foreach (var endpoint in instance.Device.Endpoints.Where(e => e.Kind is EndpointKind.Cooling or EndpointKind.Lighting))
            {
                var target = new EndpointReference(instance.Device.Id, endpoint.Id);
                var execution = snapshot.State.Endpoints.SingleOrDefault(e => e.Target == target && e.Generation == instance.Generation);
                var reasons = new List<string>();
                if (execution?.Cooling is { Faulted: true } cooling)
                    reasons.Add(string.IsNullOrWhiteSpace(cooling.Fault) ? "冷却制御異常" : cooling.Fault);
                if (!string.IsNullOrWhiteSpace(execution?.Lighting?.Fault)) reasons.Add(execution.Lighting.Fault);
                if (endpoint.Kind == EndpointKind.Lighting)
                {
                    var failed = endpoint.Capabilities.FirstOrDefault(c => c.Support == ImplementationSupport.Supported &&
                        c.Availability == DeviceAvailability.Failed &&
                        c.Feature is DeviceFeature.FixedColor or DeviceFeature.NativeEffect or DeviceFeature.ColorFrames);
                    if (failed is not null)
                        reasons.Add(string.IsNullOrWhiteSpace(failed.Reason) ? "照明の終了・復旧状態は未確認" : failed.Reason);
                }
                // A rejected trial with no original reading is not evidence of an
                // output change. Completed reports retain ownership until recovery.
                if (execution is { Owned: true, Calibration: { Restored: false, Before: not null } trial } &&
                    execution.Outcome != Outcome.Pending)
                    reasons.Add("校正後の復元未確認" + (string.IsNullOrWhiteSpace(trial.Message) ? "" : ": " + trial.Message));

                if (reasons.Count > 0)
                {
                    var registration = snapshot.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId);
                    var name = registration?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId)?.Name ?? endpoint.Name;
                    var message = (registration?.Name ?? instance.Device.Name) + " / " + name + ": " + string.Join(" / ", reasons.Distinct());
                    if (!unresolvedEndpointWarnings.ContainsKey(target) && unresolvedEndpointWarnings.Count >= DeviceServiceProtocol.MaximumTargets)
                        unresolvedEndpointWarnings.Remove(unresolvedEndpointWarnings.Keys.First());
                    unresolvedEndpointWarnings[target] = message;
                    confirmed.Add(target);
                }
                else if (execution is not null && !(execution.Owned && execution.Outcome is Outcome.Unknown or Outcome.Pending) &&
                    !endpoint.Capabilities.Any(c => c.Support == ImplementationSupport.Supported &&
                    c.Availability is DeviceAvailability.Disconnected or DeviceAvailability.TemporarilyUnavailable))
                {
                    // Absence, an obsolete generation and unreadable device state
                    // cannot establish that a previously reported fault cleared.
                    // A new generation can retain an old obligation while clearing
                    // its fault fields; unknown or pending ownership is not recovery.
                    unresolvedEndpointWarnings.Remove(target);
                }
            }
        }

        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(unresolvedLifecycleWarning)) messages.Add(unresolvedLifecycleWarning);
        foreach (var warning in unresolvedEndpointWarnings)
            messages.Add(fresh && !confirmed.Contains(warning.Key) ? "状態未取得・最後に確認: " + warning.Value : warning.Value);
        var text = string.Join("\n", messages);
        if (!fresh && messages.Count > 0)
        {
            var state = !snapshot.Connected ? "サービス未接続" : snapshot.State.Releasing || snapshot.State.ConfigurationChanging ?
                "状態更新中" : "状態取得停止";
            text = state + " · 解消未確認\n最終確認:\n" + text;
        }
        if (PersistentWarning == text) return;
        PersistentWarning = text;
        Notify(nameof(PersistentWarning));
        PublishFeedback();
    }
}
