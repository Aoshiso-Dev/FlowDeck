using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private static void WithIsaMutex(Action<Mutex> action)
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_isaBusMutex"); var previous = field.GetValue(null); using var mutex = new Mutex();
            field.SetValue(null, mutex);
            try { action(mutex); }
            finally { field.SetValue(null, previous); }
        }
    }
    public static IEnumerable<object[]> IsaChipMethods() =>
        from chip in new[] { "F718XX", "IT87XX", "W836XX", "Nct677X" }
        from method in new[] { "Update", "GetReport", "SetControl" }
        from recursive in new[] { false, true }
        select new object[] { chip, method, recursive };

    [Theory]
    [MemberData(nameof(IsaChipMethods))]
    public void OtherCompiledSuperIoMethodsReleaseExactlyTheirOwnLeaseWhenIoThrows(string name, string method, bool recursive)
    {
        WithIsaMutex(mutex =>
        {
            Assert.True(LhmSensorFreshness.PatchedLibrary);
            var type = LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc." + name);
            var chip = RuntimeHelpers.GetUninitializedObject(type);
            // No port or module exists. Initialize the chip's shape so the actual register-read
            // path, rather than an absent bookkeeping array, throws on its missing LPC transport.
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(float?[])) field.SetValue(chip, new float?[1]);
                else if (field.FieldType == typeof(byte[])) field.SetValue(chip, new byte[8]);
                else if (field.FieldType == typeof(bool[])) field.SetValue(chip, new bool[8]);
                else if (field.FieldType == typeof(ushort[])) field.SetValue(chip, new ushort[8]);
            }
            if (name == "IT87XX")
            {
                var count = type.GetField("_bankCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
                count.SetValue(chip, Convert.ChangeType(1, count.FieldType));
                LhmField(chip, "_requiresBankSelect", true);
            }
            if (name == "Nct677X") LhmField(chip, "_isNuvotonVendor", true);
            if (recursive) Assert.True(mutex.WaitOne(0));
            try
            {
                var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod(method)!.Invoke(chip, method == "SetControl" ? [0, (byte?)100] : null));
                Assert.IsType<NullReferenceException>(error.InnerException);
                Assert.Equal(!recursive, ProbeMutex(mutex));
            }
            finally { if (recursive) mutex.ReleaseMutex(); }
            Assert.True(ProbeMutex(mutex));
        });
    }

    private static object ConstructLpcDiscovery(Action<object> detect, Action createBmc)
    {
        var type = LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.LpcIO");
        var constructor = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var parameters = constructor.GetParameters(); var target = Expression.Parameter(type, "target");
        var detection = Expression.Lambda(parameters[0].ParameterType,
            Expression.Invoke(Expression.Constant(detect), Expression.Convert(target, typeof(object))), target).Compile();
        var ipmiType = parameters[1].ParameterType.GetGenericArguments().Single();
        var bmc = Expression.Lambda(parameters[1].ParameterType,
            Expression.Block(Expression.Invoke(Expression.Constant(createBmc)), Expression.Default(ipmiType))).Compile();
        return constructor.Invoke([detection, bmc]);
    }

    [Theory]
    [InlineData("success", false)] [InlineData("success", true)]
    [InlineData("detect", false)] [InlineData("detect", true)]
    [InlineData("bmc", false)] [InlineData("bmc", true)]
    public void DiscoveryReleasesIsaBeforeBmcWorkAndPreservesRecursiveOwnership(string failure, bool recursive)
    {
        WithIsaMutex(mutex =>
        {
            int detected = 0, bmc = 0;
            if (recursive) Assert.True(mutex.WaitOne(0));
            try
            {
                object Run() => ConstructLpcDiscovery(_ => {
                    detected++; Assert.False(ProbeMutex(mutex));
                    if (failure == "detect") throw new IOException("discovery failed");
                }, () => {
                    bmc++; Assert.Equal(!recursive, ProbeMutex(mutex));
                    if (failure == "bmc") throw new IOException("BMC failed");
                });
                if (failure == "success") _ = Run();
                else Assert.IsType<IOException>(Assert.Throws<TargetInvocationException>(() => Run()).InnerException);
                Assert.Equal(1, detected); Assert.Equal(failure == "detect" ? 0 : 1, bmc);
                Assert.Equal(!recursive, ProbeMutex(mutex));
            }
            finally { if (recursive) mutex.ReleaseMutex(); }
            Assert.True(ProbeMutex(mutex));
        });
    }

    [Fact]
    public void DiscoveryWithoutIsaOwnershipDoesNotStartDetectionOrBmcProbing()
    {
        lock (LibraryMutexGate)
        {
            var field = MutexField("_isaBusMutex"); var previous = field.GetValue(null); field.SetValue(null, null);
            try
            {
                int calls = 0;
                var target = ConstructLpcDiscovery(_ => calls++, () => calls++);
                Assert.Equal(0, calls); Assert.Empty((IEnumerable)target.GetType().GetProperty("SuperIO")!.GetValue(target)!);
            }
            finally { field.SetValue(null, previous); }
        }
    }

    private static object LpcModuleWithInvalidHandle(SafeFileHandle handle)
    {
        var pawn = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.PawnIo.PawnIo"));
        LhmField(pawn, "_handle", handle);
        var module = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.PawnIo.LpcIo"));
        LhmField(module, "_pawnIO", pawn); LhmField(module, "_singleArgArray", new long[1]);
        return module;
    }

    [Fact]
    public void FailedSlotSelectionClosesTheModuleInsteadOfLeakingItsHandle()
    {
        using var handle = new SafeFileHandle(new IntPtr(-1), false);
        var module = LpcModuleWithInvalidHandle(handle);
        // Force the compiled selection method to fail before any transport call. The inert
        // wrapper must still close when construction does not transfer ownership to a port.
        LhmField(module, "_singleArgArray", null);
        var type = LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.LpcPort");
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var error = Assert.Throws<TargetInvocationException>(() => constructor.Invoke([(ushort)0x2e, (ushort)0x2f, module]));
        Assert.IsType<NullReferenceException>(error.InnerException); Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedDiscoveryClosesAllPreviouslyFoundDevicesEvenIfOneCloseThrows(bool failInBmc)
    {
        WithIsaMutex(mutex =>
        {
            using var handle = new SafeFileHandle(new IntPtr(-1), false);
            var type = LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.F718XX");
            var failing = RuntimeHelpers.GetUninitializedObject(type); // Null port faults Close without any hardware access.
            var following = RuntimeHelpers.GetUninitializedObject(type);
            var port = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.Hardware.Motherboard.Lpc.LpcPort"));
            LhmField(port, "_pawnModule", LpcModuleWithInvalidHandle(handle)); LhmField(following, "_port", port);
            var error = Assert.Throws<TargetInvocationException>(() => ConstructLpcDiscovery(io => {
                var list = (IList)io.GetType().GetField("_superIOs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(io)!;
                list.Add(failing); list.Add(following);
                if (!failInBmc) throw new IOException("original discovery failure");
            }, () => throw new IOException("original BMC failure")));
            Assert.IsType<IOException>(error.InnerException); Assert.StartsWith("original", error.InnerException!.Message);
            Assert.True(handle.IsClosed); Assert.True(ProbeMutex(mutex));
        });
    }
}
