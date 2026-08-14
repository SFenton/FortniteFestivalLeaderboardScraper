using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService.Scraping;

namespace FSTService.Persistence;

public sealed record MaxScoreMaintenanceMaxima(
    int? Lead,
    int? Bass,
    int? Drums,
    int? Vocals,
    int? ProLead,
    int? ProBass,
    int? ProCymbals,
    int? ProDrums)
{
    public static MaxScoreMaintenanceMaxima From(SongMaxScores scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        return new MaxScoreMaintenanceMaxima(
            scores.MaxLeadScore,
            scores.MaxBassScore,
            scores.MaxDrumsScore,
            scores.MaxVocalsScore,
            scores.MaxProLeadScore,
            scores.MaxProBassScore,
            scores.MaxProCymbalsScore,
            scores.MaxProDrumsScore);
    }

    public int? GetByInstrument(string instrument) => instrument switch
    {
        "Solo_Guitar" => Lead,
        "Solo_Bass" => Bass,
        "Solo_Drums" => Drums,
        "Solo_Vocals" => Vocals,
        "Solo_PeripheralGuitar" => ProLead,
        "Solo_PeripheralBass" => ProBass,
        "Solo_PeripheralCymbals" => ProCymbals,
        "Solo_PeripheralDrums" => ProDrums,
        _ => throw new ArgumentOutOfRangeException(
            nameof(instrument),
            instrument,
            "Unsupported max-score instrument."),
    };

    public SongMaxScores ToSongMaxScores() => new()
    {
        MaxLeadScore = Lead,
        MaxBassScore = Bass,
        MaxDrumsScore = Drums,
        MaxVocalsScore = Vocals,
        MaxProLeadScore = ProLead,
        MaxProBassScore = ProBass,
        MaxProCymbalsScore = ProCymbals,
        MaxProDrumsScore = ProDrums,
    };

    public MaxScoreMaintenanceMaxima Validate(string parameterName)
    {
        foreach (var instrument in MaxScoreMaintenanceManifest.AllInstruments)
        {
            if (GetByInstrument(instrument) is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"{parameterName}.{instrument} must be null or positive.");
            }
        }

        return this;
    }
}

public sealed record MaxScoreMaintenanceStageRequestSong(
    string SongId,
    MaxScoreMaintenanceMaxima? ExpectedOldMaxima = null,
    MaxScoreMaintenanceMaxima? ExpectedNewMaxima = null)
{
    internal MaxScoreMaintenanceStageRequestSong ValidateAndNormalize()
    {
        var songId = MaxScoreMaintenanceManifest.NormalizeIdentifier(
            SongId,
            nameof(SongId),
            256);
        if ((ExpectedOldMaxima is null) != (ExpectedNewMaxima is null))
        {
            throw new ArgumentException(
                "Expected old and new maxima must either both be supplied or both be omitted.",
                nameof(ExpectedOldMaxima));
        }

        ExpectedOldMaxima?.Validate(nameof(ExpectedOldMaxima));
        ExpectedNewMaxima?.Validate(nameof(ExpectedNewMaxima));
        return this with { SongId = songId };
    }
}

public sealed record MaxScoreMaintenanceStageRequest(
    int RequestVersion,
    long ExpectedPublishedScrapeId,
    IReadOnlyList<MaxScoreMaintenanceStageRequestSong> Songs,
    string? ExpectedChoptVersion = null,
    string? ExpectedChoptBinarySha256 = null,
    string? ExpectedGenerationProfile = null)
{
    public const int CurrentRequestVersion = 1;

    public MaxScoreMaintenanceStageRequest ValidateAndNormalize()
    {
        if (RequestVersion != CurrentRequestVersion)
        {
            throw new ArgumentException(
                $"requestVersion must be {CurrentRequestVersion}.",
                nameof(RequestVersion));
        }
        if (ExpectedPublishedScrapeId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedPublishedScrapeId),
                "Expected published scrape ID must be positive.");
        }
        if (Songs is null
            || Songs.Count is < 1 or > MaxScoreMaintenanceManifest.MaximumSongs)
        {
            throw new ArgumentException(
                $"Stage request must contain between 1 and {MaxScoreMaintenanceManifest.MaximumSongs} songs.",
                nameof(Songs));
        }

        var songs = Songs
            .Select(song => song?.ValidateAndNormalize()
                ?? throw new ArgumentException(
                    "Stage request cannot contain a null song.",
                    nameof(Songs)))
            .OrderBy(song => song.SongId, StringComparer.Ordinal)
            .ToArray();
        MaxScoreMaintenanceManifest.RequireStrictlySortedUniqueSongIds(
            songs.Select(song => song.SongId),
            nameof(Songs));

        return this with
        {
            Songs = songs,
            ExpectedChoptVersion =
                string.IsNullOrWhiteSpace(ExpectedChoptVersion)
                ? null
                : MaxScoreMaintenanceManifest.NormalizeIdentifier(
                    ExpectedChoptVersion!,
                    nameof(ExpectedChoptVersion),
                    256),
            ExpectedChoptBinarySha256 =
                string.IsNullOrWhiteSpace(ExpectedChoptBinarySha256)
                ? null
                : MaxScoreMaintenanceManifest.NormalizeSha256(
                    ExpectedChoptBinarySha256!,
                    nameof(ExpectedChoptBinarySha256)),
            ExpectedGenerationProfile =
                string.IsNullOrWhiteSpace(ExpectedGenerationProfile)
                ? null
                : MaxScoreMaintenanceManifest.NormalizeIdentifier(
                    ExpectedGenerationProfile!,
                    nameof(ExpectedGenerationProfile),
                    256),
        };
    }
}

