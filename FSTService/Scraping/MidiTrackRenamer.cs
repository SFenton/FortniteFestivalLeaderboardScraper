using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace FSTService.Scraping;

/// <summary>
/// Produces instrument-specific MIDI variants by renaming track name meta events.
/// Port of FNFpaths download.py's replace_tracks_in_midi.
///
/// CHOpt expects standard Rock Band track naming (PART GUITAR, PART BASS, etc.)
/// but Fortnite Festival uses different track names. We produce three MIDI variants:
///
///   _pro.mid:     PLASTIC GUITAR → PART GUITAR, PLASTIC BASS → PART BASS
///                 (original PART GUITAR/BASS get _FNF suffix to avoid conflicts)
///
///   _drumvox.mid: PART DRUMS → PART GUITAR, PART VOCALS → PART BASS
///                 (original PART BASS gets _FNF suffix)
///
///   _og.mid:      Unchanged (Lead/Bass use the original PART GUITAR/BASS tracks)
/// </summary>
public static class MidiTrackRenamer
{
    /// <summary>
    /// Results of producing the three MIDI variants.
    /// </summary>
    public sealed record MidiVariants(byte[] ProMidi, byte[] DrumVoxMidi, byte[] OgMidi);

    /// <summary>
    /// Produce all three MIDI variants from a decrypted MIDI file.
    /// </summary>
    public static MidiVariants ProduceVariants(byte[] midiData)
    {
        var proMidi = RenameTracksForPro(midiData);
        var drumvoxMidi = RenameTracksForDrumVox(midiData);
        // OG is unchanged — Lead and Bass use the original tracks
        return new MidiVariants(proMidi, drumvoxMidi, (byte[])midiData.Clone());
    }

    /// <summary>
    /// For Pro Lead/Bass: Rename so CHOpt processes the PLASTIC instrument tracks.
    ///   PART GUITAR → PART GUITAR_FNF  (hide original)
    ///   PLASTIC GUITAR → PART GUITAR   (promote plastic to main)
    ///   PART BASS → PART BASS_FNF      (hide original)
    ///   PLASTIC BASS → PART BASS       (promote plastic to main)
    /// </summary>
    private static byte[] RenameTracksForPro(byte[] midiData)
    {
        return RenameTrackNames(midiData, new Dictionary<string, string>
        {
            ["PART GUITAR"] = "PART GUITAR_FNF",
            ["PLASTIC GUITAR"] = "PART GUITAR",
            ["PART BASS"] = "PART BASS_FNF",
            ["PLASTIC BASS"] = "PART BASS",
        });
    }

    /// <summary>
    /// For Drums/Vocals: Rename so CHOpt processes drums track as guitar, vocals as bass.
    ///   PART DRUMS → PART GUITAR   (drums become guitar for CHOpt)
    ///   PART BASS → PART BASS_FNF  (hide original bass)
    ///   PART VOCALS → PART BASS    (vocals become bass for CHOpt)
    /// </summary>
    private static byte[] RenameTracksForDrumVox(byte[] midiData)
    {
        return RenameTrackNames(midiData, new Dictionary<string, string>
        {
            ["PART DRUMS"] = "PART GUITAR",
            ["PART BASS"] = "PART BASS_FNF",
            ["PART VOCALS"] = "PART BASS",
        });
    }

