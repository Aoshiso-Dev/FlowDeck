using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver : IIndividualCoolingConfiguration
{
    public void ConfigureCoolingEndpoint(PhysicalDeviceBinding expected, string endpointId, CoolingEndpointConfiguration? configuration)
    {
        var value = configuration is null ? null : Contract.Copy(configuration);
        if (!DefinitionData.Cooling.Endpoints.Contains(endpointId) || value is not null &&
            (value.Target != new EndpointReference(DeviceId, endpointId) || !DeviceConfigurationRules.ValidSafety(value.Safety)))
            throw new ArgumentException("Hubの冷却端子設定が不正です");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (expected != binding || expected.DeviceId != DeviceId || value is not null && (expected != candidate || !connected))
                throw new IOException("Hubの実機関連付けが変更されています");
            if (calibrating.Contains(endpointId) || journal.Pending.Any(p => p.Channel == endpointId))
                throw new IOException("変更する端子の制御を終了してください");
            if (value is null) cooling.Remove(endpointId); else cooling[endpointId] = value;
        }
    }
}

public sealed partial class MotherboardDeviceDriver : IIndividualCoolingConfiguration
{
    public void ConfigureCoolingEndpoint(PhysicalDeviceBinding expected, string endpointId, CoolingEndpointConfiguration? configuration)
    {
        var value = configuration is null ? null : Contract.Copy(configuration);
        if (!io.Wait(0)) throw new IOException("端子の処理中は設定を変更できません");
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var current = owner.Cached(DeviceId);
                var definition = Definition ?? MotherboardDefinitionCatalog.BuiltIn.Boards.SingleOrDefault(b => b.Id == identity?.DefinitionId);
                var port = definition?.Ports.SingleOrDefault(p => p.Id == endpointId);
                if (port is null || value is not null && (value.Target != new EndpointReference(DeviceId, endpointId) ||
                    !DeviceConfigurationRules.ValidSafety(value.Safety))) throw new ArgumentException("マザーの冷却端子設定が不正です");
                if (expected != binding || journal is null || recoveryError is not null || value is not null && (expected != BindingCandidate || current is null ||
                    identity != MotherboardCoolingIdentity.From(current)))
                    throw new IOException("マザーの実機関連付け・復旧記録を確認してください");
                if (calibrating.Contains(endpointId) || attempted.Contains(endpointId) ||
                    journal.Pending.Any(p => p.Chip == port.Chip && p.Channel == port.Channel))
                    throw new IOException("変更する端子の制御を終了してください");
                if (value is null) cooling.Remove(endpointId); else cooling[endpointId] = value;
            }
        }
        finally { io.Release(); }
    }
}
