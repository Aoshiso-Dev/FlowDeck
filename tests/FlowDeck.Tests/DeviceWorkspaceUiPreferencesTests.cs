using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceWorkspaceUiPreferencesTests
{
    [Fact]
    public async Task WindowOnlyVersionOneRemainsReadableAndExplicitSaveAddsOnlyLocalUiState()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var authoritative = Path.Combine(directory.Path, "configuration.json");
        File.WriteAllText(authoritative, "service-owned settings remain unchanged");
        var window = new UiSettings("dark", 120, 80, 1100, 760);
        ServiceEnvironment.Write(path, new { Version = 1, Window = window });
        var original = File.ReadAllBytes(path);
        var store = new DeviceUiPreferencesStore(path);
        Assert.Equal(window, store.Load());
        Assert.Null(store.LoadWarning);
        Assert.Equal(0, store.Workspace.SelectedPage);
        Assert.Empty(store.Workspace.LightingTargets!);
        Assert.Equal(original, File.ReadAllBytes(path));

        await store.SaveAsync(window, new(2, [new("hub", "led1"), new("memory", "rgb")]));
        var reloaded = new DeviceUiPreferencesStore(path);
        Assert.Equal(window, reloaded.Load());
        Assert.Equal(2, reloaded.Workspace.SelectedPage);
        Assert.Equal(new[] { new EndpointReference("hub", "led1"), new("memory", "rgb") }, reloaded.Workspace.LightingTargets);
        Assert.Equal("service-owned settings remain unchanged", File.ReadAllText(authoritative));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task WindowSaveKeepsLoadedWorkspaceAndReturnedSelectionsCannotMutateTheStore()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var store = new DeviceUiPreferencesStore(path);
        var targets = new[] { new EndpointReference("hub", "led2") };
        var save = store.SaveAsync(new("dark"), new(1, targets));
        targets[0] = new("wrong", "target");
        await save;
        store = new(path);
        store.Load();
        store.Workspace.LightingTargets![0] = new("wrong", "other");
        await store.SaveAsync(new("light", Width: 1280));
        var next = new DeviceUiPreferencesStore(path);
        Assert.Equal("light", next.Load().Theme);
        Assert.Equal(1, next.Workspace.SelectedPage);
        Assert.Equal(new EndpointReference("hub", "led2"), Assert.Single(next.Workspace.LightingTargets!));
    }

    [Fact]
    public async Task DeliberatelyClearedSelectionDoesNotReappearAfterReload()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        var store = new DeviceUiPreferencesStore(path);
        await store.SaveAsync(new(), new(2, [new("hub", "led1")]));
        await store.SaveAsync(new(), new(2, []));
        var next = new DeviceUiPreferencesStore(path);
        next.Load();
        Assert.Equal(2, next.Workspace.SelectedPage);
        Assert.Empty(next.Workspace.LightingTargets!);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void InvalidNavigationAndDuplicateOrInvalidTargetsAreNormalizedWithoutRewritingTheFile(int page)
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        ServiceEnvironment.Write(path, new DeviceUiPreferences(1, new("dark"), new(page,
            [new("hub", "led1"), new("", "invalid"), null!, new("hub", "led1"), new("hub", "led3")])));
        var original = File.ReadAllBytes(path);
        var store = new DeviceUiPreferencesStore(path);
        Assert.Equal("dark", store.Load().Theme);
        Assert.Equal(0, store.Workspace.SelectedPage);
        Assert.Equal(new[] { new EndpointReference("hub", "led1"), new("hub", "led3") }, store.Workspace.LightingTargets);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingUiFileUsesDefaultsWithoutCreatingAFile()
    {
        using var directory = new ServiceTestDirectory();
        var store = new DeviceUiPreferencesStore(Path.Combine(directory.Path, "missing.json"));
        Assert.Equal("light", store.Load(new("light")).Theme);
        Assert.Equal(0, store.Workspace.SelectedPage);
        Assert.Empty(store.Workspace.LightingTargets!);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void MalformedWorkspaceRetainsOriginalFileAndFallsBackWithoutRestoringTargets()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "ui.json");
        const string contents = "{\"version\":1,\"window\":{\"theme\":\"dark\"},\"workspace\":{\"selectedPage\":2,\"lightingTargets\":\"invalid\"}}";
        File.WriteAllText(path, contents);
        var store = new DeviceUiPreferencesStore(path);
        Assert.Equal("light", store.Load(new("light")).Theme);
        Assert.NotNull(store.LoadWarning);
        Assert.Equal(0, store.Workspace.SelectedPage);
        Assert.Empty(store.Workspace.LightingTargets!);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void TargetRestorationFiltersVanishedTargetsAndNeverSubstitutesAnotherEndpoint()
    {
        var state = new DeviceWorkspaceUiState(2, [new("removed", "led1"), new("hub", "led2"), new("hub", "led1")]);
        var restored = state.AvailableLightingTargets([new("hub", "led2"), new("replacement", "led1")]);
        Assert.Equal(new EndpointReference("hub", "led2"), Assert.Single(restored));
        Assert.Equal(3, state.LightingTargets!.Length);
        Assert.Empty(state.AvailableLightingTargets([]));
    }

    [Fact]
    public void OversizedSelectionIsDiscardedInsteadOfRestoringAnArbitrarySubset()
    {
        var state = DeviceWorkspaceUiState.Normalize(new(2,
            Enumerable.Range(0, DeviceServiceProtocol.MaximumTargets + 1).Select(i => new EndpointReference("hub", "led" + i)).ToArray()));
        Assert.Equal(2, state.SelectedPage);
        Assert.Empty(state.LightingTargets!);
    }
}
