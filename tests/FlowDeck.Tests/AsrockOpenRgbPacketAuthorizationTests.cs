using FlowDeck.Core;
using FlowDeck.Infrastructure;
using NativeRig = FlowDeck.Tests.AsrockOpenRgbNativePlanTests.Rig;
using static FlowDeck.Tests.AsrockOpenRgbNativeOperationTests;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbPacketAuthorizationTests
{
    private static AsrockOpenRgbRecoveryIdentity Identity => AsrockOpenRgbNativePlanTests.Identity;
    private sealed class Rig : IAsyncDisposable
    {
        public readonly NativeRig Native = new();
        public readonly OpenRgbSdkTestServer Server = new();
        public readonly AsrockPacketEmulator Packets = new();
        public readonly CancellationTokenSource Operation = new();
        public OpenRgbSdkConnection Connection = null!;
        public OpenRgbSdkSnapshot Snapshot = null!;
        public AsrockOpenRgbNativeExecutor Executor = null!;
        public Action<AsrockAuthorizationRequest>? AtRequest;
        public Func<AsrockAuthorizationRequest, AsrockAuthorizationRequest>? Transform;
        public Action? Guard;
        public Exception? Denied;
        public bool SkipAuthorization, WrongFinalResponse;
        public AsrockAuthorizationRequest? Last;
        private AsrockOpenRgbOperationReceipt receipt = Initial();
        public Rig()
        {
            Native.Device.Order = AsrockOpenRgbDetectionRecovery.DetectionOrder(Native.Before);
            Server.Devices.Clear(); Server.Devices.Add(Native.Sdk.Id, OpenRgbSdkModelServer.Encode(Native.Sdk));
            Server.Intercept = async packet =>
            {
                if (packet.Command == AsrockOpenRgbOperationReceipt.QueryCommand)
                { await Server.ReplyAsync(packet, packet.Command, Wire(receipt)); return true; }
                if (packet.Command is not (1050 or 1051 or 1101)) return false;
                var record = Native.Journal.Pending!; var before = receipt; uint ack = 0;
                try
                {
                    if (SkipAuthorization) receipt = Completed(record.Operation!.Mutation, before).After;
                    else receipt = Packets.Execute(record, before, Server, packet.Device, (request, ct) =>
                    {
                        Last = request; AtRequest?.Invoke(request);
                        Executor.Authorization.Authorize(Transform?.Invoke(request) ?? request, ct);
                    }, Native.Device.Exchange);
                    if (WrongFinalResponse) receipt.After.Response[17] ^= 1;
                }
                catch (Exception e)
                {
                    Denied = e; ack = 1;
                    var io = Packets.Io ?? before.After;
                    if (io.Stage is 1 or 2) io = io with { Faulted = true };
                    receipt = new(before.Sequence + 1, packet.Command, AsrockOpenRgbOperationStage.Failed, before.After.Copy(), io);
                }
                await Server.AckAsync(packet, ack); return true;
            };
        }
        public async Task<Rig> Open()
        {
            Connection = await OpenRgbSdkConnection.ConnectAsync(Server.Port, ownedHeadless: true, verifyPeer: (_, _) => { }, operationTimeout: TimeSpan.FromSeconds(2));
            Snapshot = Assert.Single(await Connection.ScanAsync());
            Executor = new(Connection, Snapshot.Target, Native.Plan, Native.Journal, Identity); return this;
        }
        public Task<AsrockOpenRgbExecution> Write() => Executor.SendAsync(Snapshot.Controller, new(null, 15, Snapshot.Controller.Modes[15]),
            Native.Plan.Exit(new LightingExitPolicy(LightingExitAction.Off)), () => Guard?.Invoke(), Operation.Token);
        public async ValueTask DisposeAsync()
        { if (Connection is not null) await Connection.DisposeAsync(); await Server.DisposeAsync(); Operation.Dispose(); Native.Dispose(); }
    }

    [Fact] public async Task ExactSequenceOnTheActualSdkSocketAdvancesTheDurableRecord()
    {
        await using var r = await new Rig().Open(); var checks = 0;
        r.AtRequest = _ => { checks++; Assert.NotNull(r.Native.Journal.Pending!.Operation); };
        var result = await r.Write(); Assert.True(result.Completed);
        Assert.Equal(r.Native.Device.Writes.Count * 3, checks); Assert.Equal(12, checks);
        Assert.Null(r.Native.Journal.Pending!.Operation); Assert.Null(r.Denied);
        Assert.True(r.Packets.Io!.Same(result.After.After));
    }

    public static TheoryData<string> Alterations => new()
    { "path", "not-sdk", "protocol", "command", "controller", "server-port", "peer-port", "sequence-zero", "sequence-max", "phase",
        "attempted", "completed", "faulted", "stage", "write-result", "read-result", "packet", "response", "response-length" };
    private static AsrockAuthorizationRequest Alter(AsrockAuthorizationRequest r, string change) => change switch
    {
        "path" => r with { Path = r.Path + "-other" },
        "not-sdk" => r with { Origin = r.Origin with { Sdk = false } },
        "protocol" => r with { Origin = r.Origin with { Protocol = 5 } },
        "command" => r with { Origin = r.Origin with { Command = 1050 } },
        "controller" => r with { Origin = r.Origin with { Controller = r.Origin.Controller + 1 } },
        "server-port" => r with { Origin = r.Origin with { ServerPort = (ushort)(r.Origin.ServerPort - 1) } },
        "peer-port" => r with { Origin = r.Origin with { PeerPort = (ushort)(r.Origin.PeerPort - 1) } },
        "sequence-zero" => r with { Sequence = 0 }, "sequence-max" => r with { Sequence = ulong.MaxValue },
        "phase" => r with { Phase = AsrockAuthorizationPhase.BeforeRead },
        "attempted" => r with { Io = r.Io with { Attempted = r.Io.Attempted + 1 } },
        "completed" => r with { Io = r.Io with { Completed = r.Io.Completed - 1 } },
        "faulted" => r with { Io = r.Io with { Faulted = true } },
        "stage" => r with { Io = r.Io with { Stage = 2 } },
        "write-result" => r with { Io = r.Io with { WriteResult = 65 } },
        "read-result" => r with { Io = r.Io with { ReadResult = 64 } },
        "packet" => r with { Io = r.Io with { Request = r.Io.Request.Select((b, i) => i == 5 ? (byte)(b ^ 1) : b).ToArray() } },
        "response" => r with { Io = r.Io with { Response = Enumerable.Repeat((byte)1, 64).ToArray() } },
        "response-length" => r with { Io = r.Io with { Response = new byte[63] } },
        _ => throw new ArgumentException(change)
    };
    [Theory] [MemberData(nameof(Alterations))]
    public async Task DifferentTargetSocketOrPacketCannotBorrowTheActivePermission(string change)
    {
        await using var r = await new Rig().Open(); r.Transform = request => Alter(request, change);
        await Assert.ThrowsAnyAsync<IOException>(r.Write);
        Assert.IsType<IOException>(r.Denied); Assert.Empty(r.Native.Device.Writes);
        Assert.NotNull(r.Native.Journal.Pending!.Operation); Assert.True(r.Executor.NeedsRecovery);
        await Assert.ThrowsAsync<IOException>(r.Write); Assert.Single(r.Server.Requests, p => p.Command == 1101);
    }
    [Theory] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task OwnershipRevokedAtAnyPhaseStopsTheRemainingNativePackets(int phaseValue)
    {
        var phase = (AsrockAuthorizationPhase)phaseValue;
        await using var r = await new Rig().Open(); var revoked = false;
        r.Guard = () => { if (revoked) throw new IOException("ownership revoked"); };
        r.AtRequest = request => { if (request.Phase == phase) revoked = true; };
        await Assert.ThrowsAnyAsync<IOException>(r.Write);
        Assert.Equal("ownership revoked", r.Denied!.Message);
        Assert.Equal(phase == AsrockAuthorizationPhase.BeforeWrite ? 0 : 1, r.Native.Device.Writes.Count);
        Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task DuplicateRequestPoisonsPermissionBeforeTheNextRead()
    {
        await using var r = await new Rig().Open(); AsrockAuthorizationRequest? first = null;
        r.Transform = request => first ??= request;
        await Assert.ThrowsAnyAsync<IOException>(r.Write); Assert.IsType<IOException>(r.Denied);
        Assert.Single(r.Native.Device.Writes); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ChangingAnyLaterPacketStopsAtItsExactPrefix(int packetIndex)
    {
        await using var r = await new Rig().Open();
        r.Transform = request => request.Io.Attempted == (ulong)(9 + packetIndex) && request.Phase == AsrockAuthorizationPhase.BeforeWrite
            ? Alter(request, "packet") : request;
        await Assert.ThrowsAsync<IOException>(r.Write); Assert.IsType<IOException>(r.Denied);
        Assert.Equal(packetIndex, r.Native.Device.Writes.Count); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Theory] [InlineData(3, "stage")] [InlineData(3, "read-result")] [InlineData(3, "response")]
    [InlineData(4, "stage")] [InlineData(4, "read-result")] [InlineData(4, "write-result")]
    public async Task EachReadBoundaryRequiresTheExactCompletedLengthsAndStage(int phase, string change)
    {
        await using var r = await new Rig().Open();
        r.Transform = request => request.Phase != (AsrockAuthorizationPhase)phase ? request : request with { Io = change switch
        {
            "stage" => request.Io with { Stage = 3 },
            "read-result" => request.Io with { ReadResult = 63 },
            "write-result" => request.Io with { WriteResult = 64 },
            _ => request.Io with { Response = Enumerable.Repeat((byte)1, 64).ToArray() }
        } };
        await Assert.ThrowsAsync<IOException>(r.Write); Assert.IsType<IOException>(r.Denied);
        Assert.Single(r.Native.Device.Writes); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task ValidSyntaxAndSdkAcknowledgementWithoutAnyNativeAuthorizationAreNotCompletion()
    {
        await using var r = await new Rig().Open(); r.SkipAuthorization = true;
        await Assert.ThrowsAsync<IOException>(r.Write);
        Assert.Empty(r.Native.Device.Writes); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task CompletionMustMatchTheActuallyAuthorizedFinalResponse()
    {
        await using var r = await new Rig().Open(); r.WrongFinalResponse = true;
        await Assert.ThrowsAsync<IOException>(r.Write);
        Assert.Equal(4, r.Native.Device.Writes.Count); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task ChangedDurableFileDeniesEvenWhenTheInMemoryPlanMatches()
    {
        await using var r = await new Rig().Open();
        r.AtRequest = _ => File.WriteAllText(Assert.Single(Directory.GetFiles(r.Native.Directory.Path, "openrgb-asrock-detection-*.json")), "{}");
        await Assert.ThrowsAnyAsync<Exception>(r.Write); Assert.NotNull(r.Denied); Assert.Empty(r.Native.Device.Writes);
    }
    [Fact] public async Task CancellationDuringPermissionCheckDoesNotGrantTheProposedWrite()
    {
        await using var r = await new Rig().Open();
        r.AtRequest = _ => r.Guard = () => r.Operation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(r.Write);
        Assert.Empty(r.Native.Device.Writes); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task CompletedPermitCannotBeReused()
    {
        await using var r = await new Rig().Open(); await r.Write();
        Assert.Throws<IOException>(() => r.Executor.Authorization.Authorize(r.Last! with { Sequence = r.Last!.Sequence + 1 }, default));
        Assert.Null(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task ASecondArmingCannotReplaceTheInFlightPlan()
    {
        await using var r = await new Rig().Open();
        r.AtRequest = _ => Assert.Throws<IOException>(() => r.Executor.Authorization.Arm(r.Connection, r.Snapshot.Target,
            r.Native.Journal.Pending!.Hash, () => { }, default));
        Assert.True((await r.Write()).Completed); Assert.Null(r.Denied);
    }
    [Fact] public async Task ExternalPermissionChecksDoNotHoldTheRecoveryJournalLock()
    {
        await using var r = await new Rig().Open();
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var once = 0;
        r.AtRequest = _ => r.Guard = () =>
        {
            if (Interlocked.Increment(ref once) != 1) return;
            entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        };
        var write = r.Write();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            // A concurrent SDK before-send check must be able to acquire this journal even while
            // the pipe's external guard is waiting for that SDK's connection lock.
            var read = Task.Run(() => r.Native.Journal.Pending);
            Assert.NotNull(await read.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { resume.Set(); }
        Assert.True((await write).Completed);
    }
    [Fact] public async Task ConcurrentRequestsRevokeBothInsteadOfConsumingOnePermissionTwice()
    {
        await using var r = await new Rig().Open();
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); var once = 0;
        r.AtRequest = _ => r.Guard = () =>
        {
            if (Interlocked.Increment(ref once) != 1) return;
            entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        };
        var write = r.Write();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            Assert.Throws<IOException>(() => r.Executor.Authorization.Authorize(r.Last!, default));
        }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<IOException>(() => write);
        Assert.Empty(r.Native.Device.Writes); Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Fact] public async Task ExpiredPipeRequestCannotGrantPermissionAfterTheExternalCheckReturns()
    {
        await using var r = await new Rig().Open(); using var deadline = new CancellationTokenSource();
        r.AtRequest = request =>
        {
            r.Guard = () => deadline.Cancel();
            r.Executor.Authorization.Authorize(request, deadline.Token);
        };
        await Assert.ThrowsAsync<IOException>(r.Write);
        Assert.IsAssignableFrom<OperationCanceledException>(r.Denied); Assert.Empty(r.Native.Device.Writes);
        Assert.NotNull(r.Native.Journal.Pending!.Operation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreparedOperationNeedsBothStartAndItsActualInFlightSdkRequest(bool start)
    {
        await using var r = await new Rig().Open();
        var mutation = r.Native.Plan.Prepare(r.Native.Journal.Pending!.Expected, r.Snapshot.Controller,
            new(null, 15, r.Snapshot.Controller.Modes[15]));
        var record = r.Native.Journal.PrepareOperation(Identity, mutation, Initial(), r.Native.Plan.Exit(new LightingExitPolicy(LightingExitAction.Off)), () => { });
        using var lease = r.Executor.Authorization.Arm(r.Connection, r.Snapshot.Target, record.Hash, () => { }, default);
        if (start) lease.Start();
        var io = new AsrockOpenRgbIoReceipt(9, 8, 1, false, -2, -2, mutation.Packets[0], new byte[64]);
        var request = new AsrockAuthorizationRequest(1, AsrockAuthorizationPhase.BeforeWrite, record.Before.Identity,
            r.Connection.AuthorizationOrigin(r.Snapshot.Target, mutation.Command), io);
        Assert.Throws<IOException>(() => r.Executor.Authorization.Authorize(request, default));
        Assert.Empty(r.Native.Device.Writes);
    }
}
