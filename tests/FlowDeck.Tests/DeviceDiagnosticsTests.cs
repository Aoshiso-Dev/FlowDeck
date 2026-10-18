using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceDiagnosticsTests
{
    private static async Task<DeviceDiagnosticReport> Read(GenericRig rig, DeviceDiagnosticScope scope = DeviceDiagnosticScope.All)
    {
        DeviceDiagnosticChunk? first = null; using var bytes = new MemoryStream();
        do
        {
            var reply = await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReadDiagnostics) with { Diagnostics = new(scope, first?.TransferId, (int)bytes.Length) });
            Assert.True(reply.Success, reply.Error); var chunk = Assert.IsType<DeviceDiagnosticChunk>(reply.Diagnostics);
            Assert.True(DeviceDiagnostics.Valid(chunk)); first ??= chunk;
            Assert.Equal(first.TransferId, chunk.TransferId); Assert.Equal(first.Sha256, chunk.Sha256); bytes.Write(chunk.Data);
        } while (bytes.Length < first.TotalBytes);
        Assert.Equal(first.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        return JsonSerializer.Deserialize<DeviceDiagnosticReport>(bytes.ToArray(), Contract.Json)!;
    }
    [Fact] public async Task ExportUsesLocalAliasesAndNeverCopiesUserStringsOrRequestIdentity()
    {
        var secret = "PRIVATE-user-serial-password-path";
        await using var rig = new GenericRig(new GenericTestDriver(secret) { Locator = secret });
        Assert.Contains(DeviceDiagnostics.Feature, rig.Hello.Features);
        var request = rig.Request(60) with { Source = secret };
        await rig.Runtime.ExecuteAsync(request); var state = rig.Runtime.State(); var writes = rig.Drivers[0].Writes;
        var report = await Read(rig); var json = JsonSerializer.Serialize(report, Contract.Json);
        Assert.DoesNotContain(secret, json); Assert.DoesNotContain(request.Id, json); Assert.DoesNotContain(rig.Hello.Session, json);
        Assert.DoesNotContain(rig.Hello.Epoch, json); Assert.Equal("device-1", report.Devices[0].Id);
        Assert.Equal("provider-1", report.Devices[0].Provider); Assert.Equal("resource-1", Assert.Single(report.Devices[0].Resources));
        var e = Assert.Single(report.Events!); Assert.Equal(DeviceOperation.SetDuty, e.Operation);
        Assert.Equal(report.Devices[0].Id, e.Targets[0].Device); Assert.Equal(report.Devices[0].Endpoints[0].Id, e.Targets[0].Endpoint);
        Assert.Equal(60, report.Devices[0].Endpoints[0].Execution!.ReportedDuty); Assert.Null(report.Environment.InstalledKernelDriverVersion);
        Assert.False(report.ConfigurationAvailable); Assert.Equal(writes, rig.Drivers[0].Writes); Assert.Equal(0, rig.Drivers[0].Releases);
        Assert.Equal(state.Endpoints[0], rig.Runtime.State().Endpoints[0]);
    }
    [Fact] public async Task OperationHistoryIsOncePerRequestBoundedAndSurvivesACompletedSession()
    {
        await using var rig = new GenericRig(new GenericTestDriver("fan"));
        var first = rig.Request(60); await rig.Runtime.ExecuteAsync(first); await rig.Runtime.ExecuteAsync(first);
        await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.Result, first.Id));
        Assert.Single((await Read(rig)).Events!);
        for (var i = 0; i < 70; i++) await rig.Runtime.ExecuteAsync(rig.Request(40 + i % 30));
        var report = await Read(rig); Assert.Equal(64, report.Events!.Length); Assert.Equal(7, report.EarlierEventsDiscarded);
        await rig.Runtime.DisconnectAsync(rig.Hello.Session);
        var hello = rig.Runtime.OpenSession(ServiceFixture.Sid);
        var reply = await rig.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: new()));
        var after = JsonSerializer.Deserialize<DeviceDiagnosticReport>(reply.Diagnostics!.Data, Contract.Json)!;
        var release = Assert.Single(after.Events!, e => e.ReleaseReason == DeviceReleaseReason.ClientDisconnected);
        Assert.Equal(Outcome.Confirmed, release.Outcome); Assert.Equal(after.Devices[0].Id, release.Device);
    }
    [Fact] public async Task FailedReleaseRetainsDeviceAndUnknownOutcomeWithoutAnExceptionMessage()
    {
        await using var rig = new GenericRig(new GenericTestDriver("secret-device") { RestoreFails = true });
        await rig.Runtime.ExecuteAsync(rig.Request(60)); await rig.Runtime.DisconnectAsync(rig.Hello.Session);
        var hello = rig.Runtime.OpenSession(ServiceFixture.Sid);
        var reply = await rig.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: new()));
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(reply.Diagnostics!.Data, Contract.Json)!;
        var release = Assert.Single(report.Events!, e => e.ReleaseReason is not null);
        Assert.Equal(Outcome.Unknown, release.Outcome); Assert.True(release.FailedWithoutTarget); Assert.Equal("device-1", release.Device);
        Assert.True(report.LifecycleProblem); Assert.DoesNotContain("restore failed", Encoding.UTF8.GetString(reply.Diagnostics.Data));
    }
    [Fact] public async Task ServiceDurationIncludesTheWaitAndKeepsPartialTargetResults()
    {
        var slow = new GenericTestDriver("a") { Entered = new(), Continue = new() };
        await using var rig = new GenericRig(slow, new GenericTestDriver("b") { Fail = true });
        var running = rig.Runtime.ExecuteAsync(rig.Request()); await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        rig.Clock.Advance(.75); slow.Continue.SetResult(); await running;
        var entry = Assert.Single((await Read(rig)).Events!);
        Assert.Equal(750, entry.ServiceElapsedMilliseconds); Assert.Equal(Outcome.Partial, entry.Outcome);
        Assert.Contains(entry.Targets, t => t.Outcome == Outcome.Failed); Assert.Contains(entry.Targets, t => t.Outcome == Outcome.Confirmed);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(7)]
    public async Task RequestedScopeActuallyOmitsUnselectedSections(int value)
    {
        await using var rig = new GenericRig(new GenericTestDriver("a")); await rig.Runtime.ExecuteAsync(rig.Request());
        var scope = (DeviceDiagnosticScope)value; var report = await Read(rig, scope);
        Assert.Equal(scope.HasFlag(DeviceDiagnosticScope.Operations), report.Events is not null);
        Assert.Equal(scope.HasFlag(DeviceDiagnosticScope.Measurements), report.Devices[0].Endpoints[0].Measurements is not null);
        Assert.Null(report.Configuration);
    }
    private sealed class ManySensors(string id, int count = 256) : IDevicePollingDriver, IDeviceTelemetrySource
    {
        public int Polls, Writes; public bool Missing;
        public string DeviceId => id;
        public Task PollAsync(CancellationToken ct) { Polls++; return Task.CompletedTask; }
        public DeviceObservation Observe() => new(id, "private-provider", "private-name", DeviceKind.Motherboard,
            IdentityConfidence.SerialBound, "private-serial", "private-definition", ["private-resource"], Enumerable.Range(0, count).Select(i =>
                new DeviceEndpoint("sensor-" + i, "private-label", EndpointKind.Sensor,
                    [new(DeviceFeature.Rpm, ImplementationSupport.Supported, VerificationEvidence.MatchingHardware, DeviceAvailability.Ready, "private-reason")], SensorId: "private-sensor")).ToArray());
        public DeviceSample[] ReadCachedSamples() => Enumerable.Range(0, count).Select(i => new DeviceSample("sensor-" + i, DeviceFeature.Rpm,
            Missing ? null : 0, 1000, Missing ? "private-problem" : null)).ToArray();
        public string? Validate(DeviceEndpointAction action) => null;
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) { Writes++; throw new InvalidOperationException(); }
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult<DeviceDriverReleaseResult[]>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact] public async Task ChunkedSnapshotIsFrozenAcrossSensorChangesAndHashesAllBytes()
    {
        var driver = new ManySensors("private-device"); var clock = new TestClock();
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); long sequence = 0;
        DeviceServiceRequest Query(DeviceDiagnosticQuery query) => new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: query);
        var firstRequest = Query(new()); var firstReply = await runtime.ExecuteAsync(firstRequest); var first = firstReply.Diagnostics!;
        Assert.True(first.TotalBytes > DeviceDiagnostics.ChunkBytes); driver.Missing = true;
        Assert.Equal(first.Data, (await runtime.ExecuteAsync(firstRequest)).Diagnostics!.Data);
        using var bytes = new MemoryStream(); bytes.Write(first.Data);
        while (bytes.Length < first.TotalBytes)
        {
            var reply = await runtime.ExecuteAsync(Query(new(TransferId: first.TransferId, Offset: (int)bytes.Length)));
            Assert.True(reply.Success); bytes.Write(reply.Diagnostics!.Data);
        }
        Assert.Equal(first.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(bytes.ToArray(), Contract.Json)!;
        Assert.Equal(256, report.Devices[0].Endpoints.Length);
        Assert.All(report.Devices[0].Endpoints, e => { var m = Assert.Single(e.Measurements!); Assert.Equal(0, m.Value); Assert.False(m.Stale); });
        Assert.DoesNotContain("private-", Encoding.UTF8.GetString(bytes.ToArray())); Assert.Equal(0, driver.Polls); Assert.Equal(0, driver.Writes);
        var badScope = await runtime.ExecuteAsync(Query(new(DeviceDiagnosticScope.Summary, first.TransferId)));
        Assert.False(badScope.Success);
        await runtime.ExecuteAsync(Query(new())); Assert.False((await runtime.ExecuteAsync(Query(new(TransferId: first.TransferId)))).Success);
    }
    [Fact] public async Task ChunkLeaseExpiresWithKeepalivesAndCannotCrossSessions()
    {
        await using var rig = new GenericRig(new GenericTestDriver("a"));
        var first = (await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReadDiagnostics) with { Diagnostics = new() })).Diagnostics!;
        for (var i = 0; i < 7; i++) { rig.Clock.Advance(9); Assert.True((await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.Renew))).Success); }
        Assert.False((await rig.Runtime.ExecuteAsync(rig.Read(DeviceOperation.ReadDiagnostics) with { Diagnostics = new(TransferId: first.TransferId) })).Success);
        await rig.Runtime.DisconnectAsync(rig.Hello.Session); var hello = rig.Runtime.OpenSession(ServiceFixture.Sid);
        Assert.False((await rig.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics,
            Diagnostics: new(TransferId: first.TransferId)))).Success);
    }
    [Fact] public async Task LargestInventoryCanBeCapturedWithinTheReportSizeBound()
    {
        await using var runtime = new DeviceControlRuntime(Enumerable.Range(0, 32).Select(i => new ManySensors("device" + i)), ["user"], new TestLog(), new TestClock());
        var hello = runtime.OpenSession("user");
        var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: new()));
        Assert.True(reply.Success, reply.Error); Assert.InRange(reply.Diagnostics!.TotalBytes, DeviceDiagnostics.ChunkBytes + 1, DeviceDiagnostics.MaximumReportBytes);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task MissingAndStaleMeasurementsRemainDistinctFromValidZeroRpm(bool missing, bool aged)
    {
        var driver = new ManySensors("private-device", 1) { Missing = missing }; var clock = new TestClock();
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), clock);
        var hello = runtime.OpenSession("user"); if (aged) clock.Advance(4);
        var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: new()));
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(reply.Diagnostics!.Data, Contract.Json)!;
        var sample = Assert.Single(Assert.Single(Assert.Single(report.Devices).Endpoints).Measurements!);
        Assert.Equal(missing || aged, sample.Stale); Assert.Equal(missing || aged, sample.HasProblem);
        Assert.Equal(missing ? null : (double?)0, sample.Value); Assert.Equal(aged ? 4 : 0, sample.AgeSeconds);
        Assert.DoesNotContain("private-", Encoding.UTF8.GetString(reply.Diagnostics.Data));
    }
    [Theory] [InlineData(-1, 0)] [InlineData(8, 0)] [InlineData(7, -1)] [InlineData(7, 1)]
    public void InvalidQueriesAreRejectedBeforeExecution(int scope, int offset)
    {
        var id = Guid.NewGuid().ToString("N"); var request = new DeviceServiceRequest(2, id, id, id, 1, "PC", DeviceOperation.ReadDiagnostics,
            Diagnostics: new((DeviceDiagnosticScope)scope, Offset: offset));
        Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json)));
    }
    [Fact] public void DiagnosticsCannotBeAttachedToAnOutputOrOrdinaryStateRequest()
    {
        var id = Guid.NewGuid().ToString("N");
        foreach (var operation in new[] { DeviceOperation.State, DeviceOperation.ReadDiagnostics })
        {
            var request = new DeviceServiceRequest(2, id, id, id, 1, "PC", operation, Diagnostics: operation == DeviceOperation.State ? new() : null);
            Assert.Throws<IOException>(() => DeviceServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(request, Contract.Json)));
        }
    }
    [Fact] public void ReplyBoundsRejectForgedLengthOffsetsAndDigests()
    {
        var id = Guid.NewGuid().ToString("N"); var hello = new DeviceServiceHello(2, id, id, 12, []);
        var good = new DeviceDiagnosticChunk(id, DeviceDiagnosticScope.All, 2, new('a', 64), 0, [1, 2]);
        foreach (var bad in new[] { good with { Data = [] }, good with { Offset = 2 }, good with { Sha256 = "bad" },
            good with { TotalBytes = DeviceDiagnostics.MaximumReportBytes + 1 }, good with { Scope = (DeviceDiagnosticScope)8 } })
            Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(JsonSerializer.SerializeToUtf8Bytes(new DeviceServiceReply(2, id, id, id, true, Diagnostics: bad), Contract.Json), hello));
    }
    [Fact] public async Task AuthenticatedWorkspaceExportIsNonActuatingAndTheSavedFileContainsOnlySelectedData()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var transport = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId);
        await using var client = new DeviceWorkspaceClient(transport); await client.ConnectAsync();
        var generation = client.Snapshot!.Configuration.Generation; var writes = demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray();
        var result = await client.ReadDiagnosticsAsync(DeviceDiagnosticScope.All);
        Assert.True(result.Service.ConfigurationAvailable); Assert.True(result.Service.ConfigurationStableDuringCapture);
        Assert.Equal(client.Snapshot.Configuration.Configuration.Cooling.Endpoints.Length, result.Service.Configuration!.Cooling.Length);
        Assert.True(result.TransferMilliseconds >= 0); Assert.True(result.Service.Demo);
        var json = JsonSerializer.Serialize(result, Contract.Json);
        foreach (var device in client.Snapshot.Configuration.Configuration.Devices.Devices) Assert.DoesNotContain(device.Id, json);
        foreach (var endpoint in result.Service.Configuration.Cooling) Assert.StartsWith("device-", endpoint.Target.DeviceId);
        var path = Path.Combine(dir.Path, "diagnostics.json"); await DeviceDiagnosticFiles.SaveAsync(path, result);
        var saved = JsonSerializer.Deserialize<DeviceDiagnosticExport>(await File.ReadAllTextAsync(path), Contract.Json)!;
        Assert.Equal(result.Service.CapturedUtc, saved.Service.CapturedUtc); Assert.Equal(result.Service.Devices.Length, saved.Service.Devices.Length);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Equal(writes, demo.Hubs.Select(h => (h.Writes, h.LightingWrites)).ToArray()); Assert.Equal(generation, client.Snapshot.Configuration.Generation);
        var feature = Array.IndexOf(transport.Hello!.Features, DeviceDiagnostics.Feature); transport.Hello.Features[feature] = "unsupported-diagnostic-test";
        Assert.False(client.SupportsDiagnostics); await Assert.ThrowsAsync<IOException>(() => client.ReadDiagnosticsAsync(DeviceDiagnosticScope.All));
    }
    [Fact] public async Task CancelledSavePreservesExistingFileAndRemovesTemporaryOutput()
    {
        using var dir = new ServiceTestDirectory(); await using var rig = new GenericRig(new GenericTestDriver("a"));
        var export = new DeviceDiagnosticExport(await Read(rig), "1.0", 0); var path = Path.Combine(dir.Path, "existing.json");
        await File.WriteAllTextAsync(path, "original"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeviceDiagnosticFiles.SaveAsync(path, export, cancelled.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        await Assert.ThrowsAnyAsync<IOException>(() => DeviceDiagnosticFiles.SaveAsync(dir.Path, export));
    }
    private sealed class DiagnosticConfiguration : IDeviceConfigurationHandler, IDeviceDiagnosticConfiguration
    {
        public TaskCompletionSource? Entered, Finish;
        public int Calls; public bool ChangeGeneration;
        public Task<DeviceConfigurationReply> ExecuteAsync(DeviceConfigurationRequest request, string requestId, string owner, DeviceControlRuntime runtime, CancellationToken ct) => throw new NotSupportedException();
        public async Task<DeviceDiagnosticConfigurationCapture> ReadDiagnosticsAsync(CancellationToken ct)
        {
            Calls++; Entered?.TrySetResult(); if (Finish is not null) await Finish.Task.WaitAsync(ct);
            return new(ChangeGeneration ? Calls.ToString() : "same", new(0, 0, 0, 0, 0, 0, 0, false, false, []));
        }
    }
    [Fact] public async Task ConfigurationWaitDoesNotHoldRuntimeGateAndDisconnectCancelsCapture()
    {
        var source = new DiagnosticConfiguration { Entered = new(), Finish = new() }; var driver = new GenericTestDriver("a");
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog()); runtime.InstallConfigurationHandler(source);
        var hello = runtime.OpenSession("user");
        DeviceServiceRequest Request(long sequence, DeviceOperation operation) => new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), sequence, "PC", operation);
        var pending = runtime.ExecuteAsync(Request(1, DeviceOperation.ReadDiagnostics) with { Diagnostics = new() });
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True((await runtime.ExecuteAsync(Request(2, DeviceOperation.Renew)).WaitAsync(TimeSpan.FromSeconds(2))).Success);
            var state = runtime.State(); var endpoint = Assert.Single(state.Endpoints);
            Assert.True((await runtime.ExecuteAsync(Request(3, DeviceOperation.SetDuty) with { TopologyRevision = state.Inventory.TopologyRevision,
                Targets = [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], Duty = 60 }).WaitAsync(TimeSpan.FromSeconds(2))).Success);
            Assert.Equal(1, driver.Writes);
            Assert.False((await runtime.ExecuteAsync(Request(4, DeviceOperation.ReadDiagnostics) with { Diagnostics = new() })).Success);
            await runtime.DisconnectAsync(hello.Session).WaitAsync(TimeSpan.FromSeconds(3)); Assert.False((await pending).Success);
            Assert.Null((await pending).Diagnostics);
        }
        finally { source.Finish.TrySetResult(); await pending; }
    }
    [Fact] public async Task ConcurrentConfigurationChangeIsReportedInsteadOfClaimingOneConsistentCapture()
    {
        var source = new DiagnosticConfiguration { ChangeGeneration = true };
        await using var runtime = new DeviceControlRuntime([], ["user"], new TestLog()); runtime.InstallConfigurationHandler(source);
        var hello = runtime.OpenSession("user");
        var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: new()));
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(reply.Diagnostics!.Data, Contract.Json)!;
        Assert.True(report.ConfigurationAvailable); Assert.False(report.ConfigurationStableDuringCapture); Assert.Equal(2, source.Calls);
    }
    [Fact] public void DiagnosticUiTestRequiresAnIsolatedBuiltInDemo()
    {
        var prefix = new[] { "--devices-preview", "--demo", "--data-dir", "preview" };
        Assert.True(DeviceWorkspaceOptions.Parse([.. prefix, "--smoke", "--diagnostics"]).Diagnostics);
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--diagnostics"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--smoke", "--diagnostics", "--openrgb-inspection"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. prefix, "--smoke", "--diagnostics", "--devices-pipe", "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), "--server-pid", "1"]));
    }
}
