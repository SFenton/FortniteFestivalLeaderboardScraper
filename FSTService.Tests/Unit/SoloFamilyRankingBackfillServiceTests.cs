using System.Diagnostics;
using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SoloFamilyRankingBackfillServiceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly SoloFamilyRankingBackfillService _service;

    public SoloFamilyRankingBackfillServiceTests()
    {
        _persistence = new GlobalLeaderboardPersistence(
            _fixture.Db,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _fixture.DataSource,
            Options.Create(new FeatureOptions()));
        _service = new SoloFamilyRankingBackfillService(
            _persistence,
            _fixture.Db,
            _fixture.DataSource,
            NullLogger<SoloFamilyRankingBackfillService>.Instance);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task DryRunReportIsDeterministicAndIncludesDenominatorEvidence()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        SeedAccountRanking(
            "Solo_Guitar",
            "account",
            songsPlayed: 2,
            fullComboCount: 2,
            totalChartedSongs: 2);

        var first = _service.Rebuild(execute: false);
        var second = _service.Rebuild(execute: false);

        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
        Assert.False(first.Executed);
        Assert.Equal(0, first.InvalidRowCount);
        Assert.Equal(1, first.SourceRowsByInstrument["Solo_Guitar"]);
        Assert.Equal(1, first.CatalogDenominatorsByInstrument["Solo_Guitar"]);
        Assert.Equal(
            2,
            first.CanonicalDenominatorsByInstrument["Solo_Guitar"]);
        Assert.Equal(
            2,
            first.EffectiveDenominatorsByInstrument["Solo_Guitar"]);
        Assert.Equal(2, first.ScopeDenominators[
            SoloFamilyRankingScopes.Pad]);
        Assert.Equal(1, first.ScopeRows[SoloFamilyRankingScopes.Pad]);
    }

    [Fact]
    public async Task ExecuteWithInvalidRowsDoesNotReplaceExistingProjection()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        SeedAccountRanking(
            "Solo_Guitar",
            "invalid",
            songsPlayed: 2,
            fullComboCount: 2,
            totalChartedSongs: 1);
        _fixture.Db.ReplaceSoloFamilyRankings(
        [
            new SoloFamilyRankingDto
            {
                ScopeId = SoloFamilyRankingScopes.Pad,
                AccountId = "sentinel",
                SongsPlayed = 1,
                TotalChartedSongs = 1,
                Coverage = 1,
                RawSkillRating = 0.2,
                AdjustedSkillRating = 0.2,
                AdjustedSkillRank = 1,
                WeightedRating = 0.3,
                WeightedRank = 1,
                FcRate = 1,
                FcRateRank = 1,
                TotalScore = 100,
                TotalScoreRank = 1,
                MaxScorePercent = 0.9,
                MaxScorePercentRank = 1,
                FullComboCount = 1,
                RawMaxScorePercent = 0.9,
                RawWeightedRating = 0.3,
            },
        ]);

        var report = _service.Rebuild(execute: true);

        Assert.False(report.Executed);
        Assert.Equal(1, report.InvalidRowCount);
        Assert.NotNull(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "sentinel"));
        Assert.Null(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "invalid"));
    }

    [Fact]
    public async Task MaintenanceFailsClosedDuringActiveScrape()
    {
        var token = await CreateStablePublicationAsync(
            CreateGuitarOnlySong());
        var activeScrapeId = _fixture.Db.StartScrapeRun(token);

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Rebuild(execute: false));

        Assert.Contains(
            activeScrapeId.ToString(),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "active",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaintenanceFailsClosedWhilePublicReadsAreFrozen()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        _fixture.Db.SetPublicReadFreeze(
            true,
            reason: "test-maintenance-freeze");

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Rebuild(execute: false));

        Assert.Contains(
            "public reads are frozen",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceFailsClosedWithoutStablePublication()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Rebuild(execute: false));

        Assert.Contains(
            "safety gate failed",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaintenanceFailsClosedDuringWorkerOperation()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        var now = DateTime.UtcNow;
        _fixture.Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            "test-worker",
            now,
            now,
            currentOperation: new WorkerOperationInfo
            {
                OperationKey = "rankings.solo_family",
                OperationLabel = "Computing solo family rankings",
                Status = "running",
                StartedAtUtc = now,
                UpdatedAtUtc = now,
            });

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Rebuild(execute: false));

        Assert.Contains(
            "worker ledger is live",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaintenanceFailsClosedForLiveIdleWorker()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        var now = DateTime.UtcNow;
        _fixture.Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            "live-idle-worker",
            now.AddMinutes(-1),
            now,
            message: "idle");

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Rebuild(execute: false));

        Assert.Contains(
            "worker ledger is live",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "heartbeatAge",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaintenanceAllowsOfflineWorkerWithHistoricalFailure()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        var now = DateTime.UtcNow;
        _fixture.Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "offline",
            "scraper",
            "offline-worker",
            now.AddMinutes(-5),
            now,
            message: "worker stopped");
        _fixture.Db.UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            currentOperation: null,
            lastOperation: new WorkerOperationInfo
            {
                OperationKey = "rankings.solo_family",
                OperationLabel = "Computing solo family rankings",
                Status = "failed",
                Phase = "ComputingRankings",
                SubOperation = "solo_family_rankings",
                Detail = "synthetic historical failure",
                StartedAtUtc = now.AddMinutes(-2),
                UpdatedAtUtc = now.AddMinutes(-1),
                EndedAtUtc = now.AddMinutes(-1),
            },
            status: "offline",
            updatedAtUtc: now);

        var report = _service.Rebuild(execute: false);

        Assert.False(report.Executed);
        Assert.Equal(0, report.InvalidRowCount);
    }

    [Fact]
    public async Task MaintenanceAllowsStaleWorkerWithHistoricalRunningOperation()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        var heartbeat = DateTime.UtcNow.AddMinutes(-2);
        _fixture.Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            "stale-worker",
            heartbeat.AddMinutes(-5),
            heartbeat,
            currentOperation: new WorkerOperationInfo
            {
                OperationKey = "rankings.solo_family",
                OperationLabel = "Computing solo family rankings",
                Status = "running",
                Phase = "ComputingRankings",
                SubOperation = "solo_family_rankings",
                StartedAtUtc = heartbeat.AddMinutes(-1),
                UpdatedAtUtc = heartbeat,
            });

        var report = _service.Rebuild(execute: false);

        Assert.False(report.Executed);
        Assert.Equal(0, report.InvalidRowCount);
    }

    [Fact]
    public async Task MaintenanceFailsClosedWhenPublicationLockIsBusy()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        using var connection = _fixture.DataSource.OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT pg_advisory_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Rebuild(execute: false));

        Assert.Contains(
            "publication/maintenance lock is busy",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedRuntimeReadTimesOutWithoutReplacementOrLockLeak()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        SeedAccountRanking(
            "Solo_Guitar",
            "bounded-runtime-read",
            songsPlayed: 1,
            fullComboCount: 1,
            totalChartedSongs: 1);
        SeedSentinelSoloFamilyRanking();

        using var blockerConnection = _fixture.DataSource.OpenConnection();
        using var blockerTransaction = blockerConnection.BeginTransaction();
        using (var block = blockerConnection.CreateCommand())
        {
            block.Transaction = blockerTransaction;
            block.CommandText =
                "LOCK TABLE service_worker_status IN ACCESS EXCLUSIVE MODE";
            block.ExecuteNonQuery();
        }

        var service = new SoloFamilyRankingBackfillService(
            _persistence,
            _fixture.Db,
            _fixture.DataSource,
            NullLogger<SoloFamilyRankingBackfillService>.Instance,
            afterMaintenanceLocksAcquired: null,
            separateReadCommandTimeoutSeconds: 1);
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.ThrowsAny<Exception>(
            () => service.Rebuild(execute: true));
        stopwatch.Stop();

        Assert.Contains(
            "timeout",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Blocked runtime read took {stopwatch.Elapsed}.");
        Assert.NotNull(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "sentinel"));
        Assert.Null(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "bounded-runtime-read"));

        using (var probe = _fixture.DataSource.OpenConnection())
        using (var acquire = probe.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_try_advisory_lock(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            Assert.True(Convert.ToBoolean(acquire.ExecuteScalar()));

            using var release = probe.CreateCommand();
            release.CommandText =
                "SELECT pg_advisory_unlock(@lockKey)";
            release.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            Assert.True(Convert.ToBoolean(release.ExecuteScalar()));
        }

        blockerTransaction.Rollback();
        var retry = _service.Rebuild(execute: false);
        Assert.False(retry.Executed);
    }

    [Fact]
    public void CanonicalSourceReadHonorsFiniteCommandTimeout()
    {
        var instrumentDb = _persistence.GetOrCreateInstrumentDb(
            "Solo_Guitar");
        using var blockerConnection = _fixture.DataSource.OpenConnection();
        using var blockerTransaction = blockerConnection.BeginTransaction();
        using (var block = blockerConnection.CreateCommand())
        {
            block.Transaction = blockerTransaction;
            block.CommandText =
                "LOCK TABLE account_rankings IN ACCESS EXCLUSIVE MODE";
            block.ExecuteNonQuery();
        }

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.ThrowsAny<Exception>(
            () => instrumentDb.GetAllRankingSummariesDetailed(
                commandTimeoutSeconds: 1));
        stopwatch.Stop();

        Assert.Contains(
            "timeout",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Blocked source read took {stopwatch.Elapsed}.");
        blockerTransaction.Rollback();
        Assert.Empty(instrumentDb.GetAllRankingSummariesDetailed(
            commandTimeoutSeconds: 1));
    }

    [Fact]
    public async Task ExecuteSurvivesShortIdleTransactionTimeout()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        SeedAccountRanking(
            "Solo_Guitar",
            "idle-timeout-safe",
            songsPlayed: 1,
            fullComboCount: 1,
            totalChartedSongs: 1);

        var connectionString = new NpgsqlConnectionStringBuilder(
            _fixture.DataSource.ConnectionString)
        {
            Password = "test",
            Options =
                "-c idle_in_transaction_session_timeout=100ms",
        };
        using var shortTimeoutDataSource = NpgsqlDataSource.Create(
            connectionString.ConnectionString);
        using (var probe = shortTimeoutDataSource.OpenConnection())
        using (var show = probe.CreateCommand())
        {
            show.CommandText =
                "SHOW idle_in_transaction_session_timeout";
            Assert.Equal("100ms", Convert.ToString(show.ExecuteScalar()));
        }

        var observedDisabledTimeout = false;
        var service = new SoloFamilyRankingBackfillService(
            _persistence,
            _fixture.Db,
            shortTimeoutDataSource,
            NullLogger<SoloFamilyRankingBackfillService>.Instance,
            (connection, transaction) =>
            {
                using (var show = connection.CreateCommand())
                {
                    show.Transaction = transaction;
                    show.CommandText =
                        "SHOW idle_in_transaction_session_timeout";
                    observedDisabledTimeout =
                        Convert.ToString(show.ExecuteScalar()) == "0";
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(300));

                using var verify = connection.CreateCommand();
                verify.Transaction = transaction;
                verify.CommandText = "SELECT 1";
                Assert.Equal(1, Convert.ToInt32(verify.ExecuteScalar()));
            });

        var report = service.Rebuild(execute: true);

        Assert.True(observedDisabledTimeout);
        Assert.True(report.Executed);
        Assert.NotNull(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "idle-timeout-safe"));
    }

    [Fact]
    public async Task ConnectionLossAfterLocksCannotCommitReplacement()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        SeedAccountRanking(
            "Solo_Guitar",
            "replacement",
            songsPlayed: 1,
            fullComboCount: 1,
            totalChartedSongs: 1);
        SeedSentinelSoloFamilyRanking();

        var service = new SoloFamilyRankingBackfillService(
            _persistence,
            _fixture.Db,
            _fixture.DataSource,
            NullLogger<SoloFamilyRankingBackfillService>.Instance,
            (connection, _) =>
            {
                using var killer = _fixture.DataSource.OpenConnection();
                using var terminate = killer.CreateCommand();
                terminate.CommandText =
                    "SELECT pg_terminate_backend(@processId)";
                terminate.Parameters.AddWithValue(
                    "processId",
                    connection.ProcessID);
                Assert.True(Convert.ToBoolean(
                    terminate.ExecuteScalar()));
            });

        Assert.ThrowsAny<Exception>(
            () => service.Rebuild(execute: true));

        Assert.NotNull(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "sentinel"));
        Assert.Null(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "replacement"));
    }

    [Fact]
    public async Task ConcurrentPublicationCannotInterleave()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        SeedAccountRanking(
            "Solo_Guitar",
            "publication-lock",
            songsPlayed: 1,
            fullComboCount: 1,
            totalChartedSongs: 1);
        var publishedScrapeId = _fixture.Db
            .GetPublicationPointerState()
            .PublishedScrapeId;
        Assert.NotNull(publishedScrapeId);

        using var locksHeld = new ManualResetEventSlim();
        using var releaseMaintenance = new ManualResetEventSlim();
        using var publicationStarted = new ManualResetEventSlim();
        var service = new SoloFamilyRankingBackfillService(
            _persistence,
            _fixture.Db,
            _fixture.DataSource,
            NullLogger<SoloFamilyRankingBackfillService>.Instance,
            (_, _) =>
            {
                locksHeld.Set();
                if (!releaseMaintenance.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        "Timed out waiting to release maintenance locks.");
                }
            });

        var rebuildTask = Task.Run(
            () => service.Rebuild(execute: true));
        Assert.True(locksHeld.Wait(TimeSpan.FromSeconds(5)));

        var publicationTask = Task.Run(() =>
        {
            publicationStarted.Set();
            _fixture.Db.PublishScrapeRun(
                publishedScrapeId.Value,
                promoteCachedResponses: false);
        });
        try
        {
            Assert.True(publicationStarted.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(publicationTask.IsCompleted);
        }
        finally
        {
            releaseMaintenance.Set();
        }

        var report = await rebuildTask.WaitAsync(
            TimeSpan.FromSeconds(10));
        await publicationTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(report.Executed);
        Assert.NotNull(_fixture.Db.GetSoloFamilyRanking(
            SoloFamilyRankingScopes.Pad,
            "publication-lock"));
    }

    [Fact]
    public async Task ProgramDryRunStartsNoWorkersAndDoesNotInitializeSchema()
    {
        await CreateStablePublicationAsync(CreateGuitarOnlySong());
        using (var connection = _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TABLE score_history_dedup_original_rows CASCADE;
                """;
            command.ExecuteNonQuery();
        }

        var result = await RunProgramAsync(
            SoloFamilyRankingBackfillCommand.MaintenanceFlag);

        Assert.True(
            result.ExitCode == 0,
            $"Program exited with {result.ExitCode}:{Environment.NewLine}" +
            result.Output);
        var jsonLine = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Last(line => line.StartsWith('{')
                          && line.Contains(
                              "\"InvalidRowCount\"",
                              StringComparison.Ordinal));
        using var document = JsonDocument.Parse(jsonLine);
        Assert.False(document.RootElement.GetProperty("Executed").GetBoolean());
        Assert.Equal(
            0,
            document.RootElement
                .GetProperty("InvalidRowCount")
                .GetInt32());

        using var verifyConnection = _fixture.DataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                to_regclass(
                    'public.score_history_dedup_original_rows') IS NULL,
                (SELECT COUNT(*) FROM service_worker_status);
            """;
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    private async Task<SongCatalogPersistenceToken>
        CreateStablePublicationAsync(params Song[] songs)
    {
        var festivalPersistence = new FestivalPersistence(
            _fixture.DataSource);
        var token = await festivalPersistence.SaveSongsVersionedAsync(songs);
        var scrapeId = _fixture.Db.StartScrapeRun(token);
        _fixture.Db.CompleteScrapeRun(
            scrapeId,
            songs.Length,
            0,
            0,
            0);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        return token;
    }

    private void SeedAccountRanking(
        string instrument,
        string accountId,
        int songsPlayed,
        int fullComboCount,
        int totalChartedSongs)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO account_rankings (
                account_id, instrument, songs_played, total_charted_songs,
                coverage, raw_skill_rating, adjusted_skill_rating,
                adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank,
                max_score_percent, max_score_percent_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank,
                computed_at)
            VALUES (
                @accountId, @instrument, @songsPlayed,
                @totalChartedSongs,
                @songsPlayed::double precision /
                    NULLIF(@totalChartedSongs, 0),
                0.2, 0.2, 1, 0.3, 1,
                @fullComboCount::double precision /
                    NULLIF(@totalChartedSongs, 0),
                1, 100, 1, 0.9, 1, 95, @fullComboCount, 5, 1, 1,
                now())
            """;
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("songsPlayed", songsPlayed);
        command.Parameters.AddWithValue(
            "totalChartedSongs",
            totalChartedSongs);
        command.Parameters.AddWithValue(
            "fullComboCount",
            fullComboCount);
        command.ExecuteNonQuery();
    }

    private void SeedSentinelSoloFamilyRanking()
    {
        _fixture.Db.ReplaceSoloFamilyRankings(
        [
            new SoloFamilyRankingDto
            {
                ScopeId = SoloFamilyRankingScopes.Pad,
                AccountId = "sentinel",
                SongsPlayed = 1,
                TotalChartedSongs = 1,
                Coverage = 1,
                RawSkillRating = 0.2,
                AdjustedSkillRating = 0.2,
                AdjustedSkillRank = 1,
                WeightedRating = 0.3,
                WeightedRank = 1,
                FcRate = 1,
                FcRateRank = 1,
                TotalScore = 100,
                TotalScoreRank = 1,
                MaxScorePercent = 0.9,
                MaxScorePercentRank = 1,
                FullComboCount = 1,
                RawMaxScorePercent = 0.9,
                RawWeightedRating = 0.3,
            },
        ]);
    }

    private async Task<ProgramResult> RunProgramAsync(
        params string[] arguments)
    {
        var workingDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"solo-family-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var connectionString = new NpgsqlConnectionStringBuilder(
                _fixture.DataSource.ConnectionString)
            {
                Password = "test",
            };
            startInfo.Environment["ConnectionStrings__PostgreSQL"] =
                connectionString.ConnectionString;
            startInfo.Environment["TMPDIR"] = workingDirectory;
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start FSTService for startup validation.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProgramResult(
                process.ExitCode,
                string.Concat(stdoutTask.Result, "\n", stderrTask.Result));
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static Song CreateGuitarOnlySong()
        => new()
        {
            track = new Track
            {
                su = "song-guitar",
                tt = "Guitar Song",
                an = "Artist",
                @in = new In
                {
                    gr = 3,
                },
            },
        };

    private sealed record ProgramResult(int ExitCode, string Output);
}
