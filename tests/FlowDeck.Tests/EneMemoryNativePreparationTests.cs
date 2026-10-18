using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rig = FlowDeck.Tests.EneMemoryFramePreparationTests.Rig;

namespace FlowDeck.Tests;

public sealed class EneMemoryNativePreparationTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<DeviceServiceReply> Fixed(Rig r, int red = 11) =>
        r.Command(DeviceOperation.SetLighting, r.Memory.DeviceId, new("fixed", [new(red, 22, 33)]));
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SavedNativeOrRecoveryCanWaitWithoutBlockingSharedFanCommands(bool recovery)
    {
        await using var r = await new Rig().Open(); if (recovery) await Fixed(r);
        var saved = Signal(); var resume = Signal();
        r.Hooks.BeforeHardware = async (kind, phase, ct) =>
        {
            if (kind != DevicePreparationKind.Command || phase != 2) return;
            r.Hooks.BeforeHardware = null; saved.SetResult(); await resume.Task.WaitAsync(ct);
        };
        var work = recovery ? r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId) : Fixed(r);
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, r.Rack.Active); Assert.NotNull(r.Pending);
            Assert.Equal(recovery ? EneMemoryRecoveryPhase.Recovering : EneMemoryRecoveryPhase.Active, r.Pending!.Phase);
            var writes = r.Rack.Writes.Count;
            Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.SetDuty, r.Fan.DeviceId, duty: 75)).Result!.Outcome);
            Assert.Equal(75, r.Fan.Duty); Assert.Equal(writes, r.Rack.Writes.Count);
            if (!recovery) Assert.All(r.Memory.Observe()!.Endpoints.Single().Capabilities, c => Assert.Equal(DeviceAvailability.Ready, c.Availability));
            resume.SetResult(); Assert.Equal(Outcome.Confirmed, (await work).Result!.Outcome);
            if (recovery) Assert.Null(r.Pending);
        }
        finally { resume.TrySetResult(); await work; }
    }
    [Fact]
    public async Task OwnerLossAfterFirstNativeSaveCancelsWithoutWritingToTheUnownedDimm()
    {
        await using var r = await new Rig().Open(); var saved = Signal(); var resume = Signal();
        r.Hooks.BeforeHardware = async (kind, phase, ct) =>
        {
            if (kind != DevicePreparationKind.Command || phase != 2) return;
            r.Hooks.BeforeHardware = null; saved.SetResult(); await resume.Task.WaitAsync(ct);
        };
        var work = Fixed(r);
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.NotNull(r.Pending);
            Assert.True((await r.Command(DeviceOperation.ReleaseSession)).Success); await work;
            Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending); Assert.Equal(0, r.Rack.Active);
        }
        finally { resume.TrySetResult(); await work; }
    }
    [Fact]
    public async Task ANewerNativeRequestSupersedesTheSavedButUnsentColor()
    {
        await using var r = await new Rig().Open(); var saved = Signal(); var resume = Signal();
        r.Hooks.BeforeHardware = async (kind, phase, ct) =>
        {
            if (kind != DevicePreparationKind.Command || phase != 2) return;
            r.Hooks.BeforeHardware = null; saved.SetResult(); await resume.Task.WaitAsync(ct);
        };
        var first = Fixed(r, 11);
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3)); var second = Fixed(r, 99);
            // ExecuteAsync accepts the second policy synchronously, even though the driver's logical I/O lock is busy.
            resume.SetResult(); await first; Assert.Equal(Outcome.Confirmed, (await second).Result!.Outcome);
            Assert.DoesNotContain(r.Rack.Writes, w => w.Register == 0x8160 && w.Value == 11);
            Assert.Equal(99, r.Rack.Registers[(0x71, 0x8160)]); Assert.Equal(99, r.Pending!.Mutation.After.NativeColors[0]);
        }
        finally { resume.TrySetResult(); await first; }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AForeignChangeBetweenTheReadAndWritePhaseIsNeverOverwritten(bool recovery)
    {
        await using var r = await new Rig().Open(); if (recovery) await Fixed(r);
        var boundary = r.Rack.Writes.Count;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2)
            { r.Hooks.BeforeHardware = null; r.Rack.Registers[(0x71, 0x8160)] = 201; }
            return Task.CompletedTask;
        };
        var result = recovery ? await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId) : await Fixed(r);
        Assert.NotEqual(Outcome.Confirmed, result.Result!.Outcome); Assert.Equal(boundary, r.Rack.Writes.Count);
        Assert.Equal(201, r.Rack.Registers[(0x71, 0x8160)]);
        if (recovery) Assert.Equal(EneMemoryRecoveryPhase.Recovering, r.Pending!.Phase); else Assert.Null(r.Pending);
    }
    [Fact]
    public async Task PolicyChangeDuringNativeTransmissionStopsFurtherBytesAndRequiresRecovery()
    {
        await using var r = await new Rig().Open(); Task<DeviceServiceReply>? newer = null;
        r.Rack.BeforeData = (register, write) =>
        {
            if (!write || register != 0x8163) return;
            r.Rack.BeforeData = null; newer = Fixed(r, 99);
        };
        Assert.Equal(Outcome.Unknown, (await Fixed(r)).Result!.Outcome); Assert.NotNull(newer);
        Assert.NotEqual(Outcome.Confirmed, (await newer!).Result!.Outcome);
        Assert.NotEmpty(r.Rack.Writes); Assert.NotNull(r.Pending);
        Assert.DoesNotContain(r.Rack.Writes, w => w.Register == 0x8163 || w.Register == 0x8160 && w.Value == 99);
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId)).Result!.Outcome); Assert.Null(r.Pending);
    }
    [Fact]
    public async Task ExpiredIdentificationAfterSavingRefreshesBeforeWritingThePreparedMutation()
    {
        await using var r = await new Rig().Open(); await Fixed(r); var original = r.Pending!; var writes = r.Rack.Writes.Count; var opens = 0;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2)
            { r.Hooks.BeforeHardware = null; opens = r.Rack.Opens; r.Clock.Advance(2.1); }
            return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Confirmed, (await Fixed(r, 99)).Result!.Outcome);
        Assert.Equal(opens + 2, r.Rack.Opens); Assert.True(r.Rack.Writes.Count > writes);
        Assert.Equal(original.Scope, r.Pending!.Scope); Assert.True(EneMemoryProtocol.Same(original.Original, r.Pending.Original));
        Assert.Equal(99, r.Rack.Registers[(0x71, 0x8160)]);
    }
    [Fact]
    public async Task TheNativeJournalRemainsProtectedDuringReadbackAndFileWorkHappensWithClosedSessions()
    {
        await using var r = await new Rig().Open(); var attempted = false; var hardwareStarted = false;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            Assert.Equal(0, r.Rack.Active);
            if (kind == DevicePreparationKind.Command && phase == 2)
            {
                Assert.NotNull(r.Pending); hardwareStarted = true;
                r.Rack.BeforeData = (_, write) =>
                {
                    if (write || !r.Rack.Writes.Any()) return;
                    r.Rack.BeforeData = null;
                    var path = Assert.Single(Directory.GetFiles(r.Directory, "*.json"));
                    var error = Record.Exception(() => File.Delete(path));
                    Assert.True(error is IOException or UnauthorizedAccessException); attempted = true;
                };
            }
            return Task.CompletedTask;
        };
        r.Hooks.AfterHardware = (kind, phase, _) =>
        {
            Assert.Equal(0, r.Rack.Active);
            if (kind == DevicePreparationKind.Command && phase == 1) Assert.Null(r.Pending);
            return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Confirmed, (await Fixed(r)).Result!.Outcome); Assert.True(attempted); Assert.True(hardwareStarted);
        r.Hooks.AfterHardware = r.Hooks.BeforeHardware = null;
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId)).Result!.Outcome); Assert.Null(r.Pending);
    }
    [Fact]
    public async Task RecoveryCanRetryAfterDurableConfirmationFailsWithoutLosingItsObligation()
    {
        await using var r = await new Rig().Open(); await Fixed(r); FileStream? locked = null;
        r.Hooks.AfterHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2)
            {
                r.Hooks.AfterHardware = null; Assert.Equal(0, r.Rack.Active);
                locked = new(Assert.Single(Directory.GetFiles(r.Directory, "*.json")), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return Task.CompletedTask;
        };
        try
        {
            Assert.Equal(Outcome.Unknown, (await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId)).Result!.Outcome);
            Assert.Equal(EneMemoryRecoveryPhase.Recovering, r.Pending!.Phase); Assert.Equal(0, r.Rack.Registers[(0x71, 0x8021)]);
        }
        finally { locked?.Dispose(); }
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId)).Result!.Outcome); Assert.Null(r.Pending);
    }
}
