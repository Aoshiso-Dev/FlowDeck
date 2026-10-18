using System.Security.Cryptography;
using System.Text;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record HardwareSensorValue(string Id, string Name, string HardwareId, DeviceFeature Feature,
    double? Value, long Timestamp, string? Problem = null, DeviceMeasurementIssue Issue = DeviceMeasurementIssue.None);
public sealed record HardwareSensorDevice(string NativeId, string Name, DeviceKind Kind, string Fingerprint,
    string? SerialIdentity, string ConnectionId, string? DefinitionId, HardwareSensorValue[] Sensors, string? LocationIdentity = null,
    DeviceDiagnosticHardware? DiagnosticHardware = null);
public interface IHardwareSensorBackend : IAsyncDisposable
{
    Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct);
}

/// <summary>One hardware reader, with separate physical-device views. Reads are scheduled once for the whole provider.</summary>
public sealed partial class HardwareSensorProvider(string installation, IHardwareSensorBackend backend, MotherboardCoolingOptions? cooling = null) : IDeviceCacheRetirement
{
    public const string ProviderName = "lhm-sensors";
    private readonly object gate = new();
    private readonly SemaphoreSlim backendAccess = new(1, 1);
    private readonly Dictionary<string, IDeviceControlDriver> drivers = [];
    private Dictionary<string, HardwareSensorDevice> current = [];
    private bool disposed;
    private bool backendClosed;
    private long revision;
    internal long Revision { get { lock (gate) return revision; } }
    public string ProviderId => ProviderName;
    public string[] ResourceDomains => ["lhm", "isa-bus", "smbus"];
    public IDeviceControlDriver[] CachedDrivers { get { lock (gate) return drivers.Values.Cast<IDeviceControlDriver>().ToArray(); } }
    internal HardwareSensorDevice? Cached(string id) { lock (gate) return current.TryGetValue(id, out var d) ? Contract.Copy(d) : null; }
    public static string DeviceId(string installation, HardwareSensorDevice device) => DeviceIdentity.Create(installation, ProviderName,
        device.SerialIdentity is { } serial ? "serial:" + serial : device.LocationIdentity is { } location ? "location:" + location : "connection:" + device.ConnectionId);
    public async Task PollAsync(CancellationToken ct)
    {
        await backendAccess.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
            lock (gate) confirmedAbsent.Clear();
            var values = await backend.ReadDevicesAsync(ct).ConfigureAwait(false);
            if (values is null || values.Length > DeviceRegistry.MaximumDevices || values.Any(d => !Valid(d)))
                throw new IOException("センサー機器の取得形式が不正です");
            values = Contract.Copy(values);
            var next = values.Select(d => (Id: DeviceId(installation, d), Device: d)).ToArray();
            if (next.DistinctBy(d => d.Id).Count() != next.Length) throw new IOException("センサー機器の個体識別が重複しています");
            (string Id, HardwareSensorDevice Device)[] missing;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (next.Select(d => d.Id).Union(drivers.Keys).Count() > DeviceRegistry.MaximumDevices)
                {
                    current = next.Where(d => drivers.ContainsKey(d.Id)).ToDictionary(d => d.Id, d => d.Device); revision++;
                    confirmedAbsent = drivers.Values.Where(d => !current.ContainsKey(d.DeviceId)).ToHashSet();
                    throw new DeviceDiscoveryCapacityException("切断機器を含むセンサー機器数が上限に達しました。不要な検出履歴を整理してください");
                }
                missing = next.Where(d => !drivers.ContainsKey(d.Id)).ToArray();
            }
            // Recovery files can be slow. Do not hold the observation/cache lock while loading a newly discovered driver's journal.
            var discovered = missing.Select(d => (d.Id, Driver: cooling is not null && backend is IMotherboardCoolingBackend controller &&
                d.Device.Kind == DeviceKind.Motherboard && d.Device.DefinitionId is not null
                    ? (IDeviceControlDriver)new MotherboardDeviceDriver(this, d.Id, controller, cooling, d.Device) : new HardwareSensorDeviceDriver(this, d.Id))).ToArray();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                current = next.ToDictionary(d => d.Id, d => d.Device);
                revision++;
                foreach (var (id, driver) in discovered) drivers.TryAdd(id, driver);
                confirmedAbsent = drivers.Values.Where(d => !current.ContainsKey(d.DeviceId)).ToHashSet();
            }
        }
        catch (DeviceDiscoveryCapacityException) { throw; }
        catch
        {
            // Do not make an old value fresh when a read fails. Keep the known device and endpoints for diagnostics.
            lock (gate)
            {
                current = current.ToDictionary(p => p.Key, p => p.Value with
                    { Sensors = p.Value.Sensors.Select(s => s with { Value = null, Problem = "機器の取得に失敗しました", Issue = DeviceMeasurementIssue.Unavailable }).ToArray() });
                revision++;
            }
            throw;
        }
        finally { backendAccess.Release(); }
    }
    private static bool Valid(HardwareSensorDevice? d) => d is not null && DeviceConfigurationRules.Text(d.NativeId, 512) &&
        DeviceConfigurationRules.Text(d.Name, 160) && d.Kind is DeviceKind.Processor or DeviceKind.Graphics or DeviceKind.Motherboard &&
        d.Fingerprint is { Length: 64 } && d.Fingerprint.All(Uri.IsHexDigit) && DeviceServiceProtocol.Id(d.ConnectionId) &&
        (d.SerialIdentity is null || DeviceConfigurationRules.Text(d.SerialIdentity, 1024)) &&
        (d.LocationIdentity is null || d.LocationIdentity.Length == 64 && d.LocationIdentity.All(Uri.IsHexDigit)) &&
        (d.DefinitionId is null || d.Kind == DeviceKind.Motherboard && MotherboardDefinitionCatalog.BuiltIn.Boards.Any(b => b.Id == d.DefinitionId)) &&
        d.Sensors is { Length: <= DeviceRegistry.MaximumEndpoints } && d.Sensors.All(s => s is not null &&
            DeviceConfigurationRules.Text(s.Id, 512) && DeviceConfigurationRules.Text(s.Name, 160) && DeviceConfigurationRules.Text(s.HardwareId, 512) &&
            s.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm or DeviceFeature.DutyRead &&
            Enum.IsDefined(s.Issue) && (s.Issue == DeviceMeasurementIssue.None || s.Value is null) &&
            (s.Value is null || double.IsFinite(s.Value.Value)) && (s.Problem is null || DeviceConfigurationRules.Text(s.Problem, 512))) &&
        d.Sensors.DistinctBy(s => s.Id).Count() == d.Sensors.Length;
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (backendClosed) return; disposed = true; current.Clear(); }
        await backendAccess.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) { if (backendClosed) return; }
            await backend.DisposeAsync().ConfigureAwait(false);
            lock (gate) backendClosed = true;
        }
        finally { backendAccess.Release(); }
    }
}

