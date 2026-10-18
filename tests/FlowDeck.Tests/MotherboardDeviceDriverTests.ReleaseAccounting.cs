using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class MotherboardDeviceDriverTests
{
    [Fact]
    public async Task CancelledBeforeEnteringIoStillAcknowledgesReleaseWithoutStartingAnAcquisition()
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        var actuation = (CoolingActuation)Activator.CreateInstance(typeof(CoolingActuation),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [port.Id, 93, false, rig.Driver.CoolingConfiguration(port.Id)!.Safety, (Func<CoolingAuthorization>)(() => new())], null)!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Driver.ApplyCoolingAsync(actuation, cancellation.Token));
        Assert.Empty(rig.Backend.Commands); Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
        Assert.Equal(Outcome.Confirmed, Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Single(rig.Backend.Commands, c => c.Operation == MotherboardFanOperation.Release);
        Assert.Empty(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)); Assert.Equal(0, rig.Backend.Writes);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("authorization")]
    [InlineData("deadline")]
    public async Task RejectedAcquisitionStillReturnsOneVerifiedReleaseWithoutRegisterWrites(string phase)
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        rig.Backend.BeforeExecute = command =>
        {
            if (phase == "read" && command.Operation == MotherboardFanOperation.Read ||
                phase == "write" && command.Operation == MotherboardFanOperation.Write)
                throw new MotherboardCommandRejectedException("control changed before " + phase);
        };
        Func<CoolingAuthorization>? authorize = phase switch
        {
            "authorization" => () => new("owner changed"),
            "deadline" => () => new(ValidFor: TimeSpan.Zero),
            _ => null
        };
        Assert.Equal(Outcome.Rejected, (await rig.Apply(port, 93, authorize: authorize)).Outcome);
        Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
        Assert.Throws<IOException>(() => rig.Driver.Configure(rig.Driver.BindingCandidate, [rig.Config(port)]));
        rig.Backend.BeforeExecute = null;
        var released = Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(port.Id, released.EndpointId); Assert.Equal(Outcome.Confirmed, released.Result.Outcome);
        Assert.Single(rig.Backend.Commands, c => c.Operation == MotherboardFanOperation.Release);
        Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
        Assert.Empty(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        rig.Driver.Configure(rig.Driver.BindingCandidate, [rig.Config(port)]);
    }

    [Fact]
    public async Task ReleaseTrackingCannotGrantWriteAuthorityToAnUnconfiguredEndpoint()
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        var safety = rig.Driver.CoolingConfiguration(port.Id)!.Safety;
        rig.Driver.Configure(rig.Driver.BindingCandidate, []);
        var actuation = (CoolingActuation)Activator.CreateInstance(typeof(CoolingActuation),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [port.Id, 93, false, safety, (Func<CoolingAuthorization>)(() => new())], null)!;
        Assert.Equal(Outcome.Rejected, (await rig.Driver.ApplyCoolingAsync(actuation, default)).Outcome);
        Assert.Empty(rig.Backend.Commands); Assert.Equal(0, rig.Backend.Writes);
        Assert.NotNull(rig.Driver.ValidateCoolingSetup(port.Id));
        Assert.Equal(Outcome.Confirmed, Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
    }

    [Fact]
    public async Task FailedNoWriteReleaseRemainsTrackedUntilTheBackendConfirmsIt()
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        Assert.Equal(Outcome.Rejected, (await rig.Apply(port, 93, authorize: () => new("owner changed"))).Outcome);
        rig.Backend.BeforeExecute = command =>
        {
            if (command.Operation == MotherboardFanOperation.Release) throw new IOException("identity cannot be verified");
        };
        var failed = Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(Outcome.Unknown, failed.Result.Outcome); Assert.Contains("identity cannot be verified", failed.Result.Message);
        Assert.Throws<IOException>(() => rig.Driver.Configure(rig.Driver.BindingCandidate, [rig.Config(port)]));
        rig.Backend.BeforeExecute = null;
        Assert.Equal(Outcome.Confirmed, Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Empty(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
        Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
    }

    [Fact]
    public async Task RealPendingRecoveryStillRejectsAnExternalOwnerAndCanBeRetried()
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        Assert.Equal(Outcome.Confirmed, (await rig.Apply(port, 93)).Outcome);
        var pending = Assert.Single(rig.Journal().Pending); var map = NctFanMap.For(port.Chip, port.Channel);
        var bank = rig.Backend.Banks[port.Chip]; bank.Values[map.Command] = 99; var writes = rig.Backend.Writes;
        Assert.Equal(Outcome.Unknown, Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Equal(writes, rig.Backend.Writes); Assert.Equal(99, bank.Values[map.Command]); Assert.Single(rig.Journal().Pending);
        Assert.Throws<IOException>(() => rig.Driver.Configure(rig.Driver.BindingCandidate, [rig.Config(port)]));
        bank.Values[map.Command] = pending.IntendedCommand;
        Assert.Equal(Outcome.Confirmed, Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Equal(pending.OriginalMode, bank.Values[map.Mode]); Assert.Equal(pending.OriginalCommand, bank.Values[map.Command]);
        Assert.Empty(rig.Journal().Pending); Assert.Empty(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task RuntimeNormalExitAfterARejectedAcquisitionClearsOwnershipWithARealReleaseResult(string phase)
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        rig.Backend.BeforeExecute = command =>
        {
            if (command.Operation == (phase == "read" ? MotherboardFanOperation.Read : MotherboardFanOperation.Write))
                throw new MotherboardCommandRejectedException("control changed before " + phase);
        };
        var hello = rig.Runtime.OpenSession("user"); var state = rig.Runtime.State();
        var reference = new EndpointReference(rig.Driver.DeviceId, port.Id);
        var target = state.Endpoints.Single(e => e.Target == reference);
        var applied = await rig.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1,
            "release-accounting", DeviceOperation.SetDuty, state.Inventory.TopologyRevision,
            [new(reference, target.Generation, target.PolicyRevision)], 93));
        Assert.Equal(Outcome.Rejected, applied.Result!.Outcome);
        Assert.True(rig.Runtime.State().Endpoints.Single(e => e.Target == reference).Owned);
        Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
        rig.Backend.BeforeExecute = null;
        var exited = await rig.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 2,
            "release-accounting", DeviceOperation.ReleaseSession));
        Assert.True(exited.Success, exited.Error);
        var after = rig.Runtime.State(); Assert.Null(after.LifecycleError); Assert.False(after.Releasing);
        Assert.False(after.Endpoints.Single(e => e.Target == reference).Owned);
        Assert.Equal(Outcome.Confirmed, after.Endpoints.Single(e => e.Target == reference).Outcome);
        Assert.Single(rig.Backend.Commands, c => c.Operation == MotherboardFanOperation.Release);
        Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
    }

    [Fact]
    public async Task CalibrationPrewriteRejectionRetainsItsExistingVerifiedReleasePath()
    {
        await using var rig = await Rig.Create(); var port = MotherboardFans.Find("nct6799d-2");
        rig.Backend.BeforeExecute = command =>
        {
            if (command.Operation == MotherboardFanOperation.Write) throw new MotherboardCommandRejectedException("control changed");
        };
        var trial = await rig.Driver.BeginCalibrationAsync(CalibrationTestActuation.Create(port.Id, rig.Driver.BindingCandidate!, 100, 67), default);
        Assert.Equal(Outcome.Unknown, trial.Result.Outcome); Assert.Equal(0, rig.Backend.Writes); Assert.Empty(rig.Journal().Pending);
        rig.Backend.BeforeExecute = null;
        Assert.Equal(Outcome.Confirmed, Assert.Single(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)).Result.Outcome);
        Assert.Equal(Outcome.Confirmed, (await rig.Driver.EndCalibrationAsync(port.Id, default)).Outcome);
        Assert.Single(rig.Backend.Commands, c => c.Operation == MotherboardFanOperation.Release);
        Assert.Empty(await rig.Driver.ReleaseAsync(DeviceReleaseReason.NormalExit, default)); Assert.Equal(0, rig.Backend.Writes);
    }
}
