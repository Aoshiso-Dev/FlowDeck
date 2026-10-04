using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbOperationReceiptTests
{
    // Independent wire fixture: numeric arrays match the native JSON contract, not .NET byte[] serialization.
    private static JsonObject Io(ulong attempts = 6, bool failed = false)
    {
        var request = new int[65]; request[1] = 0x10; request[3] = 2; request[4] = 1;
        return new JsonObject { ["attempted"] = attempts, ["completed"] = failed ? attempts - 1 : attempts,
            ["stage"] = failed ? 2 : 3, ["faulted"] = failed, ["writeResult"] = 65, ["readResult"] = failed ? 0 : 64,
            ["request"] = new JsonArray(request.Select(i => JsonValue.Create(i)!).ToArray()),
            ["response"] = new JsonArray(Enumerable.Range(0, 64).Select(_ => JsonValue.Create(0)!).ToArray()) };
    }
    private static JsonObject Initial() => new() { ["version"] = 1, ["sequence"] = 0UL, ["command"] = 0u, ["stage"] = 0,
        ["before"] = Io(), ["after"] = Io() };
    private static JsonObject Next(JsonObject previous, uint command = 1050, bool failed = false) => new()
    {
        ["version"] = 1, ["sequence"] = previous["sequence"]!.GetValue<ulong>() + 1, ["command"] = command,
        ["stage"] = failed ? 3 : 2, ["before"] = previous["after"]!.DeepClone(),
        ["after"] = Io(previous["after"]!["attempted"]!.GetValue<ulong>() + 1, failed)
    };
    private static byte[] Packet(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString() + "\0");
    private static AsrockOpenRgbOperationReceipt Receipt(JsonObject value) => AsrockOpenRgbOperationReceipt.Parse(Packet(value));
    private static Task<OpenRgbSdkConnection> Connect(OpenRgbSdkTestServer server, Action? wire = null, TimeSpan? timeout = null) =>
        OpenRgbSdkConnection.ConnectAsync(server.Port, ownedHeadless: true, verifyPeer: (_, _) => { }, authorizeWrite: wire, operationTimeout: timeout);
    private static readonly RgbColor[] Blue = [new(0, 0, 255), new(0, 0, 255)];

    [Fact] public void ActualNativeHelperReceiptsContinueFromDetectionThroughSuccessAndFailure()
    {
        // Emitted by the compiled real RGB base/ASRock/native classes with mock HID; see fixture provenance.
        using var stream = typeof(AsrockOpenRgbOperationReceiptTests).Assembly.GetManifestResourceStream("FlowDeck.Tests.AsrockNativePatch4.json")!;
        using var document = JsonDocument.Parse(stream);
        var states = document.RootElement.EnumerateArray().Select(e => AsrockOpenRgbOperationReceipt.Parse(Encoding.UTF8.GetBytes(e.GetRawText() + "\0"))).ToArray();
        Assert.Equal(3, states.Length); Assert.Equal(AsrockOpenRgbOperationStage.Idle, states[0].Stage);
        Assert.Equal(6UL, states[0].After.Completed); Assert.Equal(0x11, states[0].After.Request[1]);
        AsrockOpenRgbOperationReceipt.CheckResult(states[0], states[1], 1101, 0);
        AsrockOpenRgbOperationReceipt.CheckResult(states[1], states[2], 1050, 1);
        Assert.Equal(9UL, states[1].After.Completed); Assert.Equal(10UL, states[2].After.Attempted);
        Assert.Equal(9UL, states[2].After.Completed); Assert.True(states[2].After.Faulted);
    }

    [Fact] public void CompletedAndFailedTranscriptsRetainCounterAndPacketEvidence()
    {
        var initial = Receipt(Initial()); var next = Receipt(Next(Initial()));
        AsrockOpenRgbOperationReceipt.CheckResult(initial, next, 1050, 0);
        Assert.Equal(7UL, next.After.Completed); Assert.Equal(0x10, next.After.Request[1]);
        var failed = Receipt(Next(Initial(), failed: true));
        AsrockOpenRgbOperationReceipt.CheckResult(initial, failed, 1050, 1);
        Assert.Equal(7UL, failed.After.Attempted); Assert.Equal(6UL, failed.After.Completed);
        var copy = failed.Copy(); copy.After.Request[1] = 0;
        Assert.Equal(0x10, failed.After.Request[1]); Assert.False(failed.Same(copy));
    }
    [Theory]
    [InlineData("version")] [InlineData("extra")] [InlineData("missing")] [InlineData("negative-sequence")]
    [InlineData("stage")] [InlineData("command")] [InlineData("request-length")] [InlineData("response-length")]
    [InlineData("byte-range")] [InlineData("counter-order")] [InlineData("counter-gap")] [InlineData("incomplete-success")]
    [InlineData("report-id")] [InlineData("flash-command")] [InlineData("wrong-write")] [InlineData("wrong-read")]
    [InlineData("faulted-success")] [InlineData("duplicate")] [InlineData("nul")] [InlineData("size")]
    public void RejectsMalformedOrContradictoryReceipt(string kind)
    {
        var data = Next(Initial()); var io = data["after"]!;
        switch (kind)
        {
            case "version": data["version"] = 2; break;
            case "extra": data["extra"] = 1; break;
            case "missing": data.Remove("command"); break;
            case "negative-sequence": data["sequence"] = -1; break;
            case "stage": data["stage"] = 4; break;
            case "command": data["command"] = 1102; break;
            case "request-length": io["request"]!.AsArray().RemoveAt(0); break;
            case "response-length": io["response"]!.AsArray().Add(0); break;
            case "byte-range": io["request"]![7] = 256; break;
            case "counter-order": io["completed"] = 8; break;
            case "counter-gap": io["completed"] = 0; break;
            case "incomplete-success": io["completed"] = 6; break;
            case "report-id": io["request"]![0] = 1; break;
            case "flash-command": io["request"]![1] = 0x12; break;
            case "wrong-write": io["writeResult"] = 64; break;
            case "wrong-read": io["readResult"] = 63; break;
            case "faulted-success": io["faulted"] = true; break;
        }
        var bytes = Packet(data);
        if (kind == "duplicate") bytes = Encoding.UTF8.GetBytes(data.ToJsonString().Insert(1, "\"version\":1,") + "\0");
        if (kind == "nul") bytes = bytes[..^1];
        if (kind == "size") bytes = new byte[8193];
        Assert.Throws<IOException>(() => AsrockOpenRgbOperationReceipt.Parse(bytes));
    }
    [Theory] [InlineData("sequence")] [InlineData("command")] [InlineData("before")] [InlineData("stage")]
    public void CompletionRequiresTheSameOperationAndUnbrokenIoHistory(string kind)
    {
        var initial = Receipt(Initial()); var current = Next(Initial());
        if (kind == "sequence") current["sequence"] = 2UL;
        if (kind == "command") current["command"] = 1101;
        if (kind == "before") current["before"]!["request"]![10] = 1;
        if (kind == "stage") current["stage"] = 1;
        Assert.Throws<IOException>(() => AsrockOpenRgbOperationReceipt.CheckResult(initial, Receipt(current), 1050, 0));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReadsWithFragmentedPacketsAndEitherAcknowledgementOrder(bool ackFirst)
    {
        await using var server = new OpenRgbSdkTestServer { Fragment = true, AckFirst = ackFirst };
        server.Intercept = async packet =>
        {
            if (packet.Command != AsrockOpenRgbOperationReceipt.QueryCommand) return false;
            Assert.Empty(packet.Data); await server.ReplyAsync(packet, packet.Command, Packet(Initial())); return true;
        };
        await using var connection = await Connect(server); var target = (await connection.ScanAsync())[0].Target;
        var state = await connection.ReadAsrockOperationAsync(target, () => { }); Assert.Equal(6UL, state.After.Completed);
        Assert.DoesNotContain(server.Requests, p => p.Command is 1050 or 1101);
    }
    [Fact] public async Task UnownedSessionCannotUseTheReceiptInterface()
    {
        await using var server = new OpenRgbSdkTestServer(); await using var connection = await OpenRgbSdkConnection.ConnectAsync(server.Port);
        var target = (await connection.ScanAsync())[0].Target;
        await Assert.ThrowsAsync<IOException>(() => connection.ReadAsrockOperationAsync(target, () => { }));
        Assert.DoesNotContain(server.Requests, p => p.Command == AsrockOpenRgbOperationReceipt.QueryCommand); Assert.True(connection.IsConnected);
    }
    [Theory] [InlineData(1050u)] [InlineData(1051u)] [InlineData(1101u)]
    public async Task SupportedWritesCorrelateTheReceiptAroundTheActualSdkRequest(uint command)
    {
        await using var server = new OpenRgbSdkTestServer(); var state = Initial(); var calls = 0;
        server.Intercept = async p =>
        {
            if (p.Command == AsrockOpenRgbOperationReceipt.QueryCommand) { await server.ReplyAsync(p, p.Command, Packet(state)); return true; }
            if (p.Command != command) return false;
            state = Next(state, command); await server.AckAsync(p); return true;
        };
        await using var connection = await Connect(server, () => calls++); var snapshot = (await connection.ScanAsync())[0];
        var result = command == 1101 ? await connection.WriteAsrockModeAsync(snapshot.Target, Receipt(Initial()), 0, snapshot.Controller.Modes[0], () => { }) :
            await connection.WriteAsrockColorsAsync(snapshot.Target, Receipt(Initial()), Blue, () => { }, command == 1051 ? 0 : null);
        Assert.True(result.Completed); Assert.True(result.Entered); Assert.Equal(1, calls);
        Assert.Equal(2, server.Requests.Count(p => p.Command == AsrockOpenRgbOperationReceipt.QueryCommand));
        Assert.Single(server.Requests, p => p.Command == command);
    }
    [Theory] [InlineData(1u, true)] [InlineData(2u, false)] [InlineData(3u, false)] [InlineData(4u, false)] [InlineData(5u, false)]
    public async Task RejectionPreservesReadAccessAndBlocksEveryFurtherMutation(uint status, bool entered)
    {
        await using var server = new OpenRgbSdkTestServer(); var state = Initial();
        server.Intercept = async p =>
        {
            if (p.Command == AsrockOpenRgbOperationReceipt.QueryCommand) { await server.ReplyAsync(p, p.Command, Packet(state)); return true; }
            if (p.Command != 1050) return false;
            if (entered) state = Next(state, failed: true);
            await server.AckAsync(p, status); return true;
        };
        await using var connection = await Connect(server); var target = (await connection.ScanAsync())[0].Target;
        var result = await connection.WriteAsrockColorsAsync(target, Receipt(Initial()), Blue, () => { });
        Assert.False(result.Completed); Assert.Equal(entered, result.Entered); Assert.Equal(status, result.Acknowledgement); Assert.True(connection.IsConnected);
        await connection.ReadAsrockOperationAsync(target, () => { }); await connection.ReadAsync(target);
        await Assert.ThrowsAsync<IOException>(() => connection.WriteColorsAsync(target, Blue, () => { }));
        await Assert.ThrowsAsync<IOException>(() => connection.WriteAsrockColorsAsync(target, result.After, Blue, () => { }));
        Assert.Single(server.Requests, p => p.Command == 1050);
    }
    [Fact] public async Task InvalidAcknowledgementStillTerminatesTheSession()
    {
        await using var server = new OpenRgbSdkTestServer();
        server.Intercept = async p =>
        {
            if (p.Command == AsrockOpenRgbOperationReceipt.QueryCommand) { await server.ReplyAsync(p, p.Command, Packet(Initial())); return true; }
            if (p.Command != 1050) return false; await server.AckAsync(p, 6); return true;
        };
        await using var connection = await Connect(server); var target = (await connection.ScanAsync())[0].Target;
        await Assert.ThrowsAnyAsync<IOException>(() => connection.WriteAsrockColorsAsync(target, Receipt(Initial()), Blue, () => { }));
        Assert.False(connection.IsConnected); Assert.Single(server.Requests, p => p.Command == AsrockOpenRgbOperationReceipt.QueryCommand);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedReceiptBeforeOrAfterTheWriteBlocksContinuation(bool afterWrite)
    {
        await using var server = new OpenRgbSdkTestServer(); var written = false;
        server.Intercept = async p =>
        {
            if (p.Command == 1050) { written = true; await server.AckAsync(p); return true; }
            if (p.Command != AsrockOpenRgbOperationReceipt.QueryCommand) return false;
            var state = !afterWrite || written ? Next(Next(Initial())) : Initial();
            await server.ReplyAsync(p, p.Command, Packet(state)); return true;
        };
        await using var connection = await Connect(server); var target = (await connection.ScanAsync())[0].Target;
        await Assert.ThrowsAsync<IOException>(() => connection.WriteAsrockColorsAsync(target, Receipt(Initial()), Blue, () => { }));
        Assert.Equal(afterWrite ? 1 : 0, server.Requests.Count(p => p.Command == 1050));
        await Assert.ThrowsAsync<IOException>(() => connection.WriteColorsAsync(target, Blue, () => { }));
        Assert.True(connection.IsConnected);
    }
    [Fact] public async Task WireAuthorizationCannotBeBypassedByTheReceiptPath()
    {
        await using var server = new OpenRgbSdkTestServer();
        server.Intercept = async p =>
        {
            if (p.Command != AsrockOpenRgbOperationReceipt.QueryCommand) return false;
            await server.ReplyAsync(p, p.Command, Packet(Initial())); return true;
        };
        await using var connection = await Connect(server, () => throw new IOException("not enabled")); var target = (await connection.ScanAsync())[0].Target;
        await Assert.ThrowsAsync<IOException>(() => connection.WriteAsrockColorsAsync(target, Receipt(Initial()), Blue, () => { }));
        Assert.DoesNotContain(server.Requests, p => p.Command == 1050); Assert.True(connection.IsConnected);
    }
    [Fact] public async Task MissingPostWriteReceiptDoesNotRetryOrClaimCompletion()
    {
        await using var server = new OpenRgbSdkTestServer(); var written = false;
        server.Intercept = async p =>
        {
            if (p.Command == 1050) { written = true; await server.AckAsync(p); return true; }
            if (p.Command != AsrockOpenRgbOperationReceipt.QueryCommand) return false;
            if (!written) await server.ReplyAsync(p, p.Command, Packet(Initial())); return true;
        };
        await using var connection = await Connect(server, timeout: TimeSpan.FromMilliseconds(300)); var target = (await connection.ScanAsync())[0].Target;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WriteAsrockColorsAsync(target, Receipt(Initial()), Blue, () => { }));
        Assert.Single(server.Requests, p => p.Command == 1050); Assert.False(connection.IsConnected);
    }
}
