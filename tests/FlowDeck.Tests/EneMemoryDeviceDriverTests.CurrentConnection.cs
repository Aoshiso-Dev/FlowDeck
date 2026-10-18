using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed partial class EneMemoryDeviceDriverTests
{
    private static void ConfigureCurrent(Rig rig, LightingExitPolicy? exit = null, int savedCount = 7)
    {
        var driver = rig.Driver();
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, driver.BindingCandidate! with { Fingerprint = new('f', 64) }, [],
            [new(new(driver.DeviceId, Endpoint), false, exit)],
            [new(Endpoint, "RGB", EndpointKind.Lighting, Setup: new(LedCount: savedCount, LedCountConfirmed: false))]);
        rig.Runtime.Refresh();
    }

    [Theory]
    [InlineData(LightingExitAction.RestoreOriginal)]
    [InlineData(LightingExitAction.Off)]
    [InlineData(LightingExitAction.FixedColor)]
    public async Task CurrentConnectionUsesActualEightLedsAndSavedExitWithoutConfirmationOrOldOwnership(LightingExitAction exit)
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false);
        var policy = new LightingExitPolicy(exit, exit == LightingExitAction.FixedColor ? new(9, 8, 7) : null);
        ConfigureCurrent(rig, policy);
        var driver = rig.Driver(); Assert.Equal(driver.BindingCandidate, driver.CurrentControlBinding);
        Assert.False(driver.UsesConnectionLighting); Assert.Empty(rack.Writes); Assert.Null(rig.Pending());
        Assert.True(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.FixedColor));
        Assert.False(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.FixedDuty));
        Assert.False(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.Temperature));
        Assert.False(driver.AllowsCurrentConnectionControl("zone:1", DeviceFeature.FixedColor));
        Assert.Equal(8, Assert.Single(driver.DescribeSetup()).DetectedLeds);

        // The first explicit operation must capture the current hardware, not the earlier poll.
        rack.Registers[(0x71, 0x8167)] = 146;
        var original = Snapshot(rack);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
        var pending = rig.Pending()!;
        Assert.True(EneMemoryProtocol.Same(original, pending.Original));
        Assert.Equal(driver.CurrentControlBinding, pending.Scope.Binding);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome);
        if (exit == LightingExitAction.RestoreOriginal) Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack)));
        else
        {
            Assert.Equal(exit == LightingExitAction.Off ? 0 : 1, Snapshot(rack).Control[1]);
            if (exit == LightingExitAction.FixedColor) Assert.Equal(new byte[] { 9, 7, 8 }, Snapshot(rack).NativeColors[..3]);
        }
        Assert.Null(rig.Pending());
    }

    [Fact]
    public async Task KnownNewControllerNeedsNoSavedConfigurationAndDefaultsToItsFreshOriginal()
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false); var driver = rig.Driver();
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []); rig.Runtime.Refresh();
        Assert.True(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.ColorFrames)); Assert.Empty(rack.Writes);
        var original = Snapshot(rack);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome);
        Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack)));
    }

    [Theory] [InlineData("disconnect")] [InlineData("platform")] [InlineData("invalidate")]
    public async Task ReidentifiedConnectionRejectsOldRequestAndAcceptsOnlyNewExplicitOutput(string change)
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false); ConfigureCurrent(rig);
        var before = rig.Driver().CurrentControlBinding;
        var old = rig.Capture(DeviceOperation.SetLighting, [0x71], new("fixed", [new(1, 2, 3)]));
        if (change == "disconnect") { rack.Present.Clear(); await rig.Runtime.PollAsync(); rack.Present.Add(0x71); }
        if (change == "platform") rack.Platform = "replacement platform";
        if (change == "invalidate") rig.Backend.Invalidate();
        await rig.Runtime.PollAsync(); await rig.Runtime.PollAsync();
        Assert.NotEqual(before, rig.Driver().CurrentControlBinding);
        Assert.False((await rig.Runtime.ExecuteAsync(old)).Success); Assert.Empty(rack.Writes); Assert.Null(rig.Pending());
        rack.Registers[(0x71, 0x8164)] = 212; var original = Snapshot(rack);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
        Assert.True(EneMemoryProtocol.Same(original, rig.Pending()!.Original));
        Assert.Equal(rig.Driver().CurrentControlBinding, rig.Pending()!.Scope.Binding);
    }

    [Fact]
    public async Task ReidentifiedConnectionCannotAdoptAnOldPendingRecordOrItsOriginal()
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false); ConfigureCurrent(rig);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
        var pending = rig.Pending()!; var writes = rack.Writes.Count;
        rig.Backend.Invalidate(); await rig.Runtime.PollAsync(); await rig.Runtime.PollAsync();
        Assert.False(rig.Driver().AllowsCurrentConnectionControl(Endpoint, DeviceFeature.FixedColor));
        Assert.False(rig.Driver().AllowsCurrentConnectionControl(Endpoint, DeviceFeature.RestoreLighting));
        Assert.False((await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(1, 2, 3)]))).Success);
        Assert.Throws<IOException>(() => ConfigureCurrent(rig));
        Assert.Equal(writes, rack.Writes.Count); Assert.Equal(pending.Hash, rig.Pending()!.Hash);
        Assert.Equal(pending.Scope, rig.Pending()!.Scope);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CurrentRecoveryRemainsAvailableAfterReadFailureOrPartialWrite(bool partialWrite)
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false); ConfigureCurrent(rig);
        var original = Snapshot(rack); using var cancel = new CancellationTokenSource();
        if (partialWrite)
        {
            rack.BeforeData = (register, write) => { if (write && register == 0x8161) cancel.Cancel(); };
            var result = await rig.Driver().ApplyAsync(new(Endpoint, DeviceOperation.SetLighting,
                Lighting: new("fixed", [new(30, 40, 50)])), cancel.Token);
            Assert.Equal(Outcome.Unknown, result.Outcome); Assert.Single(rack.Writes);
        }
        else
        {
            Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
            rack.BeforeData = (_, write) => { if (!write) cancel.Cancel(); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Driver().PollAsync(cancel.Token));
        }
        var pending = rig.Pending()!; var writes = rack.Writes.Count;
        Assert.False(rig.Driver().AllowsCurrentConnectionControl(Endpoint, DeviceFeature.FixedColor));
        Assert.True(rig.Driver().AllowsCurrentConnectionControl(Endpoint, DeviceFeature.RestoreLighting));
        Assert.Equal(writes, rack.Writes.Count); Assert.Equal(pending.Hash, rig.Pending()!.Hash);
        rack.BeforeData = null;
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome);
        Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack))); Assert.Null(rig.Pending());
    }

    [Fact]
    public async Task RemovingCurrentLightingSettingsKeepsKnownDefaultsWithoutConfirmationOrOutput()
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false);
        ConfigureCurrent(rig, new(LightingExitAction.FixedColor, new(9, 8, 7)));
        var driver = rig.Driver(); var current = driver.BindingCandidate!;
        var endpoint = new RegisteredEndpoint(Endpoint, "RGB", EndpointKind.Lighting, Setup: new(LedCount: 8));
        driver.ConfigureLightingEndpoint(current, Endpoint, null, endpoint); rig.Runtime.Refresh();
        Assert.Equal(current, driver.CurrentControlBinding);
        Assert.True(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.FixedColor));
        Assert.True(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.ColorFrames));
        Assert.False(driver.UsesConnectionLighting); Assert.Equal(8, Assert.Single(driver.DescribeSetup()).DetectedLeds);
        Assert.False(endpoint.Setup!.LedCountConfirmed); Assert.Empty(rack.Writes); Assert.Null(rig.Pending());

        // The removed fixed exit must not survive as ownership or as the next operation's policy.
        rack.Registers[(0x71, 0x8165)] = 153; var original = Snapshot(rack);
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
        Assert.True(EneMemoryProtocol.Same(original, rig.Pending()!.Original));
        Assert.True(EneMemoryProtocol.Same(original, rig.Pending()!.Exit));
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome);
        Assert.True(EneMemoryProtocol.Same(original, Snapshot(rack))); Assert.Null(rig.Pending());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RemovingLightingSettingsCannotRewriteCurrentOrReconnectedRecovery(bool reconnect)
    {
        var rack = new Rack(0x71); await using var rig = await new Rig(rack).Open(configure: false);
        ConfigureCurrent(rig, new(LightingExitAction.FixedColor, new(9, 8, 7)));
        Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.SetLighting, [0x71], new("fixed", [new(30, 40, 50)]))).Result!.Outcome);
        var pending = rig.Pending()!; var writes = rack.Writes.Count;
        if (reconnect) { rig.Backend.Invalidate(); await rig.Runtime.PollAsync(); await rig.Runtime.PollAsync(); }
        var driver = rig.Driver();
        Assert.Throws<IOException>(() => driver.ConfigureLightingEndpoint(driver.BindingCandidate!, Endpoint, null,
            new(Endpoint, "RGB", EndpointKind.Lighting, Setup: new(LedCount: 8))));
        Assert.Equal(writes, rack.Writes.Count); Assert.Equal(pending.Hash, rig.Pending()!.Hash);
        Assert.Equal(pending.Scope, rig.Pending()!.Scope);
        if (!reconnect)
        {
            Assert.Equal(Outcome.Confirmed, (await rig.Command(DeviceOperation.StopLighting, [0x71])).Result!.Outcome);
            Assert.Equal(new byte[] { 9, 7, 8 }, Snapshot(rack).NativeColors[..3]);
        }
        else Assert.False(driver.AllowsCurrentConnectionControl(Endpoint, DeviceFeature.FixedColor));
    }
}
