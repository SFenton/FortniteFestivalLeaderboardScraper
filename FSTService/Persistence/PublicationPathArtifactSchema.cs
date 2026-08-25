namespace FSTService.Persistence;

/// <summary>
/// Publication-bound path artifact snapshots.
/// One canonical row per publication catalog song, including authoritative
/// null-generation rows, so published path/max-score reads never depend on the
/// mutable live <c>songs</c> table.
/// </summary>
public static class PublicationPathArtifactSchema
{
    public const string TableName = "publication_path_artifacts";

    /// <summary>
    /// Binding contract version. Must match
    /// <c>PublicationRouteSurfaceContractCatalog.ContractVersion</c>.
    /// </summary>
    public const int ContractVersion = 1;

    /// <summary>Binding kind emitted when the snapshot is complete.</summary>
    public const string ManifestBindingKind =
        "generation_path_artifact_manifest";

    /// <summary>Binding kind retained when the snapshot is incomplete.</summary>
    public const string LegacyBindingKind = "legacy_live_unversioned";

    public const string RetiredBindingKind =
        "retired_generation_path_artifacts";

    public const string FailedBindingKind =
        "failed_generation_path_artifacts";

    /// <summary>Bootstrap of the current publication from live rows.</summary>
    public const string LegacyLiveBackfillSource = "legacy_live_backfill";

    /// <summary>Candidate snapshot captured at scrape allocation.</summary>
    public const string CandidateSnapshotSource =
        "generation_candidate_snapshot";

    /// <summary>Snapshot re-bound during publication preparation.</summary>
    public const string PreparedSnapshotSource =
        "generation_prepared_snapshot";

    /// <summary>Snapshot refreshed by max-score maintenance apply/rollback.</summary>
    public const string MaxScoreMaintenanceSource =
        "max_score_maintenance_apply";

    /// <summary>
    /// Columns projected for <see cref="Scraping.PathGenerationState"/> and
    /// <see cref="SongMaxScores"/> reconstruction. Ordinals must stay aligned
    /// with the live <c>songs</c> projection consumed by the path data store.
    /// </summary>
    public const string ReadColumns = """
        song_id,
               path_generation_revision,
               dat_file_hash,
               song_last_modified,
               paths_generated_at,
               chopt_version,
               chopt_binary_sha256,
               path_generation_profile,
               path_artifact_generation_id,
               COALESCE(path_expected_instruments, ARRAY[]::TEXT[]),
               max_lead_score,
               max_bass_score,
               max_drums_score,
               max_vocals_score,
               max_pro_lead_score,
               max_pro_bass_score,
               max_pro_cymbals_score,
               max_pro_drums_score,
               catalog_last_modified,
               path_generation_pending
        """;

    /// <summary>
    /// Idempotent DDL, canonical manifest hash function, current-publication
    /// bootstrap backfill, and retention of superseded snapshots.
    /// </summary>
    public static string Sql { get; } =
        MigrationSql
        + Environment.NewLine
        + Environment.NewLine
        + BuildBootstrapRebindSql()
        + ";";