public sealed record MaxScoreMaintenancePathIdentity(
    long Revision,
    string? DatFileHash,
    string? SongLastModified,
    DateTime? GeneratedAtUtc,
    string? ChoptVersion,
    string? ChoptBinarySha256,
    string? GenerationProfile,
    string? ArtifactGenerationId,
    IReadOnlyList<string> ExpectedInstruments,
    MaxScoreMaintenanceMaxima Maxima,
    bool PathGenerationPending)
{
    public static MaxScoreMaintenancePathIdentity From(
        PathGenerationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new MaxScoreMaintenancePathIdentity(
            state.Revision,
            state.DatFileHash,
            state.SongLastModified,
            state.GeneratedAtUtc,
            state.ChoptVersion,
            state.ChoptBinarySha256,
            state.GenerationProfile,
            state.ArtifactGenerationId,
            PathGenerationInstruments.NormalizeExpected(
                state.ExpectedInstruments),
            MaxScoreMaintenanceMaxima.From(state.MaxScores),
            state.PathGenerationPending);
    }

    internal MaxScoreMaintenancePathIdentity ValidateAndNormalize(
        string parameterName,
        bool requireCompleteGeneration)
    {
        if (Revision < 0 || Revision == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Path revision must allow exactly one promotion.");
        }
        var instruments = PathGenerationInstruments.NormalizeExpected(
            ExpectedInstruments
            ?? throw new ArgumentNullException(
                parameterName,
                "Expected instruments cannot be null."));
        if (instruments.Length != ExpectedInstruments.Count
            || !instruments.SequenceEqual(
                ExpectedInstruments,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Expected instruments must be unique and in canonical order.",
                parameterName);
        }

        Maxima.Validate($"{parameterName}.maxima");
        if (!requireCompleteGeneration)
            return this with { ExpectedInstruments = instruments };

        if (instruments.Length == 0)
        {
            throw new ArgumentException(
                "A staged generation must contain at least one instrument.",
                parameterName);
        }
        if (!GeneratedAtUtc.HasValue
            || GeneratedAtUtc.Value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A staged generation requires a UTC generated timestamp.",
                parameterName);
        }

        return this with
        {
            DatFileHash = NormalizeRequiredSha256(
                DatFileHash,
                $"{parameterName}.datFileHash"),
            SongLastModified = ProviderTimestampIdentity.NormalizeRequired(
                SongLastModified!,
                $"{parameterName}.songLastModified"),
            ChoptVersion = MaxScoreMaintenanceManifest.NormalizeIdentifier(
                ChoptVersion!,
                $"{parameterName}.choptVersion",
                256),
            ChoptBinarySha256 = NormalizeRequiredSha256(
                ChoptBinarySha256,
                $"{parameterName}.choptBinarySha256"),
            GenerationProfile =
                MaxScoreMaintenanceManifest.NormalizeIdentifier(
                    GenerationProfile!,
                    $"{parameterName}.generationProfile",
                    256),
            ArtifactGenerationId =
                MaxScoreMaintenanceManifest.NormalizeIdentifier(
                    ArtifactGenerationId!,
                    $"{parameterName}.artifactGenerationId",
                    256),
            ExpectedInstruments = instruments,
        };
    }

    private static string NormalizeRequiredSha256(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{parameterName} is required.",
                parameterName);
        }

        return MaxScoreMaintenanceManifest.NormalizeSha256(
            value,
            parameterName);
    }
}

