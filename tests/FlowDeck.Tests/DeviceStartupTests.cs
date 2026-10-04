using System.IO.Pipes;
using System.Security.Principal;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceStartupTests
{
    [Fact] public void NormalAndTrayLaunchesUseANewUserDirectoryWithoutImportingLegacyData()
    {
        using var directory = new ServiceTestDirectory();
        foreach (var legacy in new[] { "FlowDeck", "NzxtControlHub" })
        {
            Directory.CreateDirectory(Path.Combine(directory.Path, legacy));
            File.WriteAllText(Path.Combine(directory.Path, legacy, "settings.json"), "legacy settings must not be read");
            File.WriteAllText(Path.Combine(directory.Path, legacy, "cooling-connection.json"), "direct");
        }
        foreach (string[] args in new string[][] { [], ["--devices"], ["--service"], ["--tray"], ["--service", "--tray"] })
        {
            Assert.False(DeviceStartupOptions.LegacyDemo(args));
            var options = DeviceStartupOptions.Parse(args, directory.Path);
            Assert.Equal(Path.Combine(directory.Path, "FlowDeck", "devices-v2"), options.Directory);
            Assert.False(Directory.Exists(options.Directory));
            Assert.Equal(args.Contains("--tray"), options.StartInTray); Assert.False(options.Smoke);
        }
        Assert.Equal(4, Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Length);
    }
    [Theory]
    [InlineData("--direct")] [InlineData("--smoke")] [InlineData("--devices-pipe")]
    [InlineData("--demo")] [InlineData("--devices-startup-smoke")] [InlineData("--data-dir")]
    [InlineData("--legacy")] [InlineData("--asrock-rgb-usb")]
    public void NormalEntryRejectsOptionsThatCouldSelectLegacyOrUntrustedConnections(string arg) =>
        Assert.Throws<ArgumentException>(() => DeviceStartupOptions.Parse([arg], @"C:\Unused"));
    [Fact] public void DuplicateOrMissingDirectoryAndMixedDemoSwitchesAreRejected()
    {
        foreach (var args in new[] { new[] { "--devices", "--devices" }, ["--data-dir", ""], ["--data-dir", "--tray"],
            ["--data-dir", "one", "--data-dir", "two"], ["--devices-startup-smoke", "--demo"],
            ["--devices-startup-smoke", "--demo", "--data-dir", "demo", "--service"], ["--devices", "--demo"] })
            Assert.Throws<ArgumentException>(() => DeviceStartupOptions.Parse(args, @"C:\Unused"));
    }
    [Fact] public void StartupSmokeRequiresAnExplicitDirectoryAndNeverRoutesIntoTheOldHost()
    {
        string[] args = ["--devices-startup-smoke", "--demo", "--data-dir", @"C:\Test Data", "--tray"];
        var parsed = DeviceStartupOptions.Parse(args, @"C:\Unused");
        Assert.True(parsed.Smoke); Assert.True(parsed.StartInTray); Assert.Equal(@"C:\Test Data", parsed.Directory);
        Assert.False(DeviceStartupOptions.LegacyDemo(args));
        Assert.False(DeviceStartupOptions.LegacyDemo(["--devices-preview", "--demo"]));
        Assert.True(DeviceStartupOptions.LegacyDemo(["--demo", "--smoke", "--data-dir", "legacy-test"]));
    }
    [Fact] public void LoginRegistrationSelectsTheGenericEntryExplicitly()
    {
        var command = DeviceLoginStartup.BuildCommand(@"C:\FlowDeck\FlowDeck.exe");
        Assert.EndsWith(" --devices --tray", command);
        Assert.False(DeviceStartupOptions.LegacyDemo(["--devices", "--tray"]));
    }
    [Theory]
    [InlineData(DeviceServiceRegistration.Missing, DeviceStartupState.NotInstalled)]
    [InlineData(DeviceServiceRegistration.Waiting, DeviceStartupState.WaitingForService)]
    [InlineData((DeviceServiceRegistration)900, DeviceStartupState.Unavailable)]
    public async Task MissingOrUnconfirmedServiceDoesNotCreateATransport(DeviceServiceRegistration registration, DeviceStartupState expected)
    {
        await using var result = await DeviceStartupConnection.ConnectAsync(() => registration,
            () => throw new InvalidOperationException("No pipe should be created"));
        Assert.Equal(expected, result.State); Assert.NotEmpty(result.Status); Assert.NotEmpty(result.NextAction);
        Assert.Throws<InvalidOperationException>(() => result.TakeClient());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ServiceRegistrationFailuresAreVisibleWithoutStartingAnything(bool permission)
    {
        await using var result = await DeviceStartupConnection.ConnectAsync(
            () => throw (permission ? new UnauthorizedAccessException("private details") : new IOException("private details")),
            () => throw new InvalidOperationException("No pipe should be created"));
        Assert.Equal(permission ? DeviceStartupState.AccessDenied : DeviceStartupState.Unavailable, result.State);
        Assert.DoesNotContain("private details", result.Status + result.NextAction);
    }
    [Fact] public async Task RetryRechecksRegistrationAndTransfersOneAuthenticatedReadOnlySession()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        var registration = DeviceServiceRegistration.Waiting; var factories = 0;
        DeviceServicePipeClient Factory() { factories++; return DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId); }
        await using var waiting = await DeviceStartupConnection.ConnectAsync(() => registration, Factory);
        Assert.Equal(DeviceStartupState.WaitingForService, waiting.State); Assert.Equal(0, factories);
        registration = DeviceServiceRegistration.Running;
        await using var connected = await DeviceStartupConnection.ConnectAsync(() => registration, Factory);
        Assert.Equal(DeviceStartupState.Connected, connected.State); Assert.Equal(1, factories);
        await using var client = connected.TakeClient(); await connected.DisposeAsync();
        Assert.True(client.Snapshot!.Connected); Assert.True(client.IsDemo);
        Assert.Throws<InvalidOperationException>(() => connected.TakeClient());
        await client.RefreshAsync(); Assert.All(demo.Hubs, h => { Assert.Equal(0, h.Writes); Assert.Equal(0, h.LightingWrites); });
    }
    [Fact] public async Task DisposingAnUntakenAttemptClosesThePipe()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        var pipe = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId);
        await using var result = await DeviceStartupConnection.ConnectAsync(() => DeviceServiceRegistration.Running, () => pipe);
        Assert.True(pipe.Connected); await result.DisposeAsync(); Assert.False(pipe.Connected);
        Assert.Throws<InvalidOperationException>(() => result.TakeClient());
    }
    [Fact] public async Task AuthenticationFailureNeverBecomesAUsableSession()
    {
        using var directory = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(directory.Path);
        var pipe = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId + 10000);
        await using var result = await DeviceStartupConnection.ConnectAsync(() => DeviceServiceRegistration.Running, () => pipe);
        Assert.Equal(DeviceStartupState.AccessDenied, result.State); Assert.False(pipe.Connected);
        Assert.Throws<InvalidOperationException>(() => result.TakeClient()); Assert.All(demo.Hubs, h => Assert.Equal(0, h.Writes));
    }
    [Fact] public async Task IncompatibleAuthenticatedServiceRequiresUpdateAndClosesItsSession()
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
        var name = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, CoolingServicePipeServer.Security([sid]));
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await CoolingPipeFrames.WriteAsync(server, new DeviceServiceHello(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 12, [], true), CancellationToken.None);
        });
        var pipe = DeviceServicePipeClient.ForDemo(name, Environment.ProcessId);
        await using var result = await DeviceStartupConnection.ConnectAsync(() => DeviceServiceRegistration.Running, () => pipe);
        await serving; Assert.Equal(DeviceStartupState.Incompatible, result.State); Assert.False(pipe.Connected);
        Assert.Throws<InvalidOperationException>(() => result.TakeClient());
    }
    [Fact] public async Task CancellationBeforeRegistrationDoesNotProbeOrConnect()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeviceStartupConnection.ConnectAsync(
            () => throw new InvalidOperationException("must not probe"), () => throw new InvalidOperationException("must not connect"), stop.Token));
    }
    [Fact] public async Task CancellingAnInFlightHelloClosesTheAttemptWithoutReturningAFailedOrConnectedSession()
    {
        using var identity = WindowsIdentity.GetCurrent(); var name = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, CoolingServicePipeServer.Security([identity.User!.Value]));
        using var stop = new CancellationTokenSource(); var connected = server.WaitForConnectionAsync(stop.Token);
        var pipe = DeviceServicePipeClient.ForDemo(name, Environment.ProcessId);
        var pending = DeviceStartupConnection.ConnectAsync(() => DeviceServiceRegistration.Running, () => pipe, stop.Token);
        await connected.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(pipe.Connected);
    }
}
