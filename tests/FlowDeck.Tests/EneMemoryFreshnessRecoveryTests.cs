using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Rig = FlowDeck.Tests.EneMemoryFramePreparationTests.Rig;
using Rack = FlowDeck.Tests.EneMemoryBackendTests.Rack;

namespace FlowDeck.Tests;

public sealed class EneMemoryFreshnessRecoveryTests
{
    private static Task<DeviceServiceReply> Fixed(Rig r, int red = 11) =>
        r.Command(DeviceOperation.SetLighting, r.Memory.DeviceId, new("fixed", [new(red, 22, 33)]));

    [Fact]
    public async Task AnExpiredInitialReservationGetsNewMetadataBeforeAnyBusAccess()
    {
        await using var r = await new Rig().Open(); var before = r.Rack.Identifications;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 1) r.Clock.Advance(2.1);
            Assert.Equal(0, r.Rack.Active); return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Confirmed, (await Fixed(r)).Result!.Outcome);
        Assert.Equal(before + 1, r.Rack.Identifications);
        Assert.Equal(1, r.Rack.MaximumActive);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ASecondExpiryStopsWithoutAnyWriteAndWithoutAnotherRetry(bool firstReadExpired)
    {
        await using var r = await new Rig().Open(); var before = r.Rack.Identifications;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command &&
                (firstReadExpired ? phase is 1 or 3 : phase is 2 or 4)) r.Clock.Advance(2.1);
            return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Rejected, (await Fixed(r)).Result!.Outcome);
        Assert.Equal(before + (firstReadExpired ? 1 : 2), r.Rack.Identifications);
        Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
    }

    [Theory] [InlineData("platform")] [InlineData("controller")] [InlineData("state")]
    public async Task ReidentificationCannotWriteToChangedEvidenceOrOverwriteForeignState(string changed)
    {
        await using var r = await new Rig().Open();
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2)
            {
                r.Clock.Advance(2.1);
                if (changed == "platform") r.Rack.Platform = "replaced platform";
                if (changed == "controller") r.Rack.Identity = null;
                if (changed == "state") r.Rack.Registers[(0x71, 0x8160)] = 201;
            }
            return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Rejected, (await Fixed(r)).Result!.Outcome);
        Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
        if (changed == "state") Assert.Equal(201, r.Rack.Registers[(0x71, 0x8160)]);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExpiryAtTheDataBoundaryRetriesOnlyBeforeTheFirstWrite(bool afterWrite)
    {
        await using var r = await new Rig().Open(); var before = r.Rack.Identifications;
        r.Rack.BeforeData = (register, write) =>
        {
            if (write && register == (afterWrite ? 0x8161 : 0x8160))
            { r.Rack.BeforeData = null; r.Clock.Advance(2.1); }
        };
        Assert.Equal(afterWrite ? Outcome.Unknown : Outcome.Confirmed, (await Fixed(r)).Result!.Outcome);
        Assert.Equal(before + (afterWrite ? 1 : 2), r.Rack.Identifications);
        if (afterWrite)
        {
            Assert.Single(r.Rack.Writes); Assert.NotNull(r.Pending);
            Assert.Equal(DeviceAvailability.Failed, r.Memory.Observe()!.Endpoints.Single().Capabilities
                .Single(c => c.Feature == DeviceFeature.FixedColor).Availability);
        }
        else Assert.Equal(11, r.Rack.Registers[(0x71, 0x8160)]);
    }

    [Fact]
    public async Task GenericTransportFailureDoesNotRefreshOrRetry()
    {
        await using var r = await new Rig().Open(); var before = r.Rack.Identifications;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2) r.Rack.FailRead = true;
            return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Rejected, (await Fixed(r)).Result!.Outcome);
        Assert.Equal(before + 1, r.Rack.Identifications); Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
        r.Rack.FailRead = false;
    }

    [Fact]
    public async Task RecoveryRefreshesTheSamePreparedRecordBeforeItsFirstWrite()
    {
        await using var r = await new Rig().Open(); await Fixed(r); var scope = r.Pending!.Scope;
        string? preparedHash = null;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2)
            { preparedHash = r.Pending!.Hash; r.Clock.Advance(2.1); }
            if (kind == DevicePreparationKind.Command && phase is 3 or 4)
            { Assert.Equal(preparedHash, r.Pending!.Hash); Assert.Equal(scope, r.Pending.Scope); }
            Assert.Equal(0, r.Rack.Active); return Task.CompletedTask;
        };
        Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId)).Result!.Outcome);
        Assert.Null(r.Pending);
    }

    [Fact]
    public async Task RecoveryThatExpiresAfterOneWriteCannotReidentifyOrReplayTheMutation()
    {
        await using var r = await new Rig().Open(); await Fixed(r);
        var original = r.Pending!; var writes = r.Rack.Writes.Count; var identities = r.Rack.Identifications;
        string? preparedHash = null;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2) preparedHash = r.Pending!.Hash;
            return Task.CompletedTask;
        };
        r.Rack.BeforeData = (register, write) =>
        {
            if (write && register == 0x8161)
            { r.Rack.BeforeData = null; r.Clock.Advance(2.1); }
        };
        Assert.Equal(Outcome.Unknown, (await r.Command(DeviceOperation.StopLighting, r.Memory.DeviceId)).Result!.Outcome);
        Assert.Equal(identities + 1, r.Rack.Identifications); Assert.Equal(writes + 1, r.Rack.Writes.Count);
        var pending = r.Pending!;
        Assert.Equal(EneMemoryRecoveryPhase.Recovering, pending.Phase); Assert.Equal(preparedHash, pending.Hash);
        Assert.Equal(original.Scope, pending.Scope);
        Assert.True(EneMemoryProtocol.Same(original.Original, pending.Original));
        Assert.True(EneMemoryProtocol.Same(original.Exit, pending.Exit));
    }

    [Fact]
    public async Task FreshPreparationReplacesAnInheritedSnapshotWithoutChangingConnectionIdentity()
    {
        var rack = new Rack(0x71); var clock = new TestClock(); var captures = 0;
        var transport = rack.Transport() with { ReadPlatformFingerprint = () =>
        { Assert.Equal(0, rack.Active); captures++; return SettingsTransfer.Hash(rack.Platform); } };
        await using var backend = new EneMemoryDeviceBackend([transport], clock);
        var connection = Assert.Single(await backend.DiscoverAsync(default));
        await backend.PrepareOperationAsync(async ct =>
        {
            clock.Advance(2.1);
            await Assert.ThrowsAsync<EneMemoryFreshnessException>(() => backend.ExecuteAsync(connection, true, _ => true, ct));
            await backend.PrepareFreshOperationAsync(t => backend.ExecuteAsync(connection, true, _ => true, t), ct);
            backend.Check(connection);
            // The fresh nested scope must not pretend the older outer proof was renewed.
            await Assert.ThrowsAsync<EneMemoryFreshnessException>(() => backend.ExecuteAsync(connection, true, _ => true, ct));
            return true;
        }, default);
        Assert.Equal(3, captures); Assert.Empty(rack.Writes); Assert.Equal(rack.Opens, rack.Closes);
    }

    [Fact]
    public async Task ExhaustedReadFreshnessIsTemporaryAndTheNextHealthyPollPreservesSetup()
    {
        await using var r = await new Rig().Open(); var binding = r.Memory.BindingCandidate;
        var before = r.Rack.Identifications; r.Clock.Advance(2.1);
        r.Rack.BeforeData = (_, write) =>
        {
            Assert.False(write); r.Rack.BeforeData = null; r.Clock.Advance(2.1);
        };
        await Assert.ThrowsAsync<EneMemoryFreshnessException>(() => r.Memory.PollAsync(default));
        Assert.Equal(before + 1, r.Rack.Identifications);
        var capability = r.Memory.Observe()!.Endpoints.Single().Capabilities.Single(c => c.Feature == DeviceFeature.FixedColor);
        Assert.Equal(DeviceAvailability.TemporarilyUnavailable, capability.Availability);
        Assert.Equal(binding, r.Memory.BindingCandidate); Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
        await r.Memory.PollAsync(default);
        Assert.Equal(DeviceAvailability.Ready, r.Memory.Observe()!.Endpoints.Single().Capabilities
            .Single(c => c.Feature == DeviceFeature.FixedColor).Availability);
        Assert.Equal(binding, r.Memory.BindingCandidate); Assert.Empty(r.Rack.Writes); Assert.Null(r.Pending);
    }

    [Fact]
    public async Task ANewerPolicyDuringFreshnessRecoveryCannotSendTheSupersededColor()
    {
        await using var r = await new Rig().Open(); Task<DeviceServiceReply>? newer = null;
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2) r.Clock.Advance(2.1);
            if (kind == DevicePreparationKind.Command && phase == 3)
            { r.Hooks.BeforeHardware = null; newer = Fixed(r, 99); }
            return Task.CompletedTask;
        };
        Assert.NotEqual(Outcome.Confirmed, (await Fixed(r)).Result!.Outcome);
        Assert.NotNull(newer); Assert.Equal(Outcome.Confirmed, (await newer!).Result!.Outcome);
        Assert.DoesNotContain(r.Rack.Writes, w => w.Register == 0x8160 && w.Value == 11);
        Assert.Equal(99, r.Rack.Registers[(0x71, 0x8160)]);
    }

    [Fact]
    public async Task FreshMetadataWaitDoesNotReserveTheSharedBusOrBlockFanCommands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim(); var block = false;
        await using var r = await new Rig(transport => transport with
        {
            ReadPlatformFingerprint = () =>
            {
                if (block)
                {
                    entered.TrySetResult();
                    if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new IOException("test metadata timeout");
                }
                return transport.ReadPlatformFingerprint();
            }
        }).Open();
        r.Hooks.BeforeHardware = (kind, phase, _) =>
        {
            if (kind == DevicePreparationKind.Command && phase == 2)
            { r.Hooks.BeforeHardware = null; r.Clock.Advance(2.1); block = true; }
            return Task.CompletedTask;
        };
        var command = Fixed(r);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, r.Rack.Active); Assert.NotNull(r.Pending); Assert.Empty(r.Rack.Writes);
            Assert.Equal(Outcome.Confirmed, (await r.Command(DeviceOperation.SetDuty, r.Fan.DeviceId, duty: 75)).Result!.Outcome);
            Assert.Equal(75, r.Fan.Duty); Assert.Empty(r.Rack.Writes);
        }
        finally { resume.Set(); }
        Assert.Equal(Outcome.Confirmed, (await command).Result!.Outcome);
    }
}
