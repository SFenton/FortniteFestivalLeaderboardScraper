using System.Text;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class MidiTrackInspectorTests
{
    [Fact]
    public void Detects_supported_non_empty_tracks()
    {
        var midi = BuildMidi(
            Track("PART GUITAR", PositiveNote()),
            Track("PART BASS", PositiveNote()),
            Track("PART DRUMS", PositiveNote()),
            Track("PART VOCALS", PositiveNote()),
            Track("PLASTIC GUITAR", PositiveNote()),
            Track("PLASTIC BASS", PositiveNote()),
            Track("PLASTIC DRUMS", PositiveNote()),
            Track("EVENTS", PositiveNote()));

        var instruments =
            MidiTrackInspector.GetNonEmptyInstruments(midi);

        Assert.Equal(
            [
                "Solo_Guitar",
                "Solo_Bass",
                "Solo_Drums",
                "Solo_Vocals",
                "Solo_PeripheralGuitar",
                "Solo_PeripheralBass",
                "Solo_PeripheralCymbals",
                "Solo_PeripheralDrums",
            ],
            instruments);
    }

    [Fact]
    public void Ignores_empty_tracks_and_zero_velocity_note_on()
    {
        var midi = BuildMidi(
            Track("PART GUITAR"),
            Track(
                "PLASTIC GUITAR",
                [0x00, 0x90, 60, 0]),
            Track("PART BASS", PositiveNote()));

        var instruments =
            MidiTrackInspector.GetNonEmptyInstruments(midi);

        Assert.Equal(["Solo_Bass"], instruments);
    }

    [Fact]
    public void Detects_positive_note_on_using_running_status()
    {
        var midi = BuildMidi(
            Track(
                "PART GUITAR",
                [
                    0x00, 0x90, 60, 0,
                    0x10, 61, 100,
                ]));

        var instruments =
            MidiTrackInspector.GetNonEmptyInstruments(midi);

        Assert.Equal(["Solo_Guitar"], instruments);
    }

    [Fact]
    public void Rejects_truncated_track_data()
    {
        var midi = BuildMidi(
            Track(
                "PART GUITAR",
                [0x00, 0x90, 60]));

        Assert.Throws<InvalidDataException>(() =>
            MidiTrackInspector.GetNonEmptyInstruments(midi));
    }

    private static byte[] PositiveNote()
        => [0x00, 0x90, 60, 100];

    private static MidiTrackSpec Track(
        string name,
        byte[]? events = null)
        => new(name, events ?? []);

    private static byte[] BuildMidi(
        params MidiTrackSpec[] tracks)
    {
        using var stream = new MemoryStream();
        stream.Write("MThd"u8);
        WriteInt32BigEndian(stream, 6);
        WriteInt16BigEndian(stream, 1);
        WriteInt16BigEndian(stream, tracks.Length);
        WriteInt16BigEndian(stream, 480);

        foreach (var track in tracks)
        {
            using var trackData = new MemoryStream();
            var nameBytes = Encoding.ASCII.GetBytes(track.Name);
            trackData.WriteByte(0x00);
            trackData.WriteByte(0xFF);
            trackData.WriteByte(0x03);
            WriteVariableLengthQuantity(
                trackData,
                nameBytes.Length);
            trackData.Write(nameBytes);
            trackData.Write(track.Events);
            trackData.Write([0x00, 0xFF, 0x2F, 0x00]);

            stream.Write("MTrk"u8);
            WriteInt32BigEndian(
                stream,
                checked((int)trackData.Length));
            trackData.Position = 0;
            trackData.CopyTo(stream);
        }

        return stream.ToArray();
    }

    private static void WriteVariableLengthQuantity(
        Stream stream,
        int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        var position = buffer.Length - 1;
        buffer[position] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0)
            buffer[--position] = (byte)((value & 0x7F) | 0x80);
        stream.Write(buffer[position..]);
    }

    private static void WriteInt32BigEndian(
        Stream stream,
        int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteInt16BigEndian(
        Stream stream,
        int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed record MidiTrackSpec(
        string Name,
        byte[] Events);
}
