namespace FlowDeck.Core;

public sealed record CoolingGroupChange(string Id,long ExpectedRevision,EndpointGroup? Group);
public sealed record CoolingGroupSelection(string ExpectedGeneration,string Id,long ExpectedRevision);
public sealed record CoolingGroupPlan(long CatalogRevision,EndpointGroup Group);
public interface IDeviceCoolingGroups : IDeviceStartupConfigurationHandler
{
    CoolingGroupPlan ResolveCoolingGroup(CoolingGroupSelection selection);
    string? CheckCoolingGroup(CoolingGroupPlan plan);
}
public static class DeviceCoolingGroups
{
    public static bool Valid(EndpointGroup? group)=>group is { Kind:EndpointKind.Cooling,Revision:>=1,Targets.Length:>0 and <=64 }&&
        DeviceConfigurationRules.ProfileId(group.Id)&&DeviceConfigurationRules.Text(group.Name,80)&&
        group.Targets.All(DeviceConfigurationRules.ValidReference)&&group.Targets.Distinct().Count()==group.Targets.Length;
    public static bool Valid(CoolingGroupChange? change)=>change is not null&&DeviceConfigurationRules.ProfileId(change.Id)&&change.ExpectedRevision>=0&&
        (change.Group is null?change.ExpectedRevision>0:Valid(change.Group)&&change.Group.Id==change.Id);
    public static bool Valid(CoolingGroupSelection? selection)=>selection is not null&&DeviceServiceProtocol.Id(selection.ExpectedGeneration)&&
        DeviceConfigurationRules.ProfileId(selection.Id)&&selection.ExpectedRevision>=1;
    public static bool Operation(DeviceOperation operation)=>operation is DeviceOperation.SetDuty or DeviceOperation.SetAutomatic or DeviceOperation.ReleaseCooling or DeviceOperation.ResumeCooling;
}