public sealed record MaxScoreMaintenanceManifestSong(
    string SongId,
    string ExpectedCatalogLastModified,
    MaxScoreMaintenancePathIdentity CurrentPath,
    MaxScoreMaintenancePathIdentity StagedPath,
    IReadOnlyList<string> ChangedInstruments)
{
    internal MaxScoreMaintenanceManifestSong ValidateAndNormalize()
    {
        var songId = MaxScoreMaintenanceManifest.NormalizeIdentifier(
            SongId,
            nameof(SongId),
            256);
        var catalogLastModified =
            ProviderTimestampIdentity.NormalizeRequired(
                ExpectedCatalogLastModified,
                nameof(ExpectedCatalogLastModified));
        var current = (CurrentPath
            ?? throw new ArgumentNullException(nameof(CurrentPath)))
            .ValidateAndNormalize(nameof(CurrentPath), false);
        var staged = (StagedPath
            ?? throw new ArgumentNullException(nameof(StagedPath)))
            .ValidateAndNormalize(nameof(StagedPath), true);
        if (staged.Revision != current.Revision)
        {
            throw new ArgumentException(
                "Staged path identity must retain the current revision until promotion.",
                nameof(StagedPath));
        }
        if (!ProviderTimestampIdentity.Equivalent(
                staged.SongLastModified,
                catalogLastModified))
        {
            throw new ArgumentException(
                "Staged path timestamp must match the exact catalog timestamp.",
                nameof(StagedPath));
        }
        if (staged.PathGenerationPending)
        {
            throw new ArgumentException(
                "A staged immutable generation cannot be pending.",
                nameof(StagedPath));
        }

        var actualChanged = MaxScoreMaintenanceManifest.AllInstruments
            .Where(instrument =>
                current.Maxima.GetByInstrument(instrument)
                != staged.Maxima.GetByInstrument(instrument))
            .ToArray();
        var normalizedChanged = PathGenerationInstruments.NormalizeExpected(
            ChangedInstruments
            ?? throw new ArgumentNullException(nameof(ChangedInstruments)));
        if (normalizedChanged.Length == 0
            || !normalizedChanged.SequenceEqual(
                actualChanged,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Changed instruments must exactly identify every changed maximum in canonical order.",
                nameof(ChangedInstruments));
        }
        foreach (var instrument in normalizedChanged)
        {
            if (staged.Maxima.GetByInstrument(instrument) is not > 0)
            {
                throw new ArgumentException(
                    $"Changed maximum {instrument} must become positive.",
                    nameof(StagedPath));
            }
        }

        return this with
        {
            SongId = songId,
            ExpectedCatalogLastModified = catalogLastModified,
            CurrentPath = current,
            StagedPath = staged,
            ChangedInstruments = normalizedChanged,
        };
    }
}

