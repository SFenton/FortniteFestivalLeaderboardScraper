using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// Read-only startup readiness gate for roles that do not run schema
/// initialization but do read publication-bound path artifacts.
/// </summary>
/// <remarks>
/// API-only, explicit skip-schema, and rollout read-only startup modes never
/// run DDL, so they can start against a database whose path artifact release
/// has not been applied yet. Reading a stale or missing manifest would either
/// fail closed at request time or, worse, keep serving an unversioned surface.
/// The gate fails fast at startup instead, with an operator instruction to run
/// the API/schema initializer first.
/// </remarks>
public static class PublicationPathArtifactReleaseGate
{
    private const string UndefinedTable = "42P01";
    private const string UndefinedFunction = "42883";
    private const string UndefinedColumn = "42703";

    public static async Task<PublicationPathArtifactReleaseState>
        ReadAsync(
            NpgsqlDataSource dataSource,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        try
        {
            await using var conn =
                await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    publication.current_publication_id,
                    binding.binding_kind,
                    binding.status,
                    binding.row_count,
                    binding.content_hash,
                    binding.binding_json ->> 'contractVersion',
                    binding.binding_json ->> 'manifestVersion',
                    binding.binding_json ->> 'source',
                    (
                        SELECT COUNT(*)
                        FROM publication_path_artifacts artifact
                        WHERE artifact.publication_id =
                            publication.current_publication_id
                    ),
                    (
                        SELECT catalog.song_count
                        FROM publication_song_catalog catalog
                        WHERE catalog.publication_id =
                            publication.current_publication_id
                          AND catalog.is_exact
                    ),
                    publication_path_artifact_manifest_sha256(
                        publication.current_publication_id)
                FROM scrape_publication_state publication
                LEFT JOIN publication_surface_bindings binding
                  ON binding.publication_id =
                        publication.current_publication_id
                 AND binding.surface_name =
                        '{PublicationSurfaceNames.PathArtifacts}'
                WHERE publication.id = TRUE
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return PublicationPathArtifactReleaseState.NoPublication;

            if (await reader.IsDBNullAsync(0, ct))
                return PublicationPathArtifactReleaseState.NoPublication;

            return new PublicationPathArtifactReleaseState(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseVersion(reader, 5),
                ParseVersion(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10));
        }
        catch (PostgresException ex) when (
            ex.SqlState is UndefinedTable
                or UndefinedFunction
                or UndefinedColumn)
        {
            return PublicationPathArtifactReleaseState.SchemaMissing(
                ex.MessageText);
        }
    }

    /// <summary>
    /// Throws <see cref="PublicationPathArtifactReleaseException"/> when the
    /// current publication does not expose a ready, current-version path
    /// artifact manifest.
    /// </summary>
    public static async Task EnsureReleasedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken ct = default)
    {
        var state = await ReadAsync(dataSource, ct);
        if (state.IsReleased)
            return;

        throw new PublicationPathArtifactReleaseException(state);
    }

    private static int? ParseVersion(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
           || !int.TryParse(
                reader.GetString(ordinal),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            ? null
            : value;
}

/// <summary>
/// Read-only evidence about the current publication's path artifact release.
/// </summary>
public sealed record PublicationPathArtifactReleaseState(
    long? CurrentPublicationId,
    string? BindingKind,
    string? Status,
    long? BindingRowCount,
    string? BindingContentHash,
    int? ContractVersion,
    int? ManifestVersion,
    string? Source,
    long SnapshotRowCount,
    int? ExpectedRowCount,
    string? CanonicalContentHash,
    string? SchemaError = null)
{
    /// <summary>No publication has been published yet; nothing to verify.</summary>
    public static readonly PublicationPathArtifactReleaseState NoPublication =
        new(null, null, null, null, null, null, null, null, 0, null, null);

    public static PublicationPathArtifactReleaseState SchemaMissing(
        string detail)
        => new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            detail);

    public bool IsReleased
    {
        get
        {
            if (SchemaError is not null)
                return false;
            if (CurrentPublicationId is null)
                return true;

            return string.Equals(
                    BindingKind,
                    PublicationPathArtifactSchema.ManifestBindingKind,
                    StringComparison.Ordinal)
                && string.Equals(
                    Status,
                    PublicationGenerationStatus.Ready,
                    StringComparison.Ordinal)
                && ContractVersion
                    == PublicationPathArtifactSchema.ContractVersion
                && ManifestVersion
                    == PublicationPathArtifactSchema.ManifestVersion
                && ExpectedRowCount is int expected
                && SnapshotRowCount == expected
                && BindingRowCount == SnapshotRowCount
                && BindingContentHash is not null
                && string.Equals(
                    BindingContentHash,
                    CanonicalContentHash,
                    StringComparison.Ordinal);
        }
    }

    public string DescribeFailure()
    {
        if (SchemaError is not null)
        {
            return "the publication path artifact schema is missing ("
                + SchemaError
                + ")";
        }

        if (BindingKind is null)
        {
            return $"publication {CurrentPublicationId} has no path_artifacts "
                + "surface binding";
        }

        if (!string.Equals(
                BindingKind,
                PublicationPathArtifactSchema.ManifestBindingKind,
                StringComparison.Ordinal)
            || !string.Equals(
                Status,
                PublicationGenerationStatus.Ready,
                StringComparison.Ordinal))
        {
            return $"publication {CurrentPublicationId} binding is "
                + $"'{BindingKind}'/'{Status}' instead of "
                + $"'{PublicationPathArtifactSchema.ManifestBindingKind}'"
                + $"/'{PublicationGenerationStatus.Ready}'";
        }

        if (ContractVersion != PublicationPathArtifactSchema.ContractVersion
            || ManifestVersion
                != PublicationPathArtifactSchema.ManifestVersion)
        {
            return $"publication {CurrentPublicationId} binding is "
                + $"contractVersion={ContractVersion?.ToString() ?? "null"}, "
                + $"manifestVersion={ManifestVersion?.ToString() ?? "null"} "
                + "instead of contractVersion="
                + PublicationPathArtifactSchema.ContractVersion
                + ", manifestVersion="
                + PublicationPathArtifactSchema.ManifestVersion;
        }

        if (ExpectedRowCount is not int expected
            || SnapshotRowCount != expected
            || BindingRowCount != SnapshotRowCount)
        {
            return $"publication {CurrentPublicationId} snapshot covers "
                + $"{SnapshotRowCount} of "
                + $"{ExpectedRowCount?.ToString() ?? "unknown"} catalog songs "
                + $"(binding row count {BindingRowCount?.ToString() ?? "null"})";
        }

        return $"publication {CurrentPublicationId} binding hash does not "
            + "match the canonical manifest hash";
    }
}

/// <summary>
/// Startup failure for a role that reads publication-bound path artifacts but
/// does not run schema initialization.
/// </summary>
public sealed class PublicationPathArtifactReleaseException
    : InvalidOperationException
{
    public PublicationPathArtifactReleaseException(
        PublicationPathArtifactReleaseState state)
        : base(
            "Publication-bound path artifacts are enabled "
            + "(Scraper:UsePublicationPathArtifacts=true) but this role does "
            + "not run schema initialization "
            + "(ApiOnly, SkipStartupSchemaInitialization, or "
            + "RolloutReadOnlyStartup), and "
            + state.DescribeFailure()
            + ". Start the API/schema-initializing role first so the "
            + "publication path artifact release is applied, then start this "
            + "role. This role never runs DDL.")
    {
        State = state;
    }

    public PublicationPathArtifactReleaseState State { get; }
}
