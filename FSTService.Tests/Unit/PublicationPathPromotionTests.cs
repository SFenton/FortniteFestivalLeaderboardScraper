using System.Text;
using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

/// <summary>
/// Phase B staged path promotion: additive schema, candidate-only staging,
/// and the publication-commit compare-and-swap into live <c>songs</c>.
/// </summary>
public sealed class PublicationPathPromotionTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    private MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Promotion_columns_are_additive_and_idempotent()
    {
        await SeedCatalogAsync("song-a");

        // Re-running the migration must not fail or change the shape.
        ExecuteNonQuery(PublicationPathArtifactSchema.Sql, static _ => { });
        ExecuteNonQuery(PublicationPathArtifactSchema.Sql, static _ => { });

        var columns = ReadColumns();
        Assert.Contains("promotion_pending", columns);
        Assert.Contains("promotion_attempt_id", columns);
        Assert.Contains("promotion_generation_id", columns);
        Assert.Contains("promotion_source", columns);
        Assert.Contains("promotion_staged_at", columns);
        Assert.Contains("expected_live_revision", columns);
        Assert.Contains("expected_live_generation_id", columns);

        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        Assert.False(ReadPromotionPending(publicationId, "song-a"));
        Assert.Empty(Db.GetPublicationPathPromotions(publicationId));
    }

    [Fact]
    public async Task Manifest_hash_is_sensitive_to_promotion_metadata()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        var before = ComputeManifestHash(publicationId);
        ExecuteNonQuery(
            """
            UPDATE publication_path_artifacts
            SET promotion_pending = TRUE,
                promotion_attempt_id = 'attempt-1',
                expected_live_revision = 0
            WHERE publication_id = @publicationId
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        Assert.NotEqual(before, ComputeManifestHash(publicationId));
    }

    [Fact]
    public async Task Staged_promotion_updates_candidate_only_and_rebinds_ready()
    {
        await SeedCatalogAsync("song-a", "song-b");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        var outcome = ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 1_234);

        Assert.Equal(PublicationPathPromotionOutcome.Applied, outcome);

        var candidate = ReadSnapshotRow(publicationId, "song-a");
        Assert.Equal(1, candidate.Revision);
        Assert.Equal("gen-song-a", candidate.GenerationId);
        Assert.Equal(1_234, candidate.MaxLeadScore);
        Assert.True(candidate.PromotionPending);
        Assert.Equal(0, candidate.ExpectedLiveRevision);
        Assert.Null(candidate.ExpectedLiveGenerationId);
        Assert.False(candidate.PathGenerationPending);

        // Live rows are untouched by staging.
        var live = ReadLiveSong("song-a");
        Assert.Equal(0, live.Revision);
        Assert.Null(live.GenerationId);
        Assert.Null(live.MaxLeadScore);

        var binding = ReadPathBinding(publicationId)!;
        Assert.Equal(
            PublicationPathArtifactSchema.ManifestBindingKind,
            binding.BindingKind);
        Assert.Equal(PublicationGenerationStatus.Ready, binding.Status);
        Assert.Equal(2, binding.RowCount);
        Assert.Equal(
            ComputeManifestHash(publicationId),
            binding.ContentHash);

        var promotions = Db.GetPublicationPathPromotions(publicationId);
        var promotion = Assert.Single(promotions);
        Assert.Equal("song-a", promotion.SongId);
        Assert.Equal(1, promotion.CandidateRevision);
        Assert.Equal(0, promotion.ExpectedLiveRevision);
        Assert.Equal(
            PublicationPathArtifactSchema.ScrapePassStagingSource,
            promotion.PromotionSource);
    }

    [Fact]
    public async Task Staged_promotion_reports_conflict_and_missing_rows()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        Assert.Equal(
            PublicationPathPromotionOutcome.Conflict,
            ApplyPromotion(
                scrapeId,
                publicationId,
                "song-a",
                expectedRevision: 7,
                expectedGenerationId: null,
                maxLead: 10));
        Assert.Equal(
            PublicationPathPromotionOutcome.SongMissing,
            ApplyPromotion(
                scrapeId,
                publicationId,
                "song-missing",
                expectedRevision: 0,
                expectedGenerationId: null,
                maxLead: 10));

        // Candidate is untouched and the binding stays ready.
        var candidate = ReadSnapshotRow(publicationId, "song-a");
        Assert.Equal(0, candidate.Revision);
        Assert.False(candidate.PromotionPending);
        Assert.Equal(
            PublicationGenerationStatus.Ready,
            ReadPathBinding(publicationId)!.Status);
    }

    [Fact]
    public async Task Staged_promotion_requires_a_rebuilt_canonical_songs_cache()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 123);
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);

        var failure = Assert.Throws<InvalidOperationException>(
            () => Db.PrepareScrapePublication(
                scrapeId,
                promoteCachedResponses: false));
        Assert.Contains(
            "canonical songs cache must be rebuilt",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staged_promotion_requires_the_building_working_publication()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        Assert.Equal(
            PublicationPathPromotionOutcome.PublicationNotStaging,
            ApplyPromotion(
                scrapeId,
                publicationId,
                "song-a",
                expectedRevision: 0,
                expectedGenerationId: null,
                maxLead: 10));
    }

    [Fact]
    public async Task Commit_promotes_staged_rows_with_the_publication_pointer()
    {
        await SeedCatalogAsync("song-a", "song-b");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        Assert.Equal(
            PublicationPathPromotionOutcome.Applied,
            ApplyPromotion(
                scrapeId,
                publicationId,
                "song-a",
                expectedRevision: 0,
                expectedGenerationId: null,
                maxLead: 4_242));
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        PublishWithStagedCache(scrapeId, publicationId);

        Assert.Equal(
            publicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);

        var live = ReadLiveSong("song-a");
        Assert.Equal(1, live.Revision);
        Assert.Equal("gen-song-a", live.GenerationId);
        Assert.Equal(4_242, live.MaxLeadScore);
        Assert.False(live.PathGenerationPending);

        var untouched = ReadLiveSong("song-b");
        Assert.Equal(0, untouched.Revision);
        Assert.Null(untouched.GenerationId);
    }

    [Fact]
    public async Task Commit_leaves_the_song_pending_when_the_provider_timestamp_changed()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 999);

        // An ordinary catalog refresh changed songs.last_modified mid-scrape.
        ExecuteNonQuery(
            """
            UPDATE songs
            SET last_modified = '2026-09-01T00:00:00.0000000Z'
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        PublishWithStagedCache(scrapeId, publicationId);

        var live = ReadLiveSong("song-a");
        Assert.Equal(1, live.Revision);
        Assert.Equal("gen-song-a", live.GenerationId);
        Assert.Equal(999, live.MaxLeadScore);
        Assert.True(live.PathGenerationPending);
    }

    [Fact]
    public async Task Commit_rejects_a_changed_live_generation_without_advancing_the_pointer()
    {
        await SeedCatalogAsync("song-a");
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(firstScrapeId, promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 777);

        // An out-of-band live generation won the race after staging.
        ExecuteNonQuery(
            """
            UPDATE songs
            SET path_generation_revision = path_generation_revision + 1,
                path_artifact_generation_id = 'gen-out-of-band'
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        var preparation =
            PrepareWithStagedCache(scrapeId, publicationId);

        var failure = Assert.Throws<PublicationPathPromotionConflictException>(
            () => Db.CommitPreparedScrapePublication(preparation));
        Assert.Equal(publicationId, failure.PublicationId);
        Assert.Equal(1, failure.ExpectedCount);
        Assert.Equal(0, failure.PromotedCount);

        var pointers = Db.GetPublicationPointerState();
        Assert.Equal(currentPublicationId, pointers.CurrentPublicationId);
        Assert.Equal(publicationId, pointers.WorkingPublicationId);

        var live = ReadLiveSong("song-a");
        Assert.Equal("gen-out-of-band", live.GenerationId);
        Assert.Null(live.MaxLeadScore);
    }

    [Fact]
    public async Task Deferred_commit_promotes_from_durable_database_state()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 555);
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        PrepareWithStagedCache(scrapeId, publicationId);

        // Restart: the commit was deferred and nothing is held in memory.
        var commitIntent = Db.BeginPublicationCommitIntent(scrapeId);
        Db.TransitionPublicationCommitIntentToDeferred(commitIntent);
        var deferred = Db.GetDeferredPublicationPreparation();
        Assert.NotNull(deferred);
        Assert.Equal(publicationId, deferred!.PublicationId);
        var promotions = Db.GetPublicationPathPromotions(publicationId);
        Assert.Single(promotions);

        Db.CommitPreparedScrapePublication(deferred);

        var live = ReadLiveSong("song-a");
        Assert.Equal(1, live.Revision);
        Assert.Equal(555, live.MaxLeadScore);
        Assert.Equal(
            publicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
    }

    [Fact]
    public async Task Deferred_commit_rejects_a_stale_path_manifest()
    {
        await SeedCatalogAsync("song-a");
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            firstScrapeId,
            promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        var preparation = Db.PrepareScrapePublication(
            scrapeId,
            promoteCachedResponses: false);
        DowngradeBindingToManifestVersion1(publicationId);

        var failure = Assert.Throws<InvalidOperationException>(
            () => Db.CommitPreparedScrapePublication(preparation));
        Assert.Contains(
            "hash-valid path artifact manifest",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            currentPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.Equal(
            publicationId,
            Db.GetPublicationPointerState().WorkingPublicationId);
    }

    [Fact]
    public async Task Deferred_commit_rejects_an_inherited_songs_cache_for_staged_paths()
    {
        await SeedCatalogAsync("song-a");
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            firstScrapeId,
            promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 456);
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        var preparation =
            PrepareWithStagedCache(scrapeId, publicationId);
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json =
                    jsonb_set(
                        binding_json,
                        '{inheritedFromPublicationId}',
                        to_jsonb(@currentPublicationId::bigint))
            WHERE publication_id = @publicationId
              AND surface_name = 'api_response_cache'
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "currentPublicationId",
                    currentPublicationId!.Value);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
            });

        var failure = Assert.Throws<InvalidOperationException>(
            () => Db.CommitPreparedScrapePublication(preparation));
        Assert.Contains(
            "promotion-compatible canonical songs cache",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            currentPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.Equal(
            publicationId,
            Db.GetPublicationPointerState().WorkingPublicationId);
    }

    [Fact]
    public async Task Failed_candidate_leaves_the_current_publication_and_live_songs_unchanged()
    {
        await SeedCatalogAsync("song-a");
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(firstScrapeId, promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 321);

        Db.FailScrapeRun(scrapeId, "scrape", "injected failure");

        var pointers = Db.GetPublicationPointerState();
        Assert.Equal(currentPublicationId, pointers.CurrentPublicationId);
        Assert.Null(pointers.WorkingPublicationId);
        Assert.Empty(Db.GetPublicationPathPromotions(publicationId));

        var live = ReadLiveSong("song-a");
        Assert.Equal(0, live.Revision);
        Assert.Null(live.GenerationId);
        Assert.Null(live.MaxLeadScore);
    }

    [Fact]
    public async Task Working_scope_reads_staged_candidate_state()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ApplyPromotion(
            scrapeId,
            publicationId,
            "song-a",
            expectedRevision: 0,
            expectedGenerationId: null,
            maxLead: 8_888);

        var store = new PathDataStore(
            DataSource,
            null,
            Options.Create(new ScraperOptions
            {
                UsePublicationPathArtifacts = true,
            }));

        using (var scope = store.BeginPublicationRead(publicationId))
        {
            var scores = store.GetAllMaxScores();
            Assert.Equal(8_888, scores["song-a"].MaxLeadScore);
            var states = store.GetPathGenerationStates();
            Assert.Equal(
                ["Solo_Guitar"],
                states["song-a"].ExpectedInstruments);
        }

        // Live reads still see the untouched song row.
        Assert.Empty(store.GetLiveAllMaxScores());
    }

    [Fact]
    public async Task Schema_upgrade_rebinds_active_pointer_snapshots_to_the_current_manifest_version()
    {
        await SeedCatalogAsync("song-a", "song-b");

        // Publication 1 becomes previous, publication 2 becomes current.
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(firstScrapeId, promoteCachedResponses: false);
        var previousPublicationId =
            Db.GetPublicationGenerationForScrape(firstScrapeId)!.PublicationId;

        var secondScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(secondScrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(secondScrapeId, promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationGenerationForScrape(secondScrapeId)!
                .PublicationId;

        var pointers = Db.GetPublicationPointerState();
        Assert.Equal(currentPublicationId, pointers.CurrentPublicationId);
        Assert.Equal(previousPublicationId, pointers.PreviousPublicationId);

        // Simulate a live Phase A database: manifest-version-1 bindings whose
        // content hash came from the pre-Phase-B hash function.
        var legacyHashes = new Dictionary<long, string>();
        foreach (var publicationId in
            new[] { currentPublicationId, previousPublicationId })
        {
            var legacyHash = DowngradeBindingToManifestVersion1(publicationId);
            legacyHashes[publicationId] = legacyHash;
            var binding = ReadPathBinding(publicationId)!;
            Assert.Equal(legacyHash, binding.ContentHash);
            Assert.NotEqual(
                ComputeManifestHash(publicationId),
                binding.ContentHash);

            // A version-1 binding must fail closed for publication reads.
            var staleStore = CreateStore(usePublicationArtifacts: true);
            using var staleScope =
                staleStore.BeginPublicationRead(publicationId);
            Assert.Throws<PublicationPathArtifactsUnavailableException>(
                () => staleStore.GetPathGenerationStates());
        }

        var capturedBefore = ReadCapturedAt(currentPublicationId, "song-a");
        var previousCapturedBefore =
            ReadCapturedAt(previousPublicationId, "song-a");

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        foreach (var publicationId in
            new[] { currentPublicationId, previousPublicationId })
        {
            var binding = ReadPathBinding(publicationId)!;
            Assert.Equal(
                PublicationPathArtifactSchema.ManifestBindingKind,
                binding.BindingKind);
            Assert.Equal(PublicationGenerationStatus.Ready, binding.Status);
            Assert.Equal(2, binding.RowCount);
            Assert.NotEqual(legacyHashes[publicationId], binding.ContentHash);
            Assert.Equal(
                ComputeManifestHash(publicationId),
                binding.ContentHash);

            using var document = JsonDocument.Parse(binding.BindingJson);
            Assert.Equal(
                PublicationPathArtifactSchema.ManifestVersion,
                document.RootElement
                    .GetProperty("manifestVersion")
                    .GetInt32());
            Assert.Equal(
                PublicationPathArtifactSchema.ContractVersion,
                document.RootElement
                    .GetProperty("contractVersion")
                    .GetInt32());

            // Publication reads work again for both retained pointers.
            var store = CreateStore(usePublicationArtifacts: true);
            using var scope = store.BeginPublicationRead(publicationId);
            var states = store.GetPathGenerationStates();
            Assert.Equal(2, states.Count);
        }

        // The previous publication is rebound by the explicit schema upgrade
        // source; snapshot rows are never rewritten.
        using (var previousBinding = JsonDocument.Parse(
            ReadPathBinding(previousPublicationId)!.BindingJson))
        {
            Assert.Equal(
                PublicationPathArtifactSchema.SchemaUpgradeSource,
                previousBinding.RootElement
                    .GetProperty("source")
                    .GetString());
        }

        Assert.Equal(
            capturedBefore,
            ReadCapturedAt(currentPublicationId, "song-a"));
        Assert.Equal(
            previousCapturedBefore,
            ReadCapturedAt(previousPublicationId, "song-a"));

        // Rerunning the migration is idempotent for bindings and rows alike.
        var currentHash = ReadPathBinding(currentPublicationId)!.ContentHash;
        var previousHash = ReadPathBinding(previousPublicationId)!.ContentHash;
        await DatabaseInitializer.EnsureSchemaAsync(DataSource);
        Assert.Equal(
            currentHash,
            ReadPathBinding(currentPublicationId)!.ContentHash);
        Assert.Equal(
            previousHash,
            ReadPathBinding(previousPublicationId)!.ContentHash);
        Assert.Equal(
            capturedBefore,
            ReadCapturedAt(currentPublicationId, "song-a"));
        Assert.Equal(
            previousCapturedBefore,
            ReadCapturedAt(previousPublicationId, "song-a"));
        Assert.Equal(2, ReadSnapshot(currentPublicationId).Count);
        Assert.Equal(2, ReadSnapshot(previousPublicationId).Count);
    }

    [Fact]
    public async Task Schema_upgrade_rebinds_a_working_publication_snapshot()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        var workingPublicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        Assert.Equal(
            workingPublicationId,
            Db.GetPublicationPointerState().WorkingPublicationId);

        var legacyHash =
            DowngradeBindingToManifestVersion1(workingPublicationId);

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        var binding = ReadPathBinding(workingPublicationId)!;
        Assert.NotEqual(legacyHash, binding.ContentHash);
        Assert.Equal(
            ComputeManifestHash(workingPublicationId),
            binding.ContentHash);
        using var document = JsonDocument.Parse(binding.BindingJson);
        Assert.Equal(
            PublicationPathArtifactSchema.ManifestVersion,
            document.RootElement.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(
            PublicationPathArtifactSchema.SchemaUpgradeSource,
            document.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void Manifest_version_is_independent_of_the_route_contract_version()
    {
        Assert.Equal(
            PublicationRouteSurfaceContractCatalog.ContractVersion,
            PublicationPathArtifactSchema.ContractVersion);
        Assert.NotEqual(
            PublicationPathArtifactSchema.ContractVersion,
            PublicationPathArtifactSchema.ManifestVersion);
    }

    [Fact]
    public async Task Release_gate_accepts_a_current_version_manifest()
    {
        await SeedCatalogAsync("song-a", "song-b");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        var state = await PublicationPathArtifactReleaseGate.ReadAsync(
            DataSource);

        Assert.True(state.IsReleased);
        Assert.Equal(
            PublicationPathArtifactSchema.ManifestVersion,
            state.ManifestVersion);
        Assert.Equal(
            PublicationPathArtifactSchema.ContractVersion,
            state.ContractVersion);
        Assert.Equal(2, state.SnapshotRowCount);
        Assert.Equal(2, state.ExpectedRowCount);
        Assert.Equal(state.CanonicalContentHash, state.BindingContentHash);
        await PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
            DataSource);
    }

    [Fact]
    public async Task Release_gate_rejects_a_manifest_version_1_binding()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationPointerState().CurrentPublicationId!.Value;
        DowngradeBindingToManifestVersion1(publicationId);

        var state = await PublicationPathArtifactReleaseGate.ReadAsync(
            DataSource);
        Assert.False(state.IsReleased);
        Assert.Null(state.ManifestVersion);

        var failure =
            await Assert.ThrowsAsync<PublicationPathArtifactReleaseException>(
                () => PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
                    DataSource));
        Assert.Contains(
            "manifestVersion",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Start the API/schema-initializing role first",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_gate_rejects_a_missing_binding()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationPointerState().CurrentPublicationId!.Value;
        ExecuteNonQuery(
            """
            DELETE FROM publication_surface_bindings
            WHERE publication_id = @publicationId
              AND surface_name = 'path_artifacts'
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        var failure =
            await Assert.ThrowsAsync<PublicationPathArtifactReleaseException>(
                () => PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
                    DataSource));
        Assert.Contains(
            "no path_artifacts surface binding",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_gate_rejects_an_incomplete_snapshot()
    {
        await SeedCatalogAsync("song-a", "song-b");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationPointerState().CurrentPublicationId!.Value;
        ExecuteNonQuery(
            """
            DELETE FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = 'song-b'
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        var failure =
            await Assert.ThrowsAsync<PublicationPathArtifactReleaseException>(
                () => PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
                    DataSource));
        Assert.Contains(
            "snapshot covers 1 of 2 catalog songs",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_gate_allows_a_database_with_no_publication()
    {
        await SeedCatalogAsync("song-a");

        var state = await PublicationPathArtifactReleaseGate.ReadAsync(
            DataSource);

        Assert.Null(state.CurrentPublicationId);
        Assert.True(state.IsReleased);
    }

    [Fact]
    public async Task Api_only_role_release_gate_accepts_a_current_version_manifest()
    {
        var options = new ScraperOptions
        {
            ApiOnly = true,
            SkipStartupSchemaInitialization = false,
            UsePublicationPathArtifacts = true,
        };
        Assert.True(options.SkipsStartupSchemaInitialization);
        Assert.True(options.RequiresPublicationPathArtifactReleaseGate);

        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        await PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
            DataSource);
    }

    [Fact]
    public async Task Api_only_role_release_gate_rejects_a_manifest_version_1_binding()
    {
        var options = new ScraperOptions
        {
            ApiOnly = true,
            UsePublicationPathArtifacts = true,
        };
        Assert.True(options.RequiresPublicationPathArtifactReleaseGate);

        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        DowngradeBindingToManifestVersion1(
            Db.GetPublicationPointerState().CurrentPublicationId!.Value);

        var failure =
            await Assert.ThrowsAsync<PublicationPathArtifactReleaseException>(
                () => PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
                    DataSource));
        Assert.Contains(
            "Start the API/schema-initializing role first",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_only_role_release_gate_allows_a_database_with_no_publication()
    {
        Assert.True(
            new ScraperOptions
            {
                ApiOnly = true,
                UsePublicationPathArtifacts = true,
            }.RequiresPublicationPathArtifactReleaseGate);

        await SeedCatalogAsync("song-a");

        var state = await PublicationPathArtifactReleaseGate.ReadAsync(
            DataSource);
        Assert.Null(state.CurrentPublicationId);
        Assert.True(state.IsReleased);
        await PublicationPathArtifactReleaseGate.EnsureReleasedAsync(
            DataSource);
    }

    [Theory]
    // API-only, explicit skip-schema, and rollout read-only are no-DDL roles.
    [InlineData(true, false, false, true, true)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, false, true, true, true)]
    [InlineData(true, true, true, true, true)]
    // A schema-initializing role applies the release itself.
    [InlineData(false, false, false, true, false)]
    // Live-row reads need no publication release.
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, false, false)]
    public void Release_gate_condition_mirrors_no_ddl_roles(
        bool apiOnly,
        bool skipSchemaInitialization,
        bool rolloutReadOnlyStartup,
        bool usePublicationPathArtifacts,
        bool expectedGate)
    {
        var options = new ScraperOptions
        {
            ApiOnly = apiOnly,
            SkipStartupSchemaInitialization = skipSchemaInitialization,
            RolloutReadOnlyStartup = rolloutReadOnlyStartup,
            UsePublicationPathArtifacts = usePublicationPathArtifacts,
        };

        Assert.Equal(
            apiOnly
                || skipSchemaInitialization
                || rolloutReadOnlyStartup,
            options.SkipsStartupSchemaInitialization);
        Assert.Equal(
            expectedGate,
            options.RequiresPublicationPathArtifactReleaseGate);
    }

    /// <summary>
    /// Rewrites one active binding as a Phase A manifest-version-1 binding
    /// whose content hash predates the Phase B hash function.
    /// </summary>
    private string DowngradeBindingToManifestVersion1(long publicationId)
    {
        var legacyHash = new string('0', 64);
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json =
                    (binding_json - 'manifestVersion')
                    || jsonb_build_object('source', 'legacy_live_backfill'),
                content_hash = @legacyHash
            WHERE publication_id = @publicationId
              AND surface_name = 'path_artifacts'
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue("legacyHash", legacyHash);
            });
        return legacyHash;
    }

    private PublicationPathPromotionOutcome ApplyPromotion(
        long scrapeId,
        long publicationId,
        string songId,
        long expectedRevision,
        string? expectedGenerationId,
        int maxLead)
        => Db.ApplyWorkingPublicationPathPromotion(
            new PublicationPathPromotionRequest(
                publicationId,
                scrapeId,
                songId,
                expectedRevision,
                expectedGenerationId,
                "2026-07-31T12:00:00.0000000Z",
                new PathGenerationPromotion(
                    $"attempt-{songId}",
                    songId,
                    expectedRevision,
                    $"gen-{songId}",
                    new string('a', 64),
                    "2026-07-31T12:00:00.0000000Z",
                    new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
                    new PathGenerationRuntimeIdentity(
                        "1.16.4",
                        new string('b', 64),
                        PathGenerationProfiles.PlasticDrumsV4),
                    ["Solo_Guitar"],
                    new SongMaxScores { MaxLeadScore = maxLead })));

    private PublicationPreparationResult PrepareWithStagedCache(
        long scrapeId,
        long publicationId)
    {
        var json = Encoding.UTF8.GetBytes(
            """{"count":1,"currentSeason":14,"songs":[{"songId":"song-a"}]}""");
        Db.BulkSetCachedResponsesStaging(
            [
                (
                    Key: PublicationApiCacheKeys.Songs,
                    Json: json,
                    ETag: ResponseCacheService.ComputeETag(json))
            ],
            publicationId);
        return Db.PrepareScrapePublication(
            scrapeId,
            promoteCachedResponses: true);
    }

    private void PublishWithStagedCache(
        long scrapeId,
        long publicationId)
    {
        var preparation =
            PrepareWithStagedCache(scrapeId, publicationId);
        Db.CommitPreparedScrapePublication(preparation);
    }

    private PathDataStore CreateStore(bool usePublicationArtifacts) =>
        new(
            DataSource,
            null,
            Options.Create(new ScraperOptions
            {
                UsePublicationPathArtifacts = usePublicationArtifacts,
            }));

    private DateTime ReadCapturedAt(long publicationId, string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = @songId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue("songId", songId);
        return (DateTime)command.ExecuteScalar()!;
    }

    private Dictionary<string, long> ReadSnapshot(long publicationId)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT song_id, path_generation_revision
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private async Task SeedCatalogAsync(params string[] songIds)
    {
        var persistence = new FestivalPersistence(DataSource);
        await persistence.SaveSongsVersionedAsync(
            songIds.Select(CreateCatalogSong).ToArray());
    }

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

    private PublicationSurfaceBinding? ReadPathBinding(long publicationId)
        => Db.GetPublicationSurfaceBindings(publicationId)
            .SingleOrDefault(static binding =>
                binding.SurfaceName == PublicationSurfaceNames.PathArtifacts);

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

    private string ComputeManifestHash(long publicationId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT publication_path_artifact_manifest_sha256(@publicationId)";
        command.Parameters.AddWithValue("publicationId", publicationId);
        return (string)command.ExecuteScalar()!;
    }

    private HashSet<string> ReadColumns()
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = 'publication_path_artifacts'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private bool ReadPromotionPending(long publicationId, string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT promotion_pending
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = @songId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue("songId", songId);
        return (bool)command.ExecuteScalar()!;
    }

    private CandidateRow ReadSnapshotRow(long publicationId, string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path_generation_revision,
                   path_artifact_generation_id,
                   max_lead_score,
                   promotion_pending,
                   expected_live_revision,
                   expected_live_generation_id,
                   path_generation_pending
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = @songId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue("songId", songId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new CandidateRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6));
    }

    private LiveSongRow ReadLiveSong(string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path_generation_revision,
                   path_artifact_generation_id,
                   max_lead_score,
                   path_generation_pending
            FROM songs
            WHERE song_id = @songId
            """;
        command.Parameters.AddWithValue("songId", songId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new LiveSongRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetBoolean(3));
    }

    private sealed record CandidateRow(
        long Revision,
        string? GenerationId,
        int? MaxLeadScore,
        bool PromotionPending,
        long? ExpectedLiveRevision,
        string? ExpectedLiveGenerationId,
        bool PathGenerationPending);

    private sealed record LiveSongRow(
        long Revision,
        string? GenerationId,
        int? MaxLeadScore,
        bool PathGenerationPending);
}
