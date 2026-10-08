using System.Text.Json;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class PublicationGuardSchemaTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;
    private string ConnectionString =>
        SharedPostgresContainer.OriginalConnectionStringFor(DataSource);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Schema_plan_applies_bounded_guard_step_after_main_schema()
    {
        var plan = DatabaseInitializer.GetSchemaInitializationPlan()
            .Select(static step => step.Name)
            .ToList();
        var guard = DatabaseInitializer.PublicationGuardInitializationStep;

        Assert.Equal(
            plan.IndexOf("main-publication") + 1,
            plan.IndexOf(PublicationGuardSchema.StepName));
        Assert.True(guard.UseShortTransaction);
        Assert.Equal("2s", guard.LockTimeout);
        Assert.Equal("15s", guard.StatementTimeout);
    }

    [Fact]
    public void Fresh_schema_installs_guard_without_ranking_scans()
    {
        Assert.Equal(1, CountTriggers(PublicationGuardSchema.GuardTriggerName));
        var definition = GuardFunctionDefinition();
        Assert.Contains("scrape_phase_timings", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("account_rankings", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("solo_family_rankings", definition, StringComparison.Ordinal);
        Assert.False(FunctionExists(PublicationGuardSchema.LegacyDenominatorFunctionName));
    }

    [Fact]
    public async Task Removes_legacy_operator_objects_and_is_idempotent()
    {
        InstallLegacyOperatorObjects();
        Assert.Equal(10, CountTriggers(PublicationGuardSchema.LegacyDenominatorTriggerName));
        Assert.Contains("account_rankings", GuardFunctionDefinition(), StringComparison.Ordinal);

        await DatabaseInitializer.EnsurePublicationGuardSchemaAsync(ConnectionString);
        await DatabaseInitializer.EnsurePublicationGuardSchemaAsync(ConnectionString);

        Assert.Equal(0, CountTriggers(PublicationGuardSchema.LegacyDenominatorTriggerName));
        Assert.False(FunctionExists(PublicationGuardSchema.LegacyDenominatorFunctionName));
        Assert.Equal(1, CountTriggers(PublicationGuardSchema.GuardTriggerName));
        Assert.DoesNotContain("account_rankings", GuardFunctionDefinition(), StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_still_rejects_publishing_a_scrape_with_failed_phase_timings()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 1, 1, 1);
        Execute(
            """
            INSERT INTO scrape_phase_timings (
                scrape_id, phase, started_at, completed_at, duration_ms, success)
            VALUES (@scrapeId, 'post.compute_rankings', now(), now(), 1, FALSE)
            """,
            ("scrapeId", scrapeId));

        var error = Assert.ThrowsAny<Exception>(() =>
            Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false));

        Assert.Contains("failed scrape_phase_timings", error.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(scrapeId, Db.GetPublicationPointerState().PublishedScrapeId);
    }

    [Fact]
    public void Preparation_rejects_impossible_account_ranking_denominator()
    {
        var scrapeId = CompletedScrape();
        InsertAccountRanking("impossible", songsPlayed: 6, totalCharted: 5, fullCombos: 1);
        var before = Db.GetPublicationPointerState();

        var error = Assert.Throws<InvalidOperationException>(() =>
            Db.PrepareScrapePublication(scrapeId, promoteCachedResponses: false));

        Assert.Contains("impossible account ranking denominator", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Db.GetPublicationPointerState());
        Assert.Equal(0, CountReadyGenerations(scrapeId));
    }

    [Theory]
    [InlineData(5, 6, 0.5, 0.0)]
    [InlineData(5, 1, 1.5, 0.0)]
    [InlineData(5, 1, 0.5, 1.5)]
    public void Preparation_rejects_impossible_solo_family_ranking_row(
        int songsPlayed, int fullCombos, double coverage, double fcRate)
    {
        var scrapeId = CompletedScrape();
        InsertSoloFamilyRanking(songsPlayed, totalCharted: 5, fullCombos, coverage, fcRate);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Db.PrepareScrapePublication(scrapeId, promoteCachedResponses: false));

        Assert.Contains("impossible solo family ranking denominator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_rankings_publish()
    {
        var scrapeId = CompletedScrape();
        InsertAccountRanking("valid", songsPlayed: 5, totalCharted: 5, fullCombos: 5);
        InsertSoloFamilyRanking(songsPlayed: 5, totalCharted: 5, fullCombos: 5, coverage: 1.0, fcRate: 1.0);

        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        Assert.Equal(scrapeId, Db.GetPublicationPointerState().PublishedScrapeId);
    }

    [Fact]
    public async Task Command_applies_only_the_guard_step()
    {
        InstallLegacyOperatorObjects();
        using var output = new StringWriter();

        var exitCode = await PublicationGuardSchemaCommand.RunAsync(
            [PublicationGuardSchemaCommand.Flag], ConnectionString, output);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("schema_current", json.RootElement.GetProperty("outcome").GetString());
        Assert.True(json.RootElement.GetProperty("transactionCommitted").GetBoolean());
        Assert.False(json.RootElement.GetProperty("hostedServicesStarted").GetBoolean());
        Assert.Equal(0, CountTriggers(PublicationGuardSchema.LegacyDenominatorTriggerName));
    }

    [Theory]
    [InlineData("--initialize-schema-only")]
    [InlineData("--api-only")]
    [InlineData(PublicationGuardSchemaCommand.Flag)]
    public async Task Command_rejects_additional_arguments_before_connecting(string argument)
    {
        using var output = new StringWriter();
        string[] args = [PublicationGuardSchemaCommand.Flag, argument];

        Assert.True(PublicationGuardSchemaCommand.IsRequested(args));
        Assert.Equal(64, await PublicationGuardSchemaCommand.RunAsync(args, "Host=unused", output));
        Assert.Contains("invalid_command_arguments", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--initialize-publication-guard-schema-only=true")]
    [InlineData("/initialize-publication-guard-schema-only")]
    [InlineData("initialize-publication-guard-schema-only-extra")]
    public async Task Malformed_command_forms_are_refused_not_ignored(string argument)
    {
        using var output = new StringWriter();
        string[] args = [argument];

        Assert.True(PublicationGuardSchemaCommand.IsRequested(args));
        Assert.Equal(64, await PublicationGuardSchemaCommand.RunAsync(args, "Host=unused", output));
    }

    [Fact]
    public async Task Command_refuses_missing_connection_string()
    {
        using var output = new StringWriter();

        Assert.Equal(2, await PublicationGuardSchemaCommand.RunAsync(
            [PublicationGuardSchemaCommand.Flag], null, output));
        Assert.Contains("postgresql_connection_missing", output.ToString(), StringComparison.Ordinal);
    }

    private long CompletedScrape()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 1, 1, 1);
        return scrapeId;
    }

    private void InsertAccountRanking(string accountId, int songsPlayed, int totalCharted, int fullCombos)
        => Execute(
            """
            INSERT INTO account_rankings (
                account_id, instrument, songs_played, total_charted_songs, coverage,
                raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank,
                total_score, total_score_rank, max_score_percent, max_score_percent_rank,
                avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at)
            VALUES (
                @accountId, 'Solo_Guitar', @songsPlayed, @totalCharted,
                @songsPlayed::REAL / @totalCharted,
                0.5, 0.5, 1, 0.5, 1, @fullCombos::REAL / @totalCharted, 1,
                100, 1, 0.9, 1, 0.9, @fullCombos, 5, 1, 1, now())
            """,
            ("accountId", accountId),
            ("songsPlayed", songsPlayed),
            ("totalCharted", totalCharted),
            ("fullCombos", fullCombos));

    private void InsertSoloFamilyRanking(
        int songsPlayed, int totalCharted, int fullCombos, double coverage, double fcRate)
        => Execute(
            """
            INSERT INTO solo_family_rankings (
                scope_id, account_id, songs_played, total_charted_songs, coverage,
                raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank,
                total_score, total_score_rank, max_score_percent, max_score_percent_rank,
                full_combo_count, computed_at)
            VALUES (
                'pad', 'family-account', @songsPlayed, @totalCharted, @coverage,
                0.5, 0.5, 1, 0.5, 1, @fcRate, 1, 100, 1, 0.9, 1, @fullCombos, now())
            """,
            ("songsPlayed", songsPlayed),
            ("totalCharted", totalCharted),
            ("coverage", (float)coverage),
            ("fcRate", (float)fcRate),
            ("fullCombos", fullCombos));

    /// <summary>The operator-installed production definitions this step replaces.</summary>
    private void InstallLegacyOperatorObjects() => Execute(
        """
        CREATE OR REPLACE FUNCTION fst_account_rankings_denominator_guard_1100()
        RETURNS trigger LANGUAGE plpgsql AS $legacy$
        DECLARE denom integer;
        BEGIN
            denom := GREATEST(669, NEW.total_charted_songs, NEW.songs_played, NEW.full_combo_count);
            NEW.total_charted_songs := denom;
            NEW.coverage := NEW.songs_played::double precision / denom;
            NEW.fc_rate := NEW.full_combo_count::double precision / denom;
            RETURN NEW;
        END
        $legacy$;
        CREATE TRIGGER trg_account_rankings_denominator_guard_1100
            BEFORE INSERT OR UPDATE OF songs_played, total_charted_songs, full_combo_count
            ON account_rankings
            FOR EACH ROW EXECUTE FUNCTION fst_account_rankings_denominator_guard_1100();

        CREATE OR REPLACE FUNCTION guard_scrape_publication_no_failed_phases()
        RETURNS trigger LANGUAGE plpgsql AS $legacy$
        BEGIN
            IF NEW.published_scrape_id IS DISTINCT FROM OLD.published_scrape_id THEN
                UPDATE account_rankings
                SET total_charted_songs = total_charted_songs
                WHERE songs_played > total_charted_songs;
                IF EXISTS (SELECT 1 FROM solo_family_rankings WHERE fc_rate > 1.000001) THEN
                    RAISE EXCEPTION 'legacy guard';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $legacy$;
        """);

    private string GuardFunctionDefinition()
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_get_functiondef('guard_scrape_publication_no_failed_phases'::regproc)";
        return (string)command.ExecuteScalar()!;
    }

    private bool FunctionExists(string name)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = @name)";
        command.Parameters.AddWithValue("name", name);
        return (bool)command.ExecuteScalar()!;
    }

    private int CountReadyGenerations(long scrapeId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*)::INTEGER FROM publication_generations WHERE scrape_id = @scrapeId AND status = 'ready'";
        command.Parameters.AddWithValue("scrapeId", scrapeId);
        return (int)command.ExecuteScalar()!;
    }

    private int CountTriggers(string name)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*)::INTEGER FROM pg_trigger WHERE tgname = @name AND NOT tgisinternal";
        command.Parameters.AddWithValue("name", name);
        return (int)command.ExecuteScalar()!;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
