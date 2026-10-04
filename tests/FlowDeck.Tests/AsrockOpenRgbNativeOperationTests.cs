using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rig = FlowDeck.Tests.AsrockOpenRgbNativePlanTests.Rig;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbNativeOperationTests
{
    private static AsrockOpenRgbRecoveryIdentity Identity => AsrockOpenRgbNativePlanTests.Identity;
    private static readonly Action Guard = () => { };
    internal static AsrockOpenRgbOperationReceipt Initial()
    {
        var request = new byte[65]; request[1] = 0x11; request[3] = 6;
        var response = new byte[64]; response[3] = 6;
        var io = new AsrockOpenRgbIoReceipt(8, 8, 3, false, 65, 64, request, response);
        return new(0, 0, AsrockOpenRgbOperationStage.Idle, io, io.Copy());
    }
    internal static AsrockOpenRgbExecution Completed(AsrockOpenRgbNativeOperation operation, AsrockOpenRgbOperationReceipt? previous = null,
        OpenRgbSdkTarget? target = null)
    {
        previous ??= Initial();
        var count = previous.After.Attempted + (ulong)operation.Packets.Length;
        var io = new AsrockOpenRgbIoReceipt(count, count, 3, false, 65, 64, operation.Packets[^1].ToArray(), new byte[64]);
        return new(target!, operation.Command, 0, previous, new(previous.Sequence + 1, operation.Command, AsrockOpenRgbOperationStage.Completed, previous.After.Copy(), io));
    }
    internal static byte[] Wire(AsrockOpenRgbOperationReceipt receipt)
    {
        object Io(AsrockOpenRgbIoReceipt io) => new { attempted = io.Attempted, completed = io.Completed, stage = io.Stage, faulted = io.Faulted,
            writeResult = io.WriteResult, readResult = io.ReadResult, request = io.Request.Select(b => (int)b), response = io.Response.Select(b => (int)b) };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 1, sequence = receipt.Sequence, command = receipt.Command,
            stage = (int)receipt.Stage, before = Io(receipt.Before), after = Io(receipt.After) }) + "\0");
    }
    private static OpenRgbLightingStep Mode(Rig r, int mode = 15) => new(null, mode, r.Sdk.Modes[mode]);
    private static AsrockRgbSnapshot Start(Rig r)
    { r.Device.Order = AsrockOpenRgbDetectionRecovery.DetectionOrder(r.Before); return r.Journal.Pending!.Expected; }
    private static Dictionary<byte, BoardLightSetting> Exit(Rig r) => r.Plan.Exit(new LightingExitPolicy(LightingExitAction.FixedColor, new(4, 80, 170)));
    private static void AssertRestored(Rig r, AsrockOpenRgbDetectionRecovery journal)
    {
        Assert.True(journal.Restore(Identity, r.Device.Exchange, Guard).Restored);
        Assert.Equal(r.Before.Order, r.Device.Order);
        var actual = new AsrockVolatileTransaction(r.Before.Identity, r.Device.Exchange).Snapshot;
        Assert.All(actual.Zones, z => Assert.Equal(Exit(r)[z.Address], z.Setting)); Assert.Null(journal.Pending);
    }

    [Theory]
    [InlineData(15, 0)] [InlineData(15, 1)] [InlineData(15, 2)] [InlineData(15, 3)] [InlineData(15, 4)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)] [InlineData(1, 4)]
    [InlineData(6, 0)] [InlineData(6, 1)] [InlineData(6, 2)] [InlineData(6, 3)] [InlineData(6, 4)]
    public void EachWholeModePrefixIsDurableAndRestoresAfterLosingTheSdkResult(int mode, int completedPackets)
    {
        using var r = new Rig(emptyFirst: true, emptyMiddle: true);
        var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r, mode));
        r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard);
        foreach (var packet in operation.Packets.Take(completedPackets)) r.Device.Exchange(packet);
        if (completedPackets > 0)
        {
            // Readable Direct registers are valid evidence of a prefix, not an original pixel backup.
            var read = r.Before with { Order = r.Device.Order, Zones = r.Before.Zones.Select(z => z with
            { Setting = AsrockRgbControl.Decode(r.Device.Exchange(AsrockRgbMonitor.Query(0x11, z.Address))[1..], r.Device.Order) }).ToArray() };
            Assert.True(AsrockOpenRgbNativeOperation.Same(operation.Prefixes[completedPackets - 1], read));
        }
        var reopened = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Identity.DeviceId);
        AssertRestored(r, reopened);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ColorOnlyWritesPreserveMixedNativeModesAndSpeedsAtEveryPrefix(int prefix)
    {
        using var r = new Rig(); var start = Start(r);
        var step = new OpenRgbLightingStep(null, Colors: Enumerable.Repeat(new RgbColor(7, 140, 210), r.Sdk.Colors.Length).ToArray());
        var operation = r.Plan.Prepare(start, r.Sdk, step);
        foreach (var z in operation.After.Zones)
        {
            var original = r.Before.Zones.Single(p => p.Address == z.Address).Setting;
            Assert.Equal(original.Mode, z.Setting.Mode); Assert.Equal(original.Speed, z.Setting.Speed);
        }
        r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard);
        foreach (var packet in operation.Packets.Take(prefix)) r.Device.Exchange(packet);
        AssertRestored(r, new(r.Directory.Path, Identity.DeviceId));
    }
    [Fact] public void WholeModePlanMatchesTheSeparatePinnedImplementationFixture()
    {
        using var r = new Rig(emptyFirst: true); var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r));
        r.CompleteModeUpdate(r.Sdk with { ActiveMode = 15 });
        Assert.Equal(operation.Packets.Length, r.Device.Writes.Count);
        for (var i = 0; i < operation.Packets.Length; i++) Assert.Equal(r.Device.Writes[i], operation.Packets[i]);
    }
    [Fact] public void NonPrefixCombinationCannotBorrowOtherZonesFromDifferentPlannedStates()
    {
        using var r = new Rig(); var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r));
        r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard);
        r.Device.Exchange(operation.Packets[0]); r.Device.Exchange(operation.Packets[2]); r.Device.Writes.Clear();
        Assert.Throws<IOException>(() => r.Journal.Restore(Identity, r.Device.Exchange, Guard));
        Assert.Empty(r.Device.Writes); Assert.NotNull(r.Journal.Pending!.Operation);
    }
    [Fact] public void DirectFramesKeepNativeRegistersAndUseConfiguredMaximaForPadding()
    {
        using var r = new Rig(emptyFirst: true, emptyMiddle: true); var step = Mode(r);
        var entering = r.Plan.Prepare(Start(r), r.Sdk, step);
        var sdk = OpenRgbLightingPlan.WithState(r.Sdk, OpenRgbLightingPlan.Build(r.Sdk, OpenRgbLightingPlan.State(r.Sdk), [step]).After);
        var colors = new OpenRgbLightingStep(2, Colors: Enumerable.Repeat(new RgbColor(90, 12, 33), (int)sdk.Zones[2].LedCount).ToArray());
        // Index 2 is the deliberately empty middle zone; it must not produce any write.
        Assert.Throws<IOException>(() => r.Plan.Prepare(entering.After, sdk, colors));
        colors = new(3, Colors: Enumerable.Repeat(new RgbColor(90, 12, 33), (int)sdk.Zones[3].LedCount).ToArray());
        var operation = r.Plan.Prepare(entering.After, sdk, colors);
        Assert.All(operation.Prefixes, state => Assert.True(AsrockOpenRgbNativeOperation.Same(entering.After, state)));
        var paddedBytes = ((int)sdk.Zones.Sum(z => (long)z.MaximumLeds) + 8) * 3;
        Assert.Equal(1 + (int)Math.Ceiling((paddedBytes - 54) / 57d), operation.Packets.Length);
        var bytes = operation.Packets.SelectMany((p, i) => p.Skip(i == 0 ? 9 : 5).Take(i == 0 ? 54 : 57)).Take(paddedBytes).ToArray();
        var offset = OpenRgbLightingPlan.Offset(sdk, 3) * 3;
        Assert.Equal(new byte[] { 12, 90, 33 }, bytes[offset..(offset + 3)]);
        Assert.Equal(new byte[] { 10, 9, 11 }, bytes[..3]);
        Assert.All(bytes.Skip(sdk.Colors.Length * 3), value => Assert.Equal(0, value));
    }
    [Fact] public void AcknowledgedOperationRequiresExactNativeCountAndFinalPacketBeforeAdvancing()
    {
        using var r = new Rig(); var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r));
        var prepared = r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard); var good = Completed(operation);
        var wrongCount = good with { After = good.After with { After = good.After.After with { Attempted = 9, Completed = 9 } } };
        Assert.Throws<IOException>(() => r.Journal.CompleteOperation(Identity, prepared.Hash, wrongCount, Guard));
        var changed = good.After.After.Request.ToArray(); changed[5]++;
        var wrongPacket = good with { After = good.After with { After = good.After.After with { Request = changed } } };
        Assert.Throws<IOException>(() => r.Journal.CompleteOperation(Identity, prepared.Hash, wrongPacket, Guard));
        Assert.NotNull(r.Journal.Pending!.Operation);
        r.Journal.CompleteOperation(Identity, prepared.Hash, good, Guard); Assert.Null(r.Journal.Pending!.Operation);
        Assert.True(AsrockOpenRgbNativeOperation.Same(operation.After, r.Journal.Pending.Expected));
        Assert.Throws<IOException>(() => r.Journal.PrepareExit(Identity, operation.After, Exit(r), Guard));
        Assert.Throws<IOException>(() => r.Journal.PrepareOperation(Identity, operation, good.After, Exit(r), Guard));
    }
    [Fact] public void FailedReceiptLeavesAllKnownPrefixesAndRejectsAnotherOperation()
    {
        using var r = new Rig(); var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r));
        var prepared = r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard); var good = Completed(operation);
        var failure = good with { Acknowledgement = 1, After = good.After with { Stage = AsrockOpenRgbOperationStage.Failed } };
        Assert.Throws<IOException>(() => r.Journal.CompleteOperation(Identity, prepared.Hash, failure, Guard));
        Assert.Throws<IOException>(() => r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard));
        Assert.Equal(prepared.Hash, r.Journal.Pending!.Hash); AssertRestored(r, r.Journal);
    }
    [Fact] public void FailedCompletionSaveRetainsTheWriteAheadObligation()
    {
        using var r = new Rig(); var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r)); var writes = 0;
        var journal = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Identity.DeviceId, (path, value) =>
        { if (++writes == 2) throw new IOException("disk full"); ServiceEnvironment.Write(path, value); });
        var record = journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard);
        foreach (var p in operation.Packets) r.Device.Exchange(p);
        Assert.Throws<IOException>(() => journal.CompleteOperation(Identity, record.Hash, Completed(operation), Guard));
        Assert.NotNull(journal.Pending!.Operation); AssertRestored(r, new(r.Directory.Path, Identity.DeviceId));
    }
    [Fact] public void PrefixSelectionIsSavedBeforeAnyRecoveryWriteAndPartialRestoreCanBeRetried()
    {
        using var r = new Rig(); var operation = r.Plan.Prepare(Start(r), r.Sdk, Mode(r));
        r.Journal.PrepareOperation(Identity, operation, Initial(), Exit(r), Guard);
        foreach (var p in operation.Packets.Take(3)) r.Device.Exchange(p);
        r.Device.BeforeWrite = p => { if (p[1] == 0x10 && p[3] == 5) throw new IOException("zone failed"); };
        Assert.False(r.Journal.Restore(Identity, r.Device.Exchange, Guard).Restored);
        Assert.Null(r.Journal.Pending!.Operation); Assert.Equal(AsrockOpenRgbRecoveryPhase.Restoring, r.Journal.Pending.Phase);
        r.Device.BeforeWrite = null; r.Device.Writes.Clear(); AssertRestored(r, new(r.Directory.Path, Identity.DeviceId));
        var write = Assert.Single(r.Device.Writes); Assert.Equal(5, write[3]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RealSdkTransportHasDurablePrefixesBeforeItsWriteAndPreservesThemWhenTheReceiptIsLost(bool loseReceipt)
    {
        using var r = new Rig(); var start = Start(r); var step = Mode(r); var operation = r.Plan.Prepare(start, r.Sdk, step);
        await using var server = new OpenRgbSdkTestServer(); server.Devices.Clear(); server.Devices.Add(r.Sdk.Id, OpenRgbSdkModelServer.Encode(r.Sdk));
        var state = Initial(); var written = false;
        AsrockOpenRgbNativeExecutor executor = null!; var native = new AsrockPacketEmulator();
        server.Intercept = async p =>
        {
            if (p.Command == AsrockOpenRgbOperationReceipt.QueryCommand)
            { if (!written || !loseReceipt) await server.ReplyAsync(p, p.Command, Wire(state)); return true; }
            if (p.Command != 1101) return false;
            var durable = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Identity.DeviceId).Pending;
            Assert.NotNull(durable!.Operation); Assert.Equal(operation.Packets.Length, durable.Operation.Mutation.Packets.Length);
            state = native.Execute(durable, state, server, p.Device, executor.Authorization.Authorize, r.Device.Exchange);
            written = true; await server.AckAsync(p); return true;
        };
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port, ownedHeadless: true, verifyPeer: (_, _) => { }, operationTimeout: TimeSpan.FromMilliseconds(800));
        var snapshot = Assert.Single(await connection.ScanAsync());
        executor = new AsrockOpenRgbNativeExecutor(connection, snapshot.Target, r.Plan, r.Journal, Identity);
        if (loseReceipt)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.SendAsync(snapshot.Controller, step, Exit(r), Guard));
            Assert.NotNull(r.Journal.Pending!.Operation);
            await Assert.ThrowsAsync<IOException>(() => executor.SendAsync(snapshot.Controller, step, Exit(r), Guard));
        }
        else { Assert.True((await executor.SendAsync(snapshot.Controller, step, Exit(r), Guard)).Completed); Assert.Null(r.Journal.Pending!.Operation); }
        Assert.Single(server.Requests, p => p.Command == 1101); AssertRestored(r, r.Journal);
    }
    [Theory] [InlineData("save")] [InlineData("ownership")] [InlineData("sdk-change")]
    public async Task FailedPreconditionsNeverReachTheSdkMutation(string failure)
    {
        using var r = new Rig(); Start(r); var journal = new AsrockOpenRgbDetectionRecovery(r.Directory.Path, Identity.DeviceId,
            (path, value) => { if (failure == "save") throw new IOException("disk full"); ServiceEnvironment.Write(path, value); });
        await using var server = new OpenRgbSdkTestServer(); server.Devices.Clear(); server.Devices.Add(r.Sdk.Id, OpenRgbSdkModelServer.Encode(r.Sdk));
        server.Intercept = async p =>
        { if (p.Command != AsrockOpenRgbOperationReceipt.QueryCommand) return false; await server.ReplyAsync(p, p.Command, Wire(Initial())); return true; };
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port, ownedHeadless: true, verifyPeer: (_, _) => { });
        var snapshot = Assert.Single(await connection.ScanAsync());
        var executor = new AsrockOpenRgbNativeExecutor(connection, snapshot.Target, r.Plan, journal, Identity);
        if (failure == "sdk-change") server.Devices[r.Sdk.Id] = OpenRgbSdkModelServer.Encode(r.Sdk with { ActiveMode = 1 });
        await Assert.ThrowsAsync<IOException>(() => executor.SendAsync(snapshot.Controller, Mode(r), Exit(r),
            () => { if (failure == "ownership") throw new IOException("owner changed"); }));
        Assert.DoesNotContain(server.Requests, p => p.Command is 1050 or 1051 or 1101); Assert.Empty(r.Device.Writes);
    }
}
