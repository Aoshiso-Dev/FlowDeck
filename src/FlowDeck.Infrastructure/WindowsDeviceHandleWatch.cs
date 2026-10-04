using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Infrastructure;

internal interface IHardwareContinuityWatch : IDisposable
{
    bool ResourcesClosed { get; }
    void Check();
}

// A handle-specific PnP registration remains associated with that device after its metadata
// handle is closed. Every event permanently revokes this lifetime; query-remove-failed does not
// reinstate old requests. Callback does no I/O and never unregisters itself.
internal sealed class WindowsDeviceHandleWatch : IHardwareContinuityWatch, IAsyncDisposable
{
    // cfgmgr32.h: 16-byte header + union containing WCHAR InstanceId[MAX_DEVICE_ID_LEN=200].
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct Filter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint Type;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public nint DeviceHandle;
        [FieldOffset(16)] public Guid InterfaceClass;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint Callback(nint notification, nint context, uint action, nint eventData, uint eventDataSize);
    internal interface IApi
    {
        uint Register(ref Filter filter, nint context, Callback callback, out nint notification);
        uint Unregister(nint notification);
    }
    private sealed class NativeApi : IApi
    {
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Register_Notification(ref Filter filter, nint context, Callback callback, out nint notification);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Unregister_Notification(nint notification);
        public uint Register(ref Filter filter, nint context, Callback callback, out nint notification) =>
            CM_Register_Notification(ref filter, context, callback, out notification);
        public uint Unregister(nint notification) => CM_Unregister_Notification(notification);
    }
    private sealed class State { public int Revoked; }
    private static readonly Callback NativeCallback = OnNotification;
    private readonly object gate = new();
    private readonly IApi api;
    private readonly State state = new();
    private GCHandle context;
    private nint registration;
    private volatile bool ready, closing, closed;
    public bool ResourcesClosed => closed;
    private WindowsDeviceHandleWatch(IApi api) { this.api = api; context = GCHandle.Alloc(state); }
    private static uint OnNotification(nint _, nint context, uint action, nint data, uint length)
    {
        // Do not dereference event data or run user code on this callback thread. A static delegate
        // and GCHandle keep the state valid until successful unregistration has drained callbacks.
        if (context != 0 && GCHandle.FromIntPtr(context).Target is State state) Interlocked.Exchange(ref state.Revoked, 1);
        return 0; // ERROR_SUCCESS, including query-remove. We never veto device removal.
    }
    public static WindowsDeviceHandleWatch Create(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var reference = false;
        try
        {
            handle.DangerousAddRef(ref reference);
            if (handle.IsInvalid || handle.IsClosed) throw new IOException("機器通知の対象ハンドルが無効です");
            return Create(new() { Size = 416, Type = 1, DeviceHandle = handle.DangerousGetHandle() }, new NativeApi());
        }
        finally { if (reference) handle.DangerousRelease(); }
    }
    // Used for an OS interop test against a random, nonexistent interface class; no device is opened.
    internal static WindowsDeviceHandleWatch CreateInterfaceWatch(Guid classId)
    {
        if (classId == Guid.Empty) throw new ArgumentException("通知クラスが空です");
        return Create(new() { Size = 416, Type = 0, InterfaceClass = classId }, new NativeApi());
    }
    internal static WindowsDeviceHandleWatch Create(Filter filter, IApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (filter.Size != 416 || filter.Flags != 0 || filter.Reserved != 0 || filter.Type > 1 ||
            filter.Type == 1 && (filter.DeviceHandle == 0 || filter.DeviceHandle == -1) || filter.Type == 0 && filter.InterfaceClass == Guid.Empty)
            throw new ArgumentException("機器通知のフィルターが不正です");
        var watch = new WindowsDeviceHandleWatch(api);
        try
        {
            var result = api.Register(ref filter, GCHandle.ToIntPtr(watch.context), NativeCallback, out watch.registration);
            if (result != 0 || watch.registration == 0) throw new IOException($"機器通知を登録できません（CM={result}）");
            watch.ready = true; watch.Check(); return watch;
        }
        catch (Exception start)
        {
            try { watch.Dispose(); }
            catch (Exception close) { throw new DeviceStartupException(new AggregateException(start, close), watch, () => watch.ResourcesClosed); }
            throw;
        }
    }
    public void Check()
    {
        if (!ready || closing || closed || Volatile.Read(ref state.Revoked) != 0)
            throw new IOException("機器の接続継続を確認できません。以前の引き渡しは使用できません");
    }
    public void Dispose()
    {
        closing = true;
        lock (gate)
        {
            if (closed) return;
            if (registration != 0)
            {
                var result = api.Unregister(registration);
                if (result != 0) throw new IOException($"機器通知の終了を確認できません（CM={result}）");
                registration = 0;
            }
            // CM_Unregister_Notification waits for active callbacks. Never free before it succeeds.
            if (context.IsAllocated) context.Free();
            closed = true;
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
