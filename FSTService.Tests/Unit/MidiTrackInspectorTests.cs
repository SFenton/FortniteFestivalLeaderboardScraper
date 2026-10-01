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

    [Theory]
    [InlineData("PLASTIC DRUMS", 95)]
    [InlineData("PLASTIC DRUMS", 83)]
    [InlineData("PLASTIC DRUMS", 71)]
    [InlineData("PLASTIC DRUMS", 59)]
    [InlineData("PLASTIC DRUM", 95)]
    public void Detects_double_kick_in_each_plastic_drum_difficulty(string track, byte pitch)
    {
        Assert.True(MidiTrackInspector.HasDoubleBassSupport(
            BuildMidi(Track(track, [0x00, 0x90, pitch, 100]))));
    }

    [Theory]
    [InlineData("PLASTIC DRUMS", 96, 100)]
    [InlineData("PLASTIC DRUMS", 95, 0)]
    [InlineData("PART DRUMS", 95, 100)]
    [InlineData("PLASTIC GUITAR", 95, 100)]
    public void Ignores_regular_kick_zero_velocity_and_other_tracks(string track, byte pitch, byte velocity)
    {
        Assert.False(MidiTrackInspector.HasDoubleBassSupport(
            BuildMidi(Track(track, [0x00, 0x90, pitch, velocity]))));
    }

    [Fact]
    public void Detects_double_kick_with_running_status_but_ignores_note_off()
    {
        Assert.True(MidiTrackInspector.HasDoubleBassSupport(BuildMidi(
            Track("PLASTIC DRUMS", [0x00, 0x90, 96, 100, 0x10, 95, 100]))));
        Assert.False(MidiTrackInspector.HasDoubleBassSupport(BuildMidi(
            Track("PLASTIC DRUMS", [0x00, 0x80, 95, 100]))));
    }

    [Theory]
    [InlineData("PLASTIC DRUMS", 120, 100, true)]
    [InlineData("PLASTIC DRUM", 120, 100, true)]
    [InlineData("PLASTIC DRUMS", 120, 0, false)]
    [InlineData("PLASTIC DRUMS", 100, 100, false)]
    [InlineData("PLASTIC DRUMS", 116, 100, false)]
    [InlineData("PLASTIC DRUMS", 126, 100, false)]
    [InlineData("PART DRUMS", 120, 100, false)]
    [InlineData("PLASTIC GUITAR", 120, 100, false)]
    public void Detects_authored_windows_only_from_positive_plastic_drum_markers(
        string track, byte pitch, byte velocity, bool expected)
    {
        Assert.Equal(expected, MidiTrackInspector.HasAuthoredPlasticDrumActivationWindows(
            BuildMidi(Track(track, [0x00, 0x90, pitch, velocity]))));
    }

    [Fact]
    public void Authored_window_inspection_handles_running_status_and_ignores_note_off()
    {
        Assert.True(MidiTrackInspector.HasAuthoredPlasticDrumActivationWindows(BuildMidi(
            Track("PLASTIC DRUMS", [0x00, 0x90, 100, 100, 0x10, 120, 100, 0x10, 120, 0]))));
        Assert.False(MidiTrackInspector.HasAuthoredPlasticDrumActivationWindows(BuildMidi(
            Track("PLASTIC DRUMS", [0x00, 0x80, 120, 100]))));
        Assert.False(MidiTrackInspector.HasAuthoredPlasticDrumActivationWindows(BuildMidi(
            Track("PLASTIC DRUMS"))));
    }

    [Fact]
    public void Authored_window_inspection_cannot_certify_truncated_midi_as_marker_free()
    {
        Assert.Throws<InvalidDataException>(() =>
            MidiTrackInspector.HasAuthoredPlasticDrumActivationWindows(
                BuildMidi(Track("PLASTIC DRUMS", [0x00, 0x90, 120]))));
    }

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
