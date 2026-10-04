using System.Windows.Input;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceLoginStartupSnapshot? loginSnapshot;
    private bool loginExpanded, loginEnabled, loginReplace, loginBusy;
    private string? loginMessage;
    public bool LoginExpanded { get => loginExpanded; set => Set(ref loginExpanded, value); }
    public bool LoginEnabled { get => loginEnabled; set { if (Set(ref loginEnabled, value)) { loginMessage = null; PublishLogin(); } } }
    public bool LoginReplace { get => loginReplace; set { if (Set(ref loginReplace, value)) PublishLogin(); } }
    public bool LoginIdle => !loginBusy && !closing && loginStartup is not null;
    public bool LoginEditable => LoginIdle && loginSnapshot is { State: not DeviceLoginStartupState.Unavailable };
    public bool LoginConflict => loginSnapshot?.State == DeviceLoginStartupState.OtherRegistration;
    public bool CanSaveLogin => LoginEditable && LoginEnabled != (loginSnapshot?.State == DeviceLoginStartupState.Registered) &&
        (!LoginConflict || LoginEnabled && LoginReplace);
    public string LoginStatus => loginBusy ? "確認・保存中…" : loginMessage ?? (loginSnapshot?.State is DeviceLoginStartupState.Registered or DeviceLoginStartupState.Unregistered &&
        LoginEnabled != (loginSnapshot.State == DeviceLoginStartupState.Registered) ? "未保存" : loginSnapshot?.State switch
    {
        DeviceLoginStartupState.Registered => "登録済み",
        DeviceLoginStartupState.Unregistered => LoginEnabled ? "未保存" : "未登録",
        DeviceLoginStartupState.OtherRegistration => "別のFlowDeckが登録されています",
        DeviceLoginStartupState.Unavailable => loginSnapshot.Problem!,
        _ => loginStartup is null ? "この起動方法では設定できません" : "未取得"
    });
    public ICommand SaveLogin => new AsyncCommand(_ => SaveLoginAsync(), _ => CanSaveLogin, e => { loginMessage = "保存できませんでした。登録状態を再取得してください"; PublishLogin(); });
    public ICommand ReloadLogin => new AsyncCommand(_ => ReloadLoginAsync(), _ => LoginIdle, e => { loginMessage = e.Message; PublishLogin(); });
    public ICommand OpenLoginSettings => new AsyncCommand(_ => OpenLoginSettingsAsync(), _ => LoginIdle,
        e => { loginMessage = "Windowsのスタートアップ設定を開けませんでした"; PublishLogin(); });
    public Task OpenLoginSettingsAsync()
    {
        if (!LoginIdle) return Task.CompletedTask;
        if (client.IsDemo && loginStartup?.IsDemo != true) throw new InvalidOperationException("デモからWindowsの設定を開けません");
        return loginStartup!.OpenWindowsSettingsAsync();
    }
    private void PublishLogin()
    {
        foreach (var name in new[] { nameof(LoginIdle), nameof(LoginEditable), nameof(LoginConflict), nameof(CanSaveLogin), nameof(LoginStatus) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task ReloadLoginAsync()
    {
        if (!LoginIdle) return;
        if (client.IsDemo && loginStartup?.IsDemo != true) throw new InvalidOperationException("デモからWindowsの自動起動登録へアクセスできません");
        loginBusy = true; PublishLogin();
        try
        {
            loginSnapshot = await loginStartup!.ReadAsync(); loginEnabled = loginSnapshot.State == DeviceLoginStartupState.Registered;
            loginReplace = false; loginMessage = null; Notify(nameof(LoginEnabled)); Notify(nameof(LoginReplace));
        }
        finally { loginBusy = false; PublishLogin(); }
    }
    public async Task SaveLoginAsync()
    {
        if (!CanSaveLogin || loginSnapshot is null) return;
        if (client.IsDemo && loginStartup?.IsDemo != true) throw new InvalidOperationException("デモからWindowsの自動起動登録へアクセスできません");
        var selected = LoginEnabled; loginBusy = true; PublishLogin();
        try
        {
            loginSnapshot = await loginStartup!.SaveAsync(loginSnapshot, selected, LoginReplace);
            loginEnabled = loginSnapshot.State == DeviceLoginStartupState.Registered; loginReplace = false;
            loginMessage = selected ? "登録しました" : "登録を解除しました"; Notify(nameof(LoginEnabled)); Notify(nameof(LoginReplace));
        }
        finally { loginBusy = false; PublishLogin(); }
    }
}
