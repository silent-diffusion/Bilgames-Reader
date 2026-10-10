using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bilgames.Core.Storage;

/// <summary>
/// Enums as names, like <see cref="JsonStringEnumConverter"/>, but a name this version doesn't know
/// (written by a newer version) reads as the enum's default instead of making the whole file unreadable.
/// </summary>
public sealed class LenientEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(t))!;

    private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
                return (T)Enum.ToObject(typeof(T), n);
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var v)
                && (Enum.IsDefined(v) || typeof(T).IsDefined(typeof(FlagsAttribute), false)))
                return v;
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
