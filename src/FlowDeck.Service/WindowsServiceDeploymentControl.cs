using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

internal sealed class WindowsServiceDeploymentControl : IServiceDeploymentControl
{
    private const string Name = CoolingServiceProtocol.ServiceName;
    public ServiceRegistration Inspect()
    {
        var value = ReadRegistration();
        return value.Installed ? value with { Settings = WindowsServiceRegistration.Capture() } : value;
    }
    internal static ServiceRegistration ReadRegistration()
    {
        if (!WindowsCoolingServiceIdentity.IsInstalled()) return new(false);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name) ?? throw new IOException("サービス登録を読み取れません");
        using var service = new ServiceController(Name);
        var executable = Path.Combine(ServiceEnvironment.InstallationDirectory, "FlowDeck.Service.exe");
        return new(true, service.Status == ServiceControllerStatus.Running, key.GetValue("ImagePath") as string, key.GetValue("ObjectName") as string,
            File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion : null, service.ServiceType == ServiceType.Win32OwnProcess);
    }
    private static async Task ScAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.SystemDirectory };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("SCM設定コマンドを開始できません");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (System.TimeoutException) { if (!process.HasExited) process.Kill(); throw new IOException("SCM設定コマンドがタイムアウトしました"); }
        var detail = (await output) + (await error);
        if (process.ExitCode != 0) throw new IOException("SCM設定に失敗しました (" + args[0] + ", " + process.ExitCode + "): " + detail[..Math.Min(detail.Length, 2048)]);
    }
    private static async Task WaitAsync(Func<bool> predicate, string message)
    {
        var until = Environment.TickCount64 + 125000;
        while (!predicate()) { if (Environment.TickCount64 >= until) throw new IOException(message); await Task.Delay(250); }
    }
    public async Task StopAsync()
    {
        if (!WindowsCoolingServiceIdentity.IsInstalled()) return;
        // Disable automatic starts and recovery before waiting for a graceful stop. Never kill the service.
        await ScAsync("config", Name, "start=", "disabled");
        await ScAsync("failure", Name, "reset=", "86400", "actions=", "");
        await ScAsync("failureflag", Name, "0");
        using var service = new ServiceController(Name);
        await WaitAsync(() => { service.Refresh(); return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending; }, "サービスの起動完了を待てません");
        var pid = WindowsCoolingServiceIdentity.ProcessId();
        Process? process = null;
        if (pid > 0) try { process = Process.GetProcessById(pid); } catch (ArgumentException) { }
        try
        {
            if (service.Status == ServiceControllerStatus.Running) service.Stop();
            await WaitAsync(() => { service.Refresh(); return service.Status == ServiceControllerStatus.Stopped; }, "サービスの通常停止が未確認です");
            if (process is not null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { process?.Dispose(); }
    }
    public async Task ConfigureAsync(string[] users, ServiceRegistration? restore = null)
    {
        var verb = WindowsCoolingServiceIdentity.IsInstalled() ? "config" : "create";
        if (verb == "create")
        {
            await ScAsync(verb, Name, "type=", "own", "start=", "demand", "error=", "normal", "obj=", "LocalSystem", "binpath=",
                '"' + Path.Combine(ServiceEnvironment.InstallationDirectory, "FlowDeck.Service.exe") + '"', "displayname=", "FlowDeck Cooling");
            await ScAsync("description", Name, "FlowDeck sensor monitoring and fan control");
        }
        else await ScAsync("config", Name, "start=", "demand");
        if (restore?.Settings is { DisplayName: not null } saved)
        {
            var error = saved.ErrorControl switch { 0 => "ignore", 1 => "normal", 2 => "severe", 3 => "critical", _ => throw new IOException("サービス障害の重要度が不正です") };
            await ScAsync("config", Name, "displayname=", saved.DisplayName, "error=", error, "depend=", string.Join('/', saved.Dependencies ?? []));
            await ScAsync("description", Name, saved.Description ?? "");
        }
        await ScAsync("sdset", Name, "D:(A;;GA;;;SY)(A;;GA;;;BA)" + string.Concat(users.Select(sid => "(A;;LC;;;" + sid + ")")));
        await ScAsync("failure", Name, "reset=", "86400", "actions=", "");
        await ScAsync("failureflag", Name, "0");
    }
    public async Task StartAsync(string transactionId)
    {
        var started = DateTimeOffset.UtcNow;
        using var service = new ServiceController(Name); service.Start();
        await WaitAsync(() => { service.Refresh(); return service.Status == ServiceControllerStatus.Running; }, "サービスの起動を確認できません");
        var ready = ServiceEnvironment.Read<ServiceReadiness>(Path.Combine(ServiceEnvironment.DataDirectory, "service-ready.json"));
        if (ready.Version != 1 || ready.ProcessId <= 0 || ready.ProcessId != WindowsCoolingServiceIdentity.ProcessId() || ready.TransactionId != transactionId ||
            !CoolingServiceProtocol.Id(ready.Epoch) || ready.StartedAt < started || ready.StartedAt > DateTimeOffset.UtcNow)
            throw new IOException("現在のサービス起動と更新トランザクションの一致を確認できません");
        await Task.Delay(750);
        if (WindowsCoolingServiceIdentity.ProcessId() != ready.ProcessId) throw new IOException("起動確認中にサービスが終了または再起動しました");
    }
    public async Task ActivateAsync(ServiceRegistration? restore = null)
    {
        if (restore?.Settings is { } saved)
        {
            var start = saved.StartType switch { 2 => saved.DelayedAutoStart ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => throw new IOException("以前のサービス起動方式が不正です") };
            await ScAsync("config", Name, "start=", start); WindowsServiceRegistration.Restore(saved); return;
        }
        await ScAsync("config", Name, "start=", "delayed-auto");
        await ScAsync("failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        await ScAsync("failureflag", Name, "1");
    }
    public async Task RemoveAsync()
    {
        if (!WindowsCoolingServiceIdentity.IsInstalled()) return;
        using (var service = new ServiceController(Name)) if (service.Status != ServiceControllerStatus.Stopped) throw new IOException("稼働中のサービスは削除できません");
        await ScAsync("delete", Name);
        await WaitAsync(() => !WindowsCoolingServiceIdentity.IsInstalled(), "サービスの削除完了が未確認です");
    }
    public async Task<IAsyncDisposable> AcquireHardwareAsync() => await ServiceHardwareLease.AcquireAsync();
}
