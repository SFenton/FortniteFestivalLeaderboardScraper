namespace FSTService.Persistence;

internal static class BandRankingStorageNames
{
    internal static readonly IReadOnlyList<string> AllBandTypes = ["Band_Duets", "Band_Trios", "Band_Quad"];

    internal static string GetCurrentRankingTable(string bandType) => $"band_team_rankings_current_{GetBandTypeSlug(bandType)}";

    internal static string GetCurrentStatsTable(string bandType) => $"band_team_ranking_stats_current_{GetBandTypeSlug(bandType)}";

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
                computed_at           TIMESTAMPTZ      NOT NULL DEFAULT now(){primaryKeyClause}
            ){onCommitClause};";
    }

    internal static string GetCreateRankingIndexesSql(string tableName, bool ifNotExists = false)
    {
        var ifNotExistsClause = ifNotExists ? " IF NOT EXISTS" : string.Empty;
        var quotedTable = QuoteIdentifier(tableName);

        return $@"
            CREATE INDEX{ifNotExistsClause} {QuoteIdentifier(tableName + "_ix_adjusted")} ON {quotedTable} (band_type, ranking_scope, combo_id, adjusted_skill_rank);
            CREATE INDEX{ifNotExistsClause} {QuoteIdentifier(tableName + "_ix_weighted")} ON {quotedTable} (band_type, ranking_scope, combo_id, weighted_rank);
            CREATE INDEX{ifNotExistsClause} {QuoteIdentifier(tableName + "_ix_fcrate")} ON {quotedTable} (band_type, ranking_scope, combo_id, fc_rate_rank);
            CREATE INDEX{ifNotExistsClause} {QuoteIdentifier(tableName + "_ix_totalscore")} ON {quotedTable} (band_type, ranking_scope, combo_id, total_score_rank);";
    }

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

    internal static string GetCurrentSchemaSql()
    {
        var statements = new List<string>();

        foreach (var bandType in AllBandTypes)
        {
            var rankingsTable = GetCurrentRankingTable(bandType);
            var statsTable = GetCurrentStatsTable(bandType);

            statements.Add(GetCreateRankingTableSql(rankingsTable, includePrimaryKey: true, ifNotExists: true));
            statements.Add(GetCreateRankingIndexesSql(rankingsTable, ifNotExists: true));
            statements.Add(GetCreateStatsTableSql(statsTable, includePrimaryKey: true, ifNotExists: true));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, statements);
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