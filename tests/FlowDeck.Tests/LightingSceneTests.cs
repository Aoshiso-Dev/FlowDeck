using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LightingSceneTests
{
    private static LightingScene Native() => new(Guid.NewGuid().ToString("N"), "夜", [new("demo", "led1", HubLighting.Preset("blue"))], [new("asrock", 5, new(2, 160, 121, 236, 100))]);
    [Fact] public void NativeAndSynchronizationSettingsRoundTripWithoutFlattening()
    {
        var scene = Native() with { Sync = new("demo", ["led2"], "asrock", [6], "breathing", new(52,137,255), 3) };
        Assert.Null(LightingScenes.Validate(scene)); var copy = Contract.Copy(scene);
        Assert.Equal("breathing", copy.Sync!.Mode); Assert.Equal(3, copy.Sync.Speed); Assert.Equal(100, copy.Board[0].Effect.Speed);
        Assert.Equal(6, copy.Sync.Zones[0]); Assert.Equal("led2", copy.Sync.Channels[0]);
    }
    [Fact] public void DuplicateOverlappingAndInvalidTargetsAreRejected()
    {
        var scene = Native();
        Assert.NotNull(LightingScenes.Validate(scene with { Hub = [scene.Hub[0], scene.Hub[0]] }));
        Assert.NotNull(LightingScenes.Validate(scene with { Sync = new("demo", ["led1"], "asrock", [6], "breathing", new(1,2,3), 2) }));
        Assert.NotNull(LightingScenes.Validate(scene with { Board = [new("asrock", 8, new(1,1,2,3))] }));
        Assert.NotNull(LightingScenes.Validate(scene with { Hub = [scene.Hub[0] with { Channel = "sync" }] }));
        Assert.NotNull(LightingScenes.Validate(scene with { Hub = [], Board = [] }));
        Assert.NotNull(LightingScenes.Validate(new LightingSceneCollection(Scenes: [scene, scene with { Id = Guid.NewGuid().ToString("N") }])));
    }
    [Fact] public async Task LibrarySaveReloadBackupAndCorruptFileFailClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-scene-test-" + Guid.NewGuid().ToString("N"));
        var store = new LightingSceneStore(directory); var scene = Native();
        await store.SaveAsync(new(Scenes: [scene]));
        var original = await File.ReadAllTextAsync(store.PathName);
        await store.SaveAsync(new(Scenes: [scene with { Name = "静かな夜" }]));
        Assert.Equal("静かな夜", (await new LightingSceneStore(directory).LoadAsync()).Scenes![0].Name);
        Assert.Equal(original, await File.ReadAllTextAsync(store.PathName + ".backup"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(Scenes: [scene with { Name = "" }])));
        Assert.Equal("静かな夜", (await store.LoadAsync()).Scenes![0].Name);
        await File.WriteAllTextAsync(store.PathName, "{broken");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(store.PathName + ".backup"));
    }
    [Fact] public async Task LegacyLastWriteMigratesOnlyItsKnownPorts()
    {
        var settings = DemoHub.CreateSettings() with { Lighting = new(LastSent: "purple", Outcome: Outcome.WrittenUnverified, Channels: ["led1"]) };
        await using var f = new Fixture(settings);
        Assert.Equal(HubLighting.Preset("purple").Colors[0], f.Core.Settings.LightingPorts["led1"].Effect!.Colors[0]);
        Assert.Single(f.Core.Settings.LightingPorts);
        await using var unknown = new Fixture(settings with { Lighting = settings.Lighting with { Outcome = Outcome.Unknown } });
        Assert.Empty(unknown.Core.Settings.LightingPorts);
    }
    [Fact] public async Task SeparatePortWritesPreserveNeighborsAndUnknownResultsCannotBeCapturedAsKnown()
    {
        var hub = new ThreePortHub(); var state = await hub.ReadAsync();
        var settings = DemoHub.CreateSettings() with { Hardware = new(DemoHub.Identity, state.Channels.Select(c => c.Id).ToArray(), state.Lighting.Select(c => c.Id).ToArray(), ControlCoordinator.WiringSignature(state), true, state.Lighting) };
        await using var core = new ControlCoordinator(hub, new DemoTemperatures(), settings, new TestLog());
        await core.OpenAsync(DemoHub.Identity); await core.TickAsync();
        Task<OperationResult> Write(string port, string color) => core.RequestAsync(new(Guid.NewGuid().ToString("N"), "test", core.Epoch,
            core.Snapshot.LightingRevision, "SetLighting", LightingEffect: HubLighting.Preset(color), LightingChannels: [port]));
        await Write("led1", "blue"); await Write("led2", "purple");
        Assert.Equal(HubLighting.Preset("blue").Colors[0], core.Settings.LightingPorts["led1"].Effect!.Colors[0]);
        Assert.Equal(HubLighting.Preset("purple").Colors[0], core.Settings.LightingPorts["led2"].Effect!.Colors[0]);
        Assert.False(core.Settings.LightingPorts.ContainsKey("led3"));
        hub.Fail = true; var result = await Write("led2", "white"); Assert.Equal(Outcome.Unknown, result.Outcome);
        Assert.Null(core.Settings.LightingPorts["led2"].Effect); Assert.Equal(Outcome.Unknown, core.Settings.LightingPorts["led2"].Outcome);
        Assert.Equal(Outcome.WrittenUnverified, core.Settings.LightingPorts["led1"].Outcome); Assert.Equal(0, hub.FanWrites);
    }
    private sealed class ThreePortHub : IHubAdapter
    {
        private readonly DemoHub inner = new() { DelayMs = 0 };
        public bool Fail; public int FanWrites;
        public bool IsDemo => true; public string Version => "test";
        public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
        public Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default) => ReadAsync(ct);
        public async Task<HubStatus> ReadAsync(CancellationToken ct = default) => (await inner.ReadAsync(ct)) with { Lighting = [new("led1", "test",24),new("led2","test",24),new("led3","test",24)] };
        public Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default) { FanWrites++; return inner.SetFanGroupAsync(channels, duty, ct); }
        public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => SetLightingEffectAsync(channels, HubLighting.Preset(preset), ct);
        public Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default) => Fail ? throw new IOException("uncertain write") : inner.SetLightingEffectAsync(channels, effect, ct);
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
