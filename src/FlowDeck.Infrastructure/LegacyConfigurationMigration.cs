using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Produced by the service's device matching step, never deserialized from an imported archive as authorization.
public sealed record LegacyMigrationBindings(DeviceRegistrations Devices, DeviceBindings Bindings, MigratedReference[] References,
    EndpointReference[] ConfirmedCoolingMatches, bool SameInstallation, EndpointReference[]? ConfirmedLightingMatches = null,
    DashboardItem[]? DefaultDashboard = null, EndpointReference[]? ConfirmedTemperatureMatches = null);

/// <summary>Pure v1-to-v2 conversion. Missing references abort the whole conversion; no first-device substitution.</summary>
public static class LegacyConfigurationMigration
{
    public static DeviceConfigurationPackage Convert(IReadOnlyDictionary<string, byte[]> files, LegacyMigrationBindings matching)
    {
        // The existing reader validates every legacy component and rejects duplicate JSON members.
        var portable = SettingsTransfer.FromFiles(files);
        var settings = files.TryGetValue("settings.json", out var bytes) ? JsonSerializer.Deserialize<AppSettings>(bytes, Contract.Json)! : new();
        if (matching.References.DistinctBy(r => (r.Source, r.LegacyDeviceId, r.LegacyEndpointId)).Count() != matching.References.Length)
            throw new IOException("機器の移行対応が重複しています");
        var references = matching.References.ToDictionary(r => (r.Source, r.LegacyDeviceId, r.LegacyEndpointId), r => r.Target);
        var notices = new List<string>();
        EndpointReference Resolve(string source, string device, string endpoint) => references.GetValueOrDefault((source, device, endpoint))
            ?? throw new IOException($"移行先が未確定です: {source} / {endpoint}");
        EndpointReference Temperature(string? id) => Resolve("temperature", "local", id ?? throw new IOException("温度センサーが未設定です"));
        EndpointReference Hub(string identity, string endpoint) => Resolve("hub", identity, endpoint);
        EndpointReference BoardRgb(string identity, byte zone) => Resolve("rgb", identity, "zone:" + zone);
        bool Retain(EndpointReference target, bool wasConfirmed) => matching.SameInstallation && wasConfirmed && matching.ConfirmedCoolingMatches.Contains(target) &&
            matching.Bindings.Devices.Any(d => d.DeviceId == target.DeviceId);
        bool RetainLedCount(EndpointReference target, int? count) => matching.SameInstallation && count is not null &&
            (matching.ConfirmedLightingMatches ?? []).Contains(target) && matching.Bindings.Devices.Any(d => d.DeviceId == target.DeviceId);
        var endpointSettings = new Dictionary<EndpointReference, RegisteredEndpoint>();
        RegisteredEndpoint Endpoint(EndpointReference target) => endpointSettings.GetValueOrDefault(target) ??
            matching.Devices.Devices.SingleOrDefault(d => d.Id == target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == target.EndpointId)
            ?? throw new IOException("移行先の端子が登録されていません");
        foreach (var s in settings.Motherboard.Sensors ?? [])
        {
            var target = Resolve("board-sensor", "motherboard", s.Id); var endpoint = Endpoint(target);
            endpointSettings[target] = endpoint with { Name = s.DisplayName };
        }
        if (settings.Hardware.Hub is { } ledHub)
            foreach (var s in settings.Hardware.LedDetails ?? [])
            {
                var target = Hub(ledHub.Id, s.Id); var endpoint = Endpoint(target);
                endpointSettings[target] = endpoint with { Setup = new(s.Accessory, s.LedCount, RetainLedCount(target, s.LedCount)) };
            }
        CoolingTemperatureProfile Profile(AutoProfile p) => new(p.Id, p.Name, p.Revision,
            new[] { p.Cpu, p.Gpu }.OfType<SensorCurve>().Select(c => new TemperatureDemandCurve(Temperature(c.SensorId), c.Points.ToArray(), c.Overheat, c.ResumeBelow)).ToArray(),
            p.DescentDelaySeconds, p.DescentPerSecond);
        CoolingSafety Limits(FanLimits limits, string? cpu, string? gpu)
        {
            var protection = new List<TemperatureRule>();
            if (limits.ProtectManualCpu) protection.Add(new(Temperature(cpu ?? "unassigned:cpu"), limits.CpuOverheat ?? throw new IOException("CPU保護温度が未設定です"), limits.CpuResumeBelow ?? throw new IOException("CPU復帰温度が未設定です")));
            if (limits.ProtectManualGpu) protection.Add(new(Temperature(gpu ?? "unassigned:gpu"), limits.GpuOverheat ?? throw new IOException("GPU保護温度が未設定です"), limits.GpuResumeBelow ?? throw new IOException("GPU復帰温度が未設定です")));
            CoolingStopRule? stop = null;
            if (limits.AllowStop)
            {
                var rule = limits.Stop ?? throw new IOException("停止条件が未設定です");
                stop = new([new(Temperature(cpu ?? "unassigned:cpu"), rule.CpuBelow), new(Temperature(gpu ?? "unassigned:gpu"), rule.GpuBelow)], rule.RestartDuty, rule.RestartSeconds);
            }
            return new(limits.Minimum, limits.Fallback, limits.Exit, CoolingExitAction.FixedDuty, protection.ToArray(), stop);
        }
        var profiles = settings.Profiles.Select(Profile).ToArray(); var cooling = new List<CoolingEndpointConfiguration>();
        if (settings.Hardware.Hub is { } hub)
        foreach (var id in settings.Hardware.Fans ?? [])
        {
            var target = Hub(hub.Id, id); var confirmed = Retain(target, settings.Hardware.WiringConfirmed);
            var safety = Limits(settings.Limits, settings.CpuSensorId, settings.GpuSensorId);
            CoolingSavedPolicy? policy = settings.LastConfirmedPolicy is { Mode: FanMode.Manual, ManualDuty: { } duty } ? new(FanMode.Manual, duty) :
                settings.LastConfirmedPolicy is { Mode: FanMode.Auto, Profile: { } selected } ? new(FanMode.Auto, Profile: Profile(selected)) : null;
            if (policy is not null && settings.LastConfirmedPolicy is { Limits: { } savedLimits } saved)
                policy = policy with { SafetySnapshot = Limits(savedLimits, saved.CpuSensorId, saved.GpuSensorId) };
            var endpoint = new CoolingEndpointConfiguration(target, confirmed, safety, profiles, policy);
            var resumeError = DeviceConfigurationRules.SavedPolicyReadiness(endpoint);
            if (resumeError is null && policy is not null && DeviceCoolingPolicy.Required(policy, policy.SafetySnapshot!)
                .Any(sensor => !(matching.ConfirmedTemperatureMatches ?? []).Contains(sensor))) resumeError = "保存した温度源の個体照合が必要です";
            cooling.Add(endpoint with { ResumeOnStart = confirmed && settings.Startup.ResumeControl && resumeError is null });
            if (settings.Startup.ResumeControl && policy is not null && resumeError is not null)
                notices.Add(id.ToUpperInvariant() + " の起動時再開は保留: " + resumeError);
            if (settings.Hardware.WiringConfirmed && !confirmed) notices.Add(id.ToUpperInvariant() + " は現在の機器情報と保存した安全設定を照合します");
        }
        void BoardFan(string id, CpuFanSettings fan)
        {
            if (!fan.WiringConfirmed && fan.Minimum is null && fan.Profile is null) return;
            var target = Resolve("board-control", "motherboard", id); var confirmed = Retain(target, fan.WiringConfirmed);
            cooling.Add(new(target, confirmed, new(fan.Minimum, 100, null, CoolingExitAction.ReleaseToDevice, []), fan.Profile is { } p ? [Profile(p)] : []));
            if (fan.WiringConfirmed && !confirmed) notices.Add(id + " は現在の機器情報と保存した安全設定を照合します");
        }
        BoardFan(MotherboardFans.Cpu.Id, settings.CpuFan); BoardFan(MotherboardFans.Chassis.Id, settings.ChassisFan);
        foreach (var (id, fan) in settings.BoardFans) BoardFan(id, fan);
        static LightingProgram HubProgram(HubLightSetting effect) => new("native/nzxt-control-hub/" + effect.Mode, effect.Colors.ToArray(), effect.Speed, effect.Reverse);
        static LightingProgram BoardProgram(string identity, BoardLightSetting effect)
        {
            var memory = MemoryLighting.IsIdentity(identity);
            RgbColor[]? leds = null;
            if (memory && effect.MemoryPalette is not null)
            {
                var colors = MemoryLighting.Colors(effect);
                leds = Enumerable.Range(0, 8).Select(i => new RgbColor(colors[i * 3], colors[i * 3 + 2], colors[i * 3 + 1])).ToArray();
            }
            return new("native/" + (memory ? "ene-memory/" : "asrock-usb/") + effect.Mode, [new(effect.Red, effect.Green, effect.Blue)], effect.Speed, LedColors: leds);
        }
        DeviceLightingScene Scene(LightingScene scene)
        {
            var individuals = scene.Hub.Select(h => new EndpointLighting(Hub(h.HubId, h.Channel), HubProgram(h.Effect)))
                .Concat(scene.Board.Select(b => new EndpointLighting(BoardRgb(b.Identity, b.Zone), BoardProgram(b.Identity, b.Effect)))).ToArray();
            var sync = scene.Sync;
            var targets = sync is null ? [] : sync.Channels.Select(c => Hub(sync.HubId, c)).Concat(LightingGroups.SyncTargets(sync).Select(b => BoardRgb(b.Identity, b.Zone))).ToArray();
            return new(scene.Id, scene.Name, individuals, sync is null ? [] : [new(targets, new(sync.Mode, [sync.Color], sync.Speed))]);
        }
        var groups = portable.Groups.Groups!.Select(g => new EndpointGroup(g.Id, g.Name, EndpointKind.Lighting,
            g.Hub.Select(h => Hub(h.HubId, h.Channel)).Concat(g.Board.Select(b => BoardRgb(b.Identity, b.Zone))).ToArray())).ToArray();
        var names = portable.Names.Names!.Select(n => new EndpointAlias(n.Address.Hub is { } h ? Hub(h.HubId, h.Channel) : BoardRgb(n.Address.Board!.Identity, n.Address.Board.Zone), n.Name)).ToArray();
        var lightingPreferences = new Dictionary<EndpointReference, LightingProgram>();
        var previousEffect = settings.Lighting.Effect ?? (settings.Lighting.LastSent is "blue" or "white" or "purple" or "off" ? HubLighting.Preset(settings.Lighting.LastSent) : null);
        var defaultLight = previousEffect is not null ? HubProgram(previousEffect) : null;
        if (defaultLight is not null && settings.Hardware.Hub is { } lightingHub)
            foreach (var channel in settings.Lighting.Channels ?? settings.Hardware.Leds ?? [])
                if (!settings.LightingPorts.ContainsKey(channel)) lightingPreferences[Hub(lightingHub.Id, channel)] = defaultLight;
        foreach (var (channel, light) in settings.LightingPorts)
            if (light.Effect is { } effect) lightingPreferences[Hub(light.HubId, channel)] = HubProgram(effect);
        foreach (var s in settings.Motherboard.RgbZones ?? [])
        {
            var target = Resolve("rgb-zone-setting", "local", s.Id); var endpoint = Endpoint(target);
            endpointSettings[target] = endpoint with { Setup = new(s.Accessory, s.ConfirmedLedCount, RetainLedCount(target, s.ConfirmedLedCount)) };
            if (s.LastLighting is { } effect)
            {
                var address = matching.References.SingleOrDefault(r => r.Source == "rgb" && r.Target == target);
                if (address is not null) lightingPreferences[target] = BoardProgram(address.LegacyDeviceId, effect);
                else
                {
                    var draft = BoardProgram(effect.MemoryPalette is null ? "unresolved" : MemoryLighting.Identity(0x71), effect);
                    lightingPreferences[target] = draft with { EffectId = "legacy/unresolved/" + effect.Mode };
                    notices.Add("照明 " + s.Id + " は元の効果を保存して再関連付けを待ちます");
                }
            }
        }
        // The default catalog depends on the actual acquired snapshot. The caller supplies it instead of inferring online devices here.
        var items = settings.Dashboard.Items ?? matching.DefaultDashboard ?? throw new IOException("現在のダッシュボード項目を取得してから移行してください");
        var dashboard = items.Select(item =>
        {
            var target = item.Kind == "temperature" ? Temperature(item.SensorId == "selected" ? item.Source == "cpu" ? settings.CpuSensorId ?? "unassigned:cpu" : settings.GpuSensorId ?? "unassigned:gpu" : item.SensorId) :
                item.Source == "nzxt" ? Hub(settings.Hardware.Hub?.Id ?? "unassigned-hub", item.SensorId) : Resolve("rpm", "motherboard", item.SensorId);
            return new DeviceDashboardItem(target, item.Kind == "temperature" ? DeviceFeature.Temperature : DeviceFeature.Rpm,
                item.Name, item.Kind == "temperature" ? "temperature" : item.Role, item.Visible);
        }).ToArray();
        var registrations = matching.Devices with { Devices = matching.Devices.Devices.Select(d => d with { Endpoints = d.Endpoints.Select(e =>
            endpointSettings.GetValueOrDefault(new(d.Id, e.Id)) ?? e).ToArray() }).ToArray() };
        var notifications = DeviceNotifications.Migrate(portable.Notifications, a => a.Source switch
        {
            "sensor" => Temperature(a.SensorId), "nzxt" => Hub(a.DeviceId, a.SensorId),
            "motherboard" => Resolve("notification", a.DeviceId, a.SensorId), _ => throw new IOException("通知の移行先が未確定です")
        });
        var result = new DeviceConfigurationPackage(registrations, matching.SameInstallation ? matching.Bindings : new(2, null, []),
            new(2, cooling.ToArray(), [], profiles),
            new(2, portable.Scenes.Scenes!.Select(Scene).ToArray(), groups, names, (portable.Groups.SyncTargets ?? []).Select(b => BoardRgb(b.Identity, b.Zone)).ToArray(),
                portable.Adjustments.Select(p => new DeviceBrightness(p.Key, p.Value.Brightness, p.Value.IsOff, p.Value.BaseScene is { } s ? Scene(s) : null)).ToArray(),
                lightingPreferences.Select(p => new EndpointLighting(p.Key, p.Value)).ToArray(), defaultLight),
            new(2, dashboard), new(2, settings.Ui, settings.Startup, notifications, portable.Palette,
                new(settings.Motherboard.OpenRgbEnabled, settings.Motherboard.OpenRgbPort)),
            new(2, "legacy-v1", SettingsTransfer.Hash(files), matching.References, notices.ToArray()));
        DeviceConfigurationCodec.Validate(result);
        return matching.SameInstallation ? Contract.Copy(result) : DeviceConfigurationCodec.Portable(result);
    }
    public static ConfigurationActivation Stage(string newDirectory, IReadOnlyDictionary<string, byte[]> legacyFiles, LegacyMigrationBindings bindings)
    {
        var files = legacyFiles.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var converted = Convert(files, bindings);
        return DeviceConfigurationCodec.Store(newDirectory).Stage(DeviceConfigurationCodec.Write(converted), files);
    }
}
