using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using NativeRig = FlowDeck.Tests.AsrockOpenRgbNativePlanTests.Rig;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbDetectionAuthorizationTests
{
    private static AsrockOpenRgbRecoveryIdentity Identity => AsrockOpenRgbNativePlanTests.Identity;
    private sealed class Rig : IDisposable
    {
        public readonly NativeRig Native;
        public readonly AsrockDetectionEmulator Emulator = new();
        public readonly AsrockOpenRgbDetectionAuthorization Authorization;
        public Action? Guard;
        public Action<AsrockAuthorizationRequest>? AtRequest;
        public Func<AsrockAuthorizationRequest, AsrockAuthorizationRequest>? Transform;
        public Rig(bool empty = false)
        {
            Native = new(emptyFirst: empty);
            Authorization = new(Native.Journal, Identity, () => Guard?.Invoke());
        }
        public AsrockOpenRgbOperationReceipt Run() => Emulator.Execute(Native.Before.Identity, Native.Before.Available,
            Native.Before.Counts, Native.Before.Order, (request, ct) =>
            { AtRequest?.Invoke(request); Authorization.Authorize(Transform?.Invoke(request) ?? request, ct); }, Native.Device.Exchange);
        public void Restore()
        {
            Native.Device.Writes.Clear();
            Assert.True(Native.Journal.Restore(Identity, Native.Device.Exchange, () => { }).Restored);
            Assert.Equal(Native.Before.Order, Native.Device.Order); Assert.Null(Native.Journal.Pending);
        }
        public void Dispose() => Native.Dispose();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CompleteStartupStoresTheExactIdleBaselineAndRequiresAllStages(bool empty)
    {
        using var r = new Rig(empty); Assert.Throws<IOException>(r.Authorization.RequireCompleted);
        var receipt = r.Run(); r.Authorization.RequireCompleted();
        Assert.Equal(empty ? 8ul : 7ul, receipt.After.Attempted);
        Assert.True(receipt.Same(r.Native.Journal.Pending!.Receipt!));
        Assert.Equal(AsrockOpenRgbRecoveryPhase.ExitReady, r.Native.Journal.Pending.Phase);
        Assert.Single(r.Native.Device.Writes); Assert.Equal(0x15, r.Native.Device.Writes[0][1]);
        Assert.Equal(empty, r.Native.Journal.Pending.EmptyZoneConfigurations is not null); r.Restore();
    }
    public static IEnumerable<object[]> Boundaries() => Enumerable.Range(1, 7)
        .SelectMany(packet => new[] { 2, 3, 4 }.Select(phase => new object[] { packet, phase }));
    [Theory] [MemberData(nameof(Boundaries))]
    public void RevocationAtEveryBoundaryStopsDetectionAndPreservesNativeRestoration(int packet, int phase)
    {
        using var r = new Rig();
        r.AtRequest = request => { if (request.Io.Attempted == (ulong)packet && (int)request.Phase == phase) r.Guard = () => throw new IOException("continuity revoked"); };
        Assert.Throws<IOException>(() => r.Run()); Assert.Throws<IOException>(r.Authorization.RequireCompleted);
        Assert.Equal(packet > 4 || packet == 4 && phase != 2 ? 1 : 0, r.Native.Device.Writes.Count);
        Assert.Null(r.Native.Journal.Pending!.Receipt); Assert.NotNull(r.Native.Journal.Pending); r.Restore();
    }
    [Theory] [InlineData(1, 4)] [InlineData(1, 11)] [InlineData(2, 4)] [InlineData(3, 4)]
    [InlineData(5, 4)] [InlineData(5, 5)] [InlineData(5, 6)] [InlineData(5, 7)] [InlineData(5, 8)] [InlineData(6, 4)] [InlineData(7, 8)]
    public void ChangedCapturedFieldsRejectTheResponseBeforeItCanBeAdopted(int packet, int position)
    {
        using var r = new Rig();
        r.Transform = request =>
        {
            if (request.Io.Attempted == (ulong)packet && request.Phase == AsrockAuthorizationPhase.AfterRead)
            { var response = request.Io.Response.ToArray(); response[position] ^= 1; return request with { Io = request.Io with { Response = response } }; }
            return request;
        };
        Assert.Throws<IOException>(() => r.Run()); Assert.Throws<IOException>(r.Authorization.RequireCompleted);
        Assert.Equal(packet > 4 ? 1 : 0, r.Native.Device.Writes.Count); r.Restore();
    }
    [Fact] public void EmptyPresentZoneConfigurationIsAlsoCompared()
    {
        using var r = new Rig(true); Assert.Single(r.Native.Journal.Pending!.EmptyZoneConfigurations!);
        r.Transform = request =>
        {
            if (request.Io.Request[1] == 0x11 && request.Io.Request[3] == 0 && request.Phase == AsrockAuthorizationPhase.AfterRead)
            { var bytes = request.Io.Response.ToArray(); bytes[8]++; return request with { Io = request.Io with { Response = bytes } }; }
            return request;
        };
        Assert.Throws<IOException>(() => r.Run()); r.Restore();
    }
    [Theory] [InlineData("path")] [InlineData("sdk")] [InlineData("port")] [InlineData("sequence")]
    [InlineData("serial")] [InlineData("packet")] [InlineData("count")] [InlineData("phase")]
    public void UnplannedStartupRequestsPoisonThePermission(string change)
    {
        using var r = new Rig();
        r.Transform = request => request.Phase == AsrockAuthorizationPhase.BeforeOpen ? request : change switch
        {
            "path" => request with { Path = "other" },
            "sdk" => request with { Origin = request.Origin with { Sdk = true } },
            "port" => request with { Origin = request.Origin with { ServerPort = 6742 } },
            "sequence" => request with { Sequence = 1 },
            "serial" => request with { Phase = AsrockAuthorizationPhase.BeforeSerial },
            "phase" => request with { Phase = AsrockAuthorizationPhase.AfterRead },
            "count" => request with { Io = request.Io with { Attempted = request.Io.Attempted + 1, Completed = request.Io.Completed + 1 } },
            _ => request with { Io = request.Io with { Request = AsrockRgbMonitor.Query(0x14, 1) } }
        };
        Assert.Throws<IOException>(() => r.Run()); Assert.Empty(r.Native.Device.Writes);
        Assert.Throws<IOException>(() => r.Authorization.Authorize(r.Emulator.Requests[0] with { Sequence = 50 }, default)); r.Restore();
    }
    [Fact] public void CompletedDetectionCannotBeOpenedOrQueriedAgain()
    {
        using var r = new Rig(); r.Run();
        Assert.Throws<IOException>(() => r.Authorization.Authorize(r.Emulator.Requests[0] with { Sequence = 100 }, default));
        Assert.Throws<IOException>(r.Authorization.RequireCompleted); r.Restore();
    }
    [Fact] public void SdkInitialReceiptMustEqualTheAuthorizedDetectionBaseline()
    {
        using var r = new Rig(); var baseline = r.Run(); var record = r.Native.Journal.Pending!;
        var operation = r.Native.Plan.Prepare(record.Expected, r.Native.Sdk, new(null, 15, r.Native.Sdk.Modes[15]));
        Assert.Throws<IOException>(() => r.Native.Journal.PrepareOperation(Identity, operation, AsrockOpenRgbNativeOperationTests.Initial(), record.Desired, () => { }));
        var prepared = r.Native.Journal.PrepareOperation(Identity, operation, baseline, record.Desired, () => { });
        Assert.NotNull(prepared.Operation); r.Restore();
    }
    [Fact] public void SavedDetectionRecordCannotBeReplacedWhileRequestsAreInProgress()
    {
        using var r = new Rig();
        r.AtRequest = request =>
        {
            if (request.Phase == AsrockAuthorizationPhase.BeforeWrite)
            {
                var record = r.Native.Journal.Pending!;
                r.Native.Journal.PrepareExit(Identity, record.Expected, record.Desired, () => { });
            }
        };
        Assert.Throws<IOException>(() => r.Run()); Assert.Empty(r.Native.Device.Writes); r.Restore();
    }
    [Fact] public void FailureToSaveTheStartupBaselineDoesNotGrantTheFinalReply()
    {
        using var r = new Rig();
        var journal = new AsrockOpenRgbDetectionRecovery(r.Native.Directory.Path, Identity.DeviceId, (_, _) => throw new IOException("disk full"));
        var auth = new AsrockOpenRgbDetectionAuthorization(journal, Identity, () => { });
        Assert.Throws<IOException>(() => r.Emulator.Execute(r.Native.Before.Identity, r.Native.Before.Available,
            r.Native.Before.Counts, r.Native.Before.Order, auth.Authorize, r.Native.Device.Exchange));
        Assert.Throws<IOException>(auth.RequireCompleted); Assert.Null(journal.Pending!.Receipt); r.Restore();
    }
    [Fact] public void CaptureReadsBackEmptyZoneFieldsBeforeItAllowsStartup()
    {
        using var directory = new ServiceTestDirectory(); var device = new AsrockVolatileTransactionTests.Device();
        device.Available |= 1; device.Set(0, new(1, 0, 0, 0));
        var journal = new AsrockOpenRgbDetectionRecovery(directory.Path, Identity.DeviceId, (path, record) =>
        { ServiceEnvironment.Write(path, record); device.Set(0, new(2, 10, 20, 30)); });
        Assert.Throws<IOException>(() => journal.Capture(Identity, "fixture", device.Exchange, () => { }));
        Assert.Empty(device.Writes); Assert.NotNull(journal.Pending!.EmptyZoneConfigurations);
    }
    [Fact] public void DeadlineDuringBaselineSaveLeavesRestorationEvidenceButCannotPublishCompletion()
    {
        using var r = new Rig(); using var deadline = new CancellationTokenSource();
        var journal = new AsrockOpenRgbDetectionRecovery(r.Native.Directory.Path, Identity.DeviceId, (path, record) =>
        { ServiceEnvironment.Write(path, record); deadline.Cancel(); });
        var auth = new AsrockOpenRgbDetectionAuthorization(journal, Identity, () => { });
        Assert.Throws<OperationCanceledException>(() => r.Emulator.Execute(r.Native.Before.Identity, r.Native.Before.Available,
            r.Native.Before.Counts, r.Native.Before.Order, (request, _) => auth.Authorize(request, deadline.Token), r.Native.Device.Exchange));
        Assert.Throws<IOException>(auth.RequireCompleted);
        var reopened = new AsrockOpenRgbDetectionRecovery(r.Native.Directory.Path, Identity.DeviceId);
        Assert.NotNull(reopened.Pending!.Receipt);
        Assert.Throws<IOException>(() => new AsrockOpenRgbDetectionAuthorization(reopened, Identity, () => { }));
        Assert.True(reopened.Restore(Identity, r.Native.Device.Exchange, () => { }).Restored);
        Assert.Equal(r.Native.Before.Order, r.Native.Device.Order);
    }
    [Fact] public async Task ConcurrentRequestsRevokeStartupAndExternalGuardsDoNotHoldTheJournal()
    {
        using var r = new Rig(); using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        r.Guard = () => { entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(5))); };
        var request = new AsrockAuthorizationRequest(1, AsrockAuthorizationPhase.BeforeOpen, r.Native.Before.Identity,
            new(false, 0, 0, 0, 0, 0), new(0, 0, 0, false, -2, -2, new byte[65], new byte[64]));
        var first = Task.Run(() => r.Authorization.Authorize(request, default));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            Assert.NotNull(await Task.Run(() => r.Native.Journal.Pending).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Throws<IOException>(() => r.Authorization.Authorize(request, default));
        }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Throws<IOException>(r.Authorization.RequireCompleted); Assert.Empty(r.Native.Device.Writes); r.Restore();
    }
    [Fact] public void OldEmptyZoneRecordCanBeRestoredButCannotAuthorizeNewDetectionWithoutItsMissingEvidence()
    {
        using var r = new Rig(true); var old = r.Native.Journal.Pending! with { EmptyZoneConfigurations = null, Hash = "" };
        old = old with { Hash = SettingsTransfer.Hash(old) };
        var path = Assert.Single(Directory.GetFiles(r.Native.Directory.Path, "*.json")); ServiceEnvironment.Write(path, old);
        var reopened = new AsrockOpenRgbDetectionRecovery(r.Native.Directory.Path, Identity.DeviceId);
        Assert.Throws<IOException>(() => new AsrockOpenRgbDetectionAuthorization(reopened, Identity, () => { }));
        Assert.True(reopened.Restore(Identity, r.Native.Device.Exchange, () => { }).Restored);
    }
    [Theory] [InlineData("startup-normal")] [InlineData("startup-empty")] [InlineData("startup-unavailable")]
    public void ActualPinnedNativeDetectorTraceAndInitialSdkReceiptMatchTheParentPolicy(string name)
    {
        using var stream = GetType().Assembly.GetManifestResourceStream("FlowDeck.Tests.AsrockNativeDetectionPatch5.json")!;
        using var fixture = JsonDocument.Parse(stream);
        var entry = fixture.RootElement.EnumerateArray().Single(e => e.GetProperty("case").GetString() == name);
        using var directory = new ServiceTestDirectory();
        var device = new AsrockVolatileTransactionTests.Device { Order = entry.GetProperty("order").GetByte(), Available = entry.GetProperty("available").GetByte(),
            Counts = entry.GetProperty("counts").EnumerateArray().Select(b => b.GetByte()).ToArray() };
        device.Set(0, new(1, 12, 34, 56, 224)); device.Set(2, new(1, 12, 34, 56, 224));
        var journal = new AsrockOpenRgbDetectionRecovery(directory.Path, Identity.DeviceId);
        journal.Capture(Identity, "fixture", device.Exchange, () => { });
        var auth = new AsrockOpenRgbDetectionAuthorization(journal, Identity, () => { });
        foreach (var request in entry.GetProperty("requests").EnumerateArray())
            auth.Authorize(AsrockOpenRgbAuthorizationProtocol.Read(Encoding.UTF8.GetBytes(request.GetRawText())), default);
        auth.RequireCompleted();
        var receipt = AsrockOpenRgbOperationReceipt.Parse(Encoding.UTF8.GetBytes(entry.GetProperty("receipt").GetRawText() + "\0"));
        Assert.True(receipt.Same(journal.Pending!.Receipt!));
    }
}
