using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class ServiceDriver : ICpuFanDriver
{
    public int Duty = 65, Writes, Restores;
    public bool RestoreFails;
    public Task<CpuFanReading> ReadAsync(CancellationToken ct = default) => Task.FromResult(new CpuFanReading(Duty, 1000, TimeProvider.System.GetTimestamp()));
    public Task<CpuFanReading> WriteAsync(int duty, CancellationToken ct = default) { Writes++; Duty = duty; return ReadAsync(ct); }
    public Task RestoreAsync(CancellationToken ct = default) { Restores++; if (RestoreFails) throw new IOException("restore failure"); Duty = 65; return Task.CompletedTask; }
}
public sealed class ServiceFixture : IAsyncDisposable
{
    public const string Sid = "S-1-5-21-111-222-333-1001";
    public DemoHub Hub { get; } = new() { DelayMs = 0 };
    public DemoTemperatures Temperatures { get; } = new();
    public DemoMotherboard Board { get; } = new();
    public TestClock LeaseClock { get; } = new();
    public Dictionary<string, ServiceDriver> Drivers { get; } = MotherboardFans.Ports.ToDictionary(p => p.Id, _ => new ServiceDriver());
    public CoolingServiceRuntime Runtime { get; }
    public ServiceHello Hello { get; private set; } = null!;
    private long sequence;
    public ServiceFixture(string sid = Sid, IMemoryRgbControl? memoryRgb = null)
    {
        var settings = DemoHub.CreateSettings() with { CpuFan = new(true, 20), ChassisFan = new(true, 30),
            BoardFans = MotherboardFans.Ports.Skip(2).ToDictionary(p => p.Id, _ => new CpuFanSettings(true, 30)) };
        Runtime = new(Hub, Temperatures, Board, p => Drivers[p.Id], settings, [sid], new TestLog(), LeaseClock, memoryRgb: memoryRgb);
    }
    public async Task Initialize(bool session = true, bool loops = false)
    { await Runtime.InitializeAsync(loops); if (session) Hello = await Runtime.OpenSessionAsync(Sid); }
    public CoolingServiceRequest Request<T>(string command, T value) => new(1, Hello.Epoch, Hello.Session, Guid.NewGuid().ToString("N"), ++sequence, command, CoolingServiceProtocol.Payload(value));
    public Task<CoolingServiceReply> Call<T>(string command, T value) => Runtime.ExecuteAsync(Request(command, value));
    public ControlRequest Manual(int duty) => new(Guid.NewGuid().ToString("N"), "PC", Runtime.Epoch, Runtime.Coordinator.Snapshot.ControlRevision, "SetManualDuty", Duty: duty);
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}