public sealed record MaxScoreMaintenanceManifest(
    int ManifestVersion,
    long ExpectedPublishedScrapeId,
    long ExpectedPublicationId,
    long CatalogVersion,
    int CatalogSchemaVersion,
    string CatalogContentHash,
    int CatalogSongCount,
    DateTime CatalogSourceCapturedAtUtc,
    DateTime CreatedAtUtc,
    PathGenerationRuntimeIdentity Runtime,
    IReadOnlyList<MaxScoreMaintenanceManifestSong> Songs)
{
    public const int CurrentManifestVersion = 1;
    public const int MaximumSongs = 32;
    public const long MaximumManifestBytes = 512 * 1024;
    public static readonly IReadOnlyList<string> AllInstruments =
        PathGenerationInstruments.Definitions
            .Select(definition => definition.Instrument)
            .ToArray();

    public MaxScoreMaintenanceManifest ValidateAndNormalize()
    {
        if (ManifestVersion != CurrentManifestVersion)
        {
            throw new ArgumentException(
                $"manifestVersion must be {CurrentManifestVersion}.",
                nameof(ManifestVersion));
        }
        if (ExpectedPublishedScrapeId <= 0
            || ExpectedPublicationId <= 0
            || CatalogVersion <= 0
            || CatalogSchemaVersion != SongCatalogSnapshotBuilder.SchemaVersion
            || CatalogSongCount <= 0)
        {
            throw new ArgumentException(
                "Manifest publication and catalog identities must be positive and use the current schema.");
        }
        if (CatalogSourceCapturedAtUtc.Kind != DateTimeKind.Utc
            || CreatedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "Manifest timestamps must use UTC.");
        }
        var runtime = Runtime
            ?? throw new ArgumentNullException(nameof(Runtime));
        runtime = new PathGenerationRuntimeIdentity(
            NormalizeIdentifier(
                runtime.Version,
                $"{nameof(Runtime)}.{nameof(runtime.Version)}",
                256),
            NormalizeSha256(
                runtime.BinarySha256,
                $"{nameof(Runtime)}.{nameof(runtime.BinarySha256)}"),
            NormalizeIdentifier(
                runtime.Profile,
                $"{nameof(Runtime)}.{nameof(runtime.Profile)}",
                256));
        if (Songs is null || Songs.Count is < 1 or > MaximumSongs)
        {
            throw new ArgumentException(
                $"Manifest must contain between 1 and {MaximumSongs} songs.",
                nameof(Songs));
        }

        var songs = Songs
            .Select(song => song?.ValidateAndNormalize()
                ?? throw new ArgumentException(
                    "Manifest cannot contain a null song.",
                    nameof(Songs)))
            .ToArray();
        RequireStrictlySortedUniqueSongIds(
            songs.Select(song => song.SongId),
            nameof(Songs));
        if (songs.Select(song => song.StagedPath.ArtifactGenerationId)
            .Distinct(StringComparer.Ordinal)
            .Count() != songs.Length)
        {
            throw new ArgumentException(
                "Every staged generation ID must be unique.",
                nameof(Songs));
        }
        foreach (var song in songs)
        {
            if (!string.Equals(
                    song.StagedPath.ChoptVersion,
                    runtime.Version,
                    StringComparison.Ordinal)
                || !string.Equals(
                    song.StagedPath.ChoptBinarySha256,
                    runtime.BinarySha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    song.StagedPath.GenerationProfile,
                    runtime.Profile,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Staged runtime identity differs for {song.SongId}.",
                    nameof(Songs));
            }
        }

        return this with
        {
            CatalogContentHash = NormalizeSha256(
                CatalogContentHash,
                nameof(CatalogContentHash)),
            Runtime = runtime,
            Songs = songs,
        };
    }

    public byte[] SerializeCanonical()
        => JsonSerializer.SerializeToUtf8Bytes(
            ValidateAndNormalize(),
            MaxScoreMaintenanceJson.Canonical);

    public string ComputeDigest()
        => Convert.ToHexStringLower(
            SHA256.HashData(SerializeCanonical()));

    internal static string NormalizeIdentifier(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must be nonblank, trimmed, at most {maximumLength} characters, and contain no controls.",
                parameterName);
        }

        return value;
    }

    internal static string NormalizeSha256(
        string value,
        string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64
            || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                $"{parameterName} must contain exactly 64 hexadecimal characters.",
                parameterName);
        }

        return normalized;
    }

    internal static void RequireStrictlySortedUniqueSongIds(
        IEnumerable<string> songIds,
        string parameterName)
    {
        string? previous = null;
        foreach (var songId in songIds)
        {
            if (previous is not null
                && StringComparer.Ordinal.Compare(previous, songId) >= 0)
            {
                throw new ArgumentException(
                    "Song IDs must be unique and strictly sorted using ordinal comparison.",
                    parameterName);
            }
            previous = songId;
        }
    }
}

public sealed record MaxScoreMaintenanceRollbackSnapshot(
    int SnapshotVersion,
    DateTime CreatedAtUtc,
    string ManifestSha256,
    string PlanDigest,
    long ExpectedPublishedScrapeId,
    long ExpectedPublicationId,
    string CatalogContentHash,
    IReadOnlyList<MaxScoreMaintenanceRollbackSong> Songs)
{
    public const int CurrentSnapshotVersion = 1;

    public MaxScoreMaintenanceRollbackSnapshot ValidateAndNormalize()
    {
        if (SnapshotVersion != CurrentSnapshotVersion
            || CreatedAtUtc.Kind != DateTimeKind.Utc
            || ExpectedPublishedScrapeId <= 0
            || ExpectedPublicationId <= 0)
        {
            throw new ArgumentException(
                "Rollback snapshot identity is invalid.");
        }
        var songs = Songs
            .Select(song => song?.ValidateAndNormalize()
                ?? throw new ArgumentException(
                    "Rollback snapshot cannot contain a null song.",
                    nameof(Songs)))
            .ToArray();
        MaxScoreMaintenanceManifest.RequireStrictlySortedUniqueSongIds(
            songs.Select(song => song.SongId),
            nameof(Songs));
        return this with
        {
            ManifestSha256 = MaxScoreMaintenanceManifest.NormalizeSha256(
                ManifestSha256,
                nameof(ManifestSha256)),
            PlanDigest = MaxScoreMaintenanceManifest.NormalizeSha256(
                PlanDigest,
                nameof(PlanDigest)),
            CatalogContentHash =
                MaxScoreMaintenanceManifest.NormalizeSha256(
                    CatalogContentHash,
                    nameof(CatalogContentHash)),
            Songs = songs,
        };
    }
}

