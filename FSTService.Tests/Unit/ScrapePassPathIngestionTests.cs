using System.Net;
using System.Security.Cryptography;
using System.Text;
using FortniteFestival.Core;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Phase B publication-safe scrape-pass path ingestion. Every case asserts
/// that live <c>songs</c> rows are untouched: staging only writes the working
/// publication snapshot.
/// </summary>
public sealed class ScrapePassPathIngestionTests : IDisposable
{
    private const string ValidPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private const int GeneratedScore = 123_456;

    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly string _dataDirectory;
    private readonly byte[] _midiKey = new byte[32];
    private readonly byte[] _encryptedDat;

    public ScrapePassPathIngestionTests()
    {
        _dataDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"scrape-pass-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        RandomNumberGenerator.Fill(_midiKey);
        _encryptedDat = EncryptMidi(BuildMinimalMidi(), _midiKey);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    [Fact]
    public async Task Disabled_flag_stages_nothing()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            options => options.EnableScrapePassPathGeneration = false);

        Assert.False(ingestion.IsEnabled);
        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal(0, result.Applied);
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-a"));
    }

    [Theory]
    [InlineData(ScrapePhase.All, true)]
    [InlineData(ScrapePhase.None, false)]
    [InlineData(ScrapePhase.SoloAll, false)]
    [InlineData(ScrapePhase.SoloPrecompute, false)]
    [InlineData(ScrapePhase.SoloRankings, false)]
    [InlineData(ScrapePhase.BandAll, false)]
    public void Path_staging_requires_the_full_resolved_pipeline(
        ScrapePhase resolvedPhases,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrapeOrchestrator
                .ShouldRunScrapePassPathIngestion(
                    resolvedPhases));
    }

    [Fact]
    public async Task Bootstrap_staging_updates_the_candidate_only()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(CreateChoptScript());

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(1, result.Pending);
        Assert.Equal(1, result.Selected);
        Assert.Equal(1, result.Staged);
        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Bootstrap);
        Assert.Equal(0, result.IdenticalRefresh);
        Assert.Equal(0, result.ChangedBlocked);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Conflicted);
        Assert.Equal(0, result.Remaining);
        Assert.False(result.TimedOut);

        var candidate = ReadSnapshot(publicationId, "song-a");
        Assert.NotNull(candidate.GenerationId);
        Assert.Equal(GeneratedScore, candidate.MaxLeadScore);
        Assert.Equal(1, candidate.Revision);
        Assert.True(candidate.PromotionPending);
        Assert.Equal(["Solo_Guitar"], candidate.ExpectedInstruments);

        var live = ReadLiveSong("song-a");
        Assert.Equal(0, live.Revision);
        Assert.Null(live.GenerationId);
        Assert.Null(live.MaxLeadScore);
        Assert.True(live.PathGenerationPending);

        // The candidate binding stays ready and complete.
        var binding = Db.GetPublicationSurfaceBindings(publicationId)
            .Single(static b =>
                b.SurfaceName == PublicationSurfaceNames.PathArtifacts);
        Assert.Equal(
            PublicationPathArtifactSchema.ManifestBindingKind,
            binding.BindingKind);
        Assert.Equal(PublicationGenerationStatus.Ready, binding.Status);
    }

    [Fact]
    public async Task Midi_only_candidate_is_selected_and_staged()
    {
        var songs = await SeedPendingCatalogAsync("midi-only");
        songs[0].track.@in = new In();
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(CreateChoptScript());

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(1, result.Selected);
        Assert.Equal(1, result.Staged);
        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Bootstrap);
        Assert.Equal(
            ["Solo_Guitar"],
            ReadSnapshot(
                    publicationId,
                    "midi-only")
                .ExpectedInstruments);
    }

    [Fact]
    public async Task Identical_maxima_refresh_is_accepted()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        SeedExistingGeneration("song-a", GeneratedScore);
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(CreateChoptScript());

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.IdenticalRefresh);
        Assert.Equal(0, result.Bootstrap);
        Assert.Equal(0, result.ChangedBlocked);

        var candidate = ReadSnapshot(publicationId, "song-a");
        Assert.NotEqual("gen-existing", candidate.GenerationId);
        Assert.Equal(GeneratedScore, candidate.MaxLeadScore);
        Assert.Equal(2, candidate.Revision);

        var live = ReadLiveSong("song-a");
        Assert.Equal("gen-existing", live.GenerationId);
        Assert.Equal(1, live.Revision);
    }

    [Fact]
    public async Task Changed_maxima_are_blocked_and_recorded_by_default()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        SeedExistingGeneration("song-a", GeneratedScore - 1);
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(CreateChoptScript());

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Staged);
        Assert.Equal(1, result.ChangedBlocked);

        var candidate = ReadSnapshot(publicationId, "song-a");
        Assert.Equal("gen-existing", candidate.GenerationId);
        Assert.Equal(GeneratedScore - 1, candidate.MaxLeadScore);
        Assert.False(candidate.PromotionPending);

        var live = ReadLiveSong("song-a");
        Assert.Equal("gen-existing", live.GenerationId);
        Assert.True(live.PathGenerationPending);

        Assert.Equal(
            1,
            CountPathGenerationErrors(
                "song-a",
                PublicationPathArtifactSchema.ChangedMaximaFailureStage));
    }

    [Fact]
    public async Task Changed_maxima_are_applied_when_explicitly_allowed()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        SeedExistingGeneration("song-a", GeneratedScore - 1);
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            options =>
                options.ScrapePassPathGenerationAllowChangedMaxima = true);

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.ChangedBlocked);
        Assert.Equal(
            GeneratedScore,
            ReadSnapshot(publicationId, "song-a").MaxLeadScore);
        Assert.Equal(
            GeneratedScore - 1,
            ReadLiveSong("song-a").MaxLeadScore);
    }

    [Fact]
    public async Task Per_song_failures_do_not_stop_the_batch()
    {
        var songs = await SeedPendingCatalogAsync("song-a", "song-b");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            handler: new SelectiveDatHandler(
                _encryptedDat,
                failingSongId: "song-a"));

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(2, result.Selected);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Applied);
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-a"));
        Assert.NotNull(ReadSnapshotGeneration(publicationId, "song-b"));
        Assert.True(ReadLiveSong("song-a").PathGenerationPending);
    }

    [Fact]
    public async Task Max_songs_bounds_the_batch()
    {
        var songs = await SeedPendingCatalogAsync(
            "song-a",
            "song-b",
            "song-c");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            options => options.ScrapePassPathGenerationMaxSongs = 2);

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(3, result.Pending);
        Assert.Equal(2, result.Selected);
        Assert.Equal(2, result.Applied);
        Assert.Equal(1, result.Remaining);
        Assert.NotNull(ReadSnapshotGeneration(publicationId, "song-a"));
        Assert.NotNull(ReadSnapshotGeneration(publicationId, "song-b"));
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-c"));
    }

    [Fact]
    public async Task Timeout_stops_staging_and_leaves_the_candidate_unchanged()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(sleepSeconds: 30),
            options => options.ScrapePassPathGenerationTimeout =
                TimeSpan.FromSeconds(1));

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(0, result.Applied);
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-a"));
        Assert.Equal(0, ReadLiveSong("song-a").Revision);

        // The song that consumed the budget is backed off so it cannot
        // monopolize every pass, and it stays pending and auditable.
        var deferral = CreateStore().GetPathGenerationDeferralState("song-a")!;
        Assert.True(deferral.Pending);
        Assert.False(deferral.ReviewRequired);
        Assert.NotNull(deferral.NextAttemptAtUtc);
        Assert.Equal(1, deferral.AttemptCount);
    }

    [Fact]
    public async Task Timeout_keeps_the_completed_song_and_leaves_the_rest_pending()
    {
        var songs = await SeedPendingCatalogAsync("song-a", "song-b");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            // Four CHOpt invocations complete song-a; song-b then hangs.
            CreateChoptScript(sleepSeconds: 60, sleepAfterInvocations: 4),
            options =>
            {
                options.PathGenerationParallelism = 1;
                options.ScrapePassPathGenerationTimeout =
                    TimeSpan.FromSeconds(10);
            });

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Aborted);
        Assert.Equal(2, result.Selected);
        Assert.Equal(1, result.Staged);
        Assert.Equal(1, result.Applied);

        // Partial progress is preserved: no zero-progress retry loop.
        Assert.NotNull(ReadSnapshotGeneration(publicationId, "song-a"));
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-b"));
        Assert.True(ReadLiveSong("song-a").PathGenerationPending);
        Assert.True(ReadLiveSong("song-b").PathGenerationPending);

        var store = CreateStore();
        Assert.Null(
            store.GetPathGenerationDeferralState("song-a")!.NextAttemptAtUtc);
        Assert.NotNull(
            store.GetPathGenerationDeferralState("song-b")!.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Admission_failure_does_not_abort_the_scrape()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            admissionLeaseProvider: new ThrowingAdmissionLeaseProvider());

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.True(result.Aborted);
        Assert.Equal(0, result.Applied);
        Assert.NotNull(result.FailureReason);
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-a"));
        Assert.True(ReadLiveSong("song-a").PathGenerationPending);
        Assert.Null(
            CreateStore()
                .GetPathGenerationDeferralState("song-a")!
                .NextAttemptAtUtc);
    }

    [Fact]
    public async Task Batch_prerequisite_failure_does_not_back_off_unattempted_songs()
    {
        var songs = await SeedPendingCatalogAsync(
            "song-a",
            "song-b");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            Path.Combine(
                _dataDirectory,
                "missing-chopt"));

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.True(result.Aborted);
        Assert.Equal(2, result.Selected);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(2, result.Remaining);
        Assert.Null(
            ReadSnapshotGeneration(
                publicationId,
                "song-a"));
        Assert.Null(
            ReadSnapshotGeneration(
                publicationId,
                "song-b"));

        var store = CreateStore();
        foreach (var songId in new[] { "song-a", "song-b" })
        {
            var deferral =
                store.GetPathGenerationDeferralState(songId)!;
            Assert.True(deferral.Pending);
            Assert.Equal(0, deferral.AttemptCount);
            Assert.Null(deferral.NextAttemptAtUtc);
        }
    }

    [Fact]
    public async Task Selection_read_failure_does_not_abort_the_scrape()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            decorateStore: inner => new FailingReadPathDataStore(inner));

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.True(result.Aborted);
        Assert.Equal(0, result.Applied);
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-a"));
    }

    [Fact]
    public async Task Repository_failure_does_not_abort_the_scrape()
    {
        var songs = await SeedPendingCatalogAsync("song-a", "song-b");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            meta: CreateFailingPromotionMeta());

        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.False(result.Aborted);
        Assert.Equal(2, result.Staged);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.Conflicted);
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-a"));
        Assert.Null(ReadSnapshotGeneration(publicationId, "song-b"));
        Assert.True(ReadLiveSong("song-a").PathGenerationPending);
    }

    [Fact]
    public async Task Repository_failure_defers_the_song_and_frees_the_next_pass()
    {
        var songs = await SeedPendingCatalogAsync("song-a", "song-b");
        var first = StartScrape();
        var failingIngestion = CreateIngestion(
            CreateChoptScript(),
            options => options.ScrapePassPathGenerationMaxSongs = 1,
            meta: CreateFailingPromotionMeta());

        var firstPass = await failingIngestion.IngestAsync(
            first.ScrapeId,
            first.PublicationId,
            songs,
            CancellationToken.None);

        Assert.False(firstPass.Aborted);
        Assert.Equal(1, firstPass.Conflicted);
        Assert.Equal(0, firstPass.Applied);
        Assert.Equal(1, firstPass.Deferred);

        var store = CreateStore();
        var deferral = store.GetPathGenerationDeferralState("song-a")!;
        Assert.True(deferral.Pending);
        Assert.NotNull(deferral.NextAttemptAtUtc);
        Assert.Equal(1, deferral.AttemptCount);
        Assert.Equal(0, CountGenerations("song-a"));
        Assert.Empty(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow)
                .Where(static candidate => candidate.SongId == "song-a"));

        // Next pass: the conflicted song is backed off, so the cap goes to
        // the next pending song instead of starving it.
        Db.FailScrapeRun(first.ScrapeId, "scrape", "test isolation");
        var second = StartScrape();
        var healthyIngestion = CreateIngestion(
            CreateChoptScript(),
            options => options.ScrapePassPathGenerationMaxSongs = 1);
        var secondPass = await healthyIngestion.IngestAsync(
            second.ScrapeId,
            second.PublicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(2, secondPass.Pending);
        Assert.Equal(1, secondPass.Eligible);
        Assert.Equal(1, secondPass.Selected);
        Assert.Equal(1, secondPass.Applied);
        Assert.NotNull(ReadSnapshotGeneration(second.PublicationId, "song-b"));
        Assert.Null(ReadSnapshotGeneration(second.PublicationId, "song-a"));
    }

    [Fact]
    public async Task Explicit_promotion_conflict_defers_the_song_with_backoff()
    {
        var songs = await SeedPendingCatalogAsync("song-a", "song-b");
        var (scrapeId, publicationId) = StartScrape();

        // The candidate row no longer matches the state staged against, so
        // the apply returns an explicit Conflict outcome.
        ExecuteNonQuery(
            """
            UPDATE publication_path_artifacts
            SET path_generation_revision = path_generation_revision + 5
            WHERE publication_id = @publicationId
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        var ingestion = CreateIngestion(CreateChoptScript());
        var result = await ingestion.IngestAsync(
            scrapeId,
            publicationId,
            songs,
            CancellationToken.None);

        Assert.False(result.Aborted);
        Assert.Equal(2, result.Staged);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.Conflicted);
        Assert.Equal(2, result.Deferred);

        var store = CreateStore();
        foreach (var songId in new[] { "song-a", "song-b" })
        {
            var deferral = store.GetPathGenerationDeferralState(songId)!;
            Assert.True(deferral.Pending);
            Assert.False(deferral.ReviewRequired);
            Assert.NotNull(deferral.NextAttemptAtUtc);
            Assert.Contains(
                "Conflict",
                deferral.ReviewReason!,
                StringComparison.Ordinal);
        }

        Assert.Empty(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));
        Assert.Equal(0, CountGenerations("song-a"));
        Assert.Equal(0, CountGenerations("song-b"));
        var nextAttempt = store
            .GetPathGenerationDeferralState("song-a")!
            .NextAttemptAtUtc!
            .Value
            .ToUniversalTime();
        Assert.Equal(
            2,
            store.GetAutomaticPathGenerationCandidates(
                nextAttempt.AddMinutes(1)).Count);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var songs = await SeedPendingCatalogAsync("song-a");
        var (scrapeId, publicationId) = StartScrape();
        var ingestion = CreateIngestion(CreateChoptScript(sleepSeconds: 60));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ingestion.IngestAsync(
                scrapeId,
                publicationId,
                songs,
                cts.Token));
        Assert.Equal(0, CountGenerations("song-a"));
    }

    [Fact]
    public async Task Blocked_song_does_not_monopolize_the_next_pass()
    {
        var songs = await SeedPendingCatalogAsync("song-a", "song-b");
        SeedExistingGeneration("song-a", GeneratedScore - 1);
        var first = StartScrape();
        var ingestion = CreateIngestion(
            CreateChoptScript(),
            options => options.ScrapePassPathGenerationMaxSongs = 1);

        var firstPass = await ingestion.IngestAsync(
            first.ScrapeId,
            first.PublicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(1, firstPass.ChangedBlocked);
        Assert.Equal(0, firstPass.Applied);
        Assert.Equal(1, firstPass.Deferred);
        var blocked = CreateStore().GetPathGenerationDeferralState("song-a")!;
        Assert.True(blocked.Pending);
        Assert.True(blocked.ReviewRequired);
        var generationsAfterFirstPass = CountGenerations("song-a");
        Assert.Equal(0, generationsAfterFirstPass);
        Assert.Equal(
            1,
            CountPathGenerationErrors(
                "song-a",
                PublicationPathArtifactSchema.ChangedMaximaFailureStage));

        // Next pass: the blocked song is excluded, so the cap goes to the next
        // pending song instead of regenerating the blocked one.
        Db.FailScrapeRun(first.ScrapeId, "scrape", "test isolation");
        var second = StartScrape();
        var secondPass = await ingestion.IngestAsync(
            second.ScrapeId,
            second.PublicationId,
            songs,
            CancellationToken.None);

        Assert.Equal(2, secondPass.Pending);
        Assert.Equal(1, secondPass.Eligible);
        Assert.Equal(1, secondPass.Selected);
        Assert.Equal(1, secondPass.Applied);
        Assert.Equal(0, secondPass.ChangedBlocked);
        Assert.NotNull(ReadSnapshotGeneration(second.PublicationId, "song-b"));

        // No duplicate error rows and no repeated generation for the blocked
        // song.
        Assert.Equal(
            1,
            CountPathGenerationErrors(
                "song-a",
                PublicationPathArtifactSchema.ChangedMaximaFailureStage));
        Assert.Equal(generationsAfterFirstPass, CountGenerations("song-a"));
    }

    [Fact]
    public async Task Deferrals_rearm_on_catalog_identity_change_and_on_reset()
    {
        await SeedPendingCatalogAsync("song-a");
        var store = CreateStore();
        await store.MarkPathGenerationReviewRequiredAsync(
            "song-a",
            PublicationPathArtifactSchema.ChangedMaximaFailureStage,
            "2026-07-31T12:00:00.0000000Z",
            CancellationToken.None);

        Assert.Empty(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));
        Assert.True(store.GetPathGenerationDeferralState("song-a")!.Pending);

        // A new provider catalog identity re-arms the song automatically.
        SetCatalogLastModified("song-a", "2026-09-15T00:00:00.0000000Z");
        Assert.Single(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));

        // Backoff also defers, and the operator reset clears everything.
        SetCatalogLastModified("song-a", "2026-07-31T12:00:00.0000000Z");
        await store.SchedulePathGenerationRetryAsync(
            "song-a",
            "download failed",
            "2026-07-31T12:00:00.0000000Z",
            CancellationToken.None);
        Assert.Empty(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));

        Assert.True(store.RearmPathGeneration("song-a"));
        Assert.False(store.RearmPathGeneration("song-missing"));
        var rearmed = store.GetPathGenerationDeferralState("song-a")!;
        Assert.False(rearmed.ReviewRequired);
        Assert.Null(rearmed.NextAttemptAtUtc);
        Assert.Equal(0, rearmed.AttemptCount);
        Assert.True(rearmed.Pending);
        Assert.Single(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));
    }

    [Fact]
    public async Task Retry_after_a_catalog_rearm_replaces_the_stale_review_deferral()
    {
        await SeedPendingCatalogAsync("song-a");
        var store = CreateStore();

        // T1: blocked for review against the original catalog identity.
        await store.MarkPathGenerationReviewRequiredAsync(
            "song-a",
            PublicationPathArtifactSchema.ChangedMaximaFailureStage,
            "2026-07-31T12:00:00.0000000Z",
            CancellationToken.None);
        Assert.Empty(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));

        // T2: a new provider identity re-arms the song for one more attempt.
        SetCatalogLastModified("song-a", "2026-09-15T00:00:00.0000000Z");
        Assert.Single(
            store.GetAutomaticPathGenerationCandidates(DateTime.UtcNow));

        // That attempt fails deterministically, so ordinary retry backoff
        // replaces the now-obsolete review deferral.
        await store.SchedulePathGenerationRetryAsync(
            "song-a",
            "download failed",
            "2026-09-15T00:00:00.0000000Z",
            CancellationToken.None);

        var state = store.GetPathGenerationDeferralState("song-a")!;
        Assert.True(state.Pending);
        Assert.False(state.ReviewRequired);
        Assert.Null(state.ReviewAtUtc);
        Assert.Equal("download failed", state.ReviewReason);
        Assert.NotNull(state.NextAttemptAtUtc);
        Assert.Equal(
            "2026-09-15T00:00:00.0000000Z",
            state.DeferralIdentity);

        var nextAttemptAtUtc = state.NextAttemptAtUtc!.Value.ToUniversalTime();
        Assert.Empty(
            store.GetAutomaticPathGenerationCandidates(
                nextAttemptAtUtc.AddMinutes(-1)));
        Assert.Single(
            store.GetAutomaticPathGenerationCandidates(
                nextAttemptAtUtc.AddMinutes(1)));
    }

    [Fact]
    public async Task Retry_backoff_matches_the_bounded_schedule_in_storage()
    {
        await SeedPendingCatalogAsync("song-a");
        var store = CreateStore();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var before = DateTime.UtcNow;
            await store.SchedulePathGenerationRetryAsync(
                "song-a",
                "download failed",
                null,
                CancellationToken.None);
            var state = store.GetPathGenerationDeferralState("song-a")!;
            Assert.Equal(attempt, state.AttemptCount);
            var expected = PathDataStore.ComputeNextAttemptAtUtc(
                before,
                attempt);
            Assert.InRange(
                state.NextAttemptAtUtc!.Value.ToUniversalTime(),
                expected,
                expected.AddMinutes(1));
        }
    }

    [Fact]
    public void Retry_backoff_is_bounded_and_exponential()
    {
        var now = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            now.AddHours(1),
            PathDataStore.ComputeNextAttemptAtUtc(now, 1));
        Assert.Equal(
            now.AddHours(2),
            PathDataStore.ComputeNextAttemptAtUtc(now, 2));
        Assert.Equal(
            now.AddHours(4),
            PathDataStore.ComputeNextAttemptAtUtc(now, 3));
        Assert.Equal(
            now.AddHours(24),
            PathDataStore.ComputeNextAttemptAtUtc(now, 12));
    }

    [Fact]
    public void Classification_recognizes_bootstrap_identical_and_changed()
    {
        var promotion = CreatePromotion(1_000);
        Assert.Equal(
            StagedGenerationKind.Bootstrap,
            ScrapePassPathIngestion.Classify(
                CreateState(0, null, null),
                promotion));
        Assert.Equal(
            StagedGenerationKind.IdenticalMaxima,
            ScrapePassPathIngestion.Classify(
                CreateState(3, "gen-old", 1_000),
                promotion));
        Assert.Equal(
            StagedGenerationKind.ChangedMaxima,
            ScrapePassPathIngestion.Classify(
                CreateState(3, "gen-old", 1_001),
                promotion));
    }

    private static PathGenerationState CreateState(
        long revision,
        string? generationId,
        int? maxLead)
        => new(
            "song-a",
            revision,
            null,
            null,
            null,
            null,
            null,
            null,
            generationId,
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = maxLead });

    private static PathGenerationPromotion CreatePromotion(int maxLead)
        => new(
            "attempt",
            "song-a",
            0,
            "gen-new",
            new string('a', 64),
            null,
            DateTime.UtcNow,
            new PathGenerationRuntimeIdentity(
                "1.16.4",
                new string('b', 64),
                PathGenerationProfiles.PlasticDrumsV4),
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = maxLead });

    private ScrapePassPathIngestion CreateIngestion(
        string choptPath,
        Action<ScraperOptions>? configure = null,
        HttpMessageHandler? handler = null,
        IPathGenerationAdmissionLeaseProvider? admissionLeaseProvider = null,
        Func<IPathDataStore, IPathDataStore>? decorateStore = null,
        IMetaDatabase? meta = null)
    {
        var options = new ScraperOptions
        {
            DataDirectory = _dataDirectory,
            CHOptPath = choptPath,
            MidiEncryptionKey = Convert.ToHexString(_midiKey),
            EnablePathGeneration = true,
            EnableScrapePassPathGeneration = true,
            UsePublicationPathArtifacts = true,
            PathGenerationParallelism = 2,
            PathGenerationProfile = PathGenerationProfiles.PlasticDrumsV4,
        };
        configure?.Invoke(options);
        var wrapped = Options.Create(options);
        IPathDataStore store = new PathDataStore(DataSource, null, wrapped);
        if (decorateStore is not null)
            store = decorateStore(store);
        var coordinator = new PathGenerationCoordinator(
            new HttpClient(handler ?? new StaticDatHandler(_encryptedDat)),
            store,
            new SongsCacheService(),
            wrapped,
            new ScrapeProgressTracker(),
            NullLogger<PathGenerationCoordinator>.Instance,
            admissionLeaseProvider
                ?? UncontendedPathGenerationAdmissionLeaseProvider.Instance);
        return new ScrapePassPathIngestion(
            coordinator,
            store,
            meta ?? Db,
            wrapped,
            NullLogger<ScrapePassPathIngestion>.Instance);
    }

    private PathDataStore CreateStore() =>
        new(
            DataSource,
            null,
            Options.Create(new ScraperOptions
            {
                DataDirectory = _dataDirectory,
            }));

    private (long ScrapeId, long PublicationId) StartScrape()
    {
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        return (scrapeId, publicationId);
    }

    private async Task<IReadOnlyList<Song>> SeedPendingCatalogAsync(
        params string[] songIds)
    {
        var songs = songIds.Select(CreateCatalogSong).ToArray();
        var persistence = new FestivalPersistence(DataSource);
        await persistence.SaveSongsVersionedAsync(songs);
        ExecuteNonQuery(
            """
            UPDATE songs
            SET path_generation_pending = TRUE
            WHERE song_id = ANY(@songIds)
            """,
            command => command.Parameters.Add(
                new NpgsqlParameter("songIds", songIds)));
        return songs;
    }

    private void SeedExistingGeneration(string songId, int maxLead)
        => ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = @maxLead,
                dat_file_hash = 'existing-dat-hash',
                song_last_modified = '2026-07-31T12:00:00.0000000Z',
                paths_generated_at = TIMESTAMPTZ '2026-08-01 00:00:00Z',
                chopt_version = '1.16.4',
                chopt_binary_sha256 = @binaryHash,
                path_generation_profile = @profile,
                path_artifact_generation_id = 'gen-existing',
                path_expected_instruments = ARRAY['Solo_Guitar'],
                path_generation_revision = 1
            WHERE song_id = @songId
            """,
            command =>
            {
                command.Parameters.AddWithValue("songId", songId);
                command.Parameters.AddWithValue("maxLead", maxLead);
                command.Parameters.AddWithValue(
                    "binaryHash",
                    new string('c', 64));
                command.Parameters.AddWithValue(
                    "profile",
                    PathGenerationProfiles.PlasticDrumsV4);
            });

    private static Song CreateCatalogSong(string songId) =>
        new()
        {
            _title = songId,
            lastModified = new DateTime(
                2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
            track = new Track
            {
                su = songId,
                tt = songId,
                an = "Artist",
                ab = "Album",
                au = $"https://example.test/{songId}.jpg",
                mu = $"https://example.test/{songId}.dat",
                sig = "4/4",
                ge = ["rock"],
                ry = 2026,
                mt = 120,
                dn = 200,
                @in = new In { gr = 1 },
            },
        };

    private string? ReadSnapshotGeneration(
        long publicationId,
        string songId)
        => ReadSnapshot(publicationId, songId).GenerationId;

    private SnapshotRow ReadSnapshot(long publicationId, string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path_artifact_generation_id,
                   max_lead_score,
                   path_generation_revision,
                   promotion_pending,
                   path_expected_instruments
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = @songId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue("songId", songId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new SnapshotRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetBoolean(3),
            reader.GetFieldValue<string[]>(4));
    }

    private LiveRow ReadLiveSong(string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path_artifact_generation_id,
                   max_lead_score,
                   path_generation_revision,
                   path_generation_pending
            FROM songs
            WHERE song_id = @songId
            """;
        command.Parameters.AddWithValue("songId", songId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new LiveRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetBoolean(3));
    }

    private int CountPathGenerationErrors(string songId, string stage)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM path_generation_errors
            WHERE song_id = @songId
              AND failure_stage = @stage
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("stage", stage);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand> configure)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        command.ExecuteNonQuery();
    }

    private string CreateChoptScript(
        int sleepSeconds = 0,
        int sleepAfterInvocations = 0)
    {
        var path = Path.Combine(
            _dataDirectory,
            $"fake-chopt-{Guid.NewGuid():N}.sh");
        var counterPath = Path.Combine(
            _dataDirectory,
            $"chopt-invocations-{Guid.NewGuid():N}.log");
        var script = $$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              echo "CHOpt 1.16.4"
              exit 0
            fi
            out=""
            difficulty=""
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o) out="$2"; shift ;;
                -d) difficulty="$2"; shift ;;
              esac
              shift
            done
            printf 'x\n' >> '{{counterPath}}'
            invocations=$(wc -l < '{{counterPath}}')
            if [ "{{sleepSeconds}}" != "0" ] \
               && [ "$invocations" -gt "{{sleepAfterInvocations}}" ]; then
              sleep {{sleepSeconds}}
            fi
            printf '%s' '{{ValidPngBase64}}' | base64 -d > "$out"
            if [ "$difficulty" = "expert" ]; then
              printf '%s' '{{BuildPathJson(GeneratedScore, "expert")}}'
            else
              printf '%s' '{{BuildPathJson(100, "easy")}}'
            fi
            """;
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);
        return path;
    }

    private static string BuildPathJson(int totalScore, string difficulty)
        => $$"""{"schemaVersion":2,"songName":"Song","artist":"Artist","charter":"Charter","difficulty":"{{difficulty}}","totalScore":{{totalScore}},"pathSummary":"","activations":[],"notes":[],"spPhrases":[],"drumFills":[{}],"measures":[],"bpms":[],"timeSignatures":[]}""";

    private static byte[] BuildMinimalMidi()
    {
        using var stream = new MemoryStream();
        stream.Write("MThd"u8);
        WriteBigEndian32(stream, 6);
        WriteBigEndian16(stream, 1);
        WriteBigEndian16(stream, 1);
        WriteBigEndian16(stream, 480);
        using var trackStream = new MemoryStream();
        var trackNameBytes = Encoding.ASCII.GetBytes("PART GUITAR");
        trackStream.WriteByte(0x00);
        trackStream.WriteByte(0xff);
        trackStream.WriteByte(0x03);
        trackStream.WriteByte((byte)trackNameBytes.Length);
        trackStream.Write(trackNameBytes);
        trackStream.Write(
        [
            0x00, 0x90, 60, 100,
            0x60, 0x80, 60, 0,
        ]);
        trackStream.Write([0x00, 0xff, 0x2f, 0x00]);
        var track = trackStream.ToArray();
        stream.Write("MTrk"u8);
        WriteBigEndian32(stream, track.Length);
        stream.Write(track);
        return stream.ToArray();
    }

    private static byte[] EncryptMidi(byte[] midi, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.Zeros;
        var padded = new byte[(midi.Length + 15) / 16 * 16];
        Array.Copy(midi, padded, midi.Length);
        return aes.CreateEncryptor().TransformFinalBlock(
            padded,
            0,
            padded.Length);
    }

    private static void WriteBigEndian32(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBigEndian16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed record SnapshotRow(
        string? GenerationId,
        int? MaxLeadScore,
        long Revision,
        bool PromotionPending,
        string[] ExpectedInstruments);

    private sealed record LiveRow(
        string? GenerationId,
        int? MaxLeadScore,
        long Revision,
        bool PathGenerationPending);

    private static IMetaDatabase CreateFailingPromotionMeta()
    {
        var meta = Substitute.For<IMetaDatabase>();
        meta.ApplyWorkingPublicationPathPromotion(
                Arg.Any<PublicationPathPromotionRequest>())
            .Returns(_ => throw new InvalidOperationException(
                "Candidate promotion is unavailable."));
        return meta;
    }

    private void SetCatalogLastModified(string songId, string lastModified)
        => ExecuteNonQuery(
            """
            UPDATE songs
            SET last_modified = @lastModified
            WHERE song_id = @songId
            """,
            command =>
            {
                command.Parameters.AddWithValue("songId", songId);
                command.Parameters.AddWithValue(
                    "lastModified",
                    lastModified);
            });

    private int CountGenerations(string songId)
    {
        var probe = PathArtifactResolver.GetGenerationDirectory(
            Path.GetFullPath(_dataDirectory),
            songId,
            "probe");
        var songRoot = Path.GetDirectoryName(probe)!;
        return Directory.Exists(songRoot)
            ? Directory.EnumerateDirectories(songRoot).Count()
            : 0;
    }

    /// <summary>Fails every automatic-selection read.</summary>
    private sealed class FailingReadPathDataStore : IPathDataStore
    {
        private readonly IPathDataStore _inner;

        public FailingReadPathDataStore(IPathDataStore inner)
            => _inner = inner;

        public Dictionary<string, PathGenerationState>
            GetPathGenerationStates()
            => _inner.GetPathGenerationStates();

        public PathGenerationState? GetPathGenerationState(string songId)
            => _inner.GetPathGenerationState(songId);

        public HashSet<string> GetPendingPathGenerationSongIds()
            => _inner.GetPendingPathGenerationSongIds();

        public Dictionary<string, SongMaxScores> GetAllMaxScores()
            => _inner.GetAllMaxScores();

        public IReadOnlyList<PathGenerationCandidate>
            GetAutomaticPathGenerationCandidates(DateTime nowUtc)
            => throw new InvalidOperationException(
                "Automatic candidate selection is unavailable.");

        public Task<PathGenerationPromotionOutcome>
            TryPromoteGenerationAsync(
                PathGenerationPromotion promotion,
                CancellationToken ct)
            => _inner.TryPromoteGenerationAsync(promotion, ct);

        public Task AppendPathGenerationErrorAsync(
            PathGenerationError error,
            CancellationToken ct)
            => _inner.AppendPathGenerationErrorAsync(error, ct);
    }

    private sealed class ThrowingAdmissionLeaseProvider
        : IPathGenerationAdmissionLeaseProvider
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
            => throw new InvalidOperationException(
                "Path generation admission is unavailable.");
    }

    private sealed class StaticDatHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StaticDatHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
            });
    }

    private sealed class SelectiveDatHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly string _failingSongId;

        public SelectiveDatHandler(byte[] payload, string failingSongId)
        {
            _payload = payload;
            _failingSongId = failingSongId;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.Contains(_failingSongId, StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
            });
        }
    }
}

