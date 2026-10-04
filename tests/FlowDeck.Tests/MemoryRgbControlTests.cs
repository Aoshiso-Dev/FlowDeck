using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class MemoryRgbControlTests
{
    [Fact] public async Task InventoryPreservesTwoDistinctModulesAndDoesNotWrite()
    {
        var bus = new Bus(); var inventory = await new EneMemoryRgbControl(() => bus).ReadAsync();
        Assert.Equal(2, inventory.Devices.Length); Assert.All(inventory.Devices, d => Assert.True(d.IsMemory));
        Assert.Equal(2, inventory.Devices.Select(d => d.ControlIdentity).Distinct().Count()); Assert.Empty(bus.Writes);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(14)]
    public async Task NativeApplyUsesRbgAndReadsBackOnlyTheSelectedModule(byte mode)
    {
        var bus = new Bus(); var control = new EneMemoryRgbControl(() => bus);
        var before = (await control.ReadAsync()).Devices[0].Zones[0].Configuration!;
        var desired = MemoryLighting.Normalize(new(mode, 11, 37, 91, 128));
        var result = await control.ApplyAsync(MemoryLighting.Identity(0x71), [new(0, before, desired)]);
        Assert.True(result.Verified); Assert.Equal(desired, result.Zones[0].Reported);
        Assert.All(bus.Writes, w => { Assert.Equal(0x71, w.Address); EneMemoryRgbBus.ValidateWrite(w.Register, w.Value); });
        Assert.Equal(new byte[] { 11, 91, 37 }, new[] { bus.Registers[(0x71,0x8160)],bus.Registers[(0x71,0x8161)],bus.Registers[(0x71,0x8162)] });
        Assert.Equal(1, bus.Writes.Count(w => w.Register == 0x80a0));
        Assert.Equal((byte)1, bus.Registers[(0x73, 0x8021)]);
    }
    [Fact] public async Task StaleConfigurationRejectsBeforeAnyWrite()
    {
        var bus = new Bus(); var control = new EneMemoryRgbControl(() => bus);
        await Assert.ThrowsAsync<IOException>(() => control.ApplyAsync(MemoryLighting.Identity(0x71), [new(0, new(2, 0, 0, 0), new(1, 5, 6, 7))]));
        Assert.Empty(bus.Writes);
    }
    [Fact] public async Task CommunicationFailureIsUnknownWithoutBlindReplay()
    {
        var bus = new Bus { FailWrite = 5 }; var control = new EneMemoryRgbControl(() => bus);
        var before = (await control.ReadAsync()).Devices[0].Zones[0].Configuration!;
        var result = await control.ApplyAsync(MemoryLighting.Identity(0x71), [new(0, before, new(1, 10, 20, 30))]);
        Assert.False(result.Verified); Assert.Equal("unknown", result.Zones[0].Status); Assert.Equal(5, bus.Attempts);
        Assert.DoesNotContain(bus.Writes, w => w.Register == 0x80a0);
    }
    [Fact] public async Task UnknownFirmwareIsExcludedAndCannotBeWritten()
    {
        var bus = new Bus { Present = false }; var control = new EneMemoryRgbControl(() => bus);
        Assert.Empty((await control.ReadAsync()).Devices);
        await Assert.ThrowsAsync<IOException>(() => control.ApplyAsync(MemoryLighting.Identity(0x71), [new(0, new(1, 0, 0, 0), new(1, 9, 8, 7))]));
        Assert.Empty(bus.Writes);
    }
    [Fact] public async Task PerLedPalettesSurviveIdentificationRestorationAndBrightness()
    {
        var bus = new Bus(); bus.Registers[(0x71, 0x8164)] = 80;
        var control = new EneMemoryRgbControl(() => bus); var inventory = await control.ReadAsync();
        var original = inventory.Devices[0].Zones[0].Configuration!; Assert.NotNull(original.MemoryPalette);
        var identifying = new BoardLightSetting(1, 96, 96, 96);
        Assert.True((await control.ApplyAsync(MemoryLighting.Identity(0x71), [new(0, original, identifying)])).Verified);
        Assert.True((await control.ApplyAsync(MemoryLighting.Identity(0x71), [new(0, identifying, original)])).Verified);
        Assert.Equal(80, bus.Registers[(0x71, 0x8164)]);
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "palette", [], [new(MemoryLighting.Identity(0x71), 0, original)]);
        var half = LightingBrightness.Build(scene, 50).Board[0].Effect;
        Assert.Equal(40, MemoryLighting.Colors(half)[4]); Assert.True(MemoryLighting.Valid(half));
        var off = LightingBrightness.Build(scene, 100, true).Board[0].Effect;
        Assert.Equal(original.MemoryPalette, off.MemoryPalette);
    }
    [Fact] public void RawAddressesUnsupportedEffectsAndFlashWritesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MemoryLighting.Address("0x50"));
        Assert.Throws<ArgumentException>(() => MemoryLighting.Normalize(new(12, 0, 0, 0)));
        foreach (var register in new ushort[] { 0x80f8, 0x80f9, 0x8118, 0x1c02 })
            Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbBus.ValidateWrite(register, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbBus.ValidateWrite(0x80a0, 0xaa));
        Assert.Throws<ArgumentOutOfRangeException>(() => EneMemoryRgbBus.ValidateWrite(0x8020, 2));
    }
    [Fact] public void MemoryScenesGroupsAndBrightnessRoundTripWithoutChangingBoardOrHub()
    {
        var memory = new SceneBoardLight(MemoryLighting.Identity(0x71), 0, new(1, 100, 80, 60));
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "memory", [], [memory, new("board", 2, new(1, 8, 9, 10))]);
        var group = new LightingGroup(Guid.NewGuid().ToString("N"), "メモリ", [], [new(memory.Identity, 0)]);
        var selected = LightingGroups.Select(scene, group); Assert.Single(selected.Board); Assert.Empty(selected.Hub);
        Assert.Equal(new BoardLightSetting(1, 50, 40, 30), LightingBrightness.Build(selected, 50).Board[0].Effect);
        Assert.Equal(8, scene.Board[1].Effect.Red); Assert.Null(LightingScenes.Validate(Contract.Copy(scene)));
    }
    internal sealed class Bus : IEneMemoryIo
    {
        public bool Present = true;
        public int FailWrite, Attempts;
        public Dictionary<(byte, ushort), byte> Registers = [];
        public List<(byte Address, ushort Register, byte Value)> Writes = [];
        public Bus()
        {
            foreach (byte a in new byte[] { 0x71, 0x73 })
            {
                for (ushort r = 0x8020; r <= 0x8023; r++) Registers[(a, r)] = r == 0x8021 ? (byte)1 : (byte)0;
                for (ushort r = 0x8160; r <= 0x8177; r++) Registers[(a, r)] = 0;
                for (ushort r = 0x8100; r <= 0x8117; r++) Registers[(a, r)] = 0;
            }
        }
        public bool Identify(byte address) => Present && address is 0x71 or 0x73;
        public byte Read(byte address, ushort register) => Registers[(address, register)];
        public void Write(byte address, ushort register, byte value)
        { if (++Attempts == FailWrite) throw new IOException("injected"); Writes.Add((address, register, value)); Registers[(address, register)] = value; }
        public void Dispose() { }
    }
}
