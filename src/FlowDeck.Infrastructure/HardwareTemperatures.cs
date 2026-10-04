using LibreHardwareMonitor.Hardware;
using FlowDeck.Core;
using System.Security.Principal;
using System.Runtime.CompilerServices;

namespace FlowDeck.Infrastructure;

public sealed partial class HardwareTemperatures(FanRecoveryJournal? recovery = null) : ITemperatureSource, IMotherboardSource, ICpuFanDriver, IHardwareSensorBackend, IMotherboardCoolingBackend
{
    // Persisted Default/Software modes cause LHM to write PWM when opened. Monitoring must use Undefined.
    private sealed class ReadOnlySettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }
    private readonly object gate = new();
    private BoardSnapshot board = BoardSnapshot.Empty;
    private sealed record ObservedHardware(string Model, string Connection);
    private readonly ConditionalWeakTable<IHardware, ObservedHardware> physical = new();
    private readonly LhmSensorFreshness freshness = new();
    private HardwareSensorDevice[] sensorDevices = [];
    public BoardSnapshot Board => Volatile.Read(ref board);
    public static bool IsAdministrator
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public static string AccessStatus => (LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled
        ? "CPUアクセス: PawnIO " + LibreHardwareMonitor.PawnIo.PawnIo.Version
        : "CPUアクセス: PawnIO未登録。CPU温度を取得できない場合は公式ドライバーを導入してください")
        + (IsAdministrator ? " / 管理者権限" : " / 通常権限。CPUが未取得なら、トレイから終了後にStart-Admin.cmdで起動してください");
    private readonly Computer computer = new(new ReadOnlySettings()) { IsCpuEnabled = true, IsGpuEnabled = true,
        IsControllerEnabled = false, IsMotherboardEnabled = true, IsMemoryEnabled = false,
        IsStorageEnabled = false, IsNetworkEnabled = false };
    private bool open;
    private bool disposeStarted;
    private readonly Dictionary<string, (CpuFanRegisters Registers, NctCpuFanAccess Access)> fans = [];
    private CpuFanRegisters Fan(string id)
    {
        ObjectDisposedException.ThrowIf(disposeStarted, this);
        if (genericFans.Values.Any(f => f.Registers.NeedsRestore)) throw new IOException("汎用サービスがマザーファンを制御しています");
        var port = MotherboardFans.Find(id);
        if (!IsAdministrator || !open) throw new InvalidOperationException("マザーファンの取得には管理者権限とセンサー初期化が必要です");
        if (fans.TryGetValue(id, out var existing)) return existing.Registers;
        var motherboard = computer.Hardware.SingleOrDefault(h => h.HardwareType == HardwareType.Motherboard)
            ?? throw new NotSupportedException("マザーボードを取得できません");
        var definition = MotherboardDefinitionCatalog.BuiltIn.Resolve(motherboard.Name)
            ?? throw new NotSupportedException("このマザーのファン制御に対応する定義がありません");
        if (!definition.Ports.Contains(port)) throw new NotSupportedException("この端子の制御に対応していません");
        var sio = motherboard.SubHardware.SingleOrDefault(h => h.Identifier.ToString() == port.HardwareId)
            ?? throw new NotSupportedException("対象の制御チップがありません");
        var access = new NctCpuFanAccess(sio, port.Channel, port.Chip);
        var registers = new CpuFanRegisters(access, channel: port.Channel, chip: port.Chip, timestamp: () => Board.Timestamp ?? 0, recovery: recovery);
        fans.Add(id, (registers, access)); return registers;
    }
    public ICpuFanDriver FanDriver(string id) { MotherboardFans.Find(id); return new PortDriver(this, id); }
    public async Task RecoverInterruptedFansAsync(CancellationToken ct = default)
    {
        if (recovery is null) return;
        if (recovery.PreviousBoot) { recovery.ArchivePreviousBoot(); return; }
        if (recovery.Pending.Length == 0) return;
        await ReadAsync(ct);
        lock (gate)
        {
            var errors = new List<Exception>();
            foreach (var saved in recovery.Pending)
            {
                var port = MotherboardFans.Ports.Single(p => p.Chip == saved.Chip && p.Channel == saved.Channel);
                try { Fan(port.Id).RecoverInterruptedControl(); } catch (Exception e) { errors.Add(new IOException(port.Name + ": " + e.Message, e)); }
            }
            if (errors.Count > 0) throw new AggregateException("マザーファンの中断した制御を復旧できません", errors);
        }
    }
    private sealed class PortDriver(HardwareTemperatures owner, string id) : ICpuFanDriver
    {
        public Task<CpuFanReading> ReadAsync(CancellationToken ct = default) => Task.Run(() => { lock (owner.gate) { ct.ThrowIfCancellationRequested(); return owner.Fan(id).Read(); } }, ct);
        public Task<CpuFanReading> WriteAsync(int duty, CancellationToken ct = default) => Task.Run(() => { lock (owner.gate) { ct.ThrowIfCancellationRequested(); return owner.Fan(id).Write(duty); } }, ct);
        public Task RestoreAsync(CancellationToken ct = default) => Task.Run(() => { lock (owner.gate) { ct.ThrowIfCancellationRequested(); if (owner.fans.TryGetValue(id, out var fan)) fan.Registers.Restore(); } }, ct);
    }
    Task<CpuFanReading> ICpuFanDriver.ReadAsync(CancellationToken ct) => FanDriver(MotherboardFans.Cpu.Id).ReadAsync(ct);
    Task<CpuFanReading> ICpuFanDriver.WriteAsync(int duty, CancellationToken ct) => FanDriver(MotherboardFans.Cpu.Id).WriteAsync(duty, ct);
    Task ICpuFanDriver.RestoreAsync(CancellationToken ct) => FanDriver(MotherboardFans.Cpu.Id).RestoreAsync(ct);
    public Task<SensorReading[]> ReadAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (gate)
        {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposeStarted, this);
        if (!open) { computer.Open(); open = true; }
        var result = new List<SensorReading>();
        var devices = new List<HardwareSensorDevice>();
        var roots = computer.Hardware.ToArray(); var cpuCount = roots.Count(h => h.HardwareType == HardwareType.Cpu);
        foreach (var hardware in roots)
        {
            var values = new List<HardwareSensorValue>();
            if (hardware.HardwareType == HardwareType.Motherboard)
            {
                try
                {
                    var timestamp = TimeProvider.System.GetTimestamp();
                    var sensors = new List<BoardSensor>(); var errors = new List<string>(); ReadBoard(hardware, sensors, values, errors);
                    Volatile.Write(ref board, new(hardware.Name, sensors.ToArray(), timestamp, DateTimeOffset.UtcNow,
                        errors.Count > 0 ? errors[0] : sensors.Count == 0 ? "センサー未検出。管理者権限・PawnIO・機種対応を確認してください" : null));
                }
                catch (Exception e) { Volatile.Write(ref board, Board with { Error = "読み取り失敗: " + e.Message }); }
            }
            else if (hardware.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                Read(hardware, result, values, hardware.HardwareType == HardwareType.Cpu ? "cpu" : "gpu");
            else continue;
            var identity = physical.GetValue(hardware, h => new(h.Name, Guid.NewGuid().ToString("N")));
            devices.Add(HardwareSensorCapture.Describe(hardware, computer.SMBios, identity.Model, identity.Connection, cpuCount, values.ToArray()));
        }
        sensorDevices = devices.ToArray();
        return result.ToArray();
        }
    }, ct);
    public async Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct)
    {
        await ReadAsync(ct).ConfigureAwait(false);
        lock (gate) return Contract.Copy(sensorDevices);
    }
    private void Read(IHardware hardware, List<SensorReading> readings, List<HardwareSensorValue> values, string kind)
    {
        var timestamp = TimeProvider.System.GetTimestamp(); var acquisitions = freshness.Update(hardware, timestamp, out var error);
        HardwareSensorCapture.AddValues(hardware, values, acquisitions);
        foreach (var sensor in hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature))
        {
            var raw = (double?)sensor.Value;
            var acquisition = acquisitions[sensor];
            var valid = acquisition.Problem is null && IsUsable(raw);
            readings.Add(new(sensor.Identifier.ToString(), hardware.Name + " / " + sensor.Name,
                kind, valid ? raw : null, acquisition.Timestamp,
                raw is { } v && double.IsFinite(v) ? raw : null, acquisition.Problem ?? (valid ? null : hardware.HardwareType == HardwareType.Cpu
                    ? "CPU温度が無効です。" + AccessStatus : "温度が未取得または妥当性範囲外です。対応状況・権限を確認してください")));
            // Keep the original raw value for diagnostics; a failed update never makes it a control input.
        }
        foreach (var child in hardware.SubHardware) Read(child, readings, values, kind);
    }
    private void ReadBoard(IHardware hardware, List<BoardSensor> readings, List<HardwareSensorValue> values, List<string> errors)
    {
        if (hardware is LibreHardwareMonitor.Hardware.Motherboard.Motherboard motherboard)
            errors.AddRange(motherboard.InitializationErrors.Select(e => "マザーの取得経路を初期化できません: " + e));
        var timestamp = TimeProvider.System.GetTimestamp(); var acquisitions = freshness.Update(hardware, timestamp, out var error);
        if (error is not null) errors.Add(error);
        HardwareSensorCapture.AddValues(hardware, values, acquisitions);
        foreach (var sensor in hardware.Sensors)
        {
            var (kind, unit) = sensor.SensorType switch
            {
                SensorType.Temperature => ("temperature", "°C"), SensorType.Fan => ("fan", "rpm"),
                SensorType.Control => ("control", "%"), SensorType.Voltage => ("voltage", "V"), _ => ("", "")
            };
            if (kind.Length == 0) continue;
            double? raw = sensor.Value is { } v && float.IsFinite(v) ? v : null;
            var problem = acquisitions.TryGetValue(sensor, out var acquisition) ? acquisition.Problem : error;
            var valid = problem is null && MotherboardDisplay.IsUsable(kind, raw);
            readings.Add(new(sensor.Identifier.ToString(), sensor.Name, hardware.Name, kind, unit, valid ? raw : null, raw,
                problem ?? (valid ? null : "未取得・範囲外")));
        }
        foreach (var child in hardware.SubHardware) ReadBoard(child, readings, values, errors);
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposeStarted = true;
            var errors = new List<Exception>();
            if (open)
            {
                CloseGenericFans(errors);
                foreach (var (id, fan) in fans.ToArray())
                    try { fan.Registers.Restore(); fan.Access.Dispose(); fans.Remove(id); }
                    catch (Exception e) { errors.Add(e); }
            }
            // Failed restorations retain the original access for an explicit disposal retry.
            // Computer.Close also handles resources left by an unsuccessful Computer.Open.
            if (errors.Count == 0)
                try { computer.Close(); open = false; } catch (Exception e) { errors.Add(e); }
            sensorDevices = [];
            Volatile.Write(ref board, BoardSnapshot.Empty);
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        return ValueTask.CompletedTask;
    }
    // CPU/GPU diode readings of zero commonly indicate unavailable access. Keep the raw value for diagnostics.
    public static bool IsUsable(double? celsius) => celsius is > 0 and < 150 && double.IsFinite(celsius.Value);
}
