using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Builds migration references from compiled driver caches. Missing or ambiguous legacy addresses remain unbound drafts.</summary>
public static class LegacyMigrationPreparationBuilder
{
    private sealed record Required(string Source, string Device, string Endpoint, EndpointKind? Kind, DeviceFeature? Measurement = null)
    { public (string, string, string) Key => (Source, Device, Endpoint); }
    private sealed record Captured(IDeviceControlDriver Driver, DeviceObservation Observation, PhysicalDeviceBinding? Binding, MigratedReference[] Aliases);
    public static LegacyMigrationPreparation Prepare(string installation, IReadOnlyDictionary<string, byte[]> legacyFiles,
        IEnumerable<IDeviceControlDriver> drivers, bool sameInstallation, DashboardItem[]? defaultDashboard = null)
    {
        if (!DeviceServiceProtocol.Id(installation)) throw new ArgumentException("インストール識別子が不正です");
        var files = legacyFiles.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var content = SettingsTransfer.FromFiles(files);
        var settings = files.TryGetValue("settings.json", out var data) ? JsonSerializer.Deserialize<AppSettings>(data, Contract.Json)! : new();
        var required = Requirements(settings, content, defaultDashboard);
        var sources = drivers.Take(DeviceRegistry.MaximumDevices + 1).ToArray();
        if (sources.Length > DeviceRegistry.MaximumDevices || sources.Any(d => d is null) || sources.DistinctBy(d => d.DeviceId).Count() != sources.Length)
            throw new IOException("移行用の機器一覧が重複または上限を超えています");
        var captured = new List<Captured>();
        foreach (var driver in sources)
        {
            if (driver.Observe() is not { } observation) continue;
            var binding = (driver as IPhysicalDeviceDriver)?.BindingCandidate;
            var aliases = (driver as ILegacyMigrationSource)?.LegacyReferences() ?? [];
            var after = driver.Observe();
            if (after is null || observation.Id != driver.DeviceId || after.Locator != observation.Locator || after.DefinitionId != observation.DefinitionId ||
                !after.Endpoints.Select(e => (e.Id, e.Kind, e.SensorId)).SequenceEqual(observation.Endpoints.Select(e => (e.Id, e.Kind, e.SensorId))) ||
                binding != (driver as IPhysicalDeviceDriver)?.BindingCandidate) throw new IOException("機器取得中に接続・構成が変更されました");
            if (aliases.Length > 8192 || aliases.Any(a => a is null || a.Target is null || a.Target.DeviceId != driver.DeviceId ||
                !DeviceConfigurationRules.Text(a.Source, 64) || !DeviceConfigurationRules.Text(a.LegacyDeviceId, 2048) ||
                !DeviceConfigurationRules.Text(a.LegacyEndpointId, 512) || !observation.Endpoints.Any(e => e.Id == a.Target.EndpointId)))
                throw new IOException("ドライバーの旧設定対応表が不正です");
            captured.Add(new(driver, Contract.Copy(observation), binding, Contract.Copy(aliases)));
        }
        _ = new DeviceRegistry().Reconcile(captured.Select(c => c.Observation));
        var registrations = captured.ToDictionary(c => c.Observation.Id, c => new RegisteredDevice(c.Observation.Id, c.Observation.Name,
            c.Observation.Provider, c.Observation.Kind, c.Observation.Endpoints.Select(e => new RegisteredEndpoint(e.Id, e.Name, e.Kind, e.Role ?? "")).ToArray()));
        var bindings = captured.Select(c => c.Binding).OfType<PhysicalDeviceBinding>().ToArray();
        var references = new List<MigratedReference>(); var unresolved = new List<MigratedReference>();
        foreach (var need in required)
        {
            var matches = captured.SelectMany(c => c.Aliases.Where(a => !need.Endpoint.StartsWith("unassigned:", StringComparison.Ordinal) && (a.Source, a.LegacyDeviceId, a.LegacyEndpointId) == need.Key)
                .Where(a => c.Observation.Endpoints.Any(e => e.Id == a.Target.EndpointId &&
                    (need.Kind is null ? e.Kind is EndpointKind.Sensor or EndpointKind.Cooling : e.Kind == need.Kind) &&
                    (need.Measurement is null || e.Capabilities.Any(cap => cap.Feature == need.Measurement)))))
                .DistinctBy(a => a.Target).ToArray();
            MigratedReference reference;
            if (matches.Length == 1) reference = matches[0];
            else
            {
                var category = need.Source switch { "hub" => "hubs", "rgb" or "rgb-zone-setting" => "lighting", "temperature" => "temperatures", _ => "motherboard" };
                var id = DeviceIdentity.Create(installation, "legacy-unbound", category);
                var normalizedSource = need.Source is "board-sensor" or "rpm" ? "board-sensor" : need.Source;
                var endpoint = "legacy:" + SettingsTransfer.Hash(new { Source = normalizedSource, need.Device, need.Endpoint });
                var target = new EndpointReference(id, endpoint);
                reference = new(need.Source, need.Device, need.Endpoint, target);
                if (!registrations.TryGetValue(id, out var unbound))
                    unbound = new(id, "再関連付け待ち / " + category, "legacy-unbound", DeviceKind.Other, []);
                if (!unbound.Endpoints.Any(e => e.Id == endpoint)) unbound = unbound with {
                    Endpoints = [.. unbound.Endpoints, new(endpoint, MotherboardDisplay.ShortText(need.Endpoint, 160), need.Kind ?? EndpointKind.Sensor)] };
                registrations[id] = unbound; unresolved.Add(reference);
            }
            references.Add(reference);
        }
        var cooling = new List<EndpointReference>(); var lighting = new List<EndpointReference>();
        if (sameInstallation)
        foreach (var source in captured.Where(c => c.Driver is HubDeviceDriver && c.Binding is not null))
        {
            if (((HubDeviceDriver)source.Driver).LegacySetupEvidence(settings, source.Binding!) is not { } status) continue;
            cooling.AddRange(references.Where(r => r.Source == "hub" && r.Target.DeviceId == source.Observation.Id &&
                source.Observation.Endpoints.Any(e => e.Id == r.Target.EndpointId && e.Kind == EndpointKind.Cooling)).Select(r => r.Target));
            foreach (var detail in settings.Hardware.LedDetails ?? [])
            {
                var actual = status.Lighting.SingleOrDefault(l => l.Id == detail.Id);
                if (actual is null || actual.Accessory != detail.Accessory || actual.LedCount is { } count && detail.LedCount != count) continue;
                lighting.AddRange(references.Where(r => r.Source == "hub" && r.Target.DeviceId == source.Observation.Id && r.Target.EndpointId == detail.Id).Select(r => r.Target));
            }
        }
        // v1 stores raw sensor IDs and board wiring flags, but no historical physical fingerprints for them.
        var matching = new LegacyMigrationBindings(new(2, registrations.Values.ToArray()), new(2, installation, bindings), references.ToArray(),
            cooling.Distinct().ToArray(), sameInstallation, lighting.Distinct().ToArray(), defaultDashboard, []);
        var converted = LegacyConfigurationMigration.Convert(files, matching);
        if (unresolved.Count > 0) converted = converted with { Migration = converted.Migration with {
            Notices = [.. converted.Migration.Notices, $"{unresolved.Count}件の参照を再関連付け待ちとして保持しました"] } };
        DeviceConfigurationCodec.Validate(converted);
        return new(converted, matching, unresolved.ToArray());
    }

