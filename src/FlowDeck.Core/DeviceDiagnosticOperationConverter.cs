using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowDeck.Core;

// Retired operation names can remain in an older service's diagnostic history.
// This converter applies only to report fields, never to executable requests.
internal sealed class DeviceDiagnosticOperationConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var name = typeof(T) == typeof(DeviceConfigurationOperation) ? "saveM5" : "reportM5Connection";
        var number = typeof(T) == typeof(DeviceConfigurationOperation) ? 12 : 19;
        if (reader.TokenType == JsonTokenType.String && string.Equals(reader.GetString(), name, StringComparison.OrdinalIgnoreCase) ||
            reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value == number) return null;
        return JsonSerializer.Deserialize<T?>(ref reader, options);
    }
    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, options);
}
