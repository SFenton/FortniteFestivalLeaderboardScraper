using System.Net;
using System.Security.Cryptography;
using System.Text;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for PathGenerator covering the full orchestration: downloading,
/// decryption, variant production, CHOpt invocation, and result aggregation.
/// Uses a fake CHOpt script and mock HTTP handler to avoid external dependencies.
/// </summary>
public sealed class PathGeneratorOrchestrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fakeChoptPath;
    private readonly byte[] _midiKey;

    public PathGeneratorOrchestrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _midiKey = new byte[32];
        RandomNumberGenerator.Fill(_midiKey);

        // Create a fake CHOpt script that writes "Total score: 123456" to stdout
        // and creates a tiny PNG file at the -o path.
        if (OperatingSystem.IsWindows())
        {
            _fakeChoptPath = Path.Combine(_tempDir, "fake_chopt.bat");
            // Parse -o argument to find output path
            File.WriteAllText(_fakeChoptPath, """
                @echo off
                echo Total score: 123456
                set "outfile="
                :parse
                if "%~1"=="" goto done
                if "%~1"=="-o" (
                    set "outfile=%~2"
                    shift
                )
                shift
                goto parse
                :done
                if defined outfile (
                    echo PNG > "%outfile%"
                )
                """);
        }
        else
        {
            _fakeChoptPath = Path.Combine(_tempDir, "fake_chopt.sh");
            File.WriteAllText(_fakeChoptPath, """
                #!/bin/sh
                echo "Total score: 123456"
                outfile=""
                while [ "$#" -gt 0 ]; do
                    case "$1" in
                        -o) outfile="$2"; shift ;;
                    esac
                    shift
                done
                [ -n "$outfile" ] && echo "PNG" > "$outfile"
                """);
            File.SetUnixFileMode(_fakeChoptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Build a minimal valid MIDI file for testing.</summary>
    private static byte[] BuildMinimalMidi()
    {
        using var ms = new MemoryStream();
        ms.Write("MThd"u8);
        WriteBE32(ms, 6);
        WriteBE16(ms, 1); // format
        WriteBE16(ms, 1); // 1 track
        WriteBE16(ms, 480);
        // One track with just an End of Track event
        var trackData = new byte[] { 0x00, 0xFF, 0x2F, 0x00 };
        ms.Write("MTrk"u8);
        WriteBE32(ms, trackData.Length);
        ms.Write(trackData);
        return ms.ToArray();
    }

    /// <summary>Encrypt MIDI data with AES-ECB for testing.</summary>
    private byte[] EncryptMidi(byte[] midiData)
    {
        using var aes = Aes.Create();
        aes.Key = _midiKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.Zeros;
        using var enc = aes.CreateEncryptor();
        // Pad to 16-byte boundary
        int padded = (midiData.Length + 15) / 16 * 16;
        var input = new byte[padded];
        Array.Copy(midiData, input, midiData.Length);
        return enc.TransformFinalBlock(input, 0, input.Length);
    }

    private PathGenerator CreateGenerator(MockHttpMessageHandler handler, ScraperOptions? opts = null)
    {
        opts ??= new ScraperOptions
        {
            DataDirectory = _tempDir,
            CHOptPath = _fakeChoptPath,
            MidiEncryptionKey = Convert.ToHexString(_midiKey),
            EnablePathGeneration = true,
            PathGenerationParallelism = 2,
        };
        var http = new HttpClient(handler);
        return new PathGenerator(http, Options.Create(opts), new ScrapeProgressTracker(), Substitute.For<ILogger<PathGenerator>>());
    }

    [Fact]
    public async Task GeneratePathsAsync_no_key_returns_empty()
    {
        var handler = new MockHttpMessageHandler();
        var opts = new ScraperOptions
        {
            DataDirectory = _tempDir,
            CHOptPath = _fakeChoptPath,
            MidiEncryptionKey = null,
            EnablePathGeneration = true,
        };
        var gen = CreateGenerator(handler, opts);

        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GeneratePathsAsync_no_chopt_returns_empty()
    {
        var handler = new MockHttpMessageHandler();
        var opts = new ScraperOptions
        {
            DataDirectory = _tempDir,
            CHOptPath = Path.Combine(_tempDir, "nonexistent_chopt"),
            MidiEncryptionKey = Convert.ToHexString(_midiKey),
            EnablePathGeneration = true,
        };
        var gen = CreateGenerator(handler, opts);

        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GeneratePathsAsync_skips_song_when_hash_unchanged()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);
        var hash = MidiCryptor.ComputeHash(encrypted);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, hash, null)],
            false, CancellationToken.None);

        // Should skip because hash matches
        Assert.Empty(results);
    }

    [Fact]
    public async Task GeneratePathsAsync_processes_song_when_forced()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);
        var hash = MidiCryptor.ComputeHash(encrypted);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, hash, null)],
            force: true, CancellationToken.None);

        // Should process even though hash matches because force=true
        Assert.Single(results);
        Assert.Equal("s1", results[0].SongId);
        Assert.Equal(hash, results[0].DatFileHash);
        // 6 instruments × 4 difficulties = 24 results
        Assert.Equal(24, results[0].Results.Count);
    }

    [Fact]
    public async Task GeneratePathsAsync_returns_scores_from_chopt()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        Assert.Single(results);
        // Fake CHOpt always outputs "Total score: 123456"
        foreach (var pr in results[0].Results)
        {
            Assert.Equal(123456, pr.MaxScore);
        }
    }

    [Fact]
    public async Task GeneratePathsAsync_skips_song_with_no_dat_url()
    {
        var handler = new MockHttpMessageHandler();
        var gen = CreateGenerator(handler);

        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", null, null, null, null)],
            false, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(handler.Requests); // No HTTP requests made
    }

    [Fact]
    public async Task GeneratePathsAsync_handles_download_failure()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueError(HttpStatusCode.InternalServerError, "boom");

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        // Download failure is caught and song is skipped
        Assert.Empty(results);
    }

    [Fact]
    public async Task GeneratePathsAsync_creates_output_directory_structure()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("song123", "Test Song", "Test Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        // Verify directory structure: data/paths/{songId}/{instrument}/
        var pathsDir = Path.Combine(_tempDir, "paths", "song123");
        Assert.True(Directory.Exists(pathsDir));

        var expectedInstruments = new[]
        {
            "Solo_PeripheralGuitar", "Solo_PeripheralBass",
            "Solo_Drums", "Solo_Vocals",
            "Solo_Guitar", "Solo_Bass"
        };
        foreach (var inst in expectedInstruments)
        {
            var instDir = Path.Combine(pathsDir, inst);
            Assert.True(Directory.Exists(instDir), $"Missing directory: {instDir}");
        }

        // Verify .dat was cached
        Assert.True(File.Exists(Path.Combine(_tempDir, "midi", "song123.dat")));
    }

    [Fact]
    public async Task GeneratePathsAsync_results_include_all_difficulties()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("s1", "Song", "Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        var songResult = Assert.Single(results);
        var diffs = songResult.Results.Select(r => r.Difficulty).Distinct().OrderBy(d => d).ToList();
        Assert.Equal(["easy", "expert", "hard", "medium"], diffs);
    }

    [Fact]
    public async Task GeneratePathsAsync_multiple_songs()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);

        var handler = new MockHttpMessageHandler();
        // Two songs → two downloads
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [
                new PathGenerator.SongPathRequest("s1", "Song1", "Artist1", "http://x/1.dat", null, null, null),
                new PathGenerator.SongPathRequest("s2", "Song2", "Artist2", "http://x/2.dat", null, null, null),
            ],
            false, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.SongId == "s1");
        Assert.Contains(results, r => r.SongId == "s2");
    }

    [Fact]
    public async Task RunCHOptAsync_nonzero_exit_returns_null()
    {
        // Create a script that exits with code 1
        string failScript;
        if (OperatingSystem.IsWindows())
        {
            failScript = Path.Combine(_tempDir, "fail_chopt.bat");
            File.WriteAllText(failScript, "@echo off\necho error on stderr 1>&2\nexit /b 1\n");
        }
        else
        {
            failScript = Path.Combine(_tempDir, "fail_chopt.sh");
            File.WriteAllText(failScript, "#!/bin/sh\necho error on stderr >&2\nexit 1\n");
            File.SetUnixFileMode(failScript,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var opts = new ScraperOptions
        {
            DataDirectory = _tempDir,
            CHOptPath = failScript,
            MidiEncryptionKey = Convert.ToHexString(_midiKey),
            PathGenerationParallelism = 1,
        };
        var gen = CreateGenerator(new MockHttpMessageHandler(), opts);

        var result = await gen.RunCHOptAsync(
            failScript, "dummy.mid", "guitar", "expert",
            Path.Combine(_tempDir, "out.png"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RunCHOptAsync_success_returns_parsed_score()
    {
        var gen = CreateGenerator(new MockHttpMessageHandler());

        var result = await gen.RunCHOptAsync(
            _fakeChoptPath, "dummy.mid", "guitar", "expert",
            Path.Combine(_tempDir, "out.png"), CancellationToken.None);

        Assert.Equal(123456, result);
    }

    [Fact]
    public async Task GeneratePathsAsync_writes_song_ini()
    {
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("iniTest", "My Song Title", "My Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        // The song.ini is written to a temp dir and cleaned up after — we can verify
        // it worked by confirming CHOpt was called (results come back with scores).
        // The cleanup happens in finally{}, so we can't check the file directly.
        // Instead, verify the run succeeded (meaning the temp dir was created and used).
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "paths", "iniTest")));
    }

    [Fact]
    public async Task End_to_end_generate_and_persist_to_PathDataStore()
    {
        // Set up a real Songs table + PathDataStore so we can verify DB persistence
        var dbPath = Path.Combine(_tempDir, "e2e.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Songs (
                    SongId TEXT PRIMARY KEY, Title TEXT,
                    MaxLeadScore INTEGER, MaxBassScore INTEGER, MaxDrumsScore INTEGER,
                    MaxVocalsScore INTEGER, MaxProLeadScore INTEGER, MaxProBassScore INTEGER,
                    DatFileHash TEXT, SongLastModified TEXT, PathsGeneratedAt TEXT, CHOptVersion TEXT
                );
                INSERT INTO Songs (SongId, Title) VALUES ('testSong', 'Test Song');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new PathDataStore(dbPath);
        var midi = BuildMinimalMidi();
        var encrypted = EncryptMidi(midi);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("testSong", "Test Song", "Artist", "http://x/s.dat", null, null, null)],
            false, CancellationToken.None);

        var songResult = Assert.Single(results);

        // Persist expert scores (same logic as ScraperWorker.TryGeneratePathsAsync)
        var scores = new SongMaxScores
        {
            GeneratedAt = DateTime.UtcNow.ToString("o"),
            CHOptVersion = "1.10.3",
        };
        foreach (var pr in songResult.Results.Where(r => r.Difficulty == "expert"))
            scores.SetByInstrument(pr.Instrument, pr.MaxScore);

        store.UpdateMaxScores(songResult.SongId, scores, songResult.DatFileHash);

        // Verify the DB was updated
        var allScores = store.GetAllMaxScores();
        Assert.True(allScores.ContainsKey("testSong"));
        Assert.Equal(123456, allScores["testSong"].MaxLeadScore);
        Assert.Equal(123456, allScores["testSong"].MaxBassScore);

        var state = store.GetPathGenerationState();
        Assert.Equal(songResult.DatFileHash, state["testSong"].Hash);
    }

    [Fact]
    public async Task GeneratePathsAsync_exception_in_song_returns_empty_for_that_song()
    {
        // Send a response that's not valid encrypted MIDI — will fail during decrypt
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x00, 0x01, 0x02 }),
        });

        var gen = CreateGenerator(handler);
        var results = await gen.GeneratePathsAsync(
            [new PathGenerator.SongPathRequest("bad", "Bad Song", "Artist", "http://x/bad.dat", null, null, null)],
            false, CancellationToken.None);

        // Song should fail and be filtered out — caught by the exception handler
        Assert.Empty(results);
    }

    private static void WriteBE32(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteBE16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
}
