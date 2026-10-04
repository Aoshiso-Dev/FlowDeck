using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbDeviceDriverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionKeepsOtherDevicesVisibleButRejectsTheirEffectsBatchesAndStreams(bool shared)
    {
        var first = shared ? SharedDevice() : Device();
        var second = first with { Id = 700, Serial = "UNSELECTED", Location = "USB-700" };
        await using var r = await new Rig(SelectDevices(first), first, second).Open(exit: LightingExitAction.Off);
        Assert.Equal(2, r.Runtime.State().Inventory.Devices.Length);
        var blocked = r.Driver(700); var baseline = OpenRgbLightingPlan.State(r.Server.Devices[700]);
        Assert.All(blocked.Observe()!.Endpoints, endpoint =>
        {
            Assert.All(endpoint.Capabilities.Where(c => c.Feature != DeviceFeature.RestoreLighting), capability =>
            { Assert.False(capability.Usable); Assert.Contains("選択されていません", capability.Reason); });
            Assert.True(endpoint.Capabilities.Single(c => c.Feature == DeviceFeature.RestoreLighting).Usable);
        });
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target("device", 700)], Blue)).Success);
        Assert.Equal(Outcome.Rejected, (await blocked.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        var native = blocked.Observe()!.Endpoints.Single(e => e.Id == "device").NativeLightingEffects![1];
        Assert.Equal(Outcome.Rejected, (await blocked.ApplyAsync(new("device", DeviceOperation.SetLighting,
            Lighting: new(native.Id, [new(0, 255, 0)], Speed: 50)), default)).Outcome);
        Assert.All(await blocked.ApplyLightingBatchAsync([new(r.Zone(0, 700), Blue), new(r.Zone(1, 700), Blue)], default),
            result => Assert.Equal(Outcome.Rejected, result.Result.Outcome));
        await Assert.ThrowsAsync<IOException>(() => blocked.OpenLightingStreamAsync([r.Zone(0, 700)], Animation, default));
        Assert.Equal(0, r.Server.WriteCount);
        var targets = shared ? new[] { r.Target(r.Zone(0)), r.Target(r.Zone(1)) } : [r.Target(r.Zone(0))];
        Written(await r.Command(DeviceOperation.SetLighting, targets, Blue));
        Assert.True(OpenRgbLightingPlan.Same(baseline, OpenRgbLightingPlan.State(r.Server.Devices[700])));
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Device == 700 && p.Command is 1050 or 1051 or 1101 or 1103);
    }

    [Fact]
    public async Task EmptySelectionDoesNotImplicitlySelectEveryDetectedDevice()
    {
        await using var r = await new Rig(Array.Empty<string>(), Device(), Device(700, "SECOND")).Open();
        Assert.Equal(2, r.Provider.CachedDrivers.Length);
        foreach (var driver in r.Provider.CachedDrivers.Cast<OpenRgbDeviceDriver>())
        {
            Assert.Equal(Outcome.Rejected, (await driver.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
            Assert.Empty(await driver.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default));
        }
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task CallerMutationCannotExpandOrReplaceTheCapturedDeviceSelection()
    {
        var first = Device(); var second = Device(700, "SECOND"); var selection = SelectDevices(first);
        await using var r = new Rig(selection, first, second);
        selection[0] = SelectDevices(second).Single();
        await r.Open(); Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target("device", 700)], Blue)).Success);
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Device == 700 && p.Command is 1050 or 1051 or 1101 or 1103);
    }

    [Fact]
    public async Task ReusedSdkIndexDoesNotGiveTheReplacementTheOldDevicesSelection()
    {
        var first = Device(); await using var r = await new Rig(SelectDevices(first), first).Open();
        var old = r.Request(DeviceOperation.SetLighting, [r.Target("device")], Blue);
        var replacement = first with { Serial = "REPLACEMENT" };
        r.Server.Devices[41] = replacement;
        r.Server.Wire.Devices[41] = OpenRgbSdkModelServer.Encode(replacement);
        var revision = r.Connection!.InventoryRevision; await r.Server.Wire.SendAsync(0, 100, []);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while (r.Connection.InventoryRevision == revision) await Task.Delay(10, timeout.Token);
        await r.Runtime.PollAsync(); await r.Runtime.PollAsync();
        var newDriver = r.Driver(); Rig.Configure(newDriver); r.Runtime.Refresh();
        Assert.NotEqual(old.Targets!.Single().Target.DeviceId, newDriver.DeviceId);
        Assert.False((await r.Runtime.ExecuteAsync(old)).Success);
        Assert.False((await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue)).Success);
        Assert.Equal(0, r.Server.WriteCount);
    }

    [Fact]
    public async Task UnselectedDriverCanRecoverPersistedOutputButCannotStartAnotherEffect()
    {
        await using var r = await new Rig().Open(); var driver = r.Driver();
        var original = OpenRgbLightingPlan.State(r.Server.Devices[41]);
        Written(await r.Command(DeviceOperation.SetLighting, [r.Target("device")], Blue));
        var pending = new OpenRgbLightingRecovery(r.Directory, driver.DeviceId, null).Pending!;
        var snapshot = await r.Connection!.ReadAsync(pending.Scope.Target);
        var identity = OpenRgbDeviceIdentities.Resolve([snapshot], new Dictionary<OpenRgbSdkTarget, string>())[41];
        // Simulate abrupt host loss without running the old driver's normal release first.
        await using var restarted = new OpenRgbDeviceDriver(driver.DeviceId, r.Directory, Boot,
            () => { if (!r.RouteAvailable) throw new IOException("route unavailable"); }, selectedForControl: false);
        restarted.SetConnection(new(r.Connection, snapshot, identity));
        r.RouteAvailable = false; var writes = r.Server.WriteCount;
        Assert.Equal(Outcome.Unknown, Assert.Single(await restarted.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default)).Result.Outcome);
        Assert.Equal(writes, r.Server.WriteCount); Assert.NotEmpty(System.IO.Directory.GetFiles(r.Directory));
        r.RouteAvailable = true;
        Assert.Equal(Outcome.WrittenUnverified, Assert.Single(await restarted.ReleaseAsync(DeviceReleaseReason.StartupRecovery, default)).Result.Outcome);
        Assert.True(OpenRgbLightingPlan.Same(original, OpenRgbLightingPlan.State(r.Server.Devices[41])));
        Assert.Empty(System.IO.Directory.GetFiles(r.Directory)); Rig.Configure(restarted);
        writes = r.Server.WriteCount;
        Assert.Equal(Outcome.Rejected, (await restarted.ApplyAsync(new("device", DeviceOperation.SetLighting, Lighting: Blue), default)).Outcome);
        Assert.Equal(writes, r.Server.WriteCount);
        r.ExpectUnresolvedShutdown = true;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("41")]
    [InlineData("Fixture controller")]
    [InlineData("openrgb-sdk:ABCDEF")]
    [InlineData("other:0000000000000000000000000000000000000000000000000000000000000000")]
    public void InvalidDeviceSelectionIsRejectedBeforeConnecting(string? invalid)
    {
        var calls = 0;
        Assert.Throws<ArgumentException>(() => new OpenRgbDeviceProvider(Installation, Path.GetTempPath(), Boot,
            _ => { calls++; throw new InvalidOperationException(); }, new TestRoutePolicy(() => { }), [invalid!]));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MissingDuplicateOrOversizedSelectionsAreRejectedBeforeConnecting()
    {
        var calls = 0;
        OpenRgbDeviceProvider Create(IEnumerable<string> values) => new(Installation, Path.GetTempPath(), Boot,
            _ => { calls++; throw new InvalidOperationException(); }, new TestRoutePolicy(() => { }), values);
        Assert.Throws<ArgumentNullException>(() => Create(null!));
        var id = SelectDevices(Device()).Single(); Assert.Throws<ArgumentException>(() => Create([id, id]));
        Assert.Throws<ArgumentException>(() => Create(Enumerable.Range(0, 33).Select(i =>
            DeviceIdentity.Create(Installation, OpenRgbDeviceProvider.Provider, "fixture:" + i))));
        Assert.Equal(0, calls);
    }
}
