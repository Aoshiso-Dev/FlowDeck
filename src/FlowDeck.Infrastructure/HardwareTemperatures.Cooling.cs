using FlowDeck.Core;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Infrastructure;

public sealed partial class HardwareTemperatures
{
    private sealed class GenericFan(MotherboardCoolingIdentity identity, IHardware hardware, MotherboardFanPort port, IFanRecoveryJournal journal, NctCpuFanAccess access)
    {
        public MotherboardCoolingIdentity Identity = identity;
        public IHardware Hardware = hardware;
        public MotherboardFanPort Port = port;
        public IFanRecoveryJournal Journal = journal;
        public NctCpuFanAccess Access = access;
        public CpuFanRegisters Registers = null!;
        public long Timestamp;
    }
    private readonly Dictionary<string, GenericFan> genericFans = [];
    private (IHardware Hardware, MotherboardDefinition Definition) MatchBoard(MotherboardCoolingIdentity expected)
    {
        if (!IsAdministrator || !open) throw new MotherboardCommandRejectedException("管理者サービスでセンサーを初期化してください");
        var root = computer.Hardware.SingleOrDefault(h => h.HardwareType == HardwareType.Motherboard && h.Identifier.ToString() == expected.NativeId)
            ?? throw new MotherboardCommandRejectedException("対象のマザーを検出できません");
        if (!physical.TryGetValue(root, out var captured)) throw new MotherboardCommandRejectedException("マザーを再検出してください");
        var observed = HardwareSensorCapture.Describe(root, computer.SMBios, captured.Model, captured.Connection, 0, []);
        if (observed.DefinitionId is null || MotherboardCoolingIdentity.From(observed) != expected)
            throw new MotherboardCommandRejectedException("マザーの個体・BIOS・接続・制御チップが変更されています");
        return (root, MotherboardDefinitionCatalog.BuiltIn.Boards.Single(b => b.Id == observed.DefinitionId));
    }
    private GenericFan GenericPort(MotherboardFanCommand command, IFanRecoveryJournal journal)
    {
        var (root, definition) = MatchBoard(command.Identity);
        if (fans.Values.Any(f => f.Registers.NeedsRestore)) throw new MotherboardCommandRejectedException("従来の制御を終了してから汎用制御を開始してください");
        var port = definition.Ports.SingleOrDefault(p => p.Id == command.EndpointId)
            ?? throw new MotherboardCommandRejectedException("この定義に対象の端子がありません");
        var hardware = root.SubHardware.SingleOrDefault(h => h.Identifier.ToString() == port.HardwareId)
            ?? throw new MotherboardCommandRejectedException("対象端子の制御チップがありません");
        var key = command.Identity.NativeId + ":" + port.Id;
        if (genericFans.TryGetValue(key, out var previous))
        {
            if (previous.Identity == command.Identity && ReferenceEquals(previous.Hardware, hardware) && ReferenceEquals(previous.Journal, journal)) return previous;
            if (previous.Registers.NeedsRestore) throw new MotherboardCommandRejectedException("旧接続の端子制御が未復旧です");
            previous.Access.Dispose(); genericFans.Remove(key);
        }
        var access = new NctCpuFanAccess(hardware, port.Channel, port.Chip);
        var entry = new GenericFan(command.Identity, hardware, port, journal, access);
        entry.Registers = new(access, channel: port.Channel, chip: port.Chip, timestamp: () => entry.Timestamp, recovery: journal, guardOwnedState: true);
        genericFans.Add(key, entry); return entry;
    }
    private void RefreshGenericFan(GenericFan entry)
    {
        var timestamp = TimeProvider.System.GetTimestamp(); var acquired = freshness.Update(entry.Hardware, timestamp, out var error);
        if (error is not null) throw new IOException(error);
        var rpm = entry.Hardware.Sensors.SingleOrDefault(s => s.Identifier.ToString() == entry.Port.SensorId);
        if (rpm is null || !acquired.TryGetValue(rpm, out var reading) || reading.Problem is not null)
            throw new IOException("対象端子の回転数を新しく取得できません");
        entry.Timestamp = reading.Timestamp;
    }
    private CpuFanReading? ExecuteFanCore(MotherboardFanCommand command, IFanRecoveryJournal journal, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposeStarted, this);
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(command.Operation) || command.Operation == MotherboardFanOperation.Write &&
            (command.Duty is not (>= 1 and <= 100) || command.ExpectedDuty is not (>= 0 and <= 100)) ||
            command.Operation != MotherboardFanOperation.Write && (command.Duty is not null || command.ExpectedDuty is not null || command.Deadline is not null || command.ExpectedControl is not null))
            throw new MotherboardCommandRejectedException("マザー操作の形式が不正です");
        var entry = GenericPort(command, journal);
        if (command.Operation == MotherboardFanOperation.Release)
        {
            entry.Registers.RecoverInterruptedControl();
            try { RefreshGenericFan(entry); return entry.Registers.Read(); }
            catch { return null; } // Register restoration is already verified; unavailable telemetry does not undo its completion.
        }
        RefreshGenericFan(entry);
        if (command.Operation == MotherboardFanOperation.Read) return entry.Registers.ReadForControl();
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            MatchBoard(command.Identity);
            if (HardwareWriteDeadline.Expired(command.Deadline)) throw new MotherboardCommandRejectedException("通信待ちで温度の有効期限を超えました", DeviceDriverFailure.CoolingInputExpired);
        }
        return entry.Registers.WriteWithControlSnapshot(command.Duty!.Value, Check, command.ExpectedDuty, command.ExpectedControl);
    }
    public Task<CpuFanReading?> ExecuteFanAsync(MotherboardFanCommand command, IFanRecoveryJournal journal, CancellationToken ct) =>
        Task.Run(() => { lock (gate) return ExecuteFanCore(command, journal, ct); }, ct);
    private void CloseGenericFans(List<Exception> errors)
    {
        foreach (var (key, entry) in genericFans.ToArray())
        {
            try { MatchBoard(entry.Identity); entry.Registers.RecoverInterruptedControl(); entry.Access.Dispose(); genericFans.Remove(key); }
            catch (Exception e) { errors.Add(new IOException(entry.Port.Name + ": " + e.Message, e)); }
        }
    }
}
