using FlowDeck.Core;
using HidSharp;

namespace FlowDeck.Infrastructure;

public record BoardLightChange(byte Zone, BoardLightSetting Expected, BoardLightSetting Desired);
public record BoardLightResult(byte Zone, string Status, BoardLightSetting? Reported = null, string? Error = null);
public record BoardLightApplyResult(BoardLightResult[] Zones)
{
    public bool Verified => Zones.Length > 0 && Zones.All(z => z.Status == "verified");
}

/// <summary>Bounded native-effect transactions. No initialization, header changes, flash commits or direct frames.</summary>
public sealed class AsrockRgbControl
{
    public Task<BoardLightApplyResult> ApplyAsync(string identity, BoardLightChange[] changes, CancellationToken ct = default)
        => Task.Run(() =>
        {
            Validate(changes);
            using var session = new AsrockRgbSession(identity);
            return Apply(session.Exchange, changes, ct);
        }, ct);

    private static void Validate(BoardLightChange[] changes)
    {
        if (changes.Length is < 1 or > 8 || changes.Any(c => c.Zone > 7 || !BoardLighting.IsValid(c.Desired) || c.Desired.MemoryPalette is not null) ||
            changes.DistinctBy(c => c.Zone).Count() != changes.Length)
            throw new ArgumentException("照明の対象・効果・色・速度が不正です");
    }

    // Exchange is injectable so failure, stale configuration and partial writes can be tested without hardware.
    public static BoardLightApplyResult Apply(Func<byte[], byte[]> exchange, BoardLightChange[] changes, CancellationToken ct = default)
    {
        Validate(changes);
        byte[] Query(byte op, byte selector)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = exchange(AsrockRgbMonitor.Query(op, selector));
            return AsrockRgbMonitor.Response(bytes, bytes.Length, selector);
        }
        var available = Query(0x14, 1)[4]; var counts = Query(0x14, 2); var order = Query(0x14, 3)[4];
        // Preflight every selected zone before the first write. Zone addresses are not collection indexes.
        foreach (var change in changes)
        {
            if ((available & (1 << change.Zone)) == 0 || counts[4 + change.Zone] is 0 or 0x1e)
                throw new IOException("対象のRGBゾーンが見つからないか、LED数が0です。再取得してください");
            if (Decode(Query(0x11, change.Zone), order) != change.Expected)
                throw new IOException("照明設定が変更されています。再取得してから適用してください");
        }
        var results = new List<BoardLightResult>();
        foreach (var change in changes)
        {
            try
            {
                // Read again immediately before writing, including the channel order. Never restore a stale order table.
                if (Query(0x14, 3)[4] != order || Decode(Query(0x11, change.Zone), order) != change.Expected)
                    throw new IOException("照明設定が別のアプリで変更されました");
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
            { results.Add(new(change.Zone, "notWritten", Error: e.Message)); break; }
            try
            {
                _ = exchange(Packet(change.Zone, change.Desired, order));
                var actual = Decode(Query(0x11, change.Zone), order);
                results.Add(new(change.Zone, actual == change.Desired ? "verified" : "mismatch", actual));
                if (actual != change.Desired) break;
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
            { results.Add(new(change.Zone, "unknown", Error: e.Message)); break; }
        }
        results.AddRange(changes.Skip(results.Count).Select(c => new BoardLightResult(c.Zone, "notWritten")));
        return new(results.ToArray());
    }

    public static BoardLightSetting Decode(byte[] data, byte order)
    {
        if (data.Length != 64 || data[3] > 7) throw new IOException("不正なRGBゾーン設定です");
        var normal = (order & (1 << data[3])) != 0;
        return new(data[4], data[normal ? 5 : 6], data[normal ? 6 : 5], data[7], data[8]);
    }
    public static byte[] Packet(byte zone, BoardLightSetting value, byte order)
    {
        if (zone > 7 || !BoardLighting.IsValid(value)) throw new ArgumentException("未対応のRGB設定です");
        var data = new byte[65]; data[1] = 0x10; data[3] = zone; data[4] = value.Mode;
        var normal = (order & (1 << zone)) != 0;
        data[5] = (byte)(normal ? value.Red : value.Green); data[6] = (byte)(normal ? value.Green : value.Red);
        data[7] = (byte)value.Blue; data[8] = (byte)value.Speed; data[9] = 255;
        return data;
    }
}

internal interface IAsrockRgbSession : IDisposable
{
    byte[] Exchange(byte[] request);
}

// A service transport may marshal onto the one thread that owns its retained HID handle.
// Run the guard on that thread after queueing, immediately before the actual write.
internal interface IAsrockGuardedRgbSession : IAsrockRgbSession
{
    byte[] Exchange(byte[] request, Action beforeExchange);
}

internal sealed class AsrockSessionOpenException(Exception failure, Exception closing, IAsrockRgbSession pending)
    : AggregateException("ASRock接続の生成と後始末に失敗しました", failure, closing)
{
    public IAsrockRgbSession PendingSession { get; } = pending;
}

internal sealed class AsrockRgbSession : IAsrockIdentitySession
{
    private readonly Mutex mutex;
    private HidStream? stream;
    private IDisposable? transportLifetime;
    private bool owns, disposed;
    public string Identity { get; private set; } = "";
    public string? VerifiedSerial { get; }
    public int? DeviceRevision { get; }
    // Supplies an already owned, thread-bound transport without opening HID hardware.
    internal AsrockRgbSession(IDisposable transport, Mutex ownership)
    { transportLifetime = transport; mutex = ownership; owns = true; }
    public AsrockRgbSession(string? expectedIdentity = null, bool captureIdentity = false)
    {
        mutex = new(false, @"Global\FlowDeck-ASRock-26CE-01A2");
        try
        {
            try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new IOException("ASRock RGBに別の操作が進行中です");
            AsrockRgbMonitor.CheckConflicts();
            var devices = DeviceList.Local.GetHidDevices(AsrockRgbMonitor.VendorId, AsrockRgbMonitor.ProductId).ToArray();
            var matches = expectedIdentity is null ? devices : devices.Where(d => d.DevicePath == expectedIdentity).ToArray();
            if (matches.Length != 1) throw new IOException($"対象のASRock USB RGB機器が{matches.Length}台見つかりました");
            var device = matches[0]; Identity = device.DevicePath;
            if (device.GetMaxInputReportLength() != 65 || device.GetMaxOutputReportLength() != 65) throw new IOException("未対応のASRock HID形式です");
            stream = device.Open(); transportLifetime = stream; stream.ReadTimeout = 700; stream.WriteTimeout = 700;
            if (captureIdentity) (VerifiedSerial, DeviceRevision) = AsrockHidIdentity.Read(Identity);
        }
        catch (Exception original)
        {
            try { Dispose(); }
            catch (Exception closing) { throw new AsrockSessionOpenException(original, closing, this); }
            throw;
        }
    }
    public byte[] Exchange(byte[] request)
    {
        stream!.Write(request); var response = new byte[65]; var length = stream.Read(response);
        if (length != 65 || response[0] != 0) throw new IOException("ASRock HID応答が不完全です");
        return response;
    }
    public void Dispose()
    {
        if (disposed) return;
        // Failed transport closure retains both the transport and its ownership on the same thread.
        transportLifetime?.Dispose(); transportLifetime = null; stream = null;
        if (owns) { mutex.ReleaseMutex(); owns = false; }
        mutex.Dispose(); disposed = true;
    }
}
