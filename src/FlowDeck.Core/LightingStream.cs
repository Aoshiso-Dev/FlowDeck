namespace FlowDeck.Core;

public sealed partial class ControlCoordinator
{
    private bool ValidLightingChannelsLocked(string[]? channels) => channels is { Length: > 0 } &&
        channels.Distinct().Count() == channels.Length && channels.All(c => settings.Hardware.Leds?.Contains(c) == true && status.Lighting.Any(l => l.Id == c));

    public LightingLease BeginLightingStream(string[] channels) => BeginLightingStream(channels, null, null);
    public LightingLease BeginLightingStream(string[] channels, long? expectedRevision, long? expectedGeneration)
    {
        LightingLease lease;
        lock (sync)
        {
            if (expectedRevision is { } revision && revision != lightingRevision || expectedGeneration is { } device && device != generation)
                throw new InvalidOperationException("CONFLICT: 照明の操作または接続が変わりました");
            if (!ready || !Fresh(telemetryAt) || stopping || suspended || connecting || lightingBusy || faulted || !ValidLightingChannelsLocked(channels) || WiringErrorLocked() is not null)
                throw new InvalidOperationException("照明の同期を開始できません。接続・制御状態を確認してください");
            lightingRevision++; lease = new(lightingRevision, generation, channels.ToArray());
            lighting = lighting with { Requested = "synchronized", Outcome = Outcome.Pending };
        }
        Publish(); return lease;
    }
    public bool LightingLeaseValid(LightingLease lease)
    {
        lock (sync) return lease.Revision == lightingRevision && lease.Generation == generation && ready &&
            !stopping && !suspended && !connecting && !faulted && !lifetime.IsCancellationRequested;
    }
    public void EndLightingStream(LightingLease lease)
    {
        lock (sync)
        {
            if (lease.Revision != lightingRevision || lease.Generation != generation) return;
            lightingRevision++;
            if (lighting.Outcome == Outcome.Pending) lighting = lighting with { Outcome = Outcome.Unknown };
            else if (lighting.LastSent == "synchronized")
            {
                lighting = lighting with { Requested = "fixed", LastSent = "fixed" };
                settings = settings with { Lighting = lighting };
            }
        }
        NotifySettings(); Publish();
    }
    public async Task<bool> SendLightingFrameAsync(LightingLease lease, Func<RgbColor> sample, Func<RgbColor, Task>? companion = null)
    {
        // Do not queue frames. Telemetry, automatic cooling and pending output/exit commands own the USB gate first.
        using var usbLease = usb.TryEnter();
        if (usbLease is null) return false;
        lock (sync)
        {
            if (!LightingLeaseValid(lease)) throw new InvalidOperationException("接続または照明の操作が変更されました");
            if (!Fresh(telemetryAt) || fanBusy || lightingBusy) return false;
        }
        var color = sample();
        if (!color.Valid) throw new ArgumentException("照明色が不正です");
        var setting = new HubLightSetting("fixed", [color]);
        var hubWrite = hub.SetLightingEffectAsync(lease.Channels, setting, lifetime.Token);
        async Task WriteCompanion() { if (companion is not null) await companion(color); }
        var otherWrite = WriteCompanion();
        try { await Task.WhenAll(hubWrite, otherWrite); }
        finally
        {
            lock (sync) if (lease.Generation == generation)
                RecordLightingPortsLocked(lease.Channels, setting, hubWrite.IsCompletedSuccessfully ? hubWrite.Result.Outcome : Outcome.Unknown);
        }
        var result = await hubWrite;
        if (result.Outcome != Outcome.WrittenUnverified) throw new IOException(result.Error ?? "NZXT照明の送信を確認できません");
        bool first;
        lock (sync)
        {
            if (!LightingLeaseValid(lease)) throw new InvalidOperationException("照明の操作が変更されました");
            first = lighting.Requested == "synchronized" && lighting.Outcome == Outcome.Pending;
            lighting = new("synchronized", "synchronized", Outcome.WrittenUnverified, clock.GetUtcNow(), generation, setting, lease.Channels);
        }
        if (first) Publish();
        return true;
    }
}
