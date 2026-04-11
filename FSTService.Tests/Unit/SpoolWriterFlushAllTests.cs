using FSTService.Scraping;
using NSubstitute;
using Microsoft.Extensions.Logging;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="SpoolWriter{T}.FlushAll"/> — verifies the post-fetch
/// flush path reads spool files correctly and calls the flush delegate.
/// </summary>
public class SpoolWriterFlushAllTests
{
    private readonly ILogger _log = Substitute.For<ILogger>();

    private sealed class TestEntry
    {
        public string Id { get; set; } = "";
        public int Value { get; set; }
    }

    private SpoolWriter<TestEntry> CreateSpool()
    {
        return new SpoolWriter<TestEntry>(
            _log, "test",
            serialize: (buf, header, songId, entries) =>
            {
                SpoolWriter<TestEntry>.WriteString(buf, header, songId);
                SpoolWriter<TestEntry>.WriteInt32(buf, header, entries.Count);
                foreach (var e in entries)
                {
                    SpoolWriter<TestEntry>.WriteString(buf, header, e.Id);
                    SpoolWriter<TestEntry>.WriteInt32(buf, header, e.Value);
                }
            },
            deserialize: (stream, header) =>
            {
                var songId = SpoolWriter<TestEntry>.ReadString(stream, header);
                int count = SpoolWriter<TestEntry>.ReadInt32(stream, header);
                var entries = new TestEntry[count];
                for (int i = 0; i < count; i++)
                    entries[i] = new TestEntry
                    {
                        Id = SpoolWriter<TestEntry>.ReadString(stream, header),
                        Value = SpoolWriter<TestEntry>.ReadInt32(stream, header),
                    };
                return (songId, entries);
            },
            flush: (instrument, batch) => { });
    }

    [Fact]
    public async Task FlushAll_SingleInstrument_CallsFlushWithAllPages()
    {
        // Arrange
        var flushedBatches = new List<(string Instrument, int PageCount, int EntryCount)>();
        await using var spool = new SpoolWriter<TestEntry>(
            _log, "test-flush",
            serialize: (buf, header, songId, entries) =>
            {
                SpoolWriter<TestEntry>.WriteString(buf, header, songId);
                SpoolWriter<TestEntry>.WriteInt32(buf, header, entries.Count);
                foreach (var e in entries)
                {
                    SpoolWriter<TestEntry>.WriteString(buf, header, e.Id);
                    SpoolWriter<TestEntry>.WriteInt32(buf, header, e.Value);
                }
            },
            deserialize: (stream, header) =>
            {
                var songId = SpoolWriter<TestEntry>.ReadString(stream, header);
                int count = SpoolWriter<TestEntry>.ReadInt32(stream, header);
                var entries = new TestEntry[count];
                for (int i = 0; i < count; i++)
                    entries[i] = new TestEntry
                    {
                        Id = SpoolWriter<TestEntry>.ReadString(stream, header),
                        Value = SpoolWriter<TestEntry>.ReadInt32(stream, header),
                    };
                return (songId, entries);
            },
            flush: (instrument, batch) =>
            {
                flushedBatches.Add((instrument, batch.Count, batch.Sum(b => b.Entries.Count)));
            });

        // Write 3 pages to one instrument
        spool.Enqueue("song1", "Guitar", new[] { new TestEntry { Id = "a", Value = 1 } });
        spool.Enqueue("song2", "Guitar", new[] { new TestEntry { Id = "b", Value = 2 }, new TestEntry { Id = "c", Value = 3 } });
        spool.Enqueue("song3", "Guitar", new[] { new TestEntry { Id = "d", Value = 4 } });

        // Act
        spool.Complete();
        spool.FlushAll();

        // Assert
        Assert.Single(flushedBatches);
        Assert.Equal("Guitar", flushedBatches[0].Instrument);
        Assert.Equal(3, flushedBatches[0].PageCount);
        Assert.Equal(4, flushedBatches[0].EntryCount);
    }

    [Fact]
    public async Task FlushAll_MultipleInstruments_FlushesEachSeparately()
    {
        var flushed = new List<(string Instrument, int Pages)>();
        await using var spool = new SpoolWriter<TestEntry>(
            _log, "test-multi",
            serialize: (buf, header, songId, entries) =>
            {
                SpoolWriter<TestEntry>.WriteString(buf, header, songId);
                SpoolWriter<TestEntry>.WriteInt32(buf, header, entries.Count);
                foreach (var e in entries)
                {
                    SpoolWriter<TestEntry>.WriteString(buf, header, e.Id);
                    SpoolWriter<TestEntry>.WriteInt32(buf, header, e.Value);
                }
            },
            deserialize: (stream, header) =>
            {
                var songId = SpoolWriter<TestEntry>.ReadString(stream, header);
                int count = SpoolWriter<TestEntry>.ReadInt32(stream, header);
                var entries = new TestEntry[count];
                for (int i = 0; i < count; i++)
                    entries[i] = new TestEntry
                    {
                        Id = SpoolWriter<TestEntry>.ReadString(stream, header),
                        Value = SpoolWriter<TestEntry>.ReadInt32(stream, header),
                    };
                return (songId, entries);
            },
            flush: (instrument, batch) => flushed.Add((instrument, batch.Count)));

        spool.Enqueue("s1", "Guitar", new[] { new TestEntry { Id = "a", Value = 1 } });
        spool.Enqueue("s1", "Drums", new[] { new TestEntry { Id = "b", Value = 2 } });
        spool.Enqueue("s2", "Guitar", new[] { new TestEntry { Id = "c", Value = 3 } });
        spool.Enqueue("s1", "Vocals", new[] { new TestEntry { Id = "d", Value = 4 } });

        spool.Complete();
        spool.FlushAll();

        Assert.Equal(3, flushed.Count);
        Assert.Contains(flushed, f => f.Instrument == "Guitar" && f.Pages == 2);
        Assert.Contains(flushed, f => f.Instrument == "Drums" && f.Pages == 1);
        Assert.Contains(flushed, f => f.Instrument == "Vocals" && f.Pages == 1);
    }

