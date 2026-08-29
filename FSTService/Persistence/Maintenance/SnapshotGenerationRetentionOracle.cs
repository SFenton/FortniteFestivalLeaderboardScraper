using Npgsql;

namespace FSTService.Persistence.Maintenance;

public interface ISnapshotGenerationRetentionOracle
{
    Task<SnapshotGenerationRetentionOracleResult> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long configuredResumeScrapeId,
        int commandTimeoutSeconds,
        CancellationToken ct = default);
}

public sealed class SnapshotGenerationRetentionOracle
    : ISnapshotGenerationRetentionOracle
{
    internal const string IndexTopologySql = """
        WITH instrument_map(
            instrument,
            root_relation,
            default_relation) AS (
            VALUES
                (
                    'Solo_Guitar',
                    'leaderboard_entries_snapshot_solo_guitar',
                    'leaderboard_entries_snapshot_solo_guitar_default'),
                (
                    'Solo_Bass',
                    'leaderboard_entries_snapshot_solo_bass',
                    'leaderboard_entries_snapshot_solo_bass_default'),
                (
                    'Solo_Vocals',
                    'leaderboard_entries_snapshot_solo_vocals',
                    'leaderboard_entries_snapshot_solo_vocals_default'),
                (
                    'Solo_Drums',
                    'leaderboard_entries_snapshot_solo_drums',
                    'leaderboard_entries_snapshot_solo_drums_default'),
                (
                    'Solo_PeripheralGuitar',
                    'leaderboard_entries_snapshot_pro_guitar',
                    'leaderboard_entries_snapshot_pro_guitar_default'),
                (
                    'Solo_PeripheralBass',
                    'leaderboard_entries_snapshot_pro_bass',
                    'leaderboard_entries_snapshot_pro_bass_default'),
                (
                    'Solo_PeripheralVocals',
                    'leaderboard_entries_snapshot_pro_vocals',
                    'leaderboard_entries_snapshot_pro_vocals_default'),
                (
                    'Solo_PeripheralCymbals',
                    'leaderboard_entries_snapshot_pro_cymbals',
                    'leaderboard_entries_snapshot_pro_cymbals_default'),
                (
                    'Solo_PeripheralDrums',
                    'leaderboard_entries_snapshot_pro_drums',
                    'leaderboard_entries_snapshot_pro_drums_default')
        ), table_map AS (
            SELECT
                mapping.*,
                'leaderboard_entries_snapshot'::regclass
                    AS top_table_oid,
                to_regclass(
                    'public.' || mapping.root_relation)
                    AS root_table_oid,
                to_regclass(
                    'public.' || mapping.default_relation)
                    AS default_table_oid
            FROM instrument_map mapping
        ), top_indexes AS (
            SELECT
                index_row.indrelid AS table_oid,
                index_row.indexrelid AS index_oid
            FROM pg_index index_row
            WHERE index_row.indrelid =
                    'leaderboard_entries_snapshot'::regclass
        ), root_indexes AS (
            SELECT
                mapping.instrument,
                index_row.indrelid AS table_oid,
                index_row.indexrelid AS index_oid
            FROM table_map mapping
            JOIN pg_index index_row
              ON index_row.indrelid =
                    mapping.root_table_oid
        ), default_indexes AS (
            SELECT
                mapping.instrument,
                index_row.indrelid AS table_oid,
                index_row.indexrelid AS index_oid
            FROM table_map mapping
            JOIN pg_index index_row
              ON index_row.indrelid =
                    mapping.default_table_oid
        ), numeric_tables AS (
            SELECT
                mapping.instrument,
                tree.relid AS table_oid,
                child.relname AS child_relation,
                substring(
                    child.relname
                    FROM '_s([1-9][0-9]*)$')::BIGINT
                    AS snapshot_id
            FROM table_map mapping
            CROSS JOIN LATERAL
                pg_partition_tree(
                    mapping.root_table_oid) tree
            JOIN pg_class child
              ON child.oid = tree.relid
            WHERE tree.level = 1
              AND child.relname ~ (
                    '^'
                    || mapping.root_relation
                    || '_s[1-9][0-9]*$')
        ), numeric_indexes AS (
            SELECT
                child.instrument,
                child.table_oid,
                child.child_relation,
                child.snapshot_id,
                index_row.indexrelid AS index_oid
            FROM numeric_tables child
            LEFT JOIN pg_index index_row
              ON index_row.indrelid =
                    child.table_oid
        ), top_tree AS (
            SELECT
                tree.relid AS index_oid,
                tree.parentrelid AS parent_index_oid
            FROM top_indexes top_index
            CROSS JOIN LATERAL
                pg_partition_tree(
                    top_index.index_oid) tree
            WHERE tree.level = 1
        ), root_tree AS (
            SELECT
                root_index.instrument,
                tree.relid AS index_oid,
                tree.parentrelid AS parent_index_oid
            FROM root_indexes root_index
            CROSS JOIN LATERAL
                pg_partition_tree(
                    root_index.index_oid) tree
            WHERE tree.level = 1
        ), inventory AS (
            SELECT
                mapping.instrument,
                'top'::TEXT AS layer,
                top_index.table_oid,
                top_index.index_oid,
                NULL::OID AS parent_index_oid,
                NULL::BIGINT AS snapshot_id,
                NULL::TEXT AS child_relation
            FROM table_map mapping
            CROSS JOIN top_indexes top_index

            UNION ALL

            SELECT
                root_index.instrument,
                'root',
                root_index.table_oid,
                root_index.index_oid,
                top_tree.parent_index_oid,
                NULL::BIGINT,
                NULL::TEXT
            FROM root_indexes root_index
            LEFT JOIN top_tree
              ON top_tree.index_oid =
                    root_index.index_oid

            UNION ALL

            SELECT
                default_index.instrument,
                'default',
                default_index.table_oid,
                default_index.index_oid,
                root_tree.parent_index_oid,
                NULL::BIGINT,
                NULL::TEXT
            FROM default_indexes default_index
            LEFT JOIN root_tree
              ON root_tree.instrument =
                    default_index.instrument
             AND root_tree.index_oid =
                    default_index.index_oid

            UNION ALL

            SELECT
                numeric_index.instrument,
                'numeric',
                numeric_index.table_oid,
                numeric_index.index_oid,
                root_tree.parent_index_oid,
                numeric_index.snapshot_id,
                numeric_index.child_relation
            FROM numeric_indexes numeric_index
            LEFT JOIN root_tree
              ON root_tree.instrument =
                    numeric_index.instrument
             AND root_tree.index_oid =
                    numeric_index.index_oid
        )
        SELECT
            inventory.instrument,
            inventory.layer,
            inventory.snapshot_id,
            inventory.child_relation,
            inventory.table_oid::BIGINT,
            index_relation.oid::BIGINT,
            index_relation.relfilenode::BIGINT,
            index_relation.relname,
            index_relation.relkind::TEXT,
            index_row.indisvalid,
            index_row.indisready,
            index_row.indisprimary,
            index_row.indisunique,
            access_method.amname,
            COALESCE(
                tablespace.spcname,
                database_tablespace.spcname),
            inventory.parent_index_oid::BIGINT,
            pg_get_indexdef(index_relation.oid)
        FROM inventory
        LEFT JOIN pg_index index_row
          ON index_row.indexrelid =
                inventory.index_oid
        LEFT JOIN pg_class index_relation
          ON index_relation.oid =
                inventory.index_oid
        LEFT JOIN pg_am access_method
          ON access_method.oid =
                index_relation.relam
        LEFT JOIN pg_tablespace tablespace
          ON tablespace.oid =
                index_relation.reltablespace
        CROSS JOIN LATERAL (
            SELECT default_tablespace.spcname
            FROM pg_database database
            JOIN pg_tablespace default_tablespace
              ON default_tablespace.oid =
                    database.dattablespace
            WHERE database.datname =
                    current_database()
        ) database_tablespace
        ORDER BY
            inventory.instrument,
            inventory.layer,
            inventory.snapshot_id,
            inventory.child_relation,
            index_relation.relname
        """;

    internal const string PublicationSourceValidationSql = """
        WITH named_publications AS (
            SELECT
                pointer.slot,
                generation.publication_id,
                generation.scrape_id,
                generation.metadata #>>
                    '{publicationPreparation,scrapeId}'
                    AS metadata_scrape_id,
                generation.metadata #>>
                    '{publicationPreparation,publicationId}'
                    AS metadata_publication_id,
                CASE
                    WHEN generation.metadata #>>
                            '{publicationPreparation,expectedPublishedScopeCount}'
                            ~ '^[1-9][0-9]*$'
                    THEN (
                        generation.metadata #>>
                            '{publicationPreparation,expectedPublishedScopeCount}'
                    )::BIGINT
                    ELSE NULL
                END AS expected_row_count
            FROM scrape_publication_state state
            CROSS JOIN LATERAL (
                VALUES
                    ('current'::TEXT,
                     state.current_publication_id),
                    ('previous'::TEXT,
                     state.previous_publication_id),
                    ('working'::TEXT,
                     state.working_publication_id)
            ) pointer(slot, publication_id)
            JOIN publication_generations generation
              ON generation.publication_id =
                    pointer.publication_id
            WHERE state.id = TRUE
              AND pointer.publication_id IS NOT NULL
              AND generation.scrape_id IS NOT NULL
        ), source_stats AS (
            SELECT
                source.published_scrape_id,
                COUNT(*)::BIGINT AS actual_row_count,
                COUNT(*) FILTER (
                    WHERE btrim(source.song_id) = ''
                       OR source.instrument NOT IN (
                            'Solo_Guitar',
                            'Solo_Bass',
                            'Solo_Vocals',
                            'Solo_Drums',
                            'Solo_PeripheralGuitar',
                            'Solo_PeripheralBass',
                            'Solo_PeripheralVocals',
                            'Solo_PeripheralCymbals',
                            'Solo_PeripheralDrums')
                       OR source.scope_kind <> 'alltime'
                       OR btrim(source.content_fingerprint) = ''
                       OR btrim(source.coverage_fingerprint) = ''
                       OR NOT source.is_complete
                       OR source.source_scrape_id <= 0
                       OR source.source_scrape_id >
                            source.published_scrape_id
                       OR source.reported_total_entries <
                            source.row_count
                       OR (
                            source.source_kind = 'snapshot'
                            AND (
                                source.source_snapshot_id IS NULL
                                OR source.source_snapshot_id <= 0
                                OR source.source_snapshot_id <>
                                    source.source_scrape_id
                                OR source.row_count <= 0
                                OR source.reported_total_pages <= 0
                            )
                       )
                       OR (
                            source.source_kind = 'empty'
                            AND (
                                source.source_snapshot_id IS NOT NULL
                                OR source.row_count <> 0
                                OR source.reported_total_entries <> 0
                                OR source.reported_total_pages <> 0
                            )
                       )
                       OR source.source_kind NOT IN (
                            'snapshot',
                            'empty')
                )::INTEGER AS invalid_row_count,
                (
                    COUNT(*)
                    - COUNT(DISTINCT (
                        source.instrument,
                        source.song_id,
                        source.scope_kind))
                )::INTEGER AS duplicate_key_count,
                encode(
                    sha256(
                        convert_to(
                            COALESCE(
                                string_agg(
                                    octet_length(
                                        source.instrument)::TEXT
                                        || ':'
                                        || source.instrument
                                        || octet_length(
                                            source.song_id)::TEXT
                                        || ':'
                                        || source.song_id
                                        || octet_length(
                                            source.scope_kind)::TEXT
                                        || ':'
                                        || source.scope_kind
                                        || chr(10),
                                    '' ORDER BY
                                        source.instrument
                                            COLLATE "C",
                                        source.song_id
                                            COLLATE "C",
                                        source.scope_kind
                                            COLLATE "C"),
                                ''),
                            'UTF8')),
                    'hex') AS actual_key_hash
            FROM leaderboard_published_scope_source source
            JOIN named_publications publication
              ON publication.scrape_id =
                    source.published_scrape_id
            GROUP BY source.published_scrape_id
        )
        SELECT
            publication.slot,
            publication.publication_id,
            publication.scrape_id,
            publication.expected_row_count,
            binding.row_count,
            COALESCE(
                source.actual_row_count,
                0)::BIGINT,
            binding.content_hash,
            COALESCE(
                source.actual_key_hash,
                encode(
                    sha256(convert_to('', 'UTF8')),
                    'hex')),
            COALESCE(
                source.invalid_row_count,
                0)::INTEGER,
            COALESCE(
                source.duplicate_key_count,
                0)::INTEGER,
            COALESCE(
                publication.metadata_scrape_id =
                    publication.scrape_id::TEXT
                AND publication.metadata_publication_id =
                    publication.publication_id::TEXT
                AND binding.binding_kind = 'scrape_id'
                AND binding.status = 'ready'
                AND binding.binding_json ->> 'table' =
                    'leaderboard_published_scope_source'
                AND binding.binding_json ->> 'publicationId' =
                    publication.publication_id::TEXT
                AND binding.binding_json ->> 'publishedScrapeId' =
                    publication.scrape_id::TEXT
                AND binding.binding_json ->> 'keyHashVersion' =
                    '1',
                FALSE) AS binding_identity_valid
        FROM named_publications publication
        LEFT JOIN publication_surface_bindings binding
          ON binding.publication_id =
                publication.publication_id
         AND binding.surface_name = 'solo_scope_sources'
        LEFT JOIN source_stats source
          ON source.published_scrape_id =
                publication.scrape_id
        ORDER BY publication.slot
        """;

    internal const string Sql = """
        WITH instrument_map(instrument, root_relation) AS (
            VALUES
                ('Solo_Guitar',
                 'leaderboard_entries_snapshot_solo_guitar'),
                ('Solo_Bass',
                 'leaderboard_entries_snapshot_solo_bass'),
                ('Solo_Vocals',
                 'leaderboard_entries_snapshot_solo_vocals'),
                ('Solo_Drums',
                 'leaderboard_entries_snapshot_solo_drums'),
                ('Solo_PeripheralGuitar',
                 'leaderboard_entries_snapshot_pro_guitar'),
                ('Solo_PeripheralBass',
                 'leaderboard_entries_snapshot_pro_bass'),
                ('Solo_PeripheralVocals',
                 'leaderboard_entries_snapshot_pro_vocals'),
                ('Solo_PeripheralCymbals',
                 'leaderboard_entries_snapshot_pro_cymbals'),
                ('Solo_PeripheralDrums',
                 'leaderboard_entries_snapshot_pro_drums')
        ), children AS (
            SELECT
                mapping.instrument,
                parent_namespace.nspname AS root_schema,
                parent.relname AS root_relation,
                COALESCE(
                    snapshot_parent.oid,
                    0)::BIGINT AS snapshot_parent_oid,
                parent.oid::BIGINT AS root_oid,
                COALESCE(
                    pg_get_partkeydef(parent.oid),
                    '') AS root_partition_key,
                COALESCE(
                    pg_get_expr(
                        parent.relpartbound,
                        parent.oid,
                        TRUE),
                    '') AS root_partition_bound,
                child_namespace.nspname AS child_schema,
                child.relname AS child_relation,
                substring(
                    child.relname
                    FROM '_s([1-9][0-9]*)$')::BIGINT AS snapshot_id,
                child.oid::BIGINT AS child_oid,
                child.relfilenode::BIGINT
                    AS child_relfilenode,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE) AS partition_bound
            FROM instrument_map mapping
            JOIN pg_class parent
              ON parent.relname = mapping.root_relation
            JOIN pg_namespace parent_namespace
              ON parent_namespace.oid = parent.relnamespace
             AND parent_namespace.nspname = 'public'
            LEFT JOIN pg_inherits parent_inheritance
              ON parent_inheritance.inhrelid = parent.oid
            LEFT JOIN pg_class snapshot_parent
              ON snapshot_parent.oid =
                    parent_inheritance.inhparent
            JOIN pg_inherits inheritance
              ON inheritance.inhparent = parent.oid
            JOIN pg_class child
              ON child.oid = inheritance.inhrelid
            JOIN pg_namespace child_namespace
              ON child_namespace.oid = child.relnamespace
             AND child_namespace.nspname = 'public'
            WHERE child.relname ~ (
                '^'
                || mapping.root_relation
                || '_s[1-9][0-9]*$')
        ), named_publication_scrapes AS (
            SELECT DISTINCT generation.scrape_id
            FROM scrape_publication_state state
            CROSS JOIN LATERAL unnest(ARRAY[
                state.current_publication_id,
                state.previous_publication_id,
                state.working_publication_id
            ]::BIGINT[]) pointer(publication_id)
            JOIN publication_generations generation
              ON generation.publication_id =
                    pointer.publication_id
            WHERE state.id = TRUE
              AND pointer.publication_id IS NOT NULL
              AND generation.scrape_id IS NOT NULL
        ), live_identity(instrument, snapshot_id) AS (
            SELECT
                state.instrument,
                state.active_snapshot_id
            FROM leaderboard_snapshot_state state
            WHERE state.active_snapshot_id > 0

            UNION

            SELECT
                projection.instrument,
                projection.source_snapshot_id
            FROM solo_current_projection_scope projection
            WHERE projection.source_snapshot_id > 0

            UNION

            SELECT
                source.instrument,
                source.source_snapshot_id
            FROM leaderboard_published_scope_source source
            JOIN named_publication_scrapes named
              ON named.scrape_id =
                    source.published_scrape_id
            WHERE source.source_snapshot_id > 0

            UNION

            SELECT
                child.instrument,
                scrape.id::BIGINT
            FROM scrape_log scrape
            JOIN children child
              ON child.snapshot_id = scrape.id
            WHERE scrape.status = 'running'

            UNION

            SELECT
                child.instrument,
                child.snapshot_id
            FROM children child
            WHERE @configuredResumeScrapeId > 0
              AND child.snapshot_id =
                    @configuredResumeScrapeId

            UNION

            SELECT
                failure.instrument,
                failure.scrape_id
            FROM scrape_writer_failures failure
            WHERE failure.replayed_at IS NULL

            UNION

            SELECT
                hold.instrument,
                hold.snapshot_id
            FROM snapshot_generation_retention_holds hold
            WHERE hold.released_at IS NULL
        )
        SELECT
            concat_ws(
                '|',
                child.instrument,
                child.root_schema,
                child.root_relation,
                child.snapshot_parent_oid,
                child.root_oid,
                child.root_partition_key,
                child.root_partition_bound,
                child.child_schema,
                child.child_relation,
                child.snapshot_id,
                child.child_oid,
                child.child_relfilenode,
                child.partition_bound) AS physical_key,
            EXISTS (
                SELECT 1
                FROM live_identity live
                WHERE live.instrument = child.instrument
                  AND live.snapshot_id = child.snapshot_id
            ) AS is_live
        FROM children child
        ORDER BY physical_key
        """;

    public async Task<SnapshotGenerationRetentionOracleResult> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long configuredResumeScrapeId,
        int commandTimeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The oracle transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        var publicationSourceValidations =
            await LoadPublicationSourceValidationsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                ct);
        var indexTopologyValidations =
            await LoadIndexTopologyValidationsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = Sql;
        command.Parameters.AddWithValue(
            "configuredResumeScrapeId",
            Math.Max(0, configuredResumeScrapeId));

        var childKeys = new HashSet<string>(
            StringComparer.Ordinal);
        var liveKeys = new HashSet<string>(
            StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            if (!childKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"The SQL liveness oracle returned duplicate physical child identity {key}.");
            }

            if (reader.GetBoolean(1))
                liveKeys.Add(key);
        }

        return new SnapshotGenerationRetentionOracleResult(
            childKeys,
            liveKeys,
            publicationSourceValidations,
            indexTopologyValidations);
    }

    private static async Task<IReadOnlyList<
        SnapshotGenerationRetentionIndexTopologyValidation>>
        LoadIndexTopologyValidationsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        var layers = SnapshotGenerationRetentionContract
            .Instruments.ToDictionary(
                static instrument => instrument.Instrument,
                static _ => new OracleIndexLayers(),
                StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = IndexTopologySql;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var instrument = reader.GetString(0);
            var layer = reader.GetString(1);
            var target = layers[instrument];
            List<SnapshotGenerationRetentionIndex>?
                numericIndexes = null;
            if (layer == "numeric")
            {
                if (reader.IsDBNull(2)
                    || reader.IsDBNull(3))
                {
                    throw new InvalidOperationException(
                        "Independent index oracle returned a numeric index without child identity.");
                }
                var numericKey = (
                    SnapshotId: reader.GetInt64(2),
                    ChildRelation: reader.GetString(3));
                if (!target.Numeric.TryGetValue(
                        numericKey,
                        out numericIndexes))
                {
                    numericIndexes = [];
                    target.Numeric.Add(
                        numericKey,
                        numericIndexes);
                }
                if (reader.IsDBNull(5))
                    continue;
            }
            else if (reader.IsDBNull(5))
            {
                throw new InvalidOperationException(
                    "Independent index oracle returned a hierarchy row without index identity.");
            }

            var index = new SnapshotGenerationRetentionIndex(
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.IsDBNull(15)
                    ? null
                    : reader.GetInt64(15),
                reader.GetString(16));
            switch (layer)
            {
                case "top":
                    target.Top.Add(index);
                    break;
                case "root":
                    target.Root.Add(index);
                    break;
                case "default":
                    target.Default.Add(index);
                    break;
                case "numeric":
                    numericIndexes!.Add(index);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Independent index oracle returned an unknown hierarchy layer.");
            }
        }

        return layers
            .OrderBy(
                static entry => entry.Key,
                StringComparer.Ordinal)
            .Select(entry =>
                BuildOracleIndexTopologyValidation(
                    entry.Key,
                    entry.Value))
            .ToArray();
    }

    private static
        SnapshotGenerationRetentionIndexTopologyValidation
        BuildOracleIndexTopologyValidation(
            string instrument,
            OracleIndexLayers layers)
    {
        var topOids = layers.Top
            .Select(static index => index.IndexOid)
            .ToHashSet();
        var attachedRoots = layers.Root
            .Where(index =>
                index.ParentIndexOid.HasValue
                && topOids.Contains(
                    index.ParentIndexOid.Value))
            .ToArray();
        var rootOids = attachedRoots
            .Select(static index => index.IndexOid)
            .ToHashSet();

        static int Missing(
            IEnumerable<long> parentOids,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                children) =>
            parentOids.Count(parentOid =>
                children.All(child =>
                    child.ParentIndexOid != parentOid));

        static int Duplicates(
            IEnumerable<long> parentOids,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                children) =>
            parentOids.Sum(parentOid =>
                Math.Max(
                    0,
                    children.Count(child =>
                        child.ParentIndexOid == parentOid) - 1));

        return new
            SnapshotGenerationRetentionIndexTopologyValidation(
                instrument,
                layers.Top
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                layers.Root
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                layers.Default
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                SnapshotGenerationRetentionContract
                    .RequiredSnapshotParentIndexNames
                    .Where(required =>
                        layers.Top.All(index =>
                            index.IndexName != required))
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                layers.Top.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "I"),
                layers.Top.Count(index => !index.IsReady),
                layers.Top.Count(index =>
                    index.ParentIndexOid.HasValue),
                Missing(topOids, attachedRoots),
                Duplicates(topOids, attachedRoots),
                layers.Root.Count(index =>
                    !index.ParentIndexOid.HasValue
                    || !topOids.Contains(
                        index.ParentIndexOid.Value)),
                layers.Root.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "I"),
                layers.Root.Count(index => !index.IsReady),
                Missing(rootOids, layers.Default),
                Duplicates(rootOids, layers.Default),
                layers.Default.Count(index =>
                    !index.ParentIndexOid.HasValue
                    || !rootOids.Contains(
                        index.ParentIndexOid.Value)),
                layers.Default.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "i"),
                layers.Default.Count(index =>
                    !index.IsReady),
                layers.Numeric
                    .OrderBy(
                        static entry =>
                            entry.Key.SnapshotId)
                    .ThenBy(
                        static entry =>
                            entry.Key.ChildRelation,
                        StringComparer.Ordinal)
                    .Select(entry =>
                        BuildOracleNumericChildIndexValidation(
                            instrument,
                            entry.Key.SnapshotId,
                            entry.Key.ChildRelation,
                            entry.Value,
                            layers.Root))
                    .ToArray());
    }

    private static
        SnapshotGenerationRetentionNumericChildIndexValidation
        BuildOracleNumericChildIndexValidation(
            string instrument,
            long snapshotId,
            string childRelation,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                childIndexes,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                parentIndexes)
    {
        var expectedParentOids = parentIndexes
            .Select(static index => index.IndexOid)
            .ToHashSet();
        var parentAttributes = parentIndexes.ToDictionary(
            static index => index.IndexOid);
        var missing = expectedParentOids.Count(parentOid =>
            childIndexes.All(index =>
                index.ParentIndexOid != parentOid));
        var duplicates = expectedParentOids.Sum(parentOid =>
            Math.Max(
                0,
                childIndexes.Count(index =>
                    index.ParentIndexOid == parentOid) - 1));
        var detached = childIndexes.Count(index =>
            !index.ParentIndexOid.HasValue
            || !expectedParentOids.Contains(
                index.ParentIndexOid.Value));
        var attributeMismatches =
            childIndexes.Count(index =>
                index.ParentIndexOid.HasValue
                && parentAttributes.TryGetValue(
                    index.ParentIndexOid.Value,
                    out var parent)
                && (index.IsPrimary != parent.IsPrimary
                    || index.IsUnique != parent.IsUnique
                    || index.AccessMethod !=
                        parent.AccessMethod));
        return new
            SnapshotGenerationRetentionNumericChildIndexValidation(
                instrument,
                snapshotId,
                childRelation,
                childIndexes
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                parentIndexes.Count,
                missing,
                duplicates,
                detached,
                childIndexes.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "i"),
                childIndexes.Count(index =>
                    !index.IsReady),
                attributeMismatches);
    }

    private static async Task<IReadOnlyList<
        SnapshotGenerationRetentionPublicationSourceValidation>>
        LoadPublicationSourceValidationsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = PublicationSourceValidationSql;
        var validations =
            new List<
                SnapshotGenerationRetentionPublicationSourceValidation>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            validations.Add(
                new SnapshotGenerationRetentionPublicationSourceValidation(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetInt64(3),
                    reader.IsDBNull(4)
                        ? null
                        : reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6)
                        ? null
                        : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.GetBoolean(10)));
        }

        return validations;
    }

    private sealed class OracleIndexLayers
    {
        public List<SnapshotGenerationRetentionIndex> Top { get; } =
            [];
        public List<SnapshotGenerationRetentionIndex> Root { get; } =
            [];
        public List<SnapshotGenerationRetentionIndex> Default { get; } =
            [];
        public Dictionary<
            (long SnapshotId, string ChildRelation),
            List<SnapshotGenerationRetentionIndex>> Numeric { get; } =
            [];
    }
}
