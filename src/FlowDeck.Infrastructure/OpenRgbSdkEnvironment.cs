using System.Text.Json;

namespace FlowDeck.Infrastructure;

internal sealed record OpenRgbSdkPlugin(string Name, string Description, string Version, uint Index, uint ProtocolVersion);
internal sealed record OpenRgbEnvironmentStamp(Guid Connection, Guid Receipt, long InventoryRevision, string Digest);
internal sealed record OpenRgbSdkStartupSettings(JsonElement Client, JsonElement Profiles, JsonElement QmkOpenRgb, JsonElement QmkVial, JsonElement Asrock);
internal sealed record OpenRgbSdkEnvironment(OpenRgbEnvironmentStamp Stamp, uint ServerFlags,
    JsonElement Detectors, JsonElement DetectorSchema, JsonElement Drivers, JsonElement Server, OpenRgbSdkPlugin[]? Plugins,
    bool OwnedHeadless = false, OpenRgbSdkStartupSettings? Startup = null);

internal static partial class OpenRgbSdkProtocol
{
    // Only fixed non-secret keys are requested by the connection. Raw replies are never logged.
    public static JsonElement SettingsObject(byte[] data, bool allowUnavailable = false, bool detectorSettings = false)
    {
        var text = NullText(data, MaximumPacket);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
            // The pinned server returns JSON null when a settings category is absent. Preserve that
            // fact for optional driver settings instead of inventing defaults or an empty object.
            if (allowUnavailable && document.RootElement.ValueKind == JsonValueKind.Null) return document.RootElement.Clone();
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("OpenRGBの設定応答がオブジェクトではありません");
            var nodes = 0;
            void Check(JsonElement element, int depth = 0, bool detectorNames = false)
            {
                if (++nodes > 32768) throw new IOException("OpenRGBの設定項目数が上限を超えました");
                if (element.ValueKind == JsonValueKind.Object)
                {
                    // Upstream detector identifiers are case-sensitive: the pinned release contains
                    // both RTX 5080/5090 "GAMING OC" and "Gaming OC" registrations. Only that exact
                    // dictionary gets this rule; case aliases in control settings remain invalid.
                    var keys = new HashSet<string>(detectorNames ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Length is < 1 or > 4096 || !keys.Add(property.Name)) throw new IOException("OpenRGBの設定キーが重複または不正です");
                        Check(property.Value, depth + 1, detectorSettings && depth == 0 && property.Name == "detectors");
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Check(value, depth + 1);
            }
            Check(document.RootElement); return document.RootElement.Clone();
        }
        catch (JsonException e) { throw new IOException("OpenRGBの設定JSONが不正です", e); }
    }
    public static OpenRgbSdkPlugin[] Plugins(byte[] data)
    {
        try
        {
            using var reader = new Reader(data);
            if (reader.U32() != data.Length) throw new IOException("OpenRGBのプラグイン一覧サイズが不正です");
            var result = new OpenRgbSdkPlugin[reader.Count(64)];
            for (var i = 0; i < result.Length; i++)
                result[i] = new(reader.Text(), reader.Text(), reader.Text(), reader.U32(), reader.U32());
            reader.Finish();
            if (result.Any(p => string.IsNullOrWhiteSpace(p.Name)) || result.DistinctBy(p => p.Index).Count() != result.Length)
                throw new IOException("OpenRGBのプラグイン識別が不正です");
            return result;
        }
        catch (EndOfStreamException e) { throw new IOException("OpenRGBのプラグイン応答が不完全です", e); }
    }
}

internal sealed partial class OpenRgbSdkConnection
{
    private OpenRgbSdkEnvironment? environment;
    private long environmentTimestamp;
    private bool environmentAdvertised;

