using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSTService.Scraping.Replay;

public static class TierZeroCanonicalJson
{
    internal static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var element = JsonSerializer.SerializeToElement(
            value,
            SerializerOptions);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false,
                   }))
        {
            WriteCanonical(writer, element);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string SerializeToString<T>(T value) =>
        Encoding.UTF8.GetString(Serialize(value));

    internal static T Deserialize<T>(ReadOnlySpan<byte> json)
    {
        var value = JsonSerializer.Deserialize<T>(
            json,
            SerializerOptions);
        return value
            ?? throw new JsonException(
                $"Canonical JSON did not contain {typeof(T).Name}.");
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string ComputeManifestRootHash(
        TierZeroEvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Sha256Hex(Serialize(manifest with { PackageRootHash = null }));
    }

    internal static byte[] SerializeSealedManifest(
        TierZeroEvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var rootHash = ComputeManifestRootHash(manifest);
        return Serialize(manifest with { PackageRootHash = rootHash });
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsOciSha256(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsSha256(value[7..]);

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(
                                 static property => property.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(
                    element.GetRawText(),
                    skipInputValidation: false);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException(
                    $"Unsupported JSON value kind {element.ValueKind}.");
        }
    }
}
