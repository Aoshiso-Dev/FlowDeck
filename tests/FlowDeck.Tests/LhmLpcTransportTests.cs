using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;
using Pawn = LibreHardwareMonitor.PawnIo.PawnIo;
using BoardHardware = LibreHardwareMonitor.Hardware.Motherboard.Motherboard;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    // The non-owning marker is only passed to the injected managed executor, never to native I/O.
    private static SafeFileHandle InertHandle() => new(new IntPtr(12345), ownsHandle: false);
    private static Pawn InjectPawn(SafeFileHandle handle, Func<SafeFileHandle, byte[], byte[], (bool, uint, int)> execute) =>
        (Pawn)Activator.CreateInstance(typeof(Pawn), BindingFlags.NonPublic | BindingFlags.Instance, null, [handle, execute], null)!;
    private static object InjectLpc(Pawn pawn) => Activator.CreateInstance(LhmType("LibreHardwareMonitor.PawnIo.LpcIo"),
        BindingFlags.NonPublic | BindingFlags.Instance, null, [pawn], null)!;
    private static object InjectPort(object lpc, ushort register = 0x2e) => Activator.CreateInstance(
        LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.LpcPort"), BindingFlags.NonPublic | BindingFlags.Instance,
        null, [register, (ushort)(register + 1), lpc], null)!;
    private static object? InvokeLpc(object target, string method, params object[] args) => target.GetType().GetMethod(method)!.Invoke(target, args);

    [Theory]
    [InlineData("invalid")] [InlineData("closed")] [InlineData("read-failure")]
    [InlineData("load-failure")] [InlineData("load-exception")] [InlineData("success")]
    public void ModuleLoadTransfersHandleOnlyAfterSuccessAndClosesAllFailedPaths(string mode)
    {
        using var handle = mode == "invalid" ? new SafeFileHandle(new IntPtr(-1), false) : InertHandle();
        if (mode == "closed") handle.Dispose();
        var read = 0; var loaded = 0; var original = new IOException("injected failure");
        Func<SafeFileHandle> open = () => handle;
        Func<byte[]> resource = () => { read++; if (mode == "read-failure") throw original; return [1, 2, 3]; };
        Func<SafeFileHandle, byte[], bool> load = (h, bytes) =>
        {
            loaded++; Assert.Same(handle, h); Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            if (mode == "load-exception") throw original;
            return mode != "load-failure";
        };
        Pawn Run() => (Pawn)typeof(Pawn).GetMethod("LoadModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [open, resource, load])!;
        if (mode is "read-failure" or "load-exception")
            Assert.Same(original, Assert.Throws<TargetInvocationException>(() => Run()).InnerException);
        else
        {
            var module = Run(); Assert.Equal(mode == "success", module.IsLoaded);
            Assert.Equal(mode != "success", handle.IsClosed);
            module.Close(); module.Close();
        }
        Assert.True(handle.IsClosed);
        Assert.Equal(mode is "invalid" or "closed" ? 0 : 1, read);
        Assert.Equal(mode is "invalid" or "closed" or "read-failure" ? 0 : 1, loaded);
    }

    [Theory]
    [InlineData(false, 0U, 5, 1)] [InlineData(false, 0U, 0, 0)]
    [InlineData(true, 0U, 0, 1)] [InlineData(true, 7U, 0, 1)]
    [InlineData(true, 16U, 0, 1)] [InlineData(true, 8U, 0, 0)]
    public void CheckedPawnRejectsFailureShortMisalignedAndOversizedReplies(bool success, uint bytes, int error, int outputLength)
    {
        using var handle = InertHandle();
        var pawn = InjectPawn(handle, (_, _, output) => { Array.Fill(output, (byte)0x55); return (success, bytes, error); });
        var exception = Record.Exception(() => pawn.ExecuteChecked("ioctl_test", [], outputLength));
        Assert.NotNull(exception);
        if (!success && error == 5) Assert.Equal(unchecked((int)0x80070005), exception.HResult);
        if (!success && error == 0) Assert.Equal(unchecked((int)0x80004005), exception.HResult);
    }

    [Fact]
    public void CheckedPawnPreservesRealZeroAndUsesOnlyTheDeclaredInputAndOutputRegions()
    {
        using var handle = InertHandle(); var calls = 0;
        var pawn = InjectPawn(handle, (_, input, output) =>
        {
            calls++; Assert.Equal("ioctl_test", Encoding.ASCII.GetString(input, 0, 32).TrimEnd('\0'));
            Assert.Equal(40, input.Length); Assert.Equal(71L, BitConverter.ToInt64(input, 32)); Assert.Equal(8, output.Length);
            return (true, 8U, 0);
        });
        long[] values = [9, 10];
        Assert.Equal(0, pawn.ExecuteHr("ioctl_test", [71, 999], 1, values, 1, out var size));
        Assert.Equal(1U, size); Assert.Equal(new long[] { 0, 10 }, values);
        Assert.Equal(new long[] { 0 }, pawn.ExecuteChecked("ioctl_test", [71], 1)); Assert.Equal(2, calls);
    }

    public static IEnumerable<object[]> LpcCommands() =>
        from method in new[] { "SelectSlot", "FindBars", "ReadPort", "WritePort", "ReadByte", "ReadWord", "WriteByte" }
        from fail in new[] { false, true }
        select new object[] { method, fail };

    [Theory]
    [MemberData(nameof(LpcCommands))]
    public void EveryLpcCommandChecksItsActualResponseIncludingWrites(string method, bool fail)
    {
        var (function, input, args, value) = method switch
        {
            "SelectSlot" => ("ioctl_select_slot", new long[] { 1 }, new object[] { 1 }, (object?)null),
            "FindBars" => ("ioctl_find_bars", Array.Empty<long>(), Array.Empty<object>(), null),
            "ReadPort" => ("ioctl_pio_inb", [0x2eL], [(ushort)0x2e], (byte)0),
            "WritePort" => ("ioctl_pio_outb", [0x2eL, 0x87], [(ushort)0x2e, (byte)0x87], null),
            "ReadByte" => ("ioctl_superio_inb", [0x20L], [(byte)0x20], (byte)0),
            "ReadWord" => ("ioctl_superio_inw", [0x20L], [(byte)0x20], (ushort)0),
            _ => ("ioctl_superio_outb", [0x20L, 0x87], [(byte)0x20, (byte)0x87], null)
        };
        using var handle = InertHandle(); var called = 0;
        var lpc = InjectLpc(InjectPawn(handle, (_, bytes, output) =>
        {
            called++; Assert.Equal(function, Encoding.ASCII.GetString(bytes, 0, 32).TrimEnd('\0'));
            var actual = new long[(bytes.Length - 32) / 8]; Buffer.BlockCopy(bytes, 32, actual, 0, bytes.Length - 32); Assert.Equal(input, actual);
            Assert.Equal(value is null ? 0 : 8, output.Length);
            return fail ? (false, 0U, 5) : (true, (uint)output.Length, 0);
        }));
        if (fail) Assert.Equal(unchecked((int)0x80070005), Assert.Throws<TargetInvocationException>(() => InvokeLpc(lpc, method, args)).InnerException!.HResult);
        else Assert.Equal(value, InvokeLpc(lpc, method, args));
        Assert.Equal(1, called);
    }

    [Theory]
    [InlineData("ReadPort", -1L)] [InlineData("ReadPort", 256L)]
    [InlineData("ReadByte", -1L)] [InlineData("ReadByte", 256L)]
    [InlineData("ReadWord", -1L)] [InlineData("ReadWord", 65536L)]
    public void LpcRejectsOutOfRangeDataInsteadOfTruncatingIt(string method, long value)
    {
        using var handle = InertHandle(); var lpc = InjectLpc(InjectPawn(handle, (_, _, output) =>
        { BitConverter.GetBytes(value).CopyTo(output, 0); return (true, 8U, 0); }));
        var error = Assert.Throws<TargetInvocationException>(() => InvokeLpc(lpc, method, method == "ReadPort" ? (object)(ushort)0x2e : (byte)0x20));
        Assert.IsType<OverflowException>(error.InnerException);
    }

    [Fact]
    public void ARealSlotCommandFailureClosesTheModuleAndPreservesTheFailure()
    {
        using var handle = InertHandle(); var calls = 0;
        var lpc = InjectLpc(InjectPawn(handle, (_, _, _) => { calls++; return (false, 0U, 5); }));
        var error = Assert.Throws<TargetInvocationException>(() => InjectPort(lpc));
        Assert.Equal(unchecked((int)0x80070005), error.InnerException!.HResult);
        Assert.True(handle.IsClosed); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("WinbondNuvotonFintek", false)] [InlineData("WinbondNuvotonFintek", true)]
    [InlineData("IT87", false)] [InlineData("IT87", true)]
    [InlineData("Smsc", false)] [InlineData("Smsc", true)]
    public void InterruptedDetectionAttemptsConfigurationExitAndAlwaysClosesTheModule(string family, bool exitFails)
    {
        using var handle = InertHandle(); var writes = new List<long[]>(); var closing = false;
        var lpc = InjectLpc(InjectPawn(handle, (_, input, _) =>
        {
            var function = Encoding.ASCII.GetString(input, 0, 32).TrimEnd('\0');
            if (function == "ioctl_pio_outb") writes.Add([BitConverter.ToInt64(input, 32), BitConverter.ToInt64(input, 40)]);
            return closing && exitFails ? (false, 0U, 5) : (true, 0U, 0);
        }));
        var port = InjectPort(lpc); InvokeLpc(port, family + "Enter"); writes.Clear(); closing = true;
        if (exitFails) Assert.Throws<TargetInvocationException>(() => InvokeLpc(port, "Close")); else InvokeLpc(port, "Close");
        Assert.True(handle.IsClosed);
        Assert.Equal(family == "IT87" ? new long[] { 0x2e, 0x02 } : [0x2e, 0xaa], writes[0]);
        Assert.Equal(family == "IT87" && !exitFails ? 2 : 1, writes.Count);
        if (writes.Count == 2) Assert.Equal(new long[] { 0x2f, 0x02 }, writes[1]);
        InvokeLpc(port, "Close"); // A second close does not send another exit to a disposed transport.
    }

    [Fact]
    public void AnEnterSequenceFailureStillAttemptsExitAndSecondaryIteKeepsItsExistingExitException()
    {
        using var handle = InertHandle(); var writes = new List<long>(); var fail = true;
        var port = InjectPort(InjectLpc(InjectPawn(handle, (_, input, _) =>
        {
            if (Encoding.ASCII.GetString(input, 0, 32).TrimEnd('\0') != "ioctl_pio_outb") return (true, 0U, 0);
            writes.Add(BitConverter.ToInt64(input, 40)); return fail ? (false, 0U, 5) : (true, 0U, 0);
        })));
        Assert.Throws<TargetInvocationException>(() => InvokeLpc(port, "WinbondNuvotonFintekEnter"));
        fail = false; InvokeLpc(port, "Close"); Assert.Equal(new long[] { 0x87, 0xaa }, writes); Assert.True(handle.IsClosed);
        using var secondaryHandle = InertHandle(); var count = 0;
        var secondary = InjectPort(InjectLpc(InjectPawn(secondaryHandle, (_, _, _) => { count++; return (true, 0U, 0); })), 0x4e);
        InvokeLpc(secondary, "IT87Enter"); var before = count; InvokeLpc(secondary, "Close");
        Assert.Equal(before, count); Assert.True(secondaryHandle.IsClosed);
    }

    [Theory]
    [InlineData("WinbondNuvotonFintek")] [InlineData("IT87")] [InlineData("Smsc")]
    public void ExplicitSuccessfulExitIsNotRepeatedWhenThePortCloses(string family)
    {
        using var handle = InertHandle(); var calls = 0;
        var port = InjectPort(InjectLpc(InjectPawn(handle, (_, _, _) => { calls++; return (true, 0U, 0); })));
        InvokeLpc(port, family + "Enter"); InvokeLpc(port, family + "Exit"); var before = calls;
        InvokeLpc(port, "Close"); Assert.Equal(before, calls); Assert.True(handle.IsClosed);
    }

    private static BoardHardware InjectBoard(Func<object> lpc, Func<IHardware?> ec)
    {
        var constructor = typeof(BoardHardware).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var delegateType = constructor.GetParameters()[2].ParameterType;
        var parameter = Expression.Parameter(typeof(BoardHardware));
        var factory = Expression.Lambda(delegateType, Expression.Convert(Expression.Invoke(Expression.Constant(lpc)), delegateType.GetGenericArguments()[1]), parameter).Compile();
        return (BoardHardware)constructor.Invoke([RuntimeHelpers.GetUninitializedObject(typeof(SMBios)), new Settings(), factory, (Func<BoardHardware, IHardware?>)(_ => ec())]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedLpcDiscoveryKeepsTheBoardConstructibleAndIndependentEcUsable(bool ecFails)
    {
        var calls = 0; var child = new CachedHardware();
        var board = InjectBoard(() => throw new IOException("private-device-path"), () =>
        { calls++; if (ecFails) throw new UnauthorizedAccessException("private-path"); return child; });
        Assert.Equal(1, calls); Assert.Equal(ecFails ? 2 : 1, board.InitializationErrors.Length);
        Assert.Contains("SuperIO: IOException", board.InitializationErrors[0]);
        Assert.All(board.InitializationErrors, e => Assert.DoesNotContain("private", e));
        if (ecFails) Assert.Empty(board.SubHardware); else Assert.Same(child, Assert.Single(board.SubHardware));
        board.Update(); board.Close();
        // The boundary used by Computer.Open has returned normally; independent monitoring remains callable.
        var cpu = new CachedHardware(); var sensor = Sensor(cpu); cpu.OnUpdate = () => Set(sensor, 62);
        Assert.Equal(62, Read(new LhmSensorFreshness(), cpu, 1000).Value);
    }

    [Fact]
    public void FailedSuperIoWrappingClosesDiscoveredTransportAndContinuesIndependentEc()
    {
        WithIsaMutex(_ =>
        {
            using var handle = InertHandle(); var lpc = InjectLpc(InjectPawn(handle, (_, _, _) => (true, 0U, 0)));
            var port = InjectPort(lpc);
            var chip = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.F718XX"));
            LhmField(chip, "_port", port); // Null sensor arrays force actual SuperIOHardware construction to fail.
            var discovery = ConstructLpcDiscovery(io => ((IList)io.GetType().GetField("_superIOs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(io)!).Add(chip), () => { });
            var child = new CachedHardware(); var board = InjectBoard(() => discovery, () => child);
            Assert.NotEmpty(board.InitializationErrors); Assert.True(handle.IsClosed); Assert.Same(child, Assert.Single(board.SubHardware)); board.Close();
        });
    }

    [Fact]
    public async Task MotherboardInitializationFailureReachesTheBoardDiagnosticWithoutInventedReadings()
    {
        var board = InjectBoard(() => throw new IOException("private path"), () => null);
        var reader = new HardwareTemperatures(); // Computer construction only; Open/ReadAsync are not invoked.
        var readings = new List<BoardSensor>(); var values = new List<HardwareSensorValue>(); var errors = new List<string>();
        typeof(HardwareTemperatures).GetMethod("ReadBoard", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [board, readings, values, errors]);
        Assert.Contains("SuperIO: IOException", Assert.Single(errors)); Assert.Empty(readings); Assert.Empty(values);
        board.Close(); await reader.DisposeAsync();
    }

    private sealed class ClosingHardware(bool fail) : Hardware("test", new Identifier("test-close"), new Settings())
    {
        public bool Closed;
        public override HardwareType HardwareType => HardwareType.SuperIO;
        public override void Update() { }
        public override void Close() { Closed = true; if (fail) throw new IOException("child close failed"); }
    }

    [Fact]
    public void MotherboardShutdownAttemptsRemainingChildrenAndLpcAfterAChildFails()
    {
        WithIsaMutex(_ =>
        {
            var discovery = ConstructLpcDiscovery(_ => { }, () => { });
            var first = new ClosingHardware(true); var second = new ClosingHardware(false);
            var board = InjectBoard(() => discovery, () => first);
            typeof(BoardHardware).GetField("<SubHardware>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(board, new IHardware[] { first, second });
            using var handle = InertHandle();
            var port = InjectPort(InjectLpc(InjectPawn(handle, (_, _, _) => (true, 0U, 0))));
            var chip = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.F718XX"));
            LhmField(chip, "_port", port);
            ((IList)discovery.GetType().GetField("_superIOs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!).Add(chip);
            var error = Assert.Throws<AggregateException>(() => board.Close());
            Assert.IsType<IOException>(Assert.Single(error.InnerExceptions)); Assert.True(first.Closed); Assert.True(second.Closed); Assert.True(handle.IsClosed);
        });
    }
}
