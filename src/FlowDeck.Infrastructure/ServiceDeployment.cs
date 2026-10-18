using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// The implementation must restrict every operation to FlowDeckCooling. Start verifies a
// fresh service readiness record while the pending transaction still gates GUI access.
public interface IServiceDeploymentControl
{
    ServiceRegistration Inspect();
    Task StopAsync();
    Task ConfigureAsync(string[] allowedUsers, ServiceRegistration? restore = null);
    Task StartAsync(string transactionId);
    Task ActivateAsync(ServiceRegistration? restore = null);
    Task RemoveAsync();
    Task<IAsyncDisposable> AcquireHardwareAsync();
}
public sealed record ServiceDeploymentState(int Version, string Id, string Action, string Phase, ServiceRegistration Before,
    string[] Users, string? ManifestHash, Dictionary<string, string?>? Backups = null);
public sealed record ServiceDeploymentDocument(ServiceDeploymentState State, string Sha256);
public sealed record ServiceReadiness(int Version, int ProcessId, string Epoch, string? TransactionId, DateTimeOffset StartedAt);
public sealed record ServiceDeploymentResult(string Id, string Action, string Outcome, string BackupDirectory);

/// <summary>Durable setup with fixed destinations. No recursive deletion or recovery-journal replacement.</summary>
public sealed partial class ServiceDeployment
{
    public const string PendingName = "deployment-pending.json", InstalledManifestName = "service-package.json";
    private static readonly string[] LegacySnapshotNames = ["service.json", "cooling-settings.json", InstalledManifestName];
    private static readonly string[] SnapshotNames = [.. LegacySnapshotNames, ServiceRuntimeSelection.FileName];
    private static readonly string[] Phases = ["prepared", "backed-up", "old-moved", "new-moved", "configured", "started", "committed", "rolling-back", "rolled-back"];
    private readonly string install, data, deployments;
    private readonly bool testing;
    private readonly IServiceDeploymentControl control;
    internal Action<string>? AfterStep { get; set; }
    internal ServiceDeployment(string install, string data, IServiceDeploymentControl control, bool testing)
    {
        this.install = Path.GetFullPath(install); this.data = Path.GetFullPath(data); this.control = control; this.testing = testing;
        deployments = Path.Combine(Path.GetDirectoryName(this.install)!, "Deployments");
    }
    public ServiceDeployment(IServiceDeploymentControl control) : this(ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory, control, false) { }
    private string Pending => Path.Combine(data, PendingName);
    private string Work(ServiceDeploymentState state) => Path.Combine(deployments, state.Id);
    private string Old(ServiceDeploymentState state) => Path.Combine(Work(state), "old");
    private string New(ServiceDeploymentState state) => Path.Combine(Work(state), "new");
    private string Backup(ServiceDeploymentState state) => Path.Combine(data, "deployment-backups", state.Id);
    private static string HashFile(string path)
    { ServiceEnvironment.NoLinks(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static ServiceDeploymentState? ReadPending(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, PendingName); if (!File.Exists(path)) return null;
        var document = ServiceEnvironment.Read<ServiceDeploymentDocument>(path); var state = document.State;
        if (state is null || state.Version != 1 || !CoolingServiceProtocol.Id(state.Id) || state.Action is not ("install" or "update" or "rollback" or "remove") ||
            !Phases.Contains(state.Phase) || state.Before is null || (state.Action == "install") == state.Before.Installed || state.Users is not { Length: >= 1 and <= 8 } ||
            state.Users.Distinct(StringComparer.Ordinal).Count() != state.Users.Length || state.Users.Any(s => !new SecurityIdentifier(s).IsAccountSid()) ||
            state.ManifestHash is { } hash && !ValidHash(hash) || state.Backups is { } backups &&
            (backups.Count is not (3 or 4) || LegacySnapshotNames.Any(n => !backups.ContainsKey(n)) ||
                backups.Keys.Any(n => !SnapshotNames.Contains(n)) || backups.Values.Any(h => h is not null && !ValidHash(h))) ||
            document.Sha256 != SettingsTransfer.Hash(state)) throw new IOException("サービス更新の復旧記録が不正です");
        if (state.Action != "remove" && state.ManifestHash is null || state.Backups is null && state.Phase is not ("prepared" or "rolling-back" or "rolled-back"))
            throw new IOException("サービス更新の復旧段階と退避内容が一致しません");
        if (state.Before.Settings is { } settings && (settings.StartType is < 2 or > 4 || settings.Actions is not { Length: <= 1024 } ||
            settings.Actions.Any(a => a is null || a.Type is < 0 or > 3) || settings.RebootMessage is not { Length: <= 32768 } ||
            settings.Command is not { Length: <= 32768 } || settings.SecurityDescriptor is not { Length: > 0 and <= 65536 } ||
            settings.DisplayName is { Length: > 4096 } || settings.Description is { Length: > 32768 } || settings.ErrorControl is < 0 or > 3 ||
            settings.Dependencies is { } dependencies && (dependencies.Length > 1024 || dependencies.Any(d => string.IsNullOrEmpty(d) || d.Length > 4096 || d.Contains('/')))))
            throw new IOException("サービス登録の復旧記録が不正です");
        return state;
    }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool AllowsClients(string dataDirectory)
    { var state = ReadPending(dataDirectory); return state is null || state.Phase is "committed" or "rolled-back"; }
    private static void RequireAdministrator()
    {
        using var user = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("適用・復旧は既に管理者権限のある端末から実行してください。自動昇格は行いません");
    }
    private void ValidateFolder(string path)
    { ServiceEnvironment.NoLinks(path); if (!testing) ServiceEnvironment.ValidateTree(path); }
    private void Folder(string path, bool readable = false)
    {
        ServiceEnvironment.NoLinks(path);
        if (Directory.Exists(path)) { if (!testing) ServiceEnvironment.ValidateSecurity(new DirectoryInfo(path).GetAccessControl()); return; }
        if (testing) { Directory.CreateDirectory(path); return; }
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (readable) security.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.CreateDirectory(path); ValidateFolder(path);
    }
    private void ProtectFile(string path)
    {
        ServiceEnvironment.NoLinks(path); if (testing) return;
        var file = new FileInfo(path); var security = file.GetAccessControl(); security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        file.SetAccessControl(security); ServiceEnvironment.ValidateSecurity(file.GetAccessControl());
    }
    private void ProtectCreatedTree(string path)
    {
        ServiceEnvironment.NoLinks(path); if (testing) return;
        var folder = new DirectoryInfo(path); var security = folder.GetAccessControl(); security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        folder.SetAccessControl(security); ServiceEnvironment.ValidateSecurity(folder.GetAccessControl());
        foreach (var file in folder.EnumerateFiles()) ProtectFile(file.FullName);
        foreach (var child in folder.EnumerateDirectories()) ProtectCreatedTree(child.FullName);
    }
    private void Save<T>(string path, T value) { ServiceEnvironment.Write(path, value); ProtectFile(path); }
    private void Copy(string source, string target)
    {
        ServiceEnvironment.NoLinks(source); ServiceEnvironment.NoLinks(target);
        Folder(Path.GetDirectoryName(target)!);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
        { input.CopyTo(output); output.Flush(true); }
        ProtectFile(target);
    }
    private void MoveDirectory(string source, string target, ServiceDeploymentState state)
    {
        // The journal never provides paths. Every move is between our fixed install path and this transaction's subtree.
        foreach (var path in new[] { source, target })
        {
            var full = Path.GetFullPath(path);
            if (full != install && !full.StartsWith(Work(state) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新の移動先が専用領域の外です");
            ServiceEnvironment.NoLinks(full);
        }
        ValidateFolder(source); Folder(Path.GetDirectoryName(target)!); Directory.Move(source, target);
    }
    private FileStream OpenSetup()
    {
        if (!testing) RequireAdministrator();
        Folder(Path.GetDirectoryName(install)!, true); Folder(Path.GetDirectoryName(data)!); Folder(data); Folder(deployments);
        ValidateFolder(data); if (Directory.Exists(install)) ValidateFolder(install);
        var path = Path.Combine(data, "deployment.lock"); ServiceEnvironment.NoLinks(path);
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { ProtectFile(path); return file; } catch { file.Dispose(); throw; }
    }
    private void Record(ServiceDeploymentState state)
    { Save(Pending, new ServiceDeploymentDocument(state, SettingsTransfer.Hash(state))); AfterStep?.Invoke(state.Phase); }
    private void Archive(ServiceDeploymentState state)
    {
        var history = Path.Combine(data, "deployment-history"); Folder(history);
        Save(Path.Combine(history, state.Id + ".json"), new ServiceDeploymentDocument(state, SettingsTransfer.Hash(state)));
        ServiceEnvironment.NoLinks(Pending); File.Delete(Pending);
    }
    private void ValidateRegistration(ServiceRegistration value)
    {
        var expected = '"' + Path.Combine(install, "FlowDeck.Service.exe") + '"';
        if (value.Installed && (!string.Equals(value.BinaryPath, expected, StringComparison.OrdinalIgnoreCase) || value.Account != "LocalSystem" || !value.OwnProcess))
            throw new IOException("管理対象と異なるサービス登録です");
    }
    private bool PendingRecovery() => new HubFanRecoveryJournal(data).Pending is not null ||
        new FanRecoveryJournal(data, "setup-inspection").Pending.Length != 0 ||
        DeviceRecoveryInspection.HasPending(ServiceRuntimeSelection.RecoveryDirectory(data)) || PendingConfigurationRecovery();
    private void ConfirmRestoredFans()
    {
        if (PendingRecovery())
            throw new IOException("ファン・照明の復帰または設定・取得経路の反映が未完了です。復旧記録を保持して更新を止めます");
    }
    private Dictionary<string, string?> Snapshot(ServiceDeploymentState state)
    {
        Folder(Backup(state)); var result = new Dictionary<string, string?>();
        foreach (var name in SnapshotNames)
        {
            var source = Path.Combine(data, name); result[name] = File.Exists(source) ? HashFile(source) : null;
            if (result[name] is not null) Copy(source, Path.Combine(Backup(state), name));
        }
        SnapshotDeviceConfiguration(state, result);
        return result;
    }
    private void ValidateSnapshot(ServiceDeploymentState state)
    {
        if (state.Backups is null) return;
        // Validate the entire backup before changing any file. Hardware obligations are deliberately excluded.
        foreach (var (name, hash) in state.Backups)
            if (hash is not null && HashFile(Path.Combine(Backup(state), name)) != hash) throw new IOException("退避した設定が変わっています");
        _ = ValidateDeviceSnapshot(state);
    }
    private void RestoreSnapshot(ServiceDeploymentState state)
    {
        if (state.Backups is null) return;
        ValidateSnapshot(state);
        RestoreDeviceConfiguration(state);
        foreach (var (name, hash) in state.Backups)
        {
            var target = Path.Combine(data, name); ServiceEnvironment.NoLinks(target);
            if (hash is null) { if (File.Exists(target)) File.Delete(target); }
            else
            {
                // Preserve the original file and its security metadata when its bytes already match the verified snapshot.
                if (File.Exists(target) && HashFile(target) == hash) continue;
                var temporary = Path.Combine(data, name + ".restore-" + Guid.NewGuid().ToString("N"));
                Copy(Path.Combine(Backup(state), name), temporary); File.Move(temporary, target, true);
            }
        }
    }
    private ServicePackage Stage(ServicePackage source, ServiceDeploymentState state)
    {
        Folder(Work(state)); Folder(New(state)); Folder(Path.Combine(New(state), "payload"));
        Copy(Path.Combine(source.Root, ServicePackage.ManifestName), Path.Combine(New(state), ServicePackage.ManifestName));
        foreach (var name in source.Manifest.Files.Keys) Copy(Path.Combine(source.PayloadDirectory, name), Path.Combine(New(state), "payload", name));
        var staged = ServicePackage.Open(New(state), state.ManifestHash); ValidateFolder(New(state)); return staged;
    }
    public async Task<ServiceDeploymentResult> ApplyAsync(string action, string? packageRoot, string? manifestHash, string[] users, AppSettings? seed = null, bool emptySettings = false,
        string? runtime = null)
    {
        using var setup = OpenSetup();
        if (ReadPending(data) is not null) throw new IOException("前回の更新が未完了です。先に復旧を実行してください");
        var before = control.Inspect(); ValidateRegistration(before);
        if (action is not ("install" or "update" or "remove" or "rollback") || (action == "install") == before.Installed)
            throw new IOException("操作とサービスの導入状態が一致しません");
        ValidateRuntimeChoice(action, runtime);
        if (users.Length is < 1 or > 8 || users.Distinct().Count() != users.Length || users.Any(s => !new SecurityIdentifier(s).IsAccountSid())) throw new ArgumentException("操作ユーザーが不正です");
        if (before.Installed || File.Exists(Path.Combine(data, "service.json")))
        {
            var saved = ServiceEnvironment.Read<ServiceInstallation>(Path.Combine(data, "service.json"));
            if (saved.Version != 1 || !saved.AllowedUsers.Order().SequenceEqual(users.Order())) throw new IOException("導入済みの操作ユーザーを変更できません");
        }
        if (before.Installed) ServicePackage.Read(Path.Combine(data, InstalledManifestName), install);
        else if (Directory.Exists(install)) throw new IOException("登録のないサービス配置を上書きできません");
        ServicePackage? package = null;
        if (action != "remove")
        {
            if (packageRoot is null || manifestHash is null || !ValidHash(manifestHash)) throw new ArgumentException("確認済みの配布物とマニフェストハッシュが必要です");
            package = ServicePackage.Open(packageRoot, manifestHash);
            if (action == "update" && Version.TryParse(before.Release, out var old) && Version.Parse(package.Manifest.Release) < old) throw new IOException("以前の版はロールバックを選んでください");
        }
        var selectedRuntime = ServiceRuntimeSelection.Read(data);
        if (action != "remove" && runtime != "legacy-v1" && (runtime == "devices-v2" || selectedRuntime is not null) && package?.SupportsDevices != true)
            throw new IOException("配布物は汎用サービスに対応していません。旧方式へ戻す場合はロールバックで明示してください");
        if (runtime == "devices-v2" && seed is not null) throw new IOException("汎用サービスは新しい監視用設定から開始します。旧設定の移行は対象外です");
        if (seed is not null)
        {
            if (action != "install" || File.Exists(Path.Combine(data, "cooling-settings.json"))) throw new IOException("冷却設定の移行は設定のない初回導入だけに使用できます");
            seed = SettingsTransfer.Portable(seed);
            var empty = SettingsTransfer.FromFiles(new Dictionary<string, byte[]>());
            SettingsTransfer.Validate(empty with { Settings = seed });
        }
        else if (action == "install" && !File.Exists(Path.Combine(data, "cooling-settings.json")) && !emptySettings && runtime != "devices-v2")
            throw new IOException("初回導入では移行する設定、または空の設定で開始する指定が必要です");
        var state = new ServiceDeploymentState(1, Guid.NewGuid().ToString("N"), action, "prepared", before, users.ToArray(), manifestHash);
        ConfirmOptionalRuntime(action, runtime, package);
        if (package is not null) Stage(package, state); else Folder(Work(state));
        Record(state);
        try
        {
            await control.StopAsync();
            await using (var ownership = await control.AcquireHardwareAsync())
            {
                ConfirmRestoredFans();
                // A client may have changed route while staging or before normal stop completed.
                ConfirmOptionalRuntime(action, runtime, package);
                state = state with { Backups = Snapshot(state), Phase = "backed-up" }; Record(state);
                ValidateSnapshot(state);
                if (before.Installed)
                {
                    Folder(Old(state)); Copy(Path.Combine(data, InstalledManifestName), Path.Combine(Old(state), ServicePackage.ManifestName));
                    MoveDirectory(install, Path.Combine(Old(state), "payload"), state); AfterStep?.Invoke("old-move-before-record");
                }
                state = state with { Phase = "old-moved" }; Record(state);
                if (package is not null)
                {
                    MoveDirectory(Path.Combine(New(state), "payload"), install, state); AfterStep?.Invoke("new-move-before-record");
                    var temporary = Path.Combine(data, InstalledManifestName + ".new-" + state.Id);
                    Copy(Path.Combine(New(state), ServicePackage.ManifestName), temporary); File.Move(temporary, Path.Combine(data, InstalledManifestName), true);
                    if (!File.Exists(Path.Combine(data, "service.json"))) Save(Path.Combine(data, "service.json"), new ServiceInstallation(1, users));
                    if (!File.Exists(Path.Combine(data, "cooling-settings.json")))
                    { new ServiceSettingsStore(data).Save(seed ?? new()); ProtectFile(Path.Combine(data, "cooling-settings.json")); }
                    ServicePackage.Read(Path.Combine(data, InstalledManifestName), install, manifestHash); ValidateFolder(install);
                    if (runtime == "devices-v2")
                    {
                        _ = new ServiceRuntimeBootstrap(data).PrepareMonitoring();
                        ProtectFile(Path.Combine(data, ServiceRuntimeSelection.FileName)); ProtectCreatedTree(Path.Combine(data, "devices-v2"));
                        AfterStep?.Invoke("runtime-prepared");
                    }
                    else if (runtime == "legacy-v1")
                    { var selectionPath = Path.Combine(data, ServiceRuntimeSelection.FileName); ServiceEnvironment.NoLinks(selectionPath); File.Delete(selectionPath); }
                    ValidateFolder(data);
                }
                state = state with { Phase = "new-moved" }; Record(state);
            }
            if (action == "remove") await control.RemoveAsync();
            else
            {
                await control.ConfigureAsync(users); state = state with { Phase = "configured" }; Record(state);
                await control.StartAsync(state.Id); state = state with { Phase = "started" }; Record(state);
                await control.ActivateAsync();
                if (!control.Inspect().Running) throw new IOException("確定前にサービスが停止しました");
            }
            state = state with { Phase = "committed" }; Record(state); Archive(state);
            return new(state.Id, action, "committed", Work(state));
        }
        catch (Exception original) when (original is not ServiceDeploymentCrash)
        {
            if (ReadPending(data)?.Phase == "committed") throw new IOException("適用は完了しましたが完了記録の整理が未完了です。復旧コマンドで整理してください", original);
            try { await RollbackAsync(ReadPending(data) ?? state); }
            catch (Exception recovery) { throw new AggregateException("更新に失敗し、復旧も未完了です。記録を保持しています", original, recovery); }
            throw new IOException("更新に失敗したため以前の配置へ戻しました", original);
        }
    }
    internal static void ValidateRuntimeChoice(string action, string? runtime)
    {
        if (runtime is not (null or "devices-v2" or "legacy-v1") || runtime is not null && action == "remove" ||
            runtime == "devices-v2" && action == "rollback" || runtime == "legacy-v1" && action != "rollback")
            throw new ArgumentException("起動方式は導入・更新でdevices-v2、旧版への復帰でlegacy-v1を指定してください");
    }
    public async Task<ServiceDeploymentResult?> RecoverAsync()
    {
        using var setup = OpenSetup(); var state = ReadPending(data); if (state is null) return null;
        ValidateRegistration(state.Before); ValidateRegistration(control.Inspect());
        if (state.Phase is "committed" or "rolled-back") Archive(state);
        else await RollbackAsync(state);
        return new(state.Id, state.Action, state.Phase == "committed" ? "committed" : "rolled-back", Work(state));
    }
    private async Task RollbackAsync(ServiceDeploymentState state)
    {
        if (state.Phase == "committed") { Archive(state); return; } // The commit point must never be undone by a cleanup failure.
        state = state with { Phase = "rolling-back" }; Record(state);
        ValidateRegistration(control.Inspect()); await control.StopAsync();
        if (control.Inspect().Installed && Directory.Exists(install) && PendingRecovery())
        {
            // A previous stop may have left an unconfirmed physical obligation. Let the verified
            // installed service run its existing startup recovery before touching any binary.
            ServicePackage.Read(Path.Combine(data, InstalledManifestName), install); ValidateFolder(install);
            await control.ConfigureAsync(state.Users); await control.StartAsync(state.Id); await control.StopAsync();
        }
        await using (var ownership = await control.AcquireHardwareAsync())
        {
            ConfirmRestoredFans();
            ValidateSnapshot(state);
            var oldPayload = Path.Combine(Old(state), "payload");
            if (Directory.Exists(oldPayload))
            {
                ServicePackage.Open(Old(state), state.Backups?[InstalledManifestName] ?? throw new IOException("以前の版の整合性記録がありません"));
                if (Directory.Exists(install)) MoveDirectory(install, Path.Combine(Work(state), "rejected-" + Guid.NewGuid().ToString("N")), state);
                MoveDirectory(oldPayload, install, state); AfterStep?.Invoke("restore-move-before-record");
            }
            else if (!state.Before.Installed && Directory.Exists(install))
                MoveDirectory(install, Path.Combine(Work(state), "rejected-" + Guid.NewGuid().ToString("N")), state);
            RestoreSnapshot(state);
            if (state.Before.Installed) ServicePackage.Read(Path.Combine(data, InstalledManifestName), install);
        }
        if (state.Before.Installed)
        {
            await control.ConfigureAsync(state.Users, state.Before);
            if (state.Before.Running) await control.StartAsync(state.Id);
            await control.ActivateAsync(state.Before);
        }
        else await control.RemoveAsync();
        state = state with { Phase = "rolled-back" }; Record(state); Archive(state);
    }
}

// Test-only simulated abrupt process termination. Ordinary failures execute rollback immediately.
internal sealed class ServiceDeploymentCrash : Exception;