public sealed class HardwareSensorDeviceDriver(HardwareSensorProvider owner, string deviceId) : IDeviceCurrentConnectionControlDriver, IDeviceTelemetrySource, ILegacyMigrationSource, IDeviceDiagnosticHardwareSource
{
    public string DeviceId => deviceId;
    public DeviceDiagnosticHardware ReadCachedDiagnosticHardware() => owner.Cached(DeviceId)?.DiagnosticHardware is { } value ?
        DeviceDiagnosticHardwareRules.Normalize(value) : new(DeviceDiagnosticReadState.NotObserved, []);
    public PhysicalDeviceBinding? BindingCandidate => owner.Cached(DeviceId) is { } d ? BindingFor(DeviceId, d) : null;
    public PhysicalDeviceBinding? CurrentControlBinding => BindingCandidate;
    public bool AllowsCurrentConnectionControl(string endpointId, DeviceFeature feature) =>
        feature == DeviceFeature.Temperature && owner.Cached(DeviceId) is { } current && current.Sensors.Any(s =>
            s.Feature == feature && Endpoint(current, s).Id == endpointId && s.Issue == DeviceMeasurementIssue.None &&
            s.Problem is null && s.Value is { } value && double.IsFinite(value));
    public void ConfigureCurrentConnection(PhysicalDeviceBinding currentBinding, PhysicalDeviceBinding? savedBinding,
        IEnumerable<CoolingEndpointConfiguration> cooling, IEnumerable<LightingEndpointConfiguration> lighting,
        IEnumerable<RegisteredEndpoint> endpoints)
    {
        // This is a cached temperature source, never an output controller. The runtime checks
        // freshness with its own clock before accepting the reading as an input to a policy.
        if (currentBinding != BindingCandidate) throw new IOException("温度センサーの現在接続が変更されています");
    }
    internal static PhysicalDeviceBinding BindingFor(string id, HardwareSensorDevice d) =>
        new(id, d.SerialIdentity is { } serial ? "serial:" + serial : d.LocationIdentity is { } location ? "location:" + location :
            "connection:" + d.ConnectionId, d.DefinitionId ?? "lhm-monitor-v1", d.Fingerprint);
    private static string SensorEndpoint(string id) => "sensor:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
    private static (string Id, string Name, EndpointKind Kind) Endpoint(HardwareSensorDevice device, HardwareSensorValue sensor)
    {
        // Only the packaged definition relates a fan/control index to an electrical channel. Unknown boards remain independent readings.
        var definition = MotherboardDefinitionCatalog.BuiltIn.Boards.SingleOrDefault(b => b.Id == device.DefinitionId);
        var port = definition?.Ports.SingleOrDefault(p => p.HardwareId == sensor.HardwareId &&
            (sensor.Feature == DeviceFeature.Rpm && p.SensorId == sensor.Id ||
             sensor.Feature == DeviceFeature.DutyRead && sensor.Id == p.HardwareId + "/control/" + p.Channel));
        var relative = sensor.Id.StartsWith(device.NativeId + "/", StringComparison.Ordinal) ? sensor.Id[(device.NativeId.Length + 1)..] : sensor.Id;
        return port is null ? (SensorEndpoint(relative), sensor.Name, EndpointKind.Sensor) : (port.Id, port.Name, EndpointKind.Cooling);
    }
    public DeviceObservation? Observe()
    {
        if (owner.Cached(DeviceId) is not { } d) return null;
        var endpoints = d.Sensors.GroupBy(s => Endpoint(d, s)).Select(group => new DeviceEndpoint(group.Key.Id, group.Key.Name, group.Key.Kind,
            group.Select(s => new DeviceCapability(s.Feature, ImplementationSupport.Supported, VerificationEvidence.Automated,
                s.Issue == DeviceMeasurementIssue.None && s.Problem is null && s.Value is not null ? DeviceAvailability.Ready : DeviceAvailability.Failed,
                s.Problem ?? (s.Value is null ? "測定値を取得できません" : null))).ToArray(), group.FirstOrDefault(s => s.Feature == DeviceFeature.Rpm)?.Id ?? group.First().Id,
            group.Any(s => s.Feature == DeviceFeature.Rpm) ? "fan" : group.Any(s => s.Feature == DeviceFeature.Temperature) ? "temperature" : null)).ToArray();
        return new(DeviceId, HardwareSensorProvider.ProviderName, d.Name, d.Kind,
            d.SerialIdentity is null ? IdentityConfidence.ConnectionBound : IdentityConfidence.SerialBound,
            d.NativeId + ":" + d.ConnectionId + ":" + d.Fingerprint, d.DefinitionId, owner.ResourceDomains, endpoints);
    }
    public DeviceSample[] ReadCachedSamples() => owner.Cached(DeviceId) is { } d ? d.Sensors.Select(s =>
        new DeviceSample(Endpoint(d, s).Id, s.Feature, s.Value, s.Timestamp, s.Problem, s.Issue)).ToArray() : [];
    public MigratedReference[] LegacyReferences()
    {
        if (owner.Cached(DeviceId) is not { } d) return [];
        var result = new List<MigratedReference>();
        foreach (var sensor in d.Sensors)
        {
            var endpoint = Endpoint(d, sensor); var target = new EndpointReference(DeviceId, endpoint.Id);
            if (sensor.Feature == DeviceFeature.Temperature) result.Add(new("temperature", "local", sensor.Id, target));
            if (d.Kind != DeviceKind.Motherboard) continue;
            result.Add(new("board-sensor", "motherboard", sensor.Id, target));
            if (sensor.Feature == DeviceFeature.Rpm) result.Add(new("rpm", "motherboard", sensor.Id, target));
            if (sensor.Feature is DeviceFeature.Temperature or DeviceFeature.Rpm) result.Add(new("notification", d.Name, sensor.Id, target));
            if (endpoint.Kind == EndpointKind.Cooling) result.Add(new("board-control", "motherboard", endpoint.Id, target));
        }
        return result.Distinct().ToArray();
    }
    public string? Validate(DeviceEndpointAction action) => "この機器はセンサー取得にのみ対応しています";
    public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => Task.FromResult(new DeviceDriverResult(Outcome.Rejected, Validate(action)!));
    public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Task.FromResult(Array.Empty<DeviceDriverReleaseResult>());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