    private static Required[] Requirements(AppSettings settings, TransferContent content, DashboardItem[]? defaults)
    {
        var needs = new Dictionary<(string, string, string), Required>();
        void Add(string source, string device, string endpoint, EndpointKind? kind, DeviceFeature? feature = null)
        {
            var value = new Required(source, device, endpoint, kind, feature);
            if (!DeviceConfigurationRules.Text(device, 2048) || !DeviceConfigurationRules.Text(endpoint, 512)) throw new IOException("旧設定の参照が不正です");
            if (needs.TryGetValue(value.Key, out var old) && old.Kind != value.Kind) throw new IOException("旧設定の端子種別が矛盾しています");
            needs[value.Key] = value;
        }
        void Temperature(string? id) { if (id is not null) Add("temperature", "local", id, EndpointKind.Sensor, DeviceFeature.Temperature); }
        void Profile(AutoProfile? p) { Temperature(p?.Cpu?.SensorId); Temperature(p?.Gpu?.SensorId); }
        void LimitInputs(FanLimits? limits, string? cpu, string? gpu)
        {
            if (limits is null) return;
            if (limits.ProtectManualCpu || limits.AllowStop) Temperature(cpu ?? "unassigned:cpu");
            if (limits.ProtectManualGpu || limits.AllowStop) Temperature(gpu ?? "unassigned:gpu");
        }
        void Hub(string id, string endpoint) => Add("hub", id, endpoint, endpoint.StartsWith("fan", StringComparison.Ordinal) ? EndpointKind.Cooling : EndpointKind.Lighting);
        void Rgb(string id, byte zone) => Add("rgb", id, "zone:" + zone, EndpointKind.Lighting);
        void Scene(LightingScene scene)
        {
            foreach (var h in scene.Hub) Hub(h.HubId, h.Channel);
            foreach (var b in scene.Board) Rgb(b.Identity, b.Zone);
            if (scene.Sync is not { } sync) return;
            foreach (var channel in sync.Channels) Hub(sync.HubId, channel);
            foreach (var target in LightingGroups.SyncTargets(sync)) Rgb(target.Identity, target.Zone);
        }
        Temperature(settings.CpuSensorId); Temperature(settings.GpuSensorId);
        foreach (var p in settings.Profiles) Profile(p);
        Profile(settings.LastConfirmedPolicy?.Profile); Temperature(settings.LastConfirmedPolicy?.CpuSensorId); Temperature(settings.LastConfirmedPolicy?.GpuSensorId);
        LimitInputs(settings.Limits, settings.CpuSensorId, settings.GpuSensorId);
        LimitInputs(settings.LastConfirmedPolicy?.Limits, settings.LastConfirmedPolicy?.CpuSensorId, settings.LastConfirmedPolicy?.GpuSensorId);
        if (settings.Hardware.Hub is { } hub)
        {
            foreach (var id in (settings.Hardware.Fans ?? []).Concat(settings.Hardware.Leds ?? []).Concat(settings.Hardware.LedDetails?.Select(l => l.Id) ?? []).Concat(settings.Lighting.Channels ?? [])) Hub(hub.Id, id);
        }
        foreach (var (channel, light) in settings.LightingPorts) Hub(light.HubId, channel);
        void Fan(string id, CpuFanSettings s)
        {
            if (!s.WiringConfirmed && s.Minimum is null && s.Profile is null) return;
            Add("board-control", "motherboard", id, EndpointKind.Cooling); Profile(s.Profile);
        }
        Fan(MotherboardFans.Cpu.Id, settings.CpuFan); Fan(MotherboardFans.Chassis.Id, settings.ChassisFan);
        foreach (var (id, fan) in settings.BoardFans) Fan(id, fan);
        foreach (var sensor in settings.Motherboard.Sensors ?? []) Add("board-sensor", "motherboard", sensor.Id, null);
        foreach (var zone in settings.Motherboard.RgbZones ?? []) Add("rgb-zone-setting", "local", zone.Id, EndpointKind.Lighting);
        foreach (var scene in content.Scenes.Scenes!) Scene(scene);
        foreach (var group in content.Groups.Groups!)
        { foreach (var h in group.Hub) Hub(h.HubId, h.Channel); foreach (var b in group.Board) Rgb(b.Identity, b.Zone); }
        foreach (var sync in content.Groups.SyncTargets ?? []) Rgb(sync.Identity, sync.Zone);
        foreach (var name in content.Names.Names!)
        { if (name.Address.Hub is { } h) Hub(h.HubId, h.Channel); else Rgb(name.Address.Board!.Identity, name.Address.Board.Zone); }
        foreach (var state in content.Adjustments.Values) if (state.BaseScene is { } scene) Scene(scene);
        foreach (var item in settings.Dashboard.Items ?? defaults ?? throw new IOException("現在の既定ダッシュボードを取得してから移行してください"))
        {
            if (item.Kind == "temperature") Temperature(item.SensorId == "selected" ? item.Source == "cpu" ? settings.CpuSensorId ?? "unassigned:cpu" : settings.GpuSensorId ?? "unassigned:gpu" : item.SensorId);
            else if (item.Source == "nzxt") Hub(settings.Hardware.Hub?.Id ?? "unassigned-hub", item.SensorId);
            else Add("rpm", "motherboard", item.SensorId, null, DeviceFeature.Rpm);
        }
        foreach (var rule in content.Notifications.Rules ?? [])
        {
            var a = rule.Address;
            if (a.Source == "sensor") Temperature(a.SensorId);
            else if (a.Source == "nzxt") Hub(a.DeviceId, a.SensorId);
            else Add("notification", a.DeviceId, a.SensorId, a.Kind == "temperature" ? EndpointKind.Sensor : null,
                a.Kind == "temperature" ? DeviceFeature.Temperature : DeviceFeature.Rpm);
        }
        if (needs.Count > 8192) throw new IOException("移行する参照の数が上限を超えています");
        return needs.Values.ToArray();
    }
}
