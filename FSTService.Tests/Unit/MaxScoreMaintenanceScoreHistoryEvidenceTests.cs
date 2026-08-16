using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

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

    [Theory]
    [InlineData(11_000, 9_000, 0)]
    [InlineData(9_000, 11_000, 1)]
    public async Task Score_history_evidence_applies_overlay_precedence_before_ranking_threshold(
        int snapshotScore,
        int overlayScore,
        long expectedRowCount)
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        SeedEvidenceScope(
            dataSource,
            manifest.ExpectedPublishedScrapeId,
            "precedence-song",
            "Solo_Guitar",
            [
                new(
                    "precedence-account",
                    snapshotScore,
                    overlayScore),
            ]);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
                ["precedence-song"] =
                    CreateMaxScores(("Solo_Guitar", 10_000)),
            };
        InsertEvidenceHistory(
            dataSource,
            "precedence-song",
            "Solo_Guitar",
            "precedence-account",
            10_000,
            new DateTime(
                2026,
                8,
                15,
                14,
                0,
                0,
                DateTimeKind.Utc));

        var (optimized, reference) =
            await ComputeEvidencePairAsync(
                dataSource,
                manifest,
                maxima);

        Assert.Equal(reference, optimized);
        Assert.Equal(expectedRowCount, optimized.RowCount);
    }

    [Fact]
    public async Task Score_history_evidence_excludes_current_scores_equal_to_maximum_or_cutoff()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        SeedEvidenceScope(
            dataSource,
            manifest.ExpectedPublishedScrapeId,
            "player-equality-song",
            "Solo_Bass",
            [
                new("account-base", 10_000, null),
            ]);
        SeedEvidenceScope(
            dataSource,
            manifest.ExpectedPublishedScrapeId,
            "ranking-equality-song",
            "Solo_Guitar",
            [
                new("ranking-equality-account", 10_500, null),
            ]);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
                ["player-equality-song"] =
                    CreateMaxScores(("Solo_Bass", 10_000)),
                ["ranking-equality-song"] =
                    CreateMaxScores(("Solo_Guitar", 10_000)),
            };
        var changedAt = new DateTime(
            2026,
            8,
            15,
            14,
            5,
            0,
            DateTimeKind.Utc);
        InsertEvidenceHistory(
            dataSource,
            "player-equality-song",
            "Solo_Bass",
            "account-base",
            9_000,
            changedAt);
        InsertEvidenceHistory(
            dataSource,
            "ranking-equality-song",
            "Solo_Guitar",
            "ranking-equality-account",
            9_000,
            changedAt.AddSeconds(1));

        var (optimized, reference) =
            await ComputeEvidencePairAsync(
                dataSource,
                manifest,
                maxima);

        Assert.Equal(reference, optimized);
        Assert.Equal(0, optimized.RowCount);
    }

    [Fact]
    public async Task Score_history_evidence_low_changed_score_marks_account_for_other_scope_player_fallback()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        SeedEvidenceScope(
            dataSource,
            manifest.ExpectedPublishedScrapeId,
            "player-fallback-song",
            "Solo_Bass",
            [
                new("account-base", 10_001, null),
            ]);
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
                ["player-fallback-song"] =
                    CreateMaxScores(("Solo_Bass", 10_000)),
            };
        InsertEvidenceHistory(
            dataSource,
            "player-fallback-song",
            "Solo_Bass",
            "account-base",
            10_000,
            new DateTime(
                2026,
                8,
                15,
                14,
                10,
                0,
                DateTimeKind.Utc));

        var optimized = await ComputeOptimizedSnapshotAsync(
            dataSource,
            manifest,
            maxima);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);
        var reference =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceReferenceAsync(
                    manifest,
                    maxima,
                    connection,
                    transaction,
                    CancellationToken.None);

        Assert.Equal(reference, optimized.Evidence);
        Assert.Equal(1, optimized.Evidence.RowCount);
        Assert.Contains(
            "account-base",
            optimized.AffectedPlayerStatsAccounts);
    }

    [Fact]
    public async Task Score_history_evidence_excludes_blank_source_rows_and_rejects_blank_identity()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        using (var connection = dataSource.OpenConnection())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    @scrapeId,
                    'song-a',
                    'Solo_Guitar',
                    '',
                    1,
                    now(),
                    now())
                """;
            seed.Parameters.AddWithValue(
                "scrapeId",
                manifest.ExpectedPublishedScrapeId);
            Assert.Equal(1, seed.ExecuteNonQuery());
        }
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
            };

        var optimized = await ComputeOptimizedSnapshotAsync(
            dataSource,
            manifest,
            maxima);
        await using var referenceConnection =
            await dataSource.OpenConnectionAsync();
        await using var referenceTransaction =
            await referenceConnection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);
        var reference =
            await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceReferenceAsync(
                    manifest,
                    maxima,
                    referenceConnection,
                    referenceTransaction,
                    CancellationToken.None);

        Assert.Equal(reference, optimized.Evidence);
        Assert.DoesNotContain(
            optimized.AffectedPlayerStatsAccounts,
            string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(
            optimized.AffectedRegisteredAccounts,
            string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(
            optimized.OverlayOnlyRegisteredAccounts,
            string.IsNullOrWhiteSpace);

        using (var connection = dataSource.OpenConnection())
        using (var seedIdentity = connection.CreateCommand())
        {
            seedIdentity.CommandText = """
                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at)
                VALUES (
                    'blank-device',
                    '',
                    now())
                """;
            Assert.Equal(
                1,
                seedIdentity.ExecuteNonQuery());
        }

        var identityError =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => ComputeOptimizedSnapshotAsync(
                    dataSource,
                    manifest,
                    maxima));
        Assert.Contains(
            "Blank affected-account source rows cannot be ignored",
            identityError.Message,
            StringComparison.Ordinal);

        using (var connection = dataSource.OpenConnection())
        using (var replaceIdentity = connection.CreateCommand())
        {
            replaceIdentity.CommandText = """
                DELETE FROM registered_users
                WHERE account_id = '';

                INSERT INTO api_response_cache (
                    cache_key,
                    json_data,
                    etag,
                    cached_at)
                VALUES (
                    'player::::',
                    convert_to('{}', 'UTF8'),
                    '"blank-cache"',
                    now());
                """;
            replaceIdentity.ExecuteNonQuery();
        }
        var cacheIdentityError =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => ComputeOptimizedSnapshotAsync(
                    dataSource,
                    manifest,
                    maxima));
        Assert.Contains(
            "cacheRows=1",
            cacheIdentityError.Message,
            StringComparison.Ordinal);

        using (var connection = dataSource.OpenConnection())
        using (var replaceIdentity = connection.CreateCommand())
        {
            replaceIdentity.CommandText = """
                DELETE FROM api_response_cache
                WHERE cache_key = 'player::::';

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
                    '',
                    1,
                    1,
                    now());
                """;
            replaceIdentity.ExecuteNonQuery();
        }
        var historyIdentityError =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => ComputeOptimizedSnapshotAsync(
                    dataSource,
                    manifest,
                    maxima));
        Assert.Contains(
            "historyRows=1",
            historyIdentityError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Score_history_evidence_preserves_overlay_only_classification_and_duplicate_registrations()
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
                VALUES
                    ('overlay-device-a', 'account-overlay', now()),
                    ('overlay-device-b', 'account-overlay', now());

                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    new_score,
                    new_rank,
                    changed_at)
                VALUES (
                    'registered-history-song',
                    'Solo_Drums',
                    'account-overlay',
                    1,
                    1,
                    now());
                """;
            seed.ExecuteNonQuery();
        }

        var optimized = await ComputeOptimizedSnapshotAsync(
            dataSource,
            manifest,
            maxima);

        Assert.Equal(2, optimized.Evidence.RowCount);
        Assert.Equal(
            ["account-overlay"],
            optimized.AffectedRegisteredAccounts);
        Assert.Equal(
            ["account-overlay"],
            optimized.OverlayOnlyRegisteredAccounts);
    }

    [Theory]
    [InlineData("unpublished")]
    [InlineData("wrong-publication")]
    [InlineData("working-publication")]
    [InlineData("non-current-generation")]
    [InlineData("incomplete-source")]
    [InlineData("missing-binding")]
    [InlineData("wrong-binding")]
    [InlineData("zero-sources")]
    public async Task Score_history_evidence_rejects_invalid_publication_source_fences(
        string invalidState)
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        var effectiveManifest = manifest;
        if (invalidState == "wrong-publication")
        {
            effectiveManifest = manifest with
            {
                ExpectedPublicationId =
                    manifest.ExpectedPublicationId + 1,
            };
        }
        else
        {
            using var connection = dataSource.OpenConnection();
            using var mutation = connection.CreateCommand();
            mutation.CommandText = invalidState switch
            {
                "unpublished" => """
                    UPDATE scrape_publication_state
                    SET published_scrape_id = NULL
                    WHERE id = TRUE;
                    """,
                "working-publication" => """
                    INSERT INTO scrape_log (
                        id,
                        started_at,
                        status)
                    VALUES (
                        @workingScrapeId,
                        now(),
                        'running');
                    INSERT INTO publication_generations (
                        publication_id,
                        scrape_id,
                        status,
                        created_at)
                    VALUES (
                        @workingPublicationId,
                        @workingScrapeId,
                        'building',
                        now());
                    UPDATE scrape_publication_state
                    SET working_publication_id =
                            @workingPublicationId
                    WHERE id = TRUE;
                    """,
                "non-current-generation" => """
                    UPDATE publication_generations
                    SET status = 'retained'
                    WHERE publication_id =
                              @publicationId;
                    """,
                "incomplete-source" => """
                    UPDATE leaderboard_published_scope_source
                    SET is_complete = FALSE
                    WHERE published_scrape_id = @scrapeId;
                    """,
                "missing-binding" => """
                    DELETE FROM publication_surface_bindings
                    WHERE publication_id = @publicationId
                      AND surface_name =
                              'solo_scope_sources';
                    """,
                "wrong-binding" => """
                    UPDATE publication_surface_bindings
                    SET binding_json = jsonb_set(
                            binding_json,
                            '{publishedScrapeId}',
                            to_jsonb(
                                (@scrapeId + 1)::BIGINT))
                    WHERE publication_id = @publicationId
                      AND surface_name =
                              'solo_scope_sources';
                    """,
                "zero-sources" => """
                    DELETE FROM leaderboard_published_scope_source
                    WHERE published_scrape_id = @scrapeId;
                    UPDATE publication_surface_bindings
                    SET row_count = 0
                    WHERE publication_id = @publicationId
                      AND surface_name =
                              'solo_scope_sources';
                    """,
                _ => throw new InvalidOperationException(
                    $"Unknown invalid state '{invalidState}'."),
            };
            mutation.Parameters.AddWithValue(
                "scrapeId",
                manifest.ExpectedPublishedScrapeId);
            mutation.Parameters.AddWithValue(
                "publicationId",
                manifest.ExpectedPublicationId);
            mutation.Parameters.AddWithValue(
                "workingScrapeId",
                manifest.ExpectedPublishedScrapeId + 1);
            mutation.Parameters.AddWithValue(
                "workingPublicationId",
                manifest.ExpectedPublicationId + 1);
            mutation.ExecuteNonQuery();
        }
        var maxima =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
            };

        await using var evidenceConnection =
            await dataSource.OpenConnectionAsync();
        await using var evidenceTransaction =
            await evidenceConnection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                .ComputeAsync(
                    effectiveManifest,
                    maxima,
                    evidenceConnection,
                    evidenceTransaction,
                    CancellationToken.None,
                    ScraperOptions
                        .DefaultMaxScoreMaintenanceCommandTimeoutSeconds));
    }

    [Fact]
    public async Task Score_history_snapshot_probe_plan_uses_partition_score_index_without_broad_scan()
    {
        Assert.DoesNotContain(
            "fst_max_score_evidence_candidates",
            MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                .SelectorTableNames);
        foreach (var probeSql in new[]
                 {
                     MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                         .RankingSnapshotProbeSql,
                     MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                         .PlayerSnapshotProbeSql,
                 })
        {
            Assert.DoesNotContain(
                "LATERAL",
                probeSql,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "snapshot.snapshot_id = @sourceSnapshotId",
                probeSql,
                StringComparison.Ordinal);
            Assert.Contains(
                "snapshot.score > @scoreThreshold",
                probeSql,
                StringComparison.Ordinal);
            Assert.Contains(
                "ORDER BY snapshot.score DESC",
                probeSql,
                StringComparison.Ordinal);
        }

        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText = """
                CREATE TEMP TABLE
                    fst_max_score_evidence_fallback_scopes (
                        song_id TEXT NOT NULL,
                        instrument TEXT NOT NULL,
                        account_id TEXT NOT NULL,
                        max_threshold INTEGER NOT NULL,
                        PRIMARY KEY (
                            song_id,
                            instrument,
                            account_id)
                    ) ON COMMIT DROP;

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
                SELECT 1296,
                       'probe-song',
                       'Solo_Guitar',
                       'probe-account-' || value,
                       CASE
                           WHEN value = 1 THEN 11_000
                           ELSE 9_000
                       END,
                       value,
                       'scrape',
                       now(),
                       now()
                FROM generate_series(1, 2000) value;
                ANALYZE
                    leaderboard_entries_snapshot_solo_guitar;
                SET LOCAL enable_seqscan = off;
                SET LOCAL enable_bitmapscan = off;
                SET LOCAL plan_cache_mode =
                    force_generic_plan;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var preparedProbeSql =
            MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                .RankingSnapshotProbeSql
                .Replace(
                    "@sourceSnapshotId",
                    "$1",
                    StringComparison.Ordinal)
                .Replace(
                    "@songId",
                    "$2",
                    StringComparison.Ordinal)
                .Replace(
                    "@instrument",
                    "$3",
                    StringComparison.Ordinal)
                .Replace(
                    "@scoreThreshold",
                    "$4",
                    StringComparison.Ordinal)
                .Replace(
                    "@maxThreshold",
                    "$5",
                    StringComparison.Ordinal);
        await using (var prepare = connection.CreateCommand())
        {
            prepare.Transaction = transaction;
            prepare.CommandText = """
                PREPARE fst_score_history_probe(
                    BIGINT,
                    TEXT,
                    TEXT,
                    INTEGER,
                    INTEGER) AS
                """
                + "\n"
                + preparedProbeSql;
            await prepare.ExecuteNonQueryAsync();
        }

        await using var explain = connection.CreateCommand();
        explain.Transaction = transaction;
        explain.CommandText = """
            EXPLAIN (FORMAT JSON, COSTS OFF)
            EXECUTE fst_score_history_probe(
                1296,
                'probe-song',
                'Solo_Guitar',
                10500,
                10500);
            """;
        var planJson =
            (string)(await explain.ExecuteScalarAsync())!;
        using var plan = JsonDocument.Parse(planJson);
        var nodes = EnumeratePlanNodes(
                plan.RootElement)
            .ToArray();
        var snapshotNodes = nodes
            .Where(node =>
                node.TryGetProperty(
                    "Relation Name",
                    out var relation)
                && relation.GetString()?
                    .StartsWith(
                        "leaderboard_entries_snapshot",
                        StringComparison.Ordinal)
                    == true)
            .ToArray();

        Assert.NotEmpty(snapshotNodes);
        Assert.All(
            snapshotNodes,
            node => Assert.DoesNotContain(
                "Seq Scan",
                node.GetProperty("Node Type").GetString(),
                StringComparison.Ordinal));
        Assert.All(
            snapshotNodes,
            node => Assert.Contains(
                "solo_guitar",
                node.GetProperty("Relation Name").GetString(),
                StringComparison.Ordinal));
        var scoreIndexNode = Assert.Single(
            nodes
                .Where(node =>
                    node.TryGetProperty(
                        "Index Name",
                        out _)
                    && node.TryGetProperty(
                        "Index Cond",
                        out var condition)
                    && condition.GetString()?
                        .Contains(
                            "snapshot_id",
                            StringComparison.Ordinal)
                        == true
                    && condition.GetString()?
                        .Contains(
                            "song_id",
                            StringComparison.Ordinal)
                        == true
                    && condition.GetString()?
                        .Contains(
                            "score",
                            StringComparison.Ordinal)
                        == true)
                .DistinctBy(node =>
                    node.GetProperty(
                        "Index Name").GetString()));
        await using var indexDefinition =
            connection.CreateCommand();
        indexDefinition.Transaction = transaction;
        indexDefinition.CommandText = """
            SELECT pg_get_indexdef(indexrelid)
            FROM pg_index
            JOIN pg_class
              ON pg_class.oid = indexrelid
            WHERE pg_class.relname = @indexName;
            """;
        indexDefinition.Parameters.AddWithValue(
            "indexName",
            scoreIndexNode.GetProperty(
                "Index Name").GetString()!);
        var definition =
            (string)(await indexDefinition
                .ExecuteScalarAsync())!;
        Assert.Contains(
            "(snapshot_id, song_id, instrument, score DESC)",
            definition,
            StringComparison.Ordinal);
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

    private static async Task<
        MaxScoreMaintenanceScoreHistorySnapshot>
        ComputeOptimizedSnapshotAsync(
            NpgsqlDataSource dataSource,
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyDictionary<string, SongMaxScores> maxima)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);
        return await
            MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                .ComputeAsync(
                    manifest,
                    maxima,
                    connection,
                    transaction,
                    CancellationToken.None,
                    ScraperOptions
                        .DefaultMaxScoreMaintenanceCommandTimeoutSeconds);
    }

    private static IEnumerable<JsonElement> EnumeratePlanNodes(
        JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Node Type", out _))
                yield return element;
            foreach (var property in element
                         .EnumerateObject())
            {
                foreach (var nested in EnumeratePlanNodes(
                             property.Value))
                {
                    yield return nested;
                }
            }
            yield break;
        }
        if (element.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in element.EnumerateArray())
        {
            foreach (var nested in EnumeratePlanNodes(item))
                yield return nested;
        }
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

                UPDATE publication_surface_bindings binding
                SET row_count = (
                    SELECT COUNT(*)
                    FROM leaderboard_published_scope_source source
                    WHERE source.published_scrape_id =
                              @scrapeId
                      AND source.scope_kind = 'alltime'
                )
                FROM scrape_publication_state state
                WHERE state.id = TRUE
                  AND state.published_scrape_id = @scrapeId
                  AND binding.publication_id =
                          state.current_publication_id
                  AND binding.surface_name =
                          'solo_scope_sources';
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
