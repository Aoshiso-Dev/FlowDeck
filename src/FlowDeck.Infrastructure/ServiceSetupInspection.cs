using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Read-only preflight shared by the CLI and the first-run UI. Never runs a package executable.</summary>
public static class ServiceSetupInspection
{
    public static ServiceDeploymentPlan Create(string? action, string? packagePath, IEnumerable<string> requestedUsers,
        string? seedPath = null, bool emptySettings = false, string? runtime = null)
    {
        var users = requestedUsers.ToList();
        var package = packagePath is null ? null : ServicePackage.Open(packagePath);
        if (emptySettings && seedPath is not null) throw new ArgumentException("設定移行と空の設定を同時に指定できません");
        var seed = seedPath is null ? ((string Sha256, AppSettings Settings)?)null : ServiceSeed.Read(seedPath);
        var errors = new List<string>();
        ServiceRuntimeSelection? currentRuntime = null;
        ServiceOptionalOpenRgb? installedOpenRgb = null;
        var openRgbEnabled = false;
        try { currentRuntime = ServiceRuntimeSelection.Read(ServiceEnvironment.DataDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { errors.Add(e.Message); }
        if (currentRuntime is not null)
            try { openRgbEnabled = currentRuntime.ReadConfiguration(ServiceEnvironment.DataDirectory).Preferences.Providers?.OpenRgbEnabled == true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { errors.Add(e.Message); }
        if (runtime == "devices-v2" && seed is not null) errors.Add("汎用サービスは新しい監視用設定から開始します。旧設定の移行は対象外です");
        var registration = new ServiceRegistration(false);
        var isInstalled = WindowsCoolingServiceIdentity.IsInstalled();
        action ??= isInstalled ? "update" : "install";
        if (isInstalled)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + CoolingServiceProtocol.ServiceName)
                ?? throw new IOException("サービス登録を読み取れません");
            using var service = new ServiceController(CoolingServiceProtocol.ServiceName);
            var executable = Path.Combine(ServiceEnvironment.InstallationDirectory, "FlowDeck.Service.exe");
            registration = new(true, service.Status == ServiceControllerStatus.Running, key.GetValue("ImagePath") as string, key.GetValue("ObjectName") as string,
                File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion : null, service.ServiceType == ServiceType.Win32OwnProcess);
            try
            {
                installedOpenRgb = ServicePackage.Read(Path.Combine(ServiceEnvironment.DataDirectory, ServiceDeployment.InstalledManifestName),
                    ServiceEnvironment.InstallationDirectory).Manifest.OpenRgb;
                var installed = ServiceEnvironment.Read<ServiceInstallation>(Path.Combine(ServiceEnvironment.DataDirectory, "service.json"));
                if (users.Count == 0) users.AddRange(installed.AllowedUsers);
                else if (action != "install" && !users.Order().SequenceEqual(installed.AllowedUsers.Order())) errors.Add("更新・削除では導入済みの操作ユーザーを維持してください");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { errors.Add("導入済みのユーザー設定を確認できません。管理者による事前確認が必要です: " + e.Message); }
        }
        if (users.Count == 0) { using var user = WindowsIdentity.GetCurrent(); users.Add(user.User!.Value); }
        if (seed is not null && (action != "install" || File.Exists(Path.Combine(ServiceEnvironment.DataDirectory, "cooling-settings.json"))))
            errors.Add("冷却設定の移行は設定のない初回導入だけに使用できます");
        if (action == "install" && seed is null && !emptySettings && runtime != "devices-v2" && !File.Exists(Path.Combine(ServiceEnvironment.DataDirectory, "cooling-settings.json")))
            errors.Add("初回導入では移行する設定、または空の設定で開始する指定が必要です");
        try { if (ServiceDeployment.ReadPending(ServiceEnvironment.DataDirectory) is { } pending) errors.Add("前回の更新記録が残っています。先に復旧してください: " + pending.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(e.Message); }
        bool busy = false;
        foreach (var name in new[] { @"Global\NzxtControlHub-1E71-2022", @"Global\FlowDeck-1E71-2022" })
            try { if (Mutex.TryOpenExisting(name, out var mutex)) { busy = true; mutex.Dispose(); } }
            catch (UnauthorizedAccessException) { busy = true; }
        foreach (var path in new[] { ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory })
            if (Directory.Exists(path))
                try { ServiceEnvironment.ValidateTree(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(e.Message); }
        var plan = ServiceDeploymentPlanning.Create(action, package, registration, users.ToArray(), busy, errors.ToArray(), runtime, currentRuntime,
            installedOpenRgb, openRgbEnabled) with
        { SeedHash = seed?.Sha256, SeedHubId = seed?.Settings.Hardware.Hub?.Id, SeedProfileCount = seed?.Settings.Profiles.Length, EmptySettings = emptySettings };
        return plan;
    }
}
