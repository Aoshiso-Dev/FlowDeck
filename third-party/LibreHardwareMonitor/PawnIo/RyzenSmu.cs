using System;
using System.IO;
using LibreHardwareMonitor.Hardware;

namespace LibreHardwareMonitor.PawnIo;

public class RyzenSmu
{
    private readonly PawnIo _pawnIO;
    private readonly Func<string, long[], int, long[]> _execute;

    public RyzenSmu()
    {
        _pawnIO = PawnIo.LoadModuleFromResource(typeof(IntelMsr).Assembly, $"{nameof(LibreHardwareMonitor)}.Resources.PawnIo.RyzenSMU.bin");
        _execute = _pawnIO.ExecuteChecked;
    }

    internal RyzenSmu(Func<string, long[], int, long[]> execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    private long[] Execute(string name, int size)
    {
        var result = _execute(name, Array.Empty<long>(), size);
        if (result == null || result.Length != size) throw new IOException("Incomplete Ryzen SMU response.");
        return result;
    }

    public uint GetSmuVersion()
    {
        if (!Mutexes.WaitPciBus(5000))
            throw new TimeoutException("Timeout waiting for PCI bus mutex");

        uint version;

        try
        {
            long[] outArray = Execute("ioctl_get_smu_version", 1);
            version = (uint)outArray[0];
        }
        finally
        {
            Mutexes.ReleasePciBus();
        }

        return version;
    }

    public long GetCodeName()
    {
        long[] outArray = Execute("ioctl_get_code_name", 1);
        return outArray[0];
    }

    public long[] ReadPmTable(int size)
    {
        if (!Mutexes.WaitPciBus(5000))
            throw new TimeoutException("Timeout waiting for PCI bus mutex");

        try
        {
            long[] outArray = Execute("ioctl_read_pm_table", size);
            return outArray;
        }
        finally
        {
            Mutexes.ReleasePciBus();
        }
    }

    public void UpdatePmTable()
    {
        if (!Mutexes.WaitPciBus(5000))
            throw new TimeoutException("Timeout waiting for PCI bus mutex");

        try
        {
            Execute("ioctl_update_pm_table", 0);
        }
        finally
        {
            Mutexes.ReleasePciBus();
        }
    }

    public void ResolvePmTable(out uint version, out uint tableBase)
    {
        if (!Mutexes.WaitPciBus(5000))
            throw new TimeoutException("Timeout waiting for PCI bus mutex");

        try
        {
            long[] outArray = Execute("ioctl_resolve_pm_table", 2);
            version = (uint)outArray[0];
            tableBase = (uint)outArray[1];
        }
        finally
        {
            Mutexes.ReleasePciBus();
        }
    }

    public long[] UpdateAndReadPmTable(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!Mutexes.WaitPciBus(5000)) throw new TimeoutException("Timeout waiting for PCI bus mutex");
        try
        {
            Execute("ioctl_update_pm_table", 0);
            return Execute("ioctl_read_pm_table", size);
        }
        finally { Mutexes.ReleasePciBus(); }
    }

    public void Close() => _pawnIO?.Close();
}
