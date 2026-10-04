using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceConfigurationSnapshotTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Ports(string id, int count) : IPhysicalDeviceDriver, IDeviceSetupDriver
    {
        public string DeviceId { get; } = id;
        public string Accessory = new('光', 80);
        public string Fingerprint = new('a', 64);
        public string NativeIdentity = id;
        public int Descriptions;
        public int Observations;
        public Action? Described;
        public PhysicalDeviceBinding BindingCandidate => new(DeviceId, NativeIdentity, "fixture", Fingerprint);
        public DeviceObservation Observe()
        {
            Observations++;
            return new(DeviceId, "fixture", "同名の機器", DeviceKind.Controller, IdentityConfidence.SerialBound,
            DeviceId, "fixture", [DeviceId], Enumerable.Range(0, count).Select(i => new DeviceEndpoint("led-" + i, "照明", EndpointKind.Lighting,
                [new(DeviceFeature.FixedColor, ImplementationSupport.Supported, VerificationEvidence.Automated, DeviceAvailability.NeedsSetup)])).ToArray());
        }
        public EndpointSetupOptions[] DescribeSetup()
        {
            Descriptions++; Described?.Invoke();
            return Enumerable.Range(0, count).Select(i => new EndpointSetupOptions("led-" + i, [], [LightingExitAction.Off], 1, 24, 24, Accessory)).ToArray();
        }
        public string? Validate(DeviceEndpointAction action) => "未設定";
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => throw new InvalidOperationException("No physical writes");
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig(int devices, int endpoints) : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly Ports[] Ports = Enumerable.Range(0, devices).Select(i => new Ports("device-" + i, endpoints)).ToArray();
        public readonly TestClock Clock = new();
        public DeviceControlRuntime Runtime = null!;
        public DeviceConfigurationAuthority Authority = null!;
        public DeviceServiceHello Hello = null!;
        private long sequence;
        public void Start()
        {
            var store = DeviceConfigurationCodec.Store(Path.Combine(Directory.Path, "settings"));
            store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(Installation))));
            Authority = new(store, Installation, store.ReadActive()!);
            Runtime = new(Ports, ["user"], new TestLog(), Clock); Runtime.InstallConfigurationHandler(Authority);
            Hello = Runtime.OpenSession("user");
        }
        public async Task<DeviceServiceReply> Send(DeviceConfigurationRequest request)
        {
            var reply = await Runtime.ExecuteAsync(new(2, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "PC", DeviceOperation.Configuration, Configuration: request));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json); Assert.True(bytes.Length < DeviceServiceProtocol.MaximumFrameBytes);
            return DeviceServiceProtocol.ReadReply(bytes, Hello);
        }
        public async Task<byte[]> Download(DeviceConfigurationReply head)
        {
            using var bytes = new MemoryStream();
            while (bytes.Length < head.TotalBytes)
            {
                var reply = await Send(new(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: (int)bytes.Length));
                Assert.True(reply.Success, reply.Error); var part = reply.Configuration!;
                Assert.Equal(head.Generation, part.Generation); Assert.Equal(head.TotalBytes, part.TotalBytes); Assert.Equal((int)bytes.Length, part.Offset);
                bytes.Write(part.Data!);
            }
            Assert.Equal(head.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()))); return bytes.ToArray();
        }
        public async ValueTask DisposeAsync() { if (Runtime is not null) await Runtime.DisposeAsync(); Directory.Dispose(); }
    }

    [Theory] [InlineData(4, 100)] [InlineData(32, 256)]
    public async Task AllSetupCandidatesSurviveAChunkedImmutableTransferWithoutRegistrationOrActuation(int devices, int endpoints)
    {
        await using var rig = new Rig(devices, endpoints); rig.Start();
        Assert.Contains(DeviceConfigurationProtocol.SnapshotFeature, rig.Hello.Features);
        var first = await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates)); Assert.True(first.Success, first.Error);
        var head = first.Configuration!; Assert.Null(head.Setup); Assert.Null(head.Candidates); Assert.True(head.TotalBytes > DeviceConfigurationProtocol.ChunkBytes);
        var originalTopology = rig.Runtime.State().Inventory.TopologyRevision; var descriptions = rig.Ports.Sum(p => p.Descriptions);
        var observations = rig.Ports.Sum(p => p.Observations);
        foreach (var port in rig.Ports) { port.Accessory = "変更後"; port.Fingerprint = new('b', 64); }
        var bytes = await rig.Download(head); Assert.Equal(descriptions, rig.Ports.Sum(p => p.Descriptions));
        Assert.Equal(observations, rig.Ports.Sum(p => p.Observations));
        var result = DeviceConfigurationSnapshotCodec.Deserialize(bytes, head.Generation);
        Assert.Equal(originalTopology, result.TopologyRevision); Assert.Equal(devices, result.Candidates.Length);
        Assert.All(result.Candidates, c => Assert.Equal(new string('a', 64), c.Fingerprint));
        Assert.Equal(devices * endpoints, result.Setup!.Length); Assert.Equal(result.Setup.Length, result.Setup.DistinctBy(c => c.Target).Count());
        Assert.All(result.Setup, c => Assert.Equal(new string('光', 80), c.Options.Accessory));
        Assert.Empty(result.Configuration.Devices.Devices); Assert.Empty(result.Configuration.Lighting.Endpoints ?? []);
        var old = await rig.Send(new(DeviceConfigurationOperation.Snapshot)); Assert.False(old.Success); Assert.Contains("分割取得", old.Error);
        var next = await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates)); Assert.True(next.Success, next.Error);
        Assert.False((await rig.Send(new(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: 0))).Success);
        result = DeviceConfigurationSnapshotCodec.Deserialize(await rig.Download(next.Configuration!), head.Generation);
        Assert.All(result.Setup!, c => Assert.Equal("変更後", c.Options.Accessory));
    }

    [Fact] public async Task SnapshotValidatesMetadataBoundsDuplicateKeysAndGenerationWithoutChangingUploadLimits()
    {
        await using var rig = new Rig(1, 2); rig.Start(); var head = (await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates))).Configuration!;
        var bytes = await rig.Download(head); var value = DeviceConfigurationSnapshotCodec.Deserialize(bytes, head.Generation);
        Assert.Throws<IOException>(() => DeviceConfigurationSnapshotCodec.Deserialize(bytes, new('f', 32)));
        Assert.Throws<IOException>(() => DeviceConfigurationSnapshotCodec.Serialize(value, 256));
        Assert.Throws<IOException>(() => DeviceConfigurationSnapshotCodec.Validate(value with { Setup = [value.Setup![0], value.Setup[0]] }));
        Assert.Throws<IOException>(() => DeviceConfigurationSnapshotCodec.Validate(value with { Setup = [value.Setup![0], value.Setup[1] with { Generation = 2 }] }));
        Assert.Throws<IOException>(() => DeviceConfigurationSnapshotCodec.Validate(value with { TopologyRevision = null }));
        var duplicate = Encoding.UTF8.GetBytes("{\"generation\":\"" + head.Generation + "\"," + Encoding.UTF8.GetString(bytes)[1..]);
        Assert.Throws<IOException>(() => DeviceConfigurationSnapshotCodec.Deserialize(duplicate, head.Generation));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: head.Generation,
            TotalBytes: DeviceConfigurationProtocol.MaximumBytes + 1, Sha256: new('a', 64))));
        Assert.True(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: DeviceConfigurationProtocol.MaximumBytes)));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.SnapshotWithCandidates, Data: [1])));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: DeviceConfigurationProtocol.MaximumSnapshotBytes)));
    }

    [Fact] public async Task LargeMetadataDownloadCanExceedUploadLimitWhileEveryFrameRemainsBounded()
    {
        await using var rig = new Rig(32, 256); rig.Start(); foreach (var port in rig.Ports) port.Accessory = new('光', 256);
        var reply = await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates)); Assert.True(reply.Success, reply.Error);
        Assert.True(reply.Configuration!.TotalBytes > DeviceConfigurationProtocol.MaximumBytes);
        var result = DeviceConfigurationSnapshotCodec.Deserialize(await rig.Download(reply.Configuration), reply.Configuration.Generation);
        Assert.Equal(8192, result.Setup!.Length);
    }

    [Fact] public async Task PreviousOwnerCannotContinueAConfigurationSnapshot()
    {
        await using var rig = new Rig(1, 2); rig.Start(); var head = (await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates))).Configuration!;
        await rig.Runtime.DisconnectAsync(rig.Hello.Session); rig.Hello = rig.Runtime.OpenSession("user");
        Assert.False((await rig.Send(new(DeviceConfigurationOperation.ReadSnapshot, head.TransferId, Offset: 0))).Success);
        Assert.True((await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates))).Success);
    }
    [Fact] public async Task LegacyMetadataOversizeIsRejectedEvenBelowThe256CandidateLimit()
    {
        await using var rig = new Rig(16, 16); rig.Start();
        foreach (var port in rig.Ports) { port.Accessory = new('光', 256); port.NativeIdentity = new string('体', 2040) + port.DeviceId[^2..]; }
        var old = await rig.Send(new(DeviceConfigurationOperation.Snapshot)); Assert.False(old.Success); Assert.Contains("分割取得", old.Error);
        var head = (await rig.Send(new(DeviceConfigurationOperation.SnapshotWithCandidates))).Configuration!;
        var result = DeviceConfigurationSnapshotCodec.Deserialize(await rig.Download(head), head.Generation);
        Assert.Equal(256, result.Setup!.Length); Assert.Equal(16, result.Candidates.Length);
    }

    private sealed class Handler(DeviceConfigurationAuthority authority, Func<DeviceConfigurationRequest, DeviceConfigurationReply, DeviceConfigurationReply>? transform = null) : IDeviceChunkedConfigurationSnapshotHandler
    {
        public async Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string id, string owner, DeviceControlRuntime runtime, CancellationToken ct)
        { var reply = await authority.ExecuteAsync(request, id, owner, runtime, ct); return transform?.Invoke(request, reply) ?? reply; }
    }
    private sealed class LegacyHandler(DeviceConfigurationAuthority authority) : IDeviceConfigurationHandler
    {
        public Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string id, string owner, DeviceControlRuntime runtime, CancellationToken ct)
        { Assert.NotEqual(DeviceConfigurationOperation.SnapshotWithCandidates, request.Operation); return authority.ExecuteAsync(request, id, owner, runtime, ct); }
    }
    private static async Task WithPipe(int endpoints, bool legacy, Func<DeviceConfigurationRequest, DeviceConfigurationReply, DeviceConfigurationReply>? transform,
        Func<DeviceWorkspaceClient, DeviceServicePipeClient, Ports, GenericTestDriver, DeviceControlRuntime, Task> run)
    {
        using var directory = new ServiceTestDirectory(); using var identity = WindowsIdentity.GetCurrent(); var user = identity.User!.Value;
        var ports = new Ports("ports", endpoints); var fan = new GenericTestDriver("fan");
        await using var runtime = new DeviceControlRuntime([ports, fan], [user], new TestLog(), demo: true);
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "settings"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(Installation))));
        var authority = new DeviceConfigurationAuthority(store, Installation, store.ReadActive()!);
        runtime.InstallConfigurationHandler(legacy ? new LegacyHandler(authority) : new Handler(authority, transform));
        var pipe = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DeviceServicePipeServer(runtime, [user], new TestLog(), pipe).RunAsync(stop.Token, () => ready.TrySetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); var transport = DeviceServicePipeClient.ForDemo(pipe, Environment.ProcessId);
        await using var client = new DeviceWorkspaceClient(transport);
        try { await client.ConnectAsync(); await run(client, transport, ports, fan, runtime); }
        finally { await client.DisposeAsync(); stop.Cancel(); try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    }

    [Fact] public async Task CancellingCandidateReadRetainsOldSnapshotAndActiveFanWithoutClosingPipe()
    {
        await WithPipe(256, false, null, async (client, transport, ports, fan, runtime) => {
            var snapshot = client.Snapshot!; var endpoint = snapshot.State.Endpoints.Single(e => e.Target.DeviceId == "fan");
            Assert.True((await transport.SendAsync(transport.Request(DeviceOperation.SetDuty, topology: snapshot.State.Inventory.TopologyRevision,
                targets: [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], duty: 55))).Success);
            using var stop = new CancellationTokenSource(); ports.Accessory = "after"; ports.Described = stop.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RefreshAsync(true, stop.Token)); ports.Described = null;
            Assert.True(transport.Connected); Assert.Equal(1, fan.Writes);
            Assert.Equal(snapshot.Configuration.Setup![0].Options.Accessory, client.Snapshot!.Configuration.Setup![0].Options.Accessory);
            Assert.True(runtime.State().Endpoints.Single(e => e.Target.DeviceId == "fan").Owned);
            await client.RefreshAsync(true); Assert.Equal("after", client.Snapshot!.Configuration.Setup![0].Options.Accessory);
        });
    }

    [Theory] [InlineData("offset")] [InlineData("generation")] [InlineData("transfer")] [InlineData("total")] [InlineData("short")] [InlineData("digest")]
    public async Task CorruptOrMixedChunksNeverPublishPartialConfiguration(string fault)
    {
        var enabled = false;
        await WithPipe(128, false, (request, reply) => {
            if (!enabled) return reply;
            if (fault == "digest" && request.Operation == DeviceConfigurationOperation.SnapshotWithCandidates) return reply with { Sha256 = new('f', 64) };
            if (request.Operation != DeviceConfigurationOperation.ReadSnapshot) return reply;
            return fault switch { "offset" => reply with { Offset = reply.Offset + 1 }, "generation" => reply with { Generation = new('f', 32) },
                "transfer" => reply with { TransferId = new('f', 32) }, "total" => reply with { TotalBytes = reply.TotalBytes + 1 },
                "short" => reply with { Data = reply.Data![..^1] }, _ => reply };
        }, async (client, transport, ports, fan, runtime) => {
            var old = client.Snapshot!.Configuration; ports.Accessory = "after"; enabled = true;
            await Assert.ThrowsAsync<IOException>(() => client.RefreshAsync(true));
            Assert.Equal(old.Setup![0].Options.Accessory, client.Snapshot!.Configuration.Setup![0].Options.Accessory);
            Assert.True(transport.Connected); Assert.Equal(0, fan.Writes);
            enabled = false; await client.RefreshAsync(true); Assert.Equal("after", client.Snapshot!.Configuration.Setup![0].Options.Accessory);
        });
    }

    [Fact] public async Task ClientUsesLegacySnapshotForAServiceWithoutCombinedSnapshotFeature()
    {
        await WithPipe(2, true, null, (client, transport, ports, fan, runtime) => {
            Assert.Null(client.Snapshot!.Configuration.TopologyRevision); Assert.Equal(2, client.Snapshot.Configuration.Setup!.Length);
            Assert.DoesNotContain(DeviceConfigurationProtocol.SnapshotFeature, transport.Hello!.Features); return Task.CompletedTask;
        });
    }
    [Fact] public async Task DemoWorkspaceExposesEveryCandidateButCreatesNoControlConfiguration()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, monitoringOnly: true, manySetupCandidates: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var snapshot = client.Snapshot!;
        Assert.Equal(396, snapshot.Configuration.Setup!.Length);
        var last = snapshot.Configuration.Setup.Last(); Assert.Equal("zone-128", last.Target.EndpointId);
        var draft = new DeviceEndpointSetupDraft(snapshot, last.Target); Assert.False(draft.Values.Confirmed);
        Assert.Empty(snapshot.Configuration.Configuration.Cooling.Endpoints); Assert.Empty(snapshot.Configuration.Configuration.Lighting.Endpoints ?? []);
        await client.RefreshAsync(true); draft.Update(client.Snapshot!); Assert.False(draft.Conflict);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
}
