using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal sealed record OpenRgbSdkTarget(Guid Connection, long InventoryRevision, uint ControllerId, string Structure);
internal sealed record OpenRgbSdkSnapshot(OpenRgbSdkTarget Target, OpenRgbSdkController Controller, long Observation);
internal sealed record OpenRgbSdkAcceptance(OpenRgbSdkTarget Target, uint Command);
internal sealed class OpenRgbSdkObservationChangedException() : IOException("OpenRGBの状態が取得中に変わりました。再取得してください");

// Only the SDK's acceptance is established here; neither exclusive ownership nor physical light output is proved.
// A driver must establish its resource ownership and recovery record before authorizing a write.
internal sealed partial class OpenRgbSdkConnection : IAsyncDisposable
{
    private sealed class Pending(uint device, uint command, uint? response, bool permitRejectedAck)
    {
        public readonly uint Device = device, Command = command;
        public readonly uint? Response = response;
        public readonly bool PermitRejectedAck = permitRejectedAck;
        public readonly TaskCompletionSource<byte[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Data;
        public bool Acknowledged;
    }

    private readonly object sync = new();
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;
    private readonly Task receiver;
    private readonly Guid connection = Guid.NewGuid();
    private Dictionary<uint, OpenRgbSdkController> controllers = [];
    private Dictionary<uint, long> deviceObservations = [];
    private Pending? pending;
    private Exception? failure;
    private long revision, observation;
    private int queued;
    private bool ready, detecting;
    private uint serverFlags;
    private readonly bool ownedHeadless;
    private readonly Action? authorizeWrite;
    public bool IsConnected { get { lock (sync) return failure is null; } }
    public long InventoryRevision { get { lock (sync) return revision; } }
    public long Observation { get { lock (sync) return observation; } }

    private OpenRgbSdkConnection(TcpClient client, TimeSpan timeout, TimeProvider clock, bool ownedHeadless, Action? authorizeWrite)
    {
        this.client = client; stream = client.GetStream(); this.timeout = timeout; this.clock = clock; this.ownedHeadless = ownedHeadless;
        this.authorizeWrite = authorizeWrite;
        receiver = ReceiveAsync();
    }

    public static async Task<OpenRgbSdkConnection> ConnectAsync(int port, CancellationToken ct = default, TimeSpan? operationTimeout = null,
        TimeProvider? clock = null, Action<IPEndPoint, IPEndPoint>? verifyPeer = null, bool ownedHeadless = false, Action? authorizeWrite = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (ownedHeadless && verifyPeer is null) throw new ArgumentException("所有プロセスの照合が必要です", nameof(verifyPeer));
        var timeout = operationTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout < TimeSpan.FromMilliseconds(20) || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        OpenRgbSdkConnection? result = null;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
            verifyPeer?.Invoke((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!);
            result = new(client, timeout, clock ?? TimeProvider.System, ownedHeadless, authorizeWrite);
            await result.ExecuteAsync(async token =>
            {
                var version = OpenRgbSdkProtocol.UInt(await result.RequestAsync(0, 40, OpenRgbSdkProtocol.UInt(6), 40, token).ConfigureAwait(false));
                if (version < 6) throw new IOException("OpenRGBの制御にはSDK 6以降が必要です");
                await result.RequestAsync(0, 50, "FlowDeck\0"u8.ToArray(), null, token).ConfigureAwait(false);
                // RGB only: do not request privileged local-client configuration or unrelated APIs.
                var flags = OpenRgbSdkProtocol.UInt(await result.RequestAsync(0, 52, OpenRgbSdkProtocol.UInt(1), 53, token).ConfigureAwait(false));
                if ((flags & 1) == 0) throw new IOException("OpenRGBサーバーがRGB操作に対応していません");
                result.serverFlags = flags;
                return true;
            }, deadline.Token).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (result is not null) await result.DisposeAsync().ConfigureAwait(false); else client.Dispose();
            throw;
        }
    }

