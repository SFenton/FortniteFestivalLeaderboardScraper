using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class ScraperDataCleanupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _log = Substitute.For<ILogger>();

    public ScraperDataCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scraper_cleanup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void DeleteLegacyDatFiles_removes_only_top_level_midi_dat_files()
    {
        var midiDir = Path.Combine(_tempDir, "midi");
        Directory.CreateDirectory(midiDir);
        var datA = Path.Combine(midiDir, "a.dat");
        var datB = Path.Combine(midiDir, "b.dat");
        var keepTxt = Path.Combine(midiDir, "keep.txt");
        File.WriteAllBytes(datA, [1, 2, 3]);
        File.WriteAllBytes(datB, [4, 5]);
        File.WriteAllText(keepTxt, "keep");

        var pathsDir = Path.Combine(_tempDir, "paths", "song1");
        Directory.CreateDirectory(pathsDir);
        var generatedDatNamedFile = Path.Combine(pathsDir, "derived.dat");
        File.WriteAllText(generatedDatNamedFile, "not midi cache");

        var result = ScraperDataCleanup.DeleteLegacyDatFiles(_tempDir, _log);

        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(5, result.BytesDeleted);
        Assert.False(File.Exists(datA));
        Assert.False(File.Exists(datB));
        Assert.True(File.Exists(keepTxt));
        Assert.True(File.Exists(generatedDatNamedFile));
    }

    [Fact]
    public void CleanupStaleDataSpools_removes_old_spool_directories_and_leaves_fresh_or_unmatched()
    {
        var spoolRoot = Path.Combine(_tempDir, "spool");
        Directory.CreateDirectory(spoolRoot);
        var oldDir = Path.Combine(spoolRoot, "fst_scrape_old");
        var freshDir = Path.Combine(spoolRoot, "fst_scrape_fresh");
        var unrelatedDir = Path.Combine(spoolRoot, "other_old");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(freshDir);
        Directory.CreateDirectory(unrelatedDir);

        var oldFile = Path.Combine(oldDir, "Solo_Guitar.bin");
        var freshFile = Path.Combine(freshDir, "Solo_Guitar.bin");
        File.WriteAllBytes(oldFile, [1, 2, 3, 4]);
        File.WriteAllBytes(freshFile, [5, 6, 7]);

        var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
        var oldTime = now.UtcDateTime - TimeSpan.FromDays(2);
        var freshTime = now.UtcDateTime - TimeSpan.FromMinutes(5);
        File.SetLastWriteTimeUtc(oldFile, oldTime);
        Directory.SetLastWriteTimeUtc(oldDir, oldTime);
        File.SetLastWriteTimeUtc(freshFile, freshTime);
        Directory.SetLastWriteTimeUtc(freshDir, freshTime);
        Directory.SetLastWriteTimeUtc(unrelatedDir, oldTime);

        var result = ScraperDataCleanup.CleanupStaleDataSpools(
            _tempDir,
            TimeSpan.FromHours(24),
            _log,
            now);

        Assert.Equal(1, result.DirectoriesDeleted);
        Assert.Equal(4, result.BytesDeleted);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(freshDir));
        Assert.True(Directory.Exists(unrelatedDir));
    }

    [Fact]
    public void CleanupStaleSpools_removes_old_legacy_spool_files()
    {
        var spoolRoot = Path.Combine(_tempDir, "spool");
        Directory.CreateDirectory(spoolRoot);
        var oldFile = Path.Combine(spoolRoot, "fst_scrape_legacy.bin");
        var freshFile = Path.Combine(spoolRoot, "fst_scrape_fresh.bin");
        File.WriteAllBytes(oldFile, [1, 2]);
        File.WriteAllBytes(freshFile, [3, 4, 5]);

        var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(oldFile, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(freshFile, now.UtcDateTime - TimeSpan.FromMinutes(1));

        var result = ScraperDataCleanup.CleanupStaleSpools(spoolRoot, TimeSpan.FromHours(24), _log, now);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(2, result.BytesDeleted);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));
    }
}