    [Fact]
    public async Task FlushAll_EmptySpool_NoFlushCalls()
    {
        var flushed = new List<string>();
        await using var spool = new SpoolWriter<TestEntry>(
            _log, "test-empty",
            serialize: (buf, header, songId, entries) => { },
            deserialize: (stream, header) => ("", Array.Empty<TestEntry>()),
            flush: (instrument, batch) => flushed.Add(instrument));

        spool.Complete();
        spool.FlushAll();

        Assert.Empty(flushed);
    }

    [Fact]
    public async Task FlushAll_BeforeComplete_Throws()
    {
        await using var spool = CreateSpool();
        spool.Enqueue("s1", "Guitar", new[] { new TestEntry { Id = "a", Value = 1 } });

        Assert.Throws<InvalidOperationException>(() => spool.FlushAll());
    }

    [Fact]
    public async Task FlushAll_RoundtripPreservesData()
    {
        var received = new List<(string SongId, string Id, int Value)>();
        await using var spool = new SpoolWriter<TestEntry>(
            _log, "test-rt",
            serialize: (buf, header, songId, entries) =>
            {
                SpoolWriter<TestEntry>.WriteString(buf, header, songId);
                SpoolWriter<TestEntry>.WriteInt32(buf, header, entries.Count);
                foreach (var e in entries)
                {
                    SpoolWriter<TestEntry>.WriteString(buf, header, e.Id);
                    SpoolWriter<TestEntry>.WriteInt32(buf, header, e.Value);
                }
            },
            deserialize: (stream, header) =>
            {
                var songId = SpoolWriter<TestEntry>.ReadString(stream, header);
                int count = SpoolWriter<TestEntry>.ReadInt32(stream, header);
                var entries = new TestEntry[count];
                for (int i = 0; i < count; i++)
                    entries[i] = new TestEntry
                    {
                        Id = SpoolWriter<TestEntry>.ReadString(stream, header),
                        Value = SpoolWriter<TestEntry>.ReadInt32(stream, header),
                    };
                return (songId, entries);
            },
            flush: (instrument, batch) =>
            {
                foreach (var (songId, entries) in batch)
                    foreach (var e in entries)
                        received.Add((songId, e.Id, e.Value));
            });

        spool.Enqueue("song1", "Inst", new[]
        {
            new TestEntry { Id = "alpha", Value = 42 },
            new TestEntry { Id = "beta", Value = 99 },
        });
        spool.Enqueue("song2", "Inst", new[]
        {
            new TestEntry { Id = "gamma", Value = -1 },
        });

        spool.Complete();
        spool.FlushAll();

        Assert.Equal(3, received.Count);
        Assert.Contains(received, r => r.SongId == "song1" && r.Id == "alpha" && r.Value == 42);
        Assert.Contains(received, r => r.SongId == "song1" && r.Id == "beta" && r.Value == 99);
        Assert.Contains(received, r => r.SongId == "song2" && r.Id == "gamma" && r.Value == -1);
    }

    [Fact]
    public async Task SpoolOnDisk_UsesCustomBaseDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spool_test_{Guid.NewGuid():N}");
        try
        {
            await using var spool = new SpoolWriter<TestEntry>(
                _log, "test-dir",
                serialize: (buf, header, songId, entries) =>
                {
                    SpoolWriter<TestEntry>.WriteString(buf, header, songId);
                    SpoolWriter<TestEntry>.WriteInt32(buf, header, 0);
                },
                deserialize: (stream, header) => (SpoolWriter<TestEntry>.ReadString(stream, header), Array.Empty<TestEntry>()),
                flush: (_, _) => { },
                baseDirectory: tempDir);

            spool.Enqueue("s1", "Guitar", new[] { new TestEntry { Id = "a", Value = 1 } });

            // Verify files are in the custom directory
            Assert.True(Directory.Exists(tempDir));
            var spoolDirs = Directory.GetDirectories(tempDir, "fst_scrape_*");
            Assert.Single(spoolDirs);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