public sealed record MaxScoreMaintenanceRollbackSong(
    string SongId,
    string ExpectedCatalogLastModified,
    MaxScoreMaintenancePathIdentity Path)
{
    internal MaxScoreMaintenanceRollbackSong ValidateAndNormalize()
        => this with
        {
            SongId = MaxScoreMaintenanceManifest.NormalizeIdentifier(
                SongId,
                nameof(SongId),
                256),
            ExpectedCatalogLastModified =
                ProviderTimestampIdentity.NormalizeRequired(
                    ExpectedCatalogLastModified,
                    nameof(ExpectedCatalogLastModified)),
            Path = (Path ?? throw new ArgumentNullException(nameof(Path)))
                .ValidateAndNormalize(nameof(Path), false),
        };
}

public sealed record MaxScoreMaintenanceStageSongReport(
    string SongId,
    string Status,
    long ExpectedRevision,
    MaxScoreMaintenanceMaxima OldMaxima,
    MaxScoreMaintenanceMaxima? NewMaxima,
    IReadOnlyList<string> ChangedInstruments,
    string? StagedGenerationId,
    string? FailureStage,
    string? Detail);

public sealed record MaxScoreMaintenanceStageReport(
    int ReportVersion,
    bool Succeeded,
    long ExpectedPublishedScrapeId,
    long ExpectedPublicationId,
    string ManifestPath,
    string? ManifestSha256,
    IReadOnlyList<MaxScoreMaintenanceStageSongReport> Songs)
{
    public const int CurrentReportVersion = 1;
}

public sealed record MaxScoreMaintenancePlanCheck(
    string Name,
    bool Passed,
    string Detail);

public sealed record MaxScoreMaintenanceCandidate(
    string SubjectType,
    string SubjectKey,
    string? Instrument,
    string? SongId,
    string? ScopeKey,
    string CandidateKind,
    string Metric,
    decimal? OldNumeric,
    decimal? NewNumeric,
    int? OldRank,
    int? NewRank,
    string Lane,
    string Classification,
    bool MaintenanceInduced,
    bool BlocksMaintenance);

public sealed record MaxScoreMaintenancePlanReport(
    int ReportVersion,
    bool CanApply,
    string ManifestSha256,
    string PlanDigest,
    long ExpectedPublishedScrapeId,
    long ExpectedPublicationId,
    string CatalogContentHash,
    string PublishedScoreSourceFingerprint,
    string NotificationStateFingerprint,
    string RankHistoryFingerprint,
    IReadOnlyList<string> AffectedInstruments,
    long RoutineCandidateCount,
    IReadOnlyList<MaxScoreMaintenancePlanCheck> Checks,
    IReadOnlyList<MaxScoreMaintenanceCandidate> RoutineCandidates)
{
    public const int CurrentReportVersion = 1;
}

public enum MaxScoreMaintenancePhase
{
    None = 0,
    FreezeEstablished = 1,
    RollbackCaptured = 2,
    PathsPromoted = 3,
    DerivedStateRebuilt = 4,
    NotificationsQuarantined = 5,
    CachesStaged = 6,
    Validated = 7,
    Completed = 8,
}

public sealed record MaxScoreMaintenanceApplyReport(
    int ReportVersion,
    bool Succeeded,
    bool Resumable,
    bool PublicReadsFrozen,
    string ManifestSha256,
    string PlanDigest,
    MaxScoreMaintenancePhase Phase,
    long ExpectedPublishedScrapeId,
    long ExpectedPublicationId,
    string? RollbackSnapshotPath,
    string? RollbackSnapshotSha256,
    int PromotedSongCount,
    int RebuiltInstrumentCount,
    long QuarantinedCandidateCount,
    int VisibleDeliveryCount,
    long StagedCacheEntryCount,
    string? FailureStage,
    string? Detail)
{
    public const int CurrentReportVersion = 1;
}

internal static class MaxScoreMaintenanceJson
{
    internal static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static readonly JsonSerializerOptions Canonical = new(Strict)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    internal static readonly JsonSerializerOptions Report = new(Strict)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
