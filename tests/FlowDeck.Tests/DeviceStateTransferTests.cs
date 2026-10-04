using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceStateTransferTests
{
    private sealed class Sensors(string id, int count, TimeProvider clock) : IDeviceControlDriver, IDeviceTelemetrySource
    {
        public string DeviceId { get; } = id;
        public int Observations;
        public double Value = 42;
        public string Locator = id;
        public Action? Sampled;
        private readonly DeviceEndpoint[] endpoints = Enumerable.Range(0, count).Select(i => new DeviceEndpoint("sensor-" + i,
            new('温', 80), EndpointKind.Sensor, [new(DeviceFeature.Temperature, ImplementationSupport.Supported,
                VerificationEvidence.Automated, DeviceAvailability.Ready)])).ToArray();
        public DeviceObservation Observe()
        {
            Interlocked.Increment(ref Observations);
            return new(DeviceId, "test-sensors", "機器 " + DeviceId, DeviceKind.Processor, IdentityConfidence.SerialBound, Locator, "test-v1", [DeviceId], endpoints);
        }
        public DeviceSample[] ReadCachedSamples()
        {
            Sampled?.Invoke();
            return endpoints.Select(e => new DeviceSample(e.Id, DeviceFeature.Temperature, Value, clock.GetTimestamp())).ToArray();
        }
        public string? Validate(DeviceEndpointAction action) => "監視専用";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException("No sensor writes");
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig(int devices = 8, int endpoints = 128) : IAsyncDisposable
    {
        public readonly TestClock Clock = new();
        public Sensors[] Sensors = [];
        public DeviceControlRuntime Runtime = null!;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public void Start()
        {
            Sensors = Enumerable.Range(0, devices).Select(i => new Sensors("device-" + i, endpoints, Clock)).ToArray();
            Runtime = new(Sensors, ["user"], new TestLog(), Clock); Hello = Runtime.OpenSession("user");
        }
        public DeviceServiceRequest Request(DeviceOperation operation = DeviceOperation.ReadStateSnapshot, DeviceStateQuery? query = null) =>
            new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "PC", operation,
                StateQuery: operation == DeviceOperation.ReadStateSnapshot ? query ?? new() : null);
        public async ValueTask DisposeAsync() { if (Runtime is not null) await Runtime.DisposeAsync(); }
    }
    private static DeviceStateChunk Decode(DeviceServiceReply reply, DeviceServiceHello hello)
    {
        Assert.True(reply.Success, reply.Error); var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json);
        Assert.True(bytes.Length < DeviceServiceProtocol.MaximumFrameBytes);
        return Assert.IsType<DeviceStateChunk>(DeviceServiceProtocol.ReadReply(bytes, hello).StateChunk);
    }
    [Theory] [InlineData(8, 128)] [InlineData(32, 256)]
    public async Task CompleteInventoryExceedsOneFrameButEveryCandidateSurvivesOneImmutableSnapshot(int devices, int endpoints)
    {
        await using var rig = new Rig(devices, endpoints); rig.Start();
        var direct = JsonSerializer.SerializeToUtf8Bytes(new DeviceServiceReply(2, rig.Hello.Epoch, rig.Hello.Session,
            Guid.NewGuid().ToString("N"), true, State: rig.Runtime.State()), Contract.Json);
        Assert.True(direct.Length > DeviceServiceProtocol.MaximumFrameBytes);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(direct, rig.Hello));
        var legacy = await rig.Runtime.ExecuteAsync(rig.Request(DeviceOperation.State));
        Assert.False(legacy.Success); Assert.Contains("分割取得", legacy.Error);
        var request = rig.Request(); var first = Decode(await rig.Runtime.ExecuteAsync(request), rig.Hello);
        using var accumulator = new DeviceStateAccumulator(rig.Hello, request.Id); accumulator.Append(first);
        Assert.False(accumulator.IsComplete);
        foreach (var sensor in rig.Sensors) { sensor.Value = 95; sensor.Locator += "changed"; }
        _ = rig.Runtime.State(); var observations = rig.Sensors.Sum(s => s.Observations);
        var chunks = 1;
        while (!accumulator.IsComplete)
        {
            var next = await rig.Runtime.ExecuteAsync(rig.Request(query: new(accumulator.TransferId, accumulator.Offset)));
            accumulator.Append(Decode(next, rig.Hello)); chunks++;
        }
        Assert.Equal(observations, rig.Sensors.Sum(s => s.Observations));
        var state = accumulator.Finish(); Assert.True(chunks > 1);
        Assert.Equal(devices, state.Inventory.Devices.Length);
        Assert.Equal(devices * endpoints, state.Inventory.Devices.Sum(d => d.Device.Endpoints.Length));
        Assert.Equal(devices * endpoints, state.Telemetry!.Length); Assert.All(state.Telemetry, t => Assert.Equal(42, t.Value));
        Assert.All(state.Inventory.Devices, d => Assert.DoesNotContain("changed", d.Device.Locator));
    }
    [Fact]
    public async Task ExpiredSupersededOrPreviousOwnerTransfersCannotBeContinued()
    {
        await using var rig = new Rig(); rig.Start();
        var firstRequest = rig.Request(); var first = Decode(await rig.Runtime.ExecuteAsync(firstRequest), rig.Hello);
        var duplicate = Decode(await rig.Runtime.ExecuteAsync(firstRequest), rig.Hello); Assert.Equal(first.TransferId, duplicate.TransferId);
        Assert.False((await rig.Runtime.ExecuteAsync(firstRequest with { StateQuery = new(first.TransferId, DeviceStateTransfer.ChunkBytes) })).Success);
        _ = Decode(await rig.Runtime.ExecuteAsync(rig.Request()), rig.Hello);
        Assert.False((await rig.Runtime.ExecuteAsync(rig.Request(query: new(first.TransferId, DeviceStateTransfer.ChunkBytes)))).Success);
        var latest = Decode(await rig.Runtime.ExecuteAsync(rig.Request()), rig.Hello);
        rig.Clock.Advance(DeviceStateTransfer.LifetimeSeconds);
        Assert.False((await rig.Runtime.ExecuteAsync(rig.Request(query: new(latest.TransferId, DeviceStateTransfer.ChunkBytes)))).Success);
        latest = Decode(await rig.Runtime.ExecuteAsync(rig.Request()), rig.Hello);
        Assert.True((await rig.Runtime.ExecuteAsync(rig.Request(DeviceOperation.ReleaseSession))).Success);
        rig.Hello = rig.Runtime.OpenSession("user");
        Assert.False((await rig.Runtime.ExecuteAsync(rig.Request(query: new(latest.TransferId, DeviceStateTransfer.ChunkBytes)))).Success);
        Assert.True((await rig.Runtime.ExecuteAsync(rig.Request())).Success);
    }
    [Theory] [InlineData("offset")] [InlineData("transfer")] [InlineData("size")] [InlineData("digest")]
    public async Task AccumulatorRejectsMixedOrOutOfOrderChunksWithoutPublishingPartialState(string change)
    {
        await using var rig = new Rig(); rig.Start(); var request = rig.Request();
        var first = Decode(await rig.Runtime.ExecuteAsync(request), rig.Hello);
        var next = Decode(await rig.Runtime.ExecuteAsync(rig.Request(query: new(first.TransferId, first.Data.Length))), rig.Hello);
        using var accumulator = new DeviceStateAccumulator(rig.Hello, request.Id); accumulator.Append(first);
        Assert.Throws<IOException>(() => accumulator.Finish());
        var invalid = change switch {
            "offset" => next with { Offset = 0 }, "transfer" => next with { TransferId = Guid.NewGuid().ToString("N") },
            "size" => next with { TotalBytes = next.TotalBytes + 1 }, _ => next with { Sha256 = new('0', 64) }
        };
        Assert.Throws<IOException>(() => accumulator.Append(invalid)); Assert.Equal(first.Data.Length, accumulator.Offset);
    }
    [Theory] [InlineData("corruption")] [InlineData("epoch")] [InlineData("request")]
    public void CompletedSnapshotsStillRequireDigestOwnerAndRequestIdentity(string change)
    {
        var id = Guid.NewGuid().ToString("N"); var hello = new DeviceServiceHello(2, id, id, 12, []);
        var reply = new DeviceServiceReply(2, id, id, id, true, State: new(new(id, 0, []), [], false, null));
        if (change == "epoch") reply = reply with { Epoch = Guid.NewGuid().ToString("N") };
        if (change == "request") reply = reply with { Id = Guid.NewGuid().ToString("N") };
        var data = DeviceStateTransfer.Encode(reply); var digest = Convert.ToHexStringLower(SHA256.HashData(data));
        if (change == "corruption") data[^1] ^= 1;
        using var accumulator = new DeviceStateAccumulator(hello, id);
        accumulator.Append(new(id, data.Length, digest, 0, data)); Assert.True(accumulator.IsComplete);
        Assert.Throws<IOException>(() => accumulator.Finish());
    }
    [Fact]
    public void QueryAndSnapshotSizesRemainBoundedAndCannotBeUsedOnWriteOperations()
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new DeviceServiceRequest(2, id, id, id, 1, "PC", DeviceOperation.ReadStateSnapshot, StateQuery: new());
        Assert.NotNull(DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json)));
        foreach (var invalid in new[] { request with { StateQuery = null }, request with { StateQuery = new(null, DeviceStateTransfer.ChunkBytes) },
            request with { StateQuery = new(id, 1) }, request with { StateQuery = new(id, DeviceStateTransfer.MaximumBytes) },
            request with { Operation = DeviceOperation.Renew } })
            Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(invalid, Contract.Json)));
        var reply = new DeviceServiceReply(2, id, id, id, true, State: new(new(id, 0, []), [], false, new('x', DeviceStateTransfer.MaximumBytes)));
        Assert.Throws<IOException>(() => DeviceStateTransfer.Encode(reply));
        var hello = new DeviceServiceHello(2, id, id, 12, Enumerable.Range(0, 64).Select(i => "feature-" + i).ToArray());
        Assert.Equal(64, DeviceServiceProtocol.ReadHello(JsonSerializer.SerializeToUtf8Bytes(hello, Contract.Json)).Features.Length);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadHello(JsonSerializer.SerializeToUtf8Bytes(hello with { Features = [.. hello.Features, "overflow"] }, Contract.Json)));
    }
    [Fact]
    public async Task AuthenticatedWorkspaceReadsLargeCatalogAndRetainsCoolingWhenAnOlderReadIsRejected()
    {
        using var directory = new ServiceTestDirectory(); using var identity = WindowsIdentity.GetCurrent(); var user = identity.User!.Value;
        var sensors = Enumerable.Range(0, 8).Select(i => new Sensors("device-" + i, 128, TimeProvider.System)).ToArray();
        var fan = new GenericTestDriver("fan");
        await using var runtime = new DeviceControlRuntime([.. sensors, fan], [user], new TestLog(), demo: true);
        var installation = Guid.NewGuid().ToString("N"); var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "settings"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(installation))));
        runtime.InstallConfigurationHandler(new DeviceConfigurationAuthority(store, installation, store.ReadActive()!));
        var pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DeviceServicePipeServer(runtime, [user], new TestLog(), pipe).RunAsync(stop.Token, () => ready.TrySetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); var transport = DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId);
        await using var client = new DeviceWorkspaceClient(transport);
        try
        {
            await client.ConnectAsync(); var initial = client.Snapshot!;
            Assert.Equal(9, initial.State.Inventory.Devices.Length); Assert.Equal(1024, initial.State.Telemetry!.Length);
            var endpoint = initial.State.Endpoints.Single(e => e.Target.DeviceId == "fan");
            var set = transport.Request(DeviceOperation.SetDuty, topology: initial.State.Inventory.TopologyRevision,
                targets: [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], duty: 55);
            Assert.True((await transport.SendAsync(set)).Success); Assert.Equal(1, fan.Writes);
            var originalRevision = client.Snapshot!.State.Inventory.Revision;
            using var cancelled = new CancellationTokenSource(); sensors[0].Sampled = () => cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RefreshAsync(ct: cancelled.Token));
            sensors[0].Sampled = null;
            Assert.True(transport.Connected); Assert.Equal(originalRevision, client.Snapshot!.State.Inventory.Revision);
            Assert.Equal(1, fan.Writes); await client.RefreshAsync();
            var feature = Array.IndexOf(transport.Hello!.Features, DeviceStateTransfer.Feature);
            transport.Hello.Features[feature] = "unsupported-state-snapshot-test";
            var failure = await Assert.ThrowsAsync<IOException>(() => client.RefreshAsync()); Assert.Contains("分割取得", failure.Message);
            Assert.True(transport.Connected); Assert.Equal(1, fan.Writes); Assert.True(runtime.State().Endpoints.Single(e => e.Target.DeviceId == "fan").Owned);
            transport.Hello.Features[feature] = DeviceStateTransfer.Feature; await client.RefreshAsync();
            Assert.True(client.Snapshot!.Connected); Assert.Equal(1024, client.Snapshot.State.Telemetry!.Length);
            Assert.True((await client.ReleaseAsync()).Success);
        }
        finally
        {
            await client.DisposeAsync(); stop.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }
}
