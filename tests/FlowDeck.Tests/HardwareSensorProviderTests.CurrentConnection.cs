using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class HardwareSensorProviderTests
{
    [Fact]
    public async Task CurrentTemperatureInputRequiresExactLiveMeasurementAndNeverGrantsCooling()
    {
        var clock = new TestClock(); var gpu = Gpu(clock); var backend = new Backend { Devices = [gpu] };
        await using var provider = new HardwareSensorProvider(Installation, backend); await provider.PollAsync(default);
        var driver = Assert.IsType<HardwareSensorDeviceDriver>(Assert.Single(provider.CachedDrivers));
        var endpoint = driver.Observe()!.Endpoints.Single(e => e.Capabilities.Any(c => c.Feature == DeviceFeature.Temperature)).Id;
        var reads = backend.Reads;
        driver.ConfigureCurrentConnection(driver.BindingCandidate!, null, [], [], []);
        Assert.True(driver.AllowsCurrentConnectionControl(endpoint, DeviceFeature.Temperature));
        Assert.False(driver.AllowsCurrentConnectionControl(endpoint, DeviceFeature.FixedDuty));
        Assert.False(driver.AllowsCurrentConnectionControl(endpoint, DeviceFeature.AutomaticDuty));
        Assert.False(driver.AllowsCurrentConnectionControl("unknown", DeviceFeature.Temperature));
        Assert.Equal(reads, backend.Reads);
        backend.Fail = true; await Assert.ThrowsAsync<IOException>(() => provider.PollAsync(default));
        Assert.False(driver.AllowsCurrentConnectionControl(endpoint, DeviceFeature.Temperature));
    }
}
