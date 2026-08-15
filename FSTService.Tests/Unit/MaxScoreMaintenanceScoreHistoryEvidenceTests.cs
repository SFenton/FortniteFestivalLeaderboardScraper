using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Tests.Unit;

public sealed partial class MaxScoreMaintenancePersistenceTests
{
    [Fact]
    public void Score_history_row_hash_matches_legacy_postgresql_golden_values()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH source(
                id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at) AS (
                VALUES
                    (
                        101::INTEGER,
                        'registered-song'::TEXT,
                        'Solo_Drums'::TEXT,
                        'registered-account'::TEXT,
                        NULL::INTEGER,
                        -17::INTEGER,
                        NULL::INTEGER,
                        -3::INTEGER,
                        NULL::INTEGER,
                        NULL::BOOLEAN,
                        NULL::INTEGER,
                        NULL::REAL,
                        NULL::INTEGER,
                        NULL::TIMESTAMPTZ,
                        NULL::INTEGER,
                        (-2147483647 - 1)::INTEGER,
                        NULL::INTEGER,
                        '1969-12-31 23:59:59.999999+00'
                            ::TIMESTAMPTZ
                    ),
                    (
                        202::INTEGER,
                        'song-a'::TEXT,
                        'Solo_Guitar'::TEXT,
                        'account-overlay'::TEXT,
                        -123456::INTEGER,
                        50000::INTEGER,
                        -9::INTEGER,
                        7::INTEGER,
                        -100::INTEGER,
                        FALSE::BOOLEAN,
                        -5::INTEGER,
                        -12.5::REAL,
                        -2::INTEGER,
                        '2026-08-15 12:34:56.123456+00'
                            ::TIMESTAMPTZ,
                        -6::INTEGER,
                        8::INTEGER,
                        -10::INTEGER,
                        '2038-01-19 03:14:07.654321+00'
                            ::TIMESTAMPTZ
                    )
            ), canonical AS (
                SELECT id,
                       jsonb_build_array(
                           id,
                           song_id,
                           instrument,
                           account_id,
                           old_score,
                           new_score,
                           old_rank,
                           new_rank,
                           accuracy,
                           is_full_combo,
                           stars,
                           percentile,
                           season,
                           CASE
                               WHEN score_achieved_at IS NULL
                                   THEN NULL
                               ELSE (
                                   EXTRACT(
                                       EPOCH FROM
                                           score_achieved_at)
                                   * 1000000)::BIGINT
                           END,
                           season_rank,
                           all_time_rank,
                           difficulty,
                           (
                               EXTRACT(
                                   EPOCH FROM changed_at)
                               * 1000000)::BIGINT)::TEXT
                           AS row_identity
                FROM source
            )
            SELECT id,
                   row_identity,
                   hashtextextended(row_identity, 0),
                   hashtextextended(row_identity, 1)
            FROM canonical
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<LegacyRowHashGolden>();
        while (reader.Read())
        {
            rows.Add(new LegacyRowHashGolden(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        Assert.Equal(
            [
                new LegacyRowHashGolden(
                    101,
                    "[101, \"registered-song\", \"Solo_Drums\", \"registered-account\", null, -17, null, -3, null, null, null, null, null, null, null, -2147483648, null, -1]",
                    -6642989747900786470,
                    8880190386365305163),
                new LegacyRowHashGolden(
                    202,
                    "[202, \"song-a\", \"Solo_Guitar\", \"account-overlay\", -123456, 50000, -9, 7, -100, false, -5, -12.5, -2, 1786797296123456, -6, 8, -10, 2147483647654321]",
                    -7476338321732978398,
                    -2244904219321693245),
            ],
            rows);
    }

    [Fact]
    public async Task Score_history_evidence_matches_legacy_postgresql_golden_fingerprint()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 60_000);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
            };
        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at)
                VALUES
                    (
                        'golden-device-a',
                        'registered-account',
                        '2026-08-15 00:00:00+00'),
                    (
                        'golden-device-b',
                        'registered-account',
                        '2026-08-15 00:00:00+00');

                INSERT INTO score_history (
                    id,
                    song_id,
                    instrument,
                    account_id,
                    old_score,
                    new_score,
                    old_rank,
                    new_rank,
                    accuracy,
                    is_full_combo,
                    stars,
                    percentile,
                    season,
                    score_achieved_at,
                    season_rank,
                    all_time_rank,
                    difficulty,
                    changed_at)
                VALUES
                    (
                        101,
                        'registered-song',
                        'Solo_Drums',
                        'registered-account',
                        NULL,
                        -17,
                        NULL,
                        -3,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        (-2147483647 - 1),
                        NULL,
                        '1969-12-31 23:59:59.999999+00'
                    ),
                    (
                        202,
                        'song-a',
                        'Solo_Guitar',
                        'account-overlay',
                        -123456,
                        50000,
                        -9,
                        7,
                        -100,
                        FALSE,
                        -5,
                        -12.5,
                        -2,
                        '2026-08-15 12:34:56.123456+00',
                        -6,
                        8,
                        -10,
                        '2038-01-19 03:14:07.654321+00'
                    );
                """;
            command.ExecuteNonQuery();
        }

        var (optimized, reference) =
            await ComputeEvidencePairAsync(
                dataSource,
                manifest,
                maxima);

        Assert.Equal(reference, optimized);
        Assert.Equal(
            new MaxScoreMaintenanceScoreHistoryEvidence(
                3,
                101,
                202,
                DateTime.UnixEpoch.AddTicks(-10),
                new DateTime(
                        2038,
                        1,
                        19,
                        3,
                        14,
                        7,
                        DateTimeKind.Utc)
                    .AddTicks(6_543_210),
                "ab59b91303bfdf3865f68e5909f49c9eff55573fbc4c06417666231376b22097"),
            optimized);
    }

    [Fact]
    public async Task Score_history_evidence_preserves_master_consumption_predicates()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
            };

        AddScope(
            "player-other-instrument",
            "Solo_Bass",
            [
                new("account-base", 10_001, null),
            ]);
        AddScope(
            "player-equal-maximum",
            "Solo_Bass",
            [
                new("account-overlay", 10_000, null),
            ]);
        AddScope(
            "ranking-other-song",
            "Solo_Guitar",
            [
                new("ranking-account", 10_501, null),
            ]);
        AddScope(
            "ranking-equal-threshold",
            "Solo_Guitar",
            [
                new("ranking-equal", 10_500, null),
            ]);
        AddScope(
            "overlap-song",
            "Solo_Guitar",
            [
                new("account-base", 10_501, null),
            ]);
        AddScope(
            "overlay-high",
            "Solo_Guitar",
            [
                new("overlay-high-account", 9_000, 11_000),
            ]);
        AddScope(
            "overlay-low",
            "Solo_Guitar",
            [
                new("overlay-low-account", 11_000, 9_000),
            ]);

        using (var connection = dataSource.OpenConnection())
        using (var registrations = connection.CreateCommand())
        {
            registrations.CommandText = """
                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at)
                VALUES
                    ('device-a', 'registered-anywhere', now()),
                    ('device-b', 'registered-anywhere', now());
                """;
            registrations.ExecuteNonQuery();
        }

        var changedAt = new DateTime(
            2026,
            8,
            15,
            12,
            0,
            0,
            DateTimeKind.Utc);
        InsertHistory(
            "outside-published-scope",
            "Solo_Vocals",
            "registered-anywhere",
            1,
            changedAt.AddSeconds(1));
        InsertHistory(
            "player-other-instrument",
            "Solo_Bass",
            "account-base",
            10_500,
            changedAt.AddSeconds(2));
        InsertHistory(
            "player-other-instrument",
            "Solo_Bass",
            "account-base",
            10_501,
            changedAt.AddSeconds(3));
        InsertHistory(
            "player-equal-maximum",
            "Solo_Bass",
            "account-overlay",
            9_000,
            changedAt.AddSeconds(4));
        InsertHistory(
            "ranking-other-song",
            "Solo_Guitar",
            "ranking-account",
            10_500,
            changedAt.AddSeconds(5));
        InsertHistory(
            "ranking-other-song",
            "Solo_Guitar",
            "ranking-account",
            10_501,
            changedAt.AddSeconds(6));
        InsertHistory(
            "ranking-equal-threshold",
            "Solo_Guitar",
            "ranking-equal",
            9_000,
            changedAt.AddSeconds(7));
        InsertHistory(
            "overlap-song",
            "Solo_Guitar",
            "account-base",
            10_000,
            changedAt.AddSeconds(8));
        InsertHistory(
            "overlay-high",
            "Solo_Guitar",
            "overlay-high-account",
            10_000,
            changedAt.AddSeconds(9));
        InsertHistory(
            "overlay-low",
            "Solo_Guitar",
            "overlay-low-account",
            10_000,
            changedAt.AddSeconds(10));

        var (optimized, reference) =
            await ComputeEvidencePairAsync(
                dataSource,
                manifest,
                maxima);

        Assert.Equal(reference, optimized);
        Assert.Equal(6, optimized.RowCount);

        return;

        void AddScope(
            string songId,
            string instrument,
            IReadOnlyList<EvidenceCurrentRow> rows)
        {
            SeedEvidenceScope(
                dataSource,
                manifest.ExpectedPublishedScrapeId,
                songId,
                instrument,
                rows);
            maxima[songId] =
                CreateMaxScores((instrument, 10_000));
        }

        void InsertHistory(
            string songId,
            string instrument,
            string accountId,
            int newScore,
            DateTime rowChangedAt)
            => InsertEvidenceHistory(
                dataSource,
                songId,
                instrument,
                accountId,
                newScore,
                rowChangedAt);
    }

    [Fact]
    public async Task Score_history_evidence_matches_pre_optimization_sql_on_randomized_fixture()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
            };
        var random = new Random(20_260_815);
        var accounts = Enumerable.Range(0, 24)
            .Select(index => $"random-account-{index:00}")
            .ToArray();

        SeedEvidenceCurrentRows(
            dataSource,
            manifest.ExpectedPublishedScrapeId,
            "song-a",
            "Solo_Guitar",
            accounts.Take(8)
                .Select((accountId, index) =>
                    new EvidenceCurrentRow(
                        accountId,
                        35_000 + index * 2_000,
                        index % 3 == 0
                            ? 55_000 + index
                            : null))
                .ToArray());

        var randomSongs = Enumerable.Range(0, 6)
            .Select(index => $"random-song-{index:00}")
            .ToArray();
        var instruments = new[]
        {
            "Solo_Guitar",
            "Solo_Bass",
            "Solo_Drums",
        };
        foreach (var songId in randomSongs)
        {
            var scores = new SongMaxScores();
            foreach (var instrument in instruments)
            {
                scores.SetByInstrument(instrument, 10_000);
                var rows = accounts
                    .Where((_, index) =>
                        index == 0
                        || random.NextDouble() < 0.65)
                    .Select((accountId, index) =>
                    {
                        var snapshotScore =
                            random.Next(7_500, 12_500);
                        int? overlayScore =
                            random.NextDouble() < 0.3
                                ? random.Next(7_500, 12_500)
                                : null;
                        return new EvidenceCurrentRow(
                            accountId,
                            snapshotScore,
                            overlayScore,
                            index % 4);
                    })
                    .ToArray();
                SeedEvidenceScope(
                    dataSource,
                    manifest.ExpectedPublishedScrapeId,
                    songId,
                    instrument,
                    rows);
            }
            maxima[songId] = scores;
        }

        using (var connection = dataSource.OpenConnection())
        using (var registrations = connection.CreateCommand())
        {
            registrations.CommandText = """
                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at)
                SELECT 'device-primary',
                       account_id,
                       now()
                FROM unnest(@accountIds::TEXT[]) account_id;

                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at)
                SELECT 'device-secondary',
                       account_id,
                       now()
                FROM unnest(@duplicateAccountIds::TEXT[]) account_id;
                """;
            registrations.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                accounts.Where((_, index) => index % 4 == 0)
                    .ToArray();
            registrations.Parameters.Add(
                "duplicateAccountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                accounts.Where((_, index) => index % 8 == 0)
                    .ToArray();
            registrations.ExecuteNonQuery();
        }

        var historySongs = randomSongs
            .Append("song-a")
            .Append("unpublished-random-song")
            .ToArray();
        var historySongIds = new string[600];
        var historyInstruments = new string[600];
        var historyAccounts = new string[600];
        var historyScores = new int[600];
        var historyRanks = new int[600];
        var historyAccuracies = new int[600];
        var historyChangedAt = new DateTime[600];
        var historyAchievedAt = new DateTime[600];
        var historyBase = new DateTime(
            2026,
            8,
            15,
            13,
            0,
            0,
            DateTimeKind.Utc);
        for (var index = 0; index < historySongIds.Length; index++)
        {
            historySongIds[index] =
                historySongs[random.Next(historySongs.Length)];
            historyInstruments[index] =
                instruments[random.Next(instruments.Length)];
            historyAccounts[index] =
                accounts[random.Next(accounts.Length)];
            historyScores[index] =
                random.Next(-60_000, 60_001);
            historyRanks[index] =
                random.Next(-500, 501);
            historyAccuracies[index] =
                random.Next(80, 101);
            historyChangedAt[index] =
                historyBase.AddSeconds(index);
            historyAchievedAt[index] =
                historyBase.AddDays(-1).AddSeconds(index);
        }
        using (var connection = dataSource.OpenConnection())
        using (var history = connection.CreateCommand())
        {
            history.CommandText = """
                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    new_score,
                    new_rank,
                    accuracy,
                    score_achieved_at,
                    changed_at)
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[],
                    @accountIds::TEXT[],
                    @scores::INTEGER[],
                    @ranks::INTEGER[],
                    @accuracies::INTEGER[],
                    @achievedAt::TIMESTAMPTZ[],
                    @changedAt::TIMESTAMPTZ[]);
                """;
            history.Parameters.Add(
                "songIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                historySongIds;
            history.Parameters.Add(
                "instruments",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                historyInstruments;
            history.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                historyAccounts;
            history.Parameters.Add(
                "scores",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                historyScores;
            history.Parameters.Add(
                "ranks",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                historyRanks;
            history.Parameters.Add(
                "accuracies",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                historyAccuracies;
            history.Parameters.Add(
                "achievedAt",
                NpgsqlDbType.Array
                | NpgsqlDbType.TimestampTz).Value =
                historyAchievedAt;
            history.Parameters.Add(
                "changedAt",
                NpgsqlDbType.Array
                | NpgsqlDbType.TimestampTz).Value =
                historyChangedAt;
            history.ExecuteNonQuery();
        }

        var (optimized, reference) =
            await ComputeEvidencePairAsync(
                dataSource,
                manifest,
                maxima);

        Assert.Equal(reference, optimized);
        Assert.True(optimized.RowCount > 0);
    }

    [Fact]
    public async Task Score_history_evidence_timeout_and_cancellation_cleanup_support_repeated_invocation()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
            };
        using (var connection = dataSource.OpenConnection())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at)
                VALUES ('device-a', 'account-base', now());

                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    new_score,
                    new_rank,
                    changed_at)
                VALUES (
                    'song-a',
                    'Solo_Guitar',
                    'account-base',
                    40000,
                    1,
                    now());
                """;
            seed.ExecuteNonQuery();
        }

        await using var evidenceConnection =
            await dataSource.OpenConnectionAsync();
        await using var evidenceTransaction =
            await evidenceConnection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);

        await using (var blocker =
                     await AcquireScoreHistoryBlockerAsync(
                         dataSource))
        {
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                () => MaxScoreMaintenanceService
                    .ComputeScoreHistoryEvidenceAsync(
                        manifest,
                        maxima,
                        evidenceConnection,
                        evidenceTransaction,
                        cancellation.Token,
                        commandTimeoutSeconds: 30));
        }
        await AssertScoreHistorySelectorsDroppedAsync(
            evidenceConnection,
            evidenceTransaction);

        await using (var blocker =
                     await AcquireScoreHistoryBlockerAsync(
                         dataSource))
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => MaxScoreMaintenanceService
                    .ComputeScoreHistoryEvidenceAsync(
                        manifest,
                        maxima,
                        evidenceConnection,
                        evidenceTransaction,
                        CancellationToken.None,
                        commandTimeoutSeconds: 1));
        }
        await AssertScoreHistorySelectorsDroppedAsync(
            evidenceConnection,
            evidenceTransaction);

        var first =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceAsync(
                    manifest,
                    maxima,
                    evidenceConnection,
                    evidenceTransaction,
                    CancellationToken.None);
        var second =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceAsync(
                    manifest,
                    maxima,
                    evidenceConnection,
                    evidenceTransaction,
                    CancellationToken.None);
        var reference =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceReferenceAsync(
                    manifest,
                    maxima,
                    evidenceConnection,
                    evidenceTransaction,
                    CancellationToken.None);

        Assert.Equal(reference, first);
        Assert.Equal(first, second);
        await AssertScoreHistorySelectorsDroppedAsync(
            evidenceConnection,
            evidenceTransaction);
    }

    private static async Task<(
        MaxScoreMaintenanceScoreHistoryEvidence Optimized,
        MaxScoreMaintenanceScoreHistoryEvidence Reference)>
        ComputeEvidencePairAsync(
            NpgsqlDataSource dataSource,
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyDictionary<string, SongMaxScores> maxima)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);
        var optimized =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceAsync(
                    manifest,
                    maxima,
                    connection,
                    transaction,
                    CancellationToken.None);
        var reference =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceReferenceAsync(
                    manifest,
                    maxima,
                    connection,
                    transaction,
                    CancellationToken.None);
        return (optimized, reference);
    }

    private static SongMaxScores CreateMaxScores(
        params (string Instrument, int Maximum)[] maxima)
    {
        var result = new SongMaxScores();
        foreach (var (instrument, maximum) in maxima)
            result.SetByInstrument(instrument, maximum);
        return result;
    }

    private static void SeedEvidenceScope(
        NpgsqlDataSource dataSource,
        long scrapeId,
        string songId,
        string instrument,
        IReadOnlyList<EvidenceCurrentRow> rows)
    {
        var snapshotCount =
            rows.Count(row => row.SnapshotScore.HasValue);
        Assert.True(snapshotCount > 0);
        using (var connection = dataSource.OpenConnection())
        using (var source = connection.CreateCommand())
        {
            source.CommandText = """
                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at)
                VALUES (
                    @scrapeId,
                    @songId,
                    @instrument,
                    'alltime',
                    'snapshot',
                    @scrapeId,
                    @scrapeId,
                    @rowCount,
                    md5(@songId || ':' || @instrument),
                    md5(@songId || ':' || @instrument || ':coverage'),
                    @rowCount,
                    1,
                    TRUE,
                    now(),
                    now());
                """;
            source.Parameters.AddWithValue(
                "scrapeId",
                scrapeId);
            source.Parameters.AddWithValue(
                "songId",
                songId);
            source.Parameters.AddWithValue(
                "instrument",
                instrument);
            source.Parameters.AddWithValue(
                "rowCount",
                snapshotCount);
            source.ExecuteNonQuery();
        }
        SeedEvidenceCurrentRows(
            dataSource,
            scrapeId,
            songId,
            instrument,
            rows);
    }

    private static void SeedEvidenceCurrentRows(
        NpgsqlDataSource dataSource,
        long scrapeId,
        string songId,
        string instrument,
        IReadOnlyList<EvidenceCurrentRow> rows)
    {
        var snapshotRows = rows
            .Where(row => row.SnapshotScore.HasValue)
            .ToArray();
        var overlayRows = rows
            .Where(row => row.OverlayScore.HasValue)
            .ToArray();
        using var connection = dataSource.OpenConnection();
        if (snapshotRows.Length > 0)
        {
            using var snapshots = connection.CreateCommand();
            snapshots.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                SELECT @scrapeId,
                       @songId,
                       @instrument,
                       input.account_id,
                       input.score,
                       input.rank,
                       'scrape',
                       now(),
                       now()
                FROM unnest(
                    @accountIds::TEXT[],
                    @scores::INTEGER[],
                    @ranks::INTEGER[])
                    input(account_id, score, rank);
                """;
            snapshots.Parameters.AddWithValue(
                "scrapeId",
                scrapeId);
            snapshots.Parameters.AddWithValue(
                "songId",
                songId);
            snapshots.Parameters.AddWithValue(
                "instrument",
                instrument);
            snapshots.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                snapshotRows.Select(row => row.AccountId)
                    .ToArray();
            snapshots.Parameters.Add(
                "scores",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                snapshotRows.Select(row =>
                        row.SnapshotScore!.Value)
                    .ToArray();
            snapshots.Parameters.Add(
                "ranks",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                Enumerable.Range(1, snapshotRows.Length)
                    .ToArray();
            snapshots.ExecuteNonQuery();
        }
        if (overlayRows.Length == 0)
            return;

        using var overlays = connection.CreateCommand();
        overlays.CommandText = """
            INSERT INTO leaderboard_entries_overlay (
                song_id,
                instrument,
                account_id,
                score,
                rank,
                source,
                first_seen_at,
                last_updated_at,
                source_priority,
                overlay_reason)
            SELECT @songId,
                   @instrument,
                   input.account_id,
                   input.score,
                   input.rank,
                   'backfill',
                   now(),
                   now(),
                   input.source_priority,
                   'score-history-evidence-test'
            FROM unnest(
                @accountIds::TEXT[],
                @scores::INTEGER[],
                @ranks::INTEGER[],
                @sourcePriorities::INTEGER[])
                input(
                    account_id,
                    score,
                    rank,
                    source_priority);
            """;
        overlays.Parameters.AddWithValue(
            "songId",
            songId);
        overlays.Parameters.AddWithValue(
            "instrument",
            instrument);
        overlays.Parameters.Add(
            "accountIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            overlayRows.Select(row => row.AccountId)
                .ToArray();
        overlays.Parameters.Add(
            "scores",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            overlayRows.Select(row => row.OverlayScore!.Value)
                .ToArray();
        overlays.Parameters.Add(
            "ranks",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            Enumerable.Range(1, overlayRows.Length)
                .ToArray();
        overlays.Parameters.Add(
            "sourcePriorities",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            overlayRows.Select(row => row.SourcePriority)
                .ToArray();
        overlays.ExecuteNonQuery();
    }

    private static void InsertEvidenceHistory(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        string accountId,
        int newScore,
        DateTime changedAt)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO score_history (
                song_id,
                instrument,
                account_id,
                new_score,
                new_rank,
                accuracy,
                score_achieved_at,
                changed_at)
            VALUES (
                @songId,
                @instrument,
                @accountId,
                @newScore,
                1,
                95,
                @scoreAchievedAt,
                @changedAt);
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue(
            "instrument",
            instrument);
        command.Parameters.AddWithValue(
            "accountId",
            accountId);
        command.Parameters.AddWithValue(
            "newScore",
            newScore);
        command.Parameters.AddWithValue(
            "scoreAchievedAt",
            changedAt.AddDays(-1));
        command.Parameters.AddWithValue(
            "changedAt",
            changedAt);
        command.ExecuteNonQuery();
    }

    private static async Task<ScoreHistoryBlocker>
        AcquireScoreHistoryBlockerAsync(
            NpgsqlDataSource dataSource)
    {
        var connection =
            await dataSource.OpenConnectionAsync();
        var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            await using var command =
                connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                LOCK TABLE score_history
                    IN ACCESS EXCLUSIVE MODE;
                """;
            await command.ExecuteNonQueryAsync();
            return new ScoreHistoryBlocker(
                connection,
                transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task
        AssertScoreHistorySelectorsDroppedAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)::INTEGER
            FROM unnest(@tableNames::TEXT[]) table_name
            WHERE to_regclass(
                'pg_temp.' || table_name) IS NOT NULL;
            """;
        command.Parameters.Add(
            "tableNames",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                .SelectorTableNames.ToArray();
        Assert.Equal(
            0,
            Convert.ToInt32(
                await command.ExecuteScalarAsync()));
    }

    private sealed record EvidenceCurrentRow(
        string AccountId,
        int? SnapshotScore,
        int? OverlayScore,
        int SourcePriority = 100);

    private sealed record LegacyRowHashGolden(
        int Id,
        string RowIdentity,
        long SeedZeroHash,
        long SeedOneHash);

    private sealed class ScoreHistoryBlocker(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
