using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Infrastructure;

/// <summary>Prevents the pinned Nuvoton updater's silent ISA-mutex early return. Must stay on the calling thread.</summary>
internal static class LhmSuperIoReadLease
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static IDisposable Acquire(IHardware hardware)
    {
        if (!LhmSensorFreshness.PatchedLibrary) throw new IOException("例外時のバス解放を保証する固定版センサーライブラリが必要です");
        if (typeof(Computer).Assembly.GetName().Version != new Version(0, 9, 6, 0) ||
            hardware.GetType().FullName != "LibreHardwareMonitor.Hardware.Motherboard.SuperIOHardware")
            throw new NotSupportedException("この制御チップの実測更新を確認できません");
        var chip = hardware.GetType().GetField("_superIO", Private)?.GetValue(hardware);
        var type = chip?.GetType();
        if (type?.FullName != "LibreHardwareMonitor.Hardware.Motherboard.Lpc.Nct677X" ||
            type.GetProperty("Chip")?.GetValue(chip)?.ToString() is not ("NCT6799D" or "NCT6686D") ||
            type.GetField("_isNuvotonVendor", Private)?.GetValue(chip) is not true ||
            type.GetMethod("IsNuvotonVendor", Private) is not { } vendor)
            throw new NotSupportedException("この制御チップの実測更新を確認できません");
        // Use the exact LHM mutex object, so recursive acquisition in Nct677X.Update is guaranteed on this thread.
        var mutexType = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Mutexes");
        if (mutexType?.GetField("_isaBusMutex", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not Mutex mutex)
            throw new IOException("ISAバスの排他が初期化されていません");
        return Acquire(mutex, () => vendor.Invoke(chip, null) is true);
    }
    internal static IDisposable Acquire(Mutex mutex, Func<bool> verifyVendor)
    {
        bool acquired;
        try { acquired = mutex.WaitOne(500); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("ISAバスが使用中のため実測できません");
        try
        {
            if (!verifyVendor()) throw new IOException("制御チップの識別を確認できません");
            return new Lease(mutex);
        }
        catch { mutex.ReleaseMutex(); throw; }
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    { public void Dispose() => mutex.ReleaseMutex(); }
}
