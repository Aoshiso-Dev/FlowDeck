using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Xunit;

namespace FlowDeck.Tests;

public sealed class DeviceLightingPersonalizationTests
{
    private static EndpointReference Target(DemoDeviceWorkspace demo) => new(demo.DeviceIds[0], "led1");
    private static DeviceLightingDraft Draft(DeviceWorkspaceSnapshot s, EndpointReference target)
    {
        var draft = new DeviceLightingDraft(); draft.Update(s.State, s.Configuration.Generation, s.Configuration.Configuration.Lighting, 0, true);
        draft.Select([target]); draft.SetProgram(new("fixed", [new(12, 34, 56)], Brightness: 63)); return draft;
    }
    [Theory]
    [InlineData("name")][InlineData("control")][InlineData("reference")][InlineData("alias-extra")][InlineData("color-extra")]
    [InlineData("empty")][InlineData("many")][InlineData("invalid-color")][InlineData("missing")][InlineData("wrong-id")][InlineData("scene-extra")]
    public void InvalidPersonalizationCannotEnterTheLibrary(string kind)
    {
        var r = new LightingLibraryRequest(LightingLibraryOperation.SaveAlias, new('a', 32), "alias", 0, Alias: new(new("a", "led1"), "照明"));
        r = kind switch {
            "name" => r with { Alias = r.Alias! with { Name = new('x', 81) } },
            "control" => r with { Alias = r.Alias! with { Name = "bad\nname" } },
            "reference" => r with { Alias = r.Alias! with { Target = new("", "led1") } },
            "alias-extra" => r with { Colors = [new(1, 2, 3)] },
            "wrong-id" => r with { Id = "palette" },
            "scene-extra" => r with { Scene = new("s", "scene", [], []) },
            _ => new(LightingLibraryOperation.ToggleFavorite, new('a', 32), "palette", 0,
                Alias: kind == "color-extra" ? r.Alias : null,
                Colors: kind == "empty" ? [] : kind == "many" ? [new(1,2,3),new(4,5,6)] : kind == "invalid-color" ? [new(256,0,0)] : kind == "missing" ? null : [new(1,2,3)]) };
        Assert.False(DeviceLightingLibrary.Valid(r));
    }
    [Fact] public async Task NamesAreDurablePerEndpointAndRestoreToDefaultWithoutOutput()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsLightingPersonalization);
        var target = Target(demo); var draft = new DeviceLightAliasDraft(client.Snapshot!, target) { Name = "  前面ファン  " };
        var old = draft.Capture(); await client.SaveLightingEditAsync(old);
        Assert.Equal(new EndpointAlias(target, "前面ファン"), Assert.Single(client.Snapshot!.Configuration.Configuration.Lighting.Names));
        await Assert.ThrowsAnyAsync<Exception>(() => client.SaveLightingEditAsync(old));
        var absent = DeviceLightingPersonalization.Capture(client.Snapshot!, LightingLibraryOperation.SaveAlias, new(new(target.DeviceId, "missing"), "別の照明"));
        await Assert.ThrowsAsync<IOException>(() => client.SaveLightingEditAsync(absent));
        var restored = DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration")).ReadActive()!.Files);
        Assert.Equal("前面ファン", Assert.Single(restored.Lighting.Names).Name);
        draft = new(client.Snapshot!, target) { Name = "" }; await client.SaveLightingEditAsync(draft.Capture());
        Assert.Empty(client.Snapshot!.Configuration.Configuration.Lighting.Names);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.LightingWrites); Assert.Equal(0, h.Writes); });
    }
    [Fact] public async Task FavoriteToggleIsBoundedAndRecentColorsRemainUnscaledOrderedAndDeduplicated()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        async Task Save(LightingLibraryOperation kind, params RgbColor[] colors) => await client.SaveLightingEditAsync(DeviceLightingPersonalization.Capture(client.Snapshot!, kind, colors: colors));
        for (var i = 0; i < 32; i++) await Save(LightingLibraryOperation.ToggleFavorite, new RgbColor(i, 34, 56));
        var generation = client.Snapshot!.Configuration.Generation;
        await Assert.ThrowsAsync<IOException>(() => Save(LightingLibraryOperation.ToggleFavorite, new RgbColor(250, 1, 2)));
        Assert.Equal(generation, client.Snapshot!.Configuration.Generation); Assert.Equal(32, client.Snapshot.Configuration.Configuration.Preferences.Palette.Favorites.Length);
        await Save(LightingLibraryOperation.ToggleFavorite, new RgbColor(0, 34, 56)); await Save(LightingLibraryOperation.ToggleFavorite, new RgbColor(250, 1, 2));
        var colors = Enumerable.Range(0, 10).Select(i => new RgbColor(i, 100, 200)).ToArray(); await Save(LightingLibraryOperation.RememberColors, colors);
        await Save(LightingLibraryOperation.RememberColors, colors[5], colors[1], colors[5]);
        var palette = client.Snapshot!.Configuration.Configuration.Preferences.Palette;
        Assert.Equal(8, palette.Recent.Length); Assert.Equal(new[] { "#0564C8", "#0164C8", "#0064C8" }, palette.Recent.Take(3));
        Assert.Contains("#FA0102", palette.Favorites); Assert.DoesNotContain("#002238", palette.Favorites);
        generation = client.Snapshot.Configuration.Generation;
        await Save(LightingLibraryOperation.RememberColors, colors[5], colors[1]);
        Assert.Equal(generation, client.Snapshot!.Configuration.Generation);
        Assert.All(demo.Hubs, h => { Assert.Equal(0, h.LightingWrites); Assert.Equal(0, h.Writes); });
    }
    [Fact] public async Task OwnMetadataSavePreservesUnsentProgramSceneAndJapaneseAlias()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var before = client.Snapshot!; var target = Target(demo);
        var draft = Draft(before, target); var scene = new DeviceLightingLibraryDraft(before, LightingPresetKind.Scene) { Name = "未保存シーン" }; scene.AddPart();
        var alias = new DeviceLightAliasDraft(before, target) { Name = "変換途中" };
        await client.SaveLightingEditAsync(DeviceLightingPersonalization.Capture(before, LightingLibraryOperation.ToggleFavorite, colors: [new(12, 34, 56)]));
        var after = client.Snapshot!; Assert.True(DeviceLightingPersonalization.MetadataOnly(before, after));
        DeviceLightingPersonalization.Preserve(draft, before, after); scene.AcceptMetadataSave(before, after); alias.AcceptMetadataSave(before, after);
        draft.Update(after.State, after.Configuration.Generation, after.Configuration.Configuration.Lighting, 0, true); scene.Update(after); alias.Update(after);
        Assert.True(draft.CanApply); Assert.Equal(new RgbColor(12, 34, 56), draft.Program.Colors[0]); Assert.Equal(63, draft.Program.Brightness);
        Assert.Null(scene.Problem); Assert.Equal("未保存シーン", scene.Name); Assert.Null(alias.Problem); Assert.Equal("変換途中", alias.Name);
        Assert.Equal(after.Configuration.Generation, scene.Capture().Request.ExpectedGeneration);
        Assert.Equal(after.Configuration.Generation, alias.Capture().Request.ExpectedGeneration);
    }
    [Theory][InlineData("policy")][InlineData("session")][InlineData("epoch")][InlineData("topology")][InlineData("lighting")][InlineData("configuration")]
    public async Task MetadataPreservationCannotHideConcurrentOperatingOrConnectionChanges(string kind)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); var before = client.Snapshot!; var target = Target(demo); var draft = Draft(before, target);
        var config = before.Configuration.Configuration;
        var after = before with { Configuration = before.Configuration with { Generation = new('f', 32) } };
        after = kind switch {
            "session" => after with { Session = new('d',32) },
            "epoch" => after with { State = after.State with { Inventory = after.State.Inventory with { Epoch = new('d',32) } } },
            "topology" => after with { State = after.State with { Inventory = after.State.Inventory with { TopologyRevision = after.State.Inventory.TopologyRevision + 1 } } },
            "policy" => after with { State = after.State with { Endpoints = after.State.Endpoints.Select(e => e.Target == target ? e with { PolicyRevision = e.PolicyRevision + 1 } : e).ToArray() } },
            "lighting" => after with { Configuration = after.Configuration with { Configuration = config with { Lighting = config.Lighting with { DefaultProgram = new("off", []) } } } },
            _ => after with { Configuration = after.Configuration with { Configuration = config with { Preferences = config.Preferences with { Ui = config.Preferences.Ui with { Theme = "dark" } } } } } };
        DeviceLightingPersonalization.Preserve(draft, before, after);
        draft.Update(after.State, after.Configuration.Generation, after.Configuration.Configuration.Lighting, 0, true);
        Assert.True(draft.Conflict); Assert.Equal(new RgbColor(12, 34, 56), draft.Program.Colors[0]); Assert.False(draft.CanApply);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task NamesAndFavoritesSurviveV2Archives(bool portable)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        await client.SaveLightingEditAsync(DeviceLightingPersonalization.Capture(client.Snapshot!, LightingLibraryOperation.SaveAlias, new(Target(demo), "同じ名前")));
        await client.SaveLightingEditAsync(DeviceLightingPersonalization.Capture(client.Snapshot!, LightingLibraryOperation.ToggleFavorite, colors: [new(12,34,56)]));
        var config = client.Snapshot!.Configuration.Configuration;
        var restored = DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(DeviceSettingsArchives.Create(config, portable))).Configuration;
        Assert.Equal(config.Lighting.Names, restored.Lighting.Names); Assert.Equal(config.Preferences.Palette.Favorites, restored.Preferences.Palette.Favorites);
        Assert.All(demo.Hubs, h => Assert.Equal(0, h.LightingWrites));
    }
}
