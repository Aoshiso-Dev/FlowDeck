using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbLightingExecutorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FlowDeck-OpenRgb-execution-" + Guid.NewGuid().ToString("N"));
    private const string Id = "openrgb:executor", Boot = "test-boot";
    private static readonly Action Authorized = () => { };
    private static readonly LightingExitPolicy Original = new(LightingExitAction.RestoreOriginal);
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 255)]);
    private static readonly LightingProgram Green = new("fixed", [new(0, 255, 0)]);
    private static OpenRgbSdkController Device() => OpenRgbLightingPlanTests.Split() with { ActiveMode = 1 };
    public void Dispose()
    {
        // Only flat journal files belonging to this newly created test directory.
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
    private sealed record Session(OpenRgbSdkModelServer Server, OpenRgbSdkConnection Connection, OpenRgbLightingRecovery Journal,
        OpenRgbRecoveryScope Scope, OpenRgbLightingExecutor Executor) : IAsyncDisposable
    {
        public OpenRgbSdkController Device => Server.Devices[Scope.Target.ControllerId];
        public async ValueTask DisposeAsync() { await Connection.DisposeAsync(); await Server.DisposeAsync(); }
    }
    private async Task<Session> Start(OpenRgbSdkController? device = null, int? zone = null,
        IdentityConfidence confidence = IdentityConfidence.ConnectionBound, Action? verifyBinding = null)
    {
        var server = new OpenRgbSdkModelServer(device ?? Device());
        OpenRgbSdkConnection? connection = null;
        try
        {
            connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
            var target = Assert.Single(await connection.ScanAsync()).Target;
            var scope = new OpenRgbRecoveryScope(new(Id, "serial:" + new string('a', 64), "openrgb-sdk6", target.Structure),
                target, confidence, Guid.NewGuid().ToString("N"), zone);
            var journal = new OpenRgbLightingRecovery(directory, Id, zone);
            return new(server, connection, journal, scope, new(connection, journal, scope, Boot, verifyBinding ?? Authorized));
        }
        catch { if (connection is not null) await connection.DisposeAsync(); await server.DisposeAsync(); throw; }
    }
    private static void Written(OpenRgbLightingExecution result) => Assert.True(result.Result.Outcome == Outcome.WrittenUnverified, result.Result.Message);
    private static void Same(OpenRgbLightingState expected, OpenRgbSdkController actual) =>
        Assert.True(OpenRgbLightingPlan.Same(expected, OpenRgbLightingPlan.State(actual)));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task JournalExistsBeforeEveryWriteAndEchoDoesNotCountAsPhysicalConfirmation(bool echo)
    {
        await using var s = await Start(); var original = OpenRgbLightingPlan.State(s.Device);
        s.Server.Echo = echo; var checkedWrites = 0;
        s.Server.BeforeWrite = _ =>
        {
            var disk = new OpenRgbLightingRecovery(directory, Id, null).Pending;
            Assert.NotNull(disk); Assert.Equal(s.Journal.Pending!.Hash, disk.Hash);
            checkedWrites++; return Task.CompletedTask;
        };
        var result = await s.Executor.ApplyAsync(Blue, Original, Authorized); Written(result);
        Assert.Equal(SettingsTransfer.Hash(Blue), SettingsTransfer.Hash(result.Result.ReportedLighting!)); Assert.Equal(2, s.Server.WriteCount);
        Assert.All(s.Device.Colors, color => Assert.Equal(new RgbColor(0, 0, 255), color));
        Written(await s.Executor.RecoverAsync(Authorized)); Same(original, s.Device);
        Assert.Equal(s.Server.WriteCount, checkedWrites); Assert.Null(s.Journal.Pending); Assert.Empty(Directory.GetFiles(directory));
        Assert.DoesNotContain(s.Server.Wire.Requests, p => p.Command is 1000 or 1102 or 140);
    }

    [Fact]
    public async Task NativeParametersAndPreviouslyInactiveModeAreRestored()
    {
        await using var s = await Start(Device() with { ActiveMode = 0 }); var original = OpenRgbLightingPlan.State(s.Device);
        var effect = OpenRgbLightingProfile.Surface(s.Device, null).Native[1].Effect;
        Written(await s.Executor.ApplyAsync(new(effect.Id, [new(100, 60, 20)], Speed: 50, Reverse: true, Brightness: 50), Original, Authorized));
        Assert.Equal(1, s.Device.ActiveMode); Assert.Equal(51u, s.Device.Modes[1].Speed); Assert.Equal(1u, s.Device.Modes[1].Direction);
        Assert.Equal(new RgbColor(50, 30, 10), Assert.Single(s.Device.Modes[1].Colors));
        Written(await s.Executor.RecoverAsync(Authorized)); Same(original, s.Device);
    }

    [Fact]
    public async Task SuccessfulAckWithUnchangedReportedStateStopsTheRemainingWrites()
    {
        await using var s = await Start(); var original = OpenRgbLightingPlan.State(s.Device); s.Server.IgnoreWriteNumber = 1;
        var result = await s.Executor.ApplyAsync(Blue, Original, Authorized);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.Equal(1, s.Server.WriteCount); Same(original, s.Device);
        Assert.NotNull(s.Journal.Pending); s.Server.IgnoreWriteNumber = 0;
        Written(await s.Executor.RecoverAsync(Authorized)); Same(original, s.Device); Assert.Null(s.Journal.Pending);
    }

    [Theory]
    [InlineData(IdentityConfidence.ConnectionBound, false)]
    [InlineData(IdentityConfidence.SerialBound, true)]
    public async Task PartialSendOnDisconnectedSessionRequiresStrongBindingToRecover(IdentityConfidence confidence, bool allowed)
    {
        var original = OpenRgbLightingPlan.State(Device()); OpenRgbSdkController partial;
        await using (var s = await Start(confidence: confidence))
        {
            s.Server.DisconnectWriteNumber = 1;
            Assert.Equal(Outcome.Unknown, (await s.Executor.ApplyAsync(Blue, Original, Authorized)).Result.Outcome);
            Assert.Equal(1, s.Server.WriteCount); Assert.NotNull(s.Journal.Pending); partial = OpenRgbSdkProtocol.Copy(s.Device) with { Id = 901 };
        }
        await using var restarted = await Start(partial, confidence: confidence);
        var result = await restarted.Executor.RecoverAsync(Authorized);
        if (allowed) { Written(result); Same(original, restarted.Device); Assert.Null(restarted.Journal.Pending); }
        else { Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.Equal(0, restarted.Server.WriteCount); Assert.NotNull(restarted.Journal.Pending); }
    }

    [Fact]
    public async Task NewExecutorCannotAdoptAnUnfinishedOperationAsItsOwn()
    {
        await using var s = await Start(); Written(await s.Executor.ApplyAsync(Blue, Original, Authorized)); var count = s.Server.WriteCount;
        var second = new OpenRgbLightingExecutor(s.Connection, s.Journal, s.Scope, Boot, Authorized);
        Assert.Equal(Outcome.Rejected, (await second.ApplyAsync(Green, Original, Authorized)).Result.Outcome);
        Assert.Equal(count, s.Server.WriteCount); Written(await second.RecoverAsync(Authorized));
        Written(await second.ApplyAsync(Green, Original, Authorized)); Written(await second.RecoverAsync(Authorized));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExpiredBeforeFirstPacketRestoresThePreviousJournalWithoutOutput(bool previousOperation)
    {
        await using var s = await Start();
        if (previousOperation) Written(await s.Executor.ApplyAsync(Blue, Original, Authorized));
        var previous = s.Journal.Pending; var count = s.Server.WriteCount;
        void Authorize() { if (s.Journal.Pending?.Hash is { } hash && hash != previous?.Hash) throw new OpenRgbLightingExpiredException("expired frame"); }
        var result = await s.Executor.ApplyAsync(Green, Original, Authorize, frame: true);
        Assert.Equal(Outcome.Rejected, result.Result.Outcome); Assert.Equal(DeviceDriverFailure.LightingFrameExpired, result.Result.Failure);
        Assert.Equal(count, s.Server.WriteCount); Assert.Equal(previous?.Hash, s.Journal.Pending?.Hash);
        Written(await s.Executor.ApplyAsync(Green, Original, Authorized, frame: true)); Written(await s.Executor.RecoverAsync(Authorized));
    }

    [Fact]
    public async Task RevokedAuthorizationAfterOnePacketRetainsAnObligationAndDoesNotRetryWrites()
    {
        await using var s = await Start(); var original = OpenRgbLightingPlan.State(s.Device);
        void Authorize() { if (s.Server.WriteCount != 0) throw new IOException("authorization revoked"); }
        Assert.Equal(Outcome.Unknown, (await s.Executor.ApplyAsync(Blue, Original, Authorize)).Result.Outcome);
        Assert.Equal(1, s.Server.WriteCount); Assert.NotNull(s.Journal.Pending);
        Written(await s.Executor.RecoverAsync(Authorized)); Same(original, s.Device);
    }

    [Fact]
    public async Task SameDeviceReadRaceIsRetriedButNoWriteIsRepeated()
    {
        await using var s = await Start(); var once = false;
        s.Server.DuringRead = async packet =>
        {
            if (once) return; once = true;
            await s.Server.Wire.SendAsync(packet.Device, 1150, OpenRgbSdkTestServer.SignalController(1, OpenRgbSdkModelServer.Encode(s.Device)));
        };
        var before = s.Server.Wire.Requests.Count(p => p.Command == 1);
        Written(await s.Executor.ApplyAsync(Blue, Original, Authorized)); Assert.Equal(2, s.Server.WriteCount);
        Assert.Equal(4, s.Server.Wire.Requests.Count(p => p.Command == 1) - before);
        Written(await s.Executor.RecoverAsync(Authorized));
    }

    [Fact]
    public async Task AnotherDevicesNotificationsDoNotInvalidateARead()
    {
        await using var server = new OpenRgbSdkModelServer(Device(), Device() with { Id = 700, Name = "Independent" });
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port); var target = (await connection.ScanAsync())[0].Target;
        server.DuringRead = _ => server.Wire.SendAsync(700, 1150, OpenRgbSdkTestServer.SignalColors(0x000000ff, 0x000000ff));
        var snapshot = await connection.ReadAsync(target); Assert.Equal(target, snapshot.Target); Assert.Equal(0, server.WriteCount);
    }

    [Fact]
    public async Task RepeatedSameDeviceReadRacesAreBoundedAndNeverReachOutput()
    {
        await using var s = await Start();
        s.Server.DuringRead = packet => s.Server.Wire.SendAsync(packet.Device, 1150, OpenRgbSdkTestServer.SignalController(1, OpenRgbSdkModelServer.Encode(s.Device)));
        var before = s.Server.Wire.Requests.Count(p => p.Command == 1);
        Assert.Equal(Outcome.Rejected, (await s.Executor.ApplyAsync(Blue, Original, Authorized)).Result.Outcome);
        Assert.Equal(3, s.Server.Wire.Requests.Count(p => p.Command == 1) - before); Assert.Equal(0, s.Server.WriteCount); Assert.Null(s.Journal.Pending);
    }

    [Fact]
    public async Task ExternalChangesToTheSelectedScopeRejectFurtherOutputAndRecovery()
    {
        await using var s = await Start(zone: 0); Written(await s.Executor.ApplyAsync(Blue, Original, Authorized));
        var count = s.Server.WriteCount; var journal = s.Journal.Pending!.Hash; s.Device.Colors[0] = new(42, 42, 42);
        Assert.Equal(Outcome.Rejected, (await s.Executor.ApplyAsync(Green, Original, Authorized)).Result.Outcome);
        Assert.Equal(Outcome.Unknown, (await s.Executor.RecoverAsync(Authorized)).Result.Outcome);
        Assert.Equal(count, s.Server.WriteCount); Assert.Equal(journal, s.Journal.Pending!.Hash); Assert.Equal(new RgbColor(42, 42, 42), s.Device.Colors[0]);
    }

    [Fact]
    public async Task SiblingZoneChangesSurviveFurtherFramesAndOriginalRestoration()
    {
        await using var s = await Start(zone: 0); var original = OpenRgbLightingPlan.State(s.Device);
        Written(await s.Executor.ApplyAsync(Blue, Original, Authorized, frame: true));
        s.Device.Colors[1] = new(42, 42, 42); s.Device.Zones[1] = s.Device.Zones[1] with { ActiveMode = 1 };
        var count = s.Server.WriteCount; Written(await s.Executor.ApplyAsync(Green, Original, Authorized, frame: true));
        Assert.Equal(count + 1, s.Server.WriteCount); Assert.True(OpenRgbLightingPlan.Same(original, s.Journal.Pending!.Original));
        Written(await s.Executor.RecoverAsync(Authorized)); Assert.Equal(original.Colors[0], s.Device.Colors[0]); Assert.Equal(-1, s.Device.Zones[0].ActiveMode);
        Assert.Equal(new RgbColor(42, 42, 42), s.Device.Colors[1]); Assert.Equal(1, s.Device.Zones[1].ActiveMode);
        Assert.All(s.Server.Wire.Requests.Where(p => p.Command is 1050 or 1051 or 1101 or 1103), p => Assert.True(p.Command is 1051 or 1103));
    }

    [Fact]
    public async Task InterruptedNormalStopRetainsItsChosenTargetAcrossExecutorReload()
    {
        await using var s = await Start(); Written(await s.Executor.ApplyAsync(Blue, Original, Authorized)); var count = s.Server.WriteCount;
        void Authorize() { if (s.Server.WriteCount > count) throw new IOException("interrupt normal stop"); }
        Assert.Equal(Outcome.Unknown, (await s.Executor.RecoverAsync(Authorize, normalStop: Green)).Result.Outcome);
        Assert.Equal(count + 1, s.Server.WriteCount); Assert.Equal(OpenRgbRecoveryPhase.Recovering, s.Journal.Pending!.Phase);
        var reload = new OpenRgbLightingRecovery(directory, Id, null);
        var executor = new OpenRgbLightingExecutor(s.Connection, reload, s.Scope, Boot, Authorized);
        Written(await executor.RecoverAsync(Authorized)); Assert.Equal(0, s.Device.ActiveMode);
        Assert.All(s.Device.Colors, color => Assert.Equal(new RgbColor(0, 255, 0), color)); Assert.Null(reload.Pending);
    }

    [Fact]
    public async Task ReadLeasePreventsReplacingTheJournalWhilePacketIsInFlight()
    {
        await using var s = await Start(); var checkedLease = false;
        s.Server.BeforeWrite = _ =>
        {
            if (s.Server.WriteCount == 1)
            {
                Assert.ThrowsAny<IOException>(() => s.Journal.CancelUnsent(s.Journal.Pending!, null)); checkedLease = true;
            }
            return Task.CompletedTask;
        };
        Written(await s.Executor.ApplyAsync(Blue, Original, Authorized)); Assert.True(checkedLease); Written(await s.Executor.RecoverAsync(Authorized));
    }

    [Fact]
    public async Task QueuedCanceledRecoveryDoesNotFaultTheRunningOperation()
    {
        await using var s = await Start(); var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Server.BeforeWrite = async _ => { if (s.Server.WriteCount == 1) { waiting.SetResult(); await resume.Task.WaitAsync(s.Server.Wire.Stopping); } };
        var operation = s.Executor.ApplyAsync(Blue, Original, Authorized);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(4)); using var cancellation = new CancellationTokenSource();
            var recovery = s.Executor.RecoverAsync(Authorized, cancellation.Token); cancellation.Cancel();
            Assert.Equal(Outcome.Rejected, (await recovery).Result.Outcome);
        }
        finally { resume.TrySetResult(); }
        Written(await operation); Written(await s.Executor.ApplyAsync(Green, Original, Authorized)); Written(await s.Executor.RecoverAsync(Authorized));
    }

    [Fact]
    public async Task InvalidPhysicalBindingNeverReachesTheTransport()
    {
        await using var s = await Start(verifyBinding: () => throw new IOException("device replaced"));
        Assert.Equal(Outcome.Rejected, (await s.Executor.ApplyAsync(Blue, Original, Authorized)).Result.Outcome);
        Assert.Equal(0, s.Server.WriteCount); Assert.Null(s.Journal.Pending);
    }

    [Fact]
    public async Task PersistedAcceptedRecoveryOnlyCleansUpEvenAfterDisconnect()
    {
        await using var s = await Start(); Written(await s.Executor.ApplyAsync(Blue, Original, Authorized));
        var recovery = s.Journal.PrepareRecovery(s.Journal.Pending!, Boot, s.Scope.Binding, s.Scope.Target, s.Device);
        foreach (var step in recovery.Mutation.Steps)
            await OpenRgbLightingPlan.SendStepAsync(s.Connection, s.Scope.Target, step, Authorized, default);
        var reported = await s.Connection.ReadAsync(s.Scope.Target);
        s.Journal.AcceptRecovery(recovery, OpenRgbLightingPlan.State(reported.Controller), recovery.Mutation.Steps.Length);
        await s.Connection.DisposeAsync(); var count = s.Server.WriteCount;
        var reload = new OpenRgbLightingRecovery(directory, Id, null);
        var executor = new OpenRgbLightingExecutor(s.Connection, reload, s.Scope, Boot, () => throw new IOException("offline"));
        Written(await executor.RecoverAsync(Authorized)); Assert.Equal(count, s.Server.WriteCount); Assert.Null(reload.Pending);
    }
}
