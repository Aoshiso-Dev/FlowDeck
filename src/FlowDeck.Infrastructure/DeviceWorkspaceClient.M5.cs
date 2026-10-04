using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceM5SettingsEdit(string Epoch, string Session, string Generation, long Topology, DeviceM5Settings Settings);
public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsM5Settings => client.Hello?.Features.Contains("m5-settings-v2") == true;
    public async Task<DeviceConfigurationReply> SaveM5Async(DeviceM5SettingsEdit edit, CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!SupportsM5Settings) throw new IOException("M5GO設定に対応するサービスの更新が必要です");
            if (client.Hello?.Epoch != edit.Epoch || client.Hello.Session != edit.Session || configuration?.Generation != edit.Generation)
                throw new IOException("接続またはM5GO設定が変更されました。再取得してください");
            var reply = await client.SendAsync(client.Request(DeviceOperation.Configuration, configuration: new(DeviceConfigurationOperation.SaveM5,
                ExpectedGeneration: edit.Generation, TopologyRevision: edit.Topology, M5: edit.Settings)), ct).ConfigureAwait(false);
            if (!reply.Success || reply.Configuration is null) throw new IOException(reply.Error ?? "M5GOの設定を保存できません");
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false); return reply.Configuration;
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
    internal async Task<OperationResult> ApplyM5ControlAsync(DeviceM5Control backend, ControlRequest request, CancellationToken ct)
    {
        var acquired = false; var dispatched = false;
        try
        {
            await operations.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            EnsureOpen(); ct.ThrowIfCancellationRequested();
            var plan = backend.Capture(Snapshot ?? throw new IOException("サービスの状態を取得してください"), request);
            if (client.Hello?.Session != plan.Session || client.Hello.Epoch != plan.Epoch || configuration?.Generation != plan.Generation)
                throw new IOException("M5GOの接続・設定が変更されました");
            ct.ThrowIfCancellationRequested(); dispatched = true;
            var reply = await client.SendAsync(client.Request(plan.Operation, source: "M5GO", topology: plan.Topology,
                targets: plan.Targets, duty: plan.Duty, profile: plan.Profile, lighting: plan.Lighting), ct).ConfigureAwait(false);
            var result = reply.Result ?? throw new IOException(reply.Error ?? "M5GOの操作結果を取得できません");
            var next = await client.ReadConfigurationAsync(ct).ConfigureAwait(false); lock (gate) configuration = next;
            await ReadStateAsync(ct).ConfigureAwait(false);
            return backend.Result(request, result.Outcome, result.Message, result.Targets,
                result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? null : "CONTROL_INCOMPLETE");
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (!client.Connected) RecordError(e);
            return backend.Result(request, dispatched ? Outcome.Unknown : Outcome.Rejected,
                e is OperationCanceledException ? "接続が変更されました。現在の状態を確認してください" : e.Message,
                error: dispatched ? "CONTROL_UNKNOWN" : "CONTROL_REJECTED");
        }
        finally { if (acquired) operations.Release(); }
    }
    internal async Task<OperationResult> ApplyM5LightingAsync(M5LightingCommand request, CancellationToken ct)
    {
        var acquired = false; var dispatched = false;
        OperationResult Result(Outcome outcome, string message, string? code = null) => new(request.Id, "M5GO", request.Command,
            outcome, message, clock.GetUtcNow(), 0, 0, 0, ErrorCode: code);
        try
        {
            await operations.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            EnsureOpen(); ct.ThrowIfCancellationRequested();
            // Capture after the shared PC/M5 queue, not before waiting behind a GUI edit or reconnect.
            var edit = DeviceM5Lighting.Capture(Snapshot ?? throw new IOException("サービスの状態を取得してください"), request);
            ct.ThrowIfCancellationRequested(); dispatched = true;
            var result = await ApplyLightingCoreAsync(edit, "M5GO", ct).ConfigureAwait(false);
            return Result(result.Outcome, result.Message, result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified ? null : "LIGHTING_INCOMPLETE");
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (!client.Connected) RecordError(e);
            return Result(dispatched ? Outcome.Unknown : Outcome.Rejected,
                e is OperationCanceledException ? "接続が変更されました。現在の状態を確認してください" : e.Message,
                dispatched ? "LIGHTING_UNKNOWN" : "LIGHTING_REJECTED");
        }
        finally { if (acquired) operations.Release(); }
    }
}
