using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private sealed class LifecycleHardware(Action close) : Hardware("lifecycle", new Identifier("lifecycle"), new Settings())
    {
        public override HardwareType HardwareType => HardwareType.SuperIO;
        public override void Update() { }
        public override void Close() => close();
    }
    private static object LifecycleGroup(Action close)
    {
        var board = InjectBoard(() => throw new IOException("No real LPC in lifecycle tests"), () => new LifecycleHardware(close));
        var group = RuntimeHelpers.GetUninitializedObject(LhmType("LibreHardwareMonitor.Hardware.Motherboard.MotherboardGroup"));
        LhmField(group, "_motherboards", new[] { board });
        return group;
    }
    private static void AddLifecycleGroup(Computer computer, object group)
    {
        try { typeof(Computer).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(computer, [group]); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); }
    }
    private sealed class ComputerLifecycle
    {
        public readonly List<string> Events = [];
        public readonly HashSet<string> Fail = [];
        public readonly IOException Failure = new("injected lifecycle failure");
        public Action<Computer>? Groups;
        public readonly Computer Computer;
        public ComputerLifecycle()
        {
            Func<SMBios> bios = () => { Step("bios"); return (SMBios)RuntimeHelpers.GetUninitializedObject(typeof(SMBios)); };
            Computer = (Computer)Activator.CreateInstance(typeof(Computer), BindingFlags.Instance | BindingFlags.NonPublic, null,
                [new Settings(), bios, (Action)(() => Step("mutex-open")), (Action)(() => Step("mutex-close")),
                 (Action)(() => Step("opcode-open")), (Action)(() => Step("opcode-close")),
                 (Action<Computer>)(c => { Step("groups"); Groups?.Invoke(c); })], null)!;
        }
        public void Step(string name) { Events.Add(name); if (Fail.Contains(name)) throw Failure; }
        public void Add(Computer computer, string name) => AddLifecycleGroup(computer, LifecycleGroup(() => Step("close-" + name)));
    }

    [Theory]
    [InlineData("bios")] [InlineData("mutex-open")] [InlineData("opcode-open")]
    [InlineData("groups")] [InlineData("late-group")] [InlineData("added-event")]
    public void ComputerInitializationFailureClosesOnlyStartedResourcesAndAllowsACleanRetry(string stage)
    {
        var run = new ComputerLifecycle(); run.Fail.Add(stage);
        run.Groups = c => { run.Add(c, "first"); if (stage == "late-group") run.Step(stage); };
        HardwareEventHandler handler = _ => run.Step("added-event"); run.Computer.HardwareAdded += handler;
        Assert.Same(run.Failure, Assert.Throws<IOException>(() => run.Computer.Open()));
        Assert.Empty(run.Computer.Hardware); Assert.Throws<InvalidOperationException>(() => run.Computer.SMBios);
        Assert.Equal(stage != "bios", run.Events.Contains("mutex-close"));
        Assert.Equal(stage is not ("bios" or "mutex-open"), run.Events.Contains("opcode-close"));
        Assert.Equal(stage is "late-group" or "added-event", run.Events.Contains("close-first"));
        if (run.Events.Contains("close-first")) Assert.True(run.Events.IndexOf("close-first") < run.Events.IndexOf("opcode-close"));
        run.Fail.Clear(); run.Computer.HardwareAdded -= handler;
        run.Computer.Open(); Assert.Single(run.Computer.Hardware); Assert.NotNull(run.Computer.SMBios);
        run.Computer.Close(); Assert.Empty(run.Computer.Hardware);
    }

    [Fact]
    public void ComputerFailedOpenRetainsFailedCleanupAndRejectsReopeningUntilItIsReleased()
    {
        var run = new ComputerLifecycle(); run.Fail.UnionWith(["late-group", "close-first"]);
        run.Groups = c => { run.Add(c, "first"); run.Step("late-group"); };
        var error = Assert.Throws<AggregateException>(() => run.Computer.Open());
        Assert.True(error.Flatten().InnerExceptions.Count >= 2); Assert.Empty(run.Computer.Hardware);
        Assert.DoesNotContain("opcode-close", run.Events); Assert.DoesNotContain("mutex-close", run.Events);
        int before = run.Events.Count; Assert.Throws<InvalidOperationException>(() => run.Computer.Open()); Assert.Equal(before, run.Events.Count);
        run.Fail.Clear(); run.Computer.Close();
        Assert.Equal(new[] { "close-first", "opcode-close", "mutex-close" }, run.Events.TakeLast(3));
        run.Computer.Open(); run.Computer.Close();
    }

    [Fact]
    public void ComputerShutdownContinuesOtherGroupsAndRetriesOnlyTheFailedGroupBeforeGlobalCleanup()
    {
        var run = new ComputerLifecycle(); run.Groups = c => { run.Add(c, "a"); run.Add(c, "b"); run.Add(c, "c"); };
        int removed = 0; run.Computer.HardwareRemoved += _ => removed++;
        run.Computer.Open(); run.Fail.Add("close-b"); run.Events.Clear();
        Assert.Throws<AggregateException>(() => run.Computer.Close());
        Assert.Equal(new[] { "close-c", "close-b", "close-a" }, run.Events); Assert.Equal(3, removed);
        Assert.Empty(run.Computer.Hardware); Assert.Throws<InvalidOperationException>(() => run.Computer.Open());
        run.Fail.Clear(); run.Events.Clear(); run.Computer.Close();
        Assert.Equal(new[] { "close-b", "opcode-close", "mutex-close" }, run.Events); Assert.Equal(3, removed);
        run.Events.Clear(); run.Computer.Close(); Assert.Empty(run.Events);
    }

    [Fact]
    public void ARemovalListenerFailureCannotSkipOtherListenersGroupsOrCleanup()
    {
        var run = new ComputerLifecycle(); run.Groups = c => { run.Add(c, "a"); run.Add(c, "b"); };
        int first = 0, second = 0;
        run.Computer.HardwareRemoved += _ => { first++; throw new IOException("observer failed"); };
        run.Computer.HardwareRemoved += _ => second++;
        run.Computer.Open(); var error = Assert.Throws<AggregateException>(() => run.Computer.Close());
        Assert.Equal(2, first); Assert.Equal(2, second); Assert.Equal(2, error.Flatten().InnerExceptions.Count);
        Assert.Equal(new[] { "close-b", "close-a", "opcode-close", "mutex-close" }, run.Events.TakeLast(4));
        run.Computer.Open(); // Notification failure did not create a fictitious pending hardware closure.
        Assert.Throws<AggregateException>(() => run.Computer.Close());
    }

    [Theory]
    [InlineData("opcode-close")] [InlineData("mutex-close")] [InlineData("both")]
    public void GlobalResourceCleanupFailuresRemainRetryableWithoutClosingSuccessfulResourcesAgain(string failure)
    {
        var run = new ComputerLifecycle(); run.Computer.Open();
        if (failure == "both") run.Fail.UnionWith(["opcode-close", "mutex-close"]); else run.Fail.Add(failure);
        run.Events.Clear(); Assert.Throws<AggregateException>(() => run.Computer.Close());
        Assert.Equal(new[] { "opcode-close", "mutex-close" }, run.Events);
        Assert.Throws<InvalidOperationException>(() => run.Computer.Open());
        run.Fail.Clear(); run.Events.Clear(); run.Computer.Close();
        Assert.Equal(failure == "both" ? new[] { "opcode-close", "mutex-close" } : [failure], run.Events);
        run.Computer.Open(); run.Computer.Close();
    }

    [Fact]
    public void ResetClosesTheWholeSessionBeforeOpeningAndDoesNotReopenAfterFailedCleanup()
    {
        var run = new ComputerLifecycle(); run.Groups = c => run.Add(c, "a"); run.Computer.Open();
        run.Events.Clear(); run.Computer.Reset();
        Assert.Equal(new[] { "close-a", "opcode-close", "mutex-close", "bios", "mutex-open", "opcode-open", "groups" }, run.Events);
        run.Events.Clear(); run.Fail.Add("close-a"); Assert.Throws<AggregateException>(() => run.Computer.Reset());
        Assert.Equal(new[] { "close-a" }, run.Events); Assert.Empty(run.Computer.Hardware);
        run.Fail.Clear(); run.Computer.Close();
    }

    [Theory]
    [InlineData("Open", false)] [InlineData("Close", false)] [InlineData("Reset", false)]
    [InlineData("Open", true)] [InlineData("Close", true)] [InlineData("Reset", true)]
    public void LifecycleReentryFromHardwareCallbacksIsRejectedWithoutDuplicatingResources(string operation, bool duringClose)
    {
        var run = new ComputerLifecycle(); run.Groups = c => run.Add(c, "a"); var calls = 0;
        void Handler(IHardware _)
        {
            calls++;
            var error = Assert.Throws<TargetInvocationException>(() => typeof(Computer).GetMethod(operation)!.Invoke(run.Computer, null));
            Assert.IsType<InvalidOperationException>(error.InnerException);
        }
        if (duringClose) run.Computer.HardwareRemoved += Handler; else run.Computer.HardwareAdded += Handler;
        run.Computer.Open(); run.Computer.Close(); Assert.Equal(1, calls);
        Assert.Equal(1, run.Events.Count(e => e == "mutex-open")); Assert.Equal(1, run.Events.Count(e => e == "mutex-close"));
    }

    [Fact]
    public async Task ConcurrentCloseWaitsForOpenAndDoesNotLoseTheAcquiredGroups()
    {
        var run = new ComputerLifecycle(); using var proceed = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Groups = c => { run.Add(c, "a"); entered.SetResult(); if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
        var open = Task.Run(() => run.Computer.Open());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var close = Task.Run(() => { closing.SetResult(); run.Computer.Close(); });
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(close.IsCompleted);
            proceed.Set(); await Task.WhenAll(open, close).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(run.Computer.Hardware); Assert.Equal(new[] { "close-a", "opcode-close", "mutex-close" }, run.Events.TakeLast(3));
        }
        finally { proceed.Set(); await open; }
    }

    [Fact]
    public async Task HardwareBackendRetriesAnUnfinishedOpenCleanupAndCannotResumePollingAfterDisposalStarts()
    {
        var run = new ComputerLifecycle(); run.Fail.UnionWith(["late-group", "close-a"]);
        run.Groups = c => { run.Add(c, "a"); run.Step("late-group"); };
        var reader = new HardwareTemperatures(); LhmField(reader, "computer", run.Computer);
        await Assert.ThrowsAsync<AggregateException>(() => reader.ReadAsync());
        await Assert.ThrowsAsync<AggregateException>(async () => await reader.DisposeAsync());
        int before = run.Events.Count;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync()); Assert.Equal(before, run.Events.Count);
        run.Fail.Clear(); await reader.DisposeAsync();
        Assert.Equal(new[] { "close-a", "opcode-close", "mutex-close" }, run.Events.TakeLast(3));
        before = run.Events.Count; await reader.DisposeAsync(); Assert.Equal(before, run.Events.Count);
    }

    [Fact]
    public async Task HardwareBackendRetriesFailedNormalClosureAndClearsItsPublishedCache()
    {
        var run = new ComputerLifecycle(); run.Groups = c => run.Add(c, "a");
        var reader = new HardwareTemperatures(); LhmField(reader, "computer", run.Computer);
        await reader.ReadAsync(); run.Fail.Add("close-a");
        await Assert.ThrowsAsync<AggregateException>(async () => await reader.DisposeAsync());
        Assert.Equal(BoardSnapshot.Empty, reader.Board);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadDevicesAsync(default));
        run.Fail.Clear(); await reader.DisposeAsync(); Assert.Contains("mutex-close", run.Events);
    }

    private sealed class RestoreIo : ICpuFanRegisterIo
    {
        public bool Fail;
        public readonly Dictionary<ushort, byte> Values = new()
        { [CpuFanRegisters.ModeAddress] = 0x81, [CpuFanRegisters.CommandAddress] = 90, [CpuFanRegisters.OutputAddress] = 120 };
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public IDisposable Acquire() => new Lease();
        public byte Read(ushort address) => Values[address];
        public void Write(ushort address, byte value) { if (Fail) throw new IOException("restore failed"); Values[address] = value; }
        public int ReadRpm() => 900;
    }

    [Fact]
    public async Task FailedFanRestorationRetainsItsAccessAndDefersComputerClosureUntilRetry()
    {
        var run = new ComputerLifecycle(); var reader = new HardwareTemperatures(); LhmField(reader, "computer", run.Computer);
        await reader.ReadAsync();
        var fans = (Dictionary<string, (CpuFanRegisters, NctCpuFanAccess)>)typeof(HardwareTemperatures)
            .GetField("fans", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        using var firstMutex = new Mutex(); using var secondMutex = new Mutex();
        NctCpuFanAccess Access(Mutex mutex)
        {
            var access = (NctCpuFanAccess)RuntimeHelpers.GetUninitializedObject(typeof(NctCpuFanAccess)); LhmField(access, "bus", mutex); return access;
        }
        var firstIo = new RestoreIo(); var secondIo = new RestoreIo();
        var first = new CpuFanRegisters(firstIo); var second = new CpuFanRegisters(secondIo);
        first.Write(50); second.Write(60); fans.Add("first", (first, Access(firstMutex))); fans.Add("second", (second, Access(secondMutex)));
        firstIo.Fail = true; await Assert.ThrowsAsync<AggregateException>(async () => await reader.DisposeAsync());
        Assert.True(first.NeedsRestore); Assert.False(second.NeedsRestore); Assert.Equal("first", Assert.Single(fans).Key);
        Assert.DoesNotContain("mutex-close", run.Events);
        Assert.True(firstMutex.WaitOne(0)); firstMutex.ReleaseMutex(); Assert.Throws<ObjectDisposedException>(() => secondMutex.WaitOne(0));
        firstIo.Fail = false; await reader.DisposeAsync();
        Assert.False(first.NeedsRestore); Assert.Empty(fans); Assert.Equal(0x81, firstIo.Values[CpuFanRegisters.ModeAddress]);
        Assert.Equal(90, firstIo.Values[CpuFanRegisters.CommandAddress]); Assert.Contains("mutex-close", run.Events);
        Assert.Throws<ObjectDisposedException>(() => firstMutex.WaitOne(0));
    }
}
