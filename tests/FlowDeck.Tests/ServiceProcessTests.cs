using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServiceProcessTests
{
    [Fact] public async Task SetupReadinessDoesNotAdmitGuiCommandsBeforeTheTransactionCommits()
    {
        using var dir = new ServiceTestDirectory(); var pipe = Pipe();
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        var pending = new ServiceDeploymentState(1, Guid.NewGuid().ToString("N"), "install", "configured", new(false), [user.User!.Value], new string('0', 64),
            new() { ["service.json"] = null, ["cooling-settings.json"] = null, [ServiceDeployment.InstalledManifestName] = null });
        void Save(ServiceDeploymentState state) => ServiceEnvironment.Write(Path.Combine(dir.Path, ServiceDeployment.PendingName), new ServiceDeploymentDocument(state, SettingsTransfer.Hash(state)));
        Save(pending);
        await using var host = await Child.Start("--demo-host", "--pipe", pipe, "--data-dir", dir.Path);
        var ready = ServiceEnvironment.Read<ServiceReadiness>(Path.Combine(dir.Path, "service-ready.json"));
        Assert.Equal(host.Process.Id, ready.ProcessId); Assert.Equal(pending.Id, ready.TransactionId);
        using var probe = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        using (var cancellation = new CancellationTokenSource(200)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ConnectAsync(cancellation.Token));
        Assert.False(File.Exists(Path.Combine(dir.Path, "demo-hub.json"))); // Readiness alone causes no PWM writes.
        Save(pending with { Phase = "committed" });
        await using var client = await Connect(host, pipe); var state = await State(client); Assert.False(state.Control.Managing);
        await client.DisposeAsync(); await host.Stop();
    }
    internal sealed class Child : IAsyncDisposable
    {
        public Process Process { get; }
        private readonly Task<string> errors;
        public JsonElement Ready { get; private set; }
        private Child(string[] args)
        {
            var config = typeof(ServiceProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FlowDeck.Service/bin", config, "net10.0-windows/FlowDeck.Service.exe"));
            Assert.True(File.Exists(exe), "Build FlowDeck.sln before running process integration tests.");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process = Process.Start(start)!; errors = Process.StandardError.ReadToEndAsync();
        }
        public static async Task<Child> Start(params string[] args)
        {
            var child = new Child(args);
            try
            {
                var line = await child.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (line is null) throw new IOException(await child.errors);
                child.Ready = JsonDocument.Parse(line).RootElement.Clone(); Assert.True(child.Ready.GetProperty("ready").GetBoolean()); return child;
            }
            catch { await child.DisposeAsync(); throw; }
        }
        public async Task Stop()
        {
            await Process.StandardInput.WriteLineAsync("stop"); await Process.StandardInput.FlushAsync();
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.True(Process.ExitCode == 0, await errors);
        }
        public async Task Kill()
        { if (!Process.HasExited) Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        public async ValueTask DisposeAsync() { await Kill(); Process.Dispose(); }
    }
    private static string Pipe() => "FlowDeck.Cooling.Test." + Guid.NewGuid().ToString("N");
    private static async Task<ServiceCoolingState> State(CoolingServicePipeClient client) =>
        (await client.InvokeAsync<ServiceCoolingState, object?>("state", null))!;
    private static async Task<CoolingServicePipeClient> Connect(Child host, string pipe)
    {
        for (var i = 0; ; i++)
        {
            var client = CoolingServicePipeClient.ForDemo(pipe, host.Process.Id);
            try { await client.ConnectAsync(); return client; }
            catch when (i < 20) { await client.DisposeAsync(); await Task.Delay(50); }
        }
    }
    [Fact] public async Task KillingTheGuiProcessRestoresHubAndMotherboardBeforeAnotherOwnerConnects()
    {
        using var dir = new ServiceTestDirectory(); var pipe = Pipe();
        await using var host = await Child.Start("--demo-host", "--pipe", pipe, "--data-dir", dir.Path);
        await using var gui = await Child.Start("--demo-client", "--pipe", pipe, "--server-pid", host.Process.Id.ToString());
        Assert.Equal(54, gui.Ready.GetProperty("duty").GetInt32()); await gui.Kill();
        await using var observer = await Connect(host, pipe); var state = await State(observer);
        Assert.False(state.Control.Managing); Assert.Equal(70, state.Control.ConfirmedDuty);
        Assert.False(state.Fans[MotherboardFans.Cpu.Id].Managing); Assert.Equal(65, state.Fans[MotherboardFans.Cpu.Id].Reading!.Duty);
        await observer.DisposeAsync(); await host.Stop();
    }
    [Fact] public async Task KillingAndRestartingTheServiceRecoversThePersistedHubAndRejectsTheOldEpoch()
    {
        using var dir = new ServiceTestDirectory(); var pipe = Pipe();
        await using var first = await Child.Start("--demo-host", "--pipe", pipe, "--data-dir", dir.Path);
        await using var client = await Connect(first, pipe); var before = await State(client); var epoch = client.Hello!.Epoch;
        var command = new ControlRequest(Guid.NewGuid().ToString("N"), "test", epoch, before.Control.ControlRevision, "SetManualDuty", Duty: 54);
        Assert.Equal(Outcome.Confirmed, (await client.InvokeAsync<OperationResult, ControlRequest>("control", command))!.Outcome);
        Assert.NotNull(new HubFanRecoveryJournal(dir.Path).Pending); await first.Kill();
        await using var second = await Child.Start("--demo-host", "--pipe", pipe, "--data-dir", dir.Path);
        await using var restored = await Connect(second, pipe); Assert.NotEqual(epoch, restored.Hello!.Epoch);
        var after = await State(restored); Assert.False(after.Control.Managing); Assert.All(after.Hub.Channels, c => Assert.Equal(70, c.Duty));
        Assert.Null(new HubFanRecoveryJournal(dir.Path).Pending);
        Assert.Equal(Outcome.Rejected, (await restored.InvokeAsync<OperationResult, ControlRequest>("control", command))!.Outcome);
        await restored.DisposeAsync(); await second.Stop();
    }
    [Fact] public async Task NormalServiceStopRestoresOutputsAndPowerResumeRequiresANewSession()
    {
        using var dir = new ServiceTestDirectory(); var pipe = Pipe();
        await using var host = await Child.Start("--demo-host", "--pipe", pipe, "--data-dir", dir.Path);
        await using var client = await Connect(host, pipe); var state = await State(client); var session = client.Hello!.Session;
        await client.InvokeAsync<OperationResult, ControlRequest>("control", new(Guid.NewGuid().ToString("N"), "test", client.Hello.Epoch, state.Control.ControlRevision, "SetManualDuty", Duty: 56));
        await host.Process.StandardInput.WriteLineAsync("suspend"); await host.Process.StandardInput.FlushAsync();
        var suspended = JsonDocument.Parse((await host.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal("suspend", suspended.RootElement.GetProperty("power").GetString());
        Assert.All(ServiceEnvironment.Read<Dictionary<string, int>>(Path.Combine(dir.Path, "demo-hub.json")).Values, duty => Assert.Equal(70, duty));
        await host.Process.StandardInput.WriteLineAsync("resume"); await host.Process.StandardInput.FlushAsync();
        _ = await host.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var resumed = await Connect(host, pipe); Assert.NotEqual(session, resumed.Hello!.Session); Assert.False((await State(resumed)).Control.Managing);
        var current = await State(resumed);
        Assert.NotNull(current.SleepResumeToken);
        current = (await resumed.InvokeAsync<ServiceCoolingState, ServiceSleepResume>("restore-sleep", new(current.SleepResumeToken!, current.SettingsRevision)))!;
        Assert.True(current.Control.Managing); Assert.Equal(56, current.Control.ConfirmedDuty); Assert.Null(current.SleepResumeToken);
        await resumed.InvokeAsync<OperationResult, ControlRequest>("control", new(Guid.NewGuid().ToString("N"), "test", resumed.Hello.Epoch, current.Control.ControlRevision, "SetManualDuty", Duty: 58));
        await host.Stop();
        Assert.Null(new HubFanRecoveryJournal(dir.Path).Pending);
        Assert.All(ServiceEnvironment.Read<Dictionary<string, int>>(Path.Combine(dir.Path, "demo-hub.json")).Values, duty => Assert.Equal(70, duty));
    }
    [Fact] public async Task ClosingTheServiceJobTerminatesItsOwnHelperProcess()
    {
        using var dir = new ServiceTestDirectory();
        await using var child = await Child.Start("--demo-host", "--pipe", Pipe(), "--data-dir", dir.Path);
        using (var job = new OwnedProcessJob(child.Process)) Assert.False(child.Process.HasExited);
        await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(child.Process.HasExited);
    }
}
