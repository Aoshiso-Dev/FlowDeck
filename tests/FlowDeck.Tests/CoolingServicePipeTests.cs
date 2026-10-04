using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class CoolingServicePipeTests
{
    private static string Sid { get { using var user = WindowsIdentity.GetCurrent(); return user.User!.Value; } }
    private static string Name() => "FlowDeck.Cooling.Test." + Guid.NewGuid().ToString("N");
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20); Assert.True(condition()); }
    [Fact] public void AclAllowsOnlyTheSelectedUserAndSystemAndDeniesNetworkAndNewServerInstances()
    {
        var sid = Sid; var security = CoolingServicePipeServer.Security([sid]); Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-2" && r.AccessControlType == AccessControlType.Deny);
        Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Contains(r.IdentityReference.Value, new[] { sid, "S-1-5-18" }));
        var user = rules.Single(r => r.IdentityReference.Value == sid);
        Assert.Equal((PipeAccessRights)0, user.PipeAccessRights & PipeAccessRights.CreateNewInstance); Assert.Equal((PipeAccessRights)0, user.PipeAccessRights & PipeAccessRights.ChangePermissions);
    }
    [Fact] public async Task RealLocalPipeAuthenticatesTokenAndProcessAndRestoresAfterClientDisconnect()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var server = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name); var serving = server.RunAsync(stop.Token);
        try
        {
            await using (var client = new CoolingServicePipeClient(name, () => Environment.ProcessId))
            {
                await client.ConnectAsync(startHeartbeat: false); Assert.True(client.Connected); Assert.True(client.Hello!.Demo);
                var state = await client.InvokeAsync<ServiceCoolingState, object?>("state", null); Assert.NotNull(state);
                var result = await client.InvokeAsync<OperationResult, ControlRequest>("control", f.Manual(54)); Assert.Equal(Outcome.Confirmed, result!.Outcome);
                var config = new ServiceBoardCommand(MotherboardFans.Cpu.Id, f.Runtime.SettingsRevision, 42);
                Assert.True((await client.InvokeAsync<CpuFanState, ServiceBoardCommand>("board-manual", config))!.Managing);
                Assert.Equal(42, f.Drivers[MotherboardFans.Cpu.Id].Duty);
            }
            await Until(() => !f.Runtime.HasOwner && !f.Runtime.Coordinator.Snapshot.Managing && f.Drivers[MotherboardFans.Cpu.Id].Duty == 65);
            Assert.Equal(70, f.Runtime.Coordinator.Snapshot.ConfirmedDuty);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task ClientRejectsANameOwnedByAProcessOtherThanTheExpectedService()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId + 999);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectAsync(startHeartbeat: false)); Assert.Equal(0, f.Hub.Writes);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task AnAuthenticatedWindowsUserStillNeedsRuntimeAuthorization()
    {
        await using var f = new ServiceFixture(); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
            await Assert.ThrowsAnyAsync<IOException>(() => client.ConnectAsync(startHeartbeat: false)); Assert.False(f.Runtime.HasOwner); Assert.Equal(0, f.Hub.Writes);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task TimeoutDoesNotResendHardwareWriteAndResultCanBeQueriedOverTheSamePipe()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId); await client.ConnectAsync(startHeartbeat: false);
            f.Hub.DelayMs = 200;
            var error = await Assert.ThrowsAsync<ServiceRequestUncertainException>(() => client.CallAsync("control", f.Manual(53), TimeSpan.FromMilliseconds(10)));
            var reply = await client.InvokeAsync<CoolingServiceReply, ServiceResultQuery>("result", new(error.RequestId));
            Assert.NotNull(reply); Assert.Equal(Outcome.Confirmed, reply.Payload.Deserialize<OperationResult>(Contract.Json)!.Outcome); Assert.Equal(1, f.Hub.Writes);
        }
        finally { f.Hub.DelayMs = 0; stop.Cancel(); await serving; }
    }
    [Fact] public async Task UiSubscriberFailureDoesNotBreakHeartbeatOrStateDelivery()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token); var received = false;
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
            client.StateReceived += _ => throw new InvalidOperationException("UI callback failure"); client.StateReceived += _ => received = true;
            await client.ConnectAsync(); await Until(() => received); Assert.Null(client.Error); Assert.True(f.Runtime.HasOwner);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task FramingRejectsNegativeOversizedAndTruncatedFramesBeforeAllocatingPayload()
    {
        foreach (var length in new[] { -1, 0, CoolingServiceProtocol.MaximumFrameBytes + 1 })
        {
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
            await Assert.ThrowsAsync<IOException>(() => CoolingPipeFrames.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        }
        var truncated = new byte[5]; BinaryPrimitives.WriteInt32LittleEndian(truncated, 20);
        await Assert.ThrowsAsync<EndOfStreamException>(() => CoolingPipeFrames.ReadAsync(new MemoryStream(truncated), CancellationToken.None));
        using var stream = new MemoryStream(); await CoolingPipeFrames.WriteAsync(stream, new { Value = "日本語" }, CancellationToken.None); stream.Position = 0;
        var json = JsonDocument.Parse(await CoolingPipeFrames.ReadAsync(stream, CancellationToken.None)); Assert.Equal("日本語", json.RootElement.GetProperty("value").GetString());
    }
    [Fact] public async Task TemperatureAndOtherFanReadingsContinueDuringTheFiveSecondPortTest()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false, loops: true); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        var states = new ConcurrentQueue<ServiceCoolingState>();
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
            client.StateReceived += states.Enqueue; await client.ConnectAsync(); await Until(() => states.Count > 0);
            var before = f.Runtime.Coordinator.Snapshot.Sensors.Single(s => s.Id == "demo/cpu").Timestamp;
            var testing = client.InvokeAsync<CpuFanState, ServiceBoardCommand>("board-test", new(MotherboardFans.Cpu.Id, f.Runtime.SettingsRevision));
            await Until(() => f.Runtime.Fans[MotherboardFans.Cpu.Id].State.Mode == "test");
            f.Temperatures.Cpu = 67;
            await Until(() => states.Any(s => s.Fans[MotherboardFans.Cpu.Id].Mode == "test" &&
                s.Control.Sensors.Any(t => t.Id == "demo/cpu" && t.Celsius == 67 && t.Timestamp > before) &&
                s.Fans[MotherboardFans.Chassis.Id].Reading?.Timestamp > before));
            Assert.False(testing.IsCompleted);
            var result = await testing; Assert.True(result!.Test!.Restored); Assert.Null(client.Error); Assert.Equal(65, f.Drivers[MotherboardFans.Cpu.Id].Duty);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task RenewalsPassPendingHardwareWhileCommandsRemainOrderedAndExactlyOnce()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId); await client.ConnectAsync(startHeartbeat: false);
            f.Hub.DelayMs = 1500;
            var writing = client.InvokeAsync<OperationResult, ControlRequest>("control", f.Manual(52));
            await Until(() => f.Hub.Writes == 1);
            var queued = client.InvokeAsync<ServiceCoolingState, object?>("state", null);
            for (var i = 0; i < 4; i++)
            {
                f.LeaseClock.Advance(4);
                Assert.True(await client.InvokeAsync<bool, object?>("renew", null, TimeSpan.FromMilliseconds(600)));
                Assert.False(writing.IsCompleted); Assert.False(queued.IsCompleted);
            }
            Assert.Equal(Outcome.Confirmed, (await writing)!.Outcome); Assert.Equal(52, (await queued)!.Control.ConfirmedDuty);
            await f.Runtime.SweepAsync(); Assert.True(f.Runtime.HasOwner); Assert.Equal(1, f.Hub.Writes);
        }
        finally { f.Hub.DelayMs = 0; stop.Cancel(); await serving; }
    }
    [Fact] public async Task DisconnectDiscardsQueuedWritesAndRestoresAfterTheInFlightCommand()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        var operations = new ConcurrentQueue<OperationResult>(); f.Runtime.Coordinator.OperationCompleted += operations.Enqueue;
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId); await client.ConnectAsync(startHeartbeat: false);
            f.Hub.DelayMs = 300;
            var writing = client.InvokeAsync<OperationResult, ControlRequest>("control", f.Manual(52)); await Until(() => f.Hub.Writes == 1);
            var queued = client.InvokeAsync<CpuFanState, ServiceBoardCommand>("board-manual", new(MotherboardFans.Cpu.Id, f.Runtime.SettingsRevision, 46));
            Assert.True(await client.InvokeAsync<bool, object?>("renew", null)); // Reader has admitted the preceding queued frame.
            await client.DisposeAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => writing); await Assert.ThrowsAnyAsync<IOException>(() => queued);
            await Until(() => !f.Runtime.HasOwner && f.Hub.Writes == 2 && !f.Runtime.Coordinator.Snapshot.Managing &&
                operations.Any(value => value.Command == "StopControl" && value.Outcome == Outcome.Confirmed));
            Assert.Equal(0, f.Drivers[MotherboardFans.Cpu.Id].Writes);
            // Snapshot display values expire after three seconds; waiting for cancelled pipe
            // requests must not turn that UI freshness rule into a restoration failure.
            // Verify the completed stop and every actual mock channel instead.
            Assert.All((await f.Hub.ReadAsync()).Channels, channel => Assert.Equal(70, channel.Duty));
        }
        finally { f.Hub.DelayMs = 0; stop.Cancel(); await serving; }
    }
    [Fact] public async Task RepeatedPushesPreserveOldObservationTimesAndDoNotRenewAnIdleOwner()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        var states = new ConcurrentQueue<ServiceCoolingState>();
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
            client.StateReceived += states.Enqueue; await client.ConnectAsync(startHeartbeat: false); await Until(() => states.Count >= 3);
            var original = f.Runtime.Coordinator.Snapshot.Sensors;
            Assert.All(states, s => Assert.Equal(original, s.Control.Sensors));
            f.LeaseClock.Advance(CoolingServiceProtocol.LeaseSeconds); await f.Runtime.SweepAsync();
            await Until(() => !client.Connected); Assert.False(f.Runtime.HasOwner); Assert.NotNull(client.Error);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task EveryCompletedOperationIsDeliveredIncludingBackgroundResultsAndOriginalClientIds()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        var events = new ConcurrentQueue<OperationResult>(); var expected = new List<string>();
        try
        {
            await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
            client.OperationReceived += _ => throw new InvalidOperationException("broken history subscriber");
            client.OperationReceived += events.Enqueue; await client.ConnectAsync(startHeartbeat: false);
            for (var i = 0; i < 8; i++)
            {
                var command = f.Manual(50 + i); expected.Add(command.Id);
                Assert.Equal(Outcome.Confirmed, (await client.InvokeAsync<OperationResult, ControlRequest>("control", command))!.Outcome);
            }
            var background = f.Manual(59) with { Source = "service-background" }; expected.Add(background.Id);
            await f.Runtime.Coordinator.RequestAsync(background);
            await Until(() => events.Count >= expected.Count);
            Assert.Equal(expected, events.Select(e => e.Id)); Assert.All(events, e => Assert.Equal(Outcome.Confirmed, e.Outcome));
            Assert.Null(client.Error); Assert.True(client.Connected);
        }
        finally { stop.Cancel(); await serving; }
    }
    [Fact] public async Task ASlowUiCallbackDoesNotBlockCommandRepliesOrLoseCompletedOperations()
    {
        await using var f = new ServiceFixture(Sid); await f.Initialize(false); using var stop = new CancellationTokenSource();
        var name = Name(); var serving = new CoolingServicePipeServer(f.Runtime, [Sid], new TestLog(), name).RunAsync(stop.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<OperationResult>();
        await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
        try
        {
            client.StateReceived += _ => { entered.TrySetResult(); resume.Task.GetAwaiter().GetResult(); };
            client.OperationReceived += events.Enqueue; await client.ConnectAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var command = f.Manual(54);
            var result = await client.InvokeAsync<OperationResult, ControlRequest>("control", command, TimeSpan.FromSeconds(1));
            Assert.Equal(Outcome.Confirmed, result!.Outcome); Assert.Empty(events);
            resume.TrySetResult(); await Until(() => events.Any(e => e.Id == command.Id)); Assert.Null(client.Error);
        }
        finally { resume.TrySetResult(); await client.DisposeAsync(); stop.Cancel(); await serving; }
    }
    [Fact] public async Task ClientRejectsRepeatedPushSequenceInsteadOfReplayingHistory()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var name = Name();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); await using var client = new CoolingServicePipeClient(name, () => Environment.ProcessId);
        var events = new ConcurrentQueue<OperationResult>(); var states = new ConcurrentQueue<ServiceCoolingState>();
        client.StateReceived += states.Enqueue; client.OperationReceived += events.Enqueue;
        var connecting = client.ConnectAsync(startHeartbeat: false); await connected;
        await CoolingPipeFrames.WriteAsync(server, f.Hello, CancellationToken.None); await connecting;
        var update = new CoolingServiceMessage(1, f.Hello.Epoch, f.Hello.Session, "update", null, new(1, f.Runtime.State(), []));
        await CoolingPipeFrames.WriteAsync(server, update, CancellationToken.None); await Until(() => states.Count == 1);
        await CoolingPipeFrames.WriteAsync(server, update, CancellationToken.None); await Until(() => !client.Connected);
        Assert.Contains("欠落または重複", client.Error); Assert.Single(states); Assert.Empty(events);
    }
    [Fact] public async Task StreamProtocolRejectsStaleSessionsMixedFramesUnknownKindsAndDuplicateFields()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        var update = new CoolingServiceMessage(1, f.Hello.Epoch, f.Hello.Session, "update", null, new(1, f.Runtime.State(), []));
        static byte[] Bytes(CoolingServiceMessage value) => JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json);
        Assert.Equal("update", CoolingServiceProtocol.ReadMessage(Bytes(update), f.Hello).Kind);
        Assert.Throws<IOException>(() => CoolingServiceProtocol.ReadMessage(Bytes(update with { Session = Guid.NewGuid().ToString("N") }), f.Hello));
        Assert.Throws<IOException>(() => CoolingServiceProtocol.ReadMessage(Bytes(update with { Epoch = Guid.NewGuid().ToString("N") }), f.Hello));
        Assert.Throws<IOException>(() => CoolingServiceProtocol.ReadMessage(Bytes(update with { Kind = "event-unknown" }), f.Hello));
        var reply = new CoolingServiceReply(1, f.Hello.Epoch, f.Hello.Session, Guid.NewGuid().ToString("N"), 1, true, null, CoolingServiceProtocol.Payload(true));
        Assert.Throws<IOException>(() => CoolingServiceProtocol.ReadMessage(Bytes(update with { Reply = reply }), f.Hello));
        var json = JsonSerializer.Serialize(update, Contract.Json).Insert(1, "\"Kind\":\"update\",");
        Assert.Throws<JsonException>(() => CoolingServiceProtocol.ReadMessage(System.Text.Encoding.UTF8.GetBytes(json), f.Hello));
    }
}
