using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class MotherboardDeviceDriverTests
{
    [Theory, MemberData(nameof(Ports))] public async Task UnconfiguredBoardEndpointTrialRestoresOriginalRegistersAndLeavesSetupUnconfirmed(string id)
    {
        await using var rig=await Rig.Create(configure:false); var port=MotherboardFans.Find(id); var before=rig.Backend.Banks.ToDictionary(p=>p.Key,p=>new Dictionary<ushort,byte>(p.Value.Values));
        Assert.Null(rig.Driver.ValidateCalibrationStart(id,rig.Driver.BindingCandidate!));
        var result=await rig.Driver.BeginCalibrationAsync(CalibrationTestActuation.Create(id,rig.Driver.BindingCandidate!,100,67),default);
        Assert.Equal(Outcome.Confirmed,result.Result.Outcome); Assert.Equal(100,(await rig.Driver.ReadCalibrationAsync(id,rig.Driver.BindingCandidate!,default)).Duty);
        Assert.NotNull(rig.Driver.ValidateCoolingSetup(id)); Assert.Single(rig.Journal().Pending);
        Assert.Equal(Outcome.Confirmed,(await rig.Driver.EndCalibrationAsync(id,default)).Outcome); Assert.Empty(rig.Journal().Pending);
        // The EC request register is a transaction doorbell, not a saved fan setting.
        // Its required final value is 0x40; every mode, command and output must be restored.
        foreach(var bank in rig.Backend.Banks)
        {
            var request=NctFanMap.For(port.Chip,port.Channel).Request;
            Assert.Equal(before[bank.Key].OrderBy(p=>p.Key),bank.Value.Values.Where(p=>bank.Key!=port.Chip||p.Key!=request).OrderBy(p=>p.Key));
            if(bank.Key==port.Chip&&request is { } address) Assert.Equal(0x40,bank.Value.Values[address]);
        }
        Assert.Null(rig.Driver.CoolingConfiguration(port.Id));
    }
    [Fact] public async Task BoardCalibrationCanTestBelowTheConfiguredFloorWithoutChangingTheSavedSafety()
    {
        await using var rig=await Rig.Create(); var port=MotherboardFans.Cpu; var safety=rig.Driver.CoolingConfiguration(port.Id)!.Safety;
        var result=await rig.Driver.BeginCalibrationAsync(CalibrationTestActuation.Create(port.Id,rig.Driver.BindingCandidate!,20,67),default);
        Assert.Equal(Outcome.Confirmed,result.Result.Outcome); Assert.Equal(20,(await rig.Driver.ReadCalibrationAsync(port.Id,rig.Driver.BindingCandidate!,default)).Duty);
        Assert.Equal(safety,rig.Driver.CoolingConfiguration(port.Id)!.Safety); await rig.Driver.EndCalibrationAsync(port.Id,default); Assert.Equal(67,(int)Math.Round(rig.Backend.Duty(port)));
    }
    [Fact] public async Task BoardTrialPartialRegisterFailureKeepsRecoveryAndDoesNotConfirmSetup()
    {
        await using var rig=await Rig.Create(configure:false); var port=MotherboardFans.Cpu; var map=NctFanMap.For(port.Chip,port.Channel);
        rig.Backend.Banks[port.Chip].FailOnce=map.Mode;
        var result=await rig.Driver.BeginCalibrationAsync(CalibrationTestActuation.Create(port.Id,rig.Driver.BindingCandidate!,100,67),default);
        Assert.Equal(Outcome.Unknown,result.Result.Outcome); Assert.Single(rig.Journal().Pending);
        Assert.Equal(Outcome.Confirmed,(await rig.Driver.EndCalibrationAsync(port.Id,default)).Outcome); Assert.Empty(rig.Journal().Pending); Assert.Null(rig.Driver.CoolingConfiguration(port.Id));
    }
    [Fact] public async Task SetupDescriptorsUseBoardManagementForEveryDefinedPortWithoutWriting()
    {
        await using var r = await Rig.Create(configure: false);
        var values = r.Driver.DescribeSetup(); Assert.Equal(MotherboardFans.Ports.Count, values.Length);
        Assert.All(values, v => { Assert.Equal(new[] { CoolingExitAction.ReleaseToDevice }, v.CoolingExitActions); Assert.Empty(v.LightingExitActions); });
    }
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Bank
    {
        public Dictionary<ushort, byte> Values = [];
        public List<(ushort Address, byte Value)> Writes = [];
        public ushort? FailOnce;
    }
    private sealed class Registers(Bank bank, NctFanMap map) : ICpuFanRegisterIo
    {
        public IDisposable Acquire() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public byte Read(ushort address) => bank.Values[address];
        public int ReadRpm() => 900;
        public void WaitForController() { }
        public void Write(ushort address, byte value)
        {
            if (bank.FailOnce == address) { bank.FailOnce = null; throw new IOException("partial register write"); }
            bank.Values[address] = value; bank.Writes.Add((address, value));
            if (address == map.Command) bank.Values[map.Output] = value;
        }
    }
    private sealed class Backend : IHardwareSensorBackend, IMotherboardCoolingBackend
    {
        public TestClock Clock { get; } = new();
        public Dictionary<string, Bank> Banks = [];
        private readonly Dictionary<string, (CpuFanRegisters Registers, IFanRecoveryJournal Journal)> registers = [];
        public string Fingerprint = new('a', 64);
        public double CpuTemperature = 50;
        public bool FailRead, SwapAtWrite;
        public Action<MotherboardFanCommand>? BeforeExecute;
        public bool SupplyControlSnapshot = true;
        public List<MotherboardFanCommand> Commands = [];
        public int Disposals;
        public Backend()
        {
            foreach (var port in MotherboardFans.Ports)
            {
                if (!Banks.TryGetValue(port.Chip, out var bank)) Banks.Add(port.Chip, bank = new());
                var map = NctFanMap.For(port.Chip, port.Channel); bank.Values[map.Mode] = map.SharedMode ? (byte)0 : (byte)32;
                bank.Values[map.Command] = bank.Values[map.Output] = 170;
            }
        }
        public int Writes => Banks.Values.Sum(b => b.Writes.Count);
        public double Duty(MotherboardFanPort port) => Banks[port.Chip].Values[NctFanMap.For(port.Chip, port.Channel).Output] * 100d / 255;
        public HardwareSensorDevice Board => new("/motherboard", "ASRock X870E Taichi White", DeviceKind.Motherboard, Fingerprint, "verified-board-serial",
            new('b', 32), "asrock-x870e-taichi-white-v1", MotherboardFans.Ports.SelectMany(p => new HardwareSensorValue[]
            { new(p.SensorId, p.Name, p.HardwareId, DeviceFeature.Rpm, 900, Clock.GetTimestamp()),
              new(p.HardwareId + "/control/" + p.Channel, p.Name, p.HardwareId, DeviceFeature.DutyRead, Duty(p), Clock.GetTimestamp()) }).ToArray());
        public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => FailRead ? throw new IOException("read failed") : Task.FromResult(new[]
        {
            Board, new("/cpu/0", "CPU", DeviceKind.Processor, new('c', 64), "cpu-serial", new('d', 32), null,
                [new("/cpu/0/temperature/0", "Package", "/cpu/0", DeviceFeature.Temperature, CpuTemperature, Clock.GetTimestamp())])
        });
        public Task<CpuFanReading?> ExecuteFanAsync(MotherboardFanCommand command, IFanRecoveryJournal journal, CancellationToken ct)
        {
            Commands.Add(command); BeforeExecute?.Invoke(command);
            if (SwapAtWrite && command.Operation == MotherboardFanOperation.Write) Fingerprint = new('e', 64);
            if (command.Identity != MotherboardCoolingIdentity.From(Board)) throw new MotherboardCommandRejectedException("board changed");
            var port = MotherboardFans.Find(command.EndpointId); var map = NctFanMap.For(port.Chip, port.Channel);
            if (!registers.TryGetValue(port.Id, out var entry) || !ReferenceEquals(entry.Journal, journal))
                registers[port.Id] = entry = (new(new Registers(Banks[port.Chip], map), Clock, port.Channel, port.Chip,
                    () => Clock.GetTimestamp(), journal, guardOwnedState: true), journal);
            if (command.Operation == MotherboardFanOperation.Release) entry.Registers.RecoverInterruptedControl();
            CpuFanReading? value = command.Operation == MotherboardFanOperation.Write ? entry.Registers.WriteWithControlSnapshot(command.Duty!.Value, () =>
            {
                if (HardwareWriteDeadline.Expired(command.Deadline)) throw new MotherboardCommandRejectedException("expired", DeviceDriverFailure.CoolingInputExpired);
            }, command.ExpectedDuty, command.ExpectedControl) : command.Operation == MotherboardFanOperation.Read && SupplyControlSnapshot
                ? entry.Registers.ReadForControl() : entry.Registers.Read();
            return Task.FromResult<CpuFanReading?>(value);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly Backend Backend = new();
        public HardwareSensorProvider Provider = null!;
        public DeviceControlRuntime Runtime = null!;
        public MotherboardDeviceDriver Driver = null!;
        private DeviceServiceHello? hello;
        private long sequence;
        public static async Task<Rig> Create(bool configure = true)
        {
            var r = new Rig(); r.Provider = new(Installation, r.Backend, new(r.Directory.Path, "boot", r.Backend.Clock));
            r.Runtime = new([], ["user"], new TestLog(), r.Backend.Clock, providers: [r.Provider]); await r.Runtime.PollAsync();
            r.Driver = Assert.Single(r.Provider.CachedDrivers.OfType<MotherboardDeviceDriver>());
            if (configure) r.Driver.Configure(r.Driver.BindingCandidate, MotherboardFans.Ports.Select(p => r.Config(p)));
            return r;
        }
        public CoolingEndpointConfiguration Config(MotherboardFanPort port, int minimum = 40) => new(new(Driver.DeviceId, port.Id), true,
            new(minimum, 100, null, CoolingExitAction.ReleaseToDevice, []), []);
        public async Task<DeviceDriverResult> Apply(MotherboardFanPort port, int duty, bool fallback = false, Func<CoolingAuthorization>? authorize = null)
        {
            // This constructor stays inaccessible outside Core in production; direct driver fault tests use reflection only here.
            var actuation = (CoolingActuation)Activator.CreateInstance(typeof(CoolingActuation),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                [port.Id, duty, fallback, Driver.CoolingConfiguration(port.Id)!.Safety, authorize ?? (() => new())], null)!;
            return await Driver.ApplyCoolingAsync(actuation, default);
        }
        public async Task<DeviceServiceReply> Request(DeviceOperation op, MotherboardFanPort port, int? duty = null, CoolingTemperatureProfile? profile = null)
        {
            hello ??= Runtime.OpenSession("user"); var state = Runtime.State(); var instance = state.Inventory.Devices.Single(d => d.Device.Id == Driver.DeviceId);
            var reference = new EndpointReference(Driver.DeviceId, port.Id);
            return await Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence, "test", op,
                state.Inventory.TopologyRevision, [new(reference, instance.Generation, state.Endpoints.SingleOrDefault(s => s.Target == reference)?.PolicyRevision ?? 0)], duty, profile));
        }
        public FanRecoveryJournal Journal() => new(Path.Combine(Directory.Path, "board-" + SettingsTransfer.Hash(Driver.DeviceId)), "boot", Driver.BindingCandidate);
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Directory.Dispose(); }
    }
    public static IEnumerable<object[]> Ports() => MotherboardFans.Ports.Select(p => new object[] { p.Id });
    [Theory, MemberData(nameof(Ports))] public async Task EveryDefinedDetectedEndpointCanBeControlledAndReleasedWithoutChangingOthers(string id)
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Find(id);
        Assert.Equal(15, r.Driver.Observe()!.Endpoints.Length); Assert.Equal(0, r.Backend.Writes);
        var reply = await r.Request(DeviceOperation.SetDuty, port, 20);
        Assert.True(reply.Result?.Outcome == Outcome.Confirmed, System.Text.Json.JsonSerializer.Serialize(reply, Contract.Json)); Assert.Equal(40, (int)Math.Round(r.Backend.Duty(port)));
        Assert.All(MotherboardFans.Ports.Where(p => p != port), p => Assert.Equal(67, (int)Math.Round(r.Backend.Duty(p))));
        Assert.Single(r.Journal().Pending);
        reply = await r.Request(DeviceOperation.ReleaseCooling, port); Assert.Equal(Outcome.Confirmed, reply.Result!.Outcome);
        Assert.Equal(67, (int)Math.Round(r.Backend.Duty(port))); Assert.Empty(r.Journal().Pending);
    }
    [Fact] public async Task MissingEndpointSetupAndAChangedBoardDoNotWrite()
    {
        await using var r = await Rig.Create(configure: false); var port = MotherboardFans.Cpu;
        Assert.NotNull(r.Driver.ValidateCoolingSetup(port.Id));
        r.Driver.Configure(r.Driver.BindingCandidate, [r.Config(port)]); Assert.Null(r.Driver.ValidateCoolingSetup(port.Id));
        Assert.NotNull(r.Driver.ValidateCoolingSetup(MotherboardFans.Chassis.Id));
        r.Backend.SwapAtWrite = true; Assert.Equal(Outcome.Rejected, (await r.Apply(port, 55)).Outcome); Assert.Equal(0, r.Backend.Writes);
    }
    [Fact] public async Task RestoreIsIndependentForEachEndpointAndDoesNotOverrideAnExternalOwner()
    {
        await using var r = await Rig.Create(); var cpu = MotherboardFans.Cpu; var chassis = MotherboardFans.Chassis;
        Assert.Equal(Outcome.Confirmed, (await r.Apply(cpu, 55)).Outcome); Assert.Equal(Outcome.Confirmed, (await r.Apply(chassis, 60)).Outcome);
        var map = NctFanMap.For(cpu.Chip, cpu.Channel); r.Backend.Banks[cpu.Chip].Values[map.Command] = 230;
        var results = await r.Driver.ReleaseAsync(DeviceReleaseReason.ClientExpired, default);
        Assert.Equal(Outcome.Unknown, results.Single(x => x.EndpointId == cpu.Id).Result.Outcome);
        Assert.Equal(Outcome.Confirmed, results.Single(x => x.EndpointId == chassis.Id).Result.Outcome);
        Assert.Single(r.Journal().Pending); Assert.Equal(230, r.Backend.Banks[cpu.Chip].Values[map.Command]);
        // Resolve the simulated foreign change so disposal can complete its pending recovery.
        r.Backend.Banks[cpu.Chip].Values[map.Command] = r.Journal().Pending[0].IntendedCommand;
    }
    [Fact] public async Task PartialWritesLeaveABoundRecoveryRecordAndRestoreTheOriginalMode()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Cpu; var map = NctFanMap.For(port.Chip, port.Channel);
        r.Backend.Banks[port.Chip].FailOnce = map.Mode;
        Assert.Equal(Outcome.Unknown, (await r.Apply(port, 55)).Outcome); Assert.Single(r.Journal().Pending);
        var result = Assert.Single(await r.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(Outcome.Confirmed, result.Result.Outcome); Assert.Equal(32, r.Backend.Banks[port.Chip].Values[map.Mode]); Assert.Empty(r.Journal().Pending);
    }
    [Fact] public async Task CoolingUsesTheSharedCpuSourceAndFaultOutputThenRestoresOnDisconnect()
    {
        await using var r = await Rig.Create(); var state = r.Runtime.State();
        var cpu = state.Telemetry!.Single(s => s.Feature == DeviceFeature.Temperature).Target;
        var profile = new CoolingTemperatureProfile("cpu-curve", "CPU", 1, [new(cpu, [new(30, 50), new(70, 100)], 90, 80)], 2, 5);
        var automatic = await r.Request(DeviceOperation.SetAutomatic, MotherboardFans.Cpu, profile: profile);
        Assert.True(automatic.Result?.Outcome == Outcome.Confirmed, System.Text.Json.JsonSerializer.Serialize(automatic, Contract.Json));
        Assert.Equal(75, (int)Math.Round(r.Backend.Duty(MotherboardFans.Cpu)));
        r.Backend.CpuTemperature = 95; await r.Runtime.PollAsync(); await r.Runtime.TickCoolingAsync();
        Assert.Equal(100, (int)Math.Round(r.Backend.Duty(MotherboardFans.Cpu)));
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == MotherboardFans.Cpu.Id).Cooling!.Faulted);
        await r.Driver.ReleaseAsync(DeviceReleaseReason.ClientDisconnected, default); Assert.Equal(67, (int)Math.Round(r.Backend.Duty(MotherboardFans.Cpu)));
    }
    [Fact] public async Task ReadFailuresAndNewPollsCannotBeHiddenByThePostWriteCache()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Cpu;
        Assert.Equal(Outcome.Confirmed, (await r.Apply(port, 55)).Outcome);
        Assert.Equal(55, Math.Round(r.Driver.ReadCachedSamples().Single(s => s.EndpointId == port.Id && s.Feature == DeviceFeature.DutyRead).Value!.Value));
        var map = NctFanMap.For(port.Chip, port.Channel); r.Backend.Banks[port.Chip].Values[map.Output] = 230;
        await r.Runtime.PollAsync(); Assert.Equal(90, Math.Round(r.Driver.ReadCachedSamples().Single(s => s.EndpointId == port.Id && s.Feature == DeviceFeature.DutyRead).Value!.Value));
        r.Backend.FailRead = true; await r.Runtime.PollAsync();
        Assert.All(r.Driver.ReadCachedSamples(), s => Assert.Null(s.Value));
        r.Backend.FailRead = false;
    }
    [Fact] public async Task ReconfigurationWhileOwnedIsRejectedAndAnExpiredAuthorizationDoesNotWrite()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Cpu;
        var rejected = await r.Apply(port, 55, authorize: () => new("expired", Failure: DeviceDriverFailure.CoolingInputExpired));
        Assert.Equal(Outcome.Rejected, rejected.Outcome); Assert.Equal(DeviceDriverFailure.CoolingInputExpired, rejected.Failure); Assert.Equal(0, r.Backend.Writes);
        Assert.Equal(Outcome.Confirmed, (await r.Apply(port, 55)).Outcome);
        Assert.Throws<IOException>(() => r.Driver.Configure(r.Driver.BindingCandidate, [r.Config(port, 50)]));
    }
    [Fact] public async Task ARecreatedDriverLoadsBoundRecoveryBeforeAcceptingAnyNewControl()
    {
        await using var r = await Rig.Create(); var port = MotherboardFans.Cpu;
        Assert.Equal(Outcome.Confirmed, (await r.Apply(port, 55)).Outcome);
        var next = new HardwareSensorProvider(Installation, r.Backend, new(r.Directory.Path, "boot", r.Backend.Clock));
        await next.PollAsync(default); var recovered = Assert.Single(next.CachedDrivers.OfType<MotherboardDeviceDriver>());
        Assert.Throws<IOException>(() => recovered.Configure(recovered.BindingCandidate, [r.Config(port)]));
        var result = Assert.Single(await recovered.ReleaseAsync(DeviceReleaseReason.ServiceStopping, default));
        Assert.Equal(Outcome.Confirmed, result.Result.Outcome); Assert.Equal(67, (int)Math.Round(r.Backend.Duty(port)));
        Assert.Empty(new FanRecoveryJournal(Path.Combine(r.Directory.Path, "board-" + SettingsTransfer.Hash(r.Driver.DeviceId)), "boot", r.Driver.BindingCandidate).Pending);
    }
    [Fact] public async Task SensorBatchFailureStillAllowsTheOwnedFanToReadRegistersAndRaiseItsFaultOutput()
    {
        await using var r = await Rig.Create(); var cpu = r.Runtime.State().Telemetry!.Single(s => s.Feature == DeviceFeature.Temperature).Target;
        var profile = new CoolingTemperatureProfile("cpu-curve", "CPU", 1, [new(cpu, [new(30, 50), new(70, 100)], 90, 80)], 2, 5);
        Assert.Equal(Outcome.Confirmed, (await r.Request(DeviceOperation.SetAutomatic, MotherboardFans.Cpu, profile: profile)).Result!.Outcome);
        r.Backend.FailRead = true; await r.Runtime.PollAsync(); await r.Runtime.TickCoolingAsync();
        Assert.Equal(100, (int)Math.Round(r.Backend.Duty(MotherboardFans.Cpu)));
        Assert.True(r.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == MotherboardFans.Cpu.Id).Cooling!.Faulted);
        r.Backend.FailRead = false;
    }
}
