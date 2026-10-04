namespace FlowDeck.Core;

public sealed partial class DeviceControlRuntime
{
    private readonly DeviceNotificationMonitor notifications;
    private Task notificationWatchdog = Task.CompletedTask;
    public void TickNotifications()
    {
        lock (gate)
        {
            if (stopping || configurationHandler is not IDeviceNotificationConfiguration source) return;
            try { notifications.Evaluate(source.ReadNotificationConfiguration(), State()); }
            catch (Exception e) { notifications.EvaluationFailed(e); }
        }
    }
    private DeviceServiceReply ExecuteNotificationCore(DeviceServiceRequest request)
    {
        if (configurationHandler is not IDeviceNotificationConfiguration) return Reject(request, "通知機能に対応するサービスの更新が必要です");
        try
        {
            // Apply an explicit disconnect before evaluation, so it cannot complete an offline timer.
            if (request.Operation == DeviceOperation.ReportM5Connection) notifications.ReportM5(request.M5Connection!);
            TickNotifications();
            if (request.Operation == DeviceOperation.ReportM5Connection) return Reply(request);
            if (request.Operation == DeviceOperation.TestNotification)
            {
                notifications.Test();
                return Reply(request, new(request.Id, request.Source, Outcome.Confirmed, clock.GetUtcNow(), [], "通知テストを記録しました"));
            }
            return Reply(request) with { Notifications = notifications.Read(request.Notifications!) };
        }
        catch (Exception e) { return Reject(request, e.Message); }
    }
    private async Task<DeviceServiceReply> TrackNotificationCompletionAsync(DeviceServiceRequest request, Task<DeviceServiceReply> completion, long started)
    {
        var reply = await completion.ConfigureAwait(false);
        lock (gate)
        {
            RecordDiagnosticOperation(request, reply, started);
            // Recorded once in the cached request's completion, never by Result queries or retransmission.
            if (DeviceNotificationMonitor.Failed(reply) && configurationHandler is IDeviceNotificationConfiguration)
            {
                TickNotifications();
                try { notifications.RecordOperation(request, reply); }
                catch (Exception e) { notifications.EvaluationFailed(e); }
            }
        }
        return reply;
    }
}
