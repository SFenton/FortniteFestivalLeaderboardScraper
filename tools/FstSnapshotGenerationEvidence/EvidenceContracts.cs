using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FstSnapshotGenerationEvidence;

public static class SnapshotGenerationCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options =
            new JsonSerializerOptions(
                JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull,
                UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow,
            };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase));
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
                                 static property =>
                                     property.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(
                        property.Name);
                    WriteCanonical(
                        writer,
                        property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in
                         element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(
                    element.GetString());
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

public static class SnapshotGenerationQuarantineEvidenceContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-quarantine.v1";
    public const string QuarantineSchema =
        "fst_snapshot_quarantine";
    public const string SnapshotDdlLockName =
        "fst.snapshot-generation-partition-ddl";
    public const long RegistrationAdvisoryLockKey =
        5067481511116518500L;
    public const long ServiceMaintenanceAdvisoryLockKey =
        2026050901L;
    public const long PublicationAdvisoryLockKey =
        5067481511116519500L;
    public const long PlannerAdvisoryLockKey =
        2026082301L;
    public const long ExecutorAdvisoryLockKey =
        2026083001L;
}
