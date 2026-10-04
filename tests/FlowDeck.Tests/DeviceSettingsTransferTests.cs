using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceSettingsTransferTests
{
    private static async Task<DeviceOperationResult> Duty(DeviceWorkspaceClient client,EndpointReference target,int value)
    {
        await client.RefreshAsync(true); var snapshot=client.Snapshot!; var draft=new DeviceCoolingDraft(target);
        draft.Update(snapshot.State,snapshot.Configuration.Generation,snapshot.Configuration.Configuration.Cooling.Endpoints.Single(e=>e.Target==target),snapshot.ElapsedSeconds,true);
        draft.SetDuty(value); return await client.ApplyCoolingAsync(draft.Capture());
    }
    private static DeviceConfigurationPackage Rich(DeviceConfigurationPackage source)
    {
        var lights=source.Lighting.Endpoints!.Select(e=>e.Target).ToArray(); var a=lights[0]; var b=lights[1];
        var blue=new LightingProgram("fixed",[new(20,40,200)]); var breathing=new LightingProgram("breathing",[new(200,30,40)]);
        var scene=new DeviceLightingScene("scene","机の照明",[new(a,blue)],[new([b],breathing)]);
        var cpu=source.Cooling.Endpoints[0].Profiles[0].Curves[0].Sensor;
        var next=source with {
            Cooling=source.Cooling with { Groups=[new("fans","ケース",EndpointKind.Cooling,source.Cooling.Endpoints.Take(2).Select(e=>e.Target).ToArray())],ProfileTemplates=source.Cooling.Endpoints[0].Profiles },
            Lighting=source.Lighting with { Scenes=[scene],Groups=[new("lights","机",EndpointKind.Lighting,lights)],Names=[new(a,"前面"),new(b,"背面")],SyncTargets=[b],
                Preferences=[new(a,blue),new(b,breathing)],SavedSynchronization=[new([b],breathing)],SavedFrameGroups=[[a],[b]],
                TargetAdjustments=[new(a,blue,new(),new(),new()),new(b,breathing,new(),new(),new())],Adjustments=[new("lights",100,false,scene)] },
            Preferences=source.Preferences with { Notifications=new(Rules:[new(Guid.NewGuid().ToString("N"),cpu,DeviceFeature.Temperature,"CPU過熱",90)]),
                M5=new(Port:"COM14",DeviceId:"panel",Transport:"auto",WifiAddress:"192.168.1.19"),Ui=new("dark",50,60) } };
        DeviceConfigurationCodec.Validate(next); return next;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ArchivesRoundTripOnlyTypedSettingsAndPortableExportsClearMachineConfirmations(bool portable)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var source=Rich(client.Snapshot!.Configuration.Configuration);
        var archive=DeviceSettingsArchives.Create(source,portable); var restored=DeviceSettingsArchives.Read(DeviceSettingsArchives.Write(archive));
        Assert.Equal(SettingsTransfer.Hash(archive),SettingsTransfer.Hash(restored)); Assert.Empty(restored.Configuration.Migration.References);
        Assert.Equal("机の照明",Assert.Single(restored.Configuration.Lighting.Scenes).Name);
        Assert.All(restored.Configuration.Cooling.Endpoints,e=>Assert.Equal(!portable,e.WiringConfirmed));
        if(portable) { Assert.Empty(restored.Configuration.Bindings.Devices); Assert.Null(restored.Configuration.Bindings.InstallationId); Assert.Null(restored.Configuration.Preferences.M5.Port); Assert.Null(restored.Configuration.Preferences.Ui.Left); }
        else { Assert.NotEmpty(restored.Configuration.Bindings.Devices); Assert.Equal("COM14",restored.Configuration.Preferences.M5.Port); }
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Theory] [InlineData("digest")] [InlineData("duplicate")] [InlineData("unknown")] [InlineData("legacy")] [InlineData("version")] [InlineData("large")]
    public async Task CorruptAmbiguousOversizeOrLegacyArchivesAreRejected(string problem)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var archive=await client.ExportSettingsAsync(false);
        var json=Encoding.UTF8.GetString(DeviceSettingsArchives.Write(archive));
        var bytes=problem switch {
            "digest"=>JsonSerializer.SerializeToUtf8Bytes(archive with { Sha256=new('f',64) },Contract.Json),
            "duplicate"=>Encoding.UTF8.GetBytes(json.Insert(json.IndexOf('{')+1,"\"Version\":1,")),
            "unknown"=>Encoding.UTF8.GetBytes(json.Insert(json.IndexOf('{')+1,"\"driverDll\":\"foreign.dll\",")),
            "legacy"=>Encoding.UTF8.GetBytes("{\"version\":1,\"settings\":{}}"),
            "version"=>JsonSerializer.SerializeToUtf8Bytes(archive with { Version=2 },Contract.Json),_=>new byte[DeviceSettingsArchives.MaximumBytes+1] };
        Assert.Throws<IOException>(()=>DeviceSettingsArchives.Read(bytes)); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task FileExportIsAtomicAndCancellationPreservesAnExistingBackup()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(Path.Combine(dir.Path,"demo"));
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var archive=await client.ExportSettingsAsync(false,new("dark"));
        var path=Path.Combine(dir.Path,"backup.flowdeck.json"); await DeviceSettingsArchives.SaveAsync(path,archive); var bytes=await File.ReadAllBytesAsync(path);
        Assert.Equal("dark",(await DeviceSettingsArchives.ReadAsync(path)).Configuration.Preferences.Ui.Theme);
        using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>DeviceSettingsArchives.SaveAsync(path,archive,ct:cancel.Token));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path)); Assert.Empty(Directory.GetFiles(dir.Path,".flowdeck-export-*.tmp"));
        await Assert.ThrowsAsync<IOException>(()=>DeviceSettingsArchives.SaveAsync(Path.Combine(dir.Path,"settings.json"),archive));
    }
    [Fact] public async Task SamePcRestoreRetainsOnlyMatchingConfirmationsAndUsesPreviousExitWithoutReplayingControl()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan1");
        Assert.Equal(Outcome.Confirmed,(await Duty(client,target,63)).Outcome); await client.RefreshAsync(true);
        var source=client.Snapshot!.Configuration.Configuration; source=source with { Cooling=source.Cooling with { Endpoints=source.Cooling.Endpoints.Select(e=>e.Target==target?e with { Safety=e.Safety with { Exit=85 } }:e).ToArray() } };
        var draft=new DeviceSettingsImportDraft(client.Snapshot,DeviceSettingsArchives.Create(source,false));
        Assert.True(draft.SameInstallation); Assert.Equal(draft.Endpoints.Length,draft.MappedCount); Assert.Throws<InvalidOperationException>(()=>draft.Capture());
        draft.Confirmed=true; var preview=await client.PreviewImportAsync(draft.Capture()); Assert.Equal(63,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Equal(Outcome.Confirmed,(await client.ApplyImportAsync(preview)).Outcome);
        var saved=client.Snapshot!.Configuration.Configuration; Assert.Equal(70,(await demo.Hubs[0].ReadAsync()).Channels[0].Duty);
        Assert.Equal(85,saved.Cooling.Endpoints.Single(e=>e.Target==target).Safety.Exit); Assert.All(saved.Cooling.Endpoints,e=> { Assert.True(e.WiringConfirmed); Assert.Null(e.SavedPolicy); Assert.False(e.ResumeOnStart); });
        Assert.All(client.Snapshot.State.Endpoints,e=>Assert.False(e.Owned));
    }
    [Fact] public async Task PortableRemappingUpdatesCurvesScenesGroupsNotificationsAndAllLightingLayersTogether()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var source=Rich(client.Snapshot!.Configuration.Configuration);
        var draft=new DeviceSettingsImportDraft(client.Snapshot,DeviceSettingsArchives.Create(source,true)); Assert.False(draft.SameInstallation); Assert.Equal(0,draft.MappedCount);
        var sensors=source.Devices.Devices.SelectMany(d=>d.Endpoints.Where(e=>e.Kind==EndpointKind.Sensor).Select(e=>new EndpointReference(d.Id,e.Id))).ToArray();
        var lights=source.Lighting.Endpoints!.Select(e=>e.Target).ToArray();
        foreach(var row in draft.Endpoints) draft.Map(row.Source,row.Source==sensors[0]?sensors[1]:row.Source==sensors[1]?sensors[0]:row.Source==lights[0]?lights[1]:row.Source==lights[1]?lights[0]:row.Source);
        draft.Confirmed=true; var next=draft.Capture().Configuration; Assert.Empty(next.Bindings.Devices);
        Assert.All(next.Cooling.Endpoints,e=> { Assert.False(e.WiringConfirmed); Assert.Equal(sensors[1],e.Profiles[0].Curves[0].Sensor); Assert.Equal(sensors[1],e.Safety.ManualProtection[0].Sensor); });
        Assert.Equal(sensors[1],next.Cooling.ProfileTemplates![0].Curves[0].Sensor); Assert.Equal(sensors[1],next.Preferences.Notifications.Rules![0].Target);
        var scene=next.Lighting.Scenes[0]; Assert.Equal(lights[1],scene.Individual[0].Target); Assert.Equal(lights[0],scene.Synchronized[0].Targets[0]);
        Assert.Equal(lights[1],next.Lighting.Adjustments[0].Baseline!.Individual[0].Target); Assert.Equal(lights[1],next.Lighting.TargetAdjustments![0].Target);
        Assert.Equal(lights[0],next.Lighting.SavedSynchronization![0].Targets[0]); Assert.Equal(lights[1],next.Lighting.SavedFrameGroups![0][0]);
        Assert.All(next.Lighting.Endpoints!,e=>Assert.False(e.WiringConfirmed)); Assert.All(next.Devices.Devices.SelectMany(d=>d.Endpoints).Where(e=>e.Setup is not null),e=>Assert.False(e.Setup!.LedCountConfirmed));
        Assert.Equal(Outcome.Confirmed,(await client.ApplyImportAsync(await client.PreviewImportAsync(draft.Capture()))).Outcome);
        Assert.All(demo.Hubs,h=> { Assert.Equal(0,h.Writes); Assert.Equal(0,h.LightingWrites); });
    }
    [Fact] public async Task UnmappedEndpointsStayDistinctAndCannotCollideWithLiveHardwareOrGrantControl()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var source=client.Snapshot!.Configuration.Configuration;
        var draft=new DeviceSettingsImportDraft(client.Snapshot,DeviceSettingsArchives.Create(source,true)); draft.Confirmed=true;
        var next=draft.Capture().Configuration;
        Assert.Empty(next.Bindings.Devices); Assert.Equal(source.Devices.Devices.Sum(d=>d.Endpoints.Length),next.Devices.Devices.Sum(d=>d.Endpoints.Length));
        Assert.All(next.Devices.Devices,d=>Assert.DoesNotContain(source.Devices.Devices,o=>o.Id==d.Id));
        Assert.Equal(Outcome.Confirmed,(await client.ApplyImportAsync(await client.PreviewImportAsync(draft.Capture()))).Outcome);
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task WrongKindsAndDuplicateDestinationsCannotChangeAMapping()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var draft=new DeviceSettingsImportDraft(client.Snapshot!,await client.ExportSettingsAsync(true));
        var fan=draft.Endpoints.First(e=>e.Kind==EndpointKind.Cooling); var second=draft.Endpoints.Last(e=>e.Kind==EndpointKind.Cooling);
        var sensor=draft.Endpoints.First(e=>e.Kind==EndpointKind.Sensor);
        Assert.Throws<InvalidOperationException>(()=>draft.Map(sensor.Source,fan.Source)); Assert.Null(draft.Mapping(sensor.Source));
        draft.Map(fan.Source,fan.Source); Assert.Throws<InvalidOperationException>(()=>draft.Map(second.Source,fan.Source)); Assert.Null(draft.Mapping(second.Source));
    }
    [Theory] [InlineData("epoch")] [InlineData("configuration")] [InlineData("topology")] [InlineData("device")] [InlineData("binding")]
    public async Task NewEpochConfigurationTopologyOrPhysicalGenerationKeepsTheDraftButRequiresReload(string change)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!;
        var draft=new DeviceSettingsImportDraft(snapshot,await client.ExportSettingsAsync(false)); draft.Confirmed=true; var id=demo.DeviceIds[0];
        var changed=snapshot with { Configuration=snapshot.Configuration with { Generation=change=="configuration"?Guid.NewGuid().ToString("N"):snapshot.Configuration.Generation,
            Candidates=snapshot.Configuration.Candidates.Select(b=>change=="binding"&&b.DeviceId==id?b with { Fingerprint=new('f',64) }:b).ToArray() },
            State=snapshot.State with { Inventory=snapshot.State.Inventory with { Epoch=change=="epoch"?Guid.NewGuid().ToString("N"):snapshot.State.Inventory.Epoch,
                TopologyRevision=snapshot.State.Inventory.TopologyRevision+(change=="topology"?1:0),Devices=snapshot.State.Inventory.Devices.Select(d=>change=="device"&&d.Device.Id==id?d with { Generation=d.Generation+1 }:d).ToArray() } } };
        draft.Update(changed); Assert.True(draft.Conflict); Assert.Throws<InvalidOperationException>(()=>draft.Capture()); draft.Update(snapshot); Assert.True(draft.Conflict);
        Assert.Equal(draft.Endpoints.Length,draft.MappedCount); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Theory] [InlineData("fingerprint")] [InlineData("definition")]
    public async Task SamePcDifferentPhysicalDeviceOrDefinitionKeepsReferencesButRevokesOnlyItsConfirmations(string change)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!; var source=snapshot.Configuration.Configuration; var id=demo.DeviceIds[0];
        source=source with { Bindings=source.Bindings with { Devices=source.Bindings.Devices.Select(b=>b.DeviceId!=id?b:b with {
            Fingerprint=change=="fingerprint"?new('f',64):b.Fingerprint,DefinitionId=change=="definition"?"obsolete-definition":b.DefinitionId }).ToArray() } };
        var draft=new DeviceSettingsImportDraft(snapshot,DeviceSettingsArchives.Create(source,false)) { Confirmed=true }; var next=draft.Capture().Configuration;
        Assert.Equal(draft.Endpoints.Length,draft.MappedCount); Assert.DoesNotContain(next.Bindings.Devices,b=>b.DeviceId==id);
        Assert.All(next.Cooling.Endpoints,e=>Assert.Equal(e.Target.DeviceId!=id,e.WiringConfirmed));
        Assert.False(next.Devices.Devices.Single(d=>d.Id==id).Endpoints.Single(e=>e.Id=="led1").Setup!.LedCountConfirmed);
        Assert.True(next.Devices.Devices.Single(d=>d.Id==demo.DeviceIds[1]).Endpoints.Single(e=>e.Id=="led1").Setup!.LedCountConfirmed);
        Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task MonitoringBackupReusesStableReferencesWithoutInventingControlBindings()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var snapshot=client.Snapshot!;
        var source=DeviceConfigurationCodec.Portable(snapshot.Configuration.Configuration);
        source=source with { Bindings=source.Bindings with { InstallationId=snapshot.Configuration.Configuration.Bindings.InstallationId } };
        var draft=new DeviceSettingsImportDraft(snapshot,DeviceSettingsArchives.Create(source,false)) { Confirmed=true }; var next=draft.Capture().Configuration;
        Assert.Equal(draft.Endpoints.Length,draft.MappedCount); Assert.Empty(next.Bindings.Devices);
        Assert.Equal(source.Dashboard.Items.Select(i=>i.Target),next.Dashboard.Items.Select(i=>i.Target));
        Assert.All(next.Cooling.Endpoints,e=>Assert.False(e.WiringConfirmed));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AReplacedPreviewOrChangedConfigurationCannotBeApplied(bool changeConfiguration)
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync();
        var draft=new DeviceSettingsImportDraft(client.Snapshot!,await client.ExportSettingsAsync(false)) { Confirmed=true }; var edit=draft.Capture();
        var preview=await client.PreviewImportAsync(edit);
        if(changeConfiguration)
        {
            var snapshot=client.Snapshot!; var dashboard=snapshot.Configuration.Configuration.Dashboard;
            await client.SaveDashboardAsync(new(snapshot.State.Inventory.Epoch,snapshot.Configuration.Generation,dashboard with {
                Items=dashboard.Items.Select((item,i)=>i==0?item with { Name="新しい名前" }:item).ToArray() }));
        }
        else await client.PreviewImportAsync(edit);
        var generation=client.Snapshot!.Configuration.Generation;
        await Assert.ThrowsAsync<IOException>(()=>client.ApplyImportAsync(preview));
        Assert.Equal(generation,client.Snapshot!.Configuration.Generation); Assert.All(demo.Hubs,h=>Assert.Equal(0,h.Writes));
    }
    [Fact] public async Task FailedExitLeavesTheAdoptedConfigurationAndReportsActualPostReleaseState()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var target=new EndpointReference(demo.DeviceIds[0],"fan3");
        Assert.Equal(Outcome.Confirmed,(await Duty(client,target,63)).Outcome);
        var snapshot=client.Snapshot!; var source=snapshot.Configuration.Configuration;
        source=source with { Dashboard=source.Dashboard with { Items=source.Dashboard.Items.Select(i=>i with { Name="復元する名前" }).ToArray() } };
        var draft=new DeviceSettingsImportDraft(snapshot,DeviceSettingsArchives.Create(source,false)) { Confirmed=true };
        var preview=await client.PreviewImportAsync(draft.Capture()); demo.Hubs[0].FailNextFanWrite=true;
        await Assert.ThrowsAsync<IOException>(()=>client.ApplyImportAsync(preview));
        Assert.Equal(snapshot.Configuration.Generation,client.Snapshot!.Configuration.Generation);
        Assert.Equal(SettingsTransfer.Hash(snapshot.Configuration.Configuration),SettingsTransfer.Hash(client.Snapshot.Configuration.Configuration));
        Assert.NotNull(client.Snapshot.State.LifecycleError); Assert.Equal(63,(await demo.Hubs[0].ReadAsync()).Channels[2].Duty);
    }
    [Fact] public async Task ReviewIncludesRemovedGroupsLedConfirmationAndLightingExitColor()
    {
        using var dir=new ServiceTestDirectory(); await using var demo=await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client=demo.CreateClient(); await client.ConnectAsync(); var original=Rich(client.Snapshot!.Configuration.Configuration);
        var target=original.Lighting.Endpoints![0].Target;
        var changed=original with { Lighting=original.Lighting with { Groups=[],Adjustments=[],Endpoints=original.Lighting.Endpoints.Select(e=>e.Target==target?
            e with { WiringConfirmed=false,Exit=new(LightingExitAction.FixedColor,new(255,20,30)) }:e).ToArray() },Devices=original.Devices with {
                Devices=original.Devices.Devices.Select(d=>d.Id!=target.DeviceId?d:d with { Endpoints=d.Endpoints.Select(e=>e.Id!=target.EndpointId?e:e with {
                    Setup=e.Setup! with { LedCount=16,LedCountConfirmed=false } }).ToArray() }).ToArray() } };
        DeviceConfigurationCodec.Validate(changed); var lines=DeviceSettingsDifferences.Describe(original,changed);
        Assert.Contains(lines,l=>l=="グループ削除: 机"); Assert.Contains(lines,l=>l.Contains("LED数 16・再確認"));
        Assert.Contains(lines,l=>l.Contains("消灯 → 固定 #FF141E")&&l.Contains("再確認"));
    }
}
