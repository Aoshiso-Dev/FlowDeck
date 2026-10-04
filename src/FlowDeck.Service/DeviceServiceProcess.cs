using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

/// <summary>Protected, SCM-owned v2 host. Selection is explicit; no fallback opens a second hardware stack.</summary>
internal sealed class DeviceServiceProcess(DeviceServiceLifecycle lifecycle, ServiceHardwareLease lease) : IWindowsServiceProcess
{
    public Task Completion => lifecycle.Completion;
    public Task PowerAsync(bool resume) => lifecycle.PowerAsync(resume);
    public static async Task<DeviceServiceProcess> StartRealAsync(ServiceRuntimeSelection selection)
    {
        var installation = ServiceEnvironment.ValidateInstallation(AppContext.BaseDirectory);
        var directory = ServiceEnvironment.DataDirectory;
        _ = selection.ReadConfiguration(directory); // Refuse missing/corrupt/foreign config before acquiring hardware.
        var lease = await ServiceHardwareLease.AcquireAsync().ConfigureAwait(false);
        RollingEventLog? log = null;
        try
        {
            log = new RollingEventLog(Path.Combine(directory, "logs"));
            var lifecycle = await DeviceServiceLifecycle.StartAsync(async () =>
            {
                _ = selection.ReadConfiguration(directory);
                var boot = ServiceEnvironment.BootIdentity();
                // Legacy handles are closed before any v2 provider is constructed or opened.
                await RecoverLegacyAsync(directory, boot, log).ConfigureAwait(false);
                var store = DeviceConfigurationCodec.Store(ServiceRuntimeSelection.ConfigurationDirectory(directory));
                var routing = PackagedProviderRouting.ForInstalledService(selection.InstallationId,
                    ServiceRuntimeSelection.RecoveryDirectory(directory), boot, log);
                var startup = await DeviceRuntimeStartup.CreateWithProviderRecoveryAsync(store, selection.InstallationId,
                    routing.CreateInitial, installation.AllowedUsers, log, providerRouting: routing).ConfigureAwait(false);
                DeviceServiceHost? host = null;
                try
                {
                    host = await DeviceServiceHost.StartAsync(startup, installation.AllowedUsers, log,
                        allowsClients: () => ServiceDeployment.AllowsClients(directory)).ConfigureAwait(false);
                    ServiceEnvironment.Write(Path.Combine(directory, "service-ready.json"), new ServiceReadiness(1, Environment.ProcessId,
                        startup.Runtime.Epoch, ServiceDeployment.ReadPending(directory)?.Id, DateTimeOffset.UtcNow));
                    log.Write("deviceServiceStart", new { processId = Environment.ProcessId, startup.Runtime.Epoch, boot });
                    return host;
                }
                catch (Exception e)
                {
                    if (host is not null)
                        await DeviceStartupException.RethrowAfterCleanupAsync(e, host, () => host.Runtime.ResourcesClosed && host.Completion.IsCompleted).ConfigureAwait(false);
                    // Host.StartAsync transfers its failed transport and runtime together through this exception.
                    else if (e is not DeviceStartupException)
                        await DeviceStartupException.RethrowAfterCleanupAsync(e, startup, () => startup.Runtime.ResourcesClosed).ConfigureAwait(false);
                    throw;
                }
            }, log).ConfigureAwait(false);
            return new(lifecycle, lease);
        }
        catch (Exception e)
        {
            await CloseFailedStartupAsync(e, lease, log).ConfigureAwait(false); throw;
        }
    }
    internal static async Task CloseFailedStartupAsync(Exception failure, ServiceHardwareLease lease, IEventLog? log)
    {
        var cleanup = new DeviceStartupOwnership(failure as DeviceStartupException, lease);
        try { log?.Write("deviceServiceStartFailure", new { error = failure.ToString() }); }
        catch (Exception logging) { failure = new AggregateException("起動と診断の保存に失敗しました", failure, logging); }
        await DeviceStartupException.RethrowAfterCleanupAsync(failure, cleanup, () => cleanup.ResourcesClosed).ConfigureAwait(false);
    }
    private static async Task RecoverLegacyAsync(string directory, string boot, IEventLog log)
    {
        var errors = new List<Exception>();
        async Task Recover(string name, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (DeviceStartupException e) when (!e.ResourcesClosed)
            {
                errors.Add(new IOException(name + ": " + e.Message, e));
                // Do not construct the next legacy backend while this one still owns its bus or handles.
                throw DeviceStartupException.Combine(new AggregateException("旧サービスの接続終了が未確認です", errors), e);
            }
            catch (Exception e) { errors.Add(new IOException(name + ": " + e.Message, e)); }
        }
        await Recover("旧マザーファン", async () =>
        {
            var journal = new FanRecoveryJournal(directory, boot);
            if (journal.PreviousBoot) journal.ArchivePreviousBoot();
            else if (journal.Pending.Length != 0)
            {
                var backend = new HardwareTemperatures(journal);
                await RecoverAndCloseAsync(backend, () => backend.RecoverInterruptedFansAsync()).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await Recover("旧Control Hub", async () =>
        {
            var journal = new HubFanRecoveryJournal(directory);
            if (journal.Pending is null) return;
            var adapter = new RecoverableHubAdapter(new HubWorkerClient(
                Path.Combine(ServiceEnvironment.InstallationDirectory, "runtime", "python", "python.exe"),
                Path.Combine(ServiceEnvironment.InstallationDirectory, "worker", "hub_worker.py"), log, isolated: true), journal, () => new());
            await RecoverAndCloseAsync(adapter, () => adapter.RecoverAsync()).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Recover("旧メモリ照明", () => new EneMemoryRgbControl(recoveryDirectory: directory).RecoverInterruptedStreamAsync()).ConfigureAwait(false);
        await Recover("旧マザー照明", async () => { await AsrockRgbRecovery.RecoverAsync(directory).ConfigureAwait(false); }).ConfigureAwait(false);
        if (errors.Count != 0) throw new AggregateException("旧サービスの出力復旧が未確認です", errors);
    }
    private static async Task RecoverAndCloseAsync(IAsyncDisposable owner, Func<Task> recover)
    {
        try { await recover().ConfigureAwait(false); }
        catch (Exception e) { await DeviceStartupException.RethrowAfterCleanupAsync(e, owner).ConfigureAwait(false); throw; }
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) { throw new DeviceStartupException(e, owner); }
    }
    public async ValueTask DisposeAsync()
    {
        try { await lifecycle.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            // A failed close can leave live handles. Keep other processes excluded until a retry succeeds
            // (or the OS terminates this process and releases its handles and ownership together).
            if (lifecycle.ResourcesClosed) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
