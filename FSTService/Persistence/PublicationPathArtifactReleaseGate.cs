using System.Text.Json;
using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// Canonical path-artifact validation for explicit schema commands, pre-pool
/// startup selection, and publication-bound read admission.
/// </summary>
/// <remarks>
/// API-only, explicit skip-schema, and rollout read-only startup modes never
/// run DDL, so they can start against a database whose path artifact release
/// has not been applied yet. Reading a stale or missing manifest would either
/// fail closed at request time or, worse, keep serving an unversioned surface.
/// Explicit schema commands refuse invalid current/working bindings. Ordinary
/// startup selects sticky read-only serving before runtime pools; invalid
/// previous bindings are warnings rather than mutation-startup refusals.
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
            var states = await ReadStatesAsync(conn, null, currentOnly: true, ct);
            return states.SingleOrDefault() ?? PublicationPathArtifactReleaseState.NoPublication;
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

    internal static async Task<IReadOnlyList<PublicationPathArtifactInitializationFailure>>
        ValidateInitializedActiveBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken ct,
        bool allowLegacyUpgrade = false,
        bool requireReadyMutationPointers = false)
    {
        var states = await ReadStatesAsync(connection, transaction, currentOnly: false, ct);
        var failures = new List<PublicationPathArtifactInitializationFailure>();
        var warnings = new List<PublicationPathArtifactInitializationFailure>();
        foreach (var state in states)
        {
            ct.ThrowIfCancellationRequested();
            if (state.BindingKind is null)
            {
                if (requireReadyMutationPointers && state.IsMutationPointer)
                    failures.Add(new(state.CurrentPublicationId!.Value, "binding_missing"));
                continue;
            }
            if (allowLegacyUpgrade && state.CanUpgradeLegacyManifest)
                continue;
            var code = state.ManifestVersionFailureCode;
            if (code is null && (state.Status == PublicationGenerationStatus.Ready
                || requireReadyMutationPointers && state.IsMutationPointer))
                code = state.FailureCode;
            if (code is not null)
            {
                var diagnostic = new PublicationPathArtifactInitializationFailure(
                    state.CurrentPublicationId!.Value, code);
                if (state.IsMutationPointer)
                    failures.Add(diagnostic);
                else
                    warnings.Add(diagnostic);
            }
        }
        if (failures.Count > 0)
            throw new PublicationPathArtifactInitializationException(failures);
        return warnings;
    }

    internal static async Task ValidateExistingBeforeInitializationAsync(
        NpgsqlDataSource dataSource,
        CancellationToken ct)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            _ = await ValidateInitializedActiveBindingsAsync(
                connection, null, ct, allowLegacyUpgrade: true);
        }
        catch (PostgresException exception) when (
            exception.SqlState is UndefinedTable or UndefinedFunction or UndefinedColumn)
        {
            // A genuinely older schema must first install the bounded path step.
        }
    }

    private static async Task<IReadOnlyList<PublicationPathArtifactReleaseState>> ReadStatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool currentOnly,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 15;
        command.CommandText = """
            WITH pointers AS (
                SELECT publication_id,bool_or(is_mutation_pointer) AS is_mutation_pointer FROM (
                    SELECT current_publication_id AS publication_id,TRUE AS is_mutation_pointer
                    FROM public.scrape_publication_state WHERE id=TRUE
                    UNION ALL
                    SELECT previous_publication_id,FALSE
                    FROM public.scrape_publication_state WHERE id=TRUE AND NOT @currentOnly
                    UNION ALL
                    SELECT working_publication_id,TRUE
                    FROM public.scrape_publication_state WHERE id=TRUE AND NOT @currentOnly
                ) selected WHERE publication_id IS NOT NULL GROUP BY publication_id
            )
            SELECT pointer.publication_id,binding.binding_kind,binding.status,
                binding.row_count,binding.content_hash,
                binding.binding_json ->> 'contractVersion',
                binding.binding_json ->> 'manifestVersion',
                binding.binding_json ->> 'source',
                (SELECT count(*) FROM public.publication_path_artifacts artifact
                 WHERE artifact.publication_id=pointer.publication_id),
                (SELECT catalog.song_count FROM public.publication_song_catalog catalog
                 WHERE catalog.publication_id=pointer.publication_id AND catalog.is_exact),
                public.publication_path_artifact_manifest_sha256(pointer.publication_id),
                generation.scrape_id,binding.binding_json::text,pointer.is_mutation_pointer
            FROM pointers pointer
            LEFT JOIN public.publication_generations generation
              ON generation.publication_id=pointer.publication_id
            LEFT JOIN public.publication_surface_bindings binding
              ON binding.publication_id=pointer.publication_id
             AND binding.surface_name='path_artifacts'
            ORDER BY pointer.publication_id
            """;
        command.Parameters.AddWithValue("currentOnly", currentOnly);
        var result = new List<PublicationPathArtifactReleaseState>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseVersion(reader, 5), ParseVersion(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                GenerationScrapeId: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                BindingJson: reader.IsDBNull(12) ? null : reader.GetString(12),
                IsMutationPointer: reader.GetBoolean(13)));
        }
        return result;
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
    string? SchemaError = null,
    long? GenerationScrapeId = null,
    string? BindingJson = null,
    bool IsMutationPointer = true)
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

    public bool IsReleased => FailureCode is null;

    internal bool CanUpgradeLegacyManifest
    {
        get
        {
            if (BindingJson is null)
                return false;
            using var document = JsonDocument.Parse(BindingJson);
            var json = document.RootElement;
            return json.ValueKind == JsonValueKind.Object
                && (!json.TryGetProperty("manifestVersion", out var version)
                    || version.ValueKind == JsonValueKind.Number
                    && version.TryGetInt32(out var number)
                    && number > 0 && number < PublicationPathArtifactSchema.ManifestVersion);
        }
    }

    public string? ManifestVersionFailureCode
    {
        get
        {
            if (BindingJson is null)
                return "binding_json_invalid";
            using var document = JsonDocument.Parse(BindingJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("manifestVersion", out var version)
                || version.ValueKind != JsonValueKind.Number)
                return "manifest_version_invalid";
            var raw = version.GetRawText();
            if (raw.Length == 0 || raw == "0" || raw.Any(static value => value is < '0' or > '9'))
                return "manifest_version_invalid";
            if (!version.TryGetInt32(out var number) || number > PublicationPathArtifactSchema.ManifestVersion)
                return "manifest_version_future";
            return number < PublicationPathArtifactSchema.ManifestVersion ? "manifest_version_outdated" : null;
        }
    }

    public string? FailureCode
    {
        get
        {
            if (SchemaError is not null)
                return "schema_missing";
            if (CurrentPublicationId is null)
                return null;
            if (BindingKind is null)
                return "binding_missing";
            if (ManifestVersionFailureCode is { } versionFailure)
                return versionFailure;
            if (BindingKind != PublicationPathArtifactSchema.ManifestBindingKind)
                return "binding_kind_invalid";
            if (Status != PublicationGenerationStatus.Ready)
                return "binding_not_ready";
            using var document = JsonDocument.Parse(BindingJson!);
            var json = document.RootElement;
            if (!StringEquals(json, "table", PublicationPathArtifactSchema.TableName)
                || !json.TryGetProperty("authoritative", out var authoritative)
                || authoritative.ValueKind != JsonValueKind.True
                || !json.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(source.GetString()))
                return "binding_json_invalid";
            if (!NumberEquals(json, "publicationId", CurrentPublicationId))
                return "binding_publication_mismatch";
            if (GenerationScrapeId is null || !NumberEquals(json, "scrapeId", GenerationScrapeId))
                return "binding_scrape_mismatch";
            if (!NumberEquals(json, "contractVersion", PublicationPathArtifactSchema.ContractVersion))
                return "binding_contract_invalid";
            if (ExpectedRowCount is not int expected || expected < 0
                || !NumberEquals(json, "expectedRowCount", expected))
                return "binding_expected_count_invalid";
            if (SnapshotRowCount != expected || BindingRowCount != SnapshotRowCount)
                return "binding_row_count_mismatch";
            if (BindingContentHash is null || BindingContentHash.Length != 64
                || BindingContentHash.Any(static value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !string.Equals(BindingContentHash, CanonicalContentHash, StringComparison.Ordinal))
                return "binding_content_hash_mismatch";
            return null;
        }
    }

    private static bool NumberEquals(JsonElement json, string name, long? expected) =>
        json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var actual)
        && actual == expected;

    private static bool StringEquals(JsonElement json, string name, string expected) =>
        json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == expected;

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

        return FailureCode == "binding_content_hash_mismatch"
            ? $"publication {CurrentPublicationId} binding hash does not match the canonical manifest hash"
            : $"publication {CurrentPublicationId} path binding failed canonical validation ({FailureCode})";
    }
}

public sealed record PublicationPathArtifactInitializationFailure(long PublicationId, string Code);

public sealed class PublicationPathArtifactInitializationException(
    IReadOnlyList<PublicationPathArtifactInitializationFailure> failures)
    : InvalidOperationException(
        "Publication path artifact initialization refused without rewriting invalid bindings: "
        + string.Join(", ", failures.Select(static failure => $"{failure.PublicationId}:{failure.Code}")))
{
    public IReadOnlyList<PublicationPathArtifactInitializationFailure> Failures { get; } = failures;
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
