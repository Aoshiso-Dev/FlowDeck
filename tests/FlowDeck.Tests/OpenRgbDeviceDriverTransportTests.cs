using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    private sealed class DetachedHardware : IDeviceHardwareExecution
    {
        public int Checks;
        public void CheckAuthorization() => Checks++;
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> hardware, CancellationToken ct) => hardware(ct);
    }
    private sealed class DetachedTransport(OpenRgbBoundDevice bound) : IOpenRgbLightingTransport
    {
        public bool NeedsRecovery => false;
        public bool HasPendingWrite => false;
        public DetachedHardware? Expected;
        public int DetachedChecks;
        public async Task SendAsync(OpenRgbSdkController controller, OpenRgbLightingStep step, OpenRgbLightingExitIntent? exit,
            Action authorize, Action beforeWrite, CancellationToken ct)
        {
            if (Expected is { } hardware)
            {
                Task probe;
                using (ExecutionContext.SuppressFlow()) probe = Task.Run(() =>
                {
                    var previous = hardware.Checks; authorize();
                    Assert.Equal(previous + 1, hardware.Checks); DetachedChecks++;
                });
                await probe;
            }
            await OpenRgbLightingPlan.SendStepAsync(bound.Connection, bound.Snapshot.Target, step, () => { authorize(); beforeWrite(); }, ct);
        }
    }
    [Theory] [InlineData("apply")] [InlineData("batch")] [InlineData("recover")] [InlineData("shared-stop")]
    public async Task NativePermissionCallbackRetainsItsHardwareScopeOutsideTheSubmittersExecutionContext(string action)
    {
        await using var r = new Rig(action == "shared-stop" ? SharedDevice() : Device());
        var transports = new List<DetachedTransport>(); DetachedHardware? expected = null;
        r.TransportFactory = bound => { var transport = new DetachedTransport(bound) { Expected = expected }; transports.Add(transport); return transport; };
        await r.Open(exit: LightingExitAction.Off); var driver = r.Driver();
        if (action == "recover") Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
        if (action == "shared-stop")
        {
            Written(await r.Command(DeviceOperation.SetLighting, [r.Target(r.Zone(0)), r.Target(r.Zone(1))], Animation));
            await r.Tick(.5);
        }
        var hardware = expected = new DetachedHardware();
        foreach (var transport in transports) transport.Expected = hardware;
        try
        {
            var result = await driver.RunPhasedOperationAsync(DevicePreparationKind.Command, hardware, async ct =>
            {
                if (action == "batch") return (await driver.ApplyLightingBatchAsync([new(r.Zone(0), Blue), new(r.Zone(1), Blue)], ct)).All(p => p.Result.Outcome == Outcome.WrittenUnverified);
                if (action == "recover") return (await driver.ReleaseAsync(DeviceReleaseReason.ServiceStopping, ct)).All(p => p.Result.Outcome is Outcome.WrittenUnverified or Outcome.Confirmed);
                var applied = await driver.ApplyAsync(new(r.Zone(0),
                    action == "shared-stop" ? DeviceOperation.StopLighting : DeviceOperation.SetLighting, Lighting: Blue), ct);
                return applied.Outcome == Outcome.WrittenUnverified;
            }, default);
            var detachedChecks = transports.Sum(t => t.DetachedChecks);
            Assert.True(result); Assert.True(detachedChecks > 0); Assert.True(hardware.Checks >= detachedChecks);
        }
        finally { expected = null; foreach (var transport in transports) transport.Expected = null; }
    }
    private sealed class ProbeTransport(OpenRgbBoundDevice bound) : IOpenRgbLightingTransport
    {
        public bool NeedsRecovery => false;
        public bool HasPendingWrite => false;
        public List<OpenRgbLightingExitIntent?> Intents = [];
        public Task SendAsync(OpenRgbSdkController controller, OpenRgbLightingStep step, OpenRgbLightingExitIntent? exit,
            Action authorize, Action beforeWrite, CancellationToken ct)
        {
            Assert.Equal(bound.Snapshot.Target.ControllerId, controller.Id);
            Intents.Add(exit is null ? null : Contract.Copy(exit));
            return OpenRgbLightingPlan.SendStepAsync(bound.Connection, bound.Snapshot.Target, step, () => { authorize(); beforeWrite(); }, ct);
        }
    }
    [Fact] public async Task ProviderAndDriverUseTheCapturedTransportAndPreserveOffDuringSharedShutdown()
    {
        await using var r = new Rig(SharedDevice()); var transports = new List<ProbeTransport>();
        r.TransportFactory = device => { var transport = new ProbeTransport(device); transports.Add(transport); return transport; };
        await r.Open(exit: LightingExitAction.Off);
        var a = r.Target(r.Zone(0)); var b = r.Target(r.Zone(1));
        Written(await r.Command(DeviceOperation.SetLighting, [a, b], Animation)); await r.Tick(.5);
        var transport = Assert.Single(transports);
        Assert.Contains(transport.Intents, i => i?.Zones is { Count: 2 } zones && zones.Values.All(p => p.Action == LightingExitAction.Off));
        Assert.True((await r.Command(DeviceOperation.StopLighting, [a])).Success);
        Assert.Contains(transport.Intents, i => i?.Zones is { Count: 1 } zones && zones.TryGetValue(0, out var p) &&
            p.Action == LightingExitAction.FixedColor && p.Color == new RgbColor(100, 50, 20));
        var ended = await r.Driver().ReleaseAsync(DeviceReleaseReason.ServiceStopping, default);
        Assert.All(ended, e => Assert.Equal(Outcome.WrittenUnverified, e.Result.Outcome));
        var saved = new Dictionary<int, LightingExitPolicy>();
        foreach (var intent in transport.Intents)
            if (intent?.Zones is { } changes) foreach (var (index, policy) in changes) saved[index] = policy;
        Assert.Equal(LightingExitAction.FixedColor, saved[0].Action); Assert.Equal(LightingExitAction.Off, saved[1].Action);
        Assert.Contains(transport.Intents, i => i is null); Assert.Single(transports);
    }
    [Fact] public async Task FailedCompiledTransportBindingCannotFallBackToTheGenericSdkWriter()
    {
        await using var r = new Rig(); r.TransportFactory = _ => throw new IOException("captured controller changed");
        await r.Open();
        var reply = await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue);
        Assert.Equal(Outcome.Unknown, reply.Result!.Outcome);
        Assert.Equal(0, r.Server.WriteCount);
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command is 1050 or 1051 or 1101 or 1103);
    }
}
