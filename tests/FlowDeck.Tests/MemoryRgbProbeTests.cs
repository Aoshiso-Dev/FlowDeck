using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class MemoryRgbProbeTests
{
    [Fact] public void MissingDevicesNeverSelectARegister()
    {
        var bus = new Bus(); var result = EneMemoryRgbProbe.Read(bus);
        Assert.All(result, r => Assert.False(r.SignatureMatched)); Assert.Empty(bus.Registers);
    }
    [Fact] public void IncompleteSignaturesNeverSelectARegister()
    {
        var bus = new Bus { Partial = true }; EneMemoryRgbProbe.Read(bus); Assert.Empty(bus.Registers);
    }
    [Fact] public void ReadsIdentifiedModulesWithoutColorApplySaveOrAddressRegisters()
    {
        var bus = new Bus { Present = true }; var result = EneMemoryRgbProbe.Read(bus);
        var module = Assert.Single(result, r => r.SignatureMatched);
        Assert.Equal("AUDA0-E6K5-0101", module.Firmware); Assert.Equal(128, module.ConfigurationHex!.Length);
        Assert.All(bus.Registers, r => EneMemoryRgbProbe.CheckRegisterRequest(0x70, r));
        Assert.DoesNotContain((ushort)0x80a0, bus.Registers); Assert.DoesNotContain((ushort)0x80f9, bus.Registers);
    }
    [Fact] public void SpdPmicAndSharedAddressesAreNotAccessible()
    {
        foreach (var a in Enumerable.Range(0x48, 8).Concat(Enumerable.Range(0x50, 16)).Append(0x77))
        {
            if (a == 0x4f) continue;
            Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbProbe.CheckSignatureRequest((byte)a, 0xa0));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbProbe.CheckSignatureRequest(0x70, 0));
    }
    [Fact] public void ColorApplySaveAndRemappingRegistersAreRejected()
    {
        foreach (var r in new ushort[] { 0x8000, 0x8010, 0x80a0, 0x80f8, 0x80f9, 0x8100, 0x8160 })
            Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbProbe.CheckRegisterRequest(0x70, r));
    }
    [Fact] public void MicronIsExcludedBeforeConfigReads()
    {
        var bus = new Bus { Present = true, Micron = true }; var result = EneMemoryRgbProbe.Read(bus);
        Assert.Contains("Micron", Assert.Single(result, r => r.SignatureMatched).Note);
        Assert.All(bus.Registers, r => Assert.InRange(r, (ushort)0x1030, (ushort)0x103f));
    }
    [Fact] public void CancellationStopsBeforeBusAccess()
    {
        var bus = new Bus(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => EneMemoryRgbProbe.Read(bus, cancel.Token)); Assert.Equal(0, bus.Reads);
    }
    private sealed class Bus : IMemoryRgbProbeBus
    {
        public bool Present, Partial, Micron;
        public int Reads;
        public List<ushort> Registers { get; } = [];
        public byte? ReadSignature(byte address, byte command)
        {
            Reads++;
            return address == 0x70 && (Present || Partial && command < 0xa5) ? (byte)(command - 0xa0) : null;
        }
        public byte ReadRegister(byte address, ushort register)
        {
            Registers.Add(register);
            if (register is >= 0x1000 and < 0x1010) return (byte)"AUDA0-E6K5-0101\0\0"[register - 0x1000];
            if (Micron && register is >= 0x1030 and < 0x1036) return (byte)"Micron"[register - 0x1030];
            return 0;
        }
    }
}
