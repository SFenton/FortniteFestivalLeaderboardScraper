using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using Xunit.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class BandCurrentProjectionOptimizationTests(
    ITestOutputHelper output)
{
    [Fact]
    public void BatchedSqlUsesOneMemberStatsPassInsteadOfSeven()
    {
        var baseline =
            BandCurrentProjectionBuilder
                .GetRebuildScopeSqlForTesting(false);
        var candidate =
            BandCurrentProjectionBuilder
                .GetRebuildScopeSqlForTesting(true);

        Assert.Equal(
            BandCurrentProjectionBuilder
                .LegacyMemberStatsAggregationPassesPerRow,
            CountOccurrences(
                baseline,
                "FROM band_member_stats bms"));
        Assert.Equal(
            1,
            CountOccurrences(
                candidate,
                "FROM band_member_stats bms"));
        Assert.DoesNotContain(
            "LEFT JOIN LATERAL",
            baseline,
            StringComparison.Ordinal);
        Assert.Contains(
            "LEFT JOIN LATERAL",
            candidate,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostgreSqlPlanContainsSevenBaselineScansAndOneCandidateScan()
    {
        using var fixture = new InMemoryMetaDatabase();
        _ = Seed(fixture, 1, 2);
        var builder = CreateBuilder(fixture);
        await builder.EnsureSchemaAsync();

        var baselinePlan = await ExplainAsync(
            fixture,
            useCandidate: false);
        var candidatePlan = await ExplainAsync(
            fixture,
            useCandidate: true);

        Assert.Equal(
            BandCurrentProjectionBuilder
                .LegacyMemberStatsAggregationPassesPerRow,
            CountRelationScans(
                baselinePlan.RootElement,
                "band_member_stats"));
        Assert.Equal(
            1,
            CountRelationScans(
                candidatePlan.RootElement,
                "band_member_stats"));
    }

    [Fact]
    public async Task EmptyScopeSetPreservesNoWorkContract()
    {
        using var fixture = new InMemoryMetaDatabase();
        var builder = CreateBuilder(fixture);

        var baseline = await builder.RefreshScopesAsync(
            [],
            ProductionOptions(useCandidate: false));
        var candidate = await builder.RefreshScopesAsync(
            [],
            ProductionOptions(useCandidate: true));

        Assert.Equal(0, baseline.ScopeCount);
        Assert.Equal(0, candidate.ScopeCount);
        Assert.Equal(
            BandCurrentProjectionOperationMetrics.Empty,
            baseline.OperationMetrics);
        Assert.Equal(
            BandCurrentProjectionOperationMetrics.Empty,
            candidate.OperationMetrics);
    }

    [Fact]
    public async Task LargeBoundedScopesPreserveExactOutputAndReduceLookupPasses()
    {
        const int songCount = 64;
        const int teamsPerSong = 32;
        using var baselineFixture = new InMemoryMetaDatabase();
        using var candidateFixture = new InMemoryMetaDatabase();
        var scopes = Seed(
            baselineFixture,
            songCount,
            teamsPerSong);
        _ = Seed(
            candidateFixture,
            songCount,
            teamsPerSong);
        await PrepareMemberStatEdgeCasesAsync(
            baselineFixture);
        await PrepareMemberStatEdgeCasesAsync(
            candidateFixture);

        var baseline = await CreateBuilder(baselineFixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: false));
        var candidate = await CreateBuilder(candidateFixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: true));

        Assert.Equal(songCount, baseline.ScopeCount);
        Assert.Equal(songCount, candidate.ScopeCount);
        Assert.Equal(baseline.InsertedRows, candidate.InsertedRows);
        var baselineStateHash =
            await StateHashAsync(baselineFixture);
        var candidateStateHash =
            await StateHashAsync(candidateFixture);
        Assert.Equal(
            baselineStateHash,
            candidateStateHash);
        Assert.Equal(
            baseline.OperationMetrics!.SuccessfulScopeTransactions,
            candidate.OperationMetrics!.SuccessfulScopeTransactions);
        Assert.Equal(
            baseline.OperationMetrics.SuccessfulScopeCommandExecutions,
            candidate.OperationMetrics.SuccessfulScopeCommandExecutions);
        Assert.Equal(
            baseline.OperationMetrics.SuccessfulScopeRoundTrips,
            candidate.OperationMetrics.SuccessfulScopeRoundTrips);
        Assert.Equal(
            baseline.InsertedRows *
            BandCurrentProjectionBuilder
                .LegacyMemberStatsAggregationPassesPerRow,
            baseline.OperationMetrics.MemberStatsAggregationPasses);
        Assert.Equal(
            candidate.InsertedRows,
            candidate.OperationMetrics.MemberStatsAggregationPasses);

        output.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    scenario = "large-bounded",
                    scopes = songCount,
                    rows = baseline.InsertedRows,
                    baselineStateSha256 =
                        baselineStateHash,
                    candidateStateSha256 =
                        candidateStateHash,
                    baselineElapsedMs =
                        baseline.TotalElapsedMs,
                    candidateElapsedMs =
                        candidate.TotalElapsedMs,
                    elapsedDeltaPercent =
                        Math.Round(
                            (
                                candidate.TotalElapsedMs -
                                baseline.TotalElapsedMs
                            ) /
                            baseline.TotalElapsedMs *
                            100,
                            3),
                    baselineTransactions =
                        baseline.OperationMetrics
                            .SuccessfulScopeTransactions,
                    candidateTransactions =
                        candidate.OperationMetrics
                            .SuccessfulScopeTransactions,
                    baselineCommands =
                        baseline.OperationMetrics
                            .SuccessfulScopeCommandExecutions,
                    candidateCommands =
                        candidate.OperationMetrics
                            .SuccessfulScopeCommandExecutions,
                    baselineRoundTrips =
                        baseline.OperationMetrics
                            .SuccessfulScopeRoundTrips,
                    candidateRoundTrips =
                        candidate.OperationMetrics
                            .SuccessfulScopeRoundTrips,
                    baselineMemberStatsPasses =
                        baseline.OperationMetrics
                            .MemberStatsAggregationPasses,
                    candidateMemberStatsPasses =
                        candidate.OperationMetrics
                            .MemberStatsAggregationPasses,
                    memberStatsPassDeltaPercent =
                        Math.Round(
                            (
                                candidate.OperationMetrics
                                    .MemberStatsAggregationPasses -
                                baseline.OperationMetrics
                                    .MemberStatsAggregationPasses
                            ) /
                            (double)baseline.OperationMetrics
                                .MemberStatsAggregationPasses *
                            100,
                            3),
                }));
    }

    [Fact]
    public async Task AllUnchangedScopesProduceNoCandidateWrites()
    {
        using var baselineFixture = new InMemoryMetaDatabase();
        using var candidateFixture = new InMemoryMetaDatabase();
        var scopes = Seed(baselineFixture, 4, 4);
        _ = Seed(candidateFixture, 4, 4);
        await PrimeAsync(baselineFixture, scopes);
        await PrimeAsync(candidateFixture, scopes);
        var beforeBaseline =
            await StateHashAsync(baselineFixture);
        var beforeCandidate =
            await StateHashAsync(candidateFixture);

        var baseline = await CreateBuilder(baselineFixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: false));
        var candidate = await CreateBuilder(candidateFixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: true));

        Assert.Equal(0, baseline.ScopeCount);
        Assert.Equal(0, candidate.ScopeCount);
        Assert.Equal(0, baseline.InsertedRows);
        Assert.Equal(0, candidate.InsertedRows);
        Assert.Equal(
            BandCurrentProjectionOperationMetrics.Empty,
            baseline.OperationMetrics);
        Assert.Equal(
            BandCurrentProjectionOperationMetrics.Empty,
            candidate.OperationMetrics);
        Assert.Equal(
            beforeBaseline,
            await StateHashAsync(baselineFixture));
        Assert.Equal(
            beforeCandidate,
            await StateHashAsync(candidateFixture));
        Assert.Equal(beforeBaseline, beforeCandidate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ChangedScopeFilteringPreservesOneAndMixedScopeParity(
        int changedScopes)
    {
        using var baselineFixture = new InMemoryMetaDatabase();
        using var candidateFixture = new InMemoryMetaDatabase();
        var scopes = Seed(baselineFixture, 6, 4);
        _ = Seed(candidateFixture, 6, 4);
        await PrimeAsync(baselineFixture, scopes);
        await PrimeAsync(candidateFixture, scopes);
        var changedSongIds = scopes
            .Take(changedScopes)
            .Select(static scope => scope.SongId)
            .ToArray();
        await MarkSourceChangedAsync(
            baselineFixture,
            changedSongIds);
        await MarkSourceChangedAsync(
            candidateFixture,
            changedSongIds);

        var baseline = await CreateBuilder(baselineFixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: false));
        var candidate = await CreateBuilder(candidateFixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: true));

        Assert.Equal(changedScopes, baseline.ScopeCount);
        Assert.Equal(changedScopes, candidate.ScopeCount);
        Assert.Equal(
            await StateHashAsync(baselineFixture),
            await StateHashAsync(candidateFixture));
        Assert.Equal(
            baseline.OperationMetrics!
                .MemberStatsAggregationPasses,
            candidate.OperationMetrics!
                .MemberStatsAggregationPasses *
            BandCurrentProjectionBuilder
                .LegacyMemberStatsAggregationPassesPerRow);
    }

    [Fact]
    public async Task CandidateFailureRollsBackAndRetryPublishes()
    {
        using var fixture = new InMemoryMetaDatabase();
        var scopes = Seed(fixture, 1, 4);
        await CreateInsertFailureTriggerAsync(fixture);
        var builder = CreateBuilder(fixture);

        var failed = await builder.RefreshScopesAsync(
            scopes,
            ProductionOptions(useCandidate: true));

        Assert.Equal(1, failed.FailedScopes);
        Assert.False(failed.PublishResult.Published);
        Assert.Equal(
            "failed",
            await ScopeStatusAsync(
                fixture,
                scopes[0]));
        Assert.Equal(0, await ProjectionRowCountAsync(fixture));

        await DropInsertFailureTriggerAsync(fixture);
        var retry = await builder.RefreshScopesAsync(
            scopes,
            ProductionOptions(useCandidate: true));

        Assert.Equal(0, retry.FailedScopes);
        Assert.True(retry.PublishResult.Published);
        Assert.Equal(
            "ready",
            await ScopeStatusAsync(
                fixture,
                scopes[0]));
        Assert.True(await ProjectionRowCountAsync(fixture) > 0);
    }

    [Fact]
    public async Task PreCancelledCandidateLeavesNoProjectionState()
    {
        using var fixture = new InMemoryMetaDatabase();
        var scopes = Seed(fixture, 1, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateBuilder(fixture).RefreshScopesAsync(
                scopes,
                ProductionOptions(useCandidate: true),
                cancellation.Token));

        Assert.Equal(0, await ProjectionRowCountAsync(fixture));
        Assert.Null(await ScopeStatusAsync(fixture, scopes[0]));
    }

    private static BandCurrentProjectionRebuildOptions
        ProductionOptions(
            bool useCandidate,
            bool skipUnchanged = true) =>
        new()
        {
            DisableSynchronousCommit = true,
            SkipUnchangedScopes = skipUnchanged,
            MaxParallelBandTypes = 2,
            CandidateCleanupBatchSize = 100_000,
            CandidateCleanupMaxBatches = 100,
            PublishOnSuccess = true,
            IncludeOverallScopes = true,
            IncludeComboScopes = true,
            UseBatchedMemberStatsAggregation =
                useCandidate,
        };

    private static BandCurrentProjectionBuilder CreateBuilder(
        InMemoryMetaDatabase fixture) =>
        new(
            fixture.DataSource,
            Substitute.For<
                ILogger<BandCurrentProjectionBuilder>>());

    private static async Task<JsonDocument> ExplainAsync(
        InMemoryMetaDatabase fixture,
        bool useCandidate)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "EXPLAIN (FORMAT JSON) " +
            BandCurrentProjectionBuilder
                .GetRebuildScopeSqlForTesting(useCandidate);
        command.Parameters.AddWithValue("songId", "song-000");
        command.Parameters.AddWithValue(
            "bandType",
            "Band_Duets");
        command.Parameters.AddWithValue(
            "rankingScope",
            "overall");
        command.Parameters.AddWithValue(
            "scopeComboId",
            string.Empty);
        command.Parameters.AddWithValue(
            "expectedMembers",
            2);
        command.Parameters.AddWithValue(
            "generation",
            1L);
        command.Parameters.AddWithValue(
            "now",
            DateTime.UtcNow);
        var json = Assert.IsType<string>(
            await command.ExecuteScalarAsync());
        return JsonDocument.Parse(json);
    }

    private static int CountRelationScans(
        JsonElement element,
        string relation)
    {
        var count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(
                    "Relation Name",
                    out var name) &&
                string.Equals(
                    name.GetString(),
                    relation,
                    StringComparison.Ordinal))
            {
                count++;
            }
            foreach (var property in element.EnumerateObject())
                count += CountRelationScans(
                    property.Value,
                    relation);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                count += CountRelationScans(
                    child,
                    relation);
        }
        return count;
    }

    private static IReadOnlyList<BandCurrentProjectionScopeKey> Seed(
        InMemoryMetaDatabase fixture,
        int songCount,
        int teamsPerSong)
    {
        var persistence = new BandLeaderboardPersistence(
            fixture.DataSource,
            Substitute.For<
                ILogger<BandLeaderboardPersistence>>());
        var scopes =
            new List<BandCurrentProjectionScopeKey>(
                songCount);
        for (var songIndex = 0;
             songIndex < songCount;
             songIndex++)
        {
            var songId = $"song-{songIndex:D3}";
            var entries =
                Enumerable.Range(0, teamsPerSong)
                    .Select(teamIndex =>
                        Entry(
                            songIndex,
                            teamIndex))
                    .ToArray();
            persistence.UpsertBandEntries(
                songId,
                "Band_Duets",
                entries);
            scopes.Add(
                new BandCurrentProjectionScopeKey(
                    songId,
                    "Band_Duets",
                    "overall",
                    string.Empty));
        }
        return scopes;
    }

    private static async Task PrepareMemberStatEdgeCasesAsync(
        InMemoryMetaDatabase fixture)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE band_member_stats
            SET instrument_id = NULL,
                score = NULL,
                accuracy = NULL,
                is_full_combo = NULL,
                stars = NULL,
                difficulty = NULL
            WHERE song_id = 'song-000'
              AND team_key =
                  'account-000-000-a:account-000-000-b'
              AND instrument_combo = '0:1'
              AND member_index = 0;

            DELETE FROM band_member_stats
            WHERE song_id = 'song-000'
              AND team_key =
                  'account-000-001-a:account-000-001-b'
              AND instrument_combo = '0:3';
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static BandLeaderboardEntry Entry(
        int songIndex,
        int teamIndex)
    {
        var members = new[]
        {
            $"account-{songIndex:D3}-{teamIndex:D3}-a",
            $"account-{songIndex:D3}-{teamIndex:D3}-b",
        };
        return new BandLeaderboardEntry
        {
            TeamKey = string.Join(
                ':',
                members.Order(
                    StringComparer.Ordinal)),
            TeamMembers = members,
            InstrumentCombo = teamIndex % 2 == 0
                ? "0:1"
                : "0:3",
            Score = 1_000_000 - teamIndex,
            Accuracy = 990_000 - teamIndex,
            IsFullCombo = teamIndex % 3 == 0,
            Stars = 5,
            Difficulty = 3,
            Season = 1,
            Rank = teamIndex + 1,
            Percentile = (teamIndex + 1d) / 100d,
            EndTime =
                $"2026-08-16T00:{teamIndex % 60:D2}:00Z",
            Source = "test",
            MemberStats =
            [
                Member(0, members[0], 0, teamIndex),
                Member(1, members[1], 1, teamIndex),
            ],
        };
    }

    private static BandMemberStats Member(
        int index,
        string accountId,
        int instrumentId,
        int teamIndex) =>
        new()
        {
            MemberIndex = index,
            AccountId = accountId,
            InstrumentId = instrumentId,
            Score = 500_000 - teamIndex - index,
            Accuracy = 980_000 - teamIndex - index,
            IsFullCombo = (teamIndex + index) % 2 == 0,
            Stars = 5 - index,
            Difficulty = 3,
        };

    private static async Task PrimeAsync(
        InMemoryMetaDatabase fixture,
        IReadOnlyList<BandCurrentProjectionScopeKey> scopes)
    {
        var result = await CreateBuilder(fixture)
            .RefreshScopesAsync(
                scopes,
                ProductionOptions(
                    useCandidate: false,
                    skipUnchanged: false));
        Assert.True(result.PublishResult.Published);
    }

    private static async Task MarkSourceChangedAsync(
        InMemoryMetaDatabase fixture,
        IReadOnlyCollection<string> songIds)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE band_entries
            SET score = score + 100,
                last_updated_at = now() + interval '1 minute'
            WHERE song_id = ANY(@songIds)
            """;
        command.Parameters.AddWithValue(
            "songIds",
            songIds.ToArray());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> StateHashAsync(
        InMemoryMetaDatabase fixture)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        var rows = new List<string>();
        await using (var projection =
                     connection.CreateCommand())
        {
            projection.CommandText = """
                SELECT jsonb_build_object(
                    'songId', song_id,
                    'bandType', band_type,
                    'rankingScope', ranking_scope,
                    'scopeComboId', scope_combo_id,
                    'teamKey', team_key,
                    'entryComboId', entry_combo_id,
                    'entryInstrumentCombo',
                        entry_instrument_combo,
                    'teamMembers', team_members,
                    'memberAccountIds', member_account_ids,
                    'memberInstrumentIds',
                        member_instrument_ids,
                    'memberScores', member_scores,
                    'memberAccuracies', member_accuracies,
                    'memberFullCombos', member_full_combos,
                    'memberStars', member_stars,
                    'memberDifficulties',
                        member_difficulties,
                    'score', score,
                    'accuracy', accuracy,
                    'isFullCombo', is_full_combo,
                    'stars', stars,
                    'difficulty', difficulty,
                    'season', season,
                    'rank', rank,
                    'totalEntries', total_entries,
                    'percentile', percentile,
                    'endTime', end_time
                )::TEXT
                FROM current_band_leaderboard_entries
                ORDER BY band_type,
                         ranking_scope,
                         scope_combo_id,
                         song_id,
                         rank,
                         team_key
                """;
            await using var reader =
                await projection.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
        }
        await using (var scopes = connection.CreateCommand())
        {
            scopes.CommandText = """
                SELECT jsonb_build_object(
                    'songId', song_id,
                    'bandType', band_type,
                    'rankingScope', ranking_scope,
                    'scopeComboId', scope_combo_id,
                    'rowCount', row_count,
                    'publishedRowCount', published_row_count,
                    'status', status
                )::TEXT
                FROM band_current_projection_scope
                ORDER BY band_type,
                         ranking_scope,
                         scope_combo_id,
                         song_id
                """;
            await using var reader =
                await scopes.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
        }
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT jsonb_build_object(
                    'rowCount', row_count,
                    'scopeCount', scope_count,
                    'failedScopeCount', failed_scope_count
                )::TEXT
                FROM band_current_projection_state
                WHERE id = TRUE
                """;
            var value = await state.ExecuteScalarAsync();
            if (value is string text)
                rows.Add(text);
        }
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join('\n', rows))))
            .ToLowerInvariant();
    }

    private static async Task CreateInsertFailureTriggerAsync(
        InMemoryMetaDatabase fixture)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE FUNCTION fst_test_fail_band_projection()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'injected projection failure';
            END;
            $$;

            CREATE TRIGGER fst_test_fail_band_projection
            BEFORE INSERT ON current_band_leaderboard_entries
            FOR EACH ROW
            EXECUTE FUNCTION fst_test_fail_band_projection();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropInsertFailureTriggerAsync(
        InMemoryMetaDatabase fixture)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER fst_test_fail_band_projection
                ON current_band_leaderboard_entries;
            DROP FUNCTION fst_test_fail_band_projection();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ProjectionRowCountAsync(
        InMemoryMetaDatabase fixture)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*)::BIGINT FROM current_band_leaderboard_entries";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScopeStatusAsync(
        InMemoryMetaDatabase fixture,
        BandCurrentProjectionScopeKey scope)
    {
        await using var connection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM band_current_projection_scope
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
            """;
        command.Parameters.AddWithValue(
            "songId",
            scope.SongId);
        command.Parameters.AddWithValue(
            "bandType",
            scope.BandType);
        command.Parameters.AddWithValue(
            "rankingScope",
            scope.RankingScope);
        command.Parameters.AddWithValue(
            "scopeComboId",
            scope.ScopeComboId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(
                   value,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
