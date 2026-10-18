using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceAccentColorPreferencesTests
{
    [Theory]
    [InlineData("#aBcD01", "#ABCD01")]
    [InlineData("#000000", "#000000")]
    [InlineData("#FFFFFF", "#FFFFFF")]
    public void ValidColorsUseExactlySixHexDigitsAndNormalizeToUppercase(string value, string expected)
    {
        Assert.True(DeviceAccentColor.Valid(value));
        Assert.Equal(expected, DeviceAccentColor.Normalize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A3E635")]
    [InlineData("#ABC")]
    [InlineData("#FFA3E635")]
    [InlineData(" #A3E635")]
    [InlineData("#A3E635 ")]
    [InlineData("#A3E63G")]
    [InlineData("LimeGreen")]
    public void InvalidColorsFallBackToTheExistingPurple(string? value)
    {
        Assert.False(DeviceAccentColor.Valid(value));
        Assert.Equal("#A079EC", DeviceAccentColor.Normalize(value));
    }

    [Fact]
    public void OlderPreferencesWithoutAccentKeepWindowAndWorkspaceWithoutRewriting()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var window = new UiSettings("dark", 100, 200, 1300, 800);
        var workspace = new DeviceWorkspaceUiState(2, [new("hub", "led2")]);
        ServiceEnvironment.Write(path, new { Version = 1, Window = window, Workspace = workspace });
        var original = File.ReadAllBytes(path);

        var store = new DeviceUiPreferencesStore(path);
        Assert.Equal(window, store.Load());
        Assert.Null(store.LoadWarning);
        Assert.Equal(workspace.SelectedPage, store.Workspace.SelectedPage);
        Assert.Equal(workspace.LightingTargets, store.Workspace.LightingTargets);
        Assert.Equal(DeviceAccentColor.Default, store.AccentColor);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"#invalid\"")]
    [InlineData("\"#123\"")]
    [InlineData("42")]
    [InlineData("false")]
    [InlineData("{\"color\":\"#A3E635\"}")]
    [InlineData("[\"#A3E635\"]")]
    public void InvalidStoredAccentKeepsWindowAndWorkspaceWithoutRewriting(string accentJson)
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var window = new UiSettings("light", 100, 200, 1300, 800);
        var workspace = new DeviceWorkspaceUiState(3, [new("hub", "led1")]);
        ServiceEnvironment.Write(path, new { Version = 1, Window = window, Workspace = workspace, AccentColor = JsonSerializer.Deserialize<JsonElement>(accentJson) });
        var original = File.ReadAllBytes(path);

        var store = new DeviceUiPreferencesStore(path);
        Assert.Equal(window, store.Load());
        Assert.Null(store.LoadWarning);
        Assert.Equal(workspace.SelectedPage, store.Workspace.SelectedPage);
        Assert.Equal(workspace.LightingTargets, store.Workspace.LightingTargets);
        Assert.Equal(DeviceAccentColor.Default, store.AccentColor);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ExplicitAccentRoundTripsAndOmittedSavesPreserveItWithoutTouchingServiceFiles()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var servicePath = Path.Combine(directory.Path, "configuration.json");
        File.WriteAllText(servicePath, "service-owned settings remain unchanged");
        var serviceBytes = File.ReadAllBytes(servicePath);
        var serviceTimestamp = File.GetLastWriteTimeUtc(servicePath);
        var store = new DeviceUiPreferencesStore(path);
        var window = new UiSettings("dark", 100, 200, 1300, 800);
        var workspace = new DeviceWorkspaceUiState(2, [new("hub", "led2")]);
        await store.SaveAsync(window, workspace, "#a3e635");
        Assert.Equal("#A3E635", store.AccentColor);

        var reloaded = new DeviceUiPreferencesStore(path);
        Assert.Equal(window, reloaded.Load());
        Assert.Equal("#A3E635", reloaded.AccentColor);
        await reloaded.SaveAsync(window with { Width = 1200 });
        await reloaded.SaveAsync(window with { Theme = "light" }, new(1, workspace.LightingTargets));

        var saved = ServiceEnvironment.Read<DeviceUiPreferences>(path);
        Assert.Equal(1, saved.Version);
        Assert.Equal("light", saved.Window.Theme);
        Assert.Equal(1, saved.Workspace!.SelectedPage);
        Assert.Equal(workspace.LightingTargets, saved.Workspace.LightingTargets);
        Assert.Equal("#A3E635", saved.AccentColor);
        Assert.Equal(serviceBytes, File.ReadAllBytes(servicePath));
        Assert.Equal(serviceTimestamp, File.GetLastWriteTimeUtc(servicePath));
        Assert.Equal(new[] { "configuration.json", "ui.json" }, Directory.GetFiles(directory.Path).Select(Path.GetFileName).Order().ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#FFA3E635")]
    [InlineData("#A3E63G")]
    public async Task InvalidExplicitSaveDoesNotOverwriteAnyPreference(string accent)
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var store = new DeviceUiPreferencesStore(path);
        await store.SaveAsync(new("dark"), new(2, [new("hub", "led2")]), "#A3E635");
        var original = File.ReadAllBytes(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("light"), new(0, []), accent));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("#A3E635", store.AccentColor);
        Assert.Equal(2, store.Workspace.SelectedPage);
        Assert.Equal(new EndpointReference("hub", "led2"), Assert.Single(store.Workspace.LightingTargets!));
    }

    [Fact]
    public async Task MissingFileAfterLoadResetsAccentWithoutCreatingAFile()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var store = new DeviceUiPreferencesStore(path);
        await store.SaveAsync(new("dark"), accentColor: "#A3E635");
        File.Delete(path);

        Assert.Equal("light", store.Load(new("light")).Theme);
        Assert.Equal(DeviceAccentColor.Default, store.AccentColor);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }
}
