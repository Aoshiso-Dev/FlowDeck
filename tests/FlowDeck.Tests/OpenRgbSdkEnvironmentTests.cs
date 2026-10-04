using System.Buffers.Binary;
using System.Text;
using FlowDeck.Infrastructure;
using static FlowDeck.Tests.OpenRgbSdkTestServer;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbSdkEnvironmentTests
{
    [Fact] public async Task PinnedCaseSensitiveDetectorIdentifiersArePreservedAtTheWireBoundary()
    {
        await using var r = new Rig();
        using var profile = System.Text.Json.JsonDocument.Parse(new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, 6742).Settings);
        r.Detectors = profile.RootElement.GetProperty("Detectors").GetRawText();
        await using var connection = await r.Connect(); var environment = await connection.ReadEnvironmentAsync();
        Assert.Equal(1911, environment.Detectors.GetProperty("detectors").EnumerateObject().Count());
    }
    [Theory]
    [InlineData("{\"detectors\":{\"same\":false,\"same\":true}}")]
    [InlineData("{\"hid_safe_mode\":true,\"HID_SAFE_MODE\":false,\"detectors\":{}}")]
    public async Task RepeatedExactDetectorNamesAndCaseAliasesOfControlSettingsRemainInvalid(string detectors)
    {
        await using var r = new Rig(); r.Detectors = detectors; await using var connection = await r.Connect();
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ReadEnvironmentAsync());
    }
    [Fact] public async Task OwnedHeadlessDoesNotRequestAnUnimplementedPluginListOrInventAnEmptyList()
    {
        await using var r = new Rig();
        r.Before = async packet =>
        {
            if (packet.Command == 52 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Data) == 17)
            { await r.Server.ReplyAsync(packet, 53, Numbers(r.Server.Flags)); return true; }
            if (packet.Command == 200) throw new Exception("Pinned headless has no plugin-list payload");
            return false;
        };
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(r.Server.Port, ownedHeadless: true,
            verifyPeer: (client, remote) => WindowsTcpPeer.Check((uint)Environment.ProcessId, client, remote));
        var environment = await connection.ReadEnvironmentAsync();
        Assert.True(environment.OwnedHeadless); Assert.Null(environment.Plugins);
        Assert.DoesNotContain(r.Server.Requests, p => p.Command == 200); connection.CheckEnvironment(environment.Stamp);
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly OpenRgbSdkTestServer Server = new() { Flags = 25 };
        public readonly TestClock Clock = new();
        public Func<Packet, Task<bool>>? Before;
        public string Detectors = "{\"detectors\":{\"NZXT Control Hub\":false,\"ASRock USB\":false,\"Test USB\":true},\"hid_safe_mode\":true}";
        public string Schema = "{\"detectors\":{\"type\":\"object\"}}";
        public string ServerSettings = "{\"all_controllers\":false,\"default_host\":\"127.0.0.1\"}";
        public string Drivers = "{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2}";
        public readonly Dictionary<string, string> StartupSettings = new()
        {
            ["Client"] = "{\"clients\":[]}", ["ProfileManager"] = "{}",
            ["QMKOpenRGBDevices"] = "{\"devices\":[]}", ["QMKVialRGBDevices"] = "{\"devices\":[]}", ["ASRock Polychrome USB"] = "{}"
        };
        public byte[] PluginData = PluginList();
        public Rig() => Server.Intercept = async packet =>
        {
            if (Before is not null && await Before(packet)) return true;
            if (packet.Command == 52 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Data) == 25)
            { await Server.ReplyAsync(packet, 53, Numbers(Server.Flags)); return true; }
            if (packet.Command is 250 or 251)
            {
                var key = Encoding.UTF8.GetString(packet.Data); Assert.Equal(0u, packet.Device);
                var value = packet.Command == 251 ? Schema : key switch
                {
                    "Detectors\0" => Detectors, "Drivers\0" => Drivers, "Server\0" => ServerSettings,
                    _ => StartupSettings[key.TrimEnd('\0')]
                };
                await Server.ReplyAsync(packet, packet.Command, Json(value)); return true;
            }
            if (packet.Command == 200) { await Server.ReplyAsync(packet, 200, PluginData); return true; }
            return false;
        };
        public Task<OpenRgbSdkConnection> Connect() => OpenRgbSdkConnection.ConnectAsync(Server.Port, clock: Clock);
        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }
    private static byte[] Json(string value) => Encoding.UTF8.GetBytes(value + '\0');
    private static byte[] PluginList(params uint[] indices) => Sized(w =>
    {
        if (indices.Length == 0) indices = [7];
        w.Write((ushort)indices.Length);
        foreach (var index in indices) { Text(w, "Effects"); Text(w, "Test plugin"); Text(w, "1.0"); w.Write(index); w.Write(2u); }
    });
    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReadsBoundedSettingsAndPluginMetadataWithoutRequestingLocalPrivilegesOrChangingHardware(bool ackFirst)
    {
        await using var r = new Rig(); r.Server.Fragment = true; r.Server.AckFirst = ackFirst;
        await using var connection = await r.Connect(); var value = await connection.ReadEnvironmentAsync();
        Assert.False(value.Detectors.GetProperty("detectors").GetProperty("NZXT Control Hub").GetBoolean());
        Assert.False(value.Server.GetProperty("all_controllers").GetBoolean());
        Assert.True(value.Drivers.GetProperty("shared_smbus_access").GetBoolean()); Assert.Equal(2, value.Drivers.GetProperty("smbus_sleep_mode").GetInt32());
        var plugin = Assert.Single(value.Plugins!); Assert.Equal(7u, plugin.Index); Assert.Equal(2u, plugin.ProtocolVersion); Assert.Equal("1.0", plugin.Version);
        connection.CheckEnvironment(value.Stamp);
        Assert.All(r.Server.Requests, p => Assert.Contains(p.Command, new uint[] { 40, 50, 52, 250, 251, 200 }));
        Assert.All(r.Server.Requests.Where(p => p.Command == 52), p => Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(p.Data) & 65536));
        Assert.Equal(8, r.Server.Requests.Count(p => p.Command is 250 or 251)); Assert.Equal(2, r.Server.Requests.Count(p => p.Command == 200));
    }

    [Fact]
    public async Task MissingSettingsSupportIsReportedBeforeAnyInspectionRequest()
    {
        await using var r = new Rig(); r.Server.Flags = 1; await using var connection = await r.Connect(); var count = r.Server.Requests.Count;
        await Assert.ThrowsAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.Equal(count, r.Server.Requests.Count);
        Assert.True(connection.IsConnected); Assert.Equal(2, (await connection.ScanAsync()).Length);
    }

    [Fact]
    public async Task UnavailablePluginListingIsUnknownRatherThanAnEmptyList()
    {
        await using var r = new Rig(); r.Server.Flags = 17; await using var connection = await r.Connect();
        var value = await connection.ReadEnvironmentAsync(); Assert.Null(value.Plugins);
        Assert.DoesNotContain(r.Server.Requests, p => p.Command == 200);
    }

    [Fact]
    public async Task AnExplicitEmptyPluginListIsPreservedAsAnObservedEmptyList()
    {
        await using var r = new Rig(); r.PluginData = Sized(w => w.Write((ushort)0)); await using var connection = await r.Connect();
        var value = await connection.ReadEnvironmentAsync(); Assert.NotNull(value.Plugins); Assert.Empty(value.Plugins);
        Assert.Equal(2, r.Server.Requests.Count(p => p.Command == 200));
    }

    [Fact]
    public async Task ObservedSettingsChangingBetweenPassesInvalidateThePreviousReceipt()
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var previous = await connection.ReadEnvironmentAsync(); var calls = 0;
        r.Before = p =>
        {
            if (p.Command == 250 && Encoding.UTF8.GetString(p.Data) == "Detectors\0" && ++calls == 2) r.Detectors = "{\"detectors\":{\"NZXT Control Hub\":true}}";
            return Task.FromResult(false);
        };
        await Assert.ThrowsAsync<OpenRgbSdkObservationChangedException>(() => connection.ReadEnvironmentAsync());
        Assert.Throws<IOException>(() => connection.CheckEnvironment(previous.Stamp)); Assert.True(connection.IsConnected);
        r.Before = null; var next = await connection.ReadEnvironmentAsync(); connection.CheckEnvironment(next.Stamp);
        Assert.True(next.Detectors.GetProperty("detectors").GetProperty("NZXT Control Hub").GetBoolean());
    }

    [Fact]
    public async Task PluginListChangesAreAlsoRejectedAsAMixedObservation()
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var calls = 0;
        r.Before = p => { if (p.Command == 200 && ++calls == 2) r.PluginData = PluginList(8); return Task.FromResult(false); };
        await Assert.ThrowsAsync<OpenRgbSdkObservationChangedException>(() => connection.ReadEnvironmentAsync());
    }

    [Fact]
    public async Task DriverSettingsParticipateInTheReceiptAndChangesRejectMixedObservations()
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var prior = await connection.ReadEnvironmentAsync(); var calls = 0;
        r.Before = p =>
        {
            if (p.Command == 250 && Encoding.UTF8.GetString(p.Data) == "Drivers\0" && ++calls == 2) r.Drivers = "{\"shared_smbus_access\":false}";
            return Task.FromResult(false);
        };
        await Assert.ThrowsAsync<OpenRgbSdkObservationChangedException>(() => connection.ReadEnvironmentAsync());
        Assert.Throws<IOException>(() => connection.CheckEnvironment(prior.Stamp));
        r.Before = null; var current = await connection.ReadEnvironmentAsync(); Assert.NotEqual(prior.Stamp.Digest, current.Stamp.Digest);
        Assert.False(current.Drivers.GetProperty("shared_smbus_access").GetBoolean()); connection.CheckEnvironment(current.Stamp);
    }

    [Fact]
    public async Task MissingDriverSettingsArePreservedAsUnavailableWithoutInventingDefaults()
    {
        await using var r = new Rig(); r.Drivers = "null"; await using var connection = await r.Connect();
        var value = await connection.ReadEnvironmentAsync(); Assert.Equal(System.Text.Json.JsonValueKind.Null, value.Drivers.ValueKind);
        connection.CheckEnvironment(value.Stamp); r.Drivers = "{}"; var empty = await connection.ReadEnvironmentAsync();
        Assert.NotEqual(value.Stamp.Digest, empty.Stamp.Digest); Assert.Equal(System.Text.Json.JsonValueKind.Object, empty.Drivers.ValueKind);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"shared_smbus_access\":true,\"shared_smbus_access\":false}")]
    public async Task MalformedDriverSettingsStillFaultTheConnection(string drivers)
    {
        await using var r = new Rig(); r.Drivers = drivers; await using var connection = await r.Connect();
        await Assert.ThrowsAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task JsonPropertyOrderingAndPluginOrderingDoNotCreateFalseSettingChanges()
    {
        await using var r = new Rig(); r.Detectors = "{\"a\":true,\"b\":false}"; r.PluginData = PluginList(1, 2);
        await using var connection = await r.Connect(); var calls = 0;
        r.Before = p =>
        {
            if (p.Command == 250 && Encoding.UTF8.GetString(p.Data) == "Detectors\0" && ++calls == 2)
            { r.Detectors = "{\"b\":false,\"a\":true}"; r.PluginData = PluginList(2, 1); }
            return Task.FromResult(false);
        };
        var value = await connection.ReadEnvironmentAsync(); connection.CheckEnvironment(value.Stamp);
    }

    [Fact]
    public async Task ANewObservationInvalidatesOldReceiptsEvenWhenTheSettingsAreIdentical()
    {
        await using var r = new Rig(); await using var connection = await r.Connect();
        var first = await connection.ReadEnvironmentAsync(); first.Plugins![0] = first.Plugins[0] with { Name = "modified by caller" };
        var second = await connection.ReadEnvironmentAsync(); Assert.Equal(first.Stamp.Digest, second.Stamp.Digest);
        Assert.NotEqual(first.Stamp.Receipt, second.Stamp.Receipt); Assert.Equal("Effects", second.Plugins![0].Name);
        Assert.Throws<IOException>(() => connection.CheckEnvironment(first.Stamp)); connection.CheckEnvironment(second.Stamp);
    }

    [Theory]
    [InlineData(2.001)] [InlineData(-1)]
    public async Task ExpiredOrBackwardsClockReceiptsCannotAuthorizeAnOperation(double seconds)
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var value = await connection.ReadEnvironmentAsync();
        r.Clock.Advance(seconds); Assert.Throws<IOException>(() => connection.CheckEnvironment(value.Stamp));
    }

    [Fact]
    public async Task DetectionAndInventoryChangesInvalidateReceiptsWithoutPollingAgain()
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var value = await connection.ReadEnvironmentAsync();
        await r.Server.SendAsync(0, 101, []); await Eventually(() => connection.InventoryRevision > value.Stamp.InventoryRevision);
        Assert.Throws<IOException>(() => connection.CheckEnvironment(value.Stamp)); var requests = r.Server.Requests.Count;
        await Assert.ThrowsAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.Equal(requests, r.Server.Requests.Count);
        var revision = connection.InventoryRevision; await r.Server.SendAsync(0, 103, []); await Eventually(() => connection.InventoryRevision > revision);
        connection.CheckEnvironment((await connection.ReadEnvironmentAsync()).Stamp);
        var newer = await connection.ReadEnvironmentAsync(); await r.Server.SendAsync(0, 100, []);
        await Eventually(() => connection.InventoryRevision > newer.Stamp.InventoryRevision); Assert.Throws<IOException>(() => connection.CheckEnvironment(newer.Stamp));
    }

    [Fact]
    public async Task AListChangeDuringInspectionDiscardsTheEntireResult()
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var once = false;
        r.Before = async p => { if (p.Command == 250 && !once) { once = true; await r.Server.SendAsync(0, 100, []); } return false; };
        await Assert.ThrowsAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.True(connection.IsConnected);
        r.Before = null; connection.CheckEnvironment((await connection.ReadEnvironmentAsync()).Stamp);
    }

    [Fact]
    public async Task AReceiptFromAnotherConnectionWithIdenticalDataCannotBeReused()
    {
        await using var a = new Rig(); await using var b = new Rig();
        await using var first = await a.Connect(); await using var second = await b.Connect();
        var old = await first.ReadEnvironmentAsync(); var current = await second.ReadEnvironmentAsync(); Assert.Equal(old.Stamp.Digest, current.Stamp.Digest);
        Assert.Throws<IOException>(() => second.CheckEnvironment(old.Stamp)); second.CheckEnvironment(current.Stamp);
    }

    [Theory]
    [InlineData(1u)] [InlineData(65561u)]
    public async Task ChangedFeatureOrUnexpectedPrivilegeFlagsCannotProceedToSettingsReads(uint flags)
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); r.Server.Flags = flags;
        await Assert.ThrowsAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.DoesNotContain(r.Server.Requests, p => p.Command is 250 or 251);
    }

    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("{\"a\":true,\"A\":false}")]
    [InlineData("{\"a\":{\"b\":1,\"b\":2}}")]
    [InlineData("{\"a\":1,}")]
    public async Task MalformedSettingsFaultTheConnectionInsteadOfCreatingAReceipt(string value)
    {
        await using var r = new Rig(); r.Detectors = value; await using var connection = await r.Connect();
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.False(connection.IsConnected);
    }

    [Theory]
    [InlineData("unterminated")] [InlineData("embedded-null")] [InlineData("utf8")]
    public async Task InvalidSettingsTextIsRejectedAtTheWireBoundary(string kind)
    {
        await using var r = new Rig(); await using var connection = await r.Connect();
        r.Before = async p =>
        {
            if (p.Command != 250) return false;
            byte[] bytes = kind == "unterminated" ? "{}"u8.ToArray() : kind == "utf8" ? [0xc3, 0x28, 0] : [123, 0, 125, 0];
            await r.Server.ReplyAsync(p, 250, bytes); return true;
        };
        await Assert.ThrowsAnyAsync<IOException>(() => connection.ReadEnvironmentAsync()); Assert.False(connection.IsConnected);
    }

    [Fact]
    public void DeepOrExcessiveJsonAndTruncatedDuplicateOrExcessivePluginListsAreRejected()
    {
        Assert.Throws<IOException>(() => OpenRgbSdkProtocol.SettingsObject(Json(string.Concat(Enumerable.Repeat("{\"a\":", 30)) + "0" + new string('}', 30))));
        Assert.Throws<IOException>(() => OpenRgbSdkProtocol.SettingsObject(Json("{\"a\":[" + string.Join(',', Enumerable.Repeat("0", 32769)) + "]}")));
        Assert.Throws<IOException>(() => OpenRgbSdkProtocol.Plugins(PluginList(1, 1)));
        Assert.Throws<IOException>(() => OpenRgbSdkProtocol.Plugins(PluginList()[..^1]));
        Assert.Throws<IOException>(() => OpenRgbSdkProtocol.Plugins(Sized(w => w.Write((ushort)65))));
        Assert.Throws<IOException>(() => OpenRgbSdkProtocol.Plugins([.. PluginList(), 0]));
    }

    [Fact]
    public async Task AResponseWithoutItsAckCannotPublishAnInspectionAndCancellationClosesTheConnection()
    {
        await using var r = new Rig(); await using var connection = await r.Connect(); var sent = new TaskCompletionSource();
        r.Before = async p => { if (p.Command != 250) return false; await r.Server.SendAsync(0, 250, Json(r.Detectors)); sent.TrySetResult(); return true; };
        using var cancellation = new CancellationTokenSource(); var read = connection.ReadEnvironmentAsync(cancellation.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(4)); Assert.False(read.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read); Assert.False(connection.IsConnected);
    }
}
