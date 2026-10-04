using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public class MotherboardDefinitionTests
{
    private static string Json() => JsonSerializer.Serialize(new { version = 1, boards = MotherboardDefinitionCatalog.BuiltIn.Boards }, Contract.Json);
    [Fact]
    public void PackagedDefinitionRequiresExactModelAndRetainsAllExistingChannels()
    {
        var catalog = MotherboardDefinitionCatalog.Parse(Json());
        var board = Assert.Single(catalog.Boards); Assert.Equal(15, board.Ports.Count);
        Assert.Null(catalog.Resolve("ASRock X870E Taichi")); Assert.Null(catalog.Resolve("ASUS X870E"));
        Assert.Null(catalog.Resolve("DEMO / " + board.Name)); Assert.NotNull(catalog.Resolve("DEMO / " + board.Name, true));
        Assert.Equal(MotherboardFans.Cpu.SensorId, catalog.CpuSensorId(new(board.Name, [])));
        Assert.Equal(MotherboardFans.Cpu.DemoSensorId, catalog.CpuSensorId(new("DEMO / " + board.Name, [])));
        Assert.Null(catalog.PortForSensor(new("Unknown", []), MotherboardFans.Cpu.SensorId));
    }
    [Theory]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"version\":1", "\"version\":1,\"version\":1")]
    [InlineData("\"channel\":1", "\"channel\":99")]
    [InlineData("NCT6799D", "NCT9999")]
    [InlineData("\"cpuFanId\":\"cpu-fan1\"", "\"cpuFanId\":\"missing\"")]
    public void InvalidDefinitionsCannotChooseHardwareWrites(string original, string replacement)
        => Assert.ThrowsAny<Exception>(() => MotherboardDefinitionCatalog.Parse(Json().Replace(original, replacement, StringComparison.Ordinal)));
    [Fact]
    public void DefinitionsCannotSupplyRegistersOrCommands()
        => Assert.Throws<JsonException>(() => MotherboardDefinitionCatalog.Parse(Json().Replace("\"version\":1", "\"version\":1,\"register\":160")));
}
