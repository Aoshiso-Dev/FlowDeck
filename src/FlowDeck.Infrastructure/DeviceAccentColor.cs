using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowDeck.Infrastructure;

public static class DeviceAccentColor
{
    public const string Default = "#A079EC";

    public static bool Valid(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static string Normalize(string? value) => Valid(value) ? value!.ToUpperInvariant() : Default;
}

// Accent is an optional, local appearance preference. Invalid values must not discard valid window or navigation state.
internal sealed class DeviceAccentColorJsonConverter : JsonConverter<string>
{
    public DeviceAccentColorJsonConverter() { }
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return DeviceAccentColor.Normalize(reader.GetString());
        reader.Skip();
        return DeviceAccentColor.Default;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
