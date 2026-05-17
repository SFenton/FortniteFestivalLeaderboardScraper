namespace FSTService.Persistence;

internal static class BandRankingStorageNames
{
    internal static readonly IReadOnlyList<string> AllBandTypes = ["Band_Duets", "Band_Trios", "Band_Quad"];

    private static readonly IReadOnlyList<BandRankingIndexTemplate> RankingIndexTemplates =
    [
        new("adjusted", "adjusted_skill_rank", "supports adjusted skill ranking reads"),
        new("weighted", "weighted_rank", "supports weighted ranking reads"),
        new("fcrate", "fc_rate_rank", "supports full-combo rate ranking reads"),
        new("totalscore", "total_score_rank", "supports total-score ranking reads"),
    ];

    internal static string GetCurrentRankingTable(string bandType) => $"band_team_rankings_current_{GetBandTypeSlug(bandType)}";

    internal static string GetCurrentStatsTable(string bandType) => $"band_team_ranking_stats_current_{GetBandTypeSlug(bandType)}";

    internal static string GetCurrentBandSongRankingTable(string bandType) => $"band_song_team_rankings_current_{GetBandTypeSlug(bandType)}";

    internal static string GetBandSongRankingBuildTable(string bandType, string buildSuffix) => $"band_song_team_rankings_build_{GetBandTypeSlug(bandType)}_{buildSuffix}".Replace('-', '_');

    internal static string GetCreateRankingTableSql(string tableName, bool includePrimaryKey, bool temporary = false, bool ifNotExists = false, bool onCommitDrop = false)
    {
        var createPrefix = temporary ? "CREATE TEMP TABLE" : "CREATE TABLE";
        var ifNotExistsClause = ifNotExists ? " IF NOT EXISTS" : string.Empty;
        var onCommitClause = onCommitDrop ? " ON COMMIT DROP" : string.Empty;
        var primaryKeyClause = includePrimaryKey
            ? ",\n                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key)"
            : string.Empty;

        return $@"
            {createPrefix}{ifNotExistsClause} {QuoteIdentifier(tableName)} (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                team_members          TEXT[]           NOT NULL,
                songs_played          INT              NOT NULL,
                total_charted_songs   INT              NOT NULL,
                coverage              DOUBLE PRECISION NOT NULL,
                raw_skill_rating      DOUBLE PRECISION NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rating       DOUBLE PRECISION NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate               DOUBLE PRECISION NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score           BIGINT           NOT NULL,
                total_score_rank      INT              NOT NULL,
                avg_accuracy          DOUBLE PRECISION NOT NULL,
                full_combo_count      INT              NOT NULL,
                avg_stars             DOUBLE PRECISION NOT NULL,
                best_rank             INT              NOT NULL,
                avg_rank              DOUBLE PRECISION NOT NULL,
                raw_weighted_rating   DOUBLE PRECISION,
                computed_at           TIMESTAMPTZ      NOT NULL DEFAULT now(),
                ranking_generation    BIGINT           NOT NULL DEFAULT 0,
                row_fingerprint       TEXT             NOT NULL DEFAULT ''{primaryKeyClause}
            ){onCommitClause};";
    }

    internal static string GetEnsureRankingMetadataColumnsSql(string tableName) => $@"
            ALTER TABLE IF EXISTS {QuoteIdentifier(tableName)}
                ADD COLUMN IF NOT EXISTS ranking_generation BIGINT NOT NULL DEFAULT 0;

            ALTER TABLE IF EXISTS {QuoteIdentifier(tableName)}
                ADD COLUMN IF NOT EXISTS row_fingerprint TEXT NOT NULL DEFAULT '';";

    internal static string GetCreateRankingIndexesSql(string tableName, bool ifNotExists = false)
    {
        return string.Join(
            Environment.NewLine,
            BuildRankingIndexDefinitions(tableName, ifNotExists, concurrently: false, schemaName: null)
                .Select(definition => $"            {definition.CreateSql};"));
    }

    internal static IReadOnlyList<BandRankingIndexSqlDefinition> GetCurrentRankingIndexDefinitions() =>
        AllBandTypes
            .SelectMany(bandType => BuildRankingIndexDefinitions(
                GetCurrentRankingTable(bandType),
                ifNotExists: true,
                concurrently: true,
                schemaName: "public"))
            .ToArray();

    internal static string GetCreateStatsTableSql(string tableName, bool includePrimaryKey, bool temporary = false, bool ifNotExists = false, bool onCommitDrop = false)
    {
        var createPrefix = temporary ? "CREATE TEMP TABLE" : "CREATE TABLE";
        var ifNotExistsClause = ifNotExists ? " IF NOT EXISTS" : string.Empty;
        var onCommitClause = onCommitDrop ? " ON COMMIT DROP" : string.Empty;
        var primaryKeyClause = includePrimaryKey
            ? ",\n                PRIMARY KEY (band_type, ranking_scope, combo_id)"
            : string.Empty;

        return $@"
            {createPrefix}{ifNotExistsClause} {QuoteIdentifier(tableName)} (
                band_type      TEXT        NOT NULL,
                ranking_scope  TEXT        NOT NULL,
                combo_id       TEXT        NOT NULL DEFAULT '',
                total_teams    INT         NOT NULL,
                computed_at    TIMESTAMPTZ NOT NULL DEFAULT now(){primaryKeyClause}
            ){onCommitClause};";
    }

