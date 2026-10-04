using FlowDeck.Core;
using FlowDeck.Infrastructure;
using NativeRig = FlowDeck.Tests.AsrockOpenRgbNativePlanTests.Rig;
using static FlowDeck.Tests.AsrockOpenRgbNativeOperationTests;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbCommonTransportTests
{
    private static readonly Action Authorized = () => { };
    private static readonly LightingProgram Blue = new("fixed", [new(0, 0, 255)]);
    private static AsrockOpenRgbRecoveryIdentity Identity => AsrockOpenRgbNativePlanTests.Identity;
    private sealed class Rig : IAsyncDisposable
    {
        public readonly NativeRig Native = new();
        public readonly OpenRgbSdkModelServer Server;
        public readonly OpenRgbLightingRecovery Journal;
        public OpenRgbSdkConnection Connection = null!;
        public AsrockOpenRgbNativeExecutor Transport = null!;
        public OpenRgbLightingExecutor Executor = null!;
        private AsrockOpenRgbOperationReceipt receipt = Initial();
        private readonly AsrockPacketEmulator native = new();
        public int NativeQueries, NativeWrites;
        public bool BreakReceipt, FailBeforeWrite, ExpireBeforeWrite;
        public Rig()
        {
            Native.Device.Order = AsrockOpenRgbDetectionRecovery.DetectionOrder(Native.Before);
            Server = new(Native.Sdk);
            Journal = new(Native.Directory.Path, "common-asrock", null);
            var sdk = Server.Wire.Intercept!;
            Server.Wire.Intercept = async packet =>
            {
                if (packet.Command == AsrockOpenRgbOperationReceipt.QueryCommand)
                {
                    NativeQueries++;
                    if (!BreakReceipt || NativeWrites == 0) await Server.Wire.ReplyAsync(packet, packet.Command, Wire(receipt));
                    return true;
                }
                if (packet.Command is 1050 or 1051 or 1101)
                {
                    // The bridge test couples the independent SDK cache emulator to the prepared
                    // native packet model. Physical packet semantics have separate native tests.
                    var durable = new AsrockOpenRgbDetectionRecovery(Native.Directory.Path, Identity.DeviceId).Pending!;
                    var operation = durable.Operation!.Mutation;
                    Assert.NotNull(new OpenRgbLightingRecovery(Native.Directory.Path, "common-asrock", null).Pending);
                    Assert.Equal(packet.Command, operation.Command);
                    receipt = native.Execute(durable, receipt, Server.Wire, packet.Device, Transport.Authorization.Authorize, Native.Device.Exchange);
                    NativeWrites++;
                }
                return await sdk(packet);
            };
        }
        public async Task<Rig> Open()
        {
            Connection = await OpenRgbSdkConnection.ConnectAsync(Server.Port, ownedHeadless: true, verifyPeer: (_, _) => { },
                authorizeWrite: () => { if (ExpireBeforeWrite) throw new OpenRgbLightingExpiredException("expired at wire"); if (FailBeforeWrite) throw new IOException("route revoked at wire"); }, operationTimeout: TimeSpan.FromSeconds(2));
            var target = Assert.Single(await Connection.ScanAsync()).Target;
            Transport = new(Connection, target, Native.Plan, Native.Journal, Identity);
            var binding = new PhysicalDeviceBinding("common-asrock", "location:" + new string('a', 64), "openrgb-sdk6", target.Structure);
            var scope = new OpenRgbRecoveryScope(binding, target, IdentityConfidence.ConnectionBound, Guid.NewGuid().ToString("N"), null);
            Executor = new(Connection, Journal, scope, Identity.Boot, Authorized, Transport);
            return this;
        }
        public async Task RestoreNative()
        {
            await Connection.DisposeAsync(); await Server.DisposeAsync();
            Assert.True(Native.Journal.Restore(Identity, Native.Device.Exchange, Authorized).Restored);
        }
        public async ValueTask DisposeAsync()
        { if (Connection is not null) await Connection.DisposeAsync(); await Server.DisposeAsync(); Native.Dispose(); }
    }
    private static void Written(OpenRgbLightingExecution result) => Assert.True(result.Result.Outcome == Outcome.WrittenUnverified, result.Result.Message);

    [Theory] [InlineData(LightingExitAction.RestoreOriginal)] [InlineData(LightingExitAction.Off)] [InlineData(LightingExitAction.FixedColor)]
    public async Task CommonWholeApplyAndSdkRecoveryRetainTheExplicitNativeExit(LightingExitAction action)
    {
        await using var r = await new Rig().Open();
        var exit = new LightingExitPolicy(action, action == LightingExitAction.FixedColor ? new(0, 0, 0) : null);
        Written(await r.Executor.ApplyAsync(Blue, exit, Authorized));
        Assert.Equal(2, r.Server.WriteCount); Assert.Equal(r.Server.WriteCount, r.NativeWrites); Assert.Null(r.Native.Journal.Pending!.Operation);
        var desired = r.Native.Plan.Exit(exit);
        Assert.Equal(SettingsTransfer.Hash(desired), SettingsTransfer.Hash(r.Native.Journal.Pending.Desired));
        Written(await r.Executor.RecoverAsync(Authorized));
        Assert.Null(r.Journal.Pending); Assert.NotNull(r.Native.Journal.Pending);
        Assert.Equal(SettingsTransfer.Hash(desired), SettingsTransfer.Hash(r.Native.Journal.Pending.Desired));
        await r.RestoreNative();
        var actual = new AsrockVolatileTransaction(r.Native.Before.Identity, r.Native.Device.Exchange).Snapshot;
        Assert.Equal(r.Native.Before.Order, actual.Order); Assert.All(actual.Zones, z => Assert.Equal(desired[z.Address], z.Setting));
    }
    [Fact] public async Task SharedUpdatesKeepSiblingExitIntentAndDistinguishOffFromFixedBlack()
    {
        await using var r = await new Rig().Open();
        var colors = new Dictionary<int, RgbColor> { [0] = new(0, 0, 255), [1] = new(0, 0, 255), [2] = new(0, 0, 255) };
        var exits = colors.ToDictionary(p => p.Key, _ => new RgbColor(0, 0, 0));
        var policies = new Dictionary<int, LightingExitPolicy> { [0] = new(LightingExitAction.Off), [1] = new(LightingExitAction.FixedColor, new(0, 0, 0)),
            [2] = new(LightingExitAction.Off) };
        Written(await r.Executor.ApplySharedAsync(colors, exits, Authorized, exitPolicies: policies));
        Assert.Equal(0, r.Native.Journal.Pending!.Desired[2].Mode); Assert.Equal(1, r.Native.Journal.Pending.Desired[5].Mode);
        var changed = new RgbColor(80, 90, 100);
        Written(await r.Executor.ApplySharedAsync(new Dictionary<int, RgbColor> { [1] = new(200, 100, 50) },
            new Dictionary<int, RgbColor> { [1] = changed }, Authorized));
        var desired = r.Native.Journal.Pending!.Desired;
        Assert.Equal(0, desired[2].Mode); Assert.Equal(new BoardLightSetting(1, 80, 90, 100), desired[5]); Assert.Equal(0, desired[6].Mode);
        Written(await r.Executor.RecoverAsync(Authorized));
        Assert.Equal(SettingsTransfer.Hash(desired), SettingsTransfer.Hash(r.Native.Journal.Pending!.Desired));
        await r.RestoreNative();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NormalStopReplacesTheNativeEndWithTheExplicitNormalStop(bool off)
    {
        await using var r = await new Rig().Open();
        Written(await r.Executor.ApplyAsync(Blue, new(LightingExitAction.RestoreOriginal), Authorized));
        var stop = off ? new LightingProgram("off", []) : new LightingProgram("fixed", [new(20, 30, 40)]);
        Written(await r.Executor.RecoverAsync(Authorized, normalStop: stop));
        Assert.All(r.Native.Journal.Pending!.Desired.Values, setting => Assert.Equal(off ? new(0, 0, 0, 0) : new BoardLightSetting(1, 20, 30, 40), setting));
        await r.RestoreNative();
    }
    [Fact] public async Task LostNativeResultRetainsBothObligationsAndPreventsFurtherSdkMutations()
    {
        await using var r = await new Rig().Open(); r.BreakReceipt = true;
        var result = await r.Executor.ApplyAsync(Blue, new(LightingExitAction.Off), Authorized);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.Equal(1, r.NativeWrites);
        Assert.NotNull(r.Journal.Pending); Assert.NotNull(r.Native.Journal.Pending!.Operation);
        Assert.True(r.Executor.NeedsRecovery);
        await r.Executor.RecoverAsync(Authorized); Assert.Equal(1, r.NativeWrites);
        Assert.NotNull(r.Journal.Pending); await r.RestoreNative();
        // Native restoration alone cannot silently erase the independent SDK obligation.
        Assert.NotNull(r.Journal.Pending);
    }
    [Fact] public async Task WireRejectionBeforeSendingRevertsOnlyThisUnsentIntent()
    {
        await using var r = await new Rig().Open(); var originalHash = r.Native.Journal.Pending!.Hash; r.FailBeforeWrite = true;
        var result = await r.Executor.ApplyAsync(Blue, new(LightingExitAction.Off), Authorized);
        Assert.Equal(Outcome.Rejected, result.Result.Outcome); Assert.Equal(0, r.NativeWrites);
        Assert.Null(r.Journal.Pending); Assert.Null(r.Native.Journal.Pending!.Operation);
        Assert.Equal(AsrockOpenRgbRecoveryPhase.Detecting, r.Native.Journal.Pending.Phase);
        Assert.NotEqual(originalHash, r.Native.Journal.Pending.Hash); // The cancelled SDK record remains linked until final cleanup.
        Assert.DoesNotContain(r.Server.Wire.Requests, p => p.Command is 1050 or 1051 or 1101);
        await r.RestoreNative();
    }
    [Fact] public async Task ExpiredFrameCancelsBothPreparedIntentsAndTheNextFrameCanProceed()
    {
        await using var r = await new Rig().Open(); Written(await r.Executor.ApplyAsync(Blue, new(LightingExitAction.Off), Authorized, frame: true));
        var sdkHash = r.Journal.Pending!.Hash; var native = r.Native.Journal.Pending!; var count = r.NativeWrites;
        r.ExpireBeforeWrite = true;
        var expired = await r.Executor.ApplyAsync(new("fixed", [new(1, 2, 3)]), new(LightingExitAction.Off), Authorized, frame: true);
        Assert.Equal(Outcome.Rejected, expired.Result.Outcome); Assert.Equal(DeviceDriverFailure.LightingFrameExpired, expired.Result.Failure);
        Assert.Equal(sdkHash, r.Journal.Pending!.Hash);
        Assert.True(AsrockOpenRgbNativeOperation.Same(native.Expected, r.Native.Journal.Pending!.Expected));
        Assert.Equal(SettingsTransfer.Hash(native.Desired), SettingsTransfer.Hash(r.Native.Journal.Pending.Desired));
        Assert.True(Assert.Single(r.Native.Journal.Pending.SdkLinks!).Matches(r.Journal.Pending));
        Assert.Equal(count, r.NativeWrites); Assert.False(r.Executor.NeedsRecovery);
        r.ExpireBeforeWrite = false; Written(await r.Executor.ApplyAsync(new("fixed", [new(10, 20, 30)]), new(LightingExitAction.Off), Authorized, frame: true));
        Assert.Equal(count + 1, r.NativeWrites); Written(await r.Executor.RecoverAsync(Authorized)); await r.RestoreNative();
    }
    [Theory] [InlineData("mismatch")] [InlineData("missing")] [InlineData("restore")]
    public async Task InconsistentSharedExitPoliciesAreRejectedBeforeCreatingAnObligation(string error)
    {
        await using var r = await new Rig().Open();
        var colors = Enumerable.Range(0, 3).ToDictionary(i => i, _ => new RgbColor(0, 0, 0));
        var policies = Enumerable.Range(0, 3).ToDictionary(i => i, _ => new LightingExitPolicy(LightingExitAction.Off));
        if (error == "missing") policies.Remove(1);
        else policies[1] = error == "mismatch" ? new(LightingExitAction.FixedColor, new(50, 60, 70)) : new(LightingExitAction.RestoreOriginal);
        var result = await r.Executor.ApplySharedAsync(colors, colors, Authorized, exitPolicies: policies);
        Assert.Equal(Outcome.Rejected, result.Result.Outcome); Assert.Null(r.Journal.Pending); Assert.Null(r.Native.Journal.Pending!.Operation);
        Assert.Equal(0, r.NativeWrites); Assert.Equal(0, r.NativeQueries);
    }
    [Fact] public void PerZoneRestoreUsesTheCapturedNativeSettingWhileLeavingOtherSavedExitsIntact()
    {
        using var rig = new NativeRig(); var current = rig.Plan.Exit(new LightingExitPolicy(LightingExitAction.Off));
        var merged = rig.Plan.MergeExit(current, new(1, new(LightingExitAction.RestoreOriginal)));
        Assert.Equal(rig.Before.Zones.Single(z => z.Address == 5).Setting, merged[5]);
        Assert.Equal(0, merged[2].Mode); Assert.Equal(0, merged[6].Mode); Assert.Equal(0, current[5].Mode);
        var keep = rig.Plan.MergeExit(merged, null); Assert.Equal(SettingsTransfer.Hash(merged), SettingsTransfer.Hash(keep));
    }
    [Fact] public void AmbiguousOrEmptyNativeExitIntentsAreNotAdopted()
    {
        using var rig = new NativeRig(); var current = rig.Plan.Exit(new LightingExitPolicy(LightingExitAction.Off));
        Assert.Throws<IOException>(() => rig.Plan.MergeExit(current, new(null, null)));
        Assert.Throws<IOException>(() => rig.Plan.MergeExit(current, new(null, new(LightingExitAction.Off), [])));
        Assert.Throws<IOException>(() => rig.Plan.MergeExit(current, new(0, null, new() { [1] = new(LightingExitAction.Off) })));
        Assert.Throws<IOException>(() => rig.Plan.MergeExit(current, new(null, null, new() { [9] = new(LightingExitAction.Off) })));
    }
}
