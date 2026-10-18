using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceCurrentConnectionPersistenceTests
{
    [Fact]
    public async Task ExplicitDutyUsesActualSafetyButCannotAdoptAnOldBindingOrItsAutomaticResumeIntent()
    {
        const string installation = "0123456789abcdef0123456789abcdef";
        using var directory = new ServiceTestDirectory();
        var hub = new DemoHub { DelayMs = 0 };
        var config = await DeviceRuntimeStartupTests.Package(hub, directory.Path);
        var savedBinding = config.Bindings.Devices.Single() with { Fingerprint = new('c', 64) };
        config = config with {
            Bindings = config.Bindings with { Devices = [savedBinding] },
            Cooling = config.Cooling with { Endpoints = config.Cooling.Endpoints.Select(e => e with { WiringConfirmed = false }).ToArray() }
        };
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory.Path, "configuration"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var pointer = store.ReadActivePointer();
        var driver = new HubDeviceDriver(installation, DemoHub.Identity.Id, hub, directory.Path);
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, installation, () => new([driver], []),
            ["user"], new TestLog(), demo: true);
        var runtime = startup.Runtime; var hello = runtime.OpenSession("user"); long sequence = 0;
        DeviceServiceRequest Request(DeviceOperation operation, int? duty = null)
        {
            var state = runtime.State(); var row = state.Endpoints.Single(e => e.Target.EndpointId == "fan1");
            return new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", operation,
                state.Inventory.TopologyRevision, [new(row.Target, row.Generation, row.PolicyRevision)], duty);
        }
        Assert.Equal(0, hub.Writes);
        Assert.Equal(pointer, store.ReadActivePointer());
        Assert.NotEqual(savedBinding, driver.CurrentControlBinding);
        Assert.False(driver.CoolingConfiguration("fan1")!.ResumeOnStart);
        Assert.False((await runtime.ExecuteAsync(Request(DeviceOperation.RestoreSavedCooling))).Success);
        Assert.Equal(0, hub.Writes);
        var manual = await runtime.ExecuteAsync(Request(DeviceOperation.SetDuty, 5));
        Assert.True(manual.Success, manual.Error);
        var result = Assert.Single(manual.Result!.Targets);
        Assert.Equal(30, result.ReportedDuty);
        Assert.Equal(DevicePolicySaveState.Saved, result.Persistence!.State);
        var saved = startup.Configuration.Cooling.Endpoints.Single();
        Assert.Equal(5, saved.SavedPolicy!.Duty);
        Assert.False(saved.ResumeOnStart);
        Assert.False(saved.WiringConfirmed);
        Assert.Equal(savedBinding, Assert.Single(startup.Configuration.Bindings.Devices));
        Assert.Equal(SettingsTransfer.Hash(config.Cooling.Endpoints.Single().Safety), SettingsTransfer.Hash(saved.Safety));
        var next = await runtime.ExecuteAsync(Request(DeviceOperation.SetDuty, 55));
        Assert.True(next.Success, next.Error);
        Assert.Equal(DevicePolicySaveState.Saved, Assert.Single(next.Result!.Targets).Persistence!.State);
        Assert.Equal(2, hub.Writes);
        Assert.False((await runtime.ExecuteAsync(Request(DeviceOperation.RestoreSavedCooling))).Success);
        Assert.Equal(2, hub.Writes);
    }
}
