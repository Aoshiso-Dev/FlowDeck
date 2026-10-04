using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public enum ServiceSetupState { Idle, Reviewing, Ready, Blocked, Applying, Succeeded, Cancelled, Failed, Unconfirmed }
public sealed record ServiceSetupExecution(bool Cancelled, int? ExitCode);

/// <summary>The UI reviews an immutable plan before any explicit, user-initiated elevation.</summary>
public sealed class ServiceSetupSession
{
    private readonly Func<CancellationToken, Task<ServiceDeploymentPlan>> inspect;
    private readonly Func<ServiceDeploymentPlan, Task<ServiceSetupExecution>> execute;
    private readonly Func<ServiceDeploymentPlan, Task<bool>> confirm;
    private readonly Func<Task<bool>>? prepare;
    private string? reviewed;
    public ServiceSetupState State { get; private set; }
    public string Status { get; private set; } = "導入内容を確認してください";
    public bool Busy => State is ServiceSetupState.Reviewing or ServiceSetupState.Applying;
    public bool Prepared { get; private set; }
    // Callers cannot change the reviewed manifest, user list, or blockers through the displayed plan.
    public ServiceDeploymentPlan? Plan => reviewed is null ? null : JsonSerializer.Deserialize<ServiceDeploymentPlan>(reviewed, Contract.Json);

    public ServiceSetupSession(Func<CancellationToken, Task<ServiceDeploymentPlan>> inspect,
        Func<ServiceDeploymentPlan, Task<ServiceSetupExecution>> execute, Func<ServiceDeploymentPlan, Task<bool>> confirm,
        Func<Task<bool>>? prepare = null)
    { this.inspect = inspect; this.execute = execute; this.confirm = confirm; this.prepare = prepare; }

    public async Task ReviewAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        State = ServiceSetupState.Reviewing; Status = "配布物と導入先を確認しています…"; reviewed = null;
        try
        {
            var plan = await inspect(ct); ct.ThrowIfCancellationRequested(); ValidateScope(plan);
            reviewed = Serialize(plan);
            State = plan.ReadyForAdministratorReview ? ServiceSetupState.Ready : ServiceSetupState.Blocked;
            Status = plan.ReadyForAdministratorReview ? "導入内容を確認しました" : "導入前に確認が必要です";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { State = ServiceSetupState.Cancelled; Status = "確認を取り消しました"; }
        catch (FileNotFoundException)
        { State = ServiceSetupState.Failed; Status = "必要な配布ファイルがありません。サービス同梱版を確認してください"; }
        catch (DirectoryNotFoundException)
        { State = ServiceSetupState.Failed; Status = "サービス配布物がありません。サービス同梱版を確認してください"; }
        catch (UnauthorizedAccessException)
        { State = ServiceSetupState.Blocked; Status = "導入状態を読み取れません。管理者による事前確認が必要です"; }
        catch (Exception)
        { State = ServiceSetupState.Failed; Status = "配布物または導入状態を確認できませんでした"; }
    }

    public async Task ApplyAsync()
    {
        if (State != ServiceSetupState.Ready || reviewed is null) return;
        var expected = reviewed; var plan = Plan!;
        State = ServiceSetupState.Applying; Status = "導入を実行しています…";
        try
        {
            // Rehash the payload and reread service configuration immediately before elevation.
            var latest = await inspect(CancellationToken.None); ValidateScope(latest);
            if (!latest.ReadyForAdministratorReview || Serialize(latest) != expected)
            { State = ServiceSetupState.Blocked; Status = "配布物または導入状態が変わりました。再確認してください"; return; }
            if (prepare is not null && !Prepared)
            {
                Status = "制御と接続を終了しています…";
                if (!await prepare())
                { State = ServiceSetupState.Blocked; Status = "制御と接続の終了を確認できません。再確認して終了処理を再試行してください"; return; }
                Prepared = true;
                latest = await inspect(CancellationToken.None); ValidateScope(latest);
                if (!latest.ReadyForAdministratorReview || Serialize(latest) != expected)
                { State = ServiceSetupState.Blocked; Status = "接続の終了中に導入内容が変わりました。再確認してください"; return; }
            }
            var result = await execute(plan);
            if (result.Cancelled)
            { State = ServiceSetupState.Cancelled; Status = "管理者確認を取り消しました。導入は開始していません"; return; }
            if (result.ExitCode != 0)
            { State = ServiceSetupState.Failed; Status = "導入を完了できませんでした。復旧状態を再確認してください"; return; }
            // Exit code alone cannot prove that the intended generation is installed and running.
            Status = "導入結果を確認しています…";
            var verified = false;
            try { verified = await confirm(Plan!); }
            catch (Exception) { /* The process exited; verification failure is not an installation verdict. */ }
            if (!verified)
            {
                State = ServiceSetupState.Unconfirmed;
                Status = "処理は終了しましたが、導入結果を確認できません。再確認してください"; return;
            }
            State = ServiceSetupState.Succeeded; Status = "サービスを導入しました";
        }
        catch (Exception)
        {
            State = ServiceSetupState.Failed; Status = "導入を完了できませんでした。配布物と復旧状態を再確認してください";
        }
    }

