using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DialProtocolTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    private sealed class Backend : IDialControlBackend
    {
        public DialSnapshot View = new(true, [new("fan-a", "Fan A", "cooling", 50, 20, 100, 1, true, false, "", "version-a"),
            new("light-b", "Light B", "lighting", 70, 0, 100, 1, true, false, "", "version-b")]);
        public List<(string Target, string Version, int Value)> Calls = [];
        public TaskCompletionSource<string>? Pending;
        public DialAdjustmentStatus ResultStatus = DialAdjustmentStatus.Confirmed;
        public bool ReturnCurrentVersion = true;
        public bool ResolveMixed = true;
        public Action? AfterRead;
        public DialSnapshot Read() { var snapshot = View; AfterRead?.Invoke(); return snapshot; }
        public async Task<DialAdjustmentResult> AdjustAsync(string targetId, string expectedVersion, int value)
        {
            Calls.Add((targetId, expectedVersion, value)); var message = Pending is null ? "confirmed" : await Pending.Task;
            var index = Array.FindIndex(View.Targets, t => t.Id == targetId);
            if (index < 0) return new(ResultStatus, message);
            if (ResultStatus is DialAdjustmentStatus.Confirmed or DialAdjustmentStatus.Saved)
                View.Targets[index] = View.Targets[index] with { Value = value, Version = "result-" + Calls.Count, ValueMixed = !ResolveMixed && View.Targets[index].ValueMixed };
            return new(ResultStatus, message, ReturnCurrentVersion ? View.Targets[index].Version : "superseded");
        }
    }
    private sealed class Fixture
    {
        public Backend Backend { get; } = new();
        public Clock Clock { get; } = new();
        public DialProtocolSession Protocol { get; }
        public List<JsonElement> Output { get; } = [];
        public JsonElement State => Output.Last(e => e.GetProperty("type").GetString() == "state");
        public Fixture(bool registered = true)
        {
            Protocol = new(Backend, registered ? "dial" : null, Clock);
            Protocol.Outgoing += frame => { using var d = JsonDocument.Parse(frame); Output.Add(d.RootElement.Clone()); };
        }
        public Task Hello(string id = "dial", string boot = "boot", int responsiveInput = 0) => Protocol.ReceiveAsync(JsonSerializer.Serialize(new {
            v = 1, type = "hello", deviceType = "FlowDeckDial", deviceId = id, bootId = boot, firmwareVersion = "1.0", responsiveInput }));
        public string Input(uint seq = 1, string action = "rotate", int delta = 1, JsonElement? state = null) => JsonSerializer.Serialize(new {
            v = 1, type = "input", sessionId = (state ?? State).GetProperty("sessionId").GetString(), stateId = (state ?? State).GetProperty("stateId").GetString(),
            targetId = (state ?? State).GetProperty("targetId").GetString(), seq, action, delta });
        public string Set(uint seq, int value, JsonElement? state = null) => JsonSerializer.Serialize(new {
            v = 1, type = "input", sessionId = (state ?? State).GetProperty("sessionId").GetString(), stateId = (state ?? State).GetProperty("stateId").GetString(),
            targetId = (state ?? State).GetProperty("targetId").GetString(), seq, action = "set", value });
    }
    [Fact] public async Task UnregisteredAndForeignIdentityCannotOperateUntilExplicitRegistration()
    {
        var f = new Fixture(false); await f.Hello(); var old = f.State;
        Assert.False(f.State.GetProperty("enabled").GetBoolean()); await f.Protocol.ReceiveAsync(f.Input()); Assert.Empty(f.Backend.Calls);
        f.Protocol.RegisterObservedDevice(); Assert.True(f.State.GetProperty("enabled").GetBoolean());
        Assert.NotEqual(old.GetProperty("sessionId").GetString(), f.State.GetProperty("sessionId").GetString());
        await f.Protocol.ReceiveAsync(f.Input(2, state: old)); Assert.Empty(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(f.Input(2)); Assert.Single(f.Backend.Calls);
        await f.Hello("other", "another"); Assert.False(f.State.GetProperty("enabled").GetBoolean());
        await f.Protocol.ReceiveAsync(f.Input()); Assert.Single(f.Backend.Calls);
    }
    [Theory][InlineData("version")][InlineData("value")][InlineData("catalog")][InlineData("stale")][InlineData("unavailable")][InlineData("busy")][InlineData("edited")]
    public async Task QueuedRotationCannotFollowChangedAuthorityOrStaleState(string change)
    {
        var f = new Fixture(); await f.Hello(); var input = f.Input(); var targets = f.Backend.View.Targets;
        switch (change)
        {
            case "version": targets[0] = targets[0] with { Version = "new" }; break;
            case "value": targets[0] = targets[0] with { Value = 51 }; break;
            case "catalog": f.Backend.View = f.Backend.View with { Targets = [targets[1], targets[0]] }; break;
            case "stale": f.Clock.Milliseconds = 3000; break;
            case "unavailable": f.Backend.View = f.Backend.View with { Available = false }; break;
            case "busy": targets[0] = targets[0] with { Busy = true }; break;
            case "edited": targets[0] = targets[0] with { Enabled = false }; break;
        }
        await f.Protocol.ReceiveAsync(input); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public async Task PressCyclesWithoutWritingAndOldTargetEventsCannotReachNewTarget()
    {
        var f = new Fixture(); await f.Hello(); var before = f.State;
        await f.Protocol.ReceiveAsync(f.Input(action: "press", delta: 0));
        Assert.Empty(f.Backend.Calls); Assert.Equal("light-b", f.State.GetProperty("targetId").GetString()); Assert.Equal(2, f.State.GetProperty("index").GetInt32());
        await f.Protocol.ReceiveAsync(f.Input(2, state: before)); Assert.Empty(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(f.Input(3, delta: -3)); Assert.Equal(("light-b", "version-b", 67), Assert.Single(f.Backend.Calls));
    }
    [Fact] public async Task DisabledEditedTargetStillAllowsPressToAnotherTarget()
    {
        var f = new Fixture(); f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Enabled = false }; await f.Hello();
        await f.Protocol.ReceiveAsync(f.Input()); Assert.Empty(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(f.Input(2, "press", 0)); Assert.Equal("light-b", f.State.GetProperty("targetId").GetString());
    }
    [Fact] public async Task SequenceAndSessionPreventReplayEvenAcrossSameBootHello()
    {
        var f = new Fixture(); await f.Hello(); var input = f.Input();
        await f.Protocol.ReceiveAsync(input); await f.Hello(); await f.Protocol.ReceiveAsync(input); Assert.Single(f.Backend.Calls);
        var old = f.State; await f.Hello(boot: "new-boot"); await f.Protocol.ReceiveAsync(f.Input(2, state: old)); Assert.Single(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(f.Input()); Assert.Equal(2, f.Backend.Calls.Count);
    }
    [Theory][InlineData(0)][InlineData(21)][InlineData(-21)][InlineData(int.MaxValue)]
    public async Task InvalidDeltaCannotWrite(int delta)
    {
        var f = new Fixture(); await f.Hello(); await f.Protocol.ReceiveAsync(f.Input(delta: delta)); Assert.Empty(f.Backend.Calls);
    }
    [Theory][InlineData("boot")][InlineData("after-usb-replug")]
    public async Task UsbReconnectReadsCurrentValueWithoutApplyingOrReplayingAnOldGesture(string boot)
    {
        var f = new Fixture(); await f.Hello(responsiveInput: 1);
        var queued = f.Set(1, 95); var previous = f.State;
        f.Protocol.Disconnect();
        f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Value = 62, Version = "updated-while-unplugged" };
        await f.Hello(boot: boot, responsiveInput: 1);
        Assert.NotEqual(previous.GetProperty("sessionId").GetString(), f.State.GetProperty("sessionId").GetString());
        Assert.Equal(62, f.State.GetProperty("value").GetInt32());
        Assert.Empty(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(queued);
        Assert.Empty(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(f.Set(1, 63));
        Assert.Equal(("fan-a", "updated-while-unplugged", 63), Assert.Single(f.Backend.Calls));
    }
    [Fact]
    public async Task DifferentDialOnReusedPortCannotOperateAfterDisconnect()
    {
        var f = new Fixture(); await f.Hello(); var queued = f.Input();
        f.Protocol.Disconnect(); await f.Hello("another-dial", "another-boot", responsiveInput: 1);
        Assert.False(f.Protocol.Registered);
        Assert.False(f.State.GetProperty("enabled").GetBoolean());
        await f.Protocol.ReceiveAsync(queued);
        await f.Protocol.ReceiveAsync(f.Set(2, 95));
        Assert.Empty(f.Backend.Calls);
    }
    [Fact] public async Task BusyStateKeepsConfirmedValueAndDisconnectDoesNotReplayOrPublishCompletion()
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello();
        var operation = f.Protocol.ReceiveAsync(f.Input(delta: 5)); Assert.False(operation.IsCompleted);
        Assert.True(f.State.GetProperty("busy").GetBoolean()); Assert.False(f.State.GetProperty("enabled").GetBoolean()); Assert.Equal(50, f.State.GetProperty("value").GetInt32());
        await f.Protocol.ReceiveAsync(f.Input(2)); Assert.Single(f.Backend.Calls);
        f.Protocol.Publish(); Assert.True(f.State.GetProperty("busy").GetBoolean());
        f.Protocol.Disconnect(); var count = f.Output.Count; f.Backend.Pending.SetResult("done"); await operation;
        Assert.Equal(count, f.Output.Count); Assert.Null(f.Protocol.SessionId);
    }
    [Fact] public async Task LabelsAndMultilineErrorsAreBoundedUtf8WithoutControlCharacters()
    {
        var f = new Fixture(); f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Label = string.Concat(Enumerable.Repeat("冷却😀\n", 100)) };
        f.Backend.Pending = new(); await f.Hello(); var operation = f.Protocol.ReceiveAsync(f.Input());
        f.Backend.Pending.SetException(new IOException("failure\nwith\ttabs\r" + new string('光', 500))); await operation;
        var label = f.State.GetProperty("label").GetString()!; var message = f.State.GetProperty("message").GetString()!;
        Assert.True(Encoding.UTF8.GetByteCount(label) <= 192); Assert.True(Encoding.UTF8.GetByteCount(message) <= 256);
        Assert.DoesNotContain(label.Concat(message), char.IsControl);
        Assert.All(f.Output, e => Assert.True(Encoding.UTF8.GetByteCount(e.GetRawText()) + 1 <= 4096));
    }
    [Fact] public async Task InvalidHelloAndOversizedOrMalformedFramesNeverEstablishAConnection()
    {
        var f = new Fixture();
        await f.Protocol.ReceiveAsync("{"); await f.Protocol.ReceiveAsync(new string('x', 4096));
        await f.Protocol.ReceiveAsync("{\"v\":1,\"type\":\"hello\",\"deviceType\":\"M5GO\",\"deviceId\":\"dial\",\"bootId\":\"b\",\"firmwareVersion\":\"1\"}");
        await f.Hello("bad\nidentity"); Assert.Null(f.Protocol.DeviceId); Assert.Empty(f.Output);
    }
    [Fact] public async Task NoTargetsIsDisabledAndReadbackBelowMinimumStillDescribesTheRealValue()
    {
        var f = new Fixture(); f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Value = 0 }; await f.Hello();
        Assert.Equal(0, f.State.GetProperty("value").GetInt32()); Assert.Equal(20, f.State.GetProperty("min").GetInt32());
        await f.Protocol.ReceiveAsync(f.Input(delta: -1)); Assert.Empty(f.Backend.Calls);
        await f.Protocol.ReceiveAsync(f.Input(2)); Assert.Equal(20, Assert.Single(f.Backend.Calls).Value);
        f.Backend.View = new(true, []); f.Protocol.Publish(); Assert.Equal(0, f.State.GetProperty("count").GetInt32()); Assert.False(f.State.GetProperty("enabled").GetBoolean());
    }
    [Fact] public async Task PeriodicStateKeepsItsTokenUntilAuthorityOrSelectionChanges()
    {
        var f = new Fixture(); await f.Hello(); var first = f.State;
        f.Clock.Milliseconds = 500; f.Protocol.Publish(); f.Clock.Milliseconds = 1000; f.Protocol.Publish();
        Assert.Equal(first.GetProperty("stateId").GetString(), f.State.GetProperty("stateId").GetString());
        await f.Protocol.ReceiveAsync(f.Input(state: first)); Assert.Single(f.Backend.Calls);
    }
    [Fact] public void SettingsRemainSeparateAndDemoCannotCreatePhysicalTransport()
    {
        using var directory = new ServiceTestDirectory(); var path = Path.Combine(directory.Path, "dial-v1.json"); var store = new DeviceDialSettingsStore(path);
        Assert.Equal(new DeviceDialSettings("COM6"), store.Load()); Assert.False(File.Exists(path));
        store.Save(new("COM8", "dial")); Assert.Equal(new DeviceDialSettings("COM8", "dial"), store.Load());
        Assert.Single(Directory.GetFiles(directory.Path)); Assert.Throws<InvalidOperationException>(() => new DialSerialTransport("COM8", true));
        Assert.Throws<ArgumentException>(() => store.Save(new("COM0")));
        File.WriteAllText(path, "{malformed"); Assert.Equal(new DeviceDialSettings(), store.Load()); Assert.NotNull(store.Warning);
    }
    [Fact] public async Task AbsoluteInputRequiresNegotiatedCapabilityAndKeepsLegacyRotation()
    {
        var old = new Fixture(); await old.Hello(); await old.Protocol.ReceiveAsync(old.Set(1, 88)); Assert.Empty(old.Backend.Calls);
        Assert.Equal("rejected", old.State.GetProperty("ackStatus").GetString());
        await old.Protocol.ReceiveAsync(old.Input(2)); Assert.Equal(51, Assert.Single(old.Backend.Calls).Value);
        var f = new Fixture(); await f.Hello(responsiveInput: 1);
        Assert.Equal(1, f.Output.First(e => e.GetProperty("type").GetString() == "welcome").GetProperty("responsiveInput").GetInt32());
        await f.Protocol.ReceiveAsync(f.Set(1, 95)); Assert.Equal(95, Assert.Single(f.Backend.Calls).Value);
        Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString()); Assert.Equal(1u, f.State.GetProperty("ackSeq").GetUInt32());
    }
    [Fact] public async Task MatchingConfirmedCompletionPreservesGestureAndAcceptsOnlyFreshContinuation()
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello(responsiveInput: 1); var initial = f.State;
        var pending = f.Protocol.ReceiveAsync(f.Set(1, 55));
        Assert.False(pending.IsCompleted); Assert.Equal("pending", f.State.GetProperty("ackStatus").GetString());
        Assert.Equal(1u, f.State.GetProperty("ackSeq").GetUInt32()); Assert.Equal(50, f.State.GetProperty("value").GetInt32());
        Assert.Equal(initial.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        f.Protocol.Publish(); f.Backend.Pending.SetResult("applied"); await pending;
        Assert.Equal(55, f.State.GetProperty("value").GetInt32()); Assert.False(f.State.GetProperty("busy").GetBoolean());
        Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString());
        Assert.Equal(initial.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        f.Backend.Pending = null; await f.Protocol.ReceiveAsync(f.Set(2, 83));
        Assert.Equal(new[] { 55, 83 }, f.Backend.Calls.Select(c => c.Value));
        await f.Protocol.ReceiveAsync(f.Set(3, 90, initial)); Assert.Equal(2, f.Backend.Calls.Count);
    }
    [Theory][InlineData(DialAdjustmentStatus.Unverified)][InlineData(DialAdjustmentStatus.Rejected)]
    public async Task UnconfirmedCompletionRevokesBufferedGesture(DialAdjustmentStatus status)
    {
        var f = new Fixture(); f.Backend.ResultStatus = status; await f.Hello(responsiveInput: 1); var before = f.State;
        await f.Protocol.ReceiveAsync(f.Set(1, 75));
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        Assert.Equal(status == DialAdjustmentStatus.Unverified ? "unverified" : "rejected", f.State.GetProperty("ackStatus").GetString());
        Assert.Equal(50, f.State.GetProperty("value").GetInt32());
    }
    [Fact] public async Task DurableSavedLightingResultContinuesWithoutClaimingPhysicalConfirmation()
    {
        var f = new Fixture(); f.Backend.ResultStatus = DialAdjustmentStatus.Saved; await f.Hello(responsiveInput: 1);
        await f.Protocol.ReceiveAsync(f.Input(1, "press", 0)); var before = f.State;
        await f.Protocol.ReceiveAsync(f.Set(2, 60));
        Assert.Equal("saved", f.State.GetProperty("ackStatus").GetString()); Assert.Equal(2u, f.State.GetProperty("ackSeq").GetUInt32());
        Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        await f.Protocol.ReceiveAsync(f.Set(3, 35)); Assert.Equal(new[] { 60, 35 }, f.Backend.Calls.Select(c => c.Value));
    }
    [Fact] public async Task SavedResultCannotAuthorizeCoolingContinuation()
    {
        var f = new Fixture(); f.Backend.ResultStatus = DialAdjustmentStatus.Saved; await f.Hello(responsiveInput: 1); var before = f.State;
        await f.Protocol.ReceiveAsync(f.Set(1, 60));
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
    }
    [Fact] public async Task CompletionPublishesExactlyTheSnapshotWhoseContinuationWasChecked()
    {
        var f = new Fixture(); await f.Hello(responsiveInput: 1); var before = f.State;
        f.Backend.AfterRead = () =>
        {
            if (f.Backend.Calls.Count != 1) return;
            f.Backend.AfterRead = null;
            var targets = f.Backend.View.Targets.ToArray(); targets[0] = targets[0] with { Value = 91, Version = "external" };
            f.Backend.View = f.Backend.View with { Targets = targets };
        };
        await f.Protocol.ReceiveAsync(f.Set(1, 55));
        Assert.Equal(55, f.State.GetProperty("value").GetInt32());
        Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        // A continuation based on that previous snapshot is rejected when the external edit is observed.
        await f.Protocol.ReceiveAsync(f.Set(2, 67)); Assert.Single(f.Backend.Calls);
        Assert.Equal(91, f.State.GetProperty("value").GetInt32());
        Assert.Equal("rejected", f.State.GetProperty("ackStatus").GetString());
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
    }
    [Theory][InlineData("context")][InlineData("catalog")][InlineData("range")][InlineData("unavailable")][InlineData("version")][InlineData("members")]
    public async Task AuthorityChangeDuringApplyCannotAuthorizeBufferedContinuation(string change)
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello(responsiveInput: 1); var before = f.State;
        var pending = f.Protocol.ReceiveAsync(f.Set(1, 55));
        switch (change)
        {
            case "context": f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { ControlContext = "replacement-or-gui-edit" }; break;
            case "catalog": f.Backend.View = f.Backend.View with { Targets = [f.Backend.View.Targets[1], f.Backend.View.Targets[0]] }; break;
            case "range": f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Minimum = 30 }; break;
            case "unavailable": f.Backend.View = f.Backend.View with { Available = false }; break;
            case "version": f.Backend.ReturnCurrentVersion = false; break;
            case "members": f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { MemberCount = 3 }; break;
        }
        f.Protocol.Publish(); f.Backend.Pending.SetResult("applied"); await pending;
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
    }
    [Fact] public async Task IdleExternalChangeAndPressEachRevokeGestureWithoutWriting()
    {
        var f = new Fixture(); await f.Hello(responsiveInput: 1); var before = f.State;
        f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Version = "external" }; f.Protocol.Publish();
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        before = f.State; await f.Protocol.ReceiveAsync(f.Input(1, "press", 0));
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString()); Assert.Empty(f.Backend.Calls);
    }
    [Theory][InlineData(-1)][InlineData(0)][InlineData(19)][InlineData(101)][InlineData(int.MaxValue)]
    public async Task AbsoluteInputOutsideCurrentLimitsCannotWrite(int value)
    {
        var f = new Fixture(); await f.Hello(responsiveInput: 1); await f.Protocol.ReceiveAsync(f.Set(1, value));
        Assert.Empty(f.Backend.Calls); Assert.Equal("rejected", f.State.GetProperty("ackStatus").GetString());
    }
    [Fact] public async Task AcceptedNoChangeAcknowledgesWithoutWritingOrRevokingGesture()
    {
        var f = new Fixture(); await f.Hello(responsiveInput: 1); var before = f.State;
        await f.Protocol.ReceiveAsync(f.Set(1, 50)); Assert.Empty(f.Backend.Calls);
        Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString());
        Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
    }
    [Theory][InlineData(66)][InlineData(100)]
    public async Task MixedMaximumIsWrittenEvenAtSameValueAndSuccessfulUnificationPreservesGesture(int value)
    {
        var f = new Fixture(); f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Value = value, ValueMixed = true, MemberCount = 5 };
        await f.Hello(responsiveInput: 1); var before = f.State;
        Assert.True(before.GetProperty("valueMixed").GetBoolean()); Assert.Equal(5, before.GetProperty("memberCount").GetInt32());
        await f.Protocol.ReceiveAsync(f.Set(1, value)); Assert.Equal(value, Assert.Single(f.Backend.Calls).Value);
        Assert.False(f.State.GetProperty("valueMixed").GetBoolean()); Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString());
        Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
    }
    [Fact] public async Task MixedCompletionCannotContinueAndPartialMessageSurvivesDisabledMember()
    {
        var f = new Fixture(); f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { ValueMixed = true, MemberCount = 3 }; f.Backend.ResolveMixed = false;
        await f.Hello(responsiveInput: 1); var before = f.State; await f.Protocol.ReceiveAsync(f.Set(1, 60));
        Assert.True(f.State.GetProperty("valueMixed").GetBoolean()); Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        f.Backend.Pending = new(); f.Backend.ResultStatus = DialAdjustmentStatus.Rejected;
        var pending = f.Protocol.ReceiveAsync(f.Set(2, 70));
        f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { Enabled = false, Message = "最後の操作を確認してください" };
        f.Backend.Pending.SetResult("確認 2/3・対象ごとの結果をPCで確認してください"); await pending;
        Assert.Equal("rejected", f.State.GetProperty("ackStatus").GetString()); Assert.StartsWith("確認 2/3", f.State.GetProperty("message").GetString());
    }
    [Theory][InlineData("fallback")][InlineData("members")][InlineData("value")]
    public async Task ExternalGroupChangeDoesNotDisplayAnOldGroupsConfirmationCount(string change)
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello(responsiveInput: 1);
        var applying = f.Protocol.ReceiveAsync(f.Set(1, 60)); f.Backend.Pending.SetResult("確認 9/9・全台に適用しました"); await applying;
        Assert.StartsWith("確認 9/9", f.State.GetProperty("message").GetString());
        if (change == "fallback") f.Backend.View = f.Backend.View with { Targets = [f.Backend.View.Targets[1] with { Message = "ポンプの現在値" }] };
        else f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { MemberCount = change == "members" ? 4 : 1, Value = change == "value" ? 80 : 60, Message = "現在の全ファン" };
        f.Protocol.Publish(); Assert.DoesNotContain("確認 9/9", f.State.GetProperty("message").GetString());
        Assert.Equal(change == "fallback" ? "ポンプの現在値" : "現在の全ファン", f.State.GetProperty("message").GetString());
    }
    [Fact] public async Task RpmUpdatesKeepTokensAndConfirmationNoticeAndAcceptThePreviouslyPublishedInput()
    {
        var f = new Fixture(); f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { MemberCount = 3, RpmMin = 900, RpmMax = 1200, RpmCount = 3 };
        f.Backend.Pending = new(); await f.Hello(responsiveInput: 1);
        var applying = f.Protocol.ReceiveAsync(f.Set(1, 60)); f.Backend.Pending.SetResult("確認 3/3・全台に適用しました"); await applying;
        var confirmed = f.State;
        f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { RpmMin = 0, RpmMax = 1400, RpmCount = 2 };
        f.Backend.View.Targets[1] = f.Backend.View.Targets[1] with { RpmMin = 2300, RpmMax = 2300, RpmCount = 1 };
        f.Clock.Milliseconds = 500; f.Protocol.Publish();
        Assert.Equal(0, f.State.GetProperty("rpmMin").GetInt32()); Assert.Equal(1400, f.State.GetProperty("rpmMax").GetInt32()); Assert.Equal(2, f.State.GetProperty("rpmCount").GetInt32());
        Assert.Equal(confirmed.GetProperty("stateId").GetString(), f.State.GetProperty("stateId").GetString());
        Assert.Equal(confirmed.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        Assert.Equal("確認 3/3・全台に適用しました", f.State.GetProperty("message").GetString());
        f.Backend.Pending = null; await f.Protocol.ReceiveAsync(f.Set(2, 63, confirmed)); Assert.Equal(new[] { 60, 63 }, f.Backend.Calls.Select(c => c.Value));
    }
    [Fact] public async Task RpmOnlyBusyUpdatesPreservePendingGestureAndFreshContinuation()
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello(responsiveInput: 1);
        var applying = f.Protocol.ReceiveAsync(f.Set(1, 60)); var pending = f.State;
        foreach (var rpm in new int?[] { 0, 1850, 1_000_000, null })
        {
            f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { RpmMin = rpm, RpmMax = rpm, RpmCount = rpm is null ? 0 : 1 };
            f.Clock.Milliseconds += 500; f.Protocol.Publish();
            Assert.Equal(pending.GetProperty("stateId").GetString(), f.State.GetProperty("stateId").GetString());
            Assert.Equal(pending.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
            Assert.Equal("pending", f.State.GetProperty("ackStatus").GetString()); Assert.Equal("適用中", f.State.GetProperty("message").GetString());
        }
        f.Backend.Pending.SetResult("確認済み"); await applying;
        Assert.Equal(pending.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString());
        f.Backend.Pending = null; await f.Protocol.ReceiveAsync(f.Set(2, 80)); Assert.Equal(new[] { 60, 80 }, f.Backend.Calls.Select(c => c.Value));
    }
    [Theory][InlineData("id")][InlineData("label")][InlineData("kind")][InlineData("value")][InlineData("min")][InlineData("max")][InlineData("step")]
    [InlineData("enabled")][InlineData("busy")][InlineData("message")][InlineData("version")][InlineData("context")][InlineData("mixed")][InlineData("members")]
    public async Task ExcludingRpmFromSignaturePreservesEveryControlFieldGuard(string field)
    {
        var f = new Fixture();
        if (field == "mixed") f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { MemberCount = 2 };
        await f.Hello(responsiveInput: 1); var before = f.State; var target = f.Backend.View.Targets[0];
        target = field switch { "id" => target with { Id = "other" }, "label" => target with { Label = "renamed" }, "kind" => target with { Kind = "lighting" },
            "value" => target with { Value = 51 }, "min" => target with { Minimum = 30 }, "max" => target with { Maximum = 90 }, "step" => target with { Step = 2 },
            "enabled" => target with { Enabled = false }, "busy" => target with { Busy = true }, "message" => target with { Message = "changed" },
            "version" => target with { Version = "changed" }, "context" => target with { ControlContext = "changed" },
            "mixed" => target with { ValueMixed = true }, "members" => target with { MemberCount = 2 }, _ => throw new Exception(field) };
        f.Backend.View.Targets[0] = target with { RpmMin = 1900, RpmMax = 1900, RpmCount = 1 }; f.Protocol.Publish();
        Assert.NotEqual(before.GetProperty("stateId").GetString(), f.State.GetProperty("stateId").GetString());
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        await f.Protocol.ReceiveAsync(f.Set(1, 61, before)); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public async Task MissingAndNoTargetRpmPublishNullRangeAndZeroCount()
    {
        var f = new Fixture(); await f.Hello();
        Assert.Equal(JsonValueKind.Null, f.State.GetProperty("rpmMin").ValueKind); Assert.Equal(JsonValueKind.Null, f.State.GetProperty("rpmMax").ValueKind); Assert.Equal(0, f.State.GetProperty("rpmCount").GetInt32());
        f.Backend.View = new(true, []); f.Protocol.Publish();
        Assert.Equal(JsonValueKind.Null, f.State.GetProperty("rpmMin").ValueKind); Assert.Equal(JsonValueKind.Null, f.State.GetProperty("rpmMax").ValueKind); Assert.Equal(0, f.State.GetProperty("rpmCount").GetInt32());
        Assert.Equal(0, f.State.GetProperty("memberCount").GetInt32());
    }
    [Fact] public async Task GlobalTemperatureChangesAndMissingReadingsPreserveTokensAckAndConfirmation()
    {
        var f = new Fixture(); f.Backend.View = f.Backend.View with { CpuTempDeciC = 526, GpuTempDeciC = 441, WaterTempDeciC = 312 };
        f.Backend.Pending = new(); await f.Hello(responsiveInput: 1);
        var applying = f.Protocol.ReceiveAsync(f.Set(1, 60)); f.Backend.Pending.SetResult("確認 1/1・適用済み"); await applying; var before = f.State;
        foreach (var value in new int?[] { 0, 1500, 313, null })
        {
            f.Backend.View = f.Backend.View with { CpuTempDeciC = value, GpuTempDeciC = value, WaterTempDeciC = value }; f.Protocol.Publish();
            Assert.Equal(before.GetProperty("stateId").GetString(), f.State.GetProperty("stateId").GetString());
            Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
            Assert.Equal("confirmed", f.State.GetProperty("ackStatus").GetString()); Assert.Equal(1, f.State.GetProperty("ackSeq").GetInt32());
            Assert.Equal("確認 1/1・適用済み", f.State.GetProperty("message").GetString());
            foreach (var name in new[] { "cpuTempDeciC", "gpuTempDeciC", "waterTempDeciC" })
                if (value is null) Assert.Equal(JsonValueKind.Null, f.State.GetProperty(name).ValueKind); else Assert.Equal(value, f.State.GetProperty(name).GetInt32());
        }
        f.Backend.Pending = null; await f.Protocol.ReceiveAsync(f.Set(2, 70, before)); Assert.Equal(new[] { 60, 70 }, f.Backend.Calls.Select(c => c.Value));
    }
    [Fact] public async Task GlobalTemperatureBusyUpdatesPreserveGestureAndControlChangeStillCancelsIt()
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello(responsiveInput: 1); var applying = f.Protocol.ReceiveAsync(f.Set(1, 60)); var before = f.State;
        f.Backend.View = f.Backend.View with { CpuTempDeciC = 570, GpuTempDeciC = 480, WaterTempDeciC = null }; f.Protocol.Publish();
        Assert.Equal(before.GetProperty("stateId").GetString(), f.State.GetProperty("stateId").GetString());
        Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        Assert.Equal("pending", f.State.GetProperty("ackStatus").GetString());
        f.Backend.Pending.SetResult("confirmed"); await applying;
        Assert.Equal(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
        f.Backend.Pending = new(); applying = f.Protocol.ReceiveAsync(f.Set(2, 80)); before = f.State;
        f.Backend.View.Targets[0] = f.Backend.View.Targets[0] with { ControlContext = "changed-safety" };
        f.Backend.View = f.Backend.View with { WaterTempDeciC = 320 }; f.Protocol.Publish(); f.Backend.Pending.SetResult("confirmed"); await applying;
        Assert.NotEqual(before.GetProperty("operationId").GetString(), f.State.GetProperty("operationId").GetString());
    }
    [Fact] public async Task GlobalTemperaturesRemainOnTargetSwitchAndWhenNoCoolingTargetsExist()
    {
        var f = new Fixture(); await f.Hello(); Assert.Equal(JsonValueKind.Null, f.State.GetProperty("waterTempDeciC").ValueKind);
        f.Backend.View = f.Backend.View with { CpuTempDeciC = 520, GpuTempDeciC = 430, WaterTempDeciC = 310 }; f.Protocol.Publish();
        await f.Protocol.ReceiveAsync(f.Input(1, "press", 0)); Assert.Equal("light-b", f.State.GetProperty("targetId").GetString());
        Assert.Equal(520, f.State.GetProperty("cpuTempDeciC").GetInt32()); Assert.Equal(310, f.State.GetProperty("waterTempDeciC").GetInt32());
        f.Backend.View = f.Backend.View with { Targets = [] }; f.Protocol.Publish(); Assert.Equal(0, f.State.GetProperty("count").GetInt32());
        Assert.Equal(520, f.State.GetProperty("cpuTempDeciC").GetInt32()); Assert.Equal(430, f.State.GetProperty("gpuTempDeciC").GetInt32());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CompletionForRemovedGroupDoesNotLabelTheFallbackTargetWithItsResult(bool heartbeat)
    {
        var f = new Fixture(); f.Backend.Pending = new(); await f.Hello(responsiveInput: 1);
        var applying = f.Protocol.ReceiveAsync(f.Set(1, 60)); f.Backend.View = f.Backend.View with { Targets = [f.Backend.View.Targets[1] with { Message = "ポンプの現在値" }] };
        if (heartbeat) f.Protocol.Publish();
        f.Backend.Pending.SetResult("確認 9/9・全台に適用しました"); await applying;
        Assert.Equal("light-b", f.State.GetProperty("targetId").GetString()); Assert.Equal("ポンプの現在値", f.State.GetProperty("message").GetString());
    }
}
