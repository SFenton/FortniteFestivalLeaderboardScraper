namespace FSTService.Scraping;

public static class ScraperDataCleanup
{
    public sealed record CleanupResult(int FilesDeleted, int DirectoriesDeleted, long BytesDeleted);

    public static CleanupResult DeleteLegacyDatFiles(string dataDirectory, ILogger log)
    {
        var midiDir = Path.Combine(Path.GetFullPath(dataDirectory), "midi");
        if (!Directory.Exists(midiDir))
            return new CleanupResult(0, 0, 0);

        var filesDeleted = 0;
        long bytesDeleted = 0;
        foreach (var path in Directory.EnumerateFiles(midiDir, "*.dat", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var length = new FileInfo(path).Length;
                File.Delete(path);
                filesDeleted++;
                bytesDeleted += length;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to delete legacy encrypted DAT cache file {Path}.", path);
            }
        }

        if (filesDeleted > 0)
            log.LogInformation("Deleted {Count:N0} legacy encrypted DAT cache file(s), reclaiming {Bytes:N0} bytes.",
                filesDeleted, bytesDeleted);

        return new CleanupResult(filesDeleted, 0, bytesDeleted);
    }

    public static CleanupResult CleanupStaleDataSpools(
        string dataDirectory,
        TimeSpan minimumAge,
        ILogger log,
        DateTimeOffset? now = null)
    {
        var spoolRoot = Path.Combine(Path.GetFullPath(dataDirectory), "spool");
        return CleanupStaleSpools(spoolRoot, minimumAge, log, now);
    }

    public static CleanupResult CleanupStaleSpools(
        string spoolRoot,
        TimeSpan minimumAge,
        ILogger log,
        DateTimeOffset? now = null)
    {
        if (!Directory.Exists(spoolRoot))
            return new CleanupResult(0, 0, 0);

        var cutoffUtc = (now ?? DateTimeOffset.UtcNow).UtcDateTime - minimumAge;
        var filesDeleted = 0;
        var directoriesDeleted = 0;
        long bytesDeleted = 0;

        foreach (var dir in Directory.EnumerateDirectories(spoolRoot, "fst_scrape_*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (GetLastWriteTimeUtc(info) > cutoffUtc)
                    continue;

                var bytes = GetDirectorySize(info);
                Directory.Delete(dir, recursive: true);
                directoriesDeleted++;
                bytesDeleted += bytes;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to delete stale scrape spool directory {Path}.", dir);
            }
        }

        foreach (var file in Directory.EnumerateFiles(spoolRoot, "fst_scrape_*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoffUtc)
                    continue;

                var length = info.Length;
                File.Delete(file);
                filesDeleted++;
                bytesDeleted += length;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to delete stale scrape spool file {Path}.", file);
            }
        }

        if (filesDeleted > 0 || directoriesDeleted > 0)
            log.LogInformation(
                "Deleted {Directories:N0} stale scrape spool dirs and {Files:N0} stale scrape spool files, reclaiming {Bytes:N0} bytes.",
                directoriesDeleted, filesDeleted, bytesDeleted);

        return new CleanupResult(filesDeleted, directoriesDeleted, bytesDeleted);
    }

    private static long GetDirectorySize(DirectoryInfo directory)
    {
        long total = 0;
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += file.Length; }
            catch { }
        }
        return total;
    }

    private static DateTime GetLastWriteTimeUtc(DirectoryInfo directory)
    {
        var latest = directory.LastWriteTimeUtc;
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                if (file.LastWriteTimeUtc > latest)
                    latest = file.LastWriteTimeUtc;
            }
            catch { }
        }
        return latest;
    }
}