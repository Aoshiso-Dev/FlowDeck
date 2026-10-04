using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class HubRecoveryConnectionTests
{
    private sealed class BlockedReadLog : IEventLog
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Write(string category, object data)
        {
            if (category == "worker" && JsonSerializer.Serialize(data).Contains("read-blocked", StringComparison.Ordinal))
                Blocked.TrySetResult();
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellingAnActualWorkerReadReopensTheSameHubBeforeRelease(bool dispose)
    {
        using var dir = new ServiceTestDirectory();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FlowDeck.sln"))) root = root.Parent;
        var script = Path.Combine(dir.Path, "worker.py");
        File.Copy(Path.Combine(root!.FullName, "tests", "worker", "fake_recovery_worker.py"), script);
        var identity = new HubIdentity("recovery-fixture", "Protocol fixture", "fixture-serial", "fixture-path");
        await using var model = new DemoHub(identity) { DelayMs = 0 };
        var initial = await model.OpenAsync(identity, 1);
        var statePath = Path.Combine(dir.Path, "state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(new { identity, status = initial,
            wiring = ControlCoordinator.WiringSignature(initial), writes = Array.Empty<object>() }, Contract.Json));
        var log = new BlockedReadLog();
        await using var worker = new HubWorkerClient(Path.Combine(root.FullName, ".tools", "python313", "python.exe"), script, log, () => false, isolated: true);
        await using var driver = new HubDeviceDriver("0123456789abcdef0123456789abcdef", identity.Id, worker, dir.Path);
        await driver.InitializeAsync();
        var binding = driver.BindingCandidate;
        driver.Configure(binding, [new(new(driver.DeviceId, "fan1"), true, new(30, 100, 55, CoolingExitAction.FixedDuty, []), [])]);
        Assert.Equal(Outcome.Confirmed, (await driver.ApplyAsync(new("fan1", DeviceOperation.SetDuty, 59), default)).Outcome);
        File.WriteAllText(Path.Combine(dir.Path, "block-read"), "");
        using var cancelled = new CancellationTokenSource();
        var read = driver.RefreshAsync(cancelled.Token);
        await log.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstPid = JsonDocument.Parse(File.ReadLines(Path.Combine(dir.Path, "commands.jsonl")).First()).RootElement.GetProperty("pid").GetInt32();
        using var child = Process.GetProcessById(firstPid);
        cancelled.Cancel();
        var error = await Assert.ThrowsAsync<IOException>(() => read);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(worker.SupportsFanEndpoints);
        Assert.Single(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending);

        if (dispose) await driver.DisposeAsync();
        else
        {
            var release = Assert.Single(await driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
            Assert.Equal(Outcome.Confirmed, release.Result.Outcome);
            Assert.Equal(55, release.Result.ReportedDuty);
            Assert.Equal(binding, driver.BindingCandidate);
        }
        Assert.Empty(new HubEndpointRecoveryJournal(dir.Path, driver.DeviceId).Pending);
        using var final = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.Equal(new[] { 59, 55 }, final.RootElement.GetProperty("writes").EnumerateArray().Select(w => w.GetProperty("duty").GetInt32()));
        Assert.All(final.RootElement.GetProperty("writes").EnumerateArray(), w => Assert.Equal("fan1", w.GetProperty("channel").GetString()));
        Assert.Equal(initial.Channels.Skip(1).Select(c => c.Duty), final.RootElement.GetProperty("status").Deserialize<HubStatus>(Contract.Json)!.Channels.Skip(1).Select(c => c.Duty));
        var commands = File.ReadLines(Path.Combine(dir.Path, "commands.jsonl")).Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!).ToArray();
        var recovered = commands.Where(c => c["pid"].GetInt32() != firstPid).Select(c => c["command"].GetString()).ToArray();
        Assert.Equal(new[] { "Probe", "OpenInitialize", "ReadStatus", "SetFanEndpoint" }, recovered);
    }
}
