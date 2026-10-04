using System.Buffers.Binary;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using static FlowDeck.Tests.OpenRgbSdkTestServer;

namespace FlowDeck.Tests;

public sealed class OpenRgbSdkConnectionTests
{
    [Fact] public async Task PeerIdentityIsVerifiedBeforeSendingAnySdkRequest()
    {
        await using var server = new OpenRgbSdkTestServer(); var called = false;
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port, verifyPeer: (client, remote) =>
        {
            Assert.Empty(server.Requests); WindowsTcpPeer.Check((uint)Environment.ProcessId, client, remote); called = true;
        });
        Assert.True(called); Assert.True(connection.IsConnected);
    }
    [Fact] public async Task WrongOwnerIsRejectedBeforeHandshakeEvenOnTheExpectedLoopbackPort()
    {
        await using var server = new OpenRgbSdkTestServer();
        await Assert.ThrowsAsync<IOException>(() => OpenRgbSdkConnection.ConnectAsync(server.Port,
            verifyPeer: (client, remote) => WindowsTcpPeer.Check(uint.MaxValue, client, remote)));
        Assert.Empty(server.Requests);
    }
    [Fact] public async Task OwnedHeadlessModeRequiresPeerVerification()
    {
        await using var server = new OpenRgbSdkTestServer();
        await Assert.ThrowsAsync<ArgumentException>(() => OpenRgbSdkConnection.ConnectAsync(server.Port, ownedHeadless: true));
        Assert.Empty(server.Requests);
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static Task<OpenRgbSdkAcceptance> Blue(OpenRgbSdkConnection connection, OpenRgbSdkTarget target) =>
        connection.WriteColorsAsync(target, [new(0, 0, 255), new(0, 0, 255)], () => { });

    [Fact] public async Task CompiledMutationGateIsCheckedAtTheWireWithoutDisablingReadOnlyInspection()
    {
        await using var server = new OpenRgbSdkTestServer(); var allowed = false; var calls = 0;
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port, authorizeWrite: () =>
        { calls++; if (!allowed) throw new IOException("native exit adapter is not ready"); });
        var inventory = await connection.ScanAsync(); Assert.Equal(0, calls); var target = inventory[0].Target;
        await Assert.ThrowsAsync<IOException>(() => Blue(connection, target)); Assert.Equal(1, calls);
        Assert.DoesNotContain(server.Requests, p => p.Command == 1050);
        _ = await connection.ReadAsync(target); Assert.True(connection.IsConnected);
        allowed = true; await Blue(connection, target); Assert.Equal(2, calls); Assert.Single(server.Requests, p => p.Command == 1050);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NegotiatesSixAndUsesUniqueIdsWithFragmentedResponsesAndEitherAckOrder(bool ackFirst)
    {
        await using var server = new OpenRgbSdkTestServer { AckFirst = ackFirst, Fragment = true, Version = 8 };
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var inventory = await connection.ScanAsync(); Assert.Equal(new uint[] { 41, 700 }, inventory.Select(s => s.Target.ControllerId));
        await Blue(connection, inventory[1].Target);
        var write = Assert.Single(server.Requests, r => r.Command == 1050); Assert.Equal(700u, write.Device);
        Assert.Equal(new byte[] { 14, 0, 0, 0, 2, 0, 0, 0, 255, 0, 0, 0, 255, 0 }, write.Data);
        Assert.DoesNotContain(server.Requests, p => p.Command is 1000 or 1102 or 140);
    }
    [Theory]
    [InlineData(2u)] [InlineData(5u)]
    public async Task LegacyServerIsRejectedBeforeAnyInventoryOrOutput(uint version)
    {
        await using var server = new OpenRgbSdkTestServer { Version = version };
        await Assert.ThrowsAnyAsync<IOException>(() => OpenRgbSdkConnection.ConnectAsync(server.Port));
        Assert.All(server.Requests, r => Assert.Equal(40u, r.Command));
    }
    [Fact]
    public async Task ServerWithoutRgbCapabilityIsRejected()
    {
        await using var server = new OpenRgbSdkTestServer { Flags = 0 };
        await Assert.ThrowsAnyAsync<IOException>(() => OpenRgbSdkConnection.ConnectAsync(server.Port));
        Assert.DoesNotContain(server.Requests, r => r.Command == 0);
    }
    [Fact]
    public async Task ReorderDoesNotRetargetWritesButListChangeRequiresNewAuthorizationTarget()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var original = await connection.ScanAsync(); var target = original[1].Target;
        var first = server.Devices[41]; var second = server.Devices[700]; server.Devices.Clear(); server.Devices.Add(700, second); server.Devices.Add(41, first);
        var reordered = await connection.ScanAsync(); Assert.Equal(target, reordered[0].Target);
        await Blue(connection, target); var count = server.Requests.Count;
        var revision = connection.InventoryRevision; await server.SendAsync(0, 100, []); await Eventually(() => connection.InventoryRevision > revision);
        await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target)); Assert.Equal(count, server.Requests.Count);
        var replacement = await connection.ScanAsync(); await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target));
        await Blue(connection, replacement[0].Target); Assert.All(server.Requests.Where(p => p.Command == 1050), p => Assert.Equal(700u, p.Device));
    }
    [Fact]
    public async Task SameNumericIdOnNewConnectionDoesNotReuseOldToken()
    {
        await using var first = new OpenRgbSdkTestServer(); await using var a = await OpenRgbSdkConnection.ConnectAsync(first.Port);
        var target = (await a.ScanAsync())[0].Target; await a.DisposeAsync();
        await using var second = new OpenRgbSdkTestServer(); await using var b = await OpenRgbSdkConnection.ConnectAsync(second.Port);
        var fresh = (await b.ScanAsync())[0].Target;
        await Assert.ThrowsAnyAsync<IOException>(() => Blue(b, target)); Assert.DoesNotContain(second.Requests, p => p.Command == 1050);
        await Blue(b, fresh);
    }
    [Fact]
    public async Task ListChangeInMiddleOfScanDiscardsWholeInventory()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        server.Intercept = async packet =>
        {
            if (packet.Command != 1) return false;
            await server.SendAsync(0, 100, []); await server.ReplyAsync(packet, 1, Description()); return true;
        };
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ScanAsync());
        Assert.Single(server.Requests, p => p.Command == 1);
        server.Intercept = null; Assert.Equal(2, (await connection.ScanAsync()).Length);
    }
    [Fact]
    public async Task DetectionSuspendsInventoryUntilCompletionAndAcceptsProgressNotifications()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target; var revision = connection.InventoryRevision;
        await server.SendAsync(0, 101, []); await server.SendAsync(0, 102, Sized(w => { w.Write(50u); Text(w, "Scanning"); }));
        await Eventually(() => connection.InventoryRevision > revision);
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ScanAsync()); await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target));
        revision = connection.InventoryRevision; await server.SendAsync(0, 103, []); await Eventually(() => connection.InventoryRevision > revision);
        Assert.Equal(2, (await connection.ScanAsync()).Length);
    }
    [Fact]
    public async Task ColorEchoIsReportedWithoutInvalidatingIdentityOrClaimingReadbackOnAck()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target; var before = connection.Reported(target); await Blue(connection, target);
        Assert.Equal(before.Controller.Colors, connection.Reported(target).Controller.Colors);
        await server.SendAsync(41, 1150, SignalColors(0xff0000, 0xff0000)); await Eventually(() => connection.Observation > before.Observation);
        var reported = connection.Reported(target); Assert.Equal(target, reported.Target); Assert.Equal(new RgbColor(0, 0, 255), reported.Controller.Colors[0]);
        // Returned arrays are not the connection's authoritative cache.
        reported.Controller.Colors[0] = new(0, 0, 0); reported.Controller.Zones[0].Matrix!.Cells[0] = 100;
        Assert.Equal(new RgbColor(0, 0, 255), connection.Reported(target).Controller.Colors[0]); await Blue(connection, target);
    }
    [Theory]
    [InlineData(1u, true)] [InlineData(3u, false)] [InlineData(11u, false)]
    public async Task ModeObservationAndStructuralNotificationsAreDifferent(uint reason, bool remainsValid)
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target; var observation = connection.Observation;
        await server.SendAsync(41, 1150, SignalController(reason, Description())); await Eventually(() => connection.Observation > observation);
        if (remainsValid) Assert.Equal(target, connection.Reported(target).Target);
        else await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target));
    }
    [Fact]
    public async Task ChangedCapabilitiesInQueryInvalidateOldTargetWithoutWriting()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target; server.Devices[41] = Description(count: 3);
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ReadAsync(target)); await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target));
        Assert.DoesNotContain(server.Requests, p => p.Command == 1050);
        var fresh = (await connection.ScanAsync())[0]; Assert.Equal(3, fresh.Controller.Leds.Length);
    }
    [Fact]
    public async Task NewerNotificationIsNotOverwrittenByOlderQueryResponse()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        server.Intercept = async packet =>
        {
            if (packet.Command != 1) return false;
            await server.SendAsync(packet.Device, 1, Description()); await server.SendAsync(packet.Device, 1150, SignalColors(0x0000ff, 0x0000ff));
            await server.AckAsync(packet); return true;
        };
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ReadAsync(target));
        Assert.Equal(new RgbColor(255, 0, 0), connection.Reported(target).Controller.Colors[0]);
    }
    [Fact]
    public async Task AuthorizationAndBoundsAreCheckedImmediatelyBeforeSend()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target; var before = server.Requests.Count;
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => connection.WriteColorsAsync(target, [new(0, 0, 0), new(0, 0, 0)], () => throw new UnauthorizedAccessException()));
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteColorsAsync(target, [new(0, 0, 0)], () => { }));
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteColorsAsync(target, [new(0, 0, 0), new(0, 0, 0)], () => { }, 8));
        Assert.Equal(before, server.Requests.Count); Assert.True(connection.IsConnected); await Blue(connection, target);
    }
    [Fact]
    public async Task NativeModeUsesAdvertisedCapabilitiesAndZoneCommand()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var device = (await connection.ScanAsync())[0]; var mode = device.Controller.Zones[0].Modes[1];
        await connection.WriteModeAsync(device.Target, 1, mode with { Speed = 50, Direction = 1 }, () => { }, 0);
        Assert.Equal(41u, Assert.Single(server.Requests, r => r.Command == 1103).Device);
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteModeAsync(device.Target, 1, mode with { Speed = 999 }, () => { }));
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteModeAsync(device.Target, 1, mode with { Flags = 0 }, () => { }));
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteModeAsync(device.Target, 1, mode with { Direction = 99 }, () => { }));
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteModeAsync(device.Target, 1, mode with { Direction = 9 }, () => { }));
        Assert.Single(server.Requests, p => p.Command >= 1000);
    }
    [Theory]
    [InlineData(1u)] [InlineData(4u)] [InlineData(5u)] [InlineData(99u)]
    public async Task FailedOrUnknownAckClosesConnectionAndNeverRetries(uint status)
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        server.Intercept = async packet => { if (packet.Command != 1050) return false; await server.AckAsync(packet, status); return true; };
        await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target)); Assert.False(connection.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(() => Blue(connection, target)); Assert.Single(server.Requests, p => p.Command == 1050);
    }
    [Fact]
    public async Task TimeoutAfterSendingClosesConnectionSoLateAckCannotConfirmAnotherRequest()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port, operationTimeout: TimeSpan.FromMilliseconds(400));
        var target = (await connection.ScanAsync())[0].Target;
        server.Intercept = packet => Task.FromResult(packet.Command == 1050);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Blue(connection, target)); Assert.False(connection.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(() => Blue(connection, target)); Assert.Single(server.Requests, p => p.Command == 1050);
    }
    [Fact]
    public async Task QueuedCancellationDoesNotCancelIndependentInFlightRequestAndPayloadIsFrozen()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        var started = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Intercept = async packet =>
        {
            if (packet.Command != 1050 || !started.TrySetResult(packet)) return false;
            await release.Task.WaitAsync(server.Stopping); await server.AckAsync(packet); return true;
        };
        var first = Blue(connection, target); await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource(); var canceled = connection.WriteColorsAsync(target, [new(1, 2, 3), new(1, 2, 3)], () => { }, ct: cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        RgbColor[] colors = [new(10, 20, 30), new(40, 50, 60)]; var frozen = connection.WriteColorsAsync(target, colors, () => { }); colors[0] = new(0, 0, 0);
        release.SetResult(); await first; await frozen; Assert.True(connection.IsConnected);
        var last = server.Requests.Last(p => p.Command == 1050); Assert.Equal(new byte[] { 10, 20, 30, 0 }, last.Data[6..10]);
        Assert.Equal(2, server.Requests.Count(p => p.Command == 1050));
    }
    [Fact]
    public async Task QueuedWriteRechecksAuthorizationAfterWaiting()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Intercept = async packet =>
        {
            if (packet.Command != 1050) return false; started.TrySetResult(); await release.Task.WaitAsync(server.Stopping); await server.AckAsync(packet); return true;
        };
        var first = Blue(connection, target); await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); var authorized = true;
        var waiting = connection.WriteColorsAsync(target, [new(1, 2, 3), new(1, 2, 3)], () => { if (!authorized) throw new UnauthorizedAccessException(); });
        authorized = false; release.SetResult(); await first; await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => waiting);
        Assert.Single(server.Requests, p => p.Command == 1050); Assert.True(connection.IsConnected);
    }
    [Fact]
    public async Task QueueIsBoundedAndCancelingWaitersDoesNotSendThem()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Intercept = async packet =>
        {
            if (packet.Command != 1050) return false; started.TrySetResult(); await release.Task.WaitAsync(server.Stopping); await server.AckAsync(packet); return true;
        };
        var first = Blue(connection, target); await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var waiting = Enumerable.Range(0, 63).Select(_ => connection.ReadAsync(target, cancellation.Token)).ToArray();
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ReadAsync(target)); cancellation.Cancel();
        foreach (var task in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        release.SetResult(); await first; Assert.True(connection.IsConnected);
        Assert.Equal(2, server.Requests.Count(p => p.Command == 1)); // initial inventory only
    }
    [Fact]
    public async Task ListChangeDuringWriteDoesNotReportSuccessOrResend()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        server.Intercept = async packet =>
        {
            if (packet.Command != 1050) return false; await server.SendAsync(0, 100, []); await server.AckAsync(packet); return true;
        };
        await Assert.ThrowsAnyAsync<IOException>(() => Blue(connection, target)); Assert.Single(server.Requests, p => p.Command == 1050);
    }
    [Theory]
    [InlineData("device")] [InlineData("command")] [InlineData("size")] [InlineData("inventory")]
    public async Task MalformedPeerIsQuarantined(string corruption)
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        server.Intercept = async packet =>
        {
            if (packet.Command != 0) return false;
            if (corruption == "device") await server.SendAsync(99, 0, Numbers(0));
            if (corruption == "command") await server.SendAsync(0, 10, Numbers(1050, 0));
            if (corruption == "inventory") await server.ReplyAsync(packet, 0, Numbers(2, 41, 41));
            if (corruption == "size")
            {
                var header = new byte[16]; "ORGB"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), uint.MaxValue); await server.SendRawAsync(header);
            }
            return true;
        };
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ScanAsync()); Assert.False(connection.IsConnected);
    }
    [Fact]
    public async Task DisconnectInvalidatesAllLeases()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target; server.Disconnect(); await Eventually(() => !connection.IsConnected);
        Assert.ThrowsAny<IOException>(() => connection.Reported(target));
    }
    [Theory]
    [InlineData(0)] [InlineData(80)] [InlineData(65536)]
    public async Task PortBoundsRejectBeforeConnecting(int port) => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OpenRgbSdkConnection.ConnectAsync(port));
}
