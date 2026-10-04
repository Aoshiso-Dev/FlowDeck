using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

[CollectionDefinition("Wall-clock transport", DisableParallelization = true)]
public sealed class TransportTimingCollection { }

// This class measures wall-clock packet cadence; CPU-heavy parallel tests are
// separate from the explicit injected-delay and stale-input tests.
[Collection("Wall-clock transport")]
public class HubLightingWorkerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task LatestReportsKeepThreePortAnimationMovingAcrossBackgroundMonitoringPolls()
    {
        using var dir = new ServiceTestDirectory(); var log = new TestLog(); var worker = Worker("fake_smooth_lighting_worker.py", log);
        var identity = Assert.Single(await worker.ProbeAsync());
        var driver = new HubDeviceDriver(Guid.NewGuid().ToString("N"), identity.Id, worker, dir.Path);
        await driver.InitializeAsync();
        Assert.True(worker.SupportsSmoothLightingFrames);
        Assert.Equal(33, driver.LightingLimits!.MinimumIntervalMilliseconds);
        var ports = new[] { "led1", "led2", "led3" };
        driver.ConfigureLighting(driver.BindingCandidate,
            ports.Select(p => new LightingEndpointConfiguration(new(driver.DeviceId, p), true, new(LightingExitAction.Off))),
            ports.Select(p => new RegisteredEndpoint(p, p, EndpointKind.Lighting, Setup: new("RGB", 24, true))));
        await using var runtime = new DeviceControlRuntime([driver], ["user"], log);
        var hello = runtime.OpenSession("user"); var state = runtime.State();
        var targets = state.Endpoints.Where(e => ports.Contains(e.Target.EndpointId))
            .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        Assert.True((await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test",
            DeviceOperation.SetLighting, state.Inventory.TopologyRevision, targets, Lighting: new("breathing", [new(255, 255, 255)], 2)))).Success);
        runtime.Start();
        await Task.Delay(3100);
        var lights = runtime.State().Endpoints.Where(e => ports.Contains(e.Target.EndpointId)).ToArray();
        if (lights.Any(e => e.Lighting is not { Running: true, Frames: >= 40, DroppedFrames: 0 }))
            lock (log.Entries) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(log.Entries.TakeLast(100)));
        Assert.All(lights, e => { Assert.True(e.Lighting!.Running); Assert.True(e.Lighting.Frames >= 40, $"Only {e.Lighting.Frames} frames"); Assert.Equal(0, e.Lighting.DroppedFrames); });
        Assert.Single(lights.Select(e => e.Lighting!.LastColor).Distinct());
        output.WriteLine($"Three-port frames in 3.1 s: {lights[0].Lighting!.Frames}; dropped: {lights[0].Lighting!.DroppedFrames}; max transport ms: {lights[0].Lighting!.MaximumWriteMilliseconds:F3}");
        await runtime.DisconnectAsync(hello.Session);
        Assert.Empty(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        Assert.All(runtime.State().Endpoints.Where(e => ports.Contains(e.Target.EndpointId)), e => Assert.False(e.Owned));
    }

    private static string Root
    {
        get
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "FlowDeck.sln"))) path = path.Parent;
            return path?.FullName ?? throw new DirectoryNotFoundException();
        }
    }
    private static HubWorkerClient Worker(string script = "fake_guarded_lighting_worker.py", IEventLog? log = null) => new(
        Path.Combine(Root, ".tools", "python313", "python.exe"), Path.Combine(Root, "tests", "worker", script), log ?? new TestLog(), () => false);
    private static readonly HubLightSetting Blue = new("fixed", [new(17, 34, 51)]);
    [Fact] public async Task IdentifiedF420AccessoriesRetainSetupGuardsAcrossTheWorkerProtocol()
    {
        using var dir = new ServiceTestDirectory();
        var worker = Worker("fake_accessory_worker.py");
        var identity = Assert.Single(await worker.ProbeAsync());
        await using var driver = new HubDeviceDriver(Guid.NewGuid().ToString("N"), identity.Id, worker, dir.Path);
        await driver.InitializeAsync();
        var options = driver.DescribeSetup();
        var reverse = Assert.Single(options, o => o.EndpointId == "led1");
        Assert.Equal("F420 RGB Reverse (2026) (0x3f)", reverse.Accessory);
        Assert.Equal(24, reverse.DetectedLeds);
        Assert.Equal("F420 RGB (2026) (0x38)", Assert.Single(options, o => o.EndpointId == "led2").Accessory);
        Assert.DoesNotContain(options, o => o.EndpointId == "led3"); // Full chain is 48, not clamped to 24.
        Assert.Null(Assert.Single(options, o => o.EndpointId == "led4").DetectedLeds);
        var action = new DeviceEndpointAction("led1", DeviceOperation.SetLighting, Lighting: new("fixed", [new(1, 2, 3)]));
        Assert.NotNull(driver.Validate(action)); // Identification never grants output ownership.
        var config = new LightingEndpointConfiguration(new(driver.DeviceId, "led1"), true, new(LightingExitAction.Off));
        foreach (var count in new[] { 1, 23, 27 })
        {
            driver.ConfigureLighting(driver.BindingCandidate, [config],
                [new("led1", "LED1", EndpointKind.Lighting, Setup: new(reverse.Accessory!, count, true))]);
            Assert.NotNull(driver.Validate(action));
        }
        driver.ConfigureLighting(driver.BindingCandidate, [config],
            [new("led1", "LED1", EndpointKind.Lighting, Setup: new(reverse.Accessory!, 24, true))]);
        Assert.Null(driver.Validate(action));
        driver.ConfigureLighting(driver.BindingCandidate, [config with { Target = new(driver.DeviceId, "led3") }],
            [new("led3", "LED3", EndpointKind.Lighting, Setup: new("F420 RGB (2026) (0x38), F420 RGB Reverse (2026) (0x3f)", 24, true))]);
        Assert.NotNull(driver.Validate(action with { EndpointId = "led3" }));
    }

    private static async Task<HubLightingExpectation> Open(HubWorkerClient worker)
    {
        var hub = Assert.Single(await worker.ProbeAsync());
        return new(hub.Id, ControlCoordinator.WiringSignature(await worker.OpenAsync(hub, 1)));
    }

    [Fact] public async Task NewLightingCommandUsesRealWorkerGuardsAndOnlySelectedPort()
    {
        await using var worker = Worker(); Assert.False(worker.SupportsLightingEndpoints);
        var expected = await Open(worker);
        Assert.True(worker.SupportsLightingEndpoints); Assert.True(worker.SupportsLightingDeadlines);
        var result = await worker.SetLightingEndpointsAsync(["led3"], Blue, expected);
        Assert.Equal(Outcome.WrittenUnverified, result.Outcome);
        var row = Assert.Single(result.Channels!); Assert.Equal("led3", row.Id); Assert.Equal("writtenUnverified", row.Confirmation);
        Assert.Equal(Outcome.Rejected, (await worker.SetLightingEndpointsAsync(["led1"], Blue, expected with { HubId = "replacement" })).Outcome);
        Assert.Equal("WIRING_CHANGED", (await worker.SetLightingEndpointsAsync(["led1"], Blue, expected with { WiringSignature = "old" })).Error);
        await worker.DisposeAsync(); Assert.False(worker.SupportsLightingEndpoints); Assert.False(worker.SupportsLightingDeadlines);
    }

    [Fact] public async Task LightingFrameUsesSameAbsoluteDeadlineInBothProcesses()
    {
        await using var worker = Worker(); var expected = await Open(worker);
        var valid = expected with { ValidUntilTick64 = HardwareWriteDeadline.Now + 2000 };
        Assert.Equal(Outcome.WrittenUnverified, (await worker.SetLightingEndpointsAsync(["led1", "led3"], Blue, valid)).Outcome);
        var expired = await worker.SetLightingEndpointsAsync(["led1"], Blue, expected with { ValidUntilTick64 = HardwareWriteDeadline.Now - 1 });
        Assert.Equal(Outcome.Rejected, expired.Outcome); Assert.Equal(HardwareWriteDeadline.LightingExpiredError, expired.Error);
    }

    [Fact] public async Task FanDeadlineFeatureDoesNotImplyLightingDeadlineFeature()
    {
        await using var worker = Worker("fake_guarded_lighting_no_deadline_worker.py"); var expected = await Open(worker);
        Assert.True(worker.SupportsLightingEndpoints); Assert.True(worker.SupportsFanDeadlines); Assert.False(worker.SupportsLightingDeadlines);
        await Assert.ThrowsAsync<NotSupportedException>(() => worker.SetLightingEndpointsAsync(["led1"], Blue,
            expected with { ValidUntilTick64 = HardwareWriteDeadline.Now + 1000 }));
        Assert.Equal(Outcome.WrittenUnverified, (await worker.SetLightingEndpointsAsync(["led1"], Blue, expected)).Outcome);
    }

    [Theory] [InlineData("fake_worker.py")] [InlineData("fake_endpoint_worker.py")]
    public async Task LegacyWorkersCannotReceiveNewLightingCommands(string script)
    {
        await using var worker = Worker(script); var expected = await Open(worker);
        Assert.False(worker.SupportsLightingEndpoints);
        await Assert.ThrowsAsync<NotSupportedException>(() => worker.SetLightingEndpointsAsync(["led1"], Blue, expected));
        Assert.Single(await worker.ProbeAsync());
    }

    [Fact] public async Task PartialHidFailureRetainsEachPortResultAcrossProtocol()
    {
        await using var worker = Worker(); var expected = await Open(worker);
        var result = await worker.SetLightingEndpointsAsync(["led1", "led2", "led3"], new("fixed", [new(254, 0, 0)]), expected);
        Assert.Equal(Outcome.Partial, result.Outcome);
        var rows = Assert.IsType<ChannelResult[]>(result.Channels);
        Assert.Equal(new[] { "completed", "unknown", "notExecuted" }, rows.Select(c => c.Write));
        Assert.Equal(new[] { "led1", "led2", "led3" }, rows.Select(c => c.Id));
    }

    [Theory]
    [InlineData("fake_guarded_lighting_worker.py", 2)]
    [InlineData("fake_guarded_lighting_slow_status_worker.py", 3)]
    public async Task CommonRuntimeAndHubDriverStreamThroughTheActualPythonProtocolAndReleaseAllSelectedPorts(string script, int portCount)
    {
        using var dir = new ServiceTestDirectory(); var worker = Worker(script); var identity = Assert.Single(await worker.ProbeAsync());
        var driver = new HubDeviceDriver(Guid.NewGuid().ToString("N"), identity.Id, worker, dir.Path);
        await driver.InitializeAsync();
        var ports = portCount == 2 ? new[] { "led1", "led3" } : new[] { "led1", "led2", "led3" };
        driver.ConfigureLighting(driver.BindingCandidate,
            ports.Select(p => new LightingEndpointConfiguration(new(driver.DeviceId, p), true, new(LightingExitAction.Off))),
            ports.Select(p => new RegisteredEndpoint(p, p, EndpointKind.Lighting, Setup: new("RGB", 24, true))));
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog()); var hello = runtime.OpenSession("user");
        var state = runtime.State();
        var targets = state.Endpoints.Where(e => ports.Contains(e.Target.EndpointId))
            .Select(e => new DeviceTargetVersion(e.Target, e.Generation, e.PolicyRevision)).ToArray();
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetLighting,
            state.Inventory.TopologyRevision, targets, Lighting: new("breathing", [new(40, 80, 200)], Brightness: 50));
        var reply = await runtime.ExecuteAsync(request); Assert.True(reply.Success); Assert.Equal(Outcome.WrittenUnverified, reply.Result!.Outcome);
        Assert.Equal(portCount, reply.Result.Targets.Length); Assert.All(reply.Result.Targets, r => Assert.Equal(new RgbColor(20, 40, 100), r.ReportedLighting!.Colors[0]));
        Assert.Equal(portCount, new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending.Length);
        await runtime.DisconnectAsync(hello.Session);
        Assert.Empty(new HubLightingRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        Assert.All(runtime.State().Endpoints.Where(e => ports.Contains(e.Target.EndpointId)), e =>
        { Assert.False(e.Owned); Assert.Equal(Outcome.WrittenUnverified, e.Outcome); Assert.Equal("off", e.ReportedLighting!.EffectId); });
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task MalformedLightingIsRejectedBeforeWorkerStartup(int invalid)
    {
        await using var worker = new HubWorkerClient("missing-python", "missing-worker", new TestLog(), () => false);
        string[] channels = invalid switch { 0 => [], 1 => ["sync"], 2 => ["led1", "led1"], _ => ["led1"] };
        var effect = invalid == 3 ? new HubLightSetting("fixed", [new(256, 0, 0)]) : Blue;
        var expected = invalid switch { 4 => new HubLightingExpectation("", "wire"),
            5 => new("hub", ""), 6 => new("hub", "wire", 0), _ => new("hub", "wire") };
        await Assert.ThrowsAsync<ArgumentException>(() => worker.SetLightingEndpointsAsync(channels, effect, expected));
    }
}
