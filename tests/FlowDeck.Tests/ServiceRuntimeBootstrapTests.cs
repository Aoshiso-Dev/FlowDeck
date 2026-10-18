using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServiceRuntimeBootstrapTests
{
    [Fact] public void InitialSetupCreatesOneStableMonitoringGenerationAndLeavesLegacyBytesUntouched()
    {
        using var dir = new ServiceTestDirectory();
        var original = "legacy settings and formatting\r\n"u8.ToArray(); var path = Path.Combine(dir.Path, "cooling-settings.json");
        File.WriteAllBytes(path, original);
        var selection = new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring(); var config = selection.ReadConfiguration(dir.Path);
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path)); var before = store.ReadActivePointer();
        Assert.Equal(selection, new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring()); Assert.Equal(before, store.ReadActivePointer());
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Empty(config.Devices.Devices); Assert.Empty(config.Bindings.Devices);
        Assert.Equal(selection.InstallationId, config.Bindings.InstallationId); Assert.Empty(config.Cooling.Endpoints);
        Assert.Empty(config.Lighting.Scenes); Assert.Empty(config.Lighting.SyncTargets); Assert.Empty(config.Dashboard.Items);
        Assert.False(config.Preferences.Startup.ResumeControl); Assert.False(config.Preferences.Startup.Login);
        Assert.False(config.Preferences.Providers!.OpenRgbEnabled);
    }
    [Theory] [InlineData("intent")] [InlineData("generation")] [InlineData("before-selection")] [InlineData("selection")]
    public void InterruptedSetupRetainsItsIdentityAndResumesOnlyItsExactInitialGeneration(string boundary)
    {
        using var dir = new ServiceTestDirectory();
        var bootstrap = new ServiceRuntimeBootstrap(dir.Path) { Checkpoint = step => { if (step == boundary) throw new IOException("crash"); } };
        Assert.Throws<IOException>(() => bootstrap.PrepareMonitoring());
        var intent = ServiceEnvironment.Read<DeviceBootstrapIntent>(Path.Combine(dir.Path, "devices-v2", ServiceRuntimeBootstrap.IntentName));
        Assert.Equal(boundary == "selection", ServiceRuntimeSelection.Read(dir.Path) is not null);
        var selection = new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring(); Assert.Equal(intent.InstallationId, selection.InstallationId);
        Assert.Equal(intent.Generation, DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path)).ReadActivePointer());
    }
    [Fact] public void AChangedOrUnownedGenerationCannotBeResetOrReactivatedAsAnInitialConfiguration()
    {
        using var dir = new ServiceTestDirectory(); var first = new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring();
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path));
        var config = first.ReadConfiguration(dir.Path); config = config with { Preferences = config.Preferences with { Ui = new("dark") } };
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(config))); var adopted = store.ReadActivePointer();
        Assert.Equal(first, new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring()); Assert.Equal(adopted, store.ReadActivePointer());
        File.Delete(Path.Combine(dir.Path, ServiceRuntimeSelection.FileName));
        Assert.Throws<IOException>(() => new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring()); Assert.Null(ServiceRuntimeSelection.Read(dir.Path));
        Assert.Equal(adopted, store.ReadActivePointer());
        File.Delete(Path.Combine(dir.Path, "devices-v2", ServiceRuntimeBootstrap.IntentName));
        Assert.Throws<IOException>(() => new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring()); Assert.Equal(adopted, store.ReadActivePointer());
    }
    [Fact] public void ACorruptIntentOrGenerationIsPreservedWithoutActivation()
    {
        using var dir = new ServiceTestDirectory();
        var bootstrap = new ServiceRuntimeBootstrap(dir.Path) { Checkpoint = step => { if (step == "intent") throw new IOException("crash"); } };
        Assert.Throws<IOException>(() => bootstrap.PrepareMonitoring());
        var path = Path.Combine(dir.Path, "devices-v2", ServiceRuntimeBootstrap.IntentName); var bytes = File.ReadAllBytes(path);
        File.WriteAllText(path, "{broken"); Assert.ThrowsAny<Exception>(() => new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring());
        Assert.Equal("{broken", File.ReadAllText(path)); File.WriteAllBytes(path, bytes);
        var intent = ServiceEnvironment.Read<DeviceBootstrapIntent>(path);
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path));
        var component = Path.Combine(store.DirectoryPath, "generations", intent.Generation.Id, "cooling.json"); File.AppendAllText(component, "changed");
        Assert.Throws<IOException>(() => new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring()); Assert.Null(store.ReadActivePointer());
        Assert.Null(ServiceRuntimeSelection.Read(dir.Path)); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact] public async Task IntentCannotActivateAValidButNonMonitoringGeneration()
    {
        using var dir = new ServiceTestDirectory(); var config = await DeviceRuntimeStartupTests.Package(new DemoHub { DelayMs = 0 }, dir.Path);
        var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(dir.Path)); var staged = store.Stage(DeviceConfigurationCodec.Write(config));
        ServiceEnvironment.Write(Path.Combine(dir.Path, "devices-v2", ServiceRuntimeBootstrap.IntentName), new DeviceBootstrapIntent(1, config.Bindings.InstallationId!, staged.Target));
        Assert.Throws<IOException>(() => new ServiceRuntimeBootstrap(dir.Path).PrepareMonitoring()); Assert.Null(store.ReadActivePointer()); Assert.Null(ServiceRuntimeSelection.Read(dir.Path));
    }
}
