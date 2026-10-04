using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record LocalMonitoringSnapshot(DashboardReading[] Temperatures, DashboardReading[] Fans, string Status, string? Problem);

/// <summary>Ephemeral monitoring, with no service, configuration authority or output driver.</summary>
public sealed class LocalMonitoringSession(IHardwareSensorBackend backend, TimeProvider? time = null) : IAsyncDisposable
{
    private sealed record Entry(HardwareSensorDevice Device, int Ordinal, bool Connected);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> devices = [];
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim shutdown = new(1);
    private Task polling = Task.CompletedTask;
    private bool started, stopping, closed;
    private string? problem;
    public LocalMonitoringSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var values = devices.Values.OrderBy(e => e.Ordinal).SelectMany(e => e.Device.Sensors.Select(s =>
                {
                    var age = clock.GetElapsedTime(s.Timestamp).TotalSeconds;
                    var state = DeviceMeasurement.State(new(new(e.Device.ConnectionId, s.Id), s.Feature, s.Value, age, false, s.Problem, s.Issue), 0);
                    var status = !e.Connected ? "切断" : problem is not null ? "未取得" : DeviceMeasurement.Label(state);
                    var kind = s.Feature == DeviceFeature.Temperature ? "temperature" : "fan";
                    var name = s.Feature == DeviceFeature.Temperature ? e.Device.Name + " · GPU" + e.Ordinal : "GPU" + e.Ordinal + " ファン";
                    var item = new DashboardItem("local-gpu", e.Device.ConnectionId + ":" + s.Id, kind, name);
                    return new DashboardReading(item, e.Connected && problem is null && state == DeviceMeasurementState.Valid ? s.Value : null,
                        null, status, "監視のみ", e.Device.Name, item.Key);
                })).ToArray();
                var count = devices.Values.Count(d => d.Connected);
                var label = stopping ? "監視を終了しています…" : problem is not null ? problem : !started ? "GPUを取得しています…" :
                    count == 0 ? "対応するGPUを検出できません" : $"GPU {count}台 · 監視のみ";
                return new(values.Where(v => v.Item.Kind == "temperature").ToArray(), values.Where(v => v.Item.Kind == "fan").ToArray(), label, problem);
            }
        }
    }
    public Task PollAsync()
    {
        lock (gate)
        {
            if (stopping) return Task.CompletedTask;
            if (!polling.IsCompleted) return polling;
            return polling = ReadAsync();
        }
    }
    private async Task ReadAsync()
    {
        try
        {
            var read = await backend.ReadDevicesAsync(stop.Token).ConfigureAwait(false);
            if (read is null || read.Length > DeviceRegistry.MaximumDevices || read.Any(d => d is null || d.Kind != DeviceKind.Graphics ||
                !DeviceServiceProtocol.Id(d.ConnectionId) || !DeviceConfigurationRules.Text(d.Name, 160) || d.Sensors is not { Length: <= 256 } ||
                d.Sensors.Any(s => s is null || s.Feature is not (DeviceFeature.Temperature or DeviceFeature.Rpm) || !Enum.IsDefined(s.Issue) ||
                    !DeviceConfigurationRules.Text(s.Id, 512)) || d.Sensors.DistinctBy(s => s.Id).Count() != d.Sensors.Length) ||
                read.DistinctBy(d => d.ConnectionId).Count() != read.Length) throw new IOException("監視値の形式が不正です");
            read = Contract.Copy(read);
            lock (gate)
            {
                if (stopping) return;
                if (devices.Keys.Union(read.Select(d => d.ConnectionId)).Count() > DeviceRegistry.MaximumDevices)
                    throw new IOException("監視機器数が上限に達しました");
                foreach (var key in devices.Keys.ToArray()) devices[key] = devices[key] with { Connected = false };
                foreach (var device in read)
                    devices[device.ConnectionId] = new(device, devices.TryGetValue(device.ConnectionId, out var old) ? old.Ordinal : devices.Count + 1, true);
                started = true; problem = null;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (gate) if (!stopping) { started = true; problem = e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                ? "対応するGPUドライバーを利用できません" : "GPU監視の取得に失敗しました"; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await shutdown.WaitAsync().ConfigureAwait(false);
        try
        {
            Task pending;
            lock (gate) { if (closed) return; stopping = true; pending = polling; }
            await stop.CancelAsync().ConfigureAwait(false); await pending.ConfigureAwait(false);
            await backend.DisposeAsync().ConfigureAwait(false);
            lock (gate) closed = true;
            stop.Dispose();
        }
        finally { shutdown.Release(); }
    }
}