/// <summary>
/// Backend-only Phase B option bounds and the admin regeneration race gate.
/// </summary>
public sealed class ScrapePassPathGenerationOptionsTests
{
    [Fact]
    public void Defaults_are_publication_safe()
    {
        var options = new ScraperOptions();

        Assert.False(options.EnableScrapePassPathGeneration);
        Assert.Equal(25, options.ScrapePassPathGenerationMaxSongs);
        Assert.Equal(
            TimeSpan.FromMinutes(20),
            options.ScrapePassPathGenerationTimeout);
        Assert.False(options.ScrapePassPathGenerationAllowChangedMaxima);
        Assert.False(
            new ScraperOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void Max_songs_is_bounded(int value)
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                ScrapePassPathGenerationMaxSongs = value,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "ScrapePassPathGenerationMaxSongs",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Timeout_is_bounded()
    {
        var tooShort = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                ScrapePassPathGenerationTimeout = TimeSpan.FromSeconds(5),
            });
        var tooLong = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                ScrapePassPathGenerationTimeout = TimeSpan.FromHours(7),
            });

        Assert.True(tooShort.Failed);
        Assert.True(tooLong.Failed);
        Assert.Contains(
            "ScrapePassPathGenerationTimeout",
            tooShort.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Scrape_pass_staging_requires_the_publication_source_flag()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnableScrapePassPathGeneration = true,
                UsePublicationPathArtifacts = false,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "UsePublicationPathArtifacts",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Scrape_pass_staging_accepts_a_coherent_configuration()
        => Assert.False(
            new ScraperOptionsValidator()
                .Validate(
                    null,
                    new ScraperOptions
                    {
                        EnableScrapePassPathGeneration = true,
                        UsePublicationPathArtifacts = true,
                        MidiEncryptionKey =
                            "0123456789abcdef0123456789abcdef",
                    })
                .Failed);

    [Fact]
    public void Scrape_pass_staging_requires_a_midi_key()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnableScrapePassPathGeneration = true,
                UsePublicationPathArtifacts = true,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "MidiEncryptionKey",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Scrape_pass_staging_rejects_an_invalid_midi_key()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnableScrapePassPathGeneration = true,
                UsePublicationPathArtifacts = true,
                MidiEncryptionKey = "not-hex",
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "32- or 64-character",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Scrape_pass_staging_requires_path_generation()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnablePathGeneration = false,
                EnableScrapePassPathGeneration = true,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "EnableScrapePassPathGeneration",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_automatic_generation_stays_rejected()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnableAutomaticPathGeneration = true,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "not supported",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_regeneration_is_rejected_in_publication_bound_mode()
    {
        // The primary rejection: while publication-bound path artifacts are
        // the read source, immediate live promotion is never a supported
        // operation, regardless of pointer state or the API role's own
        // (non-authoritative) staging flag.
        Assert.Equal(
            AdminPathRegenerationGate.PublicationBoundReason,
            AdminPathRegenerationGate.GetConflictReason(
                new ScraperOptions
                {
                    UsePublicationPathArtifacts = true,
                    EnableScrapePassPathGeneration = false,
                },
                () => new PublicationPointerState(1, null, null, 1, null)));
        Assert.Equal(
            AdminPathRegenerationGate.PublicationBoundReason,
            AdminPathRegenerationGate.GetConflictReason(
                new ScraperOptions
                {
                    UsePublicationPathArtifacts = true,
                    EnableScrapePassPathGeneration = true,
                },
                () => new PublicationPointerState(1, null, 2, 1, null)));
    }

    [Fact]
    public void Admin_regeneration_in_publication_bound_mode_needs_no_pointer_read()
        => Assert.Equal(
            AdminPathRegenerationGate.PublicationBoundReason,
            AdminPathRegenerationGate.GetConflictReason(
                new ScraperOptions { UsePublicationPathArtifacts = true },
                () => throw new InvalidOperationException(
                    "The gate must not need publication pointer state.")));

    [Fact]
    public void Admin_regeneration_is_rejected_even_when_the_source_flag_is_off()
    {
        // Defense in depth: a misconfigured or disabled source flag must not
        // re-open the live-promotion race.
        Assert.Equal(
            AdminPathRegenerationGate.StagingEnabledReason,
            AdminPathRegenerationGate.GetConflictReason(
                new ScraperOptions
                {
                    UsePublicationPathArtifacts = false,
                    EnableScrapePassPathGeneration = true,
                },
                () => new PublicationPointerState(1, null, null, 1, null)));
        Assert.Equal(
            AdminPathRegenerationGate.WorkingPublicationReason,
            AdminPathRegenerationGate.GetConflictReason(
                new ScraperOptions
                {
                    UsePublicationPathArtifacts = false,
                    EnableScrapePassPathGeneration = false,
                },
                () => new PublicationPointerState(1, null, 2, 1, null)));
    }

    [Fact]
    public void Admin_regeneration_is_allowed_only_in_legacy_live_mode()
        => Assert.Null(AdminPathRegenerationGate.GetConflictReason(
            new ScraperOptions
            {
                UsePublicationPathArtifacts = false,
                EnableScrapePassPathGeneration = false,
            },
            () => new PublicationPointerState(1, null, null, 1, null)));
}