    private static string BuildBootstrapRebindSql() =>
        RebindSql
            .Replace(
                "@publicationId",
                """
                (
                    SELECT current_publication_id
                    FROM scrape_publication_state
                    WHERE id = TRUE
                )
                """,
                StringComparison.Ordinal)
            .Replace(
                "@source",
                $"'{LegacyLiveBackfillSource}'",
                StringComparison.Ordinal)
            .Replace(
                "@contractVersion",
                ContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("@now", "now()", StringComparison.Ordinal);

    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS publication_path_artifacts (
            publication_id              BIGINT NOT NULL
                REFERENCES publication_generations(publication_id)
                ON DELETE CASCADE,
            song_id                     TEXT        NOT NULL,
            path_generation_revision    BIGINT      NOT NULL DEFAULT 0,
            path_artifact_generation_id TEXT,
            dat_file_hash               TEXT,
            song_last_modified          TEXT,
            catalog_last_modified       TEXT,
            paths_generated_at          TIMESTAMPTZ,
            chopt_version               TEXT,
            chopt_binary_sha256         TEXT,
            path_generation_profile     TEXT,
            path_expected_instruments   TEXT[]      NOT NULL
                DEFAULT ARRAY[]::TEXT[],
            path_generation_pending     BOOLEAN     NOT NULL DEFAULT FALSE,
            max_lead_score              INTEGER,
            max_bass_score              INTEGER,
            max_drums_score             INTEGER,
            max_vocals_score            INTEGER,
            max_pro_lead_score          INTEGER,
            max_pro_bass_score          INTEGER,
            max_pro_cymbals_score       INTEGER,
            max_pro_drums_score         INTEGER,
            captured_at                 TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (publication_id, song_id)
        );

        CREATE OR REPLACE FUNCTION publication_path_artifact_manifest_sha256(
            target_publication_id BIGINT)
        RETURNS TEXT
        LANGUAGE sql
        STABLE
        AS $$
            SELECT encode(
                digest(
                    convert_to(
                        COALESCE(
                            string_agg(line, chr(30) ORDER BY song_id),
                            ''),
                        'UTF8'),
                    'sha256'),
                'hex')
            FROM (
                SELECT
                    song_id,
                    song_id
                    || chr(31) || path_generation_revision::text
                    || chr(31)
                        || COALESCE(path_artifact_generation_id, '')
                    || chr(31) || COALESCE(dat_file_hash, '')
                    || chr(31) || COALESCE(song_last_modified, '')
                    || chr(31) || COALESCE(catalog_last_modified, '')
                    || chr(31) || COALESCE(
                        to_char(
                            paths_generated_at AT TIME ZONE 'UTC',
                            'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                        '')
                    || chr(31) || COALESCE(chopt_version, '')
                    || chr(31) || COALESCE(chopt_binary_sha256, '')
                    || chr(31) || COALESCE(path_generation_profile, '')
                    || chr(31) || COALESCE(
                        array_to_string(path_expected_instruments, ','),
                        '')
                    || chr(31) || CASE
                        WHEN path_generation_pending THEN '1' ELSE '0'
                    END
                    || chr(31) || COALESCE(max_lead_score::text, '')
                    || chr(31) || COALESCE(max_bass_score::text, '')
                    || chr(31) || COALESCE(max_drums_score::text, '')
                    || chr(31) || COALESCE(max_vocals_score::text, '')
                    || chr(31) || COALESCE(max_pro_lead_score::text, '')
                    || chr(31) || COALESCE(max_pro_bass_score::text, '')
                    || chr(31) || COALESCE(max_pro_cymbals_score::text, '')
                    || chr(31) || COALESCE(max_pro_drums_score::text, '')
                        AS line
                FROM publication_path_artifacts
                WHERE publication_id = target_publication_id
            ) canonical
        $$;

        -- Bootstrap: capture the CURRENT publication only. Prior publication
        -- history cannot be reconstructed from mutable live rows.
        INSERT INTO publication_path_artifacts (
            publication_id, song_id, path_generation_revision,
            path_artifact_generation_id, dat_file_hash, song_last_modified,
            catalog_last_modified, paths_generated_at, chopt_version,
            chopt_binary_sha256, path_generation_profile,
            path_expected_instruments, path_generation_pending,
            max_lead_score, max_bass_score, max_drums_score, max_vocals_score,
            max_pro_lead_score, max_pro_bass_score, max_pro_cymbals_score,
            max_pro_drums_score, captured_at)
        SELECT
            catalog_song.publication_id,
            catalog_song.song_id,
            COALESCE(song.path_generation_revision, 0),
            song.path_artifact_generation_id,
            song.dat_file_hash,
            song.song_last_modified,
            NULLIF(song.last_modified, ''),
            song.paths_generated_at,
            song.chopt_version,
            song.chopt_binary_sha256,
            song.path_generation_profile,
            COALESCE(
                song.path_expected_instruments,
                ARRAY[]::TEXT[]),
            COALESCE(song.path_generation_pending, FALSE),
            song.max_lead_score,
            song.max_bass_score,
            song.max_drums_score,
            song.max_vocals_score,
            song.max_pro_lead_score,
            song.max_pro_bass_score,
            song.max_pro_cymbals_score,
            song.max_pro_drums_score,
            now()
        FROM (
            SELECT DISTINCT
                catalog.publication_id,
                entry -> 'track' ->> 'su' AS song_id
            FROM scrape_publication_state publication
            JOIN publication_song_catalog catalog
              ON catalog.publication_id =
                    publication.current_publication_id
            CROSS JOIN LATERAL jsonb_array_elements(
                catalog.catalog_json -> 'songs') AS entry
            WHERE publication.id = TRUE
              AND catalog.is_exact
              AND NOT EXISTS (
                  SELECT 1
                  FROM publication_path_artifacts existing
                  WHERE existing.publication_id = catalog.publication_id
              )
        ) catalog_song
        LEFT JOIN songs song
          ON song.song_id = catalog_song.song_id
        WHERE catalog_song.song_id IS NOT NULL
        ON CONFLICT (publication_id, song_id) DO NOTHING;

        DELETE FROM publication_path_artifacts artifact
        USING scrape_publication_state publication
        WHERE publication.id = TRUE
          AND artifact.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND artifact.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND artifact.publication_id IS DISTINCT FROM
              publication.working_publication_id;

        UPDATE publication_surface_bindings binding
        SET binding_kind = 'retired_generation_path_artifacts',
            binding_json = jsonb_build_object(
                'table', 'publication_path_artifacts',
                'retired', true),
            row_count = 0,
            content_hash = NULL,
            status = 'retired',
            built_at = now()
        FROM scrape_publication_state publication
        WHERE publication.id = TRUE
          AND binding.surface_name = 'path_artifacts'
          AND binding.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.working_publication_id
          AND binding.status <> 'retired';
        """;

    /// <summary>
    /// Captures the complete candidate snapshot for <c>@publicationId</c> from
    /// its exact publication catalog joined to live songs, including explicit
    /// null-generation rows. Requires <c>@publicationId</c> and <c>@now</c>.
    /// </summary>
    public const string CaptureSnapshotSql = """
        INSERT INTO publication_path_artifacts (
            publication_id, song_id, path_generation_revision,
            path_artifact_generation_id, dat_file_hash, song_last_modified,
            catalog_last_modified, paths_generated_at, chopt_version,
            chopt_binary_sha256, path_generation_profile,
            path_expected_instruments, path_generation_pending,
            max_lead_score, max_bass_score, max_drums_score, max_vocals_score,
            max_pro_lead_score, max_pro_bass_score, max_pro_cymbals_score,
            max_pro_drums_score, captured_at)
        SELECT
            @publicationId,
            catalog_song.song_id,
            COALESCE(song.path_generation_revision, 0),
            song.path_artifact_generation_id,
            song.dat_file_hash,
            song.song_last_modified,
            NULLIF(song.last_modified, ''),
            song.paths_generated_at,
            song.chopt_version,
            song.chopt_binary_sha256,
            song.path_generation_profile,
            COALESCE(song.path_expected_instruments, ARRAY[]::TEXT[]),
            COALESCE(song.path_generation_pending, FALSE),
            song.max_lead_score,
            song.max_bass_score,
            song.max_drums_score,
            song.max_vocals_score,
            song.max_pro_lead_score,
            song.max_pro_bass_score,
            song.max_pro_cymbals_score,
            song.max_pro_drums_score,
            @now
        FROM (
            SELECT DISTINCT entry -> 'track' ->> 'su' AS song_id
            FROM publication_song_catalog catalog
            CROSS JOIN LATERAL jsonb_array_elements(
                catalog.catalog_json -> 'songs') AS entry
            WHERE catalog.publication_id = @publicationId
              AND catalog.is_exact
        ) catalog_song
        LEFT JOIN songs song
          ON song.song_id = catalog_song.song_id
        WHERE catalog_song.song_id IS NOT NULL
        ON CONFLICT (publication_id, song_id) DO NOTHING
        """;

    /// <summary>
    /// Refreshes the current publication snapshot from restored/promoted live
    /// <c>songs</c> rows. Requires <c>@publicationId</c>.
    /// </summary>
    public const string RefreshSnapshotFromLiveSongsSql = """
        UPDATE publication_path_artifacts artifact
        SET path_generation_revision = song.path_generation_revision,
            path_artifact_generation_id = song.path_artifact_generation_id,
            dat_file_hash = song.dat_file_hash,
            song_last_modified = song.song_last_modified,
            catalog_last_modified = NULLIF(song.last_modified, ''),
            paths_generated_at = song.paths_generated_at,
            chopt_version = song.chopt_version,
            chopt_binary_sha256 = song.chopt_binary_sha256,
            path_generation_profile = song.path_generation_profile,
            path_expected_instruments = COALESCE(
                song.path_expected_instruments,
                ARRAY[]::TEXT[]),
            path_generation_pending = song.path_generation_pending,
            max_lead_score = song.max_lead_score,
            max_bass_score = song.max_bass_score,
            max_drums_score = song.max_drums_score,
            max_vocals_score = song.max_vocals_score,
            max_pro_lead_score = song.max_pro_lead_score,
            max_pro_bass_score = song.max_pro_bass_score,
            max_pro_cymbals_score = song.max_pro_cymbals_score,
            max_pro_drums_score = song.max_pro_drums_score,
            captured_at = now()
        FROM songs song
        WHERE artifact.publication_id = @publicationId
          AND song.song_id = artifact.song_id
        """;

    /// <summary>
    /// Emits (or re-emits) the <c>path_artifacts</c> surface binding for
    /// <c>@publicationId</c>. The binding is only marked ready when the
    /// snapshot row count matches the bound publication catalog exactly.
    /// Requires <c>@publicationId</c>, <c>@source</c>, <c>@contractVersion</c>
    /// and <c>@now</c>.
    /// </summary>
    public const string RebindSql = """
        WITH target AS (
            SELECT
                generation.publication_id,
                generation.scrape_id,
                (
                    SELECT COUNT(*)
                    FROM publication_path_artifacts artifact
                    WHERE artifact.publication_id =
                        generation.publication_id
                ) AS row_count,
                (
                    SELECT catalog.song_count
                    FROM publication_song_catalog catalog
                    WHERE catalog.publication_id =
                        generation.publication_id
                      AND catalog.is_exact
                ) AS expected_row_count,
                publication_path_artifact_manifest_sha256(
                    generation.publication_id) AS content_hash
            FROM publication_generations generation
            WHERE generation.publication_id = @publicationId
        ),
        classified AS (
            SELECT
                target.*,
                target.expected_row_count IS NOT NULL
                    AND target.row_count = target.expected_row_count
                        AS complete
            FROM target
        )
        INSERT INTO publication_surface_bindings (
            publication_id, surface_name, binding_kind, binding_json,
            row_count, content_hash, status, built_at)
        SELECT
            classified.publication_id,
            'path_artifacts',
            CASE
                WHEN classified.complete
                    THEN 'generation_path_artifact_manifest'
                ELSE 'legacy_live_unversioned'
            END,
            CASE
                WHEN classified.complete
                    THEN jsonb_build_object(
                        'table', 'publication_path_artifacts',
                        'publicationId', classified.publication_id,
                        'scrapeId', classified.scrape_id,
                        'source', @source,
                        'authoritative', true,
                        'contractVersion', @contractVersion,
                        'expectedRowCount', classified.expected_row_count)
                ELSE jsonb_build_object(
                    'table', 'songs',
                    'publicationId', classified.publication_id,
                    'scrapeId', classified.scrape_id,
                    'source', @source,
                    'authoritative', false,
                    'contractVersion', @contractVersion,
                    'expectedRowCount', classified.expected_row_count,
                    'snapshotRowCount', classified.row_count,
                    'incomplete', true)
            END,
            CASE
                WHEN classified.complete THEN classified.row_count
                ELSE NULL
            END,
            CASE
                WHEN classified.complete THEN classified.content_hash
                ELSE NULL
            END,
            CASE
                WHEN classified.complete THEN 'ready'
                ELSE 'building'
            END,
            @now
        FROM classified
        ON CONFLICT (publication_id, surface_name) DO UPDATE SET
            binding_kind = EXCLUDED.binding_kind,
            binding_json = EXCLUDED.binding_json,
            row_count = EXCLUDED.row_count,
            content_hash = EXCLUDED.content_hash,
            status = EXCLUDED.status,
            built_at = EXCLUDED.built_at
        """;

    /// <summary>
    /// Retains only current/previous/working publication snapshots and retires
    /// superseded path bindings. Requires <c>@now</c>.
    /// </summary>
    public const string RetainPointerSnapshotsSql = """
        DELETE FROM publication_path_artifacts artifact
        USING scrape_publication_state publication
        WHERE publication.id = TRUE
          AND artifact.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND artifact.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND artifact.publication_id IS DISTINCT FROM
              publication.working_publication_id;

        UPDATE publication_surface_bindings binding
        SET binding_kind = 'retired_generation_path_artifacts',
            binding_json = jsonb_build_object(
                'table', 'publication_path_artifacts',
                'retired', true),
            row_count = 0,
            content_hash = NULL,
            status = 'retired',
            built_at = @now
        FROM scrape_publication_state publication
        WHERE publication.id = TRUE
          AND binding.surface_name = 'path_artifacts'
          AND binding.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.working_publication_id
          AND binding.status <> 'retired';
        """;

    /// <summary>
    /// Retains only the explicit current/previous snapshots after a publication
    /// commit. Requires <c>@currentPublicationId</c>,
    /// <c>@previousPublicationId</c> and <c>@now</c>.
    /// </summary>
    public const string RetainExplicitSnapshotsSql = """
        DELETE FROM publication_path_artifacts
        WHERE publication_id <> @currentPublicationId
          AND (
              @previousPublicationId IS NULL
              OR publication_id <> @previousPublicationId
          );

        UPDATE publication_surface_bindings
        SET binding_kind = 'retired_generation_path_artifacts',
            binding_json = jsonb_build_object(
                'table', 'publication_path_artifacts',
                'retired', true),
            row_count = 0,
            content_hash = NULL,
            status = 'retired',
            built_at = @now
        WHERE surface_name = 'path_artifacts'
          AND publication_id <> @currentPublicationId
          AND (
              @previousPublicationId IS NULL
              OR publication_id <> @previousPublicationId
          )
          AND status <> 'retired';
        """;

    /// <summary>
    /// Drops the failed candidate snapshot. Requires <c>@publicationId</c>.
    /// </summary>
    public const string CleanupFailedSnapshotSql = """
        DELETE FROM publication_path_artifacts
        WHERE publication_id = @publicationId
        """;
}
