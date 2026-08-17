using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FSTService.Api;

internal static class PublicApiJsonContract
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    internal static JavaScriptEncoder Encoder { get; } =
        JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    internal static void Configure(
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Encoder = Encoder;
    }

    internal static Utf8JsonWriter CreateProjectionWriter(
        Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = Encoder,
            });
    }

    internal static bool IsValidUtf8(
        ReadOnlySpan<byte> json)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(json);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
