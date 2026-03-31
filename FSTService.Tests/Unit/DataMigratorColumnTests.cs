namespace FSTService.Tests.Unit;

/// <summary>
/// Verifies that the DataMigrator includes all required columns in the songs table migration.
/// This prevents a regression where path-generation columns (max scores, dat hash, etc.)
/// are accidentally omitted during SQLite → PostgreSQL migration.
/// </summary>
public sealed class DataMigratorColumnTests
{
    /// <summary>
    /// The DataMigrator source must include all path-generation columns in the songs CopyTableAsync call.
    /// Without these columns, the PG songs table has NULL max scores after migration, which breaks
    /// the entire invalid-score filtering feature.
    /// </summary>
    [Theory]
    [InlineData("MaxLeadScore", "max_lead_score")]
    [InlineData("MaxBassScore", "max_bass_score")]
    [InlineData("MaxDrumsScore", "max_drums_score")]
    [InlineData("MaxVocalsScore", "max_vocals_score")]
    [InlineData("MaxProLeadScore", "max_pro_lead_score")]
    [InlineData("MaxProBassScore", "max_pro_bass_score")]
    [InlineData("DatFileHash", "dat_file_hash")]
    [InlineData("SongLastModified", "song_last_modified")]
    [InlineData("PathsGeneratedAt", "paths_generated_at")]
    [InlineData("CHOptVersion", "chopt_version")]
    public void DataMigrator_Songs_IncludesColumn(string sqliteCol, string pgCol)
    {
        // Read the DataMigrator source file to verify column inclusion.
        // This is a compile-time-adjacent safety net: if someone removes a column
        // from the migration, this test fails immediately.
        var sourceFile = FindSourceFile("DataMigrator.cs");
        Assert.NotNull(sourceFile);

        var source = File.ReadAllText(sourceFile);

        Assert.Contains($"\"{sqliteCol}\"", source,
            StringComparison.Ordinal);
        Assert.Contains($"\"{pgCol}\"", source,
            StringComparison.Ordinal);
    }

    private static string? FindSourceFile(string fileName)
    {
        // Walk up from test output directory to find the repo root
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "FSTService", "Persistence", "Pg", fileName);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return null;
    }
}
