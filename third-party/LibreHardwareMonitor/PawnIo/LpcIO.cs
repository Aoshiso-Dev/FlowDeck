namespace LibreHardwareMonitor.PawnIo;

internal class LpcIo
{
    private readonly long[] _doubleArgArray = new long[2];
    private readonly PawnIo _pawnIO;
    private readonly long[] _singleArgArray = new long[1];

    public LpcIo() : this(PawnIo.LoadModuleFromResource(typeof(LpcIo).Assembly, $"{nameof(LibreHardwareMonitor)}.Resources.PawnIo.LpcIO.bin")) { }
    internal LpcIo(PawnIo module) => _pawnIO = module ?? throw new System.ArgumentNullException(nameof(module));

    public void SelectSlot(int slot)
    {
        _singleArgArray[0] = slot;
        _pawnIO.ExecuteChecked("ioctl_select_slot", _singleArgArray, 0);
    }

    public void FindBars()
    {
        _pawnIO.ExecuteChecked("ioctl_find_bars", [], 0);
    }

    public byte ReadPort(ushort port)
    {
        _singleArgArray[0] = port;
        return checked((byte)_pawnIO.ExecuteChecked("ioctl_pio_inb", _singleArgArray, 1)[0]);
    }

    public void WritePort(ushort port, byte value)
    {
        _doubleArgArray[0] = port;
        _doubleArgArray[1] = value;
        _pawnIO.ExecuteChecked("ioctl_pio_outb", _doubleArgArray, 0);
    }

    public byte ReadByte(byte register)
    {
        _singleArgArray[0] = register;
        return checked((byte)_pawnIO.ExecuteChecked("ioctl_superio_inb", _singleArgArray, 1)[0]);
    }

    public ushort ReadWord(byte register)
    {
        _singleArgArray[0] = register;
        return checked((ushort)_pawnIO.ExecuteChecked("ioctl_superio_inw", _singleArgArray, 1)[0]);
    }

    public void WriteByte(byte register, byte value)
    {
        _doubleArgArray[0] = register;
        _doubleArgArray[1] = value;
        _pawnIO.ExecuteChecked("ioctl_superio_outb", _doubleArgArray, 0);
    }

    public void Close() => _pawnIO.Close();
}
