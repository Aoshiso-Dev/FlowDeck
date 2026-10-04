using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceNameChoice(string id, string name) : Observable
{
    public string Id { get; } = id;
    public string Name { get; private set; } = name;
    public string Label { get; private set; } = name;
    public void Update(string value) { if (Name == value) return; Name = value; Notify(nameof(Name)); }
    public void UpdateLabel(string value) { if (Label == value) return; Label = value; Notify(nameof(Label)); }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public ObservableCollection<DeviceNameChoice> DeviceNameChoices { get; } = [];
    private readonly Dictionary<string, DeviceNameDraft> deviceNameDrafts = [];
    private DeviceNameChoice? selectedDeviceName;
    private bool reconcilingDeviceNames;
    public bool DeviceNamesSupported => client.SupportsDeviceNames;
    public bool DeviceNamesIdle => DeviceNamesSupported && SetupIdle;
    public DeviceNameDraft? SelectedDeviceNameDraft => selectedDeviceName is null ? null : deviceNameDrafts.GetValueOrDefault(selectedDeviceName.Id);
    public DeviceNameChoice? SelectedDeviceName
    {
        get => selectedDeviceName;
        set
        {
            if (reconcilingDeviceNames || !DeviceNamesIdle || value == selectedDeviceName || value is not null && !DeviceNameChoices.Contains(value)) return;
            selectedDeviceName = value; ClearRegistrationRemoval(); Notify(nameof(SelectedDeviceName)); Notify(nameof(EditedDeviceName)); PublishDeviceNames();
        }
    }
    public string EditedDeviceName
    {
        get => SelectedDeviceNameDraft?.Name ?? "";
        set
        {
            if (SelectedDeviceNameDraft is not { } draft || draft.Name == value) return;
            draft.Name = value; ClearRegistrationRemoval(); Notify(nameof(EditedDeviceName)); PublishDeviceNames();
        }
    }
    public bool HasDeviceName => SelectedDeviceNameDraft is not null;
    public bool CanSaveDeviceName => DeviceNamesIdle && SelectedDeviceNameDraft is { CanSave: true };
    public string DeviceNameStatus => SelectedDeviceNameDraft is not { } draft ? "" :
        draft.Problem ?? (draft.Dirty ? "未保存" : "保存済み");
    public ICommand SaveDeviceName => new AsyncCommand(_ => SaveDeviceNameAsync(), _ => CanSaveDeviceName, e => Notice = e.Message);
    public ICommand ReloadDeviceName => new AsyncCommand(_ => ReloadDeviceNameAsync(), _ => DeviceNamesIdle && HasDeviceName, e => Notice = e.Message);
    private void RenderDeviceNames(DeviceWorkspaceSnapshot snapshot)
    {
        var previousText = EditedDeviceName;
        var registered = snapshot.Configuration.Configuration.Devices.Devices;
        reconcilingDeviceNames = true;
        try
        {
            // Retain drafts across selection changes and keep a removed, edited device visible as a conflict.
            foreach (var old in DeviceNameChoices.Where(c => !registered.Any(d => d.Id == c.Id) && c != selectedDeviceName &&
                !deviceNameDrafts[c.Id].Dirty && !deviceNameDrafts[c.Id].Conflict).ToArray())
            { DeviceNameChoices.Remove(old); deviceNameDrafts.Remove(old.Id); }
            foreach (var device in registered)
            {
                var choice = DeviceNameChoices.SingleOrDefault(c => c.Id == device.Id);
                if (choice is null)
                {
                    deviceNameDrafts.Add(device.Id, new(device.Id));
                    DeviceNameChoices.Add(new(device.Id, device.Name));
                }
                else choice.Update(device.Name);
            }
            foreach (var group in DeviceNameChoices.GroupBy(c => c.Name, StringComparer.Ordinal))
            {
                static string Suffix(DeviceNameChoice c) => c.Id.Length > 8 ? c.Id[^8..] : c.Id;
                foreach (var choice in group)
                {
                    var suffix = Suffix(choice);
                    if (group.Any(c => c.Id != choice.Id && Suffix(c) == suffix)) suffix = choice.Id;
                    choice.UpdateLabel(group.Count() > 1 ? choice.Name + " · " + suffix : choice.Name);
                }
            }
            foreach (var draft in deviceNameDrafts.Values) draft.Update(snapshot);
            if (selectedDeviceName is null)
            { selectedDeviceName = DeviceNameChoices.FirstOrDefault(); Notify(nameof(SelectedDeviceName)); }
        }
        finally { reconcilingDeviceNames = false; }
        // Do not touch an IME-owned Text value on each telemetry update.
        if (EditedDeviceName != previousText) Notify(nameof(EditedDeviceName));
        PublishDeviceNames();
    }
    private void PublishDeviceNames()
    {
        foreach (var name in new[] { nameof(DeviceNamesSupported), nameof(DeviceNamesIdle), nameof(HasDeviceName),
            nameof(CanSaveDeviceName), nameof(DeviceNameStatus) }) Notify(name);
        PublishRegistrationRemoval(); CommandManager.InvalidateRequerySuggested();
    }
    public async Task SaveDeviceNameAsync()
    {
        Render(); if (!CanSaveDeviceName) return;
        var draft = SelectedDeviceNameDraft!; var edit = draft.Capture(); setupBusy = true; Render();
        try
        {
            var result = await client.SaveDeviceNameAsync(edit);
            ClearConfigurationPreviews(); draft.Reset(client.Snapshot!); Notice = result.Message;
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task ReloadDeviceNameAsync()
    {
        if (!DeviceNamesIdle || SelectedDeviceNameDraft is not { } draft) return;
        setupBusy = true; Render();
        try { await client.RefreshAsync(true); ClearRegistrationRemoval(); draft.Reset(client.Snapshot!); Notify(nameof(EditedDeviceName)); }
        finally { setupBusy = false; Render(); }
    }
}