    public Task<OpenRgbSdkSnapshot[]> ScanAsync(CancellationToken ct = default, Action? authorize = null) => ExecuteAsync(async token =>
    {
        long start, observed;
        lock (sync)
        {
            if (detecting) throw new IOException("OpenRGBが機器を検出中です");
            start = revision; observed = observation;
        }
        var countData = await RequestAsync(0, 0, [], 0, token, authorize).ConfigureAwait(false);
        var ids = Parse(() => OpenRgbSdkProtocol.ControllerIds(countData));
        var found = new Dictionary<uint, OpenRgbSdkController>();
        foreach (var id in ids)
        {
            lock (sync) if (start != revision) throw Stale();
            var data = await RequestAsync(id, 1, OpenRgbSdkProtocol.UInt(6), 1, token, authorize).ConfigureAwait(false);
            found.Add(id, Parse(() => OpenRgbSdkProtocol.Controller(id, data)));
        }
        lock (sync)
        {
            CheckConnected();
            if (start != revision || observed != observation || detecting) throw Stale();
            var changed = !ready || controllers.Count != found.Count || found.Any(p => !controllers.TryGetValue(p.Key, out var previous) ||
                OpenRgbSdkProtocol.Structure(p.Value) != OpenRgbSdkProtocol.Structure(previous));
            if (changed) revision++;
            controllers = found; ready = true; observation++;
            deviceObservations = ids.ToDictionary(id => id, id => deviceObservations.GetValueOrDefault(id) + 1);
            return ids.Select(id => Snapshot(controllers[id])).ToArray();
        }
    }, ct);

    public OpenRgbSdkSnapshot Reported(OpenRgbSdkTarget target)
    {
        lock (sync) return Snapshot(CheckTarget(target));
    }

    public Task<OpenRgbSdkSnapshot> ReadAsync(OpenRgbSdkTarget target, CancellationToken ct = default, Action? authorize = null) => ExecuteAsync(async token =>
    {
        long observed;
        lock (sync) { _ = CheckTarget(target); observed = deviceObservations.GetValueOrDefault(target.ControllerId); }
        var data = await RequestAsync(target.ControllerId, 1, OpenRgbSdkProtocol.UInt(6), 1, token, () => { _ = CheckTarget(target); authorize?.Invoke(); }).ConfigureAwait(false);
        var controller = Parse(() => OpenRgbSdkProtocol.Controller(target.ControllerId, data));
        lock (sync)
        {
            _ = CheckTarget(target);
            // Do not replace a newer asynchronous observation with an older query response.
            if (observed != deviceObservations.GetValueOrDefault(target.ControllerId)) throw new OpenRgbSdkObservationChangedException();
            if (OpenRgbSdkProtocol.Structure(controller) != target.Structure) { Invalidate(); throw Stale(); }
            controllers[target.ControllerId] = controller; observation++; deviceObservations[target.ControllerId] = observed + 1; return Snapshot(controller);
        }
    }, ct);

    public Task<OpenRgbSdkAcceptance> WriteColorsAsync(OpenRgbSdkTarget target, RgbColor[] colors, Action authorize,
        int? zone = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        // Freeze the caller's buffer before queueing; a subsequent edit must not change an authorized request.
        var payload = OpenRgbSdkProtocol.UpdateColors(colors, zone);
        var count = colors.Length;
        return WriteAsync(target, zone is null ? 1050u : 1051u, payload, controller =>
        {
            var expected = zone is { } z ? Zone(controller, z).LedCount : (uint)controller.Leds.Length;
            if (count == 0 || count != expected) throw new IOException("OpenRGBの対象と送信LED数が一致しません");
        }, authorize, ct);
    }

    public Task<OpenRgbSdkAcceptance> WriteModeAsync(OpenRgbSdkTarget target, int index, OpenRgbSdkMode mode, Action authorize,
        int? zone = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        var frozen = mode with { Colors = mode.Colors.ToArray() };
        var payload = OpenRgbSdkProtocol.UpdateMode(index, frozen, zone);
        return WriteAsync(target, zone is null ? 1101u : 1103u, payload, controller =>
        {
            var modes = zone is { } z ? Zone(controller, z).Modes : controller.Modes;
            if (index < 0 || index >= modes.Length) throw new IOException("OpenRGBの効果が範囲外です");
            if (zone is not null && (modes[index].Flags & 1024) != 0) throw new IOException("このOpenRGB効果は機器全体の操作が必要です");
            CheckMode(modes[index], frozen);
        }, authorize, ct);
    }

    public Task<OpenRgbSdkAcceptance> InheritControllerModeAsync(OpenRgbSdkTarget target, int zone, Action authorize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        return WriteAsync(target, 1103, OpenRgbSdkProtocol.InheritControllerMode(zone), c => _ = Zone(c, zone), authorize, ct);
    }

    private Task<OpenRgbSdkAcceptance> WriteAsync(OpenRgbSdkTarget target, uint command, byte[] payload,
        Action<OpenRgbSdkController> validate, Action authorize, CancellationToken ct) => ExecuteAsync(async token =>
    {
        await RequestAsync(target.ControllerId, command, payload, null, token, () =>
        { CheckMutations(); validate(CheckTarget(target)); authorize(); authorizeWrite?.Invoke(); }).ConfigureAwait(false);
        lock (sync) _ = CheckTarget(target);
        return new OpenRgbSdkAcceptance(target, command);
    }, ct);

