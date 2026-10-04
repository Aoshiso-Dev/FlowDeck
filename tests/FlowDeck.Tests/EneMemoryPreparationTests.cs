using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneMemoryPreparationTests
{
    private const string Installation = "85c1704c8b554b3bbd55bfcfa3fc191a";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task RuntimeFanCommandProceedsWhileMemoryOsMetadataIsBlocked()
    {
        using var resume = new ManualResetEventSlim(); var entered = Signal(); var rack = new Rack(0x71); var metadataThread = 0;
        var transport = rack.Transport() with { ReadPlatformFingerprint = () =>
        {
            metadataThread = Environment.CurrentManagedThreadId; entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new IOException("test metadata timeout");
            return SettingsTransfer.Hash(rack.Platform);
        } };
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Ene-Prepare-" + Guid.NewGuid().ToString("N"));
        var backend = new EneMemoryDeviceBackend([transport]); var provider = new EneMemoryDeviceProvider(Installation, directory, "boot", backend);
        var fan = new GenericTestDriver("fan", "smbus"); await using var runtime = new DeviceControlRuntime([fan], ["user"], new TestLog(), providers: [provider]);
        var hello = runtime.OpenSession("user"); var poll = runtime.PollAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var state = runtime.State(); var target = state.Endpoints.Single(e => e.Target.DeviceId == fan.DeviceId);
            var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "test", DeviceOperation.SetDuty,
                state.Inventory.TopologyRevision, [new(target.Target, target.Generation, target.PolicyRevision)], Duty: 75)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(Outcome.Confirmed, reply.Result!.Outcome); Assert.Equal(75, fan.Duty); Assert.Equal(1, fan.Writes); Assert.Equal(0, rack.Opens);
        }
        finally { resume.Set(); await poll.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.NotEqual(metadataThread, rack.Thread); Assert.Equal(1, rack.Opens); Assert.Empty(rack.Writes); Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public async Task QueuedProviderUsesItsPreparedSnapshotWithoutReadingMetadataUnderTheBusReservation()
    {
        var rack = new Rack(0x71); var reads = 0; var prepared = Signal();
        var transport = rack.Transport() with { ReadPlatformFingerprint = () => { Interlocked.Increment(ref reads); prepared.TrySetResult(); return SettingsTransfer.Hash(rack.Platform); } };
        var directory = Path.Combine(Path.GetTempPath(), "FlowDeck-Ene-Prepare-" + Guid.NewGuid().ToString("N"));
        await using var provider = new EneMemoryDeviceProvider(Installation, directory, "boot", new EneMemoryDeviceBackend([transport]));
        var scheduler = new DeviceResourceScheduler(); var busy = Signal(); var release = Signal();
        var holding = scheduler.RunAsync(["smbus"], HardwarePriority.Manual, async _ => { busy.SetResult(); await release.Task; return true; });
        await busy.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var polling = scheduler.RunPreparedAsync(provider.ResourceDomains, HardwarePriority.Monitoring, provider, DevicePreparationKind.Polling,
            async ct => { await provider.PollAsync(ct); return true; });
        try
        {
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, rack.Opens); release.SetResult();
            await Task.WhenAll(holding, polling).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, reads); Assert.Single(provider.CachedDrivers);
        }
        finally { release.TrySetResult(); await holding; }
    }
    [Fact]
    public async Task MetadataWaitingDoesNotBlockTheBackendTransportThread()
    {
        var rack = new Rack(0x71); var clock = new TestClock(); var blocked = false; var entered = Signal(); using var resume = new ManualResetEventSlim();
        var transport = rack.Transport() with { ReadPlatformFingerprint = () =>
        {
            if (blocked) { entered.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new IOException("metadata timeout"); }
            return SettingsTransfer.Hash(rack.Platform);
        } };
        await using var backend = new EneMemoryDeviceBackend([transport], clock); var c = Assert.Single(await backend.DiscoverAsync(default)); blocked = true;
        var preparation = backend.PrepareOperationAsync(_ => Task.FromResult(true), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await backend.ExecuteAsync(c, false, s => s.Read(0x8020), default).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { resume.Set(); await preparation; }
        Assert.Empty(rack.Writes);
    }
    [Fact]
    public async Task ExpiredPreparedMetadataCannotOpenTheBusOrRenewConnectionEvidence()
    {
        var rack = new Rack(0x71); var clock = new TestClock(); await using var backend = new EneMemoryDeviceBackend([rack.Transport()], clock);
        var c = Assert.Single(await backend.DiscoverAsync(default)); var entered = Signal(); var resume = Signal(); var opens = rack.Opens;
        var command = backend.PrepareOperationAsync(async ct =>
        {
            entered.SetResult(); await resume.Task; return await backend.ExecuteAsync(c, true, s => { s.Write(0x8160, 44, () => { }); return true; }, ct);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); clock.Advance(2.1); resume.SetResult();
        await Assert.ThrowsAnyAsync<IOException>(() => command); Assert.Equal(opens, rack.Opens); Assert.Empty(rack.Writes); backend.Check(c);
        await Assert.ThrowsAsync<IOException>(() => backend.ExecuteAsync(c, false, s => s.Read(0x8020), default)); Assert.Equal(opens, rack.Opens);
    }
    [Fact]
    public async Task AnOlderPreparationCannotOverrideNewerProofOrInvalidateTheNewConnection()
    {
        var rack = new Rack(0x71); await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var c = Assert.Single(await backend.DiscoverAsync(default));
        var entered = Signal(); var resume = Signal();
        var old = backend.PrepareOperationAsync(async ct =>
        {
            entered.SetResult(); await resume.Task; return await backend.DiscoverAsync(ct);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); rack.Platform = "new-platform";
        var current = Assert.Single(await backend.DiscoverAsync(default)); Assert.NotEqual(c, current); var opens = rack.Opens;
        resume.SetResult(); Assert.Empty(await old); Assert.Equal(opens, rack.Opens); backend.Check(current);
    }
    [Fact]
    public async Task IdenticalFreshPreparationsCanExecuteInReverseOrder()
    {
        var rack = new Rack(0x71, 0x73); await using var backend = new EneMemoryDeviceBackend([rack.Transport()]); var connections = await backend.DiscoverAsync(default);
        var entered = Signal(); var resume = Signal();
        var first = backend.PrepareOperationAsync(async ct =>
        {
            entered.SetResult(); await resume.Task; return await backend.ExecuteAsync(connections[0], true, s => s.Read(0x8020), ct);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await backend.ExecuteAsync(connections[1], true, s => s.Read(0x8020), default)); resume.SetResult();
        Assert.Equal(0, await first); Assert.All(connections, backend.Check); Assert.Empty(rack.Writes);
    }
    [Fact]
    public async Task InvalidationDuringMetadataCaptureCannotPublishStaleConnections()
    {
        var rack = new Rack(0x71); var entered = Signal(); using var resume = new ManualResetEventSlim();
        var transport = rack.Transport() with { ReadPlatformFingerprint = () =>
        {
            entered.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new IOException("metadata timeout");
            return SettingsTransfer.Hash(rack.Platform);
        } };
        await using var backend = new EneMemoryDeviceBackend([transport]); var discovery = backend.DiscoverAsync(default);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); backend.Invalidate(); }
        finally { resume.Set(); }
        Assert.Empty(await discovery); Assert.Equal(0, rack.Opens); Assert.Single(await backend.DiscoverAsync(default));
    }
}
