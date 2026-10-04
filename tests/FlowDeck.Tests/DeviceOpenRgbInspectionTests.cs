using System.Buffers.Binary;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceOpenRgbInspectionTests
{
    [Theory]
    [InlineData("--notifications")] [InlineData("--lighting-identification")] [InlineData("--m5-connection")]
    public void InspectionSmokeRequiresTheIsolatedDemoAndCannotMixScenarios(string other)
    {
        string[] args = ["--devices-preview", "--demo", "--smoke", "--openrgb-inspection", "--data-dir", Path.GetTempPath()];
        Assert.True(DeviceWorkspaceOptions.Parse(args).OpenRgbInspection);
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. args, other]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse(args.Where(a => a != "--smoke").ToArray()));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. args, "--devices-pipe", "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), "--server-pid", "1"]));
    }

    [Theory]
    [InlineData(1023, false)] [InlineData(1024, true)] [InlineData(6742, true)] [InlineData(65535, true)] [InlineData(65536, false)]
    public void InspectionRequestAcceptsOnlyBoundedPortsAndAnExplicitGeneration(int port, bool valid)
    {
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.InspectOpenRgb, ExpectedGeneration: Guid.NewGuid().ToString("N"), OpenRgbPort: port);
        Assert.Equal(valid, DeviceConfigurationProtocol.Valid(request));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { ExpectedGeneration = null }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Data = [1] }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { TopologyRevision = 0 }));
        Assert.False(DeviceConfigurationProtocol.Valid(request with { Operation = DeviceConfigurationOperation.Snapshot }));
    }

    [Fact]
    public async Task TheSdkInspectorReadsOnlySettingsAndNeverScansOrWritesControllers()
    {
        await using var server = new OpenRgbSdkModelServer();
        var result = await new DeviceOpenRgbInspector().InspectAsync(server.Port, default);
        Assert.Equal(OpenRgbInspectionStatus.Observed, result.Status); Assert.True(DeviceOpenRgbInspection.Valid(result));
        Assert.Equal("未検証", result.Items.Single(i => i.Key == "coexistence").Value);
        Assert.Equal(OpenRgbInspectionCondition.Unverified, result.Items.Single(i => i.Key == "coexistence").Condition);
        Assert.All(server.Wire.Requests, r => Assert.Contains(r.Command, new uint[] { 40, 50, 52, 250, 251, 200 }));
        Assert.All(server.Wire.Requests.Where(r => r.Command == 52), r => Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(r.Data) & 65536));
        Assert.Equal(0, server.WriteCount); Assert.Equal(2, server.Wire.Requests.Count(p => p.Command == 200));
    }

    [Theory]
    [InlineData("null")] [InlineData("{}")] [InlineData("{\"shared_smbus_access\":\"true\",\"smbus_sleep_mode\":9}")]
    public async Task MissingAndMalformedDriverValuesRemainUnavailable(string drivers)
    {
        await using var server = new OpenRgbSdkModelServer { Drivers = drivers };
        var result = await new DeviceOpenRgbInspector().InspectAsync(server.Port, default);
        Assert.Equal(OpenRgbInspectionStatus.Observed, result.Status);
        Assert.All(result.Items.Where(i => i.Key is "shared-smbus" or "smbus-wait"), i => Assert.Equal(OpenRgbInspectionCondition.Unavailable, i.Condition));
    }

    [Theory]
    [InlineData(1, OpenRgbInspectionStatus.SettingsUnavailable)] [InlineData(17, OpenRgbInspectionStatus.Observed)]
    public async Task MissingSettingsOrPluginSupportCannotBecomeAnObservedEmptyConfiguration(uint flags, OpenRgbInspectionStatus expected)
    {
        await using var server = new OpenRgbSdkModelServer(); server.Wire.Flags = flags;
        var result = await new DeviceOpenRgbInspector().InspectAsync(server.Port, default);
        Assert.Equal(expected, result.Status);
        if (flags == 17) Assert.Equal(OpenRgbInspectionCondition.Unavailable, result.Items.Single(i => i.Key == "plugins").Condition);
        Assert.DoesNotContain(server.Wire.Requests, p => p.Command == 200);
    }

    [Fact]
    public async Task TimeoutClosesTheInspectionAndReportsNoControlReadiness()
    {
        await using var server = new OpenRgbSdkTestServer { Intercept = _ => Task.FromResult(true) };
        var result = await new DeviceOpenRgbInspector(TimeSpan.FromMilliseconds(150)).InspectAsync(server.Port, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(OpenRgbInspectionStatus.ConnectionFailed, result.Status);
        Assert.Equal(OpenRgbInspectionCondition.Unverified, Assert.Single(result.Items).Condition);
    }

    [Fact]
    public async Task CallerCancellationIsNotReturnedAsASuccessfulObservation()
    {
        await using var server = new OpenRgbSdkTestServer { Intercept = _ => Task.FromResult(true) };
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeviceOpenRgbInspector().InspectAsync(server.Port, cancellation.Token));
    }

    [Fact]
    public async Task RemoteMetadataCannotLeakIntoTheServiceReport()
    {
        await using var server = new OpenRgbSdkModelServer { Detectors = "{\"secret\":\"do not echo\",\"detectors\":{\"private name\":true}}" };
        var result = await new DeviceOpenRgbInspector().InspectAsync(server.Port, default);
        var serialized = JsonSerializer.Serialize(result, Contract.Json);
        Assert.DoesNotContain("secret", serialized); Assert.DoesNotContain("private name", serialized); Assert.DoesNotContain("do not echo", serialized);
        Assert.Equal("有効 1 / 1 項目", result.Items.Single(i => i.Key == "detectors").Value);
    }

    [Fact]
    public async Task ResponsesRejectMixedTransferDataDuplicateRowsAndUnboundedText()
    {
        var result = await new DemoOpenRgbInspector().InspectAsync(6742, default);
        var reply = new DeviceConfigurationReply(Outcome.Confirmed, "確認済み", Guid.NewGuid().ToString("N"), OpenRgbInspection: result);
        Assert.True(DeviceConfigurationProtocol.Valid(reply));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Data = [1] }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Outcome = Outcome.Rejected }));
        Assert.False(DeviceOpenRgbInspection.Valid(result with { Items = [result.Items[0], result.Items[0]] }));
        Assert.False(DeviceOpenRgbInspection.Valid(result with { Message = new('a', 161) }));
        Assert.False(DeviceOpenRgbInspection.Valid(result with { Items = [result.Items[0] with { Value = "line\nline" }] }));
    }

    [Fact]
    public async Task DemoWorkspaceInspectsOverAuthenticatedPipeWithoutChangingSettingsOrOutputs()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var before = client.Snapshot!.Configuration;
        Assert.True(client.SupportsOpenRgbInspection);
        var result = await client.InspectOpenRgbAsync(7654);
        Assert.Equal(7654, result.Port); Assert.Equal(1, demo.OpenRgb.Calls);
        await client.RefreshAsync(readConfiguration: true);
        Assert.Equal(before.Generation, client.Snapshot!.Configuration.Generation);
        Assert.Equal(JsonSerializer.Serialize(before.Configuration, Contract.Json), JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration, Contract.Json));
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }

    [Fact]
    public async Task OlderServiceDoesNotReceiveTheNewRequest()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, openRgbInspection: false);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        Assert.False(client.SupportsOpenRgbInspection);
        await Assert.ThrowsAsync<IOException>(() => client.InspectOpenRgbAsync(6742));
        Assert.Equal(0, demo.OpenRgb.Calls); Assert.True(client.Snapshot!.Connected);
    }

    [Fact]
    public async Task CancellingTheCallerAfterDispatchDoesNotDropTheSharedControlConnection()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demo.OpenRgb.Before = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        using var cancellation = new CancellationTokenSource(); var session = client.Snapshot!.Session;
        var pending = client.InspectOpenRgbAsync(6742, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            cancellation.Cancel(); release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await client.RefreshAsync(); Assert.True(client.Snapshot!.Connected); Assert.Equal(session, client.Snapshot.Session);
            Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
        }
        finally { release.TrySetResult(); }
    }
}

