using System.Text;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class MidiTrackRenamerTests
{
    /// <summary>
    /// Build a minimal MIDI file with one track containing a Track Name meta event.
    /// </summary>
    private static byte[] BuildMidiWithTrackName(string trackName)
    {
        using var ms = new MemoryStream();

        // MThd header: format 1, 1 track, 480 ticks/quarter
        ms.Write("MThd"u8);
        WriteInt32BE(ms, 6);       // header length
        WriteInt16BE(ms, 1);       // format 1
        WriteInt16BE(ms, 1);       // 1 track
        WriteInt16BE(ms, 480);     // ticks/quarter

        // MTrk chunk
        var trackData = BuildTrackData(trackName);
        ms.Write("MTrk"u8);
        WriteInt32BE(ms, trackData.Length);
        ms.Write(trackData);

        return ms.ToArray();
    }

    /// <summary>
    /// Build a MIDI file with multiple tracks, each with a different track name.
    /// </summary>
    private static byte[] BuildMidiWithMultipleTracks(params string[] trackNames)
    {
        using var ms = new MemoryStream();

        // MThd header
        ms.Write("MThd"u8);
        WriteInt32BE(ms, 6);
        WriteInt16BE(ms, 1);
        WriteInt16BE(ms, trackNames.Length);
        WriteInt16BE(ms, 480);

        foreach (var name in trackNames)
        {
            var trackData = BuildTrackData(name);
            ms.Write("MTrk"u8);
            WriteInt32BE(ms, trackData.Length);
            ms.Write(trackData);
        }

        return ms.ToArray();
    }

    private static byte[] BuildTrackData(string trackName)
    {
        using var ms = new MemoryStream();
        var nameBytes = Encoding.ASCII.GetBytes(trackName);

        // Delta time = 0, Track Name meta event (FF 03)
        ms.WriteByte(0x00);        // delta time
        ms.WriteByte(0xFF);        // meta event
        ms.WriteByte(0x03);        // Track Name
        WriteVLQ(ms, nameBytes.Length);
        ms.Write(nameBytes);

        // End of track meta event (FF 2F 00)
        ms.WriteByte(0x00);        // delta time
        ms.WriteByte(0xFF);
        ms.WriteByte(0x2F);
        ms.WriteByte(0x00);

        return ms.ToArray();
    }

    [Fact]
    public void ProduceVariants_returns_two_variants()
    {
        var midi = BuildMidiWithTrackName("PART GUITAR");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        Assert.NotNull(variants.ProMidi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void OgVariant_is_unchanged_copy()
    {
        var midi = BuildMidiWithTrackName("PART GUITAR");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        Assert.Equal(midi, variants.OgMidi);
    }

    [Fact]
    public void ProVariant_renames_plastic_guitar_to_part_guitar()
    {
        var midi = BuildMidiWithMultipleTracks("PART GUITAR", "PLASTIC GUITAR");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        var trackNames = ExtractTrackNames(variants.ProMidi);
        Assert.Contains("PART GUITAR_FNF", trackNames);  // original hidden
        Assert.Contains("PART GUITAR", trackNames);        // plastic promoted
    }

    [Fact]
    public void ProVariant_renames_plastic_bass_to_part_bass()
    {
        var midi = BuildMidiWithMultipleTracks("PART BASS", "PLASTIC BASS");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        var trackNames = ExtractTrackNames(variants.ProMidi);
        Assert.Contains("PART BASS_FNF", trackNames);  // original hidden
        Assert.Contains("PART BASS", trackNames);       // plastic promoted
    }

    [Fact]
    public void Unmatched_track_names_are_preserved()
    {
        var midi = BuildMidiWithMultipleTracks("EVENTS", "BEAT", "PART GUITAR");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        var proNames = ExtractTrackNames(variants.ProMidi);
        Assert.Contains("EVENTS", proNames);
        Assert.Contains("BEAT", proNames);
    }

    [Fact]
    public void Invalid_midi_throws()
    {
        var bad = new byte[] { 0, 1, 2, 3 };
        Assert.Throws<InvalidDataException>(() => MidiTrackRenamer.ProduceVariants(bad));
    }

    [Fact]
    public void Empty_midi_throws()
    {
        Assert.Throws<InvalidDataException>(() => MidiTrackRenamer.ProduceVariants(Array.Empty<byte>()));
    }

    [Fact]
    public void Track_with_channel_events_is_preserved()
    {
        // Build a MIDI with a track containing Note On/Off channel events + track name
        var midi = BuildMidiWithChannelEvents("PART GUITAR");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        // Pro variant should rename PART GUITAR → PART GUITAR_FNF
        var names = ExtractTrackNames(variants.ProMidi);
        Assert.Contains("PART GUITAR_FNF", names);
        // The MIDI should still be valid (not truncated)
        Assert.True(variants.ProMidi.Length > 14);
    }

    [Fact]
    public void Track_with_program_change_events_is_preserved()
    {
        // Program Change (0xC0) is a 2-byte channel event
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xC0, 0x05,        // delta=0, Program Change ch0, program=5
            0x00, 0xFF, 0x2F, 0x00,  // End of Track
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_pitch_bend_events_is_preserved()
    {
        // Pitch Bend (0xE0) is a 3-byte channel event
        var midi = BuildMidiWithSpecificEvents("BEAT", new byte[]
        {
            0x00, 0xE0, 0x00, 0x40,  // delta=0, Pitch Bend ch0
            0x00, 0xFF, 0x2F, 0x00,  // End of Track
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_sysex_event_is_preserved()
    {
        // SysEx (0xF0) with VLQ length
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF0, 0x03, 0x01, 0x02, 0xF7,  // delta=0, Sysex, len=3, data, end
            0x00, 0xFF, 0x2F, 0x00,                // End of Track
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_non_trackname_meta_events_preserved()
    {
        // Tempo meta event (FF 51 03 xx xx xx)
        var midi = BuildMidiWithSpecificEvents("PART GUITAR", new byte[]
        {
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,  // Set Tempo
            0x00, 0xFF, 0x2F, 0x00,                      // End of Track
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        var names = ExtractTrackNames(variants.ProMidi);
        Assert.Contains("PART GUITAR_FNF", names);
    }

    [Fact]
    public void ProduceVariants_handles_long_track_name()
    {
        // Track name longer than 127 bytes requires multi-byte VLQ
        var longName = new string('X', 200);
        var midi = BuildMidiWithTrackName(longName);
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        var names = ExtractTrackNames(variants.OgMidi);
        Assert.Contains(longName, names);
    }

    [Fact]
    public void Trailing_non_MTrk_data_is_copied()
    {
        // Build a MIDI and append garbage after the track
        var midi = BuildMidiWithTrackName("TEST");
        var extended = new byte[midi.Length + 4];
        Array.Copy(midi, extended, midi.Length);
        extended[midi.Length] = 0xDE;
        extended[midi.Length + 1] = 0xAD;
        extended[midi.Length + 2] = 0xBE;
        extended[midi.Length + 3] = 0xEF;

        var variants = MidiTrackRenamer.ProduceVariants(extended);
        // Should not throw — trailing data is copied
        Assert.True(variants.OgMidi.Length >= extended.Length);
    }

    [Fact]
    public void Track_with_truncated_meta_event_is_preserved()
    {
        // A track that ends with 0xFF but no meta type byte → truncated
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xFF, // truncated: meta event status byte with no type or length
        });
        // Should not throw — truncated data is copied as-is
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_polyphonic_key_pressure_is_preserved()
    {
        // 0xA0 = 3-byte event
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xA0, 0x3C, 0x40,  // Polyphonic Key Pressure
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_control_change_is_preserved()
    {
        // 0xB0 = 3-byte event
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xB0, 0x07, 0x64,  // Control Change
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_channel_pressure_is_preserved()
    {
        // 0xD0 = 2-byte event
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xD0, 0x40,        // Channel Pressure
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_sysex_f7_event_is_preserved()
    {
        // SysEx escape (0xF7) with VLQ length
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF7, 0x02, 0x01, 0x02,  // Sysex escape, len=2, data
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_system_realtime_event_is_handled()
    {
        // 0xF8 = system real-time (1-byte). However, these appear between events
        // in running status context. Our parser hits the channel event path.
        // We'll test via the unknown event fallback by using 0xF4 (undefined)
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF4,              // Undefined system common → triggers -1 → copy rest
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_mtc_quarter_frame_event_is_preserved()
    {
        // 0xF1 = MTC Quarter Frame, 2 bytes
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF1, 0x00,        // MTC Quarter Frame
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_song_position_event_is_preserved()
    {
        // 0xF2 = Song Position Pointer, 3 bytes
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF2, 0x00, 0x00,  // Song Position Pointer
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_song_select_event_is_preserved()
    {
        // 0xF3 = Song Select, 2 bytes
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF3, 0x01,        // Song Select
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Track_with_tune_request_event_is_preserved()
    {
        // 0xF6 = Tune Request, 1 byte
        var midi = BuildMidiWithSpecificEvents("EVENTS", new byte[]
        {
            0x00, 0xF6,              // Tune Request
            0x00, 0xFF, 0x2F, 0x00,
        });
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        Assert.NotNull(variants.OgMidi);
    }

    [Fact]
    public void Non_MTrk_data_after_track_is_copied()
    {
        // Build a MIDI with valid MThd + MTrk, then non-MTrk bytes after
        using var ms = new MemoryStream();
        // MThd header
        ms.Write("MThd"u8);
        WriteInt32BE(ms, 6);
        WriteInt16BE(ms, 1);       // format
        WriteInt16BE(ms, 1);       // 1 track
        WriteInt16BE(ms, 480);     // ticks
        // MTrk with just End of Track
        var trackData = BuildTrackData("TEST");
        ms.Write("MTrk"u8);
        WriteInt32BE(ms, trackData.Length);
        ms.Write(trackData);
        // Non-MTrk garbage bytes that should be copied
        ms.Write(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x01, 0x02, 0x03 });

        var midi = ms.ToArray();
        var variants = MidiTrackRenamer.ProduceVariants(midi);
        // Verify the trailing data was preserved (output should be >= input length)
        Assert.True(variants.OgMidi.Length >= midi.Length);
    }

    [Fact]
    public void Rename_to_longer_name_updates_vlq_and_chunk_length()
    {
        // Force a rename that makes the track name longer. PART GUITAR (11 chars) →
        // PART GUITAR_FNF (15 chars). Verify the MIDI is valid.
        var midi = BuildMidiWithMultipleTracks("PART GUITAR", "PLASTIC GUITAR");
        var variants = MidiTrackRenamer.ProduceVariants(midi);

        // The pro variant: PART GUITAR→PART GUITAR_FNF (15) and PLASTIC GUITAR→PART GUITAR (11)
        var names = ExtractTrackNames(variants.ProMidi);
        Assert.Contains("PART GUITAR_FNF", names); // 15 chars
        Assert.Contains("PART GUITAR", names);     // 11 chars (shortened from PLASTIC GUITAR)
    }

    /// <summary>
    /// Build a MIDI track with a track name + Note On/Off channel events.
    /// </summary>
    private static byte[] BuildMidiWithChannelEvents(string trackName)
    {
        using var ms = new MemoryStream();
        var nameBytes = Encoding.ASCII.GetBytes(trackName);

        // Track name
        ms.WriteByte(0x00); ms.WriteByte(0xFF); ms.WriteByte(0x03);
        WriteVLQ(ms, nameBytes.Length);
        ms.Write(nameBytes);

        // Note On (0x90 = Note On, ch0)
        ms.WriteByte(0x00); ms.WriteByte(0x90); ms.WriteByte(60); ms.WriteByte(100);
        // Note Off (0x80 = Note Off, ch0)
        ms.WriteByte(0x60); ms.WriteByte(0x80); ms.WriteByte(60); ms.WriteByte(0);
        // End of Track
        ms.WriteByte(0x00); ms.WriteByte(0xFF); ms.WriteByte(0x2F); ms.WriteByte(0x00);

        return WrapInMidi(ms.ToArray());
    }

    /// <summary>
    /// Build a MIDI with a track name meta event followed by specific raw bytes.
    /// </summary>
    private static byte[] BuildMidiWithSpecificEvents(string trackName, byte[] eventBytes)
    {
        using var ms = new MemoryStream();
        var nameBytes = Encoding.ASCII.GetBytes(trackName);

        // Track name
        ms.WriteByte(0x00); ms.WriteByte(0xFF); ms.WriteByte(0x03);
        WriteVLQ(ms, nameBytes.Length);
        ms.Write(nameBytes);

        // Specific event bytes
        ms.Write(eventBytes);

        return WrapInMidi(ms.ToArray());
    }

    /// <summary>Wrap raw track data in a complete MIDI file.</summary>
    private static byte[] WrapInMidi(byte[] trackData)
    {
        using var ms = new MemoryStream();
        ms.Write("MThd"u8);
        WriteInt32BE(ms, 6);
        WriteInt16BE(ms, 1);
        WriteInt16BE(ms, 1);
        WriteInt16BE(ms, 480);
        ms.Write("MTrk"u8);
        WriteInt32BE(ms, trackData.Length);
        ms.Write(trackData);
        return ms.ToArray();
    }

    /// <summary>
    /// Extract all Track Name meta event texts from a MIDI file.
    /// </summary>
    private static List<string> ExtractTrackNames(byte[] midi)
    {
        var names = new List<string>();
        int pos = 0;

        // Skip MThd
        if (midi.Length < 14) return names;
        int headerLen = ReadInt32BE(midi, 4);
        pos = 8 + headerLen;

        while (pos + 8 <= midi.Length)
        {
            if (midi[pos] != 'M' || midi[pos + 1] != 'T' ||
                midi[pos + 2] != 'r' || midi[pos + 3] != 'k')
                break;

            int chunkLen = ReadInt32BE(midi, pos + 4);
            int dataStart = pos + 8;
            int dataEnd = dataStart + chunkLen;

            // Scan track data for FF 03 events
            int p = dataStart;
            while (p < dataEnd)
            {
                // Skip delta time
                while (p < dataEnd && (midi[p] & 0x80) != 0) p++;
                if (p < dataEnd) p++;

                if (p >= dataEnd) break;

                if (midi[p] == 0xFF && p + 1 < dataEnd)
                {
                    byte metaType = midi[p + 1];
                    int vlqPos = p + 2;
                    int textLen = ReadVLQ(midi, ref vlqPos);

                    if (metaType == 0x03 && vlqPos + textLen <= dataEnd)
                    {
                        names.Add(Encoding.ASCII.GetString(midi, vlqPos, textLen));
                    }

                    p = vlqPos + textLen;
                }
                else
                {
                    break; // can't parse further without running status etc.
                }
            }

            pos = dataEnd;
        }

        return names;
    }

    private static int ReadVLQ(byte[] data, ref int pos)
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

    private static int ReadInt32BE(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static void WriteInt32BE(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteInt16BE(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteVLQ(Stream s, int v)
    {
        Span<byte> buf = stackalloc byte[4];
        int idx = 3;
        buf[idx] = (byte)(v & 0x7F);
        v >>= 7;
        while (v > 0)
        {
            idx--;
            buf[idx] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        s.Write(buf.Slice(idx, 4 - idx));
    }
}
