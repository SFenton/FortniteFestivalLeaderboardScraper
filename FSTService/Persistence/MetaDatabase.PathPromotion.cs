using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Phase B staged path promotion into the working publication snapshot.
/// Candidate rows are the only thing this file mutates; live <c>songs</c>
/// rows are promoted later, inside the publication commit transaction.
/// </summary>
public sealed partial class MetaDatabase
{
    /// <summary>
    /// Returns true when an immutable generation is referenced by live song
    /// state or any retained publication snapshot/promotion row.
    /// </summary>
    public bool IsPathArtifactGenerationReferenced(
        string songId,
        string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);

        using var conn = _ds.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM songs song
                WHERE song.song_id = @songId
                  AND song.path_artifact_generation_id =
                        @generationId
                UNION ALL
                SELECT 1
                FROM publication_path_artifacts artifact
                WHERE artifact.song_id = @songId
                  AND (
                        artifact.path_artifact_generation_id =
                            @generationId
                        OR artifact.promotion_generation_id =
                            @generationId
                  )
            )
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue(
            "generationId",
            generationId);
        return command.ExecuteScalar() is true;
    }

    /// <summary>
    /// Applies one validated staged generation to the working publication
    /// snapshot and rebinds the ready <c>path_artifacts</c> manifest.
    /// The snapshot stays complete even when other pending songs are
    /// excluded, blocked, or failed.
    /// </summary>
    public PublicationPathPromotionOutcome
        ApplyWorkingPublicationPathPromotion(
            PublicationPathPromotionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Promotion);

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;
        int applied;
        using (var apply = conn.CreateCommand())
        {
            apply.Transaction = tx;
            apply.CommandText =
                PublicationPathArtifactSchema.ApplyStagedPromotionSql;
            AddStagedPromotionParameters(apply, request, now);
            applied = apply.ExecuteNonQuery();
        }

        if (applied == 0)
        {
            var outcome = ClassifyStagedPromotionFailure(conn, tx, request);
            tx.Rollback();
            return outcome;
        }

        if (applied > 1)
        {
            tx.Rollback();
            throw new InvalidOperationException(
                $"Staged promotion for {request.SongId} matched {applied} snapshot rows.");
        }

        BindPublicationPathArtifacts(
            conn,
            tx,
            request.PublicationId,
            PublicationPathArtifactSchema.ScrapePassStagingSource,
            now,
            requireReady: true);
        tx.Commit();
        return PublicationPathPromotionOutcome.Applied;
    }

    private static PublicationPathPromotionOutcome
        ClassifyStagedPromotionFailure(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            PublicationPathPromotionRequest request)
    {
        using var classify = conn.CreateCommand();
        classify.Transaction = tx;
        classify.CommandText =
            PublicationPathArtifactSchema.ClassifyStagedPromotionSql;
        classify.Parameters.AddWithValue(
            "publicationId",
            request.PublicationId);
        classify.Parameters.AddWithValue("scrapeId", request.ScrapeId);
        classify.Parameters.AddWithValue("songId", request.SongId);
        using var reader = classify.ExecuteReader();
        if (!reader.Read())
            return PublicationPathPromotionOutcome.PublicationNotStaging;

        var publicationBuilding = reader.GetBoolean(0);
        var rowPresent = reader.GetBoolean(1);
        if (!publicationBuilding)
            return PublicationPathPromotionOutcome.PublicationNotStaging;

        return rowPresent
            ? PublicationPathPromotionOutcome.Conflict
            : PublicationPathPromotionOutcome.SongMissing;
    }

    private static void AddStagedPromotionParameters(
        NpgsqlCommand command,
        PublicationPathPromotionRequest request,
        DateTime now)
    {
        var promotion = request.Promotion;
        var maxScores = promotion.MaxScores;
        command.Parameters.AddWithValue(
            "publicationId",
            request.PublicationId);
        command.Parameters.AddWithValue("scrapeId", request.ScrapeId);
        command.Parameters.AddWithValue("songId", request.SongId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            request.ExpectedRevision);
        command.Parameters.Add(
            "expectedGenerationId",
            NpgsqlDbType.Text).Value =
            (object?)request.ExpectedGenerationId ?? DBNull.Value;
        command.Parameters.Add(
            "expectedCatalogLastModified",
            NpgsqlDbType.Text).Value =
            (object?)request.ExpectedCatalogLastModified ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "generationId",
            promotion.ArtifactGenerationId);
        command.Parameters.AddWithValue("attemptId", promotion.AttemptId);
        command.Parameters.AddWithValue(
            "datFileHash",
            promotion.DatFileHash);
        command.Parameters.Add(
            "songLastModified",
            NpgsqlDbType.Text).Value =
            (object?)promotion.SongLastModified ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "generatedAt",
            promotion.GeneratedAtUtc);
        command.Parameters.AddWithValue(
            "choptVersion",
            promotion.Runtime.Version);
        command.Parameters.AddWithValue(
            "choptBinarySha256",
            promotion.Runtime.BinarySha256);
        command.Parameters.AddWithValue(
            "profile",
            promotion.Runtime.Profile);
        command.Parameters.Add(
            "expectedInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            promotion.ExpectedInstruments.ToArray();
        AddNullableInt(command, "maxLead", maxScores.MaxLeadScore);
        AddNullableInt(command, "maxBass", maxScores.MaxBassScore);
        AddNullableInt(command, "maxDrums", maxScores.MaxDrumsScore);
        AddNullableInt(command, "maxVocals", maxScores.MaxVocalsScore);
        AddNullableInt(command, "maxProLead", maxScores.MaxProLeadScore);
        AddNullableInt(command, "maxProBass", maxScores.MaxProBassScore);
        AddNullableInt(
            command,
            "maxProCymbals",
            maxScores.MaxProCymbalsScore);
        AddNullableInt(command, "maxProDrums", maxScores.MaxProDrumsScore);
        command.Parameters.AddWithValue(
            "source",
            PublicationPathArtifactSchema.ScrapePassStagingSource);
        command.Parameters.AddWithValue("now", now);
    }

    private static void AddNullableInt(
        NpgsqlCommand command,
        string name,
        int? value)
        => command.Parameters.Add(name, NpgsqlDbType.Integer).Value =
            (object?)value ?? DBNull.Value;

    /// <summary>
    /// Reads the staged promotion metadata of a publication, ordered by song.
    /// Deferred and restarted commits reconstruct promotion inputs from this
    /// durable state only.
    /// </summary>
    public IReadOnlyList<PublicationPathPromotionRow>
        GetPublicationPathPromotions(long publicationId)
    {
        var result = new List<PublicationPathPromotionRow>();
        using var conn = _ds.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT song_id,
                   path_generation_revision,
                   path_artifact_generation_id,
                   expected_live_revision,
                   expected_live_generation_id,
                   promotion_attempt_id,
                   promotion_source,
                   catalog_last_modified
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND promotion_pending
            ORDER BY song_id
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PublicationPathPromotionRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    /// <summary>
    /// Promotes every staged candidate row into live <c>songs</c> inside the
    /// publication commit transaction, immediately before the publication
    /// pointer advances. Any compare-and-swap mismatch is unexpected and
    /// nonretryable: it rolls the whole commit back so the candidate is failed
    /// and isolated instead of becoming a deferred wedge.
    /// </summary>
    private static int PromoteStagedPathArtifacts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId)
    {
        var songIds = new List<string>();
        using (var locked = conn.CreateCommand())
        {
            locked.Transaction = tx;
            locked.CommandText =
                PublicationPathArtifactSchema.LockStagedPromotionsSql;
            locked.Parameters.AddWithValue("publicationId", publicationId);
            using var reader = locked.ExecuteReader();
            while (reader.Read())
                songIds.Add(reader.GetString(0));
        }

        if (songIds.Count == 0)
            return 0;

        using (var lockSongs = conn.CreateCommand())
        {
            lockSongs.Transaction = tx;
            lockSongs.CommandText =
                PublicationPathArtifactSchema.LockPromotionTargetSongsSql;
            lockSongs.Parameters.Add(
                "songIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                songIds.ToArray();
            lockSongs.ExecuteNonQuery();
        }

        int promoted;
        using (var promote = conn.CreateCommand())
        {
            promote.Transaction = tx;
            promote.CommandText = PublicationPathArtifactSchema
                .PromoteStagedArtifactsToLiveSongsSql;
            promote.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            promoted = promote.ExecuteNonQuery();
        }

        if (promoted != songIds.Count)
        {
            throw new PublicationPathPromotionConflictException(
                publicationId,
                songIds.Count,
                promoted);
        }

        return promoted;
    }
}