    internal static string GetCreateBandSongRankingTableSql(string tableName, bool includePrimaryKey, bool temporary = false, bool ifNotExists = false, bool onCommitDrop = false)
    {
        var createPrefix = temporary ? "CREATE TEMP TABLE" : "CREATE TABLE";
        var ifNotExistsClause = ifNotExists ? " IF NOT EXISTS" : string.Empty;
        var onCommitClause = onCommitDrop ? " ON COMMIT DROP" : string.Empty;
        var primaryKeyClause = includePrimaryKey
            ? ",\n                PRIMARY KEY (band_type, ranking_scope, scope_combo_id, team_key, song_id)"
            : string.Empty;

        return $@"
            {createPrefix}{ifNotExistsClause} {QuoteIdentifier(tableName)} (
                band_type       TEXT             NOT NULL,
                ranking_scope   TEXT             NOT NULL,
                scope_combo_id  TEXT             NOT NULL DEFAULT '',
                team_key        TEXT             NOT NULL,
                song_id         TEXT             NOT NULL,
                entry_combo_id  TEXT             NOT NULL DEFAULT '',
                rank            INTEGER          NOT NULL,
                total_entries   INTEGER          NOT NULL,
                percentile      DOUBLE PRECISION NOT NULL,
                score           INTEGER          NOT NULL,
                accuracy        INTEGER,
                is_full_combo   BOOLEAN,
                stars           INTEGER,
                season          INTEGER,
                end_time        TEXT,
                computed_at     TIMESTAMPTZ      NOT NULL DEFAULT now(){primaryKeyClause}
            ){onCommitClause};";
    }

    internal static string GetCreateBandSongRankingIndexesSql(string tableName, bool includeUnique, bool ifNotExists = false)
    {
        var quotedTable = QuoteIdentifier(tableName);
        var ifNotExistsClause = ifNotExists ? " IF NOT EXISTS" : string.Empty;
        var statements = new List<string>();

        if (includeUnique)
            statements.Add($"CREATE UNIQUE INDEX{ifNotExistsClause} {QuoteIdentifier(tableName + "_pkey")} ON {quotedTable} (band_type, ranking_scope, scope_combo_id, team_key, song_id)");

        statements.Add($"CREATE INDEX{ifNotExistsClause} {QuoteIdentifier(tableName + "_ix_team")} ON {quotedTable} (band_type, ranking_scope, scope_combo_id, team_key, percentile ASC, rank ASC, score DESC, song_id ASC)");

        return string.Join($";{Environment.NewLine}", statements.Select(statement => $"            {statement}")) + ";";
    }

    internal static string GetCurrentSchemaSql()
    {
        var statements = new List<string>();

        foreach (var bandType in AllBandTypes)
        {
            var rankingsTable = GetCurrentRankingTable(bandType);
            var statsTable = GetCurrentStatsTable(bandType);
            var songRankingsTable = GetCurrentBandSongRankingTable(bandType);

            statements.Add(GetCreateRankingTableSql(rankingsTable, includePrimaryKey: true, ifNotExists: true));
            statements.Add(GetEnsureRankingMetadataColumnsSql(rankingsTable));
            statements.Add(GetCreateStatsTableSql(statsTable, includePrimaryKey: true, ifNotExists: true));
            statements.Add(GetCreateBandSongRankingTableSql(songRankingsTable, includePrimaryKey: true, ifNotExists: true));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, statements);
    }

    private static IReadOnlyList<BandRankingIndexSqlDefinition> BuildRankingIndexDefinitions(
        string tableName,
        bool ifNotExists,
        bool concurrently,
        string? schemaName)
    {
        var quotedTable = QuoteIdentifier(tableName);
        var qualifiedTable = string.IsNullOrWhiteSpace(schemaName)
            ? quotedTable
            : $"{schemaName}.{quotedTable}";
        var ifNotExistsClause = ifNotExists ? " IF NOT EXISTS" : string.Empty;
        var concurrentlyClause = concurrently ? " CONCURRENTLY" : string.Empty;

        return RankingIndexTemplates
            .Select(template =>
            {
                var indexName = $"{tableName}_ix_{template.Suffix}";
                var keyColumns = new[] { "band_type", "ranking_scope", "combo_id", template.RankColumn };
                var columnList = string.Join(", ", keyColumns.Select(QuoteIdentifier));
                var createSql = $"CREATE INDEX{concurrentlyClause}{ifNotExistsClause} {QuoteIdentifier(indexName)} ON {qualifiedTable} ({columnList})";
                return new BandRankingIndexSqlDefinition(indexName, tableName, keyColumns, createSql, template.Purpose);
            })
            .ToArray();
    }

    internal static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string GetBandTypeSlug(string bandType) => bandType switch
    {
        "Band_Duets" => "band_duets",
        "Band_Trios" => "band_trios",
        "Band_Quad" => "band_quad",
        _ => throw new ArgumentOutOfRangeException(nameof(bandType), bandType, "Unsupported band type."),
    };
}

internal sealed record BandRankingIndexSqlDefinition(
    string Name,
    string TableName,
    IReadOnlyList<string> KeyColumns,
    string CreateSql,
    string Purpose);

internal sealed record BandRankingIndexTemplate(string Suffix, string RankColumn, string Purpose);