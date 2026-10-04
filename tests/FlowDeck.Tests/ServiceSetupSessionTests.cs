using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class ServiceSetupSessionTests
{
    private static ServiceDeploymentPlan Plan() => new("install", CoolingServiceProtocol.ServiceName,
        ServiceEnvironment.InstallationDirectory, ServiceEnvironment.DataDirectory, "2.0.0", new('a', 64), 12, 1024,
        [ServiceFixture.Sid], true, [], ["監視用の設定から開始"], Runtime: "devices-v2");

    [Fact] public async Task ReviewDoesNotExecuteAndDisplayedPlanCannotRewriteReviewedUsersOrHash()
    {
        var calls = 0; var original = Plan(); ServiceDeploymentPlan? applied = null;
        var session = new ServiceSetupSession(_ => Task.FromResult(original), plan =>
        { calls++; applied = plan; return Task.FromResult(new ServiceSetupExecution(false, 0)); }, _ => Task.FromResult(true));
        await session.ApplyAsync(); Assert.Equal(0, calls);
        await session.ReviewAsync(); Assert.Equal(ServiceSetupState.Ready, session.State); Assert.Equal(0, calls);
        session.Plan!.AllowedUsers[0] = "changed";
        await session.ApplyAsync(); Assert.Equal(1, calls); Assert.Equal(original.AllowedUsers, applied!.AllowedUsers);
        Assert.Equal(ServiceSetupState.Succeeded, session.State);
        await session.ApplyAsync(); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("manifest")][InlineData("users")][InlineData("action")][InlineData("blocker")][InlineData("steps")]
    public async Task ChangedReviewRequiresExplicitReconfirmationBeforeAnyExecution(string change)
    {
        var initial = Plan(); var current = initial; var executions = 0;
        var session = new ServiceSetupSession(_ => Task.FromResult(current), _ =>
        { executions++; return Task.FromResult(new ServiceSetupExecution(false, 0)); }, _ => Task.FromResult(true));
        await session.ReviewAsync();
        current = change switch
        {
            "manifest" => initial with { ManifestHash = new('b', 64) },
            "users" => initial with { AllowedUsers = ["S-1-5-21-1-2-3-1002"] },
            "action" => initial with { Action = "update" },
            "blocker" => initial with { Blockers = ["以前の導入が未完了"] },
            _ => initial with { Steps = ["different changes"] }
        };
        await session.ApplyAsync(); Assert.Equal(0, executions); Assert.Equal(ServiceSetupState.Blocked, session.State);
        Assert.Contains("再確認", session.Status);
    }

    [Fact] public async Task RepeatedActionsDuringDeploymentDoNotLaunchOrInspectAgain()
    {
        var gate = new TaskCompletionSource<ServiceSetupExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspections = 0; var executions = 0;
        var session = new ServiceSetupSession(_ => { inspections++; return Task.FromResult(Plan()); },
            _ => { executions++; return gate.Task; }, _ => Task.FromResult(true));
        await session.ReviewAsync(); var apply = session.ApplyAsync(); Assert.True(session.Busy);
        await session.ReviewAsync(); await session.ApplyAsync();
        Assert.Equal(2, inspections); Assert.Equal(1, executions);
        gate.SetResult(new(false, 0)); await apply; Assert.False(session.Busy);
    }

    [Theory][InlineData(true, null, ServiceSetupState.Cancelled)][InlineData(false, 1, ServiceSetupState.Failed)]
    [InlineData(false, null, ServiceSetupState.Failed)]
    public async Task CancellationAndFailureCannotReportSuccessOrRetryAutomatically(bool cancelled, int? exitCode, ServiceSetupState expected)
    {
        var executions = 0; var confirmations = 0;
        var session = new ServiceSetupSession(_ => Task.FromResult(Plan()), _ =>
        { executions++; return Task.FromResult(new ServiceSetupExecution(cancelled, exitCode)); }, _ => { confirmations++; return Task.FromResult(true); });
        await session.ReviewAsync(); await session.ApplyAsync(); await session.ApplyAsync();
        Assert.Equal(expected, session.State); Assert.Equal(1, executions); Assert.Equal(0, confirmations);
    }

    [Fact] public async Task InstallationVerificationRetainsExclusiveOperationUntilItFinishes()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var inspections = 0;
        var session = new ServiceSetupSession(_ => { inspections++; return Task.FromResult(Plan()); },
            _ => Task.FromResult(new ServiceSetupExecution(false, 0)), _ => gate.Task);
        await session.ReviewAsync(); var apply = session.ApplyAsync();
        Assert.True(session.Busy); await session.ReviewAsync(); await session.ApplyAsync(); Assert.Equal(2, inspections);
        gate.SetResult(true); await apply; Assert.Equal(ServiceSetupState.Succeeded, session.State);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ZeroExitWithoutVerifiedInstalledGenerationRemainsUnconfirmed(bool throws)
    {
        var session = new ServiceSetupSession(_ => Task.FromResult(Plan()), _ => Task.FromResult(new ServiceSetupExecution(false, 0)),
            _ => throws ? throw new IOException("not readable") : Task.FromResult(false));
        await session.ReviewAsync(); await session.ApplyAsync();
        Assert.Equal(ServiceSetupState.Unconfirmed, session.State); Assert.Contains("確認できません", session.Status);
    }

    [Theory][InlineData("legacy")][InlineData("remove")][InlineData("path")][InlineData("seed")]
    public async Task StartupOnlyAcceptsGenericInstallOrUpdatePlans(string invalid)
    {
        var plan = Plan(); plan = invalid switch
        {
            "legacy" => plan with { Runtime = "legacy-v1" }, "remove" => plan with { Action = "remove" },
            "path" => plan with { InstallationDirectory = Path.GetTempPath() }, _ => plan with { SeedHash = new('b', 64) }
        };
        var session = new ServiceSetupSession(_ => Task.FromResult(plan), _ => throw new InvalidOperationException("must not execute"), _ => Task.FromResult(true));
        await session.ReviewAsync(); await session.ApplyAsync(); Assert.Equal(ServiceSetupState.Failed, session.State); Assert.Null(session.Plan);
    }

    [Fact] public async Task ClosingReviewCancelsAndRetainsNoUsablePlan()
    {
        using var ct = new CancellationTokenSource();
        var session = new ServiceSetupSession(async token => { await Task.Delay(Timeout.Infinite, token); return Plan(); },
            _ => throw new InvalidOperationException("must not execute"), _ => Task.FromResult(true));
        var review = session.ReviewAsync(ct.Token); ct.Cancel(); await review;
        Assert.Equal(ServiceSetupState.Cancelled, session.State); Assert.Null(session.Plan);
    }

    [Fact] public async Task BlockedPreflightNeverInvokesExecutor()
    {
        var plan = Plan() with { Blockers = ["前回の更新記録が残っています"] };
        var session = new ServiceSetupSession(_ => Task.FromResult(plan), _ => throw new InvalidOperationException("must not execute"), _ => Task.FromResult(true));
        await session.ReviewAsync(); await session.ApplyAsync();
        Assert.Equal(ServiceSetupState.Blocked, session.State); Assert.Equal(plan.Blockers, session.Plan!.Blockers);
    }

    [Fact] public async Task MissingBundledPackageFailsBeforeAnyServiceInspectionOrElevation()
    {
        using var folder = new ServiceTestDirectory();
        var session = ServiceSetupSession.ForApplication(folder.Path);
        await session.ReviewAsync(); await session.ApplyAsync();
        Assert.Equal(ServiceSetupState.Failed, session.State); Assert.Null(session.Plan);
    }

    [Theory][InlineData("preflight")][InlineData("launch")]
    public async Task FailureBeforeProcessCompletionNeverReachesConfirmation(string stage)
    {
        var inspections = 0; var executions = 0; var confirmations = 0;
        var session = new ServiceSetupSession(_ =>
        { if (++inspections == 2 && stage == "preflight") throw new IOException(); return Task.FromResult(Plan()); }, _ =>
        { executions++; throw new IOException(); }, _ => { confirmations++; return Task.FromResult(true); });
        await session.ReviewAsync(); await session.ApplyAsync();
        Assert.Equal(ServiceSetupState.Failed, session.State); Assert.Equal(stage == "launch" ? 1 : 0, executions); Assert.Equal(0, confirmations);
    }

    private static (ServicePackage Package, ServiceDeploymentPlan Plan) Package(string directory)
    {
        var root = Path.Combine(directory, "配布物 with spaces", "service-setup"); var payload = Path.Combine(root, "payload");
        foreach (var name in ServicePackage.RequiredFiles)
        { var target = Path.Combine(payload, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "inert test file " + name); }
        ServiceEnvironment.Write(Path.Combine(root, ServicePackage.ManifestName), ServicePackage.CreateManifest(payload, "2.0.0", ["devices-v2"]));
        var package = ServicePackage.Open(root);
        return (package, ServiceDeploymentPlanning.Create("install", package, new(false), [ServiceFixture.Sid], false, runtime: "devices-v2"));
    }

    [Fact] public void LaunchContractKeepsExactUnicodePathsHashAndUsersWithoutRunningTheExecutable()
    {
        using var folder = new ServiceTestDirectory(); var (package, plan) = Package(folder.Path);
        var info = ServiceSetupSession.StartInfo(package.Root, plan);
        Assert.True(info.UseShellExecute); Assert.Equal("runas", info.Verb); Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Equal(Path.Combine(package.PayloadDirectory, "FlowDeck.Service.exe"), info.FileName);
        Assert.Equal(new[] { "--setup-apply", "install", "--package", package.Root, "--manifest-sha256", package.ManifestHash,
            "--runtime", "devices-v2", "--user", ServiceFixture.Sid }, info.ArgumentList);
        Assert.Empty(info.Arguments); Assert.False(info.RedirectStandardOutput);
    }

    [Theory][InlineData("payload")][InlineData("manifest")]
    public void LastMomentPackageModificationCannotProduceAProcessLaunch(string target)
    {
        using var folder = new ServiceTestDirectory(); var (package, plan) = Package(folder.Path);
        File.AppendAllText(target == "payload" ? Path.Combine(package.PayloadDirectory, "FlowDeck.Service.exe") :
            Path.Combine(package.Root, ServicePackage.ManifestName), " ");
        Assert.ThrowsAny<IOException>(() => ServiceSetupSession.StartInfo(package.Root, plan));
    }
}
