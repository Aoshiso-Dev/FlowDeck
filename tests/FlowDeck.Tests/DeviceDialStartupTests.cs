using FlowDeck.App;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceDialStartupTests
{
    private static readonly DeviceDialSettings Saved = new("COM6", "dial-a8a16e8dbd44");

    [Fact]
    public async Task StartsOnlyTheSavedRegisteredDialOnceWithoutRewritingItsSettings()
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "dial-v1.json");
        File.WriteAllText(path, "{ \"port\": \"COM28\", \"deviceId\": \"saved-dial\" }");
        var original = File.ReadAllBytes(path);
        var settings = new DeviceDialSettingsStore(path).Load();
        var startup = new DeviceDialStartup();
        var calls = new List<DeviceDialSettings>();
        Task Connect(DeviceDialSettings target, Func<bool> current)
        {
            Assert.True(current()); calls.Add(target); return Task.CompletedTask;
        }
        await startup.StartAsync(settings, false, () => true, Connect);
        await startup.StartAsync(new("COM6", "other-dial"), false, () => true, Connect);
        Assert.Equal(new DeviceDialSettings("COM28", "saved-dial"), Assert.Single(calls));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("COM6", null)]
    [InlineData("COM6", "")]
    [InlineData("COM6", "invalid\nidentity")]
    [InlineData("COM0", "dial")]
    [InlineData("COM01", "dial")]
    [InlineData("com6", "dial")]
    [InlineData("COM6/other", "dial")]
    public async Task InvalidOrUnregisteredSettingsDoNotOpenAnyPort(string port, string? device)
    {
        var calls = 0;
        await new DeviceDialStartup().StartAsync(new(port, device), false, () => true,
            (_, _) => { calls++; return Task.CompletedTask; });
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptFileDoesNotFallBackToOpeningDefaultCom6(bool corrupt)
    {
        using var directory = new ServiceTestDirectory();
        var path = Path.Combine(directory.Path, "dial-v1.json");
        if (corrupt) File.WriteAllText(path, "{broken");
        var store = new DeviceDialSettingsStore(path);
        var settings = store.Load(); var calls = 0;
        await new DeviceDialStartup().StartAsync(settings, false, () => store.Warning is null,
            (_, _) => { calls++; return Task.CompletedTask; });
        Assert.Equal(0, calls);
        Assert.Equal(corrupt, File.Exists(path));
        if (corrupt) Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task DemoDoesNotEvenEvaluateThePhysicalConnectionGate()
    {
        await new DeviceDialStartup().StartAsync(Saved, true,
            () => throw new InvalidOperationException("Physical connection gate was reached"),
            (_, _) => throw new InvalidOperationException("Physical connection was attempted"));
    }

    [Fact]
    public async Task ClosingUnavailableOrAlreadyConnectedWorkspaceIsSkippedWithoutLaterRetry()
    {
        var startup = new DeviceDialStartup(); var allowed = false; var calls = 0;
        Task Connect(DeviceDialSettings _, Func<bool> __) { calls++; return Task.CompletedTask; }
        await startup.StartAsync(Saved, false, () => allowed, Connect);
        allowed = true;
        await startup.StartAsync(Saved, false, () => allowed, Connect);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ManualConnectDisconnectOrPortEditBeforeStartupTakesPrecedence()
    {
        var startup = new DeviceDialStartup(); var calls = 0;
        startup.Suppress();
        await startup.StartAsync(Saved, false, () => true,
            (_, _) => { calls++; return Task.CompletedTask; });
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingStartupCannotReconnectAfterManualActionOrWorkspaceExit(bool manual)
    {
        var startup = new DeviceDialStartup(); var allowed = true; var opened = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = startup.StartAsync(Saved, false, () => allowed, async (_, current) =>
        {
            entered.SetResult(); await resume.Task;
            if (current()) opened++;
        });
        await entered.Task;
        if (manual) startup.Suppress(); else allowed = false;
        resume.SetResult(); await operation;
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task FailedPortOpenIsReportedOnceAndNeverRetriedAutomatically()
    {
        var startup = new DeviceDialStartup(); var calls = 0;
        Task Connect(DeviceDialSettings _, Func<bool> __)
        { calls++; return Task.FromException(new IOException("Port unavailable")); }
        var error = await Assert.ThrowsAsync<IOException>(() => startup.StartAsync(Saved, false, () => true, Connect));
        Assert.Equal("Port unavailable", error.Message);
        await startup.StartAsync(Saved, false, () => true, Connect);
        Assert.Equal(1, calls);
    }
}
