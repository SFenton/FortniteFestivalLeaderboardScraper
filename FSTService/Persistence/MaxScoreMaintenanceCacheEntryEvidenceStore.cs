using Npgsql;

namespace FSTService.Persistence;

internal sealed record MaxScoreMaintenanceCacheEntryValidation(
    long EvidenceRows,
    long LegacyStagingRows,
    long PublicationStagingRows,
    long LegacyMismatches,
    long PublicationMismatches)
{
    internal bool Matches(long expectedEntryCount)
        => expectedEntryCount > 0
           && EvidenceRows == expectedEntryCount
           && LegacyStagingRows == expectedEntryCount
           && PublicationStagingRows == expectedEntryCount
           && LegacyMismatches == 0
           && PublicationMismatches == 0;

    internal string Describe(long expectedEntryCount)
        => $"evidence={EvidenceRows}/{expectedEntryCount}, "
           + $"legacy={LegacyStagingRows}/{expectedEntryCount} "
           + $"(mismatches={LegacyMismatches}), "
           + $"publication={PublicationStagingRows}/{expectedEntryCount} "
           + $"(mismatches={PublicationMismatches})";
}

internal static class MaxScoreMaintenanceCacheEntryEvidenceStore
{
    private const string StagingComparisonSql = """
        WITH legacy AS MATERIALIZED (
            SELECT cache_key,
                   etag,
                   encode(
                       digest(json_data, 'sha256'),
                       'hex') AS json_sha256
            FROM api_response_cache_staging
        ), publication AS MATERIALIZED (
            SELECT cache_key,
                   etag,
                   encode(
                       digest(json_data, 'sha256'),
                       'hex') AS json_sha256
            FROM publication_api_response_cache_staging
            WHERE publication_id = @publicationId
        ), mismatches AS (
            SELECT COUNT(*)::BIGINT AS row_count
            FROM legacy
            FULL JOIN publication USING (cache_key)
            WHERE legacy.cache_key IS NULL
               OR publication.cache_key IS NULL
               OR legacy.etag IS DISTINCT FROM publication.etag
               OR legacy.json_sha256 IS DISTINCT FROM
                    publication.json_sha256
        )
        SELECT
            (SELECT COUNT(*)::BIGINT FROM legacy),
            (SELECT COUNT(*)::BIGINT FROM publication),
            mismatches.row_count
        FROM mismatches
        """;

    private const string ValidationSql = """
        WITH evidence AS MATERIALIZED (
            SELECT cache_key, etag, json_sha256
            FROM max_score_maintenance_cache_entries
            WHERE manifest_sha256 = @manifestSha256
        ), legacy AS MATERIALIZED (
            SELECT cache_key,
                   etag,
                   encode(
                       digest(json_data, 'sha256'),
                       'hex') AS json_sha256
            FROM api_response_cache_staging
        ), publication AS MATERIALIZED (
            SELECT cache_key,
                   etag,
                   encode(
                       digest(json_data, 'sha256'),
                       'hex') AS json_sha256
            FROM publication_api_response_cache_staging
            WHERE publication_id = @publicationId
        ), legacy_mismatches AS (
            SELECT COUNT(*)::BIGINT AS row_count
            FROM evidence
            FULL JOIN legacy USING (cache_key)
            WHERE evidence.cache_key IS NULL
               OR legacy.cache_key IS NULL
               OR evidence.etag IS DISTINCT FROM legacy.etag
               OR evidence.json_sha256 IS DISTINCT FROM
                    legacy.json_sha256
        ), publication_mismatches AS (
            SELECT COUNT(*)::BIGINT AS row_count
            FROM evidence
            FULL JOIN publication USING (cache_key)
            WHERE evidence.cache_key IS NULL
               OR publication.cache_key IS NULL
               OR evidence.etag IS DISTINCT FROM publication.etag
               OR evidence.json_sha256 IS DISTINCT FROM
                    publication.json_sha256
        )
        SELECT
            (SELECT COUNT(*)::BIGINT FROM evidence),
            (SELECT COUNT(*)::BIGINT FROM legacy),
            (SELECT COUNT(*)::BIGINT FROM publication),
            legacy_mismatches.row_count,
            publication_mismatches.row_count
        FROM legacy_mismatches,
             publication_mismatches
        """;

