using System.ServiceProcess;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--devices-smoke"]) return await DeviceServiceSmoke.RunAsync();
            if (args is ["--devices-hub-smoke"]) return await DeviceServiceSmoke.RunHubAdaptersAsync();
            if (args.FirstOrDefault() == "--devices-demo-host") return await DeviceDemoProcess.RunHostAsync(args);
            if (args.FirstOrDefault() == "--devices-demo-client") return await DeviceDemoProcess.RunClientAsync(args);
            if (args.FirstOrDefault() == "--setup-plan") return ServiceSetupPlanning.Run(args);
            if (args.FirstOrDefault() is "--setup-apply" or "--setup-recover") return await ServiceSetupApply.RunAsync(args);
            if (args is ["--package-openrgb", var sourcePackage, var reviewedHash, var openRgbArchive, var destination])
            {
                var package = ServicePackagePreparation.WithOpenRgb(sourcePackage, reviewedHash, openRgbArchive, destination);
                Console.WriteLine(JsonSerializer.Serialize(new { package.Root, package.Manifest.Release, package.ManifestHash,
                    files = package.Manifest.Files.Count, package.Manifest.OpenRgb }, Contract.Json));
                return 0;
            }
            if (args.Length == 3 && args[0] == "--package-manifest")
            {
                var root = Path.GetFullPath(args[1]);
                ServiceEnvironment.Write(Path.Combine(root, ServicePackage.ManifestName), ServicePackage.CreateManifest(Path.Combine(root, "payload"), args[2], ["legacy-v1", "devices-v2"]));
                var package = ServicePackage.Open(root);
                Console.WriteLine(JsonSerializer.Serialize(new { package.Manifest.Release, package.ManifestHash, files = package.Manifest.Files.Count }, Contract.Json));
                return 0;
            }
            if (args.Length == 0)
            {
                // No interactive elevation or implicit console hardware mode.
                if (Environment.UserInteractive) throw new IOException("WindowsサービスとしてSCMから起動してください。導入は管理者用手順で行います");
                ServiceBase.Run(new WindowsCoolingService()); return 0;
            }
            if (args.Length == 5 && args[0] == "--demo-host" && args[1] == "--pipe" && args[3] == "--data-dir")
            {
                if (!Path.IsPathFullyQualified(args[4])) throw new ArgumentException("デモには専用データフォルダーの絶対パスが必要です");
                await using var host = await CoolingServiceProcess.StartDemoAsync(args[4], args[2]);
                Console.WriteLine(JsonSerializer.Serialize(new { ready = true, processId = Environment.ProcessId, epoch = host.Runtime.Epoch, demo = true }, Contract.Json));
                while (true)
                {
                    var input = Console.In.ReadLineAsync();
                    if (await Task.WhenAny(input, host.Completion) == host.Completion)
                    { await host.Completion; throw new IOException("デモサービスの通信が終了しました"); }
                    var command = await input;
                    if (command is null or "stop") break;
                    if (command is "suspend" or "resume")
                    {
                        await host.PowerAsync(command == "resume");
                        Console.WriteLine(JsonSerializer.Serialize(new { power = command, state = host.Runtime.State() }, Contract.Json));
                    }
                    else throw new ArgumentException("未対応のデモ操作です");
                }
                await host.DisposeAsync(); Console.WriteLine("{\"stopped\":true}"); return 0;
            }
            if (args.Length == 5 && args[0] == "--demo-client" && args[1] == "--pipe" && args[3] == "--server-pid" && int.TryParse(args[4], out var pid))
            {
                await using var client = CoolingServicePipeClient.ForDemo(args[2], pid); await client.ConnectAsync();
                var state = await client.InvokeAsync<ServiceCoolingState, object?>("state", null) ?? throw new IOException("状態を取得できません");
                var result = await client.InvokeAsync<OperationResult, ControlRequest>("control", new(Guid.NewGuid().ToString("N"), "process-test",
                    client.Hello!.Epoch, state.Control.ControlRevision, "SetManualDuty", Duty: 54));
                if (result?.Outcome != Outcome.Confirmed) throw new IOException("模擬Hubの操作が未確認です");
                state = await client.InvokeAsync<ServiceCoolingState, object?>("state", null) ?? throw new IOException("状態を取得できません");
                await client.InvokeAsync<CpuFanState, ServiceBoardCommand>("board-manual", new(MotherboardFans.Cpu.Id, state.SettingsRevision, 42));
                Console.WriteLine(JsonSerializer.Serialize(new { ready = true, processId = Environment.ProcessId, duty = 54 }, Contract.Json));
                await Console.In.ReadLineAsync(); return 0;
            }
            throw new ArgumentException("サービス引数が不正です");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
    }
}