    private static string Serialize(ServiceDeploymentPlan plan) => JsonSerializer.Serialize(plan, Contract.Json);
    private static void ValidateScope(ServiceDeploymentPlan plan)
    {
        if (plan.Action is not ("install" or "update") || plan.Runtime != "devices-v2" || plan.SeedHash is not null ||
            plan.SeedHubId is not null || plan.SeedProfileCount is not null || plan.EmptySettings ||
            plan.ServiceName != CoolingServiceProtocol.ServiceName || !plan.RequiresAdministrator ||
            plan.InstallationDirectory != ServiceEnvironment.InstallationDirectory || plan.DataDirectory != ServiceEnvironment.DataDirectory ||
            plan.ManifestHash is not { Length: 64 } hash || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("汎用版の導入確認と一致しません");
    }

    public static ServiceSetupSession ForApplication(string applicationDirectory, Func<Task<bool>>? prepare = null)
    {
        var root = Path.Combine(Path.GetFullPath(applicationDirectory), "service-setup");
        return new(ct => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var plan = ServiceSetupInspection.Create(null, root, [], runtime: "devices-v2");
            if (prepare is not null) plan = plan with { Steps = ["現在の制御とM5GO接続を終了。未保存の画面編集は引き継がない", .. plan.Steps] };
            ct.ThrowIfCancellationRequested(); return plan;
        }, ct), plan => RunElevatedAsync(root, plan), plan => Task.Run(() => ConfirmInstalled(plan)), prepare);
    }

    internal static ProcessStartInfo StartInfo(string root, ServiceDeploymentPlan plan)
    {
        ValidateScope(plan);
        if (!plan.ReadyForAdministratorReview) throw new IOException("導入前の確認が未完了です");
        var package = ServicePackage.Open(root, plan.ManifestHash);
        if (!package.SupportsDevices) throw new IOException("汎用サービスの配布物ではありません");
        var info = new ProcessStartInfo(Path.Combine(package.PayloadDirectory, "FlowDeck.Service.exe"))
        { UseShellExecute = true, Verb = "runas", WorkingDirectory = package.PayloadDirectory, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--setup-apply", plan.Action, "--package", package.Root,
            "--manifest-sha256", plan.ManifestHash!, "--runtime", "devices-v2" }) info.ArgumentList.Add(arg);
        foreach (var user in plan.AllowedUsers) { info.ArgumentList.Add("--user"); info.ArgumentList.Add(user); }
        return info;
    }

    private static async Task<ServiceSetupExecution> RunElevatedAsync(string root, ServiceDeploymentPlan plan)
    {
        var info = await Task.Run(() => StartInfo(root, plan));
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return new(true, null); }
        using (process)
        {
            if (process is null) throw new IOException("導入プロセスを確認できません");
            // A deployment transaction must finish or recover; closing the GUI must not kill it.
            await process.WaitForExitAsync(); return new(false, process.ExitCode);
        }
    }

    private static bool ConfirmInstalled(ServiceDeploymentPlan plan)
    {
        if (!WindowsCoolingServiceIdentity.IsInstalled() || WindowsCoolingServiceIdentity.ProcessId() <= 0) return false;
        var installed = ServicePackage.Read(Path.Combine(ServiceEnvironment.DataDirectory, ServiceDeployment.InstalledManifestName),
            ServiceEnvironment.InstallationDirectory, plan.ManifestHash);
        var settings = ServiceEnvironment.Read<ServiceInstallation>(Path.Combine(ServiceEnvironment.DataDirectory, "service.json"));
        return installed.SupportsDevices && ServiceRuntimeSelection.Read(ServiceEnvironment.DataDirectory) is not null &&
            settings.Version == 1 && settings.AllowedUsers.Order().SequenceEqual(plan.AllowedUsers.Order()) &&
            ServiceDeployment.ReadPending(ServiceEnvironment.DataDirectory) is null;
    }
}