public partial class DeviceConfigurationAuthorityTests
{
    [Fact]
    public async Task OpenRgbInspectionDoesNotSerializeCoolingPersistenceAndRejectsTheNowStaleResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspector = new DemoOpenRgbInspector { Before = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        await using var r = await Rig.Create(inspector: inspector);
        var request = r.Request(new(DeviceConfigurationOperation.InspectOpenRgb, ExpectedGeneration: r.Startup.Generation.Id, OpenRgbPort: 6742));
        var pending = r.Startup.Runtime.ExecuteAsync(request); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.Contains(DeviceOpenRgbInspection.Feature, r.Hello.Features);
            var duplicate = r.Startup.Runtime.ExecuteAsync(request);
            var busy = await r.Send(request.Configuration!).WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(busy.Success); Assert.Equal(1, inspector.Calls);
            var cooling = await r.Startup.Runtime.ExecuteAsync(r.Duty(75)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(cooling.Success, cooling.Error); Assert.Equal(75, (await r.Hub.ReadAsync()).Channels[0].Duty);
            release.SetResult(); var reply = await pending; Assert.False(reply.Success); Assert.Null(reply.Configuration);
            Assert.Equal(reply, await duplicate); Assert.Equal(1, inspector.Calls);
        }
        finally { release.TrySetResult(); await pending; }
    }

