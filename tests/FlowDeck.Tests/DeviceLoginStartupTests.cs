using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.Tests;

public class DeviceLoginStartupTests
{
    private const string Executable = @"C:\Flow Deck 日本語\FlowDeck.exe";
    private static DeviceLoginStartup Create(DemoLoginStartupRegistration registry, bool exists = true) => new(Executable, registry, _ => exists);
    [Fact] public async Task ReadingDoesNotCreateRegistrationAndEnablingAlwaysUsesServiceAndTray()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry);
        Assert.Equal("\"" + Executable + "\" --devices --tray", login.Command);
        var before = await login.ReadAsync(); Assert.Equal(DeviceLoginStartupState.Unregistered, before.State); Assert.Equal(0, registry.Writes);
        var saved = await login.SaveAsync(before, true); Assert.Equal(DeviceLoginStartupState.Registered, saved.State);
        Assert.Equal(login.Command, registry.Value.Command); Assert.Equal(1, registry.Writes);
        Assert.Equal(saved, await login.SaveAsync(saved, true)); Assert.Equal(1, registry.Writes);
        Assert.Equal(DeviceLoginStartupState.Unregistered, (await login.SaveAsync(saved, false)).State); Assert.Equal(2, registry.Writes);
    }
    [Theory] [InlineData("FlowDeck.exe")] [InlineData(@"C:FlowDeck.exe")] [InlineData(@"C:\App\FlowDeck.dll")] [InlineData("C:\\a\"b\\FlowDeck.exe")] [InlineData("C:\\a\nb\\FlowDeck.exe")]
    public void RelativeOrNonExecutableOrInjectablePathsAreRejected(string executable) => Assert.Throws<ArgumentException>(() => DeviceLoginStartup.BuildCommand(executable));
    [Fact] public void RunValueRespectsDocumented260CharacterLimit()
    {
        const string suffix = "\\FlowDeck.exe";
        var overhead = "C:\\".Length + suffix.Length + "\"\" --devices --tray".Length;
        Assert.Equal(260, DeviceLoginStartup.BuildCommand("C:\\" + new string('a', 260 - overhead) + suffix).Length);
        Assert.Throws<ArgumentException>(() => DeviceLoginStartup.BuildCommand("C:\\" + new string('a', 261 - overhead) + suffix));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AnotherRegistrationCannotBeRemovedOrReplacedImplicitly(bool enable)
    {
        var registry = new DemoLoginStartupRegistration { Value = DeviceLoginStartupValue.From(RegistryValueKind.String, @"""C:\Other\FlowDeck.exe"" --tray") };
        var login = Create(registry); var before = await login.ReadAsync(); var original = registry.Value;
        Assert.Equal(DeviceLoginStartupState.OtherRegistration, before.State);
        await Assert.ThrowsAsync<IOException>(() => login.SaveAsync(before, enable)); Assert.Equal(original, registry.Value); Assert.Equal(0, registry.Writes);
        if (enable) Assert.Equal(DeviceLoginStartupState.Registered, (await login.SaveAsync(before, true, replaceOther: true)).State);
        else await Assert.ThrowsAsync<IOException>(() => login.SaveAsync(before, false, replaceOther: true));
    }
    [Fact] public async Task InvalidTypeAndExpandableStringsAreNotTreatedAsOwnedCommands()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry);
        foreach (var value in new[] { DeviceLoginStartupValue.From(RegistryValueKind.ExpandString, login.Command),
            DeviceLoginStartupValue.From(RegistryValueKind.Binary, new byte[] { 2, 0, 0, 0 }) })
        {
            registry.Value = value; var actual = await login.ReadAsync(); Assert.Equal(DeviceLoginStartupState.OtherRegistration, actual.State);
            await Assert.ThrowsAsync<IOException>(() => login.SaveAsync(actual, false)); Assert.Equal(value, registry.Value);
        }
    }
    [Fact] public async Task PathCaseIsInsensitiveButRequiredSwitchesAreCaseSensitive()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry);
        registry.Value = DeviceLoginStartupValue.From(RegistryValueKind.String, "\"" + Executable.ToUpperInvariant() + "\" --devices --tray");
        Assert.Equal(DeviceLoginStartupState.Registered, (await login.ReadAsync()).State);
        registry.Value = DeviceLoginStartupValue.From(RegistryValueKind.String, login.Command.ToUpperInvariant());
        Assert.Equal(DeviceLoginStartupState.OtherRegistration, (await login.ReadAsync()).State);
    }
    [Fact] public async Task StaleSnapshotAndChangeImmediatelyBeforeWritingAreBothRejected()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry); var snapshot = await login.ReadAsync();
        var other = DeviceLoginStartupValue.From(RegistryValueKind.String, "other-app"); registry.Value = other;
        await Assert.ThrowsAsync<IOException>(() => login.SaveAsync(snapshot, true)); Assert.Equal(0, registry.Writes);
        snapshot = await login.ReadAsync(); registry.BeforeWrite = () => registry.Value = DeviceLoginStartupValue.Missing;
        await Assert.ThrowsAsync<IOException>(() => login.SaveAsync(snapshot, true, true)); Assert.Equal(0, registry.Writes);
    }
    [Fact] public async Task UnverifiableWriteNeverClaimsSuccessOrOverwritesExternalChangesWithRollback()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry);
        var other = DeviceLoginStartupValue.From(RegistryValueKind.String, "external-change"); registry.AfterWrite = () => registry.Value = other;
        await Assert.ThrowsAsync<IOException>(async () => await login.SaveAsync(await login.ReadAsync(), true));
        Assert.Equal(other, registry.Value); Assert.Equal(1, registry.Writes);
    }
    [Fact] public async Task MissingExecutableCanBeUnregisteredButCannotBeNewlyRegistered()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry, false);
        await Assert.ThrowsAsync<IOException>(async () => await login.SaveAsync(await login.ReadAsync(), true)); Assert.Equal(0, registry.Writes);
        registry.Value = DeviceLoginStartupValue.From(RegistryValueKind.String, login.Command);
        Assert.Equal(DeviceLoginStartupState.Unregistered, (await login.SaveAsync(await login.ReadAsync(), false)).State);
    }
    [Fact] public async Task AccessDeniedReadsAreExplicitAndFailedWritesPreservePreviousRegistration()
    {
        var registry = new DemoLoginStartupRegistration { DenyRead = true }; var login = Create(registry);
        var unreadable = await login.ReadAsync(); Assert.Equal(DeviceLoginStartupState.Unavailable, unreadable.State);
        registry.DenyRead = false; var snapshot = await login.ReadAsync(); registry.DenyWrite = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => login.SaveAsync(snapshot, true)); Assert.Equal(DeviceLoginStartupValue.Missing, registry.Value);
        await Assert.ThrowsAsync<IOException>(() => login.SaveAsync(unreadable, true)); Assert.Equal(0, registry.Writes);
    }
    [Fact] public async Task ConcurrentStaleEditsCannotUndoOneAnother()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry); var snapshot = await login.ReadAsync();
        var first = login.SaveAsync(snapshot, true); var second = login.SaveAsync(snapshot, false);
        Assert.Equal(DeviceLoginStartupState.Registered, (await first).State); await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal(1, registry.Writes);
    }
    [Fact] public async Task DemoSettingsActionDoesNotInvokeAShellOrRegisterAnything()
    {
        var registry = new DemoLoginStartupRegistration(); var login = Create(registry);
        await login.OpenWindowsSettingsAsync(); Assert.Equal(1, registry.SettingsOpened); Assert.Equal(0, registry.Writes); Assert.Equal(0, registry.Reads);
    }
    [Fact] public void StartupUiTestRejectsRealOrMixedRoutes()
    {
        string[] basic = ["--devices-preview", "--demo", "--data-dir", "demo"];
        Assert.True(DeviceWorkspaceOptions.Parse([.. basic, "--smoke", "--login-startup"]).LoginStartup);
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. basic, "--login-startup"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. basic, "--smoke", "--login-startup", "--diagnostics"]));
        Assert.Throws<ArgumentException>(() => DeviceWorkspaceOptions.Parse([.. basic, "--smoke", "--login-startup", "--devices-pipe", "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"), "--server-pid", "1"]));
    }
}
