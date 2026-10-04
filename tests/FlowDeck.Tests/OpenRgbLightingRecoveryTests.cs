using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbLightingRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FlowDeck-OpenRgb-" + Guid.NewGuid().ToString("N"));
    private const string Id = "openrgb:test", Boot = "test-boot";
    public void Dispose()
    {
        // This test creates only flat journal files in its new unique directory.
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
    private static OpenRgbRecoveryScope Scope(OpenRgbSdkController device, int? zone = null, IdentityConfidence confidence = IdentityConfidence.ConnectionBound)
    {
        var structure = OpenRgbSdkProtocol.Structure(device);
        return new(new(Id, "serial:" + new string('a', 64), "openrgb-sdk6", structure), new(Guid.NewGuid(), 1, device.Id, structure), confidence, Guid.NewGuid().ToString("N"), zone);
    }
    private (OpenRgbLightingRecovery Journal, OpenRgbRecoveryRecord Record, OpenRgbSdkController Device) Prepare(int? zone = null,
        IdentityConfidence confidence = IdentityConfidence.ConnectionBound)
    {
        var device = OpenRgbLightingPlanTests.Split() with { ActiveMode = 1 }; var scope = Scope(device, zone, confidence);
        var mutation = OpenRgbLightingPlan.Create(device, zone, new("fixed", [new(0, 0, 255)]));
        var journal = new OpenRgbLightingRecovery(directory, Id, zone);
        return (journal, journal.Prepare(Boot, scope, device, mutation, OpenRgbLightingPlan.State(device)), device);
    }
    [Fact]
    public void DurableIntentRetainsOriginalAndDoesNotStoreHardwareSerials()
    {
        var (journal, record, _) = Prepare(); var reload = new OpenRgbLightingRecovery(directory, Id, null);
        Assert.Equal(record.Hash, reload.Pending!.Hash); Assert.Equal(record.Hash, journal.Pending!.Hash);
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));
        Assert.DoesNotContain("SERIAL", json); Assert.DoesNotContain("USB location", json);
        var copy = journal.Pending!; copy.Original.Colors[0] = new(0, 0, 0);
        Assert.NotEqual(copy.Original.Colors[0], journal.Pending!.Original.Colors[0]);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EverySendBoundaryCanRecoverAfterReload(int prefix)
    {
        var (_, record, device) = Prepare(); var current = prefix == 0 ? record.Mutation.Before : record.Mutation.Prefixes[prefix - 1];
        var reload = new OpenRgbLightingRecovery(directory, Id, null);
        var restore = reload.PrepareRecovery(record, Boot, record.Scope.Binding, record.Scope.Target, OpenRgbLightingPlan.WithState(device, current));
        Assert.True(OpenRgbLightingPlan.Same(record.Original, restore.Mutation.After));
        Assert.ThrowsAny<IOException>(() => reload.Complete(restore));
        Assert.ThrowsAny<IOException>(() => reload.AcceptRecovery(restore, restore.Mutation.After, restore.Mutation.Steps.Length - 1));
        var accepted = reload.AcceptRecovery(restore, restore.Mutation.After, restore.Mutation.Steps.Length);
        Assert.Equal(OpenRgbRecoveryPhase.Accepted, new OpenRgbLightingRecovery(directory, Id, null).Pending!.Phase);
        reload.Complete(accepted); Assert.Null(reload.Pending); Assert.Empty(Directory.GetFiles(directory));
    }
    [Fact]
    public void RecoveryItselfCanResumeFromAnIntermediatePrefix()
    {
        var (journal, record, device) = Prepare();
        var restore = journal.PrepareRecovery(record, Boot, record.Scope.Binding, record.Scope.Target, OpenRgbLightingPlan.WithState(device, record.Mutation.After));
        var partial = restore.Mutation.Prefixes[0]; var reloaded = new OpenRgbLightingRecovery(directory, Id, null);
        var retry = reloaded.PrepareRecovery(restore, Boot, record.Scope.Binding, record.Scope.Target, OpenRgbLightingPlan.WithState(device, partial));
        Assert.True(OpenRgbLightingPlan.Same(record.Exit, retry.Mutation.After));
    }
    [Fact]
    public void ExternalChangeRejectsRecoveryAndKeepsObligation()
    {
        var (journal, record, device) = Prepare(); var foreign = Contract.Copy(record.Mutation.After); foreign.Colors[0] = new(42, 42, 42);
        Assert.ThrowsAny<IOException>(() => journal.PrepareRecovery(record, Boot, record.Scope.Binding, record.Scope.Target, OpenRgbLightingPlan.WithState(device, foreign)));
        Assert.Equal(record.Hash, journal.Pending!.Hash);
        Assert.ThrowsAny<IOException>(() => journal.PrepareRecovery(record, "different-boot", record.Scope.Binding, record.Scope.Target, OpenRgbLightingPlan.WithState(device, record.Mutation.After)));
    }
    [Fact]
    public void SerialRebindingRequiresSamePhysicalAndStructuralEvidence()
    {
        var (_, record, _) = Prepare(confidence: IdentityConfidence.SerialBound);
        var target = record.Scope.Target with { Connection = Guid.NewGuid(), ControllerId = 999, InventoryRevision = 10 };
        Assert.Null(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding, target));
        Assert.NotNull(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding with { Fingerprint = new string('b', 64) }, target));
        Assert.NotNull(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding, target with { Structure = new string('c', 64) }));
    }
    [Fact]
    public void ConnectionOnlyIdentityCannotBeRecoveredOnAnotherConnectionOrListGeneration()
    {
        var (_, record, _) = Prepare();
        Assert.NotNull(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding, record.Scope.Target with { Connection = Guid.NewGuid() }));
        Assert.NotNull(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding, record.Scope.Target with { InventoryRevision = 2 }));
        Assert.NotNull(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding, record.Scope.Target with { ControllerId = 700 }));
    }
    [Fact]
    public void FreshSerialMatchCanRecoverAfterAListChangeOnTheSameConnection()
    {
        var (_, record, _) = Prepare(confidence: IdentityConfidence.SerialBound);
        var refreshed = record.Scope.Target with { InventoryRevision = record.Scope.Target.InventoryRevision + 1, ControllerId = 901 };
        Assert.Null(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding, refreshed));
        Assert.NotNull(OpenRgbLightingRecovery.Check(record, Boot, record.Scope.Binding with { ProviderIdentity = "serial:" + new string('b', 64) }, refreshed));
    }
    [Fact]
    public void ZoneRecoveryAllowsSiblingActivityAndPreservesItsCurrentColor()
    {
        var (journal, record, device) = Prepare(0); var live = Contract.Copy(record.Mutation.After); live.Colors[1] = new(42, 42, 42);
        var restore = journal.PrepareRecovery(record, Boot, record.Scope.Binding, record.Scope.Target, OpenRgbLightingPlan.WithState(device, live));
        Assert.Equal(record.Original.Colors[0], restore.Mutation.After.Colors[0]); Assert.Equal(live.Colors[1], restore.Mutation.After.Colors[1]);
        Assert.All(restore.Mutation.Steps, step => Assert.Equal(0, step.Zone));
        var reported = Contract.Copy(restore.Mutation.After); reported.Colors[1] = new(60, 60, 60);
        var accepted = journal.AcceptRecovery(restore, reported, restore.Mutation.Steps.Length); journal.Complete(accepted); Assert.Null(journal.Pending);
    }
    [Fact]
    public void RepeatedEditsKeepTheFirstOriginalAndExit()
    {
        var (journal, record, device) = Prepare(0); var current = OpenRgbLightingPlan.WithState(device, record.Mutation.After);
        var mutation = OpenRgbLightingPlan.Create(current, 0, new("fixed", [new(0, 200, 0)]));
        var second = journal.Prepare(Boot, record.Scope, current, mutation, record.Exit, record);
        Assert.True(OpenRgbLightingPlan.Same(record.Original, second.Original)); Assert.True(OpenRgbLightingPlan.Same(record.Exit, second.Exit));
        journal.CancelUnsent(second, record); Assert.Equal(record.Hash, journal.Pending!.Hash);
    }
    [Fact]
    public void CannotPrepareOverAnotherOwnerOrSendSiblingThroughZoneRecord()
    {
        var (journal, record, device) = Prepare(0);
        Assert.ThrowsAny<IOException>(() => journal.Prepare(Boot, record.Scope, device, record.Mutation, record.Exit));
        var current = OpenRgbLightingPlan.WithState(device, record.Mutation.After); var sibling = OpenRgbLightingPlan.Create(current, 1, new("fixed", [new(0, 200, 0)]));
        Assert.ThrowsAny<IOException>(() => journal.Prepare(Boot, record.Scope, current, sibling, record.Exit, record));
        Assert.Equal(record.Hash, journal.Pending!.Hash);
    }
    [Fact]
    public void ReadLeasePreventsIntentReplacementDuringTransport()
    {
        var (journal, record, _) = Prepare();
        using (var lease = journal.AcquireReadLease(record))
        {
            lease.Check(); Assert.ThrowsAny<IOException>(() => journal.CancelUnsent(record, null));
            Assert.Equal(record.Hash, journal.Pending!.Hash);
        }
        journal.CancelUnsent(record, null); Assert.Null(journal.Pending);
    }
    [Fact]
    public void TamperingAndMissingFileAreDetectedBeforeOutput()
    {
        var (journal, record, _) = Prepare(); var path = Assert.Single(Directory.GetFiles(directory));
        var json = File.ReadAllText(path); File.WriteAllText(path, json.Replace("test-boot", "other-boot"));
        Assert.ThrowsAny<IOException>(() => journal.CheckCurrent(record)); Assert.ThrowsAny<IOException>(() => new OpenRgbLightingRecovery(directory, Id, null));
        File.Delete(path); Assert.ThrowsAny<IOException>(() => journal.AcquireReadLease(record));
    }
    [Fact]
    public void RealVirtualZoneMetadataCannotBeLostWhenValidatingMutation()
    {
        var plain = OpenRgbLightingPlanTests.Device(); var mutation = OpenRgbLightingPlan.Create(plain, 0, new("fixed", [new(0, 0, 255)]));
        var virtualDevice = plain with { Zones = [plain.Zones[0] with { Flags = 1 }] };
        var scope = Scope(virtualDevice); var journal = new OpenRgbLightingRecovery(directory, Id, null);
        Assert.ThrowsAny<IOException>(() => journal.Prepare(Boot, scope, virtualDevice, mutation, OpenRgbLightingPlan.State(virtualDevice)));
        Assert.Null(journal.Pending);
    }
    [Fact]
    public void LocationEvidenceCannotBeRelabeledAsSerialBinding()
    {
        var device = OpenRgbLightingPlanTests.Device(); var scope = Scope(device, confidence: IdentityConfidence.SerialBound);
        scope = scope with { Binding = scope.Binding with { ProviderIdentity = "location:" + new string('a', 64) } };
        var journal = new OpenRgbLightingRecovery(directory, Id, null);
        var mutation = OpenRgbLightingPlan.Create(device, null, new("fixed", [new(0, 0, 255)]));
        Assert.ThrowsAny<IOException>(() => journal.Prepare(Boot, scope, device, mutation, OpenRgbLightingPlan.State(device)));
        Assert.Null(journal.Pending);
    }
    [Fact]
    public void ImpossibleOriginalRestorationPreventsRecordingNewOutput()
    {
        var device = OpenRgbLightingPlanTests.Device(); device = device with { ActiveMode = -1 };
        var scope = Scope(device); var mutation = OpenRgbLightingPlan.Create(device, null, new("fixed", [new(0, 0, 255)]));
        var journal = new OpenRgbLightingRecovery(directory, Id, null);
        Assert.ThrowsAny<IOException>(() => journal.Prepare(Boot, scope, device, mutation, OpenRgbLightingPlan.State(device)));
        Assert.Null(journal.Pending); Assert.False(Directory.Exists(directory));
    }
}
