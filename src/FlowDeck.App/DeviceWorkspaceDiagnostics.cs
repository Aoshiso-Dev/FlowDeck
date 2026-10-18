using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private bool diagnosticsExpanded, diagnosticsBusy, diagnosticMeasurements = true, diagnosticOperations = true, diagnosticConfiguration = true;
    private DeviceDiagnosticExport? diagnosticExport;
    private string? diagnosticMessage;
    public bool DiagnosticsExpanded { get => diagnosticsExpanded; set => Set(ref diagnosticsExpanded, value); }
    public bool DiagnosticsIdle => !diagnosticsBusy && !closing;
    public bool DiagnosticMeasurements { get => diagnosticMeasurements; set { if (Set(ref diagnosticMeasurements, value)) ClearDiagnosticExport(); } }
    public bool DiagnosticOperations { get => diagnosticOperations; set { if (Set(ref diagnosticOperations, value)) ClearDiagnosticExport(); } }
    public bool DiagnosticConfiguration { get => diagnosticConfiguration; set { if (Set(ref diagnosticConfiguration, value)) ClearDiagnosticExport(); } }
    private DeviceDiagnosticScope DiagnosticScope => (DiagnosticMeasurements ? DeviceDiagnosticScope.Measurements : 0) |
        (DiagnosticOperations ? DeviceDiagnosticScope.Operations : 0) | (DiagnosticConfiguration ? DeviceDiagnosticScope.Configuration : 0);
    public bool CanCreateDiagnostics => DiagnosticsIdle && !setupBusy && client.SupportsDiagnostics &&
        client.Snapshot is { Connected: true, ExitAttempted: false, State: { Releasing: false } };
    public bool CanSaveDiagnostics => DiagnosticsIdle && diagnosticExport is not null;
    public string DiagnosticsStatus => diagnosticsBusy ? "作成・保存中…" : diagnosticMessage ?? (!client.SupportsDiagnostics ? "サービス未対応" :
        client.Snapshot?.Connected != true ? "サービス未接続" : "未作成");
    public string DiagnosticsSummary => diagnosticExport is not { } value ? "" :
        $"{value.Service.CapturedUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss} 時点\n" +
        $"機器 {value.Service.Devices.Length} ／ 端子 {value.Service.Devices.Sum(d => d.Endpoints.Length)}" +
        (value.Service.Events is { } events ? $" ／ 履歴 {events.Length}" : "") +
        (value.Service.FormatVersion >= 2 ? $"\n機種・版情報：{value.Service.Devices.Count(d => d.Hardware?.Items.Any(i => i.State == DeviceDiagnosticReadState.Available) == true)} / {value.Service.Devices.Length} 機器" : "") +
        (value.Service.Scope.HasFlag(DeviceDiagnosticScope.Configuration) ? "\n設定：" +
            (!value.Service.ConfigurationAvailable ? "未取得" : value.Service.ConfigurationStableDuringCapture ? "取得済み" : "取得中に変更あり") : "");
    public string DiagnosticsVersions
    {
        get
        {
            if (diagnosticExport is not { } value) return "";
            var env = value.Service.Environment;
            var service = value.Service.Demo ? "デモ" : env.Components.FirstOrDefault(c => c.Component == "FlowDeck.Service")?.Version ?? "未取得";
            var kernel = env.KernelDrivers?.FirstOrDefault(d => d.Component == "PawnIO");
            var driver = kernel is null ? "未取得" :
                (kernel.RegistrationState == DeviceDiagnosticReadState.Available ? "ファイル " + DiagnosticVersionText(kernel.FileState, kernel.FileVersion) :
                    "ドライバー " + DiagnosticVersionText(kernel.RegistrationState, null)) +
                " ／ 登録版 " + DiagnosticVersionText(kernel.PackageState, kernel.PackageVersion);
            return $"{env.OperatingSystem} {env.OsVersion} ({env.OsArchitecture ?? env.Architecture})\n" +
                $"アプリ {value.AppVersion ?? "未取得"} ／ サービス {service}\nPawnIO：{driver}";
        }
    }
    private static string DiagnosticVersionText(DeviceDiagnosticReadState state, string? version) => state switch
    {
        DeviceDiagnosticReadState.Available => version ?? "未取得",
        DeviceDiagnosticReadState.NotInstalled => "未登録",
        DeviceDiagnosticReadState.Missing => "ファイルなし",
        DeviceDiagnosticReadState.AccessDenied => "権限不足",
        DeviceDiagnosticReadState.Invalid => "情報不正",
        DeviceDiagnosticReadState.Unsupported => "対象外",
        DeviceDiagnosticReadState.Failed => "取得失敗",
        DeviceDiagnosticReadState.TimedOut => "時間切れ",
        _ => "未取得"
    };
    public ICommand CreateDiagnostics => new AsyncCommand(_ => CreateDiagnosticsAsync(), _ => CanCreateDiagnostics, e => { diagnosticMessage = e.Message; PublishDiagnostics(); });
    public ICommand SaveDiagnostics => new AsyncCommand(async _ =>
    {
        var dialog = new SaveFileDialog { Title = "診断情報の書き出し", Filter = "診断情報 (*.json)|*.json", DefaultExt = ".json",
            AddExtension = true, OverwritePrompt = true, FileName = $"FlowDeck-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dialog.ShowDialog() == true) await SaveDiagnosticsToAsync(dialog.FileName);
    }, _ => CanSaveDiagnostics, e => { diagnosticMessage = "保存失敗 · 保存先の確認が必要"; PublishDiagnostics(); });
    private void ClearDiagnosticExport() { diagnosticExport = null; diagnosticMessage = null; PublishDiagnostics(); }
    private void PublishDiagnostics()
    {
        foreach (var property in new[] { nameof(DiagnosticsIdle), nameof(CanCreateDiagnostics), nameof(CanSaveDiagnostics), nameof(DiagnosticsStatus), nameof(DiagnosticsSummary), nameof(DiagnosticsVersions) }) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task CreateDiagnosticsAsync()
    {
        if (!CanCreateDiagnostics) return;
        var scope = DiagnosticScope; diagnosticExport = null; diagnosticMessage = null; diagnosticsBusy = true; PublishDiagnostics();
        try
        {
            var value = await client.ReadDiagnosticsAsync(scope);
            if (!closing && scope == DiagnosticScope) { diagnosticExport = value; diagnosticMessage = "作成済み"; }
        }
        finally { diagnosticsBusy = false; PublishDiagnostics(); }
    }
    public async Task SaveDiagnosticsToAsync(string path)
    {
        if (!CanSaveDiagnostics || diagnosticExport is not { } value) return;
        diagnosticsBusy = true; PublishDiagnostics();
        try { await DeviceDiagnosticFiles.SaveAsync(path, value); diagnosticMessage = "保存済み: " + Path.GetFileName(path); }
        finally { diagnosticsBusy = false; PublishDiagnostics(); }
    }
}