    // Evidence for a compiled coexistence policy, NOT proof of exclusive device ownership or of
    // disabled bus probing. SDK settings are observations; another client can still change them.
    public Task<OpenRgbSdkEnvironment> ReadEnvironmentAsync(CancellationToken ct = default, Action? authorize = null) => ExecuteAsync(async token =>
    {
        long initialRevision;
        lock (sync)
        {
            environment = null;
            if (detecting) throw new IOException("OpenRGBが機器を検出中です");
            if ((serverFlags & 16) == 0) throw new IOException("OpenRGBが検出設定の取得に対応していません");
            initialRevision = revision;
        }
        if (!environmentAdvertised)
        {
            // RGB, plugin listing and settings reads. Never request privileged local-client status.
            var data = await RequestAsync(0, 52, OpenRgbSdkProtocol.UInt(ownedHeadless ? 1u | 16u : 1u | 8u | 16u), 53, token, authorize).ConfigureAwait(false);
            var flags = Parse(() => OpenRgbSdkProtocol.UInt(data));
            if ((flags & (1 | 16)) != (1 | 16) || (flags & (1 << 16)) != 0)
                throw new IOException("OpenRGBの設定取得機能または権限応答が一致しません");
            lock (sync) { serverFlags = flags; environmentAdvertised = true; }
        }
        async Task<(JsonElement Detectors, JsonElement Schema, JsonElement Drivers, JsonElement Server, OpenRgbSdkPlugin[]? Plugins, OpenRgbSdkStartupSettings? Startup)> Read()
        {
            void Guard() { if (initialRevision != revision || detecting) throw Stale(); authorize?.Invoke(); }
            async Task<JsonElement> Settings(string key, bool schema = false, bool allowUnavailable = false)
            {
                var command = schema ? 251u : 250u;
                var bytes = await RequestAsync(0, command, System.Text.Encoding.UTF8.GetBytes(key + '\0'), command, token, Guard).ConfigureAwait(false);
                return Parse(() => OpenRgbSdkProtocol.SettingsObject(bytes, allowUnavailable, detectorSettings: key == "Detectors" && !schema));
            }
            var detectors = await Settings("Detectors").ConfigureAwait(false);
            var schema = await Settings("Detectors", schema: true).ConfigureAwait(false);
            var drivers = await Settings("Drivers", allowUnavailable: true).ConfigureAwait(false);
            var server = await Settings("Server").ConfigureAwait(false);
            OpenRgbSdkPlugin[]? plugins = null;
            // The pinned headless server advertises the plugin API but has no PluginManager and
            // sends no plugin-list payload. Do not turn that into a timeout or claim an empty list.
            if (!ownedHeadless && (serverFlags & 8) != 0)
            {
                var bytes = await RequestAsync(0, 200, [], 200, token, Guard).ConfigureAwait(false);
                plugins = Parse(() => OpenRgbSdkProtocol.Plugins(bytes));
            }
            OpenRgbSdkStartupSettings? startup = null;
            if (ownedHeadless)
                startup = new(await Settings("Client").ConfigureAwait(false), await Settings("ProfileManager").ConfigureAwait(false),
                    await Settings("QMKOpenRGBDevices").ConfigureAwait(false), await Settings("QMKVialRGBDevices").ConfigureAwait(false),
                    await Settings("ASRock Polychrome USB").ConfigureAwait(false));
            return (detectors, schema, drivers, server, plugins, startup);
        }
        string Digest((JsonElement Detectors, JsonElement Schema, JsonElement Drivers, JsonElement Server, OpenRgbSdkPlugin[]? Plugins, OpenRgbSdkStartupSettings? Startup) value) =>
            SettingsTransfer.Hash(new { ServerFlags = serverFlags, OwnedHeadless = ownedHeadless, value.Detectors, value.Schema, value.Drivers, value.Server,
                Plugins = value.Plugins?.OrderBy(p => p.Index).ToArray(), value.Startup });
        var first = await Read().ConfigureAwait(false);
        var second = await Read().ConfigureAwait(false);
        var digest = Digest(second);
        if (Digest(first) != digest) throw new OpenRgbSdkObservationChangedException();
        lock (sync)
        {
            CheckConnected();
            if (initialRevision != revision || detecting) throw Stale();
            environmentTimestamp = clock.GetTimestamp();
            environment = new(new(connection, Guid.NewGuid(), revision, digest), serverFlags, second.Detectors, second.Schema, second.Drivers, second.Server, second.Plugins, ownedHeadless, second.Startup);
            return environment with { Plugins = environment.Plugins?.ToArray() };
        }
    }, ct);

    // Used from a driver's final write authorization. The receipt is tied to this connection and
    // inventory, expires quickly, and is invalidated when a refresh starts, even if it then fails.
    public void CheckEnvironment(OpenRgbEnvironmentStamp stamp)
    {
        lock (sync)
        {
            CheckConnected();
            var elapsed = clock.GetElapsedTime(environmentTimestamp);
            if (environment?.Stamp != stamp || stamp.Connection != connection || stamp.InventoryRevision != revision || detecting ||
                elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(2)) throw new IOException("OpenRGBの接続条件を再取得してください");
        }
    }
}
