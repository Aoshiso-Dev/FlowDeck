using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceLightingIdentificationEdit(string Epoch, string Session, string Generation, long Topology, DeviceTargetVersion Target)
{
    public static DeviceLightingIdentificationEdit Capture(DeviceWorkspaceSnapshot snapshot, EndpointReference target)
    {
        if (!snapshot.Connected || !double.IsFinite(snapshot.ElapsedSeconds) || snapshot.ElapsedSeconds is < 0 or >= 3 || snapshot.Session is null)
            throw new IOException("識別する前に接続を更新してください");
        if (DeviceLightingIdentification.Problem(snapshot.State, target) is { } error) throw new IOException(error);
        var state = snapshot.State.Endpoints.Single(e => e.Target == target);
        return new(snapshot.State.Inventory.Epoch, snapshot.Session, snapshot.Configuration.Generation, snapshot.State.Inventory.TopologyRevision,
            new(target, state.Generation, state.PolicyRevision));
    }
}
public sealed partial class DeviceWorkspaceClient
{
    private sealed record IdentificationCall(DeviceServicePipeClient Client, DeviceServiceRequest Request, Task<DeviceServiceReply> Pending);
    private IdentificationCall? identificationCall;
    public bool SupportsLightingIdentification => client.Hello?.Features.Contains(DeviceLightingIdentification.Feature) == true;
    public bool IdentificationRunning { get { lock (gate) return identificationCall is not null; } }
    public async Task<DeviceOperationResult> IdentifyLightingAsync(DeviceLightingIdentificationEdit edit, CancellationToken ct = default)
    {
        IdentificationCall call;
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); if (!SupportsLightingIdentification || IdentificationRunning) throw new IOException("識別を開始できません");
            await ReadStateAsync(ct).ConfigureAwait(false);
            var target = state!.Endpoints.SingleOrDefault(e => e.Target == edit.Target.Target);
            if (client.Hello!.Epoch != edit.Epoch || client.Hello.Session != edit.Session || configuration!.Generation != edit.Generation ||
                state.Inventory.TopologyRevision != edit.Topology || target?.Generation != edit.Target.Generation || target.PolicyRevision != edit.Target.ExpectedPolicyRevision)
                throw new IOException("確認後に識別対象・接続・設定が変更されました");
            var request = client.Request(DeviceOperation.IdentifyLighting, topology: edit.Topology, targets: [edit.Target]);
            call = new(client, request, client.SendAsync(request, ct)); lock (gate) identificationCall = call;
        }
        finally { operations.Release(); }
        try
        {
            var reply = await call.Pending.ConfigureAwait(false);
            return reply.Result ?? throw new IOException(reply.Error ?? "識別結果を取得できません");
        }
        catch (Exception e) { if (!call.Client.Connected) RecordError(e); throw; }
        finally
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(client, call.Client) && !disposed && !released && !exitAttempted && client.Connected)
                {
                    var next = await client.ReadConfigurationAsync(CancellationToken.None).ConfigureAwait(false); lock (gate) configuration = next;
                    await ReadStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e) { RecordError(e); }
            finally { lock (gate) if (ReferenceEquals(identificationCall, call)) identificationCall = null; operations.Release(); }
        }
    }
    public async Task CancelLightingIdentificationAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen(); IdentificationCall? call; lock (gate) call = identificationCall;
            if (call is null || call.Pending.IsCompleted) return;
            if (!ReferenceEquals(client, call.Client)) throw new IOException("識別中の接続が変更されました");
            var reply = await client.SendAsync(client.Request(DeviceOperation.CancelLightingIdentification, resultId: call.Request.Id), ct).ConfigureAwait(false);
            if (!reply.Success && !call.Pending.IsCompleted) throw new IOException(reply.Error ?? "識別の中止を確認できません");
        }
        finally { operations.Release(); }
    }
}
