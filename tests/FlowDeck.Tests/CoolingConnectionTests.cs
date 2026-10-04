using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class CoolingConnectionTests
{
    [Fact] public void RegisteredOrExplicitServiceNeverFallsBackToDirectAndDemoCannotReachRealService()
    {
        Assert.True(CoolingConnectionOptions.Resolve([], "auto", true).UseService);
        Assert.False(CoolingConnectionOptions.Resolve([], "auto", false).UseService);
        Assert.True(CoolingConnectionOptions.Resolve([], "service", false).UseService);
        Assert.True(CoolingConnectionOptions.Resolve(["--service"], "direct", false).UseService);
        Assert.False(CoolingConnectionOptions.Resolve(["--direct"], "service", true).UseService);
        Assert.Throws<ArgumentException>(() => CoolingConnectionOptions.Resolve(["--service", "--direct"], "auto", false));
        Assert.False(CoolingConnectionOptions.Resolve(["--demo", "--smoke", "--data-dir", "isolated"], "service", true).UseService);
        Assert.Throws<ArgumentException>(() => CoolingConnectionOptions.Resolve(["--service", "--demo", "--smoke", "--data-dir", "isolated"], "auto", true));
        Assert.Throws<ArgumentException>(() => CoolingConnectionOptions.Resolve(["--demo-service-pipe", "FlowDeck.Cooling.v1", "--demo-service-pid", "1"], "auto", false));
        var pipe = "FlowDeck.Cooling.Test." + Guid.NewGuid().ToString("N");
        Assert.True(CoolingConnectionOptions.Resolve(["--demo", "--smoke", "--data-dir", "isolated", "--demo-service-pipe", pipe, "--demo-service-pid", "1"], "auto", false).UseService);
    }
    [Fact] public void ServiceGuiMutexIsSeparateFromHardwareOwnershipButStillPreventsDuplicateGui()
    {
        var prefix = "FlowDeck.Test." + Guid.NewGuid().ToString("N");
        using var hardware = new ApplicationInstance(false, prefix); Assert.True(hardware.Acquired);
        using var gui = new ApplicationInstance(false, prefix, service: true); Assert.True(gui.Acquired);
        using var duplicate = new ApplicationInstance(false, prefix, service: true); Assert.False(duplicate.Acquired);
        using var direct = new ApplicationInstance(false, prefix); Assert.False(direct.Acquired);
    }
    [Fact] public void ConnectionPreferenceIsLocalAndRejectsInvalidValues()
    {
        using var dir = new ServiceTestDirectory(); var store = new CoolingConnectionPreferenceStore(dir.Path);
        Assert.Equal("auto", store.Load()); store.Save("service"); Assert.Equal("service", store.Load());
        Assert.Throws<ArgumentException>(() => store.Save("fallback")); Assert.Equal("service", store.Load());
    }
}
