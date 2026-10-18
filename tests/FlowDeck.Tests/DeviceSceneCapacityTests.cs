using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceSceneCapacityTests
{
    private static readonly EndpointReference Target = new("a", "light1");
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 180)]);
    private static DeviceLightingScene Scene(int index) => new("scene-" + index, "シーン " + index, [new(Target, Blue)], []);
    private static DeviceConfigurationPackage Package(int count)
    {
        var source = ServiceRuntimeBootstrap.MonitoringConfiguration(new('a', 32));
        return source with {
            Devices = new(2, [new("a", "照明", "demo", DeviceKind.Memory, [new("light1", "照明", EndpointKind.Lighting)])]),
            Lighting = source.Lighting with { Scenes = Enumerable.Range(0, count).Select(Scene).ToArray() }
        };
    }
    [Theory]
    [InlineData(0, true)] [InlineData(33, true)] [InlineData(64, true)] [InlineData(65, false)]
    public void StoredCatalogRetainsAllScenesUpToThePcLimit(int count, bool accepted)
    {
        var package = Package(count);
        if (!accepted)
        {
            var error = Assert.Throws<IOException>(() => DeviceConfigurationCodec.Validate(package));
            Assert.IsType<ArgumentException>(error.InnerException); return;
        }
        DeviceConfigurationCodec.Validate(package);
        var copy = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(package));
        Assert.Equal(package.Lighting.Scenes.Select(s => s.Id), copy.Lighting.Scenes.Select(s => s.Id));
        foreach (var portable in new[] { false, true })
        {
            var restored = DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(DeviceSettingsArchives.Create(package, portable)));
            Assert.Equal(package.Lighting.Scenes.Select(s => s.Id), restored.Configuration.Lighting.Scenes.Select(s => s.Id));
        }
    }
    [Fact]
    public async Task ServicePersistsAll64RejectsOverflowAtomicallyAndAllowsEditingAtCapacity()
    {
        await using var rig = new DeviceLightingLibraryTests.Rig();
        for (var i = 0; i < 64; i++)
        {
            var reply = await rig.Save(rig.SaveScene(Scene(i))); Assert.True(reply.Success, reply.Error);
        }
        var generation = rig.Authority.Generation;
        var full = await rig.Save(rig.SaveScene(Scene(64)));
        Assert.False(full.Success); Assert.Contains("64", full.Error); Assert.Equal(generation, rig.Authority.Generation);
        var edit = await rig.Save(rig.SaveScene(Scene(63) with { Name = "最後のシーンを改名" }));
        Assert.True(edit.Success, edit.Error);
        var restored = DeviceConfigurationCodec.Read(rig.Store.ReadActive()!.Files);
        Assert.Equal(64, restored.Lighting.Scenes.Length);
        Assert.Equal("最後のシーンを改名", restored.Lighting.Scenes.Single(s => s.Id == "scene-63").Name);
        Assert.All(new[] { rig.A, rig.B, rig.C }, d => { Assert.Equal(0, d.NativeWrites); Assert.Equal(0, d.Opens); });
        Assert.Equal(0, rig.Fan.Writes);
    }
}
