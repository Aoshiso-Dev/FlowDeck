using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    private readonly object notificationSync = new();
    private readonly SemaphoreSlim notificationStoreGate = new(1);
    private NotificationEngine notificationEngine = new();
    private readonly List<NotificationEvent> notificationHistory = [];
    private readonly Dictionary<string,long> notificationOperations = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<OperationResult> notificationQueue = new();
    private CancellationTokenSource? notificationLifetime;
    private Task notificationLoop = Task.CompletedTask;
    private bool notificationsSuspended;
    private string? notificationHub, notificationBoard;
    public NotificationSettings NotificationOptions { get; private set; } = new(Rules: []);
    public string? NotificationError { get; private set; }
    public string? NotificationRuntimeError { get; private set; }
    public NotificationEvent[] NotificationHistory { get { lock(notificationSync) return notificationHistory.ToArray(); } }
    public NotificationEvent[] ActiveNotifications { get { lock(notificationSync) return notificationEngine.Active; } }
    public event Action<NotificationEvent[]>? NotificationsRaised;
    private NotificationStore NotificationStore => new(Store.DirectoryPath);

    public async Task LoadNotificationsAsync()
    {
        await notificationStoreGate.WaitAsync();
        try
        {
            try
            {
                var value = await NotificationStore.LoadAsync();
                lock(notificationSync) { NotificationOptions = value; NotificationError = null; notificationEngine.Reset(); }
            }
            catch(Exception e)
            {
                lock(notificationSync) { NotificationOptions = new(Enabled:false,Rules:[]); NotificationError = "通知設定を読み込めません: " + e.Message; notificationEngine.Reset(); }
            }
        }
        finally { notificationStoreGate.Release(); }
    }
    public async Task SaveNotificationsAsync(NotificationSettings value)
    {
        value = Contract.Copy(value); await notificationStoreGate.WaitAsync();
        try
        {
            if(NotificationError is not null) throw new IOException(NotificationError);
            await NotificationStore.SaveAsync(value);
            lock(notificationSync) NotificationOptions = value;
            Log.Write("notificationSettings",value);
        }
        finally { notificationStoreGate.Release(); }
    }
    public NotificationSample[] NotificationSamples() => NotificationInputs.Collect(Coordinator.Settings,Coordinator.Snapshot,Motherboard.Board,BoardFans.States);
    private async Task StartNotificationsAsync()
    {
        lock(notificationSync)
        {
            notificationEngine = new(); notificationHistory.Clear(); notificationOperations.Clear(); notificationQueue.Clear(); NotificationRuntimeError = null;
            notificationHub = notificationBoard = null; notificationsSuspended = false;
        }
        await LoadNotificationsAsync();
        Coordinator.OperationCompleted += TrackNotificationOperation;
        var lifetime = new CancellationTokenSource(); notificationLifetime = lifetime;
        notificationLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    try { EvaluateNotifications(); NotificationRuntimeError = null; }
                    catch(Exception e)
                    {
                        if(NotificationRuntimeError != e.Message) Log.Write("notificationError",new { e.Message });
                        NotificationRuntimeError = e.Message;
                    }
                } while(await timer.WaitForNextTickAsync(lifetime.Token));
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        });
    }
    private async Task StopNotificationsAsync()
    {
        Coordinator.OperationCompleted -= TrackNotificationOperation;
        if(notificationLifetime is { } lifetime) { lifetime.Cancel(); await notificationLoop; lifetime.Dispose(); notificationLifetime = null; }
    }
    public void SuspendNotifications(bool suspended)
    {
        lock(notificationSync)
        {
            notificationsSuspended = suspended; notificationEngine.Reset(); notificationHub = notificationBoard = null;
            notificationOperations.Clear(); notificationQueue.Clear();
        }
    }
    internal void EvaluateNotifications()
    {
        NotificationEvent[] events;
        lock(notificationSync)
        {
            if(notificationsSuspended) return;
            var settings = NotificationOptions; var state = Coordinator.Snapshot; var board = Motherboard.Board;
            var signals = new List<NotificationSignal>();
            if(settings.ControlFaults)
            {
                signals.Add(new("hub-fault","NZXTファン制御",state.Faulted,state.Fault ?? "ファン制御で異常が発生しました",true));
                foreach(var (id,fan) in BoardFans.States)
                    signals.Add(new("fan-fault:" + id,MotherboardFans.Find(id).Name,fan.Mode == "fault",fan.Message,true));
            }
            if(settings.Disconnects)
            {
                var id = Coordinator.Settings.Hardware.Hub?.Id;
                if(notificationHub != id) notificationHub = null;
                if(id is not null && state.HubReady && DashboardDisplay.Fresh(state.TelemetryTimestamp)) notificationHub = id;
                if(notificationHub is not null) signals.Add(new("hub-offline:" + notificationHub,"NZXT Control Hub",!state.HubReady || !DashboardDisplay.Fresh(state.TelemetryTimestamp),"NZXT Control Hub · 接続または更新が停止しています"));
                if(board.IsFresh()) notificationBoard = board.Name;
                if(notificationBoard is not null) signals.Add(new("board-offline:" + notificationBoard,"マザーセンサー",!board.IsFresh(),"マザーセンサー · 接続または更新が停止しています"));
            }
            var collected = notificationEngine.Evaluate(settings,NotificationInputs.Collect(Coordinator.Settings,state,board,BoardFans.States),signals.ToArray()).ToList();
            while(notificationQueue.TryDequeue(out var operation))
            {
                if(!settings.Enabled || !settings.OperationFailures || settings.ControlFaults && state.Faulted) continue;
                var key = "operation:" + operation.Command + ":" + (operation.ErrorCode ?? operation.Outcome.ToString()); var now = TimeProvider.System.GetTimestamp();
                if(notificationOperations.TryGetValue(key,out var sent) && TimeProvider.System.GetElapsedTime(sent,now).TotalSeconds < Math.Max(60,settings.RepeatSeconds)) continue;
                if(notificationOperations.Count >= 128) notificationOperations.Clear();
                notificationOperations[key] = now;
                collected.Add(new(key,operation.Source,"raised",operation.Message,DateTimeOffset.UtcNow));
            }
            events = collected.ToArray();
            RecordNotificationsLocked(events);
        }
        PublishNotifications(events);
    }
    private void RecordNotificationsLocked(NotificationEvent[] events)
    {
        foreach(var e in events) { notificationHistory.Insert(0,e); Log.Write("notification",e); }
        if(notificationHistory.Count > 100) notificationHistory.RemoveRange(100,notificationHistory.Count - 100);
    }
    private void TrackNotificationOperation(OperationResult result)
    {
        if(result.Outcome is not (Outcome.Failed or Outcome.Partial or Outcome.Unknown)) return;
        // Never take the notification lock or call desktop subscribers on the hardware completion thread.
        notificationQueue.Enqueue(result);
        while(notificationQueue.Count > 128) notificationQueue.TryDequeue(out _);
    }
    public void TestNotification()
    {
        NotificationEvent[] events = [new("test","FlowDeck","test","FlowDeck · 通知テスト",DateTimeOffset.UtcNow)];
        lock(notificationSync) RecordNotificationsLocked(events);
        PublishNotifications(events);
    }
    private void PublishNotifications(NotificationEvent[] events)
    {
        if(events.Length == 0 || NotificationsRaised is not { } subscribers) return;
        foreach(Action<NotificationEvent[]> subscriber in subscribers.GetInvocationList())
            try { subscriber(events); } catch(Exception e) { Log.Write("notificationDelivery",new { e.Message }); }
    }
}
