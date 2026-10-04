using System.Buffers.Binary;

namespace FSTService.Feedback;

/// <summary>
/// Lossless removal of privacy-sensitive metadata (EXIF/GPS, XMP, IPTC, text chunks)
/// from JPEG and PNG files.
/// </summary>
public static class FeedbackMetadataStripper
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly HashSet<string> PngDroppedChunks = new(StringComparer.Ordinal)
    {
        "eXIf", "tEXt", "zTXt", "iTXt", "tIME",
    };

    /// <summary>
    /// Copies a JPEG without APP1 (EXIF/XMP), APP13 (IPTC) and comment segments.
    /// Returns the EXIF orientation (1-8) found in the source, or 1 when absent.
    /// Returns null when the file is not a well-formed JPEG.
    /// </summary>
    public static int? StripJpeg(string sourcePath, string destinationPath)
    {
        var data = File.ReadAllBytes(sourcePath);
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;

        var orientation = 1;
        using var output = File.Create(destinationPath);
        output.Write(data, 0, 2);
        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
                return null;
            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            // Start of scan: the entropy-coded image data and trailer follow unchanged.
            if (marker == 0xDA)
            {
                output.Write(data, position, data.Length - position);
                return orientation;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2, 2));
            if (length < 2 || position + 2 + length > data.Length)
                return null;
            var segment = data.AsSpan(position, 2 + length);
            var payload = segment[4..];

            var drop = marker switch
            {
                0xE1 => true,
                0xED => true,
                0xFE => true,
                _ => false,
            };
            if (marker == 0xE1 && payload.StartsWith("Exif\0\0"u8))
                orientation = ReadExifOrientation(payload[6..]) ?? orientation;
            if (!drop)
                output.Write(segment);
            position += 2 + length;
        }

        return null;
    }

    /// <summary>Copies a PNG without EXIF/text/time chunks. Returns false when malformed.</summary>
    public static bool StripPng(string sourcePath, string destinationPath)
    {
        var data = File.ReadAllBytes(sourcePath);
        if (data.Length < PngSignature.Length || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            return false;

        using var output = File.Create(destinationPath);
        output.Write(PngSignature);
        var position = PngSignature.Length;
        while (position + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            if (length > int.MaxValue || position + 12L + length > data.Length)
                return false;
            var type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            var total = 12 + (int)length;
            if (!PngDroppedChunks.Contains(type))
                output.Write(data, position, total);
            position += total;
            if (type == "IEND")
                return true;
        }

        return false;
    }

    internal static int? ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
            return null;
        bool little;
        if (tiff.StartsWith("II"u8))
            little = true;
        else if (tiff.StartsWith("MM"u8))
            little = false;
        else
            return null;

        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifdOffset = U32(tiff.Slice(4, 4));
        if (ifdOffset + 2 > tiff.Length)
            return null;
        var count = U16(tiff.Slice((int)ifdOffset, 2));
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifdOffset + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
                return null;
            if (U16(tiff.Slice(entry, 2)) != 0x0112)
                continue;
            var value = U16(tiff.Slice(entry + 8, 2));
            return value is >= 1 and <= 8 ? value : null;
        }

        return null;
    }

    /// <summary>ffmpeg filter that applies an EXIF orientation, or null for the identity.</summary>
    public static string? OrientationFilter(int orientation) => orientation switch
    {
        2 => "hflip",
        3 => "hflip,vflip",
        4 => "vflip",
        5 => "transpose=0",
        6 => "transpose=1",
        7 => "transpose=3",
        8 => "transpose=2",
        _ => null,
    };
}
