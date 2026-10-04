namespace FlowDeck.Core;

public sealed record DeviceDashboardEdit(string Epoch, string Generation, DeviceDashboardConfiguration Dashboard);

/// <summary>Keep presentation edits anchored to the configuration first edited, independently of telemetry refreshes.</summary>
public sealed class DeviceDashboardDraft
{
    private DeviceDashboardItem[] items = [], current = [];
    private string epoch = "", generation = "", editedEpoch = "", editedGeneration = "";
    private bool connected;
    public DeviceDashboardItem[] Items => items.ToArray();
    public bool Dirty { get; private set; }
    public bool Conflict => Dirty && (epoch != editedEpoch || generation != editedGeneration);
    public bool CanSave => connected && Dirty && !Conflict && Valid(new(2, items));
    public void Update(DeviceRegistrations registrations, DeviceDashboardConfiguration saved, DeviceServiceState state,
        string nextGeneration, bool available)
    {
        epoch = state.Inventory.Epoch; generation = nextGeneration; connected = available && !state.Releasing && !state.ConfigurationChanging;
        // Adoption of a new physical endpoint is a separate operation, never an effect of editing its display.
        current = saved.Items.Where(i => registrations.Devices.Any(d =>
            d.Id == i.Target.DeviceId && d.Endpoints.Any(e => e.Id == i.Target.EndpointId))).ToArray();
        if (!Dirty) items = current.ToArray();
    }
    private void Begin()
    {
        if (Dirty) return;
        editedEpoch = epoch; editedGeneration = generation; Dirty = true;
    }
    public void Set(DeviceDashboardItem item)
    {
        var index = Array.FindIndex(items, i => i.Target == item.Target && i.Measurement == item.Measurement);
        if (index < 0) throw new ArgumentException("表示項目がありません");
        if (items[index] == item) return;
        Begin(); items[index] = item;
    }
    public void Move(EndpointReference target, DeviceFeature measurement, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentException("移動方向が不正です");
        var index = Array.FindIndex(items, i => i.Target == target && i.Measurement == measurement);
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= items.Length) return;
        Begin(); (items[index], items[destination]) = (items[destination], items[index]);
    }
    public void Reset() { Dirty = false; items = current.ToArray(); }
    public void Remove(EndpointReference target, DeviceFeature measurement)
    {
        if (!items.Any(i => i.Target == target && i.Measurement == measurement)) return;
        Begin(); items = items.Where(i => i.Target != target || i.Measurement != measurement).ToArray();
    }
    public DeviceDashboardEdit Capture() => CanSave ? new(editedEpoch, editedGeneration, new(2, Items)) :
        throw new InvalidOperationException(Conflict ? "編集元の設定が変更されています" : "表示設定を保存できません");
    public static bool Valid(DeviceDashboardConfiguration? dashboard) => dashboard is { Version: 2, Items.Length: <= DeviceDashboard.MaximumSavedItems } &&
        dashboard.Items.All(i => i is not null && DeviceConfigurationRules.ValidReference(i.Target) && DeviceConfigurationRules.Text(i.Name, 80) &&
            ValidPresentation(i) &&
            (i.Measurement == DeviceFeature.Temperature ? i.Role == "temperature" : i.Measurement == DeviceFeature.Rpm && i.Role is "fan" or "pump")) &&
        dashboard.Items.DistinctBy(i => (i.Target, i.Measurement)).Count() == dashboard.Items.Length;
    public static bool ValidPresentation(DeviceDashboardItem item) => item.Indicator is null or "circle" or "bar" or "number" &&
        (item.DisplayMaximum is null || double.IsFinite(item.DisplayMaximum.Value) && item.DisplayMaximum is >= 1 and <= 1_000_000);
}