internal sealed class WindowsCoolingService : ServiceBase
{
    private IWindowsServiceProcess? host;
    private DeviceStartupException? pendingStartup;
    private volatile bool stopping;
    public WindowsCoolingService()
    { ServiceName = CoolingServiceProtocol.ServiceName; CanStop = true; CanShutdown = true; CanHandlePowerEvent = true; AutoLog = false; }
    private void WaitWithHints(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            RequestAdditionalTime(TimeSpan.FromSeconds(15));
            if (Environment.TickCount64 - started > 120_000)
            {
                new RollingEventLog(Path.Combine(ServiceEnvironment.DataDirectory, "logs")).Write("serviceTimeout", new { stopping });
                // Durable recovery records survive this termination; SCM recovery must restart a failed process.
                Environment.Exit(1);
            }
            _ = Task.WhenAny(task, Task.Delay(5000)).GetAwaiter().GetResult();
        }
        task.GetAwaiter().GetResult();
    }
    protected override void OnStart(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("サービスの追加起動引数は受け付けません");
        // A repeated SCM start in the same process cannot bypass an unfinished previous start.
        if (pendingStartup is { } pending)
        {
            WaitWithHints(pending.DisposeAsync().AsTask());
            if (!pending.ResourcesClosed) throw new IOException("前回の起動処理の終了が未確認です");
            pendingStartup = null;
        }
        try
        {
            var start = StartConfiguredAsync(); WaitWithHints(start); host = start.GetAwaiter().GetResult();
        }
        catch (DeviceStartupException e) { pendingStartup = e; throw; }
        _ = ObserveServerAsync(host);
    }
    private static async Task<IWindowsServiceProcess> StartConfiguredAsync()
    {
        _ = ServiceEnvironment.ValidateInstallation(AppContext.BaseDirectory);
        return ServiceRuntimeSelection.Read(ServiceEnvironment.DataDirectory) is { } selection
            ? await DeviceServiceProcess.StartRealAsync(selection)
            : await CoolingServiceProcess.StartRealAsync();
    }
    private async Task ObserveServerAsync(IWindowsServiceProcess process)
    {
        try { await process.Completion; }
        catch (Exception e) { new RollingEventLog(Path.Combine(ServiceEnvironment.DataDirectory, "logs")).Write("serviceFatal", new { error = e.ToString() }); }
        if (!stopping)
        {
            try { await process.DisposeAsync(); } catch { /* Recovery journals retain unconfirmed obligations. */ }
            Environment.Exit(1);
        }
    }
    protected override void OnStop()
    {
        stopping = true;
        try { WaitWithHints(CloseProcessAsync()); }
        catch (Exception e)
        {
            ExitCode = 1;
            new RollingEventLog(Path.Combine(ServiceEnvironment.DataDirectory, "logs")).Write("serviceStopFailure", new { error = e.ToString() });
        }
    }
    protected override void OnShutdown()
    {
        stopping = true;
        // RequestAdditionalTime is not available for shutdown/power notifications.
        if (host is not null || pendingStartup is not null)
            try { CloseProcessAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); }
            catch (Exception e) { new RollingEventLog(Path.Combine(ServiceEnvironment.DataDirectory, "logs")).Write("serviceShutdown", new { error = e.ToString() }); ExitCode = 1; }
    }
    private async Task CloseProcessAsync()
    {
        if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
        if (pendingStartup is { } pending)
        {
            try { await pending.DisposeAsync().ConfigureAwait(false); }
            finally { if (pending.ResourcesClosed) pendingStartup = null; }
        }
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (!stopping && host is { } current && powerStatus is PowerBroadcastStatus.Suspend or PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
            _ = ObservePowerAsync(current, powerStatus != PowerBroadcastStatus.Suspend);
        return true;
    }
    private static async Task ObservePowerAsync(IWindowsServiceProcess process, bool resume)
    { try { await process.PowerAsync(resume); } catch { /* PowerAsync records the failure; control remains suspended. */ } }
}
