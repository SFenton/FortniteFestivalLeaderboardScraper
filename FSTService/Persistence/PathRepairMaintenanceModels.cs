using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSTService.Persistence;

public sealed record PathRepairMaxScores(
    int? Lead,
    int? Bass,
    int? Drums,
    int? Vocals,
    int? ProLead,
    int? ProBass);

public sealed record PathRepairStageSongReport(
    string SongId,
    string Status,
    long ExpectedRevision,
    string ExpectedCatalogLastModified,
    int? OldProLeadMaxScore,
    int? ProposedProLeadMaxScore,
    string? StagedGenerationId,
    string? StagedDatFileHash,
    string? FailureStage,
    string? Detail);

public sealed record PathRepairStageReport(
    string Command,
    bool Succeeded,
    bool ManifestWritten,
    string ManifestPath,
    string? ManifestSha256,
    IReadOnlyList<PathRepairStageSongReport> Songs);

public sealed record PathRepairRollbackSong(
    string SongId,
    long Revision,
    string ExpectedCatalogLastModified,
    string? DatFileHash,
    string? SongLastModified,
    DateTime? PathsGeneratedAtUtc,
    string? ChoptVersion,
    string? ChoptBinarySha256,
    string? GenerationProfile,
    string? ArtifactGenerationId,
    IReadOnlyList<string> ExpectedInstruments,
    PathRepairMaxScores MaxScores,
    bool PathGenerationPending);

public sealed record PathRepairRollbackSnapshot(
    int SnapshotVersion,
    DateTime CreatedAtUtc,
    long ExpectedPublishedScrapeId,
    string ManifestSha256,
    IReadOnlyList<PathRepairRollbackSong> Songs)
{
    public const int CurrentSnapshotVersion = 1;
}

public sealed record PathRepairPromotionSongReport(
    string SongId,
    string Status,
    long ExpectedRevision,
    long? ResultingRevision,
    string StagedGenerationId,
    string? Detail);

public sealed record PathRepairPromotionReport(
    string Command,
    bool Succeeded,
    bool PartialPromotion,
    long ExpectedPublishedScrapeId,
    string ManifestSha256,
    string RollbackSnapshotPath,
    string RollbackSnapshotSha256,
    bool PublicReadsFrozen,
    int PromotedCount,
    IReadOnlyList<PathRepairPromotionSongReport> Songs);

public sealed record PathRepairRankingRebuildReport(
    string Command,
    bool Succeeded,
    long ExpectedPublishedScrapeId,
    long PublicationId,
    string CatalogContentHash,
    int CatalogSongCount,
    bool PublicReadsFrozenDuringRebuild,
    bool PublicReadsRestored,
    string? Detail);

public sealed record PathRepairCommandFailureReport(
    string Command,
    bool Succeeded,
    string ErrorType,
    string Detail);

internal static class PathRepairJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
}
