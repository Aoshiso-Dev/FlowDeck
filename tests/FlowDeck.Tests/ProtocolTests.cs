using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public class ProtocolTests
{
    private sealed class Peer(Fixture f)
    {
        public M5ProtocolSession Protocol { get; } = new(f.Core, "test-device", time:f.Clock);
        public System.Collections.Concurrent.ConcurrentQueue<JsonElement> Messages { get; } = new();
        public void Hello(string boot="boot")
        {
            if (Messages.IsEmpty) Protocol.Outgoing += line => { using var d = JsonDocument.Parse(line); Messages.Enqueue(d.RootElement.Clone()); };
            Protocol.Receive(JsonSerializer.Serialize(new {v=1,type="hello",deviceType="M5GO",deviceId="test-device",bootId=boot,firmwareVersion="1.0.0"}));
        }
        public void Send(string type,Dictionary<string,object?>? data=null)
        { data??=[]; data["v"]=1; data["type"]=type; data["sessionId"]=Protocol.SessionId; data["controllerEpoch"]=f.Core.Epoch; Protocol.Receive(JsonSerializer.Serialize(data)); }
        public void Ack(JsonElement state) => Send("stateApplied",new(){["snapshotSequence"]=state.GetProperty("snapshotSequence").GetInt64(),["controlRevision"]=state.GetProperty("controlRevision").GetInt64(),["lightingRevision"]=state.GetProperty("lightingRevision").GetInt64()});
        public JsonElement LatestState => Messages.Last(m=>m.GetProperty("type").GetString()=="state");
        public void Manual(long number,string id,int duty,long revision) => Send("command",new(){["id"]=id,["commandSequence"]=number,["expectedRevision"]=revision,["command"]="SetManualDuty",["payload"]=new{duty}});
    }
    [Fact] public async Task SendingStateDoesNotEstablishSync()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();Assert.True(p.Protocol.Connected);Assert.False(p.Protocol.Synchronized);p.Ack(p.LatestState);Assert.True(p.Protocol.Synchronized); }
    [Fact] public async Task UnknownAckRejected()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();p.Send("stateApplied",new(){["snapshotSequence"]=999,["controlRevision"]=0,["lightingRevision"]=0});Assert.False(p.Protocol.Synchronized); }
    [Fact] public async Task RevisionChangeWaitsForAckButTelemetryDoesNotFlicker()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();p.Ack(p.LatestState);await f.Core.TickAsync();p.Protocol.SendState();Assert.True(p.Protocol.Synchronized);await f.Manual(54);p.Protocol.SendState();Assert.False(p.Protocol.Synchronized);p.Ack(p.LatestState);Assert.True(p.Protocol.Synchronized); }
    [Fact] public async Task OldSessionDoesNotRenewLiveness()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();f.Clock.Advance(2);p.Protocol.Receive(JsonSerializer.Serialize(new{v=1,type="heartbeat",sessionId="old",controllerEpoch=f.Core.Epoch}));f.Clock.Advance(1);Assert.False(p.Protocol.Connected); }
    [Fact] public async Task MalformedFrameDoesNotRenewLiveness()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();f.Clock.Advance(2);p.Protocol.Receive("invalid");p.Send("unknownMessage");f.Clock.Advance(1);Assert.False(p.Protocol.Connected); }
    [Fact] public async Task WrongDeviceCannotHandshake()
    { await using var f=new Fixture();await f.Initialize();var p=new M5ProtocolSession(f.Core,"expected",time:f.Clock);p.Receive(JsonSerializer.Serialize(new{v=1,type="hello",deviceType="M5GO",deviceId="other",bootId="boot",firmwareVersion="1"}));Assert.False(p.Connected); }
    [Fact] public async Task HeartbeatCannotRevalidateOldMeasurements()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();f.Clock.Advance(2);p.Send("heartbeat");f.Clock.Advance(1);p.Protocol.SendState();Assert.True(p.Protocol.Connected);Assert.Equal(JsonValueKind.Null,p.LatestState.GetProperty("fan").GetProperty("confirmedDuty").ValueKind); }
    [Fact] public async Task DuplicateCommandIsNotExecutedTwice()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();var rev=f.Core.Snapshot.ControlRevision;p.Manual(1,"a",54,rev);await Wait(()=>f.Hub.Writes==1&&!f.Core.Snapshot.FanBusy);p.Manual(1,"a",54,rev);await Task.Delay(30);Assert.Equal(1,f.Hub.Writes); }
    [Fact] public async Task CacheEvictionCannotReplayPastSequence()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();var rev=f.Core.Snapshot.ControlRevision;p.Manual(10,"first",54,rev);await Wait(()=>f.Hub.Writes==1&&!f.Core.Snapshot.FanBusy);p.Manual(9,"uncached",60,f.Core.Snapshot.ControlRevision);Assert.Equal(1,f.Hub.Writes);Assert.Contains(p.Messages,m=>m.TryGetProperty("error",out var e)&&e.GetString()=="OLD_SEQUENCE"); }
    [Fact] public async Task DuplicateIdWithDifferentSequenceIsRejected()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();var rev=f.Core.Snapshot.ControlRevision;p.Manual(1,"same",54,rev);await Wait(()=>f.Hub.Writes==1&&!f.Core.Snapshot.FanBusy);p.Manual(2,"same",54,rev);Assert.Equal(1,f.Hub.Writes);Assert.Contains(p.Messages,m=>m.TryGetProperty("error",out var e)&&e.GetString()=="DUPLICATE_ID"); }
    [Fact] public async Task ReconnectChangesSessionAndRejectsOldAck()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();var oldSession=p.Protocol.SessionId;var previous=p.LatestState;p.Hello("newboot");Assert.NotEqual(oldSession,p.Protocol.SessionId);var ack=new Dictionary<string,object?>{["v"]=1,["type"]="stateApplied",["sessionId"]=oldSession,["controllerEpoch"]=f.Core.Epoch,["snapshotSequence"]=previous.GetProperty("snapshotSequence").GetInt64(),["controlRevision"]=0,["lightingRevision"]=0};p.Protocol.Receive(JsonSerializer.Serialize(ack));Assert.False(p.Protocol.Synchronized); }
    [Fact] public async Task StateContractFitsM5FrameLimit()
    { await using var f=new Fixture();await f.Initialize();var p=new Peer(f);p.Hello();Assert.True(Encoding.UTF8.GetByteCount(p.LatestState.GetRawText())+1<=4096);Assert.Equal(3,p.LatestState.GetProperty("profiles").GetArrayLength()); }
    private static async Task Wait(Func<bool> predicate) { for(var i=0;i<100;i++){if(predicate())return;await Task.Delay(10);}throw new TimeoutException(); }
}
