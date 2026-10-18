namespace FlowDeck.Core;

public sealed record DeviceMonitoringSelection(DeviceDashboardItem Item, long Generation);
public sealed record DeviceMonitoringCandidate(DeviceMonitoringSelection Selection, string DeviceName, double? Value, string Status, bool Available);
public sealed record DeviceMonitoringEdit(string Epoch, string ConfigurationGeneration, long TopologyRevision, DeviceMonitoringSelection[] Items);

/// <summary>Explicit monitoring choices stay anchored to the inventory/configuration first selected.</summary>
public sealed class DeviceMonitoringDraft
{
    public const int PageSize = 32;
    private DeviceMonitoringCandidate[] candidates = [];
    private readonly Dictionary<string, DeviceMonitoringCandidate> selected = [];
    private string epoch = "", configuration = "", editedEpoch = "", editedConfiguration = "";
    private long topology, editedTopology;
    private bool connected;
    public DeviceMonitoringCandidate[] Candidates => candidates.ToArray();
    public DeviceMonitoringCandidate[] VisibleCandidates => candidates.Skip(Page * PageSize).Take(PageSize).ToArray();
    public int Page { get; private set; }
    public int PageCount => Math.Max(1, (candidates.Length + PageSize - 1) / PageSize);
    public int CandidateCount => candidates.Length;
    public int RegisteredCount { get; private set; }
    public int RemainingSlots => Math.Max(0, DeviceDashboard.MaximumSavedItems - RegisteredCount);
    public bool CanSelectMore => selected.Count < RemainingSlots;
    public bool Dirty => selected.Count != 0;
    public int SelectedCount => selected.Count;
    public bool Conflict => Dirty && (epoch != editedEpoch || configuration != editedConfiguration || topology != editedTopology);
    public bool CanRegister => connected && Dirty && !Conflict && selected.Count <= RemainingSlots &&
        selected.All(p => candidates.Any(c => Key(c) == p.Key && c.Available && c.Selection.Generation == p.Value.Selection.Generation));
    public static string Key(DeviceMonitoringCandidate candidate) => DeviceDashboard.Key(candidate.Selection.Item.Target, candidate.Selection.Item.Measurement);
    public bool IsSelected(DeviceMonitoringCandidate candidate) => selected.ContainsKey(Key(candidate));
    public void Update(DeviceRegistrations registrations, DeviceDashboardConfiguration dashboard, DeviceServiceState state,
        string nextConfiguration, double elapsed, bool available)
    {
        epoch = state.Inventory.Epoch; topology = state.Inventory.TopologyRevision; configuration = nextConfiguration;
        connected = available && !state.Releasing && !state.ConfigurationChanging;
        RegisteredCount = dashboard.Items.Length;
        var registered = dashboard.Items.Select(i => (i.Target, i.Measurement)).ToHashSet();
        var connectedTargets = state.Inventory.Devices.Where(d => available && d.Connected)
            .SelectMany(d => d.Device.Endpoints.Select(e => new EndpointReference(d.Device.Id, e.Id))).ToHashSet();
        var values = new List<DeviceMonitoringCandidate>();
        foreach (var item in DeviceDashboard.Items(registrations, dashboard, state.Inventory).Where(i =>
            connectedTargets.Contains(i.Target) && !registered.Contains((i.Target, i.Measurement))))
        {
            var instance = state.Inventory.Devices.Single(d => d.Device.Id == item.Target.DeviceId);
            var endpoint = instance.Device.Endpoints.Single(e => e.Id == item.Target.EndpointId);
            if (!endpoint.Capabilities.Any(c => c.Feature == item.Measurement && c.Support == ImplementationSupport.Supported) ||
                (item.Measurement == DeviceFeature.Temperature ? endpoint.Kind != EndpointKind.Sensor : endpoint.Kind is not (EndpointKind.Cooling or EndpointKind.Sensor))) continue;
            var reading = DeviceDashboard.Read(item, registrations, state, elapsed, available);
            var proposal = item with { Name = DisplayName(item.Name), Visible = true };
            var deviceName = registrations.Devices.SingleOrDefault(d => d.Id == instance.Device.Id)?.Name ?? DisplayDeviceName(instance.Device, state.Inventory);
            values.Add(new(new(proposal, instance.Generation), deviceName, reading.Value, reading.State, connected && reading.Value is not null));
        }
        // Keep disconnected selections anchored internally, without adding absent hardware back to the list.
        foreach (var pair in selected.Where(p => connectedTargets.Contains(p.Value.Selection.Item.Target) && values.All(c => Key(c) != p.Key)))
            values.Add(pair.Value with { Available = false, Value = null, Status = "再取得が必要" });
        candidates = values.ToArray();
        Page = Math.Min(Page, PageCount - 1);
    }
    public void Select(DeviceMonitoringCandidate candidate, bool value)
    {
        var key = Key(candidate);
        if (!value) { selected.Remove(key); return; }
        var current = candidates.SingleOrDefault(c => Key(c) == key);
        if (current?.Available != true) throw new InvalidOperationException("監視値を取得できる項目を選択してください");
        if (selected.ContainsKey(key)) return;
        if (!CanSelectMore) throw new InvalidOperationException($"{current.Selection.Item.Name}を追加できません。監視項目は{DeviceDashboard.MaximumSavedItems}件までです");
        if (!Dirty) { editedEpoch = epoch; editedConfiguration = configuration; editedTopology = topology; }
        selected[key] = current;
    }
    public void Reset() { selected.Clear(); Page = 0; }
    public void MovePage(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentException("ページ移動が不正です");
        Page = Math.Clamp(Page + direction, 0, PageCount - 1);
    }
    public DeviceMonitoringEdit Capture() => CanRegister ? new(editedEpoch, editedConfiguration, editedTopology, selected.Values.Select(c => c.Selection).ToArray()) :
        throw new InvalidOperationException(Conflict ? "選択後に機器または設定が変わりました。再取得してください" :
            selected.Count > RemainingSlots ? $"監視項目は{DeviceDashboard.MaximumSavedItems}件までです" : "監視項目を追加できません");
    public static bool Valid(DeviceMonitoringSelection[]? items) => items is { Length: > 0 and <= DeviceDashboard.MaximumSavedItems } &&
        items.All(i => i is { Generation: > 0, Item.Visible: true }) && DeviceDashboardDraft.Valid(new(2, items.Select(i => i.Item).ToArray()));
    public static string DisplayName(string name) => Clip(name, 80);
    public static string DisplayDeviceName(DeviceObservation device, DeviceInventory inventory)
    {
        if (!inventory.Devices.Any(d => d.Device.Id != device.Id && d.Device.Name == device.Name)) return DisplayName(device.Name);
        var suffix = device.Id.Length > 8 ? device.Id[^8..] : device.Id;
        return Clip(device.Name, 80 - suffix.Length - 3) + " · " + suffix;
    }
    private static string Clip(string name, int maximum) => name.Length <= maximum ? name : name[..(char.IsHighSurrogate(name[maximum - 1]) ? maximum - 1 : maximum)];
}