/// <summary>
/// The supported production role configuration must never leave the catalog
/// without a path generator, and must never re-enable legacy live promotion.
/// </summary>
public sealed class DeploymentRolePathGenerationConfigTests
{
    [Fact]
    public void Service_role_reads_publication_bound_path_artifacts()
    {
        var role = ReadRoleEnv("fstservice-role.env");

        Assert.Equal("true", role["Scraper__UsePublicationPathArtifacts"]);
        Assert.Equal("false", role["Scraper__EnableAutomaticPathGeneration"]);

        // The service role must not run the worker-only generator.
        Assert.False(
            role.ContainsKey("Scraper__EnableScrapePassPathGeneration"));
    }

    [Fact]
    public void Worker_role_enables_the_publication_safe_generator()
    {
        var role = ReadRoleEnv("fstworker-role.env");

        Assert.Equal("true", role["Scraper__UsePublicationPathArtifacts"]);
        Assert.Equal("true", role["Scraper__EnableScrapePassPathGeneration"]);
        Assert.Equal("false", role["Scraper__EnableAutomaticPathGeneration"]);
        Assert.Equal(
            "false",
            role["Scraper__ScrapePassPathGenerationAllowChangedMaxima"]);
    }

    [Fact]
    public void Role_configurations_pass_option_validation()
    {
        foreach (var roleFile in
            new[] { "fstservice-role.env", "fstworker-role.env" })
        {
            var role = ReadRoleEnv(roleFile);
            var options = new ScraperOptions
            {
                UsePublicationPathArtifacts = ReadBool(
                    role,
                    "Scraper__UsePublicationPathArtifacts"),
                EnableScrapePassPathGeneration = ReadBool(
                    role,
                    "Scraper__EnableScrapePassPathGeneration"),
                EnableAutomaticPathGeneration = ReadBool(
                    role,
                    "Scraper__EnableAutomaticPathGeneration"),
            };
            if (options.EnableScrapePassPathGeneration)
            {
                // Secrets stay outside tracked role files; inject the
                // deployment prerequisite for option-shape validation.
                options.MidiEncryptionKey =
                    "0123456789abcdef0123456789abcdef";
            }
            if (role.TryGetValue(
                    "Scraper__ScrapePassPathGenerationMaxSongs",
                    out var maxSongs))
            {
                options.ScrapePassPathGenerationMaxSongs =
                    int.Parse(maxSongs);
            }
            if (role.TryGetValue(
                    "Scraper__ScrapePassPathGenerationTimeout",
                    out var timeout))
            {
                options.ScrapePassPathGenerationTimeout =
                    TimeSpan.Parse(timeout);
            }

            Assert.False(
                new ScraperOptionsValidator()
                    .Validate(null, options)
                    .Failed,
                $"{roleFile} must be a valid configuration.");
        }
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> role,
        string key)
        => role.TryGetValue(key, out var value)
           && bool.Parse(value);

    private static Dictionary<string, string> ReadRoleEnv(string fileName)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "deploy",
            "config",
            fileName);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            result[trimmed[..separator]] = trimmed[(separator + 1)..];
        }

        return result;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Repository root was not found.");
    }
}
