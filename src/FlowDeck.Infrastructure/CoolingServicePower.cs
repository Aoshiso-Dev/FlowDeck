using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class CoolingServiceRuntime
{
    private sealed record SleepTicket(string Token, string Sid, string ConfigurationHash, string PolicyHash, long? ReadyAt = null);
    private readonly object powerSync = new();
    private SleepTicket? sleepTicket;
    private bool corePowerSuspended;
    private void ClearSleepTicket() { lock (powerSync) sleepTicket = null; }
    private void InvalidateChangedSleepSettings()
    {
        var hash = SettingsTransfer.Hash(ServiceFanConfiguration.From(Coordinator.Settings));
        lock (powerSync) if (sleepTicket is { } ticket && ticket.ConfigurationHash != hash) sleepTicket = null;
    }
    private void ExpireSleepTicket()
    {
        lock (powerSync) if (sleepTicket?.ReadyAt is { } ready && leaseClock.GetElapsedTime(ready).TotalSeconds >= 60) sleepTicket = null;
    }
    private string? SleepResumeToken()
    {
        lock (powerSync) return !powerSuspended && !corePowerSuspended && sleepTicket is { ReadyAt: not null } ticket &&
            ticket.Sid == Volatile.Read(ref ownerSid) && leaseClock.GetElapsedTime(ticket.ReadyAt.Value).TotalSeconds < 60 ? ticket.Token : null;
    }
    // Both the authenticated GUI and SCM may arrive first. Capture once, before releasing ownership.
    private async Task SuspendPowerCoreAsync()
    {
        if (corePowerSuspended) return;
        var current = Coordinator.Snapshot; var settings = Coordinator.Settings; var sid = Volatile.Read(ref ownerSid);
        lock (powerSync) sleepTicket = sid is not null && current is { Managing: true, Faulted: false, FanBusy: false, LastConfirmed: not null } &&
            settings.LastConfirmedPolicy is { } policy && SettingsTransfer.Hash(policy) == SettingsTransfer.Hash(current.LastConfirmed)
            ? new(Guid.NewGuid().ToString("N"), sid, SettingsTransfer.Hash(ServiceFanConfiguration.From(settings)), SettingsTransfer.Hash(policy)) : null;
        corePowerSuspended = true;
        try
        {
            await ReleaseCoreAsync("Windowsのスリープ", preserveSleep: true);
            if (LifecycleError is not null) ClearSleepTicket();
            await Fans.SuspendAsync();
        }
        catch { ClearSleepTicket(); throw; }
        finally { Coordinator.Suspend(); }
    }
    private async Task ResumePowerCoreAsync()
    {
        if (!corePowerSuspended) return; // Duplicate OS notifications must not reconnect or re-arm a ticket.
        Fans.Resume(); await Coordinator.ResumeFromSleepAsync(); corePowerSuspended = false;
        lock (powerSync) if (sleepTicket is { } ticket) sleepTicket = ticket with { ReadyAt = leaseClock.GetTimestamp() };
    }
    private async Task RestoreSleepAsync(ServiceSleepResume request)
    {
        RequireSettingsRevision(request.ExpectedRevision);
        var settingsHash = SettingsTransfer.Hash(ServiceFanConfiguration.From(Coordinator.Settings));
        SleepTicket ticket;
        lock (powerSync)
        {
            if (powerSuspended || corePowerSuspended || sleepTicket is not { ReadyAt: not null } current || current.Token != request.Token ||
                current.Sid != Volatile.Read(ref ownerSid) || current.ConfigurationHash != settingsHash ||
                leaseClock.GetElapsedTime(current.ReadyAt.Value).TotalSeconds >= 60)
                throw new IOException("スリープ前の操作ユーザー・設定・再開期限が一致しません");
            ticket = current; sleepTicket = null; // Consume before a possible write; never replay an uncertain resume.
        }
        await ResumeConfirmedPolicyAsync(ticket.PolicyHash);
        log.Write("serviceSleepResume", new { Coordinator.Snapshot.Mode, Coordinator.Snapshot.Confirmation, Coordinator.Snapshot.Faulted });
    }
    private async Task ResumeConfirmedPolicyAsync(string policyHash)
    {
        var settings = Coordinator.Settings; var policy = settings.LastConfirmedPolicy;
        if (Coordinator.Snapshot.Managing || policy is null || policyHash != SettingsTransfer.Hash(policy) ||
            SettingsTransfer.Hash(policy.Limits) != SettingsTransfer.Hash(settings.Limits) || policy.CpuSensorId != settings.CpuSensorId || policy.GpuSensorId != settings.GpuSensorId ||
            policy.Mode == FanMode.Manual && (policy.ManualDuty is not { } duty || !FanPolicyEngine.IsDutyAllowed(duty, settings.Limits)))
            throw new IOException("現在の条件と確認済みの再開設定が一致しません");
        Coordinator.UpdateSettings(settings with { Startup = new(ResumeControl: true) }, false);
        try { await Coordinator.RestoreStartupAsync(); }
        finally { Coordinator.UpdateSettings(Coordinator.Settings with { Startup = new() }, false); }
    }
}
