using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FullServerProductProviderEnforcesSelectionAndCompletesNativeShutdown(bool selected)
    {
        await using var r = new NativeProviderRig(selected: selected, fullServer: true); await r.Open();
        var before = await r.NativePacketCount();
        var result = await r.Write();
        Assert.True(result.Outcome == (selected ? Outcome.WrittenUnverified : Outcome.Rejected),
            System.Text.Json.JsonSerializer.Serialize(result));
        if (!selected)
        {
            Assert.Equal(before, await r.NativePacketCount());
            Assert.DoesNotContain(r.Wire.Requests, p => p.Origin.Sdk);
        }
        await r.Provider.PollAsync(default); Assert.Equal(1, r.Launches);
        await r.Provider.DisposeAsync(); r.AssertClosed(hadWrite: selected);
    }

    [Fact]
    public async Task FullServerDeniedNativeReadKeepsRecoveryUntilOwnedChildCloses()
    {
        await using var r = new NativeSdkRig(fullServer: true); await r.Open();
        r.BeforeAuthorize = request =>
        {
            if (request.Origin.Sdk && request.Phase == AsrockAuthorizationPhase.BeforeRead)
                throw new IOException("fixture read permission revoked");
        };
        var result = await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome);
        Assert.NotNull(r.Record.Operation); Assert.NotNull(r.SdkJournal.Pending);
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed); Assert.Null(r.PersistedSdk);
        Assert.All(Assert.Single(r.Native.Scope.ReadNativeShutdownRecoveries()).Results,
            value => Assert.Equal(Outcome.Confirmed, value.Result.Outcome));
    }

    [Fact]
    public async Task FullServerReadsRegisteredSettingsAndSerializesActualNativeController()
    {
        await using var r = new NativeSdkRig(fullServer: true); await r.Open();
        var profile = r.Startup;
        var environment = await r.Connection.ReadEnvironmentAsync(); profile.CheckLiveEnvironment(environment);
        Assert.Equal(0u, r.Snapshot.Controller.Id);
        Assert.Equal("ASRock Fixture board", r.Snapshot.Controller.Name);
        Assert.Equal("bool", environment.DetectorSchema.GetProperty("hid_safe_mode").GetProperty("type").GetString());
        Assert.Null(environment.Plugins);
        Assert.Equal(1911, environment.Detectors.GetProperty("detectors").EnumerateObject().Count());
        var persisted = await r.ReadFile("OpenRGB.json");
        Assert.Equal(1911, persisted.GetProperty("Detectors").GetProperty("detectors").EnumerateObject().Count());
        Assert.False(persisted.GetProperty("Server").GetProperty("all_controllers").GetBoolean());
        Assert.Empty(persisted.GetProperty("Client").GetProperty("clients").EnumerateArray());
        var again = await r.Connection.ReadEnvironmentAsync();
        Assert.Equal(environment.Stamp.Digest, again.Stamp.Digest); profile.CheckLiveEnvironment(again);
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FullServerNativeWorkerAndUpdateNotificationsSettleLightingAndRestore(bool direct)
    {
        await using var r = new NativeSdkRig(emptyFirst: true, fullServer: true); await r.Open();
        var program = direct ? NativeSdkBlue : new LightingProgram(
            OpenRgbLightingProfile.Surface(r.Snapshot.Controller, null).Native.Single(n => n.Index == 2).Effect.Id,
            [new(0, 0, 255)], Speed: 60);
        SdkAccepted(await r.Executor.ApplyAsync(program, new(LightingExitAction.RestoreOriginal), r.Native.Scope.CheckRoute, frame: direct));
        if (direct)
            SdkAccepted(await r.Executor.ApplyAsync(new("fixed", [new(80, 160, 240)]), new(LightingExitAction.RestoreOriginal), r.Native.Scope.CheckRoute, frame: true));
        Assert.NotNull(r.SdkJournal.Pending); Assert.Null(r.Record.Operation);
        SdkAccepted(await r.Executor.RecoverAsync(r.Native.Scope.CheckRoute)); Assert.Null(r.SdkJournal.Pending);
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed);
    }

    [Fact]
    public async Task FullServerDisconnectReconnectAndNormalStopCloseNativeController()
    {
        await using var r = new NativeSdkRig(fullServer: true); await r.Open();
        var prior = r.Snapshot.Target;
        await r.Connection.DisposeAsync();
        r.Connection = await r.Native.Owner!.ConnectAsync(r.Ready.GetProperty("port").GetInt32(), authorizeWrite: r.Native.Scope.CheckRoute);
        var snapshot = Assert.Single(await r.Connection.ScanAsync());
        Assert.NotEqual(prior.Connection, snapshot.Target.Connection);
        Assert.Throws<IOException>(() => r.Connection.Reported(prior));
        r.Native.Scope.CheckInventory([snapshot.Controller]);
        await r.Connection.DisposeAsync();
        File.WriteAllText(Path.Combine(r.Native.Directory.Path, "native-finish"), "stop");
        var result = await r.ReadFile("native-result.json", checkError: true);
        Assert.Equal(0, result.GetProperty("code").GetInt32());
        var server = await r.ReadFile("native-server-stopped.json");
        Assert.False(server.GetProperty("online").GetBoolean()); Assert.Equal(0, server.GetProperty("clients").GetInt32());
        Assert.Equal(1, (await r.ReadFile("native-state.json")).GetProperty("closes").GetInt32());
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed);
    }
}
