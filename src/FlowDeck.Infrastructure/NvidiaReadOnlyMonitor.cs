using System.Runtime.InteropServices;
using System.Text;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

internal interface INvidiaMonitorLibrary : IDisposable { nint Find(uint id); }
internal sealed class NvidiaMonitorLibrary : INvidiaMonitorLibrary
{
    private nint module;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Query(uint id);
    private Query? query;
    private bool disposed;
    public nint Find(uint id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("64ビットのアプリが必要です");
        if (module == 0) module = LoadLibraryEx("nvapi64.dll", 0, 0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32, including dependencies.
        if (module == 0) throw new DllNotFoundException("NVIDIAドライバーを利用できません");
        if (query is null)
        {
            var address = GetProcAddress(module, "nvapi_QueryInterface");
            if (address == 0) throw new EntryPointNotFoundException("NVIDIA監視APIを利用できません");
            query = Marshal.GetDelegateForFunctionPointer<Query>(address);
        }
        return query(id);
    }
    public void Dispose()
    {
        if (module == 0) { disposed = true; return; }
        if (!FreeLibrary(module)) throw new IOException("GPU監視ライブラリの終了を確認できません");
        module = 0; disposed = true;
    }
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string filename, nint file, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);
}

/// <summary>Only initialization, enumeration and documented read APIs are bound. No fan, RGB, I2C or driver-settings setters.</summary>
public sealed class NvidiaReadOnlyMonitor : IHardwareSensorBackend
{
    internal const uint InitializeId = 0x0150e828, UnloadId = 0xd22bdd7e, EnumerateId = 0xe5ac921f,
        NameId = 0xceee8e9f, ThermalId = 0xe3640a56, TachId = 0x5f608315;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Lifecycle();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Enumerate([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] nint[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadName(nint gpu, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadThermal(nint gpu, uint sensor, ref ThermalSettings settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadTach(nint gpu, out uint rpm);
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct ThermalSensor
    { public int Controller, Minimum, Maximum, Temperature, Target; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct ThermalSettings
    {
        public uint Version, Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public ThermalSensor[] Sensors;
        internal static ThermalSettings Create() => new() { Version = (uint)Marshal.SizeOf<ThermalSettings>() | 2u << 16, Sensors = new ThermalSensor[3] };
    }
    private readonly object gate = new();
    private readonly Func<INvidiaMonitorLibrary> factory;
    private readonly TimeProvider clock;
    private readonly Dictionary<nint, (string Name, string Connection)> connections = [];
    private INvidiaMonitorLibrary? library;
    private Lifecycle? unload;
    private Enumerate? enumerate;
    private ReadName? name;
    private ReadThermal? thermal;
    private ReadTach? tach;
    private bool initialized, closing, closed;
    public NvidiaReadOnlyMonitor() : this(() => new NvidiaMonitorLibrary(), TimeProvider.System) { }
    internal NvidiaReadOnlyMonitor(Func<INvidiaMonitorLibrary> factory, TimeProvider? clock = null)
    { this.factory = factory; this.clock = clock ?? TimeProvider.System; }
    private T? Bind<T>(uint id) where T : Delegate => library!.Find(id) is var pointer && pointer != 0 ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : null;
    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(closing, this);
        if (initialized) return;
        library ??= factory();
        var initialize = Bind<Lifecycle>(InitializeId) ?? throw new IOException("GPU監視APIを利用できません");
        unload = Bind<Lifecycle>(UnloadId) ?? throw new IOException("GPU監視APIを終了できません");
        enumerate = Bind<Enumerate>(EnumerateId) ?? throw new IOException("GPUの一覧を取得できません");
        name = Bind<ReadName>(NameId); thermal = Bind<ReadThermal>(ThermalId); tach = Bind<ReadTach>(TachId);
        if (initialize() != 0) throw new IOException("NVIDIAドライバーの監視を開始できません");
        initialized = true;
    }
    public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => Task.Run(() =>
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested(); EnsureOpen();
            var handles = new nint[64]; var status = enumerate!(handles, out var count);
            if (status == -6) { connections.Clear(); return Array.Empty<HardwareSensorDevice>(); } // NVIDIA_DEVICE_NOT_FOUND
            if (status != 0 || count > handles.Length) throw new IOException("GPU一覧を取得できません");
            var active = handles.Take((int)count).ToArray();
            if (active.Any(h => h == 0) || active.Distinct().Count() != active.Length) throw new IOException("GPU一覧が不正です");
            foreach (var removed in connections.Keys.Except(active).ToArray()) connections.Remove(removed);
            var result = new List<HardwareSensorDevice>();
            foreach (var handle in active)
            {
                ct.ThrowIfCancellationRequested();
                var buffer = new byte[64];
                var gpuName = name?.Invoke(handle, buffer) == 0 ? Encoding.ASCII.GetString(buffer).Split('\0')[0].Trim() : "NVIDIA GPU";
                gpuName = string.IsNullOrWhiteSpace(gpuName) ? "NVIDIA GPU" : MotherboardDisplay.ShortText(gpuName, 160);
                if (!connections.TryGetValue(handle, out var identity) || identity.Name != gpuName)
                    connections[handle] = identity = (gpuName, Guid.NewGuid().ToString("N"));
                var nativeId = "/nvapi/" + identity.Connection;
                var settings = ThermalSettings.Create(); var temperatureTime = clock.GetTimestamp();
                double? temperature = null;
                if (thermal?.Invoke(handle, 15, ref settings) == 0 && settings.Count <= 3 && settings.Sensors is { Length: 3 })
                {
                    var gpuSensors = settings.Sensors.Take((int)settings.Count).Where(s => s.Target == 1).ToArray();
                    if (gpuSensors.Length == 1) temperature = gpuSensors[0].Temperature;
                }
                var rpmTime = clock.GetTimestamp(); double? rpm = null;
                if (tach is not null && tach(handle, out var readRpm) == 0) rpm = readRpm;
                HardwareSensorValue Value(string id, string label, DeviceFeature feature, double? value, long timestamp)
                {
                    var issue = value is null ? DeviceMeasurementIssue.Unavailable :
                        DeviceMeasurement.InRange(feature, value.Value) && (feature != DeviceFeature.Rpm || value <= 30000) ? DeviceMeasurementIssue.None : DeviceMeasurementIssue.OutOfRange;
                    return new(nativeId + "/" + id, label, nativeId, feature, issue == DeviceMeasurementIssue.None ? value : null, timestamp,
                        issue == DeviceMeasurementIssue.None ? null : issue == DeviceMeasurementIssue.OutOfRange ? "範囲外" : "未取得", issue);
                }
                result.Add(new(nativeId, gpuName, DeviceKind.Graphics, SettingsTransfer.Hash(new { gpuName, identity.Connection }), null,
                    identity.Connection, null, [Value("temperature", "GPU", DeviceFeature.Temperature, temperature, temperatureTime),
                    Value("fan", "GPUファン", DeviceFeature.Rpm, rpm, rpmTime)]));
            }
            ct.ThrowIfCancellationRequested(); return result.ToArray();
        }
    }, ct);
    public async ValueTask DisposeAsync()
    {
        // Native reads cannot be forcibly cancelled. Their owner remains alive until the read and unload complete.
        await Task.Run(() =>
        {
            lock (gate)
            {
                if (closed) return; closing = true;
                if (initialized)
                {
                    if (unload!() != 0) throw new IOException("GPU監視の終了を確認できません。再試行してください");
                    initialized = false;
                }
                library?.Dispose(); library = null; connections.Clear(); closed = true;
            }
        }).ConfigureAwait(false);
    }
}
