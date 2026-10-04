using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceProviderSelectionSettingsTests
{
    private static string Id(int n) => "openrgb-sdk:" + n.ToString("x64");

    [Theory]
    [InlineData("invalid")] [InlineData("duplicate")] [InlineData("large")] [InlineData("disabled")] [InlineData("port")]
    public void SelectionSettingsRejectAmbiguousOrUnsupportedForms(string reason)
    {
        var preferences = reason switch {
            "invalid" => new DeviceProviderPreferences(true, 6742, ["0"]),
            "duplicate" => new(true, 6742, [Id(1), Id(1)]),
            "large" => new(true, 6742, Enumerable.Range(0, 33).Select(Id).ToArray()),
            "disabled" => new(false, 6742, [Id(1)]), _ => new(true, 1023, [Id(1)]) };
        Assert.False(preferences.Valid()); Assert.Throws<IOException>(() => preferences.Normalize());
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(Guid.NewGuid().ToString("N"));
        Assert.Throws<IOException>(() => DeviceConfigurationCodec.SerializePackage(config with { Preferences = config.Preferences with { Providers = preferences } }));
    }

    [Fact]
    public void SelectionUsesSetEqualityCopiesInputAndClearsOnPortableExport()
    {
        var ids = new[] { Id(2), Id(1) }; var preferences = new DeviceProviderPreferences(true, 6742, ids).Normalize();
        Assert.True(preferences.EquivalentTo(new(true, 6742, [Id(1), Id(2)])));
        Assert.True(new DeviceProviderPreferences(true).EquivalentTo(new(true, 6742, [])));
        ids[0] = Id(3); Assert.Equal([Id(1), Id(2)], preferences.OpenRgbDeviceIds!);
        var config = ServiceRuntimeBootstrap.MonitoringConfiguration(Guid.NewGuid().ToString("N"));
        config = config with { Preferences = config.Preferences with { Providers = preferences } };
        Assert.True(preferences.EquivalentTo(DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(config)).Preferences.Providers));
        Assert.True(preferences.EquivalentTo(DeviceConfigurationCodec.DeserializePackage(DeviceConfigurationCodec.SerializePackage(config)).Preferences.Providers));
        var portable = DeviceConfigurationCodec.Portable(config);
        Assert.False(portable.Preferences.Providers!.OpenRgbEnabled); Assert.Null(portable.Preferences.Providers.OpenRgbDeviceIds);
    }

    private static async Task Enable(DeviceWorkspaceClient client)
    {
        var preview = await client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, []));
        Assert.Empty(preview.Transfer.ProviderRoute!.OpenRgbDeviceIds!);
        Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(preview)).Outcome);
    }
    private static string[] Candidates(DeviceWorkspaceClient client) => client.Snapshot!.State.Inventory.Devices
        .Where(d => d.Connected && d.Device.Provider == "openrgb-sdk").Select(d => d.Device.Id).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task SelectionOnlyChangeIsReviewedPersistedAndPassedToTheReplacementProvider()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsProviderDeviceSelection); await Enable(client);
        var ids = Candidates(client); Assert.Equal(2, ids.Length); var previous = demo.Routing!.Created.Last();
        Assert.All(previous.CachedDrivers, d => Assert.NotNull(d.Validate(new("light1", DeviceOperation.SetLighting, Lighting: new("fixed", [new(0, 0, 255)])))));
        var edit = DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, [ids[0]]);
        var preview = await client.PreviewProviderRouteAsync(edit);
        Assert.Equal([ids[0]], preview.Transfer.ProviderRoute!.OpenRgbDeviceIds!);
        Assert.Equal(["demo-sdk"], preview.Transfer.ProviderRoute.RemoveProviderIds); Assert.Equal(["demo-sdk"], preview.Transfer.ProviderRoute.AddProviderIds);
        Assert.Contains("OpenRGBの制御対象：切替デモ照明", DeviceProviderRoutePresentation.Describe(preview.Transfer.ProviderRoute, client.Snapshot));
        Assert.False(previous.Disposed); Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(preview)).Outcome);
        Assert.True(previous.Disposed); Assert.Equal([ids[0]], client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbDeviceIds!);
        foreach (var driver in demo.Routing.Created.Last().CachedDrivers)
            Assert.Equal(driver.DeviceId != ids[0], driver.Validate(new("light1", DeviceOperation.SetLighting, Lighting: new("fixed", [new(0, 0, 255)]))) is not null);
        var persisted = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration")).ReadActive()!;
        Assert.Equal([ids[0]], DeviceConfigurationCodec.Read(persisted.Files).Preferences.Providers!.OpenRgbDeviceIds!);
        var clear = await client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, []));
        Assert.Empty(clear.Transfer.ProviderRoute!.OpenRgbDeviceIds!); Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(clear)).Outcome);
        Assert.Null(client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbDeviceIds);
        Assert.All(demo.Routing.Created.Last().CachedDrivers, d => Assert.NotNull(d.Validate(new("light1", DeviceOperation.SetLighting, Lighting: new("fixed", [new(0, 0, 255)])))));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InitialConnectionAndUnknownCandidatesCannotPreauthorizeDeviceSelection(bool alreadyConnected)
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); if (alreadyConnected) await Enable(client);
        var previous = demo.Routing!.Created.Last(); var generation = client.Snapshot!.Configuration.Generation;
        await Assert.ThrowsAsync<IOException>(() => client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, [Id(1)])));
        Assert.False(previous.Disposed); Assert.Equal(generation, client.Snapshot!.Configuration.Generation);
    }

    [Fact]
    public async Task APortChangeRequiresAnEmptySelectionAndCannotReuseTheOtherServersCandidates()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); await Enable(client);
        var previous = demo.Routing!.Created.Last();
        await Assert.ThrowsAsync<IOException>(() => client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6743, [Candidates(client)[0]])));
        Assert.False(previous.Disposed);
        var review = await client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6743, []));
        Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(review)).Outcome);
    }

    [Fact]
    public async Task MutationAfterCaptureDoesNotChangeSelectionAndMutationAfterReviewCannotBeApplied()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); await Enable(client);
        var ids = Candidates(client); var input = new[] { ids[0] };
        var edit = DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, input); input[0] = ids[1];
        var review = await client.PreviewProviderRouteAsync(edit); Assert.Equal([ids[0]], review.Transfer.ProviderRoute!.OpenRgbDeviceIds!);
        var previous = demo.Routing!.Created.Last(); review.Edit.Settings.OpenRgbDeviceIds![0] = ids[1];
        await Assert.ThrowsAsync<IOException>(() => client.ApplyProviderRouteAsync(review)); Assert.False(previous.Disposed);
    }

    [Fact]
    public async Task EquivalentSelectionAfterRoundTripDoesNotTriggerAnotherSwitch()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); await Enable(client);
        var ids = Candidates(client);
        var review = await client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, ids));
        Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(review)).Outcome); var count = demo.Routing!.Created.Count;
        await Assert.ThrowsAsync<IOException>(() => client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742, ids.Reverse())));
        Assert.Equal(count, demo.Routing.Created.Count);
    }
}
