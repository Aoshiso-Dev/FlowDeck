using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    // Real owned Windows child, real named pipe, real TCP and native implementation.
    // Only HID, enumeration/continuity and the child's server framing loop are fixtures.
    // This does not open product hardware or bypass the packaged provider's closed gate.
    private sealed class NativeSdkRig : IAsyncDisposable
    {
        private const string BinarySha256 = "5556E26BA724F752FDD23F1CF76D9471A1656D51991B514720665E4ED4DFF60F";
        internal const string FullServerSha256 = "90E24ADE26E2BBC12E786A48669D8489C191C8C48F510E945543D7B224B239DE";
        private readonly bool fullServer;
        private readonly bool ownedShutdown;
        public readonly Rig Native = new();
        public readonly ConcurrentQueue<AsrockAuthorizationRequest> Requests = new();
        public Action<AsrockAuthorizationRequest>? BeforeAuthorize;
        public OpenRgbSdkConnection Connection = null!;
        public OpenRgbSdkSnapshot Snapshot = null!;
        public AsrockOpenRgbNativeExecutor Transport = null!;
        public OpenRgbLightingExecutor Executor = null!;
        public OpenRgbLightingRecovery SdkJournal = null!;
        public JsonElement Ready;
        public OpenRgbStartupProfile Startup = null!;
        public OpenRgbRecoveryRecord? PersistedSdk => new OpenRgbLightingRecovery(Native.Directory.Path, "native-sdk-fixture", null).Pending;
        private bool stateLoaded;
        private string FilePath(string name) => Path.Combine(Native.Directory.Path, name);
        public NativeSdkRig(bool emptyFirst = false, bool fullServer = false, bool ownedShutdown = false)
        {
            this.fullServer = fullServer;
            this.ownedShutdown = ownedShutdown;
            if (ownedShutdown && !fullServer) throw new ArgumentException("Owned shutdown requires the full native server fixture");
            var device = Native.Boards[0].Device;
            device.Counts[2] = 4; device.Counts[5] = 2; device.Counts[6] = 1;
            if (emptyFirst) { device.Available |= 1; device.Set(0, new(0, 0, 0, 0)); }
            Native.BeforeRestore = LoadNativeState;
        }
        public AsrockOpenRgbRecoveryRecord Record
        {
            get
            {
                var file = Assert.Single(System.IO.Directory.GetFiles(Native.Directory.Path, AsrockOpenRgbDetectionRecovery.FilePrefix + "*.json"));
                var parsed = JsonSerializer.Deserialize<AsrockOpenRgbRecoveryRecord>(File.ReadAllText(file), Contract.Json)!;
                return new AsrockOpenRgbDetectionRecovery(Native.Directory.Path, parsed.Scope.DeviceId).Pending!;
            }
        }
        public async Task<OpenRgbOwnedProcess> Launch(OpenRgbStartupProfile profile, Func<Task> release,
            uint dropCommand = 0, int failReadAt = 0, bool useProfilePort = false)
        {
            Startup = profile;
            using var settings = JsonDocument.Parse(profile.Settings);
            var board = Native.Boards[0]; var device = board.Device;
            File.WriteAllText(FilePath("native-seed.json"), JsonSerializer.Serialize(new
            {
                path = board.Path, counts = device.Counts.Select(b => (int)b).ToArray(), order = (int)device.Order,
                available = (int)device.Available, raw = device.Raw.ToDictionary(p => p.Key.ToString(), p => p.Value.Select(b => (int)b).ToArray()),
                settings = settings.RootElement.GetProperty("ASRock Polychrome USB"), environment = settings.RootElement,
                listenPort = useProfilePort ? profile.Port : 0, dropCommand, failReadAt
            }));
            using (var resource = typeof(NativeSdkRig).Assembly.GetManifestResourceStream(fullServer
                ? "FlowDeck.Tests.AsrockFullServerIntegration.bin" : "FlowDeck.Tests.AsrockSdkIntegration.bin")!)
            using (var stream = new MemoryStream())
            {
                resource.CopyTo(stream); var image = stream.ToArray(); Assert.Equal(fullServer ? FullServerSha256 : BinarySha256, Convert.ToHexString(SHA256.HashData(image)));
                File.WriteAllBytes(FilePath("authorization-client.exe"), image);
            }
            var start = new ProcessStartInfo(FilePath("authorization-client.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Native.Directory.Path };
            start.Environment.Remove(AsrockOpenRgbStartupChannel.EnvironmentName);
            start.Environment.Remove(OpenRgbShutdownSignal.EnvironmentName);
            start.ArgumentList.Add(ownedShutdown ? "--owned-shutdown-fixture" : "--integration-fixture");
            var channel = new AsrockOpenRgbStartupChannel(Native.Scope.AttachPreparedHelper, (request, ct) =>
            {
                Requests.Enqueue(request); BeforeAuthorize?.Invoke(request); Native.Scope.Authorize(request, ct);
            });
            Native.Owner = await OpenRgbOwnedProcess.StartAsync(start, new Close(() => { }), () => { },
                info => Native.RealChild = WindowsSuspendedProcess.Create(info), release, () => channel,
                ownedShutdown ? () => new OpenRgbShutdownSignal() : null);
            Ready = await ReadFile("native-ready.json", checkError: true);
            return Native.Owner;
        }
        public async Task Open(uint dropCommand = 0, int failReadAt = 0, Func<Task>? release = null)
        {
            await Native.Scope.PrepareAsync(default);
            using var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            if (fullServer) reservation.Start();
            var port = fullServer ? ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port : 6742;
            var profile = Native.Scope.BindStartup(new(OpenRgbStartupKind.AsrockPolychromeUsb, port));
            reservation.Stop();
            var owner = await Launch(profile, release ?? (() => Task.CompletedTask), dropCommand, failReadAt, useProfilePort: fullServer);
            Native.Scope.AttachHelper(owner);
            Connection = await owner.ConnectAsync(Ready.GetProperty("port").GetInt32(), authorizeWrite: Native.Scope.CheckRoute);
            Snapshot = Assert.Single(await Connection.ScanAsync()); Native.Scope.CheckInventory([Snapshot.Controller]);
            var identities = OpenRgbDeviceIdentities.Resolve([Snapshot], new Dictionary<OpenRgbSdkTarget, string>());
            var bound = new OpenRgbBoundDevice(Connection, Snapshot, identities[Snapshot.Target.ControllerId]);
            Transport = Assert.IsType<AsrockOpenRgbNativeExecutor>(Native.Scope.CreateLightingTransport(bound));
            const string id = "native-sdk-fixture";
            SdkJournal = new(Native.Directory.Path, id, null);
            var scope = new OpenRgbRecoveryScope(OpenRgbDeviceIdentities.Binding(id, bound)!, Snapshot.Target,
                bound.Identity.Confidence, Guid.NewGuid().ToString("N"), null);
            Executor = new(Connection, SdkJournal, scope, Record.Scope.Boot, Native.Scope.CheckRoute, Transport);
        }
        public async Task<JsonElement> ReadFile(string name, bool checkError = false)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (checkError && File.Exists(FilePath("native-error.json"))) throw new IOException(File.ReadAllText(FilePath("native-error.json")));
                try
                {
                    // The child publishes by atomic replacement. Permit rename while this
                    // snapshot is read, as a slow test reader must not break native IO.
                    await using var stream = new FileStream(FilePath(name), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        4096, FileOptions.Asynchronous);
                    using var value = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
                    return value.RootElement.Clone();
                }
                catch (Exception e) when (e is IOException or JsonException) { }
                await Task.Delay(20, deadline.Token);
            }
        }
        private void LoadNativeState()
        {
            Assert.True(Native.Owner is null || Native.Owner.ResourcesClosed);
            // ResourcesClosed is published only after exit was confirmed and the process
            // handle disposed. Querying that disposed handle would mask the test's result.
            if (stateLoaded || !File.Exists(FilePath("native-state.json"))) return;
            using var value = JsonDocument.Parse(File.ReadAllText(FilePath("native-state.json"))); var state = value.RootElement;
            var device = Native.Boards[0].Device;
            device.Order = state.GetProperty("order").GetByte();
            device.Raw = state.GetProperty("raw").EnumerateObject().ToDictionary(p => byte.Parse(p.Name), p => p.Value.EnumerateArray().Select(b => b.GetByte()).ToArray());
            device.Writes.Clear(); stateLoaded = true;
        }
        public async Task Stop()
        {
            Native.Scope.CheckNativeShutdown(PersistedSdk is { } pending ? [pending] : []);
            if (Connection is not null) await Connection.DisposeAsync();
            if (Native.Owner is not null) await Native.Owner.DisposeAsync();
            await Native.Scope.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (Connection is not null) await Connection.DisposeAsync();
            if (Native.Owner is not null) await Native.Owner.DisposeAsync();
            AsrockAuthorizationFixture.DeleteExitedImage(Native.Directory.Path);
            await Native.DisposeAsync();
        }
    }
    private static readonly LightingProgram NativeSdkBlue = new("fixed", [new(0, 0, 255)]);
    private static void SdkAccepted(OpenRgbLightingExecution result) => Assert.True(result.Result.Outcome == Outcome.WrittenUnverified, JsonSerializer.Serialize(result.Result));

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeSdkDetectionAndActualSerializedInventoryMatchCapturedRecovery(bool emptyFirst)
    {
        await using var r = new NativeSdkRig(emptyFirst); await r.Open();
        var record = r.Record; Assert.Equal(AsrockOpenRgbRecoveryPhase.ExitReady, record.Phase);
        var expected = AsrockOpenRgbOperationReceipt.Parse(Encoding.UTF8.GetBytes(r.Ready.GetProperty("receipt").GetRawText() + "\0"));
        var actual = await r.Connection.ReadAsrockOperationAsync(r.Snapshot.Target, r.Native.Scope.CheckRoute, default);
        Assert.True(expected.Same(actual)); Assert.Equal(AsrockOpenRgbOperationStage.Idle, actual.Stage);
        Assert.Equal(record.Before.Zones.Length + (emptyFirst ? 1 : 0), r.Snapshot.Controller.Zones.Length);
        Assert.Equal(42u, r.Snapshot.Controller.Id); Assert.Equal("HID: " + r.Native.Boards[0].Path, r.Snapshot.Controller.Location);
        Assert.Equal(AsrockAuthorizationPhase.BeforeOpen, r.Requests.First().Phase);
        Assert.All(r.Requests, request => Assert.False(request.Origin.Sdk));
        Assert.True((await r.ReadFile("native-state.json")).GetProperty("environmentCleared").GetBoolean());
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed);
        Assert.Equal(record.Before.Order, r.Native.Boards[0].Device.Order);
        Assert.Equal(2, r.Native.Boards[0].Opens); Assert.Equal(2, r.Native.Boards[0].Closes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeSdkLightingAndDirectFramesRestoreCapturedPerZoneSettings(bool direct)
    {
        await using var r = new NativeSdkRig(emptyFirst: true); await r.Open(); var before = r.Record.Before;
        var program = direct ? NativeSdkBlue : new LightingProgram(
            OpenRgbLightingProfile.Surface(r.Snapshot.Controller, null).Native.Single(n => n.Index == 2).Effect.Id, [new(0, 0, 255)], Speed: 60);
        SdkAccepted(await r.Executor.ApplyAsync(program, new(LightingExitAction.RestoreOriginal), r.Native.Scope.CheckRoute, frame: direct));
        if (direct) SdkAccepted(await r.Executor.ApplyAsync(new("fixed", [new(80, 160, 240)]), new(LightingExitAction.RestoreOriginal), r.Native.Scope.CheckRoute, frame: true));
        Assert.NotNull(r.SdkJournal.Pending); Assert.Null(r.Record.Operation);
        var origin = r.Requests.Where(p => p.Origin.Sdk).Select(p => p.Origin).ToArray(); Assert.NotEmpty(origin);
        Assert.All(origin, value => { Assert.Equal(6u, value.Protocol); Assert.Equal(42u, value.Controller); Assert.Equal(r.Ready.GetProperty("port").GetUInt16(), value.ServerPort); Assert.NotEqual(0, value.PeerPort); });
        var receipt = await r.Connection.ReadAsrockOperationAsync(r.Snapshot.Target, r.Native.Scope.CheckRoute, default);
        Assert.Equal(AsrockOpenRgbOperationStage.Completed, receipt.Stage);
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed); Assert.Null(r.PersistedSdk);
        Assert.All(Assert.Single(r.Native.Scope.ReadNativeShutdownRecoveries()).Results, value => Assert.Equal(Outcome.Confirmed, value.Result.Outcome));
        var device = r.Native.Boards[0].Device; device.OwnerThread = null;
        var restored = new AsrockVolatileTransaction(before.Identity, device.Exchange).Snapshot;
        Assert.Equal(before.Order, restored.Order);
        foreach (var zone in before.Zones) Assert.Equal(zone.Setting, restored.Zones.Single(z => z.Address == zone.Address).Setting);
    }

    [Theory] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task NativeSdkDeniedPacketKeepsUncertainOperationAndRestoresAfterChildExit(int phase)
    {
        await using var r = new NativeSdkRig(); await r.Open();
        r.BeforeAuthorize = request => { if (request.Origin.Sdk && (int)request.Phase == phase) throw new IOException("fixture packet denied"); };
        var result = await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.NotNull(r.SdkJournal.Pending); Assert.NotNull(r.Record.Operation);
        var state = await r.ReadFile("native-state.json"); var count = state.GetProperty("packets").GetArrayLength();
        _ = await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute);
        Assert.Equal(count, (await r.ReadFile("native-state.json")).GetProperty("packets").GetArrayLength());
        await r.Stop(); Assert.Null(r.PersistedSdk); Assert.True(r.Native.Scope.ResourcesClosed);
    }

    [Fact] public async Task NativeSdkLostAcknowledgementRetainsCompletedNativeMutationForRecovery()
    {
        await using var r = new NativeSdkRig(); await r.Open(dropCommand: 1101);
        var result = await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.NotNull(r.Record.Operation); Assert.NotNull(r.SdkJournal.Pending);
        var native = await r.ReadFile("native-receipt.json"); Assert.Equal((int)AsrockOpenRgbOperationStage.Completed, native.GetProperty("stage").GetInt32());
        await r.Stop(); Assert.Null(r.PersistedSdk);
        Assert.All(r.Native.Boards[0].Device.Raw.Where(p => r.Native.Boards[0].Device.Counts[p.Key] > 0), p => Assert.Equal(0, p.Value[0]));
    }

    [Fact] public async Task NativeSdkReadTimeoutReportsPartialMutationAndRestoresIt()
    {
        // Three initial queries, color-order write and three zone queries precede this read.
        await using var r = new NativeSdkRig(); await r.Open(failReadAt: 8);
        var result = await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome); Assert.NotNull(r.Record.Operation);
        var native = await r.ReadFile("native-receipt.json");
        Assert.Equal((int)AsrockOpenRgbOperationStage.Failed, native.GetProperty("stage").GetInt32());
        Assert.Equal(0, native.GetProperty("after").GetProperty("readResult").GetInt32());
        Assert.Equal(1, r.Requests.Count(p => p.Origin.Sdk && p.Phase == AsrockAuthorizationPhase.BeforeWrite));
        await r.Stop(); Assert.Null(r.PersistedSdk); Assert.True(r.Native.Scope.ResourcesClosed);
    }

    [Fact] public async Task NativeSdkPhysicalDisconnectPreventsNativeWriteAndKeepsRecovery()
    {
        await using var r = new NativeSdkRig(); await r.Open();
        var initial = (await r.ReadFile("native-state.json")).GetProperty("packets").GetArrayLength();
        r.BeforeAuthorize = request => { if (request.Origin.Sdk) r.Native.Boards[0].Watch.Revoked = true; };
        var result = await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute);
        Assert.Equal(Outcome.Unknown, result.Result.Outcome);
        Assert.Equal(initial, (await r.ReadFile("native-state.json")).GetProperty("packets").GetArrayLength());
        r.Native.AllowUnrestored = true;
        await Assert.ThrowsAsync<AggregateException>(r.Stop);
        Assert.True(r.Native.Owner!.ResourcesClosed); Assert.False(r.Native.Scope.ResourcesClosed);
        Assert.NotNull(r.Record.Operation); Assert.NotNull(r.PersistedSdk);
        Assert.Equal(1, r.Native.Boards[0].Opens); // No native restoration after continuity was lost.
    }

    [Fact] public async Task NativeSdkUnarmedWriteCannotBypassTheDurableOperation()
    {
        await using var r = new NativeSdkRig(); await r.Open();
        var initial = (await r.ReadFile("native-state.json")).GetProperty("packets").GetArrayLength();
        var colors = Enumerable.Repeat(new RgbColor(0, 0, 255), r.Snapshot.Controller.Colors.Length).ToArray();
        await Assert.ThrowsAnyAsync<IOException>(() => r.Connection.WriteColorsAsync(r.Snapshot.Target, colors, r.Native.Scope.CheckRoute));
        Assert.Equal(initial, (await r.ReadFile("native-state.json")).GetProperty("packets").GetArrayLength());
        Assert.Null(r.Record.Operation); Assert.Null(r.PersistedSdk);
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed);
    }
}
