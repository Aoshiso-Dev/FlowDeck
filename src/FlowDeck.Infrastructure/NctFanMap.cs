namespace FlowDeck.Infrastructure;

public sealed record NctFanMap(string Chip, int Channel, ushort Mode, ushort Command, ushort Output, ushort? Request = null)
{
    public bool SharedMode => Request is not null;
    public byte Mask => (byte)(1 << Channel);
    public static NctFanMap For(string chip, int channel)
    {
        if (chip == "NCT6799D" && channel is >= 0 and < 7)
            return new(chip, channel, new ushort[] { 0x102, 0x202, 0x302, 0x802, 0x902, 0xA02, 0xB02 }[channel],
                new ushort[] { 0x109, 0x209, 0x309, 0x809, 0x909, 0xA09, 0xB09 }[channel],
                new ushort[] { 0x001, 0x003, 0x011, 0x013, 0x015, 0xA09, 0xB09 }[channel]);
        if (chip == "NCT6686D" && channel is >= 0 and < 8)
            return new(chip, channel, 0xA00, (ushort)(0xA28 + channel), (ushort)(0x160 + channel), 0xA01);
        throw new NotSupportedException("未対応の制御チップまたはチャンネルです");
    }
}
