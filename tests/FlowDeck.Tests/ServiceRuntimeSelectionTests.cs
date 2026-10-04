using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServiceRuntimeSelectionTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    [Fact] public async Task ConfigurationPresenceAloneDoesNotSwitchTheInstalledRuntime()
    {
        using var dir = new ServiceTestDirectory(); Assert.Null(ServiceRuntimeSelection.Read(dir.Path));
        var config = await DeviceRuntimeStartupTests.Package(new DemoHub { DelayMs = 0 }, dir.Path);
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config))); Assert.Null(ServiceRuntimeSelection.Read(dir.Path));
        var selected = new ServiceRuntimeSelection(1, "devices-v2", Installation);
        ServiceEnvironment.Write(Path.Combine(dir.Path, ServiceRuntimeSelection.FileName), selected);
        Assert.Equal(selected, ServiceRuntimeSelection.Read(dir.Path));
        Assert.Equal(SettingsTransfer.Hash(config), SettingsTransfer.Hash(selected.ReadConfiguration(dir.Path)));
    }
    [Fact] public async Task MissingCorruptAndForeignConfigurationCannotSilentlyStartAnotherMode()
    {
        using var dir = new ServiceTestDirectory(); var selected = new ServiceRuntimeSelection(1, "devices-v2", Installation);
        var selectionPath = Path.Combine(dir.Path, ServiceRuntimeSelection.FileName); ServiceEnvironment.Write(selectionPath, selected);
        Assert.Throws<IOException>(() => selected.ReadConfiguration(dir.Path));
        var config = await DeviceRuntimeStartupTests.Package(new DemoHub { DelayMs = 0 }, dir.Path);
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path)); store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var changed = selected with { InstallationId = Guid.NewGuid().ToString("N") }; ServiceEnvironment.Write(selectionPath, changed);
        Assert.Throws<IOException>(() => selected.ReadConfiguration(dir.Path)); Assert.Throws<IOException>(() => changed.ReadConfiguration(dir.Path));
        ServiceEnvironment.Write(selectionPath, selected); File.WriteAllText(Path.Combine(store.DirectoryPath, "current.json"), "{broken");
        Assert.Throws<IOException>(() => selected.ReadConfiguration(dir.Path)); Assert.Equal(selected, ServiceRuntimeSelection.Read(dir.Path));
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(store.DirectoryPath, "current.json")));
    }
    [Theory]
    [InlineData("{\"version\":1,\"runtime\":\"legacy\",\"installationId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"version\":1,\"runtime\":\"devices-v2\",\"installationId\":\"invalid\"}")]
    [InlineData("{\"version\":1,\"runtime\":\"devices-v2\",\"installationId\":\"0123456789abcdef0123456789abcdef\",\"driverDll\":\"untrusted.dll\"}")]
    [InlineData("{\"version\":1,\"version\":1,\"runtime\":\"devices-v2\",\"installationId\":\"0123456789abcdef0123456789abcdef\"}")]
    public void InvalidSelectionIsNotTreatedAsAnAbsentSelection(string json)
    {
        using var dir = new ServiceTestDirectory(); var path = Path.Combine(dir.Path, ServiceRuntimeSelection.FileName); File.WriteAllText(path, json);
        Assert.ThrowsAny<Exception>(() => ServiceRuntimeSelection.Read(dir.Path)); Assert.Equal(json, File.ReadAllText(path));
    }
    [Fact] public async Task ComponentFactoryReceivesTheValidatedGenerationWithoutBeingAbleToMutateIt()
    {
        using var dir = new ServiceTestDirectory(); var config = await DeviceRuntimeStartupTests.Package(new DemoHub { DelayMs = 0 }, dir.Path);
        var store = DeviceConfigurationCodec.Store(Path.Combine(dir.Path, "configuration")); store.Activate(store.Stage(DeviceConfigurationCodec.Write(config)));
        var called = false;
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, Installation, actual =>
        {
            called = true; Assert.Equal(SettingsTransfer.Hash(config), SettingsTransfer.Hash(actual));
            actual.Devices.Devices[0] = actual.Devices.Devices[0] with { Name = "factory mutation" };
            return new([], []);
        }, ["user"], new TestLog(), demo: true);
        Assert.True(called); Assert.Equal(config.Devices.Devices[0].Name, startup.Configuration.Devices.Devices[0].Name);
    }
}
