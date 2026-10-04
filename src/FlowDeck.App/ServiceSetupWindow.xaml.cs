using System.Windows;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public partial class ServiceSetupWindow : Window
{
    private readonly ServiceSetupSession session;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closing, closed, working;
    internal Task<bool> Completion => completion.Task;
    internal ServiceSetupWindow(ServiceSetupSession session)
    {
        this.session = session; InitializeComponent(); Render();
        Closing += (_, e) =>
        {
            if (working)
            {
                e.Cancel = true;
                if (session.State == ServiceSetupState.Reviewing) { closing = true; stop.Cancel(); }
                else StatusText.Text = "導入結果の確認が終わるまでお待ちください";
            }
            else if (!closed) { closed = true; stop.Dispose(); completion.TrySetResult(session.State == ServiceSetupState.Succeeded); }
        };
    }
    internal void Render()
    {
        StatusText.Text = session.Status;
        var plan = session.Plan;
        PlanPanel.Visibility = plan is null ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Text = plan is null ? "" : $"{(plan.Action == "install" ? "新規導入" : "更新")} · {plan.Release}";
        InstallPathText.Text = plan?.InstallationDirectory ?? ""; DataPathText.Text = plan?.DataDirectory ?? "";
        StepsList.ItemsSource = plan?.Steps;
        BlockersText.Text = plan is null ? "" : string.Join(Environment.NewLine, plan.Blockers);
        DetailsText.Text = plan is null ? "" : $"{plan.VerifiedFiles:N0} ファイル · {plan.VerifiedBytes / 1048576.0:N1} MB\nSHA-256: {plan.ManifestHash}\n\n操作ユーザー\n{string.Join(Environment.NewLine, plan.AllowedUsers)}";
        ReviewButton.IsEnabled = !working && !closing;
        ApplyButton.IsEnabled = !working && !closing && session.State == ServiceSetupState.Ready;
        ApplyButton.Content = plan?.Action == "update" ? "管理者として更新" : "管理者として導入";
        CloseButton.Content = session.Prepared ? "接続へ戻る" : session.State == ServiceSetupState.Succeeded ? "再接続" : "閉じる";
        CloseButton.IsEnabled = !working || session.State == ServiceSetupState.Reviewing;
    }
    private async void Review(object sender, RoutedEventArgs e) => await ReviewAsync();
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    private void Exit(object sender, RoutedEventArgs e) => Close();
    internal Task ReviewAsync() => OperateAsync(() => session.ReviewAsync(stop.Token));
    internal Task ApplyAsync() => OperateAsync(session.ApplyAsync);
    private async Task OperateAsync(Func<Task> action)
    {
        if (working || closing || closed) return;
        working = true;
        try { var task = action(); Render(); await task; }
        finally { working = false; Render(); if (closing) Close(); }
    }
    internal static async Task<bool> RunAsync(Window owner)
        => await RunAsync(owner, ServiceSetupSession.ForApplication(AppContext.BaseDirectory));
    internal static async Task<bool> RunAsync(Window owner, ServiceSetupSession session, Func<ServiceSetupWindow, Task>? inspect = null)
    {
        var window = new ServiceSetupWindow(session) { Owner = owner };
        // The parent remains visible but cannot start a competing connection or close during deployment.
        owner.IsEnabled = false;
        try { window.Show(); await window.ReviewAsync(); if (inspect is not null) await inspect(window); return await window.Completion; }
        finally { owner.IsEnabled = true; owner.Activate(); }
    }
}
