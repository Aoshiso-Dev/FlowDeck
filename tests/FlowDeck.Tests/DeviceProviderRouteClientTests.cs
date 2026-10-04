using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceProviderRouteClientTests
{
    [Theory]
    [InlineData("--openrgb-inspection")] [InlineData("--login-startup")] [InlineData("--cooling-settings")]
    public void RouteSmokeCannotUseARealConnectionOrMixScenarios(string other)
    {
        string[] args = ["--devices-preview", "--demo", "--smoke", "--provider-routes", "--data-dir", Path.GetTempPath()];
        Assert.True(DeviceWorkspaceOptions.Parse(args).ProviderRoutes);
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. args, other]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse(args.Where(a => a != "--smoke").ToArray()));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. args, "--devices-pipe", "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), "--server-pid", "1"]));
    }

    [Fact]
    public async Task ClientReviewsAndAdoptsBothRoutesUsingTheAuthenticatedConfigurationTransfer()
    {
        using var directory = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync(); Assert.True(client.SupportsProviderRouting);
        var before = client.Snapshot!; var edit = DeviceProviderRouteEdit.Capture(before, true, 6742);
        var preview = await client.PreviewProviderRouteAsync(edit); Assert.NotNull(preview.Transfer.ProviderRoute);
        Assert.Equal(["demo-direct-light"], preview.Transfer.ProviderRoute.RemovingDeviceIds); // Observation labels say "demo"; handle ownership is different.
        Assert.Equal(before.Configuration.Generation, client.Snapshot!.Configuration.Generation);
        Assert.False(demo.Routing!.Created[0].Disposed); Assert.Equal(0, demo.OpenRgb.Calls);
        var result = await client.ApplyProviderRouteAsync(preview); Assert.Equal(Outcome.Confirmed, result.Outcome);
        Assert.True(demo.Routing.Created[0].Disposed); Assert.True(client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbEnabled);
        await Assert.ThrowsAsync<IOException>(() => client.ApplyProviderRouteAsync(preview));
        var reverse = await client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, false, 6742));
        Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(reverse)).Outcome);
        Assert.True(demo.Routing.Created[1].Disposed); Assert.False(client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbEnabled);
    }

    [Fact]
    public async Task AnInspectionCapableServiceDoesNotImplyRoutingPermission()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        Assert.True(client.SupportsOpenRgbInspection); Assert.False(client.SupportsProviderRouting);
        await client.InspectOpenRgbAsync(6742); Assert.False(client.SupportsProviderRouting);
        await Assert.ThrowsAsync<IOException>(() => client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742)));
        Assert.False(client.Snapshot!.Configuration.Configuration.Preferences.Providers!.OpenRgbEnabled);
    }

    [Fact]
    public async Task ACancelledAdoptionDoesNotDisconnectTheSharedClientOrSwitchProviders()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path, providerRouting: true);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var preview = await client.PreviewProviderRouteAsync(DeviceProviderRouteEdit.Capture(client.Snapshot!, true, 6742));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ApplyProviderRouteAsync(preview, cancel.Token));
        Assert.True(client.Snapshot!.Connected); Assert.False(demo.Routing!.Created[0].Disposed);
        Assert.Equal(Outcome.Confirmed, (await client.ApplyProviderRouteAsync(preview)).Outcome);
    }
}