    [Fact]
    public async Task OpenRgbInspectionPreservesAnInProgressConfigurationUpload()
    {
        var inspector = new DemoOpenRgbInspector(); await using var r = await Rig.Create(inspector: inspector);
        var transfer = await r.Upload(r.Changed(), validate: false); var pointer = r.Startup.Generation;
        var result = await r.Send(new(DeviceConfigurationOperation.InspectOpenRgb, ExpectedGeneration: pointer.Id, OpenRgbPort: 6742));
        Assert.True(result.Success, result.Error); Assert.Equal(pointer, r.Store.ReadActivePointer()); Assert.Equal(0, r.Hub.Writes);
        Assert.True((await r.Send(new(DeviceConfigurationOperation.Validate, transfer))).Success);
    }

    [Fact]
    public async Task OpenRgbInspectionChecksSessionAndGenerationBeforeOpeningAnything()
    {
        var inspector = new DemoOpenRgbInspector(); await using var r = await Rig.Create(inspector: inspector);
        var request = r.Request(new(DeviceConfigurationOperation.InspectOpenRgb, ExpectedGeneration: Guid.NewGuid().ToString("N"), OpenRgbPort: 6742));
        Assert.False((await r.Startup.Runtime.ExecuteAsync(request)).Success);
        Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Request(request.Configuration! with { ExpectedGeneration = r.Startup.Generation.Id }) with { Session = Guid.NewGuid().ToString("N") })).Success);
        Assert.Equal(0, inspector.Calls); Assert.Equal(0, r.Hub.Writes);
    }

    [Fact]
    public async Task SessionReleaseCancelsOpenRgbInspectionWithoutReturningOldObservations()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspector = new DemoOpenRgbInspector { Before = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        await using var r = await Rig.Create(inspector: inspector);
        var pending = r.Send(new(DeviceConfigurationOperation.InspectOpenRgb, ExpectedGeneration: r.Startup.Generation.Id, OpenRgbPort: 6742));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await r.Startup.Runtime.DisconnectAsync(r.Hello.Session);
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(reply.Success); Assert.Null(reply.Configuration);
        Assert.Equal(0, r.Hub.Writes);
    }
}
