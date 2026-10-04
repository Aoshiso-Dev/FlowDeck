using FlowDeck.Core;
using FlowDeck.Infrastructure;
using NativeRig = FlowDeck.Tests.AsrockOpenRgbNativePlanTests.Rig;

namespace FlowDeck.Tests;

public sealed class OpenRgbNativeSettlementTests
{
    private static AsrockOpenRgbRecoveryIdentity Identity => AsrockOpenRgbNativePlanTests.Identity;
    private static readonly Action Guard = () => { };
    private sealed class Rig : IDisposable
    {
        public readonly NativeRig Native = new();
        public readonly OpenRgbSdkTarget Target;
        public readonly PhysicalDeviceBinding Binding;
        public readonly string DeviceId = "sdk-native-settlement";
        public Rig()
        {
            Native.Device.Order = AsrockOpenRgbDetectionRecovery.DetectionOrder(Native.Before);
            Target = new(Guid.NewGuid(), 1, Native.Sdk.Id, OpenRgbSdkProtocol.Structure(Native.Sdk));
            Binding = new(DeviceId, "location:" + new string('a', 64), "openrgb-sdk6", Target.Structure);
        }
        public (OpenRgbLightingRecovery Journal, OpenRgbRecoveryRecord Record) Prepare(int? zone = null)
        {
            var sdk = Native.Sdk; var state = OpenRgbLightingPlan.State(sdk);
            var count = zone is { } z ? (int)sdk.Zones[z].LedCount : sdk.Colors.Length;
            var mutation = OpenRgbLightingPlan.Build(sdk, state, [new(zone, Colors: Enumerable.Repeat(new RgbColor(1, 2, 3), count).ToArray())]);
            var journal = new OpenRgbLightingRecovery(Native.Directory.Path, DeviceId, zone);
            var scope = new OpenRgbRecoveryScope(Binding, Target, IdentityConfidence.ConnectionBound, Guid.NewGuid().ToString("N"), zone);
            var record = journal.Prepare(Identity.Boot, scope, sdk, mutation, state);
            Native.Journal.TrackSdk(Identity, OpenRgbSdkRecoveryLink.From(record), Guard);
            return (journal, record);
        }
        public void BeginPartialNative()
        {
            var operation = Native.Plan.Prepare(Native.Journal.Pending!.Expected, Native.Sdk, new(null, 15, Native.Sdk.Modes[15]));
            Native.Journal.PrepareOperation(Identity, operation, AsrockOpenRgbNativeOperationTests.Initial(),
                Native.Plan.Exit(new LightingExitPolicy(LightingExitAction.Off)), Guard);
            foreach (var packet in operation.Packets.Take(2)) Native.Device.Exchange(packet);
        }
        public string SdkPath(int? zone = null) => Path.Combine(Native.Directory.Path,
            "openrgb-lighting-" + SettingsTransfer.Hash(new { deviceId = DeviceId, zone }).ToLowerInvariant() + ".json");
        public string NativePath => System.IO.Directory.GetFiles(Native.Directory.Path, AsrockOpenRgbDetectionRecovery.FilePrefix + "*.json").Single();
        public AsrockOpenRgbDetectionRecovery Reopen() => new(Native.Directory.Path, Identity.DeviceId);
        public void Restore() => Assert.True(Native.Journal.Restore(Identity, Native.Device.Exchange, Guard).Restored);
        public void Dispose() => Native.Dispose();
    }
    [Fact] public void SdkRecordCannotBeDeletedBeforeNativeReadbackIsDurablyVerified()
    {
        using var r = new Rig(); r.Prepare(); r.BeginPartialNative();
        Assert.Throws<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity));
        Assert.True(File.Exists(r.SdkPath())); Assert.NotNull(r.Native.Journal.Pending!.Operation);
        r.Restore(); Assert.Equal(AsrockOpenRgbRecoveryPhase.Restored, r.Native.Journal.Pending!.Phase);
        Assert.True(File.Exists(r.SdkPath()));
        r.Native.Journal.CompleteSdkSettlement(Identity); Assert.Null(r.Native.Journal.Pending);
        Assert.Empty(System.IO.Directory.GetFiles(r.Native.Directory.Path));
    }
    [Fact] public void FailedNativeCompletionSaveRetainsBothRecordsAndRetryDoesNotRepeatWrites()
    {
        using var r = new Rig(); r.Prepare(); r.BeginPartialNative();
        var failing = new AsrockOpenRgbDetectionRecovery(r.Native.Directory.Path, Identity.DeviceId, (path, value) =>
        { if (value.Phase == AsrockOpenRgbRecoveryPhase.Restored) throw new IOException("proof save failed"); ServiceEnvironment.Write(path, value); });
        Assert.Throws<IOException>(() => failing.Restore(Identity, r.Native.Device.Exchange, Guard));
        Assert.True(File.Exists(r.SdkPath())); Assert.Equal(AsrockOpenRgbRecoveryPhase.Restoring, failing.Pending!.Phase);
        r.Native.Device.Writes.Clear(); var reopened = r.Reopen();
        Assert.True(reopened.Restore(Identity, r.Native.Device.Exchange, Guard).Restored); Assert.Empty(r.Native.Device.Writes);
        reopened.CompleteSdkSettlement(Identity); Assert.Null(reopened.Pending);
    }
    [Fact] public void LockedSdkFileLeavesVerifiedProofAndCleanupRetryNeverTouchesHardware()
    {
        using var r = new Rig(); var (sdk, record) = r.Prepare(); r.BeginPartialNative(); r.Restore();
        using (sdk.AcquireReadLease(record))
            Assert.ThrowsAny<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity));
        Assert.Equal(AsrockOpenRgbRecoveryPhase.Restored, r.Native.Journal.Pending!.Phase); Assert.True(File.Exists(r.SdkPath()));
        r.Native.Device.Writes.Clear(); var reopened = r.Reopen();
        Assert.True(reopened.Restore(Identity, _ => throw new InvalidOperationException("hardware must not be reopened"), Guard).Restored);
        reopened.CompleteSdkSettlement(Identity); Assert.Empty(r.Native.Device.Writes); Assert.Null(reopened.Pending);
    }
    [Fact] public void LastNativeFileDeletionCanBeRetriedAfterSdkFilesAreAlreadyGone()
    {
        using var r = new Rig(); r.Prepare(); r.Restore(); var path = r.NativePath;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity));
        Assert.False(File.Exists(r.SdkPath())); Assert.True(File.Exists(path));
        var reopened = r.Reopen(); reopened.CompleteSdkSettlement(Identity); Assert.Empty(System.IO.Directory.GetFiles(r.Native.Directory.Path));
    }
    [Fact] public void MultipleScopesCanFinishFileCleanupIndependentlyWithoutLosingNativeProof()
    {
        using var r = new Rig(); r.Prepare(0); var (second, record) = r.Prepare(1); r.BeginPartialNative(); r.Restore();
        using (second.AcquireReadLease(record))
            Assert.ThrowsAny<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity));
        Assert.False(File.Exists(r.SdkPath(0))); Assert.True(File.Exists(r.SdkPath(1))); Assert.True(File.Exists(r.NativePath));
        r.Native.Device.Writes.Clear(); r.Reopen().CompleteSdkSettlement(Identity); Assert.Empty(r.Native.Device.Writes);
        Assert.Empty(System.IO.Directory.GetFiles(r.Native.Directory.Path));
    }
    [Theory] [InlineData("owner")] [InlineData("boot")] [InlineData("target")] [InlineData("revision")]
    public void ChangedSdkOwnershipOrUnrelatedRevisionIsNeverErased(string change)
    {
        using var r = new Rig(); var (_, record) = r.Prepare(); r.Restore();
        var changed = change switch
        {
            "owner" => record with { Scope = record.Scope with { Owner = Guid.NewGuid().ToString("N") } },
            "boot" => record with { Boot = "other-boot" },
            "target" => record with { Scope = record.Scope with { Target = record.Scope.Target with { Connection = Guid.NewGuid() } } },
            _ => record with { Revision = Guid.NewGuid().ToString("N") }
        };
        changed = changed with { Hash = SettingsTransfer.Hash(changed with { Hash = "" }) }; ServiceEnvironment.Write(r.SdkPath(), changed);
        Assert.Throws<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity));
        Assert.True(File.Exists(r.SdkPath())); Assert.Equal(AsrockOpenRgbRecoveryPhase.Restored, r.Native.Journal.Pending!.Phase);
    }
    [Fact] public void CorruptSdkRecordDoesNotInvalidateOrEraseTheVerifiedNativeProof()
    {
        using var r = new Rig(); r.Prepare(); r.Restore(); var hash = r.Native.Journal.Pending!.Hash;
        File.WriteAllText(r.SdkPath(), "{\"Version\":2,\"version\":1}");
        Assert.ThrowsAny<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity));
        Assert.Equal(hash, r.Native.Journal.Pending.Hash); Assert.True(File.Exists(r.SdkPath()));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void KnownSdkRecoveryOrAcceptanceSuccessorCanBeSettledAfterNativeRestore(bool accepted)
    {
        using var r = new Rig(); var (sdk, record) = r.Prepare();
        var current = OpenRgbLightingPlan.WithState(r.Native.Sdk, record.Mutation.After);
        var recovery = sdk.PrepareRecovery(record, Identity.Boot, r.Binding, r.Target, current);
        if (accepted)
        {
            r.Native.Journal.TrackSdk(Identity, OpenRgbSdkRecoveryLink.From(recovery), Guard);
            sdk.AcceptRecovery(recovery, recovery.Mutation.After, recovery.Mutation.Steps.Length);
        }
        r.Restore(); r.Native.Journal.CompleteSdkSettlement(Identity);
        Assert.False(File.Exists(r.SdkPath())); Assert.Null(r.Native.Journal.Pending);
    }
    [Fact] public void CancelledUnsentSdkRevisionCanSettleItsLinkedPredecessor()
    {
        using var r = new Rig(); var (sdk, first) = r.Prepare();
        var current = OpenRgbLightingPlan.WithState(r.Native.Sdk, first.Mutation.After);
        var mutation = OpenRgbLightingPlan.Build(current, first.Mutation.After,
            [new(null, Colors: Enumerable.Repeat(new RgbColor(3, 4, 5), current.Colors.Length).ToArray())]);
        var second = sdk.Prepare(Identity.Boot, first.Scope, current, mutation, first.Exit, first);
        r.Native.Journal.TrackSdk(Identity, OpenRgbSdkRecoveryLink.From(second), Guard); sdk.CancelUnsent(second, first);
        r.Restore(); r.Native.Journal.CompleteSdkSettlement(Identity); Assert.False(File.Exists(r.SdkPath()));
    }
    [Fact] public void WrongBootOrHandoverCannotTrackOrConsumeTheOtherOwnersRecords()
    {
        using var r = new Rig(); var (_, record) = r.Prepare();
        var link = OpenRgbSdkRecoveryLink.From(record);
        Assert.Throws<IOException>(() => r.Native.Journal.TrackSdk(Identity, link with { Boot = "other" }, Guard));
        r.Restore();
        Assert.Throws<IOException>(() => r.Native.Journal.CompleteSdkSettlement(Identity with { Handover = Guid.NewGuid().ToString("N") }));
        Assert.Throws<IOException>(() => r.Native.Journal.TrackSdk(Identity, link, Guard));
        Assert.True(File.Exists(r.SdkPath()));
    }
}
