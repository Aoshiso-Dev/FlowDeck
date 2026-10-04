using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

// Explicit development entry point. Both endpoints and every output remain in memory.
public static class DeviceServiceSmoke
{
    private sealed class Log : IEventLog { public void Write(string category, object data) { } }
    private sealed class DemoThermalSource : IHardwareSensorBackend
    {
        private readonly string connection = Guid.NewGuid().ToString("N");
        public double Value = 50;
        public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => Task.FromResult<HardwareSensorDevice[]>([
            new("/demo-cpu/0", "模擬CPU", DeviceKind.Processor, new('a', 64), null, connection, null,
                [new("/demo-cpu/0/temperature/0", "温度", "/demo-cpu/0", DeviceFeature.Temperature, Value, TimeProvider.System.GetTimestamp())])]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public static async Task<int> RunHubAdaptersAsync()
    {
        using var user = WindowsIdentity.GetCurrent(); var sid = user.User?.Value ?? throw new IOException("Windowsユーザーがありません");
        var installation = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Generic-Hub-Smoke-" + Guid.NewGuid().ToString("N"));
        var aIdentity = new HubIdentity("demo-serial-a", "同じHubモデル", "A", "demo:a");
        var bIdentity = new HubIdentity("demo-serial-b", "同じHubモデル", "B", "demo:b");
        var a = new DemoHub(aIdentity) { DelayMs = 0 }; var b = new DemoHub(bIdentity) { DelayMs = 0 };
        var first = new HubDeviceDriver(installation, aIdentity.Id, a, directory);
        var second = new HubDeviceDriver(installation, bIdentity.Id, b, directory);
        var temperature = new DemoThermalSource();
        var sensors = new HardwareSensorProvider(installation, temperature);
        // Create a registration fixture from memory-only devices, then exercise the same cold-start path as a saved service generation.
        await first.InitializeAsync(); await second.InitializeAsync(); await sensors.PollAsync(CancellationToken.None);
        var registered = new IPhysicalDeviceDriver[] { first, second }.Concat(sensors.CachedDrivers.Cast<IPhysicalDeviceDriver>()).ToArray();
        var detected = second.CachedStatus!; var limits = new FanLimits(50, 100, 80);
        var oldSettings = new AppSettings { Hardware = new(bIdentity, detected.Channels.Select(c => c.Id).ToArray(),
                detected.Lighting.Select(l => l.Id).ToArray(), ControlCoordinator.WiringSignature(detected), true, detected.Lighting),
            CpuSensorId = "/demo-cpu/0/temperature/0", Limits = limits, Startup = new(true, true),
            LastConfirmedPolicy = new(FanMode.Manual, 65, Limits: limits),
            Dashboard = new([new("cpu", "selected", "temperature", "CPU"), new("nzxt", "fan3", "fan", "背面")]) };
        var legacyFiles = new Dictionary<string, byte[]> { ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(oldSettings, Contract.Json) };
        var migration = LegacyMigrationPreparationBuilder.Prepare(installation, legacyFiles, registered, sameInstallation: true);
        if (migration.Unresolved.Length != 0 || migration.Configuration.Cooling.Endpoints.Any(e => !e.WiringConfirmed || !e.ResumeOnStart))
            throw new IOException("旧設定と実機キャッシュの対応を確認できません");
        var package = migration.Configuration;
        // A remembered, unbound RGB device exercises settings larger than one IPC frame without opening another transport.
        var offlineRgb = new RegisteredDevice("demo-offline-rgb", "未接続の照明", "demo-rgb", DeviceKind.Controller,
            [new("led1", "LED", EndpointKind.Lighting)]);
        var ledColors = Enumerable.Range(0, 4096).Select(_ => new RgbColor(10, 20, 30)).ToArray();
        package = package with { Devices = package.Devices with { Devices = [.. package.Devices.Devices, offlineRgb] },
            Lighting = package.Lighting with { Endpoints = [new(new(second.DeviceId, "led1"), true, new(LightingExitAction.Off))],
                Scenes = Enumerable.Range(0, 6).Select(i => new DeviceLightingScene("demo-scene-" + i,
                "保存した照明 " + i, [new(new(offlineRgb.Id, "led1"), new("fixed", [new(10, 20, 30)], LedColors: ledColors))], [])).ToArray() } };
        if (DeviceConfigurationCodec.SerializePackage(package).Length <= DeviceServiceProtocol.MaximumFrameBytes)
            throw new IOException("設定分割転送の試験データが不足しています");
        var store = DeviceConfigurationCodec.Store(Path.Combine(directory, "configuration"));
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(package), legacyFiles));
        var log = new Log();
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, installation, () => new([first, second], [sensors]), [sid], log, demo: true);
        var runtime = startup.Runtime;
        if (a.Writes != 0 || b.Writes != 0 || runtime.State().LifecycleError is not null) throw new IOException("起動時の復旧と監視状態を確認できません");
        runtime.Start(); var pipeName = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server = new DeviceServicePipeServer(runtime, [sid], log, pipeName).RunAsync(stop.Token);
        try
        {
            await using (var client = DeviceServicePipeClient.ForDemo(pipeName, Environment.ProcessId))
            {
                await client.ConnectAsync(ct: stop.Token);
                var snapshot = await client.ReadConfigurationAsync(stop.Token);
                var renamed = snapshot.Configuration with { Devices = snapshot.Configuration.Devices with {
                    Devices = snapshot.Configuration.Devices.Devices.Select(d => d.Id == second.DeviceId ? d with { Name = "第二Hub" } : d).ToArray() } };
                var preview = await client.PreviewConfigurationAsync(snapshot.Generation, renamed, stop.Token);
                if (!preview.ChangedComponents.Contains("devices.json") || preview.Bindings.Count(b => b.Matches) != 3 ||
                    preview.Bindings.Single(b => b.DeviceId == offlineRgb.Id).Matches)
                    throw new IOException("設定差分と物理関連付けを確認できませんでした");
                var beforeApply = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                await client.ApplyConfigurationAsync(preview, beforeApply.Inventory.TopologyRevision, stop.Token);
                var adopted = await client.ReadConfigurationAsync(stop.Token);
                if (adopted.Generation == snapshot.Generation || adopted.Configuration.Devices.Devices.Single(d => d.Id == second.DeviceId).Name != "第二Hub" || a.Writes != 0 || b.Writes != 0)
                    throw new IOException("設定世代を一括で採用できませんでした");
                var state = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State ?? throw new IOException("機器一覧がありません");
                if (state.Inventory.Devices.Length != 3 || state.Telemetry is not { Length: 21 } || state.Telemetry.Any(t => t.Stale))
                    throw new IOException("汎用Hubの測定値を取得できません");
                var endpoint = state.Endpoints.Single(e => e.Target == new EndpointReference(second.DeviceId, "fan3"));
                var restore = client.Request(DeviceOperation.RestoreSavedCooling, topology: state.Inventory.TopologyRevision,
                    targets: [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)]);
                if ((await client.SendAsync(restore, stop.Token)).Result?.Targets.Single().ReportedDuty != 65 || a.Writes != 0 || b.Writes != 1)
                    throw new IOException("採用済み世代の保存方針を再開できませんでした");
                state = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                endpoint = state.Endpoints.Single(e => e.Target == endpoint.Target);
                var request = client.Request(DeviceOperation.SetDuty, topology: state.Inventory.TopologyRevision,
                    targets: [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], duty: 32);
                var result = await client.SendAsync(request, stop.Token);
                if (result.Result?.Outcome != Outcome.Confirmed || result.Result.Targets.Single().ReportedDuty != 50 ||
                    result.Result.Targets.Single().Persistence?.State != DevicePolicySaveState.Saved || a.Writes != 0 || b.Writes != 2 ||
                    startup.Configuration.Cooling.Endpoints.Single(e => e.Target == endpoint.Target).SavedPolicy?.Duty != 32)
                    throw new IOException("汎用Hubの端子・最低出力の適用に失敗しました");
                await client.SendAsync(request, stop.Token); if (b.Writes != 2) throw new IOException("端子への再送を重複実行しました");
                var changed = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                if (changed.Telemetry!.Single(t => t.Target == endpoint.Target && t.Feature == DeviceFeature.DutyRead).Value != 50)
                    throw new IOException("設定後の報告出力が通信に反映されませんでした");
                DeviceTargetVersion CurrentTarget(DeviceServiceState current)
                { var row = current.Endpoints.Single(e => e.Target == endpoint.Target); return new(row.Target, row.Generation, row.PolicyRevision); }
                var automatic = client.Request(DeviceOperation.SetAutomatic, topology: changed.Inventory.TopologyRevision, targets: [CurrentTarget(changed)],
                    profile: new("demo-curve", "模擬温度カーブ", 1, [new(changed.Telemetry!.Single(t => t.Feature == DeviceFeature.Temperature).Target,
                        [new(20, 50), new(80, 100)], 90, 80)], 0, 100));
                var autoResult = (await client.SendAsync(automatic, stop.Token)).Result?.Targets.Single();
                if (autoResult?.ReportedDuty != 75 || autoResult.Persistence?.State != DevicePolicySaveState.Saved)
                    throw new IOException("パイプ経由の温度カーブを適用できませんでした");
                var savedAutomatic = (await client.ReadConfigurationAsync(stop.Token)).Configuration.Cooling.Endpoints.Single(e => e.Target == endpoint.Target);
                if (!savedAutomatic.ResumeOnStart || savedAutomatic.SavedPolicy is not { Mode: FanMode.Auto, Profile.Id: "demo-curve", SafetySnapshot: { } savedSafety } ||
                    !DeviceConfigurationRules.SameSafety(savedAutomatic.Safety, savedSafety)) throw new IOException("適用した自動方針と安全条件を保存できませんでした");
                temperature.Value = 95; await runtime.PollAsync(); await runtime.PollAsync(); await runtime.TickCoolingAsync(); await runtime.TickCoolingAsync();
                var fallback = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                if (fallback.Endpoints.Single(e => e.Target == endpoint.Target) is not { ReportedDuty: 100, Cooling: { Faulted: true, Mode: FanMode.Fallback } })
                    throw new IOException("過熱時の保護結果がパイプに反映されませんでした");
                temperature.Value = 50; await runtime.PollAsync(); await runtime.PollAsync(); await runtime.TickCoolingAsync();
                var resume = client.Request(DeviceOperation.ResumeCooling, topology: fallback.Inventory.TopologyRevision, targets: [CurrentTarget(fallback)]);
                if ((await client.SendAsync(resume, stop.Token)).Result?.Outcome != Outcome.Confirmed)
                    throw new IOException("パイプ経由で明示的に制御を再開できませんでした");
                var resumed = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                if (resumed.Endpoints.Single(e => e.Target == endpoint.Target).Cooling is not { Faulted: false, Mode: FanMode.Auto })
                    throw new IOException("再開した制御方式を確認できませんでした");
                DeviceTargetVersion LightingTarget(DeviceServiceState current)
                { var row = current.Endpoints.Single(e => e.Target == new EndpointReference(second.DeviceId, "led1")); return new(row.Target, row.Generation, row.PolicyRevision); }
                var animate = client.Request(DeviceOperation.SetLighting, topology: resumed.Inventory.TopologyRevision, targets: [LightingTarget(resumed)],
                    lighting: new("breathing", [new(40, 80, 200)], Brightness: 50));
                if ((await client.SendAsync(animate, stop.Token)).Result?.Outcome != Outcome.WrittenUnverified || a.LightingWrites != 0)
                    throw new IOException("汎用Hubの照明同期を開始できませんでした");
                DeviceServiceState illuminated;
                do
                {
                    await Task.Delay(100, stop.Token); illuminated = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                } while (illuminated.Endpoints.Single(e => e.Target == new EndpointReference(second.DeviceId, "led1")).Lighting?.Frames < 2);
                var stopLighting = client.Request(DeviceOperation.StopLighting, topology: illuminated.Inventory.TopologyRevision, targets: [LightingTarget(illuminated)]);
                if ((await client.SendAsync(stopLighting, stop.Token)).Result?.Outcome != Outcome.WrittenUnverified ||
                    b.LightEffects["led1"].Colors[0] != new RgbColor(20, 40, 100) || new HubLightingRecoveryJournal(directory, second.DeviceId).Pending.Length != 0)
                    throw new IOException("汎用Hubの基準色への通常停止を確認できませんでした");
                illuminated = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                var fixedColor = client.Request(DeviceOperation.SetLighting, topology: illuminated.Inventory.TopologyRevision, targets: [LightingTarget(illuminated)],
                    lighting: new("fixed", [new(10, 20, 30)]));
                if ((await client.SendAsync(fixedColor, stop.Token)).Result?.Outcome != Outcome.WrittenUnverified)
                    throw new IOException("汎用Hubの固定照明を確認できませんでした");
            }
            while (runtime.State() is { } finishing && (finishing.Releasing || finishing.Endpoints.Any(e => e.Target.DeviceId == second.DeviceId && e.Owned)))
                await Task.Delay(20, stop.Token);
            if (a.Writes != 0 || b.Writes < 6 || runtime.State().LifecycleError is not null ||
                (await b.ReadAsync()).Channels.Any(c => c.Duty != (c.Id == "fan3" ? 80 : 45)) || new HubEndpointRecoveryJournal(directory, second.DeviceId).Pending.Length != 0 ||
                a.LightingWrites != 0 || b.LightingWrites < 5 || b.LightEffects["led1"].Mode != "off" || new HubLightingRecoveryJournal(directory, second.DeviceId).Pending.Length != 0)
                throw new IOException("切断後の端子別終了または対象外保持に失敗しました");
            Console.WriteLine(JsonSerializer.Serialize(new { demo = true, physicalHardware = false, hubDeviceDrivers = 2, sharedSensorProvider = true, telemetryValues = 21,
                legacyMigrationPrepared = true, legacyBackupRetained = true,
                manualRequestPersisted = 32, automaticPolicyPersisted = true,
                configurationLoaded = true, configurationIpcRoundTrip = true, configurationLargerThanFrame = true, atomicGenerationAdopted = true,
                physicalBindingsChecked = 3, startupMonitoringOnly = true, savedPolicyRestored = 65,
                addressedSecondHubOnly = true, minimumApplied = 50, automaticDemand = 75, overheatFallback = 100, explicitResume = true,
                hubLightingFrames = true, hubLightingBaseColorStop = true, hubLightingExitOff = true,
                exitApplied = 80, duplicateSuppressed = true, recoveryJournalCleared = true }, Contract.Json));
            return 0;
        }
        finally { stop.Cancel(); try { await server.ConfigureAwait(false); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }
    public static async Task<int> RunAsync()
    {
        using var user = WindowsIdentity.GetCurrent(); var sid = user.User?.Value ?? throw new IOException("Windowsユーザーがありません");
        var a = new DemoEndpointDriver("demo-controller-a", "同じ型番", DeviceKind.Controller);
        var b = new DemoEndpointDriver("demo-controller-b", "同じ型番", DeviceKind.Controller, minimum: 40);
        var board = new DemoEndpointDriver("demo-board", "マザー", DeviceKind.Motherboard, cooling: false);
        var memory = new DemoEndpointDriver("demo-memory", "メモリ", DeviceKind.Memory, cooling: false);
        var log = new Log(); await using var runtime = new DeviceControlRuntime([a, b, board, memory], [sid], log, demo: true);
        runtime.Start(); var pipeName = "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server = new DeviceServicePipeServer(runtime, [sid], log, pipeName).RunAsync(stop.Token);
        try
        {
            await using (var client = DeviceServicePipeClient.ForDemo(pipeName, Environment.ProcessId))
            {
                await client.ConnectAsync(ct: stop.Token);
                var state = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State ?? throw new IOException("機器一覧がありません");
                DeviceTargetVersion Target(string device, string endpoint)
                {
                    var s = state.Endpoints.Single(s => s.Target == new EndpointReference(device, endpoint));
                    return new(s.Target, s.Generation, s.PolicyRevision);
                }
                var fan = client.Request(DeviceOperation.SetDuty, topology: state.Inventory.TopologyRevision, targets: [Target(b.DeviceId, "fan1")], duty: 55);
                var result = await client.SendAsync(fan, stop.Token);
                if (result.Result?.Outcome != Outcome.Confirmed || a.FanWrites != 0 || b.FanWrites != 1 || b.Duty != 55) throw new IOException("同型機器の操作先が不正です");
                await client.SendAsync(fan, stop.Token); if (b.FanWrites != 1) throw new IOException("要求を重複実行しました");
                var lights = client.Request(DeviceOperation.SetLighting, topology: state.Inventory.TopologyRevision,
                    targets: [Target(board.DeviceId, "light1"), Target(memory.DeviceId, "light1")], lighting: new("fixed", [new(10, 20, 30)]));
                if ((await client.SendAsync(lights, stop.Token)).Result?.Outcome != Outcome.WrittenUnverified || board.LightWrites != 1 || memory.LightWrites != 1)
                    throw new IOException("Hubを含まない照明操作が失敗しました");
                state = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                var stream = client.Request(DeviceOperation.SetLighting, topology: state.Inventory.TopologyRevision,
                    targets: [Target(board.DeviceId, "light1"), Target(memory.DeviceId, "light1")], lighting: new("breathing", [new(40, 80, 200)], Brightness: 50));
                if ((await client.SendAsync(stream, stop.Token)).Result?.Outcome != Outcome.WrittenUnverified) throw new IOException("共通の照明同期を開始できません");
                do
                {
                    await Task.Delay(100, stop.Token); state = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                } while (state.Endpoints.Any(e => e.Target.DeviceId is "demo-board" or "demo-memory" && e.Lighting?.Frames < 2));
                var synchronized = state.Endpoints.Where(e => e.Target.DeviceId is "demo-board" or "demo-memory").ToArray();
                if (synchronized.Any(e => e.Lighting is not { Running: true, Frames: >= 2 }) || synchronized[0].Lighting!.GroupId != synchronized[1].Lighting!.GroupId ||
                    synchronized[0].Lighting!.LastColor != synchronized[1].Lighting!.LastColor) throw new IOException("共通時計の照明状態を確認できません");
                // Stopping one member now detaches only that member. Select the complete group here.
                var end = client.Request(DeviceOperation.StopLighting, topology: state.Inventory.TopologyRevision,
                    targets: [Target(board.DeviceId, "light1"), Target(memory.DeviceId, "light1")]);
                if ((await client.SendAsync(end, stop.Token)).Result?.Outcome != Outcome.WrittenUnverified ||
                    board.Color.Colors[0] != new RgbColor(20, 40, 100) || memory.Color.Colors[0] != board.Color.Colors[0]) throw new IOException("同期グループの通常停止を確認できません");
                state = (await client.SendAsync(client.Request(DeviceOperation.State), stop.Token)).State!;
                if (state.Endpoints.Where(e => e.Target.DeviceId is "demo-board" or "demo-memory")
                    .Any(e => e.Owned || e.Lighting is not { Running: false }))
                    throw new IOException("同期グループの停止後に照明の所有または実行状態が残っています");
                Console.WriteLine(JsonSerializer.Serialize(new { demo = true, deviceCount = state.Inventory.Devices.Length, addressedSecondController = true,
                    duplicateSuppressed = true, boardAndMemoryLighting = true, serviceLightingFrames = true, commonPhase = true, normalStopSettledGroup = true,
                    pipe = DeviceServiceProtocol.Version }, Contract.Json));
            }
            await b.Released.Task.WaitAsync(stop.Token);
            if (b.Duty != 65) throw new IOException("切断後の制御解除が未確認です");
            Console.WriteLine("{\"releasedAfterDisconnect\":true}"); return 0;
        }
        finally
        {
            stop.Cancel(); try { await server.ConfigureAwait(false); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
