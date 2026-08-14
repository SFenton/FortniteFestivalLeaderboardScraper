using System.Buffers.Binary;
using System.Text;

namespace FSTService.Scraping;

internal static class MidiTrackInspector
{
    public static string[] GetNonEmptyInstruments(ReadOnlySpan<byte> midiData)
    {
        var trackNames = GetNonEmptyTrackNames(midiData);
        return PathGenerationInstruments.Definitions
            .Where(definition => trackNames.Contains(definition.MidiTrackName))
            .Select(definition => definition.Instrument)
            .ToArray();
    }

    private static HashSet<string> GetNonEmptyTrackNames(
        ReadOnlySpan<byte> midiData)
    {
        if (midiData.Length < 14 ||
            !midiData[..4].SequenceEqual("MThd"u8))
        {
            throw new InvalidDataException(
                "Invalid MIDI file: missing MThd header.");
        }

        var headerLength = ReadChunkLength(midiData, 4);
        if (headerLength < 6 || 8L + headerLength > midiData.Length)
        {
            throw new InvalidDataException(
                "Invalid MIDI file: truncated MThd chunk.");
        }

        var trackCount = BinaryPrimitives.ReadUInt16BigEndian(
            midiData.Slice(10, 2));
        var position = checked(8 + headerLength);
        var tracksRead = 0;
        var nonEmptyTrackNames = new HashSet<string>(
            StringComparer.Ordinal);

        while (tracksRead < trackCount)
        {
            if (position > midiData.Length - 8)
            {
                throw new InvalidDataException(
                    "Invalid MIDI file: missing MTrk chunk.");
            }

            var chunkLength = ReadChunkLength(midiData, position + 4);
            var chunkStart = checked(position + 8);
            var chunkEndLong = (long)chunkStart + chunkLength;
            if (chunkEndLong > midiData.Length)
            {
                throw new InvalidDataException(
                    "Invalid MIDI file: truncated chunk.");
            }

            var chunkEnd = (int)chunkEndLong;
            if (midiData.Slice(position, 4).SequenceEqual("MTrk"u8))
            {
                var track = InspectTrack(
                    midiData.Slice(chunkStart, chunkEnd - chunkStart));
                if (track.HasNotes && track.Name is not null)
                    nonEmptyTrackNames.Add(track.Name);
                tracksRead++;
            }

            position = chunkEnd;
        }

        return nonEmptyTrackNames;
    }

    private static MidiTrackInspection InspectTrack(
        ReadOnlySpan<byte> trackData)
    {
        var position = 0;
        byte runningStatus = 0;
        string? trackName = null;
        var hasNotes = false;

        while (position < trackData.Length)
        {
            ReadVariableLengthQuantity(trackData, ref position);
            EnsureAvailable(trackData, position, 1);

            var next = trackData[position];
            byte status;
            var usesRunningStatus = next < 0x80;
            if (usesRunningStatus)
            {
                if (runningStatus == 0)
                {
                    throw new InvalidDataException(
                        "Invalid MIDI track: data byte has no running status.");
                }

                status = runningStatus;
            }
            else
            {
                status = next;
                position++;
                if (status < 0xF0)
                    runningStatus = status;
            }

            if (status == 0xFF)
            {
                runningStatus = 0;
                EnsureAvailable(trackData, position, 1);
                var metaType = trackData[position++];
                var length = ReadVariableLengthQuantity(
                    trackData,
                    ref position);
                EnsureAvailable(trackData, position, length);
                if (metaType == 0x03 && trackName is null)
                {
                    trackName = Encoding.ASCII.GetString(
                        trackData.Slice(position, length));
                }

                position += length;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                runningStatus = 0;
                var length = ReadVariableLengthQuantity(
                    trackData,
                    ref position);
                EnsureAvailable(trackData, position, length);
                position += length;
                continue;
            }

            if (status >= 0xF8)
                continue;

            if (status >= 0xF0)
            {
                runningStatus = 0;
                var systemDataLength = status switch
                {
                    0xF1 => 1,
                    0xF2 => 2,
                    0xF3 => 1,
                    0xF6 => 0,
                    _ => throw new InvalidDataException(
                        $"Invalid MIDI track: unsupported status 0x{status:X2}."),
                };
                EnsureDataBytes(
                    trackData,
                    position,
                    systemDataLength);
                position += systemDataLength;
                continue;
            }

            var channelType = (byte)(status & 0xF0);
            var dataLength = channelType is 0xC0 or 0xD0 ? 1 : 2;
            if (usesRunningStatus)
            {
                position++;
            }
            else
            {
                EnsureDataBytes(trackData, position, 1);
                position++;
            }

            byte secondData = 0;
            if (dataLength == 2)
            {
                EnsureDataBytes(trackData, position, 1);
                secondData = trackData[position++];
            }

            if (channelType == 0x90 && secondData > 0)
                hasNotes = true;
        }

        return new MidiTrackInspection(trackName, hasNotes);
    }

    private static int ReadChunkLength(
        ReadOnlySpan<byte> data,
        int position)
    {
        EnsureAvailable(data, position, 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(
            data.Slice(position, 4));
        if (length > int.MaxValue)
        {
            throw new InvalidDataException(
                "Invalid MIDI file: chunk is too large.");
        }

        return (int)length;
    }

    private static int ReadVariableLengthQuantity(
        ReadOnlySpan<byte> data,
        ref int position)
    {
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            EnsureAvailable(data, position, 1);
            var current = data[position++];
            value = (value << 7) | (current & 0x7F);
            if ((current & 0x80) == 0)
                return value;
        }

        throw new InvalidDataException(
            "Invalid MIDI track: variable-length quantity exceeds four bytes.");
    }

    private static void EnsureDataBytes(
        ReadOnlySpan<byte> data,
        int position,
        int count)
    {
        EnsureAvailable(data, position, count);
        for (var index = 0; index < count; index++)
        {
            if (data[position + index] >= 0x80)
            {
                throw new InvalidDataException(
                    "Invalid MIDI track: channel data byte has its status bit set.");
            }
        }
    }

    private static void EnsureAvailable(
        ReadOnlySpan<byte> data,
        int position,
        int count)
    {
        if (position < 0 ||
            count < 0 ||
            position > data.Length - count)
        {
            throw new InvalidDataException(
                "Invalid MIDI file: truncated event data.");
        }
    }

    private sealed record MidiTrackInspection(
        string? Name,
        bool HasNotes);
}
