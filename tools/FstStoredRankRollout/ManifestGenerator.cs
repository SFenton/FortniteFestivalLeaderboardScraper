using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace FstStoredRankRollout;

public sealed class ManifestGenerator
{
    private readonly NpgsqlDataSource _dataSource;

    public ManifestGenerator(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<RolloutManifest> GenerateAsync(
        int seed,
        int maxMappedScopes,
        int maxTieScopesPerInstrument,
        string serviceImageReference,
        string serviceImageId,
        string workerContainerId,
        string workerImageReference,
        string workerImageId,
        string workerContainerStatus,
        string workerContainerState,
        string serviceDatabaseHost,
        int serviceDatabasePort,
        string serviceDatabaseName,
        string serviceDatabaseUsername,
        string postgresContainerId,
        string postgresImageReference,
        string postgresImageId,
        IReadOnlyList<string> postgresNetworkNames,
        IReadOnlyList<string> postgresNetworkAliases,
        IReadOnlyList<string> postgresServerAddresses,
        IReadOnlyList<PostgresNetworkBinding> postgresNetworkBindings,
        string evidenceMountTarget,
        string evidenceMountSource,
        string evidenceMountFileSystem,
        CancellationToken cancellationToken)
    {
        RolloutImagePin.Validate(serviceImageReference, serviceImageId);
        if (string.IsNullOrWhiteSpace(workerContainerId)
            || string.IsNullOrWhiteSpace(workerImageReference)
            || !RolloutImagePin.IsValidImageId(workerImageId)
            || workerContainerStatus is not ("exited" or "created")
            || string.IsNullOrWhiteSpace(workerContainerState))
        {
            throw new InvalidDataException("Worker runtime pin is incomplete or not stopped.");
        }
        RolloutEvidenceMount.Validate(
            evidenceMountTarget,
            evidenceMountSource,
            evidenceMountFileSystem);
        if (string.IsNullOrWhiteSpace(serviceDatabaseHost)
            || serviceDatabasePort <= 0
            || string.IsNullOrWhiteSpace(serviceDatabaseName)
            || string.IsNullOrWhiteSpace(serviceDatabaseUsername)
            || string.IsNullOrWhiteSpace(postgresContainerId)
            || string.IsNullOrWhiteSpace(postgresImageReference)
            || !RolloutImagePin.IsValidImageId(postgresImageId)
            || postgresNetworkNames.Count == 0
            || postgresNetworkAliases.Count == 0
            || postgresServerAddresses.Count == 0
            || postgresNetworkBindings.Count != 1)
        {
            throw new InvalidDataException("Runtime PostgreSQL binding is incomplete.");
        }
        var databaseIdentity = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
            _dataSource,
            cancellationToken);
        var serviceDatabaseTarget = new ServiceDatabaseTarget
        {
            Host = serviceDatabaseHost,
            Port = serviceDatabasePort,
            Database = serviceDatabaseName,
            Username = serviceDatabaseUsername,
        };
        var databaseBinding = new RolloutManifest
        {
            DatabaseIdentity = databaseIdentity,
            ServiceDatabaseTarget = serviceDatabaseTarget,
            PostgresContainerId = postgresContainerId,
            PostgresImageReference = postgresImageReference,
            PostgresImageId = postgresImageId,
            PostgresNetworkNames = postgresNetworkNames,
            PostgresNetworkAliases = postgresNetworkAliases,
            PostgresServerAddresses = postgresServerAddresses,
            PostgresNetworkBindings = postgresNetworkBindings,
        };
        var databaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
            databaseBinding,
            databaseIdentity);
        if (!databaseAttestation.Passed)
        {
            throw new InvalidDataException(
                "Evidence, service, and Postgres container targets do not match: " +
                string.Join(", ", databaseAttestation.Failures));
        }
        var (publishedScrapeId, frozen) = await ReadPublicationIdentityAsync(cancellationToken);
        if (frozen)
            throw new InvalidOperationException("Public reads are frozen; rollout evidence cannot be generated.");
        var maxScores = new PathDataStore(
                _dataSource,
                NullLogger<PathDataStore>.Instance)
            .GetAllMaxScores();
        var candidates = await ReadScopeCandidatesAsync(
            maxMappedScopes,
            maxScores,
            cancellationToken);
        var selected = DeterministicRollout.SelectScopes(
                candidates,
                GlobalLeaderboardScraper.AllInstruments,
                seed)
            .ToDictionary(static scope => scope.Id, StringComparer.Ordinal);

        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            var tieCandidates = DeterministicRollout.StableOrder(
                    candidates.Where(scope =>
                        string.Equals(scope.Instrument, instrument, StringComparison.Ordinal)
                        && scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                        && scope.PublishedRowCount > 1
                        && scope.ProjectionGeneration.HasValue),
                    seed,
                    $"tie:{instrument}")
                .Take(maxTieScopesPerInstrument);
            foreach (var candidate in tieCandidates)
            {
                var ties = await ReadTieEvidenceAsync(candidate, cancellationToken);
                if (ties.Count == 0)
                    continue;
                selected[candidate.Id] = Clone(candidate, exactScoreTimeTies: ties);
                break;
            }
        }

        foreach (var candidate in DeterministicRollout.StableOrder(
                     candidates.Where(static scope =>
                         scope.HasActiveOverlay
                         && scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                         && scope.PublishedRowCount > 0),
                     seed,
                     "overlay-derived-row"))
        {
            var overlayRows = await ReadOverlayDerivedRowsAsync(candidate, cancellationToken);
            if (overlayRows.Count == 0)
                continue;
            selected[candidate.Id] = Clone(candidate, overlayDerivedRows: overlayRows);
            break;
        }

        var thresholdFound = false;
        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            var thresholdCandidates = DeterministicRollout.StableOrder(
                    candidates.Where(scope =>
                        string.Equals(scope.Instrument, instrument, StringComparison.Ordinal)
                        && scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                        && scope.PublishedRowCount > 1
                        && scope.RawMaxScore is > 0),
                    seed,
                    $"threshold-boundary:{instrument}")
                .Take(maxTieScopesPerInstrument);
            foreach (var candidate in thresholdCandidates)
            {
                var threshold = ReadThresholdBoundaryEvidence(candidate, seed);
                if (threshold is null)
                    continue;
                selected[candidate.Id] = Clone(candidate, thresholdBoundary: threshold);
                thresholdFound = true;
                break;
            }
            if (thresholdFound)
                break;
        }

        var selectionGuard = await ReadSelectionGuardAsync(
            selected.Values,
            cancellationToken);
        var refreshedMaxScores = new PathDataStore(
                _dataSource,
                NullLogger<PathDataStore>.Instance)
            .GetAllMaxScores();
        var refreshedCandidates = (await ReadScopeCandidatesAsync(
                maxMappedScopes,
                refreshedMaxScores,
                cancellationToken))
            .ToDictionary(static scope => scope.Id, StringComparer.Ordinal);
        foreach (var selectedId in selected.Keys.ToArray())
        {
            if (!refreshedCandidates.TryGetValue(selectedId, out var refreshed))
            {
                throw new InvalidOperationException(
                    $"Selected published scope disappeared during manifest generation: {selectedId}");
            }
            selected[selectedId] = refreshed;
        }

        var enrichedScopes = new List<ScopeEvidence>();
        foreach (var scope in selected.Values
                     .OrderBy(static value => value.Instrument, StringComparer.Ordinal)
                     .ThenBy(static value => value.SongId, StringComparer.Ordinal))
        {
            var ties = await ReadTieEvidenceAsync(scope, cancellationToken);
            var overlayRows = await ReadOverlayDerivedRowsAsync(scope, cancellationToken);
            var thresholdBoundary = ReadThresholdBoundaryEvidence(scope, seed);
            var samples = ReadSampleAccounts(scope, ties, seed);
            enrichedScopes.Add(Clone(
                scope,
                exactScoreTimeTies: ties,
                sampleAccounts: samples,
                overlayDerivedRows: overlayRows,
                thresholdBoundary: thresholdBoundary));
        }

        var rowCases = BuildRowCases(enrichedScopes);
        var apiWorkloads = BuildApiWorkloads(enrichedScopes, seed);
        var coverage = DeterministicRollout.BuildCoverage(
            enrichedScopes,
            rowCases,
            apiWorkloads,
            GlobalLeaderboardScraper.AllInstruments,
            candidates
                .Select(static scope => scope.SourceClass)
                .Distinct()
                .ToArray());
        var manifest = new RolloutManifest
        {
            Seed = seed,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            PublishedScrapeId = publishedScrapeId,
            PublicReadsFrozen = frozen,
            ServiceImageReference = serviceImageReference,
            ServiceImageId = serviceImageId,
            WorkerContainerId = workerContainerId,
            WorkerImageReference = workerImageReference,
            WorkerImageId = workerImageId,
            WorkerContainerStatus = workerContainerStatus,
            WorkerContainerState = workerContainerState,
            DatabaseIdentity = databaseIdentity,
            ServiceDatabaseTarget = serviceDatabaseTarget,
            PostgresContainerId = postgresContainerId,
            PostgresImageReference = postgresImageReference,
            PostgresImageId = postgresImageId,
            PostgresNetworkNames = postgresNetworkNames
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PostgresNetworkAliases = postgresNetworkAliases
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PostgresServerAddresses = postgresServerAddresses
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PostgresNetworkBindings = postgresNetworkBindings
                .OrderBy(static binding => binding.NetworkName, StringComparer.Ordinal)
                .Select(static binding => new PostgresNetworkBinding
                {
                    NetworkName = binding.NetworkName,
                    NetworkId = binding.NetworkId,
                    ServiceAlias = binding.ServiceAlias,
                    ExclusiveOwnerContainerId = binding.ExclusiveOwnerContainerId,
                    ServerAddresses = binding.ServerAddresses
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray(),
            EvidenceMountTarget = evidenceMountTarget,
            EvidenceMountSource = evidenceMountSource,
            EvidenceMountFileSystem = evidenceMountFileSystem,
            SelectionGuardFingerprint = selectionGuard,
            RequiredInstruments = GlobalLeaderboardScraper.AllInstruments.ToArray(),
            Scopes = enrichedScopes,
            RowCases = rowCases,
            ApiWorkloads = apiWorkloads,
            Coverage = coverage,
        };
        manifest.SelectionFingerprint = DeterministicRollout.ComputeManifestFingerprint(manifest);

        var (endingPublishedScrapeId, endingFrozen) = await ReadPublicationIdentityAsync(cancellationToken);
        if (endingPublishedScrapeId != publishedScrapeId || endingFrozen != frozen)
        {
            throw new InvalidOperationException(
                "Publication state changed while the manifest was generated; discard the mixed snapshot.");
        }
        var endingSelectionGuard = await ReadSelectionGuardAsync(
            selected.Values,
            cancellationToken);
        if (!string.Equals(selectionGuard, endingSelectionGuard, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Selected source, projection, or overlay state changed while the manifest was generated.");
        }
        var endingDatabaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
                _dataSource,
                cancellationToken));
        if (!endingDatabaseAttestation.Passed)
        {
            throw new InvalidOperationException(
                "Database identity changed while the manifest was generated: " +
                string.Join(", ", endingDatabaseAttestation.Failures));
        }

        return manifest;
    }

    public async Task<ManifestGuardReport> ValidateGuardAsync(
        RolloutManifest manifest,
        CancellationToken cancellationToken)
    {
        var databaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
                _dataSource,
                cancellationToken));
        var (publishedScrapeId, frozen) = await ReadPublicationIdentityAsync(cancellationToken);
        var observedGuard = await ReadSelectionGuardAsync(
            manifest.Scopes,
            cancellationToken);
        var failures = new List<string>();
        if (!databaseAttestation.Passed)
        {
            failures.AddRange(databaseAttestation.Failures.Select(
                static failure => $"database-identity:{failure}"));
        }
        if (publishedScrapeId != manifest.PublishedScrapeId)
        {
            failures.Add(
                $"published-scrape:{publishedScrapeId}:expected:{manifest.PublishedScrapeId}");
        }
        if (frozen)
            failures.Add("public-reads-frozen");
        if (!string.Equals(
                observedGuard,
                manifest.SelectionGuardFingerprint,
                StringComparison.Ordinal))
        {
            failures.Add("selection-guard-changed");
        }

        return new ManifestGuardReport
        {
            ObservedAtUtc = DateTimeOffset.UtcNow,
            ExpectedPublishedScrapeId = manifest.PublishedScrapeId,
            PublishedScrapeId = publishedScrapeId,
            PublicReadsFrozen = frozen,
            ExpectedGuardFingerprint = manifest.SelectionGuardFingerprint,
            ObservedGuardFingerprint = observedGuard,
            DatabaseAttestation = databaseAttestation,
            Passed = failures.Count == 0,
            Failures = failures,
        };
    }

    internal async Task<string> ReadSelectionGuardAsync(
        IEnumerable<ScopeEvidence> scopes,
        CancellationToken cancellationToken)
    {
        var selected = scopes
            .OrderBy(static scope => scope.Instrument, StringComparer.Ordinal)
            .ThenBy(static scope => scope.SongId, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
            return Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested AS (
                SELECT *
                FROM unnest(@songIds::TEXT[], @instruments::TEXT[])
                    AS requested(song_id, instrument)
            ),
            {PublishedSoloScopeSql.CurrentSourcesCte}
            SELECT
                requested.instrument,
                requested.song_id,
                source.source_kind,
                source.source_snapshot_id,
                source.source_scrape_id,
                source.projection_source_snapshot_id,
                source.row_count,
                mapped.content_fingerprint,
                mapped.coverage_fingerprint,
                scope.projection_generation,
                scope.row_count,
                scope.source_snapshot_id,
                scope.status,
                scope.updated_at,
                projection.physical_row_count,
                projection.max_computed_at,
                overlay.overlay_row_count,
                overlay.max_updated_at,
                song.path_generation_revision,
                song.max_lead_score,
                song.max_bass_score,
                song.max_drums_score,
                song.max_vocals_score,
                song.max_pro_lead_score,
                song.max_pro_bass_score,
                song.max_pro_cymbals_score,
                song.max_pro_drums_score
            FROM requested
            JOIN published_sources source
              ON source.song_id = requested.song_id
             AND source.instrument = requested.instrument
            JOIN leaderboard_published_scope_source mapped
              ON mapped.published_scrape_id = source.published_scrape_id
             AND mapped.song_id = source.song_id
             AND mapped.instrument = source.instrument
             AND mapped.scope_kind = 'alltime'
            LEFT JOIN solo_current_projection_scope scope
              ON scope.song_id = requested.song_id
             AND scope.instrument = requested.instrument
            LEFT JOIN songs song
              ON song.song_id = requested.song_id
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::BIGINT AS physical_row_count,
                       MAX(current.computed_at) AS max_computed_at
                FROM current_leaderboard_entries current
                WHERE current.song_id = requested.song_id
                  AND current.instrument = requested.instrument
                  AND current.projection_generation = scope.projection_generation
            ) projection ON TRUE
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::BIGINT AS overlay_row_count,
                       MAX(current_overlay.last_updated_at) AS max_updated_at
                FROM leaderboard_entries_overlay current_overlay
                WHERE current_overlay.song_id = requested.song_id
                  AND current_overlay.instrument = requested.instrument
            ) overlay ON TRUE
            ORDER BY requested.instrument, requested.song_id
            """;
        command.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = selected.Select(static scope => scope.SongId).ToArray();
        command.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = selected.Select(static scope => scope.Instrument).ToArray();
        var builder = new StringBuilder();
        var rowCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rowCount++;
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (index > 0)
                    builder.Append('\u001f');
                builder.Append(reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture));
            }
            builder.Append('\n');
        }
        if (rowCount != selected.Length)
        {
            throw new InvalidOperationException(
                $"Selection guard resolved {rowCount} of {selected.Length} scopes.");
        }
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private async Task<(long PublishedScrapeId, bool Frozen)> ReadPublicationIdentityAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(published_scrape_id, 0),
                   COALESCE(public_reads_frozen, FALSE)
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("scrape_publication_state is missing.");
        var publishedScrapeId = reader.GetInt64(0);
        if (publishedScrapeId <= 0)
            throw new InvalidOperationException("No published scrape is selected.");
        return (publishedScrapeId, reader.GetBoolean(1));
    }

    private async Task<IReadOnlyList<ScopeEvidence>> ReadScopeCandidatesAsync(
        int maxMappedScopes,
        IReadOnlyDictionary<string, SongMaxScores> maxScores,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await ReadOnlyPostgres.BeginRepeatableReadOnlyAsync(
            connection,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH {PublishedSoloScopeSql.CurrentSourcesCte}
            SELECT
                source.published_scrape_id,
                source.song_id,
                source.instrument,
                source.source_kind,
                source.source_snapshot_id,
                source.source_scrape_id,
                source.projection_source_snapshot_id,
                source.row_count,
                mapped.content_fingerprint,
                mapped.coverage_fingerprint,
                scope.projection_generation,
                scope.row_count,
                scope.source_snapshot_id,
                scope.status,
                EXISTS (
                    SELECT 1
                    FROM leaderboard_entries_overlay overlay
                    WHERE overlay.song_id = source.song_id
                      AND overlay.instrument = source.instrument
                ) AS has_active_overlay
            FROM published_sources source
            JOIN leaderboard_published_scope_source mapped
              ON mapped.published_scrape_id = source.published_scrape_id
             AND mapped.song_id = source.song_id
             AND mapped.instrument = source.instrument
             AND mapped.scope_kind = 'alltime'
             AND mapped.is_complete
            LEFT JOIN solo_current_projection_scope scope
              ON scope.song_id = source.song_id
             AND scope.instrument = source.instrument
            ORDER BY source.instrument, source.song_id
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("limit", maxMappedScopes + 1);
        var scopes = new List<ScopeEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var publishedScrapeId = reader.GetInt64(0);
            var songId = reader.GetString(1);
            var instrument = reader.GetString(2);
            var sourceKind = reader.GetString(3);
            var sourceSnapshotId = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
            var sourceScrapeId = reader.GetInt64(5);
            var projectionSourceSnapshotId = reader.GetInt64(6);
            var projectionGeneration = reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10);
            var projectionScopeSourceSnapshotId = reader.IsDBNull(12) ? (long?)null : reader.GetInt64(12);
            var projectionStatus = reader.IsDBNull(13) ? null : reader.GetString(13);
            var sourceClass = Classify(
                publishedScrapeId,
                sourceKind,
                sourceScrapeId,
                projectionSourceSnapshotId,
                projectionGeneration,
                projectionScopeSourceSnapshotId,
                projectionStatus);
            scopes.Add(new ScopeEvidence
            {
                Id = $"{instrument}:{songId}",
                SongId = songId,
                Instrument = instrument,
                PublishedScrapeId = publishedScrapeId,
                SourceKind = sourceKind,
                SourceSnapshotId = sourceSnapshotId,
                SourceScrapeId = sourceScrapeId,
                ProjectionSourceSnapshotId = projectionSourceSnapshotId,
                PublishedRowCount = reader.GetInt64(7),
                ContentFingerprint = reader.GetString(8),
                CoverageFingerprint = reader.GetString(9),
                ProjectionGeneration = projectionGeneration,
                ProjectionRowCount = reader.IsDBNull(11) ? (long?)null : reader.GetInt64(11),
                ProjectionScopeSourceSnapshotId = projectionScopeSourceSnapshotId,
                ProjectionStatus = projectionStatus,
                SourceClass = sourceClass,
                HasActiveOverlay = reader.GetBoolean(14),
                RawMaxScore = maxScores.TryGetValue(songId, out var songMaxScores)
                    ? songMaxScores.GetByInstrument(instrument)
                    : null,
            });
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        if (scopes.Count > maxMappedScopes)
        {
            throw new InvalidOperationException(
                $"Published mapping exceeds the bounded manifest limit ({maxMappedScopes}).");
        }
        return scopes;
    }

    private async Task<IReadOnlyList<TieEvidence>> ReadTieEvidenceAsync(
        ScopeEvidence scope,
        CancellationToken cancellationToken)
    {
        if (scope.SourceClass is ScopeSourceClass.Empty
            or ScopeSourceClass.SourceMismatch
            or ScopeSourceClass.ProjectionMissing
            || scope.ProjectionGeneration is null)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH ordered AS (
                SELECT projection.score,
                       COALESCE(projection.end_time, projection.first_seen_at::TEXT) AS order_time,
                       projection.rank,
                       projection.account_id,
                       ROW_NUMBER() OVER (
                           ORDER BY {SoloLeaderboardOrderingSql.OrderBy("projection")}
                       ) AS exact_order
                FROM current_leaderboard_entries projection
                WHERE projection.song_id = @songId
                  AND projection.instrument = @instrument
                  AND projection.projection_generation = @generation
            )
            SELECT ordered.score,
                   ordered.order_time,
                   MIN(ordered.rank)::INT AS min_rank,
                   COUNT(*)::BIGINT AS peer_count,
                   (ARRAY_AGG(ordered.account_id ORDER BY ordered.exact_order))[1:8] AS account_ids
            FROM ordered
            GROUP BY ordered.score, ordered.order_time
            HAVING COUNT(*) > 1
            ORDER BY MIN(ordered.exact_order)
            LIMIT 32
            """;
        command.Parameters.AddWithValue("songId", scope.SongId);
        command.Parameters.AddWithValue("instrument", scope.Instrument);
        command.Parameters.AddWithValue("generation", scope.ProjectionGeneration.Value);
        var ties = new List<TieEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ties.Add(new TieEvidence
            {
                Score = reader.GetInt32(0),
                OrderTime = reader.GetString(1),
                MinRank = reader.GetInt32(2),
                PeerCount = reader.GetInt64(3),
                AccountIds = reader.GetFieldValue<string[]>(4),
            });
        }

        return ties;
    }

    private async Task<IReadOnlyList<ExpectedLeaderboardRow>> ReadOverlayDerivedRowsAsync(
        ScopeEvidence scope,
        CancellationToken cancellationToken)
    {
        if (!scope.HasActiveOverlay
            || scope.SourceClass is not (ScopeSourceClass.Current or ScopeSourceClass.Reused)
            || scope.ProjectionGeneration is null)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH {PublishedSoloScopeSql.CurrentSourcesCte}
            SELECT projection.account_id,
                   projection.score,
                   projection.rank,
                   projection.source
            FROM leaderboard_entries_overlay overlay
            JOIN solo_current_projection_scope scope
              ON scope.song_id = overlay.song_id
             AND scope.instrument = overlay.instrument
             AND scope.status = 'ready'
             AND scope.row_count > 0
            JOIN published_sources source
              ON source.song_id = scope.song_id
             AND source.instrument = scope.instrument
             AND source.source_kind = 'snapshot'
             AND scope.source_snapshot_id IS NOT DISTINCT FROM
                 source.projection_source_snapshot_id
            JOIN current_leaderboard_entries projection
              ON projection.song_id = scope.song_id
             AND projection.instrument = scope.instrument
             AND projection.projection_generation = scope.projection_generation
             AND projection.account_id = overlay.account_id
             AND projection.score = overlay.score
             AND projection.source = overlay.source
            WHERE overlay.song_id = @songId
              AND overlay.instrument = @instrument
            ORDER BY projection.rank, projection.account_id
            LIMIT 8
            """;
        command.Parameters.AddWithValue("songId", scope.SongId);
        command.Parameters.AddWithValue("instrument", scope.Instrument);
        var rows = new List<ExpectedLeaderboardRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ExpectedLeaderboardRow
            {
                AccountId = reader.GetString(0),
                Score = reader.GetInt32(1),
                Rank = reader.GetInt32(2),
                Source = reader.GetString(3),
            });
        }
        return rows;
    }

    private ThresholdBoundaryEvidence? ReadThresholdBoundaryEvidence(
        ScopeEvidence scope,
        int seed)
    {
        if (scope.SourceClass is not (ScopeSourceClass.Current or ScopeSourceClass.Reused)
            || scope.RawMaxScore is not > 0)
        {
            return null;
        }

        using var database = new InstrumentDatabase(
            scope.Instrument,
            _dataSource,
            NullLogger<InstrumentDatabase>.Instance)
        {
            UsePublishedScopeSources = true,
            UseStoredProjectionRanksForFilteredReads = false,
        };
        foreach (var leewayTenths in DeterministicRollout.FractionalLeewayTenthsCandidates(
                     scope.RawMaxScore.Value,
                     seed,
                     scope.Id))
        {
            var threshold = DeterministicRollout.CalculateThreshold(
                scope.RawMaxScore.Value,
                leewayTenths);
            if (threshold <= 0 || threshold >= int.MaxValue)
                continue;

            var below = database.GetCurrentStateLeaderboardWithCount(
                scope.SongId,
                top: 1,
                maxScore: threshold - 1);
            var exact = database.GetCurrentStateLeaderboardWithCount(
                scope.SongId,
                top: 1,
                maxScore: threshold);
            var plus = database.GetCurrentStateLeaderboardWithCount(
                scope.SongId,
                top: 1,
                maxScore: threshold + 1);
            var exactAddedCount = exact.TotalCount - below.TotalCount;
            var plusAddedCount = plus.TotalCount - exact.TotalCount;
            if (exactAddedCount <= 0 || plusAddedCount <= 0)
                continue;

            var exactRows = database.GetCurrentStateLeaderboardWithCount(
                    scope.SongId,
                    top: Math.Min(exactAddedCount, 8),
                    maxScore: threshold)
                .Entries
                .Select(ToExpectedRow)
                .ToArray();
            var plusRows = database.GetCurrentStateLeaderboardWithCount(
                    scope.SongId,
                    top: Math.Min(plusAddedCount, 8),
                    maxScore: threshold + 1)
                .Entries
                .Select(ToExpectedRow)
                .ToArray();
            if (exactRows.Length == 0
                || plusRows.Length == 0
                || exactRows.Any(row => row.Score != threshold)
                || plusRows.Any(row => row.Score != threshold + 1))
            {
                continue;
            }

            return new ThresholdBoundaryEvidence
            {
                RawMaxScore = scope.RawMaxScore.Value,
                LeewayTenths = leewayTenths,
                Threshold = threshold,
                BelowTotalCount = below.TotalCount,
                ExactTotalCount = exact.TotalCount,
                PlusTotalCount = plus.TotalCount,
                ExactAddedRows = exactRows,
                PlusAddedRows = plusRows,
            };
        }

        return null;
    }

    private IReadOnlyList<SampleAccount> ReadSampleAccounts(
        ScopeEvidence scope,
        IReadOnlyList<TieEvidence> ties,
        int seed)
    {
        if (scope.SourceClass == ScopeSourceClass.Empty)
            return [];

        using var database = new InstrumentDatabase(
            scope.Instrument,
            _dataSource,
            NullLogger<InstrumentDatabase>.Instance)
        {
            UsePublishedScopeSources = true,
            UseStoredProjectionRanksForFilteredReads = false,
        };
        var samples = new Dictionary<string, SampleAccount>(StringComparer.OrdinalIgnoreCase);

        AddRows(database.GetCurrentStateLeaderboardWithCount(scope.SongId, top: 3).Entries, "top", samples);
        foreach (var offset in new[] { 98, 99, 100 })
        {
            if (scope.PublishedRowCount <= offset)
                continue;
            AddRows(
                database.GetCurrentStateLeaderboardWithCount(scope.SongId, top: 3, offset: offset).Entries,
                $"offset-{offset}",
                samples);
        }

        if (scope.RawMaxScore is > 0)
        {
            var fractional = DeterministicRollout.SelectFractionalLeewayTenths(
                scope.RawMaxScore.Value,
                seed,
                scope.Id);
            foreach (var threshold in new[]
                     {
                         scope.RawMaxScore.Value,
                         DeterministicRollout.CalculateThreshold(scope.RawMaxScore.Value, fractional),
                     })
            {
                foreach (var edge in new[] { threshold - 1, threshold, threshold + 1 })
                {
                    AddRows(
                        database.GetCurrentStateLeaderboardWithCount(scope.SongId, top: 3, maxScore: edge).Entries,
                        $"threshold-{edge}",
                        samples);
                }
            }
        }

        foreach (var tie in ties.Take(2))
        {
            AddRows(
                database.GetCurrentStateLeaderboardWithCount(
                    scope.SongId,
                    top: (int)Math.Min(tie.PeerCount + 2, 10),
                    offset: Math.Max(0, tie.MinRank - 2),
                    maxScore: tie.Score).Entries,
                $"tie-{tie.Score}-{tie.MinRank}",
                samples);
        }

        return samples.Values
            .OrderBy(static account => account.Rank)
            .ThenBy(static account => account.AccountId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<RowParityCase> BuildRowCases(
        IReadOnlyList<ScopeEvidence> scopes)
    {
        var cases = new List<RowParityCase>();
        foreach (var scope in scopes)
        {
            var accountIds = scope.SampleAccounts
                .Select(static account => account.AccountId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            var fallbackThreshold = scope.SampleAccounts.Count > 0
                ? Math.Max(0, scope.SampleAccounts[0].Score - 1)
                : int.MaxValue;
            var baseThreshold = scope.RawMaxScore is > 0 ? scope.RawMaxScore.Value : fallbackThreshold;
            cases.Add(new RowParityCase
            {
                Id = CaseId(scope, "filtered-top"),
                ScopeId = scope.Id,
                SongId = scope.SongId,
                Instrument = scope.Instrument,
                MaxScore = baseThreshold,
                RawMaxScore = scope.RawMaxScore,
                LeewayTenths = scope.RawMaxScore is > 0 ? 0 : null,
                Top = 100,
                Offset = 0,
                AccountIds = accountIds,
                Tags = Tags(scope, "filtered-top"),
                Core = scope.RawMaxScore is > 0,
            });

            if (scope.ThresholdBoundary is { } boundary)
            {
                var boundaryAccounts = boundary.ExactAddedRows
                    .Concat(boundary.PlusAddedRows)
                    .Select(static row => row.AccountId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                cases.Add(new RowParityCase
                {
                    Id = CaseId(scope, "fractional-threshold-minus-one"),
                    ScopeId = scope.Id,
                    SongId = scope.SongId,
                    Instrument = scope.Instrument,
                    MaxScore = boundary.Threshold - 1,
                    Top = 100,
                    Offset = 0,
                    ExpectedTotalCount = boundary.BelowTotalCount,
                    ExpectedAbsentAccountIds = boundaryAccounts,
                    AccountIds = boundaryAccounts,
                    Tags = Tags(
                        scope,
                        "fractional-threshold",
                        "csharp-truncation",
                        "threshold-minus-one",
                        "actual-boundary-transition"),
                });
                cases.Add(new RowParityCase
                {
                    Id = CaseId(scope, "fractional-threshold-exact"),
                    ScopeId = scope.Id,
                    SongId = scope.SongId,
                    Instrument = scope.Instrument,
                    MaxScore = boundary.Threshold,
                    RawMaxScore = boundary.RawMaxScore,
                    LeewayTenths = boundary.LeewayTenths,
                    Top = 100,
                    Offset = 0,
                    ExpectedTotalCount = boundary.ExactTotalCount,
                    ExpectedRows = boundary.ExactAddedRows,
                    ExpectedAbsentAccountIds = boundary.PlusAddedRows
                        .Select(static row => row.AccountId)
                        .ToArray(),
                    AccountIds = boundaryAccounts,
                    Tags = Tags(
                        scope,
                        "fractional-threshold",
                        "csharp-truncation",
                        "threshold-exact",
                        "actual-boundary-transition"),
                });
                cases.Add(new RowParityCase
                {
                    Id = CaseId(scope, "fractional-threshold-plus-one"),
                    ScopeId = scope.Id,
                    SongId = scope.SongId,
                    Instrument = scope.Instrument,
                    MaxScore = boundary.Threshold + 1,
                    Top = 100,
                    Offset = 0,
                    ExpectedTotalCount = boundary.PlusTotalCount,
                    ExpectedRows = boundary.PlusAddedRows,
                    AccountIds = boundaryAccounts,
                    Tags = Tags(
                        scope,
                        "fractional-threshold",
                        "csharp-truncation",
                        "threshold-plus-one",
                        "actual-boundary-transition"),
                });
            }

            foreach (var overlayRow in scope.OverlayDerivedRows.Take(1))
            {
                cases.Add(new RowParityCase
                {
                    Id = CaseId(scope, "source-matched-overlay-row"),
                    ScopeId = scope.Id,
                    SongId = scope.SongId,
                    Instrument = scope.Instrument,
                    MaxScore = int.MaxValue,
                    Top = 1,
                    Offset = Math.Max(0, overlayRow.Rank - 1),
                    ExpectedFirstRank = overlayRow.Rank,
                    MinimumExpectedRows = 1,
                    ExpectedRows = [overlayRow],
                    AccountIds = [overlayRow.AccountId],
                    Tags = Tags(
                        scope,
                        "active-overlay",
                        "source-matched-overlay",
                        "overlay-derived-row"),
                });
            }

            foreach (var offset in new[] { 99, 100 })
            {
                if (scope.PublishedRowCount <= offset)
                    continue;
                cases.Add(new RowParityCase
                {
                    Id = CaseId(scope, $"page-{offset}"),
                    ScopeId = scope.Id,
                    SongId = scope.SongId,
                    Instrument = scope.Instrument,
                    MaxScore = int.MaxValue,
                    Top = 2,
                    Offset = offset,
                    ExpectedFirstRank = offset + 1,
                    MinimumExpectedRows = 1,
                    AccountIds = accountIds,
                    Tags = Tags(scope, "rank-page-boundary"),
                    Core = false,
                });
            }

            foreach (var tie in scope.ExactScoreTimeTies.Take(1))
            {
                cases.Add(new RowParityCase
                {
                    Id = CaseId(scope, "exact-score-time-tie"),
                    ScopeId = scope.Id,
                    SongId = scope.SongId,
                    Instrument = scope.Instrument,
                    MaxScore = tie.Score,
                    Top = (int)Math.Min(tie.PeerCount + 2, 10),
                    Offset = Math.Max(0, tie.MinRank - 2),
                    AccountIds = tie.AccountIds,
                    Tags = Tags(scope, "exact-score-time-tie", "account-id-tiebreak"),
                    Core = false,
                });
            }
        }

        return cases
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ApiWorkload> BuildApiWorkloads(
        IReadOnlyList<ScopeEvidence> scopes,
        int seed)
    {
        var workloads = new Dictionary<string, ApiWorkload>(StringComparer.Ordinal);
        foreach (var scope in scopes.Where(static scope => scope.SampleAccounts.Count > 0))
        {
            var accountIds = scope.SampleAccounts
                .Select(static account => account.AccountId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            var leewayTenths = scope.RawMaxScore is > 0
                ? DeterministicRollout.SelectFractionalLeewayTenths(scope.RawMaxScore.Value, seed, scope.Id)
                : (int?)null;
            var leewayQuery = leewayTenths.HasValue
                ? $"&leeway={(leewayTenths.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture)}"
                : string.Empty;
            var song = Uri.EscapeDataString(scope.SongId);
            var instrument = Uri.EscapeDataString(scope.Instrument);
            var singleId = ApiId("single", scope);
            workloads[singleId] = new ApiWorkload
            {
                Id = singleId,
                Kind = "single",
                Path = $"/api/leaderboard/{song}/{instrument}?top=100&offset=0{leewayQuery}",
                SongId = scope.SongId,
                Instrument = scope.Instrument,
                AccountIds = accountIds,
                Tags = Tags(scope, "filtered-top", leewayTenths.HasValue ? "csharp-truncation" : "unfiltered-api"),
                Core = false,
            };

            var accounts = Uri.EscapeDataString(string.Join(',', accountIds));
            var playerId = ApiId("player", scope);
            workloads[playerId] = new ApiWorkload
            {
                Id = playerId,
                Kind = "player",
                Path =
                    $"/api/player/{Uri.EscapeDataString(accountIds[0])}?songId={song}&instruments={instrument}" +
                    (leewayTenths.HasValue
                        ? $"&leeway={(leewayTenths.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture)}"
                        : string.Empty),
                SongId = scope.SongId,
                Instrument = scope.Instrument,
                AccountIds = [accountIds[0]],
                Tags = Tags(scope, "filtered-player-api", "response-cache-aware"),
                Core = false,
            };

            var memberId = ApiId("member", scope);
            workloads[memberId] = new ApiWorkload
            {
                Id = memberId,
                Kind = "member",
                Path =
                    $"/api/leaderboard/{song}/members/scores?accountIds={accounts}&instruments={instrument}" +
                    (leewayTenths.HasValue
                        ? $"&leeway={(leewayTenths.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture)}"
                        : string.Empty),
                SongId = scope.SongId,
                Instrument = scope.Instrument,
                AccountIds = accountIds,
                Tags = Tags(scope, "filtered-member", "multi-account-parity"),
                Core = false,
            };

            var singleMemberId = ApiId("member-single", scope);
            workloads[singleMemberId] = new ApiWorkload
            {
                Id = singleMemberId,
                Kind = "member",
                Path =
                    $"/api/leaderboard/{song}/members/scores?" +
                    $"accountIds={Uri.EscapeDataString(accountIds[0])}&instruments={instrument}" +
                    (leewayTenths.HasValue
                        ? $"&leeway={(leewayTenths.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture)}"
                        : string.Empty),
                SongId = scope.SongId,
                Instrument = scope.Instrument,
                AccountIds = [accountIds[0]],
                Tags = Tags(
                    scope,
                    "filtered-member",
                    "single-account",
                    "core-filtered-player-query"),
                Core = false,
            };

            var listId = $"list:{scope.SongId}";
            workloads.TryAdd(listId, new ApiWorkload
            {
                Id = listId,
                Kind = "list",
                Path = $"/api/leaderboard/{song}/all?top=100" +
                       (leewayTenths.HasValue
                           ? $"&leeway={(leewayTenths.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture)}"
                           : string.Empty),
                SongId = scope.SongId,
                AccountIds = accountIds,
                Tags = ["filtered-list"],
                Core = false,
            });
        }

        foreach (var empty in scopes.Where(static scope => scope.SourceClass == ScopeSourceClass.Empty))
        {
            var id = ApiId("single", empty);
            workloads.TryAdd(id, new ApiWorkload
            {
                Id = id,
                Kind = "single",
                Path =
                    $"/api/leaderboard/{Uri.EscapeDataString(empty.SongId)}/{Uri.EscapeDataString(empty.Instrument)}?top=100&offset=0",
                SongId = empty.SongId,
                Instrument = empty.Instrument,
                Tags = Tags(empty, "empty-scope"),
            });
        }

        var allWorkloads = workloads.Values
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var coreScope = scopes
            .Where(scope =>
                scope.RawMaxScore is > 0
                && scope.SampleAccounts.Count > 0
                && scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused)
            .OrderByDescending(static scope => scope.PublishedRowCount)
            .ThenBy(
                scope => DeterministicRollout.StableKey(seed, "core-scope", scope.Id),
                StringComparer.Ordinal)
            .FirstOrDefault();
        var coreIds = new HashSet<string>(StringComparer.Ordinal);
        var benchmarkIds = new HashSet<string>(StringComparer.Ordinal);
        if (coreScope is not null)
        {
            coreIds.Add(ApiId("single", coreScope));
            coreIds.Add(ApiId("member-single", coreScope));
            benchmarkIds.UnionWith(coreIds);
            benchmarkIds.Add(ApiId("member", coreScope));
            benchmarkIds.Add($"list:{coreScope.SongId}");
        }

        foreach (var sourceClass in new[]
                 {
                     ScopeSourceClass.Current,
                     ScopeSourceClass.Reused,
                     ScopeSourceClass.Empty,
                     ScopeSourceClass.SourceMismatch,
                 })
        {
            var representative = scopes
                .Where(scope => scope.SourceClass == sourceClass)
                .OrderBy(
                    scope => DeterministicRollout.StableKey(
                        seed,
                        $"benchmark:{sourceClass}",
                        scope.Id),
                    StringComparer.Ordinal)
                .FirstOrDefault();
            if (representative is not null)
                benchmarkIds.Add(ApiId("single", representative));
        }

        return allWorkloads
            .Select(workload => CloneWorkload(
                workload,
                core: coreIds.Contains(workload.Id),
                benchmark: benchmarkIds.Contains(workload.Id)))
            .ToArray();
    }

    public static ScopeSourceClass Classify(
        long publishedScrapeId,
        string sourceKind,
        long sourceScrapeId,
        long projectionSourceSnapshotId,
        long? projectionGeneration,
        long? projectionScopeSourceSnapshotId,
        string? projectionStatus)
    {
        if (string.Equals(sourceKind, "empty", StringComparison.Ordinal))
            return ScopeSourceClass.Empty;
        if (!projectionGeneration.HasValue
            || !string.Equals(projectionStatus, "ready", StringComparison.Ordinal))
        {
            return ScopeSourceClass.ProjectionMissing;
        }
        if (projectionScopeSourceSnapshotId != projectionSourceSnapshotId)
            return ScopeSourceClass.SourceMismatch;
        return sourceScrapeId < publishedScrapeId
            ? ScopeSourceClass.Reused
            : ScopeSourceClass.Current;
    }

    private static void AddRows(
        IEnumerable<LeaderboardEntryDto> rows,
        string evidenceKind,
        IDictionary<string, SampleAccount> samples)
    {
        foreach (var row in rows)
        {
            samples.TryAdd(
                $"{row.AccountId}:{evidenceKind}",
                new SampleAccount
                {
                    AccountId = row.AccountId,
                    Score = row.Score,
                    Rank = row.Rank,
                    ApiRank = row.ApiRank,
                    EndTime = row.EndTime,
                    Source = row.Source,
                    EvidenceKind = evidenceKind,
                });
        }
    }

    private static ScopeEvidence Clone(
        ScopeEvidence source,
        IReadOnlyList<TieEvidence>? exactScoreTimeTies = null,
        IReadOnlyList<SampleAccount>? sampleAccounts = null,
        IReadOnlyList<ExpectedLeaderboardRow>? overlayDerivedRows = null,
        ThresholdBoundaryEvidence? thresholdBoundary = null) =>
        new()
        {
            Id = source.Id,
            SongId = source.SongId,
            Instrument = source.Instrument,
            PublishedScrapeId = source.PublishedScrapeId,
            SourceKind = source.SourceKind,
            SourceSnapshotId = source.SourceSnapshotId,
            SourceScrapeId = source.SourceScrapeId,
            ProjectionSourceSnapshotId = source.ProjectionSourceSnapshotId,
            PublishedRowCount = source.PublishedRowCount,
            ContentFingerprint = source.ContentFingerprint,
            CoverageFingerprint = source.CoverageFingerprint,
            ProjectionGeneration = source.ProjectionGeneration,
            ProjectionRowCount = source.ProjectionRowCount,
            ProjectionScopeSourceSnapshotId = source.ProjectionScopeSourceSnapshotId,
            ProjectionStatus = source.ProjectionStatus,
            SourceClass = source.SourceClass,
            HasActiveOverlay = source.HasActiveOverlay,
            RawMaxScore = source.RawMaxScore,
            ExactScoreTimeTies = exactScoreTimeTies ?? source.ExactScoreTimeTies,
            SampleAccounts = sampleAccounts ?? source.SampleAccounts,
            OverlayDerivedRows = overlayDerivedRows ?? source.OverlayDerivedRows,
            ThresholdBoundary = thresholdBoundary ?? source.ThresholdBoundary,
        };

    private static ExpectedLeaderboardRow ToExpectedRow(LeaderboardEntryDto row) =>
        new()
        {
            AccountId = row.AccountId,
            Score = row.Score,
            Rank = row.Rank,
            Source = row.Source,
        };

    private static string CaseId(ScopeEvidence scope, string suffix) =>
        $"row:{scope.Instrument}:{scope.SongId}:{suffix}";

    private static string ApiId(string kind, ScopeEvidence scope) =>
        $"{kind}:{scope.Instrument}:{scope.SongId}";

    private static IReadOnlyList<string> Tags(ScopeEvidence scope, params string[] extra) =>
        new[] { $"source-{scope.SourceClass.ToString().ToLowerInvariant()}" }
            .Concat(scope.HasActiveOverlay ? ["active-overlay"] : [])
            .Concat(extra)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static ApiWorkload CloneWorkload(
        ApiWorkload workload,
        bool core,
        bool benchmark) =>
        new()
        {
            Id = workload.Id,
            Kind = workload.Kind,
            Path = workload.Path,
            SongId = workload.SongId,
            Instrument = workload.Instrument,
            ExpectedStatusCode = workload.ExpectedStatusCode,
            AccountIds = workload.AccountIds,
            Tags = workload.Tags,
            Core = core,
            Benchmark = benchmark,
        };
}