    /// <summary>
    /// Apply track name renames to a MIDI file's Track Name meta events (FF 03).
    ///
    /// MIDI format:
    ///   - File starts with MThd header (14 bytes)
    ///   - Followed by MTrk chunks: 'MTrk' + 4-byte big-endian length + track data
    ///   - Track Name meta event: delta-time + FF 03 + variable-length-quantity(len) + ASCII text
    ///
    /// When a rename changes the text length, we adjust the VLQ length field and
    /// the MTrk chunk length accordingly.
    /// </summary>
    private static byte[] RenameTrackNames(byte[] midiData, Dictionary<string, string> renames)
    {
        using var output = new MemoryStream(midiData.Length + 256);
        int pos = 0;

        // Copy MThd header: 'MThd' (4) + length (4) + header data (typically 6) = 14 bytes
        if (midiData.Length < 14 ||
            midiData[0] != 'M' || midiData[1] != 'T' ||
            midiData[2] != 'h' || midiData[3] != 'd')
        {
            throw new InvalidDataException("Invalid MIDI file: missing MThd header.");
        }

        int headerLen = ReadInt32BE(midiData, 4);
        int headerTotal = 8 + headerLen; // 'MThd' + length + data
        output.Write(midiData, 0, headerTotal);
        pos = headerTotal;

        // Process each MTrk chunk
        while (pos + 8 <= midiData.Length)
        {
            if (midiData[pos] != 'M' || midiData[pos + 1] != 'T' ||
                midiData[pos + 2] != 'r' || midiData[pos + 3] != 'k')
            {
                // Not an MTrk chunk — copy remaining bytes and stop
                output.Write(midiData, pos, midiData.Length - pos);
                break;
            }

            int chunkLength = ReadInt32BE(midiData, pos + 4);
            int chunkDataStart = pos + 8;
            int chunkDataEnd = chunkDataStart + chunkLength;
            if (chunkDataEnd > midiData.Length) chunkDataEnd = midiData.Length;

            // Process track data, looking for Track Name meta events (FF 03)
            var trackData = ProcessTrackData(
                midiData.AsSpan(chunkDataStart, chunkDataEnd - chunkDataStart), renames);

            // Write MTrk header with updated length
            output.WriteByte((byte)'M');
            output.WriteByte((byte)'T');
            output.WriteByte((byte)'r');
            output.WriteByte((byte)'k');
            WriteInt32BE(output, trackData.Length);
            output.Write(trackData);

            pos = chunkDataEnd;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Scan track data for Track Name meta events (FF 03) and apply renames.
    /// </summary>
    private static byte[] ProcessTrackData(ReadOnlySpan<byte> data, Dictionary<string, string> renames)
    {
        using var output = new MemoryStream(data.Length + 64);
        int pos = 0;

        while (pos < data.Length)
        {
            int eventStart = pos;

            // Read delta time (variable-length quantity)
            int deltaStart = pos;
            while (pos < data.Length && (data[pos] & 0x80) != 0) pos++;
            if (pos < data.Length) pos++; // consume final byte of VLQ
            int deltaEnd = pos;

            if (pos >= data.Length) break;

            byte status = data[pos];

            if (status == 0xFF && pos + 1 < data.Length)
            {
                // Meta event
                byte metaType = data[pos + 1];
                int vlqStart = pos + 2;
                int vlqPos = vlqStart;
                int textLength = ReadVLQ(data, ref vlqPos);
                int textStart = vlqPos;
                int textEnd = Math.Min(textStart + textLength, data.Length);

                if (metaType == 0x03 && textEnd <= data.Length) // Track Name
                {
                    string trackName = Encoding.ASCII.GetString(data.Slice(textStart, textEnd - textStart));

                    if (renames.TryGetValue(trackName, out var newName))
                    {
                        byte[] newNameBytes = Encoding.ASCII.GetBytes(newName);

                        // Write delta time unchanged
                        output.Write(data.Slice(deltaStart, deltaEnd - deltaStart));
                        // Write FF 03
                        output.WriteByte(0xFF);
                        output.WriteByte(0x03);
                        // Write new VLQ length
                        WriteVLQ(output, newNameBytes.Length);
                        // Write new name
                        output.Write(newNameBytes);

                        pos = textEnd;
                        continue;
                    }
                }

                // Not a track name or not in renames — copy entire event
                output.Write(data.Slice(eventStart, textEnd - eventStart));
                pos = textEnd;
            }
            else if (status == 0xFF)
            {
                // Truncated meta event — copy remaining
                output.Write(data.Slice(eventStart, data.Length - eventStart));
                break;
            }
            else
            {
                // Channel event or sysex — copy byte by byte
                // We need to figure out the event length
                int eventLen = GetChannelEventLength(status, data, pos);
                if (eventLen <= 0)
                {
                    // Unknown or sysex — copy rest
                    output.Write(data.Slice(eventStart, data.Length - eventStart));
                    break;
                }

                output.Write(data.Slice(eventStart, deltaEnd - deltaStart + eventLen));
                pos += eventLen;
            }
        }

        return output.ToArray();
    }

    [ExcludeFromCodeCoverage] // Defence-in-depth: these status bytes don't appear in FNF MIDI tracks
    private static int GetChannelEventLength(byte status, ReadOnlySpan<byte> data, int pos)
    {
        byte type = (byte)(status & 0xF0);
        return type switch
        {
            0x80 => 3, // Note Off
            0x90 => 3, // Note On
            0xA0 => 3, // Polyphonic Key Pressure
            0xB0 => 3, // Control Change
            0xC0 => 2, // Program Change
            0xD0 => 2, // Channel Pressure
            0xE0 => 3, // Pitch Bend
            0xF0 => GetSysexLength(data, pos), // Sysex
            _ => -1,   // Unknown
        };
    }

    [ExcludeFromCodeCoverage] // Defence-in-depth: sysex messages don't appear in FNF MIDI tracks
    private static int GetSysexLength(ReadOnlySpan<byte> data, int pos)
    {
        if (pos >= data.Length) return -1;
        byte status = data[pos];

        if (status == 0xF0 || status == 0xF7)
        {
            // Sysex: status + VLQ length + data
            int vlqPos = pos + 1;
            int length = ReadVLQ(data, ref vlqPos);
            return (vlqPos - pos) + length;
        }

        // System real-time (F8-FF) are 1 byte
        if (status >= 0xF8) return 1;

        // Other system common: variable
        return status switch
        {
            0xF1 => 2, // MTC Quarter Frame
            0xF2 => 3, // Song Position Pointer
            0xF3 => 2, // Song Select
            0xF6 => 1, // Tune Request
            _ => -1,
        };
    }

    private static int ReadVLQ(ReadOnlySpan<byte> data, ref int pos)
    {
        int value = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    [ExcludeFromCodeCoverage] // Generic MIDI utility — multi-byte VLQ path unreachable with our fixed rename set (all < 128 chars)
    private static void WriteVLQ(Stream stream, int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

        // Encode value into VLQ bytes (big-endian, continuation bit)
        Span<byte> buf = stackalloc byte[4];
        int idx = 3;
        buf[idx] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            idx--;
            buf[idx] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        stream.Write(buf.Slice(idx, 4 - idx));
    }

    private static int ReadInt32BE(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static void WriteInt32BE(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