public class CoolingServiceTests
{
    [Fact] public async Task OnlyOneAuthorizedOwnerCanAcquireAndOldSessionsCannotWrite()
    {
        await using var f = new ServiceFixture(); await f.Initialize(false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Runtime.OpenSessionAsync("S-1-5-21-111-222-333-9999"));
        var first = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid);
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.OpenSessionAsync(ServiceFixture.Sid));
        await f.Runtime.DisconnectAsync(first.Session); var second = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid);
        var old = new CoolingServiceRequest(1, first.Epoch, first.Session, Guid.NewGuid().ToString("N"), 1, "control", CoolingServiceProtocol.Payload(f.Manual(60)));
        var denied = await f.Runtime.ExecuteAsync(old); Assert.False(denied.Success); Assert.Equal(0, f.Hub.Writes);
        Assert.NotEqual(first.Session, second.Session); await f.Runtime.DisconnectAsync(first.Session); Assert.True(f.Runtime.HasOwner);
    }
    [Fact] public async Task DuplicateCommandsAreExactlyOnceAndSequenceCannotGoBackwards()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var command = f.Request("control", f.Manual(55));
        var first = await f.Runtime.ExecuteAsync(command); Assert.True(first.Success); var count = f.Hub.Writes;
        Assert.Equal(Outcome.Confirmed, first.Payload.Deserialize<OperationResult>(Contract.Json)!.Outcome);
        Assert.Equal(SettingsTransfer.Canonical(first), SettingsTransfer.Canonical(await f.Runtime.ExecuteAsync(command))); Assert.Equal(count, f.Hub.Writes);
        var altered = command with { Payload = CoolingServiceProtocol.Payload(f.Manual(80)) };
        Assert.False((await f.Runtime.ExecuteAsync(altered)).Success);
        Assert.False((await f.Runtime.ExecuteAsync(command with { Id = Guid.NewGuid().ToString("N") })).Success); Assert.Equal(count, f.Hub.Writes);
        var query = await f.Call("result", new ServiceResultQuery(command.Id));
        Assert.Equal(command.Id, query.Payload.Deserialize<CoolingServiceReply>(Contract.Json)!.Id);
    }
    [Fact] public async Task ExpiredOwnerRestoresAllFansAndCannotRenewItsOldNonce()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        Assert.True((await f.Call("control", f.Manual(58))).Success);
        foreach (var port in MotherboardFans.Ports) Assert.True((await f.Call("board-manual", new ServiceBoardCommand(port.Id, f.Runtime.SettingsRevision, 45))).Success);
        Assert.All(f.Drivers.Values, d => Assert.Equal(45, d.Duty));
        f.LeaseClock.Advance(CoolingServiceProtocol.LeaseSeconds); await f.Runtime.SweepAsync();
        Assert.False(f.Runtime.HasOwner); Assert.False(f.Runtime.Coordinator.Snapshot.Managing); Assert.Equal(70, f.Runtime.Coordinator.Snapshot.ConfirmedDuty);
        Assert.All(f.Drivers.Values, d => { Assert.Equal(65, d.Duty); Assert.Equal(1, d.Restores); });
        Assert.False((await f.Call<object?>("heartbeat", null)).Success);
    }
    [Fact] public async Task ValidHeartbeatsRenewLeaseButDuplicateOrInvalidMessagesDoNot()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var heartbeat = f.Request<object?>("heartbeat", null);
        Assert.True((await f.Runtime.ExecuteAsync(heartbeat)).Success); f.LeaseClock.Advance(8);
        Assert.True((await f.Call<object?>("heartbeat", null)).Success); f.LeaseClock.Advance(8); await f.Runtime.SweepAsync(); Assert.True(f.Runtime.HasOwner);
        Assert.True((await f.Runtime.ExecuteAsync(heartbeat)).Success); f.LeaseClock.Advance(4); await f.Runtime.SweepAsync(); Assert.False(f.Runtime.HasOwner);
    }
    [Fact] public async Task OutOfBandRenewalRejectsReplaysForeignSessionsPayloadsAndExpiredOwners()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        var renewal = f.Request<object?>("renew", null);
        Assert.True((await f.Runtime.ExecuteAsync(renewal)).Success); f.LeaseClock.Advance(7);
        Assert.False((await f.Runtime.ExecuteAsync(renewal)).Success);
        Assert.False((await f.Runtime.ExecuteAsync(renewal with { Sequence = 2, Session = Guid.NewGuid().ToString("N") })).Success);
        Assert.False((await f.Runtime.ExecuteAsync(renewal with { Sequence = 2, Payload = CoolingServiceProtocol.Payload(new { Duty = 100 }) })).Success);
        f.LeaseClock.Advance(5);
        Assert.False((await f.Runtime.ExecuteAsync(renewal with { Sequence = 3 })).Success);
        await f.Runtime.SweepAsync(); Assert.False(f.Runtime.HasOwner); Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task FailedExitDoesNotPreventOtherRestorationsOrPermitANewOwnerUntilRecovery()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); await f.Call("control", f.Manual(55));
        await f.Call("board-manual", new ServiceBoardCommand(MotherboardFans.Cpu.Id, f.Runtime.SettingsRevision, 40));
        f.Hub.Disconnected = true; await f.Runtime.DisconnectAsync(f.Hello.Session);
        Assert.NotNull(f.Runtime.LifecycleError); Assert.Equal(65, f.Drivers[MotherboardFans.Cpu.Id].Duty);
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.OpenSessionAsync(ServiceFixture.Sid));
        f.Hub.Disconnected = false; await f.Runtime.SweepAsync(); Assert.Null(f.Runtime.LifecycleError);
        Assert.NotNull(await f.Runtime.OpenSessionAsync(ServiceFixture.Sid));
    }
    [Fact] public async Task EveryMotherboardRestoreIsAttemptedEvenWhenOnePortFails()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        foreach (var p in MotherboardFans.Ports.Take(2)) await f.Call("board-manual", new ServiceBoardCommand(p.Id, f.Runtime.SettingsRevision, 40));
        f.Drivers[MotherboardFans.Cpu.Id].RestoreFails = true; await f.Runtime.DisconnectAsync(f.Hello.Session);
        Assert.NotNull(f.Runtime.LifecycleError); Assert.Equal(65, f.Drivers[MotherboardFans.Chassis.Id].Duty);
        f.Drivers[MotherboardFans.Cpu.Id].RestoreFails = false; await f.Runtime.SweepAsync(); Assert.Null(f.Runtime.LifecycleError);
    }
    [Fact] public async Task AutomaticCoolingUsesServiceSensorUpdatesWithoutGuiCommands()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var profile = f.Runtime.Coordinator.Settings.Profiles[1];
        var request = f.Manual(55) with { Command = "SelectAutoProfile", ProfileId = profile.Id, ProfileRevision = profile.Revision };
        var reply = await f.Call("control", request); Assert.Equal(Outcome.Confirmed, reply.Payload.Deserialize<OperationResult>(Contract.Json)!.Outcome);
        var before = f.Hub.Writes; f.Temperatures.Cpu = 110; await f.Runtime.Coordinator.TickAsync();
        Assert.True(f.Hub.Writes > before); Assert.Equal(FanMode.Fallback, f.Runtime.Coordinator.Snapshot.Mode); Assert.Equal(100, f.Runtime.Coordinator.Snapshot.ConfirmedDuty);
    }
    [Fact] public async Task ConfigurationCannotChangeManagingPortOrBypassSettingsRevisions()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var revision = f.Runtime.SettingsRevision;
        var configuration = ServiceFanConfiguration.From(f.Runtime.Coordinator.Settings);
        Assert.False((await f.Call("configure", new ServiceConfigure(configuration, revision - 1))).Success);
        Assert.True((await f.Call("board-manual", new ServiceBoardCommand(MotherboardFans.Cpu.Id, revision, 45))).Success);
        Assert.False((await f.Call("configure", new ServiceConfigure(configuration with { CpuFan = new(true, 40) }, revision))).Success);
        Assert.Equal(20, f.Runtime.Coordinator.Settings.CpuFan.Minimum); Assert.Equal(45, f.Drivers[MotherboardFans.Cpu.Id].Duty);
        // Returning ownership is always allowed even when the client's edit revision is stale.
        Assert.True((await f.Call("board-restore", new ServiceBoardCommand(MotherboardFans.Cpu.Id, -1))).Success); Assert.Equal(65, f.Drivers[MotherboardFans.Cpu.Id].Duty);
    }
    [Fact] public async Task ConfigurationNeverTrustsAnExecutableFieldOrClientSuppliedHidPath()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var configuration = ServiceFanConfiguration.From(f.Runtime.Coordinator.Settings);
        var malicious = configuration with { Hardware = configuration.Hardware with { Hub = configuration.Hardware.Hub! with { Path = "evil.exe" } } };
        Assert.True((await f.Call("configure", new ServiceConfigure(malicious, f.Runtime.SettingsRevision))).Success);
        Assert.Equal(DemoHub.Identity.Path, f.Runtime.Coordinator.Settings.Hardware.Hub!.Path);
        var request = f.Request("configure", new ServiceConfigure(configuration, f.Runtime.SettingsRevision));
        var json = request.Payload.GetRawText().Replace("\"configuration\":{", "\"configuration\":{\"pythonPath\":\"evil.exe\",");
        var bad = request with { Payload = JsonDocument.Parse(json).RootElement.Clone() };
        Assert.False((await f.Runtime.ExecuteAsync(bad)).Success); Assert.Equal(0, f.Hub.Writes);
        Assert.False((await f.Call("open", new ServiceOpenHub("unknown-device"))).Success);
    }
    [Fact] public async Task OutputRequestsUseSavedPortLimitsAndCannotAddressAnUnknownPort()
    {
        await using var f = new ServiceFixture(); await f.Initialize();
        Assert.False((await f.Call("board-manual", new ServiceBoardCommand(MotherboardFans.Cpu.Id, f.Runtime.SettingsRevision, 1))).Success);
        Assert.False((await f.Call("board-manual", new ServiceBoardCommand("raw-register", f.Runtime.SettingsRevision, 80))).Success);
        Assert.All(f.Drivers.Values, d => Assert.Equal(0, d.Writes));
        var invalid = await f.Call("control", f.Manual(1)); Assert.Equal(Outcome.Rejected, invalid.Payload.Deserialize<OperationResult>(Contract.Json)!.Outcome);
        Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public async Task LightingFramesCannotChangeTheirLeasedPortsOrAffectCooling()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); await f.Call("control", f.Manual(55)); var before = f.Runtime.Coordinator.Snapshot;
        var current = f.Runtime.Coordinator.Snapshot;
        var begin = new ServiceLightBegin("demo", ["led1"], current.LightingRevision, current.DeviceGeneration);
        var start = await f.Call("light-begin", begin);
        Assert.False((await f.Call("light-begin", begin)).Success);
        var lease = start.Payload.Deserialize<LightingLease>(Contract.Json)!;
        Assert.False((await f.Call("light-frame", new ServiceLightFrame(lease with { Channels = ["led2"] }, new(1, 2, 3)))).Success);
        Assert.True((await f.Call("light-frame", new ServiceLightFrame(lease, new(30, 40, 200)))).Success);
        Assert.Equal(before.ControlRevision, f.Runtime.Coordinator.Snapshot.ControlRevision); Assert.Equal(55, f.Runtime.Coordinator.Snapshot.ConfirmedDuty);
        await f.Call("light-end", lease); Assert.False((await f.Call("light-frame", new ServiceLightFrame(lease, new(1, 2, 3)))).Success);
    }
    [Fact] public async Task AReconnectDoesNotAdoptPreviousCommandsAndOriginalSensorTimestampsArePreserved()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var state = f.Runtime.State();
        Assert.Equal(f.Runtime.Coordinator.Snapshot.Sensors[0].Timestamp, state.Control.Sensors[0].Timestamp);
        var previous = f.Request("control", f.Manual(55)); await f.Runtime.ExecuteAsync(previous); await f.Runtime.DisconnectAsync(f.Hello.Session);
        var current = await f.Runtime.OpenSessionAsync(ServiceFixture.Sid); Assert.Equal(FanMode.Monitor, f.Runtime.State().Control.Mode);
        Assert.False((await f.Runtime.ExecuteAsync(previous)).Success); Assert.NotEqual(current.Session, previous.Session);
    }
    [Fact] public async Task EvictedRepliesCannotBeReplayedAndMissingResultsAreExplicitlyUnknown()
    {
        await using var f = new ServiceFixture(); await f.Initialize(); var first = f.Request("control", f.Manual(55)); await f.Runtime.ExecuteAsync(first);
        for (var i = 0; i < 129; i++) await f.Call<object?>("heartbeat", null);
        var count = f.Hub.Writes; Assert.False((await f.Runtime.ExecuteAsync(first)).Success); Assert.Equal(count, f.Hub.Writes);
        var query = await f.Call("result", new ServiceResultQuery(first.Id)); Assert.Equal(JsonValueKind.Null, query.Payload.ValueKind);
    }
    [Fact] public void ProtocolRejectsOversizedFramesUnknownVersionsDuplicateKeysAndUnboundedCommands()
    {
        var r = new CoolingServiceRequest(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 1, "state", CoolingServiceProtocol.Payload<object?>(null));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(r, Contract.Json); Assert.Equal(r.Id, CoolingServiceProtocol.Read(bytes).Id);
        Assert.Throws<IOException>(() => CoolingServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(r with { Version = 2 }, Contract.Json)));
        Assert.Throws<IOException>(() => CoolingServiceProtocol.Read(JsonSerializer.SerializeToUtf8Bytes(r with { Command = "run-process" }, Contract.Json)));
        Assert.Throws<IOException>(() => CoolingServiceProtocol.Read(new byte[CoolingServiceProtocol.MaximumFrameBytes + 1]));
        var json = Encoding.UTF8.GetString(bytes); Assert.Throws<JsonException>(() => CoolingServiceProtocol.Read(Encoding.UTF8.GetBytes(json.Insert(1, "\"Version\":1,"))));
    }
}