    private static OpenRgbSdkZone Zone(OpenRgbSdkController controller, int index) => index >= 0 && index < controller.Zones.Length
        ? controller.Zones[index] : throw new IOException("OpenRGBのゾーンが範囲外です");

    internal static void CheckMode(OpenRgbSdkMode basis, OpenRgbSdkMode requested)
    {
        if (basis.Name != requested.Name || basis.Flags != requested.Flags || basis.SpeedMin != requested.SpeedMin || basis.SpeedMax != requested.SpeedMax ||
            basis.BrightnessMin != requested.BrightnessMin || basis.BrightnessMax != requested.BrightnessMax || basis.ColorsMin != requested.ColorsMin || basis.ColorsMax != requested.ColorsMax)
            throw new IOException("OpenRGBの効果定義が変わりました");
        bool Range(uint value, uint min, uint max) => value >= Math.Min(min, max) && value <= Math.Max(min, max);
        if (!Range(requested.Speed, basis.SpeedMin, basis.SpeedMax) || (basis.Flags & 1) == 0 && requested.Speed != basis.Speed ||
            !Range(requested.Brightness, basis.BrightnessMin, basis.BrightnessMax) || (basis.Flags & 16) == 0 && requested.Brightness != basis.Brightness ||
            requested.ColorMode > 3 || requested.ColorMode == 0 && (basis.Flags & (32 | 64 | 128)) != 0 ||
            requested.ColorMode == 1 && (basis.Flags & 32) == 0 || requested.ColorMode == 2 && (basis.Flags & 64) == 0 ||
            requested.ColorMode == 3 && (basis.Flags & 128) == 0 || (basis.Flags & 64) != 0 && !Range((uint)requested.Colors.Length, basis.ColorsMin, basis.ColorsMax))
            throw new IOException("OpenRGBの効果パラメーターが範囲外です");
        // The pinned SDK advertises diagonal flags, but its SetModeValuesFromMode only accepts LR/UD/HV.
        // An ACK can still report success when that function rejects the values. Do not offer unsupported directions.
        var directionFlag = requested.Direction switch { 0 or 1 => 2u, 2 or 3 => 4u, 4 or 5 => 8u, _ => 0u };
        if (!((basis.Flags & (2 | 4 | 8)) == 0 && requested.Direction == 0) && (directionFlag == 0 || (basis.Flags & directionFlag) == 0))
            throw new IOException("OpenRGBがこの方向に対応していません");
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (Interlocked.Increment(ref queued) > 64) { Interlocked.Decrement(ref queued); throw new IOException("OpenRGBの操作待ちが上限に達しました"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); deadline.CancelAfter(timeout);
        var entered = false;
        try
        {
            await operations.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            lock (sync) CheckConnected();
            return await action(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fault cancels the receiver and queued operations. Preserve the protocol/socket cause
            // instead of racing its pending completion against that internal cancellation.
            lock (sync)
                if (failure is not null and not OperationCanceledException and not ObjectDisposedException)
                    throw new IOException("OpenRGBとの通信に失敗しました", failure);
            throw;
        }
        finally { if (entered) operations.Release(); Interlocked.Decrement(ref queued); }
    }

    private async Task<byte[]> RequestAsync(uint device, uint command, byte[] data, uint? response, CancellationToken ct, Action? beforeSend = null, bool permitRejectedAck = false)
    {
        var packet = OpenRgbSdkProtocol.Packet(device, command, data);
        Pending request;
        ValueTask sending;
        // Validation failures before a packet is sent leave the connection reusable.
        lock (sync)
        {
            CheckConnected(); ct.ThrowIfCancellationRequested(); beforeSend?.Invoke();
            if (permitRejectedAck && (!ownedHeadless || response is not null || command is not (1050 or 1051 or 1101)))
                throw new IOException("OpenRGBの拒否応答を保持できない要求です");
            request = new(device, command, response, permitRejectedAck); pending = request;
            try { sending = stream.WriteAsync(packet, ct); }
            catch (Exception e) { Fault(e); throw; }
        }
        try
        {
            await sending.ConfigureAwait(false);
            return await request.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OpenRgbSdkRejectedException) when (permitRejectedAck) { throw; }
        catch (Exception e)
        {
            // An uncertain in-flight packet is never retried on this session. Late ACKs cannot authorize another write.
            Fault(e); throw;
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (true)
            {
                var header = new byte[16]; await stream.ReadExactlyAsync(header, lifetime.Token).ConfigureAwait(false);
                var (device, command, length) = OpenRgbSdkProtocol.Header(header);
                var data = new byte[length]; await stream.ReadExactlyAsync(data, lifetime.Token).ConfigureAwait(false);
                lock (sync) Receive(device, command, data);
            }
        }
        catch (Exception e) { Fault(e); }
    }

    private void Receive(uint device, uint command, byte[] data)
    {
        if (command == 1150)
        {
            var signal = OpenRgbSdkProtocol.Signal(device, data); observation++;
            if (!controllers.TryGetValue(device, out var previous)) { if (ready) Invalidate(); return; }
            deviceObservations[device] = deviceObservations.GetValueOrDefault(device) + 1;
            if (signal.Colors is { } colors)
            {
                if (colors.Length != previous.Leds.Length) { Invalidate(); return; }
                controllers[device] = previous with { Colors = colors };
            }
            else if (signal.Controller is { } current)
            {
                if (OpenRgbSdkProtocol.Structure(current) != OpenRgbSdkProtocol.Structure(previous) || signal.Reason is >= 3)
                { Invalidate(); return; }
                controllers[device] = current;
            }
            return;
        }
        if (command is 100 or 101 or 103)
        {
            if (device != 0 || data.Length != 0) throw new IOException("OpenRGBの一覧通知が不正です");
            if (command == 101) detecting = true; else if (command == 103) detecting = false;
            Invalidate(); return;
        }
        if (command == 102)
        {
            if (device != 0) throw new IOException("OpenRGBの検出通知が不正です");
            OpenRgbSdkProtocol.DetectionProgress(data); return;
        }
        if (command == 51)
        {
            if (device != 0) throw new IOException("OpenRGBのサーバー通知が不正です");
            _ = OpenRgbSdkProtocol.NullText(data); return;
        }
        var request = pending ?? throw new IOException("OpenRGBから要求に対応しない応答を受信しました");
        if (device != request.Device) throw new IOException("OpenRGBの応答先が一致しません");
        if (command == 10)
        {
            if (data.Length != 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != request.Command || request.Acknowledged)
                throw new IOException("OpenRGBの受理応答が一致しません");
            var status = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            if (status is >= 1 and <= 5 && request.PermitRejectedAck)
            {
                mutationsBlocked = true; pending = null;
                request.Completion.TrySetException(new OpenRgbSdkRejectedException(status)); return;
            }
            if (status != 0) throw new IOException(status <= 5 ? $"OpenRGBが操作を拒否しました（状態 {status}）" : "OpenRGBの受理状態が不正です");
            request.Acknowledged = true;
        }
        else
        {
            if (request.Response != command || request.Data is not null) throw new IOException("OpenRGBの応答種別が一致しません");
            if (command == 40 && OpenRgbSdkProtocol.UInt(data) < 6) throw new IOException("OpenRGBの制御にはSDK 6以降が必要です");
            request.Data = data;
        }
        if (request.Acknowledged && (request.Response is null || request.Data is not null))
        { pending = null; request.Completion.TrySetResult(request.Data ?? []); }
    }

    private OpenRgbSdkSnapshot Snapshot(OpenRgbSdkController controller) => new(
        new(connection, revision, controller.Id, OpenRgbSdkProtocol.Structure(controller)), OpenRgbSdkProtocol.Copy(controller), observation);
    private OpenRgbSdkController CheckTarget(OpenRgbSdkTarget target)
    {
        CheckConnected();
        if (!ready || detecting || target.Connection != connection || target.InventoryRevision != revision || !controllers.TryGetValue(target.ControllerId, out var controller) ||
            target.Structure != OpenRgbSdkProtocol.Structure(controller)) throw Stale();
        return controller;
    }
    private static IOException Stale() => new("OpenRGBの機器構成が変わりました。再取得と対応確認が必要です");
    private T Parse<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (IOException e) { Fault(e); throw; }
    }
    private void Invalidate() { revision++; ready = false; }
    private void CheckConnected() { if (failure is not null) throw new IOException("OpenRGBとの接続が終了しています", failure); }
    private void Fault(Exception error)
    {
        lock (sync)
        {
            if (failure is not null) return;
            failure = error; Invalidate(); pending?.Completion.TrySetException(error); pending = null;
            lifetime.Cancel(); client.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Fault(new ObjectDisposedException(nameof(OpenRgbSdkConnection)));
        await receiver.ConfigureAwait(false);
        // Gate/token source may still be referenced by canceled operations unwinding on other threads.
    }
}