    internal static async Task CaptureAsync(
        string manifestSha256,
        long publicationId,
        long expectedEntryCount,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The cache evidence transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        await using (var compare = connection.CreateCommand())
        {
            compare.Transaction = transaction;
            compare.CommandTimeout = 600;
            compare.CommandText = StagingComparisonSql;
            compare.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            await using var reader =
                await compare.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException(
                    "Maintenance cache staging comparison returned no evidence.");
            }

            var legacyRows = reader.GetInt64(0);
            var publicationRows = reader.GetInt64(1);
            var mismatches = reader.GetInt64(2);
            if (expectedEntryCount <= 0
                || legacyRows != expectedEntryCount
                || publicationRows != expectedEntryCount
                || mismatches != 0)
            {
                throw new InvalidOperationException(
                    "Maintenance cache staging tables differ before immutable evidence capture: "
                    + $"expected={expectedEntryCount}, legacy={legacyRows}, "
                    + $"publication={publicationRows}, mismatches={mismatches}.");
            }
        }

        await using var capture = connection.CreateCommand();
        capture.Transaction = transaction;
        capture.CommandTimeout = 600;
        capture.CommandText = """
            INSERT INTO max_score_maintenance_cache_entries (
                manifest_sha256,
                cache_key,
                etag,
                json_sha256)
            SELECT @manifestSha256,
                   cache_key,
                   etag,
                   encode(
                       digest(json_data, 'sha256'),
                       'hex')
            FROM publication_api_response_cache_staging
            WHERE publication_id = @publicationId
            """;
        capture.Parameters.AddWithValue(
            "manifestSha256",
            manifestSha256);
        capture.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        var captured = await capture.ExecuteNonQueryAsync(ct);
        if (captured != expectedEntryCount)
        {
            throw new InvalidOperationException(
                $"Immutable cache evidence captured {captured} of {expectedEntryCount} expected entries.");
        }
    }

    internal static async Task ValidateAsync(
        string manifestSha256,
        long publicationId,
        long expectedEntryCount,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken ct)
    {
        var validation = await ReadAsync(
            manifestSha256,
            publicationId,
            connection,
            transaction,
            ct);
        ThrowIfInvalid(validation, expectedEntryCount);
    }

    internal static void Validate(
        string manifestSha256,
        long publicationId,
        long expectedEntryCount,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = CreateValidationCommand(
            manifestSha256,
            publicationId,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "Persisted max-score cache evidence was unavailable.");
        }
        ThrowIfInvalid(ReadValidation(reader), expectedEntryCount);
    }

    private static async Task<MaxScoreMaintenanceCacheEntryValidation>
        ReadAsync(
            string manifestSha256,
            long publicationId,
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            CancellationToken ct)
    {
        await using var command = CreateValidationCommand(
            manifestSha256,
            publicationId,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Persisted max-score cache evidence was unavailable.");
        }
        return ReadValidation(reader);
    }

    private static NpgsqlCommand CreateValidationCommand(
        string manifestSha256,
        long publicationId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 600;
        command.CommandText = ValidationSql;
        command.Parameters.AddWithValue(
            "manifestSha256",
            manifestSha256);
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        return command;
    }

    private static MaxScoreMaintenanceCacheEntryValidation
        ReadValidation(NpgsqlDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));

    private static void ThrowIfInvalid(
        MaxScoreMaintenanceCacheEntryValidation validation,
        long expectedEntryCount)
    {
        if (!validation.Matches(expectedEntryCount))
        {
            throw new InvalidOperationException(
                "Maintenance cache staging no longer matches its immutable entry evidence: "
                + validation.Describe(expectedEntryCount)
                + ".");
        }
    }
}
