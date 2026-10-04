using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceTelemetryTests
{
    private sealed class PollingDriver(string id, TestClock clock) : IDevicePollingDriver, IDeviceTelemetrySource
    {
        private readonly GenericTestDriver inner = new(id);
        public string DeviceId => id;
        public int Polls, Releases;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Continue;
        public DeviceSample[]? Samples;
        public DeviceObservation? Observe()
        {
            var device = inner.Observe()!; var endpoint = device.Endpoints[0];
            return device with { Endpoints = [endpoint with { Capabilities = [.. endpoint.Capabilities,
                new(DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.Ready)] }] };
        }
        public string? Validate(DeviceEndpointAction action) => inner.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => inner.ApplyAsync(action, ct);
        public async Task PollAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Polls); Entered.TrySetResult(); if (Continue is not null) await Continue.Task;
            Samples ??= [new("fan1", DeviceFeature.Rpm, 900, clock.GetTimestamp())];
        }
        public DeviceSample[] ReadCachedSamples() => Samples ?? [];
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct)
        { Interlocked.Increment(ref Releases); return inner.ReleaseAsync(reason, ct); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact] public async Task HubPollingProducesEndpointTelemetryAndDistinguishesMissingStaleAndZeroRpm()
    {
        using var directory = new ServiceTestDirectory(); var clock = new TestClock(); var hub = new DemoHub { DelayMs = 0 };
        var driver = new HubDeviceDriver("0123456789abcdef0123456789abcdef", DemoHub.Identity.Id, hub, directory.Path, clock);
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), clock);
        Assert.Empty(runtime.State().Telemetry!); await runtime.PollAsync();
        var first = runtime.State(); Assert.Equal(10, first.Telemetry!.Length); Assert.All(first.Telemetry, t => Assert.False(t.Stale));
        Assert.Equal(0, hub.Writes); clock.Advance(4);
        Assert.All(runtime.State().Telemetry!, t => { Assert.True(t.Stale); Assert.Equal(4, t.AgeSeconds); });
        await hub.SetFanGroupAsync(["fan1"], 0); await runtime.PollAsync();
        var zero = runtime.State().Telemetry!.Single(t => t.Target.EndpointId == "fan1" && t.Feature == DeviceFeature.Rpm);
        Assert.False(zero.Stale); Assert.Equal(0, zero.Value);
        hub.Disconnected = true; await runtime.PollAsync();
        Assert.All(runtime.State().Telemetry!, t => Assert.True(t.Stale));
    }
    [Fact] public async Task SlowPollingDoesNotQueueDuplicatesBlockIndependentDevicesOrStopLeaseExpiration()
    {
        var clock = new TestClock(); var a = new PollingDriver("a", clock) { Continue = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var b = new PollingDriver("b", clock); await using var runtime = new DeviceControlRuntime([a, b], ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); var first = runtime.PollAsync();
        await Task.WhenAll(a.Entered.Task, b.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5)); var second = runtime.PollAsync();
        Assert.Equal(1, a.Polls); runtime.Start(); clock.Advance(13);
        await Task.Run(async () => { while (Volatile.Read(ref b.Releases) == 0) await Task.Delay(20); }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, a.Releases); Assert.True(runtime.State().Releasing);
        a.Continue.TrySetResult(); await Task.WhenAll(first, second, runtime.SweepAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, a.Releases); Assert.Null(runtime.State().LifecycleError);
    }
    [Fact] public async Task InvalidOrDuplicateSamplesCannotBecomeAValidZeroReading()
    {
        var clock = new TestClock(); var driver = new PollingDriver("a", clock); await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), clock);
        driver.Samples = [new("fan1", DeviceFeature.Rpm, double.NaN, clock.GetTimestamp())];
        var invalid = Assert.Single(runtime.State().Telemetry!); Assert.True(invalid.Stale); Assert.Null(invalid.Value);
        driver.Samples = [new("fan1", DeviceFeature.Rpm, 0, clock.GetTimestamp()), new("fan1", DeviceFeature.Rpm, 900, clock.GetTimestamp())];
        var duplicate = Assert.Single(runtime.State().Telemetry!); Assert.True(duplicate.Stale); Assert.Null(duplicate.Value);
    }
    [Fact] public async Task GenericTelemetrySurvivesTheVersionTwoContractAndRejectsImpossibleFreshness()
    {
        var clock = new TestClock(); var driver = new PollingDriver("a", clock); await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); await runtime.PollAsync();
        var reply = new DeviceServiceReply(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), true, State: runtime.State());
        Assert.Equal(900, Assert.Single(DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json), hello).State!.Telemetry!).Value);
        var sample = Assert.Single(reply.State!.Telemetry!);
        var bad = reply with { State = reply.State with { Telemetry = [sample with { AgeSeconds = null, Stale = false }] } };
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(bad, Contract.Json), hello));
    }
}
