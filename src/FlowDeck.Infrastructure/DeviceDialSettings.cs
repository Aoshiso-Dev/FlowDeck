using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceDialSettings(string Port = "COM6", string? DeviceId = null);
/// <summary>Per-user opt-in connection settings; no service configuration migration or autoconnect.</summary>
public sealed class DeviceDialSettingsStore(string path)
{
    public string? Warning { get; private set; }
    public static bool ValidPort(string? port) => port is { Length: >= 4 and <= 8 } && port.StartsWith("COM", StringComparison.Ordinal) &&
        port[3] != '0' && port[3..].All(char.IsAsciiDigit);
    public DeviceDialSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 4096) throw new IOException("ダイヤル設定が大きすぎます");
            var value = JsonSerializer.Deserialize<DeviceDialSettings>(File.ReadAllText(path), Contract.Json);
            if (value is null || !ValidPort(value.Port) || value.DeviceId is not null && !DialProtocolSession.IdentityText(value.DeviceId))
                throw new IOException("ダイヤル設定が不正です");
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "ダイヤル設定を読み込めません: " + e.Message; return new(); }
    }
    public void Save(DeviceDialSettings value)
    {
        if (!ValidPort(value.Port) || value.DeviceId is not null && !DialProtocolSession.IdentityText(value.DeviceId)) throw new ArgumentException("ダイヤル設定が不正です");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Contract.Json));
        File.Move(temporary, path, true); Warning = null;
    }
}
