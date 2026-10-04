using System.Security.Principal;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record ServiceRecoveryAction(int Type, uint Delay);
public sealed record ServiceRegistrationSettings(int StartType, bool DelayedAutoStart, uint ResetPeriod, ServiceRecoveryAction[] Actions,
    string RebootMessage, string Command, bool FailureOnNonCrash, string SecurityDescriptor,
    string? DisplayName = null, string? Description = null, int ErrorControl = 1, string[]? Dependencies = null);
public record ServiceRegistration(bool Installed, bool Running = false, string? BinaryPath = null, string? Account = null, string? Release = null,
    bool OwnProcess = true, ServiceRegistrationSettings? Settings = null);
public record ServiceDeploymentPlan(string Action, string ServiceName, string InstallationDirectory, string DataDirectory,
    string? Release, string? ManifestHash, int VerifiedFiles, long VerifiedBytes, string[] AllowedUsers, bool RequiresAdministrator, string[] Blockers, string[] Steps,
    string? SeedHash = null, string? SeedHubId = null, int? SeedProfileCount = null, bool EmptySettings = false, string? Runtime = null,
    ServiceOptionalOpenRgb? OpenRgb = null, ServiceOptionalOpenRgb? PreviousOpenRgb = null)
{
    public bool ReadyForAdministratorReview => Blockers.Length == 0;
}

public static class ServiceDeploymentPlanning
{
    public static ServiceDeploymentPlan Create(string action, ServicePackage? package, ServiceRegistration service, string[] allowedUsers,
        bool hardwareBusy, string[]? inspectionErrors = null, string? runtime = null, ServiceRuntimeSelection? currentRuntime = null,
        ServiceOptionalOpenRgb? installedOpenRgb = null, bool openRgbEnabled = false)
    {
        if (action is not ("install" or "update" or "remove" or "rollback")) throw new ArgumentException("未対応のサービス導入操作です");
        if (action != "remove" && package is null) throw new ArgumentException("検証するサービス配布物が必要です");
        ServiceDeployment.ValidateRuntimeChoice(action, runtime);
        if (allowedUsers.Length is < 1 or > 8 || allowedUsers.Distinct(StringComparer.Ordinal).Count() != allowedUsers.Length ||
            allowedUsers.Any(sid => !new SecurityIdentifier(sid).IsAccountSid())) throw new ArgumentException("操作ユーザーSIDが不正です");
        package?.VerifyPayload();
        var errors = new List<string>(inspectionErrors ?? []);
        if (ServiceDeployment.OpenRgbRequirementError(action, runtime, package, openRgbEnabled) is { } optionalError) errors.Add(optionalError);
        if (action != "remove" && runtime != "legacy-v1" && (runtime == "devices-v2" || currentRuntime is not null) && package?.SupportsDevices != true)
            errors.Add("配布物は汎用サービスに対応していません。旧方式への復帰は明示したロールバックが必要です");
        if (action == "install" && service.Installed) errors.Add("サービスは導入済みです。更新を選んでください");
        if (action is "update" or "remove" or "rollback" && !service.Installed) errors.Add("FlowDeckサービスは未導入です");
        var expected = '"' + Path.Combine(ServiceEnvironment.InstallationDirectory, "FlowDeck.Service.exe") + '"';
        if (service.Installed && (!string.Equals(service.BinaryPath, expected, StringComparison.OrdinalIgnoreCase) || service.Account != "LocalSystem" || !service.OwnProcess))
            errors.Add("既存サービスの実行パス・アカウント・種類が管理対象と一致しません");
        if (hardwareBusy && !service.Running) errors.Add("FlowDeckまたは旧版が機器を使用しています。通常終了が必要です");
        if (service.Release is { } installed && package is not null && Version.TryParse(installed, out var oldVersion) &&
            action == "update" && Version.Parse(package.Manifest.Release) < oldVersion) errors.Add("以前の版へ戻す場合はロールバックを選んでください");
        var steps = action switch
        {
            "install" => new[] { "Program FilesとProgramDataに保護された専用フォルダーを作成", "配布ファイルを保護された仮配置へコピーして再検証", "指定したユーザーSIDと初期設定を機械全体の領域に保存", "SYSTEMの自動遅延起動サービスと障害時の再起動を登録", "監視状態でサービス起動を確認し、導入記録を保存" },
            "update" or "rollback" => new[] { "サービスを通常停止し、ファンの復帰を確認", "既存の実行ファイル・設定・登録情報を退避", "検証した配布物へ入れ替え、ユーザーSIDと機械設定を維持", "サービスを開始して状態を確認", "失敗時は退避した版と登録情報を復元" },
            _ => new[] { "サービスを通常停止し、ファンの復帰を確認", "SCMからサービス登録を解除", "実行ファイルを退避", "ProgramDataの設定・復旧記録・ログを保持" }
        };
        if (runtime == "devices-v2") steps = [.. steps, "汎用設定が未作成の場合は空の監視用設定を別領域へ作成し、起動方式を切替。旧設定は移行しない"];
        if (runtime == "legacy-v1") steps = [.. steps, "旧起動方式へ戻す。汎用設定と復旧記録は保持"];
        if (package?.Manifest.OpenRgb is not null) steps = [.. steps, "専用OpenRGBをサービスと同じ保護された配置へ導入。機器の取得経路は自動変更しない"];
        else if (installedOpenRgb is not null) steps = [.. steps, "今回の配置にはOpenRGBを含めない。以前のOpenRGBはサービスと一緒に退避"];
        return new(action, CoolingServiceProtocol.ServiceName, ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory,
            package?.Manifest.Release ?? service.Release, package?.ManifestHash, package?.Manifest.Files.Count ?? 0,
            package?.Manifest.Files.Values.Sum(f => f.Length) ?? 0, allowedUsers.ToArray(), true, errors.ToArray(), steps, Runtime: runtime,
            OpenRgb: package?.Manifest.OpenRgb, PreviousOpenRgb: installedOpenRgb);
    }
}
