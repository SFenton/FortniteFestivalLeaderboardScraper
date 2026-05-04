using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;

namespace FSTService.Persistence;

public sealed class BandIdentityPersistence
{
    public const string TableName = "band_identity";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<BandIdentityPersistence> _log;

    public BandIdentityPersistence(NpgsqlDataSource dataSource, ILogger<BandIdentityPersistence> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task<BandIdentityUpsertResult> UpsertForTeamsAsync(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> teamsByBandType,
        CancellationToken ct = default)
    {
        var requestedTeams = teamsByBandType.Sum(static kvp => kvp.Value.Count);
        if (requestedTeams == 0)
            return new BandIdentityUpsertResult(0, 0, 0);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TEMP TABLE _band_identity_refresh_keys (
                    band_type TEXT NOT NULL,
                    team_key  TEXT NOT NULL,
                    band_id   TEXT NOT NULL,
                    PRIMARY KEY (band_type, team_key)
                ) ON COMMIT DROP
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        var copiedTeams = 0;
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY _band_identity_refresh_keys (band_type, team_key, band_id) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var (bandType, teamKeys) in teamsByBandType.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var teamKey in teamKeys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(bandType) || string.IsNullOrWhiteSpace(teamKey))
                        continue;

                    copiedTeams++;
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(bandType, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(teamKey, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(BandIdentity.CreateBandId(bandType, teamKey), NpgsqlDbType.Text, ct);
                }
            }

            await writer.CompleteAsync(ct);
        }

        if (copiedTeams == 0)
        {
            await tx.RollbackAsync(ct);
            return new BandIdentityUpsertResult(requestedTeams, 0, 0);
        }

        int upsertedRows;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandTimeout = 0;
            cmd.CommandText = $"""
                WITH source_summary AS (
                    SELECT be.band_type,
                           be.team_key,
                           count(*)::integer AS appearance_count,
                           min(be.first_seen_at) AS first_seen_at,
                           max(be.last_updated_at) AS last_seen_at
                    FROM band_entries be
                    JOIN _band_identity_refresh_keys keys
                      ON keys.band_type = be.band_type
                     AND keys.team_key = be.team_key
                    GROUP BY be.band_type, be.team_key
                )
                INSERT INTO {TableName} (
                    band_id,
                    band_type,
                    team_key,
                    member_account_ids,
                    appearance_count,
                    first_seen_at,
                    last_seen_at,
                    updated_at,
                    source)
                SELECT keys.band_id,
                       keys.band_type,
                       keys.team_key,
                       string_to_array(keys.team_key, ':'),
                       COALESCE(source_summary.appearance_count, 0),
                       source_summary.first_seen_at,
                       source_summary.last_seen_at,
                       now(),
                       'band_maintenance'
                FROM _band_identity_refresh_keys keys
                LEFT JOIN source_summary
                  ON source_summary.band_type = keys.band_type
                 AND source_summary.team_key = keys.team_key
                ON CONFLICT (band_id) DO UPDATE SET
                    band_type = EXCLUDED.band_type,
                    team_key = EXCLUDED.team_key,
                    member_account_ids = EXCLUDED.member_account_ids,
                    appearance_count = GREATEST({TableName}.appearance_count, EXCLUDED.appearance_count),
                    first_seen_at = COALESCE(LEAST({TableName}.first_seen_at, EXCLUDED.first_seen_at), {TableName}.first_seen_at, EXCLUDED.first_seen_at),
                    last_seen_at = COALESCE(GREATEST({TableName}.last_seen_at, EXCLUDED.last_seen_at), {TableName}.last_seen_at, EXCLUDED.last_seen_at),
                    updated_at = EXCLUDED.updated_at,
                    source = EXCLUDED.source
                """;
            upsertedRows = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _log.LogInformation(
            "Band identity refresh complete: {CopiedTeams:N0} team(s), {UpsertedRows:N0} row(s).",
            copiedTeams,
            upsertedRows);
        return new BandIdentityUpsertResult(requestedTeams, copiedTeams, upsertedRows);
    }
}

public sealed record BandIdentityUpsertResult(
    int RequestedTeams,
    int CopiedTeams,
    int UpsertedRows);
