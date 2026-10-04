using System.Runtime.InteropServices;
using System.Text;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class NvidiaReadOnlyMonitorTests
{
    internal sealed class Library : INvidiaMonitorLibrary
    {
        public readonly Dictionary<uint, Delegate> Functions;
        public List<uint> Queries { get; } = [];
        public nint[] Handles = [17];
        public uint? Count;
        public string Name = "Synthetic GPU";
        public int InitializeStatus, UnloadStatus, EnumerateStatus, ThermalStatus, TachStatus;
        public int Temperature = 52;
        public uint Rpm = 900, ThermalCount = 1;
        public int Target = 1;
        public int Initialized, Unloaded, Disposed, FailDispose;
        public Action? BeforeThermal;
        public Library()
        {
            Functions = new()
            {
                [NvidiaReadOnlyMonitor.InitializeId] = new NvidiaReadOnlyMonitor.Lifecycle(() => { Initialized++; return InitializeStatus; }),
                [NvidiaReadOnlyMonitor.UnloadId] = new NvidiaReadOnlyMonitor.Lifecycle(() => { Unloaded++; return UnloadStatus; }),
                [NvidiaReadOnlyMonitor.EnumerateId] = new NvidiaReadOnlyMonitor.Enumerate((nint[] handles, out uint count) =>
                { Handles.Take(64).ToArray().CopyTo(handles, 0); count = Count ?? (uint)Handles.Length; return EnumerateStatus; }),
                [NvidiaReadOnlyMonitor.NameId] = new NvidiaReadOnlyMonitor.ReadName((_, bytes) => { Encoding.ASCII.GetBytes(Name).Take(63).ToArray().CopyTo(bytes, 0); return 0; }),
                [NvidiaReadOnlyMonitor.ThermalId] = new NvidiaReadOnlyMonitor.ReadThermal((nint _, uint sensor, ref NvidiaReadOnlyMonitor.ThermalSettings settings) =>
                {
                    Assert.Equal(15u, sensor); Assert.Equal(68u | 2u << 16, settings.Version); BeforeThermal?.Invoke();
                    settings.Count = ThermalCount; settings.Sensors[0].Temperature = Temperature; settings.Sensors[0].Target = Target;
                    return ThermalStatus;
                }),
                [NvidiaReadOnlyMonitor.TachId] = new NvidiaReadOnlyMonitor.ReadTach((nint _, out uint rpm) => { rpm = Rpm; return TachStatus; })
            };
        }
        public nint Find(uint id) { Queries.Add(id); return Functions.TryGetValue(id, out var function) ? Marshal.GetFunctionPointerForDelegate(function) : 0; }
        public void Dispose() { if (FailDispose-- > 0) throw new IOException("mock unload failure"); Disposed++; }
    }
    [Fact] public async Task LazyReaderBindsOnlyReadAndLifecycleApisAndPairsInitializationWithUnload()
    {
        var lib = new Library(); var factories = 0;
        await using var reader = new NvidiaReadOnlyMonitor(() => { factories++; return lib; });
        Assert.Equal(0, factories);
        var first = Assert.Single(await reader.ReadDevicesAsync(CancellationToken.None));
        Assert.Equal(DeviceKind.Graphics, first.Kind); Assert.Null(first.SerialIdentity); Assert.Null(first.DefinitionId);
        Assert.Equal(52d, first.Sensors.Single(s => s.Feature == DeviceFeature.Temperature).Value);
        Assert.Equal(900d, first.Sensors.Single(s => s.Feature == DeviceFeature.Rpm).Value);
        Assert.All(first.Sensors, s => Assert.Equal(DeviceMeasurementIssue.None, s.Issue));
        await reader.ReadDevicesAsync(CancellationToken.None);
        Assert.Equal(1, factories); Assert.Equal(1, lib.Initialized);
        Assert.Equal(new uint[] { 0x0150e828, 0xd22bdd7e, 0xe5ac921f, 0xceee8e9f, 0xe3640a56, 0x5f608315 }, lib.Queries);
        await reader.DisposeAsync(); await reader.DisposeAsync(); Assert.Equal(1, lib.Unloaded); Assert.Equal(1, lib.Disposed);
    }
    [Fact] public void ThermalAbiUsesSignedTemperatureAndTheDocumentedV2Layout()
    {
        Assert.Equal(20, Marshal.SizeOf<NvidiaReadOnlyMonitor.ThermalSensor>());
        Assert.Equal(68, Marshal.SizeOf<NvidiaReadOnlyMonitor.ThermalSettings>());
        Assert.Equal(8, Marshal.OffsetOf<NvidiaReadOnlyMonitor.ThermalSettings>(nameof(NvidiaReadOnlyMonitor.ThermalSettings.Sensors)).ToInt32());
    }
    [Theory] [InlineData(0, 0, DeviceMeasurementIssue.OutOfRange, DeviceMeasurementIssue.None)]
    [InlineData(151, 30001, DeviceMeasurementIssue.OutOfRange, DeviceMeasurementIssue.OutOfRange)]
    [InlineData(-12, 900, DeviceMeasurementIssue.OutOfRange, DeviceMeasurementIssue.None)]
    public async Task InvalidTemperatureIsNotFreshAndZeroRpmIsPreserved(int temperature, uint rpm, DeviceMeasurementIssue tempIssue, DeviceMeasurementIssue rpmIssue)
    {
        var lib = new Library { Temperature = temperature, Rpm = rpm }; await using var reader = new NvidiaReadOnlyMonitor(() => lib);
        var readings = Assert.Single(await reader.ReadDevicesAsync(CancellationToken.None)).Sensors;
        Assert.Equal(tempIssue, readings[0].Issue); Assert.Null(readings[0].Value);
        Assert.Equal(rpmIssue, readings[1].Issue); Assert.Equal(rpmIssue == DeviceMeasurementIssue.None ? (double?)rpm : null, readings[1].Value);
    }
    [Fact] public async Task FailedReadsCannotReuseTheOutParametersAndUnsupportedFunctionsStayUnavailable()
    {
        var lib = new Library { ThermalStatus = -1, TachStatus = -1 }; await using var reader = new NvidiaReadOnlyMonitor(() => lib);
        Assert.All(Assert.Single(await reader.ReadDevicesAsync(CancellationToken.None)).Sensors, s =>
        { Assert.Null(s.Value); Assert.Equal(DeviceMeasurementIssue.Unavailable, s.Issue); });
        var absent = new Library(); absent.Functions.Remove(NvidiaReadOnlyMonitor.ThermalId); absent.Functions.Remove(NvidiaReadOnlyMonitor.TachId);
        await using var missing = new NvidiaReadOnlyMonitor(() => absent);
        Assert.All(Assert.Single(await missing.ReadDevicesAsync(CancellationToken.None)).Sensors, s => Assert.Null(s.Value));
    }
    [Theory] [InlineData(4u, 1)] [InlineData(1u, 8)] [InlineData(0u, 1)]
    public async Task MalformedCountsAndNonGpuThermalTargetsDoNotBecomeGpuTemperature(uint count, int target)
    {
        var lib = new Library { ThermalCount = count, Target = target }; await using var reader = new NvidiaReadOnlyMonitor(() => lib);
        var values = Assert.Single(await reader.ReadDevicesAsync(CancellationToken.None)).Sensors;
        Assert.Null(values[0].Value); Assert.Equal(900d, values[1].Value);
    }
    [Theory] [InlineData("count")] [InlineData("duplicate")] [InlineData("null")] [InlineData("error")]
    public async Task InvalidEnumerationNeverReturnsAUsablePartialList(string mode)
    {
        var lib = new Library(); if (mode == "count") lib.Count = 65;
        if (mode == "duplicate") lib.Handles = [17, 17]; if (mode == "null") lib.Handles = [0]; if (mode == "error") lib.EnumerateStatus = -1;
        await using var reader = new NvidiaReadOnlyMonitor(() => lib);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadDevicesAsync(CancellationToken.None));
    }
    [Fact] public async Task ReorderingRetainsSessionIdentityButRemovalOrModelChangeDoesNotReuseIt()
    {
        var lib = new Library { Handles = [17, 19] }; await using var reader = new NvidiaReadOnlyMonitor(() => lib);
        var first = await reader.ReadDevicesAsync(CancellationToken.None); lib.Handles = [19, 17];
        var second = await reader.ReadDevicesAsync(CancellationToken.None);
        Assert.Equal(first[0].ConnectionId, second[1].ConnectionId); Assert.Equal(first[1].ConnectionId, second[0].ConnectionId);
        lib.EnumerateStatus = -6; Assert.Empty(await reader.ReadDevicesAsync(CancellationToken.None));
        lib.EnumerateStatus = 0; var third = await reader.ReadDevicesAsync(CancellationToken.None);
        Assert.DoesNotContain(third[0].ConnectionId, first.Select(d => d.ConnectionId));
        lib.Name = "Replacement"; var fourth = await reader.ReadDevicesAsync(CancellationToken.None);
        Assert.NotEqual(third[0].ConnectionId, fourth[0].ConnectionId);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitializationOrMissingUnloadFailsBeforeReadsAndRetainsLibraryForCleanup(bool missingUnload)
    {
        var lib = new Library { InitializeStatus = -1 }; if (missingUnload) lib.Functions.Remove(NvidiaReadOnlyMonitor.UnloadId);
        await using var reader = new NvidiaReadOnlyMonitor(() => lib);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadDevicesAsync(CancellationToken.None));
        Assert.Equal(missingUnload ? 0 : 1, lib.Initialized); await reader.DisposeAsync();
        Assert.Equal(0, lib.Unloaded); Assert.Equal(1, lib.Disposed);
    }
    [Fact] public async Task UnloadAndLibraryReleaseFailuresAreRetainedAndRetriedWithoutDoubleUnloading()
    {
        var lib = new Library { UnloadStatus = -1 }; var reader = new NvidiaReadOnlyMonitor(() => lib);
        await reader.ReadDevicesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await reader.DisposeAsync()); Assert.Equal(0, lib.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadDevicesAsync(CancellationToken.None));
        lib.UnloadStatus = 0; lib.FailDispose = 1;
        await Assert.ThrowsAsync<IOException>(async () => await reader.DisposeAsync()); Assert.Equal(2, lib.Unloaded);
        await reader.DisposeAsync(); Assert.Equal(2, lib.Unloaded); Assert.Equal(1, lib.Disposed);
    }
    [Fact] public async Task DisposalCannotUnloadWhileNativeReadingIsInFlight()
    {
        using var wait = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lib = new Library { BeforeThermal = () => { entered.TrySetResult(); wait.Wait(); } }; var reader = new NvidiaReadOnlyMonitor(() => lib);
        var reading = reader.ReadDevicesAsync(CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = reader.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); Assert.Equal(0, lib.Unloaded);
        wait.Set(); await reading; await closing; Assert.Equal(1, lib.Unloaded);
    }
    [Fact] public async Task CancelledReadNeverLoadsTheDriver()
    {
        await using var reader = new NvidiaReadOnlyMonitor(() => throw new InvalidOperationException("must not load"));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadDevicesAsync(stop.Token));
    }
}
