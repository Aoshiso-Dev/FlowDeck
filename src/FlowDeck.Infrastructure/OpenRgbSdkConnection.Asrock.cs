using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed class OpenRgbSdkRejectedException(uint status) : IOException($"OpenRGBが操作を拒否しました（状態 {status}）")
{ public uint Status { get; } = status; }
internal sealed record AsrockOpenRgbExecution(OpenRgbSdkTarget Target, uint Command, uint Acknowledgement,
    AsrockOpenRgbOperationReceipt Before, AsrockOpenRgbOperationReceipt After)
{
    public bool Completed => Acknowledgement == 0 && After.Stage == AsrockOpenRgbOperationStage.Completed;
    public bool Entered => After.Sequence != Before.Sequence;
}

internal sealed partial class OpenRgbSdkConnection
{
    internal AsrockAuthorizationOrigin AuthorizationOrigin(OpenRgbSdkTarget target, uint command, bool inFlight = false)
    {
        lock (sync)
        {
            CheckAsrockTarget(target);
            if (command is not (1050 or 1051 or 1101) ||
                client.Client.LocalEndPoint is not System.Net.IPEndPoint local ||
                client.Client.RemoteEndPoint is not System.Net.IPEndPoint remote ||
                !local.Address.Equals(System.Net.IPAddress.Loopback) || !remote.Address.Equals(System.Net.IPAddress.Loopback) ||
                remote.Port < 1024 || local.Port == 0 ||
                inFlight && (pending is null || pending.Device != target.ControllerId || pending.Command != command))
                throw new IOException("ASRockの認可対象と進行中のSDK接続が一致しません");
            return new(true, 6, command, target.ControllerId, checked((ushort)remote.Port), checked((ushort)local.Port));
        }
    }
    private bool mutationsBlocked;
    private void CheckMutations()
    {
        if (mutationsBlocked) throw new IOException("OpenRGBの前回の操作を復旧するまで追加の書き込みはできません");
    }
    private void CheckAsrockTarget(OpenRgbSdkTarget target)
    {
        if (!ownedHeadless) throw new IOException("ASRock操作記録には所有済み専用OpenRGBが必要です");
        _ = CheckTarget(target);
    }
    private async Task<AsrockOpenRgbOperationReceipt> ReadAsrockCoreAsync(OpenRgbSdkTarget target, Action authorize, CancellationToken ct)
    {
        var data = await RequestAsync(target.ControllerId, AsrockOpenRgbOperationReceipt.QueryCommand, [],
            AsrockOpenRgbOperationReceipt.QueryCommand, ct, () => { CheckAsrockTarget(target); authorize(); }).ConfigureAwait(false);
        var result = Parse(() => AsrockOpenRgbOperationReceipt.Parse(data));
        lock (sync) CheckAsrockTarget(target);
        return result;
    }
    public Task<AsrockOpenRgbOperationReceipt> ReadAsrockOperationAsync(OpenRgbSdkTarget target, Action authorize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        return ExecuteAsync(token => ReadAsrockCoreAsync(target, authorize, token), ct);
    }
    public Task<AsrockOpenRgbExecution> WriteAsrockColorsAsync(OpenRgbSdkTarget target, AsrockOpenRgbOperationReceipt expected,
        RgbColor[] colors, Action authorize, int? zone = null, CancellationToken ct = default, Action? beforeWrite = null)
    {
        var payload = OpenRgbSdkProtocol.UpdateColors(colors, zone); var count = colors.Length;
        return WriteAsrockAsync(target, expected, zone is null ? 1050u : 1051u, payload, controller =>
        {
            var required = zone is { } z ? Zone(controller, z).LedCount : (uint)controller.Leds.Length;
            if (count == 0 || count != required) throw new IOException("OpenRGBの対象と送信LED数が一致しません");
        }, authorize, ct, beforeWrite);
    }
    public Task<AsrockOpenRgbExecution> WriteAsrockModeAsync(OpenRgbSdkTarget target, AsrockOpenRgbOperationReceipt expected,
        int index, OpenRgbSdkMode mode, Action authorize, CancellationToken ct = default, Action? beforeWrite = null)
    {
        var frozen = mode with { Colors = mode.Colors.ToArray() };
        return WriteAsrockAsync(target, expected, 1101, OpenRgbSdkProtocol.UpdateMode(index, frozen, null), controller =>
        {
            if (index < 0 || index >= controller.Modes.Length) throw new IOException("OpenRGBの効果が範囲外です");
            CheckMode(controller.Modes[index], frozen);
        }, authorize, ct, beforeWrite);
    }
    private Task<AsrockOpenRgbExecution> WriteAsrockAsync(OpenRgbSdkTarget target, AsrockOpenRgbOperationReceipt expected,
        uint command, byte[] payload, Action<OpenRgbSdkController> validate, Action authorize, CancellationToken ct, Action? beforeWrite)
    {
        ArgumentNullException.ThrowIfNull(authorize); var frozen = expected.Copy();
        return ExecuteAsync(async token =>
        {
            lock (sync) { CheckAsrockTarget(target); CheckMutations(); validate(CheckTarget(target)); }
            var before = await ReadAsrockCoreAsync(target, authorize, token).ConfigureAwait(false);
            if (!before.Same(frozen) || before.Stage is not (AsrockOpenRgbOperationStage.Idle or AsrockOpenRgbOperationStage.Completed) || before.Sequence == ulong.MaxValue)
            {
                lock (sync) mutationsBlocked = true;
                throw new IOException("OpenRGBの前回のASRock操作記録が変わりました");
            }
            uint acknowledgement = 0;
            try
            {
                await RequestAsync(target.ControllerId, command, payload, null, token, () =>
                { CheckAsrockTarget(target); CheckMutations(); validate(CheckTarget(target)); authorize(); authorizeWrite?.Invoke(); beforeWrite?.Invoke(); }, permitRejectedAck: true).ConfigureAwait(false);
            }
            catch (OpenRgbSdkRejectedException rejected) { acknowledgement = rejected.Status; }
            // Known rejection leaves only reads available, so the failure receipt can be persisted before shutdown.
            try
            {
                var after = await ReadAsrockCoreAsync(target, authorize, token).ConfigureAwait(false);
                AsrockOpenRgbOperationReceipt.CheckResult(before, after, command, acknowledgement);
                return new AsrockOpenRgbExecution(target, command, acknowledgement, before, after);
            }
            catch
            {
                lock (sync) mutationsBlocked = true;
                throw;
            }
        }, ct);
    }
}
