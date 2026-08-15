using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService.Scraping;

namespace FSTService.Persistence;

public static class MaxScoreMaintenanceStagePurposes
{
    public const string Discovery = "discovery";
    public const string Promotion = "promotion";

    internal static string Normalize(
        string value,
        string parameterName)
    {
        var normalized = MaxScoreMaintenanceManifest
            .NormalizeIdentifier(
                value,
                parameterName,
                32);
        if (normalized is not Discovery and not Promotion)
        {
            throw new ArgumentException(
                $"{parameterName} must be '{Discovery}' or '{Promotion}'.",
                parameterName);
        }

        return normalized;
    }
}

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

public sealed record MaxScoreMaintenanceMaximaConstraint(
    string Instrument,
    int? ExpectedValue)
{
    internal MaxScoreMaintenanceMaximaConstraint
        ValidateAndNormalize()
    {
        var instrument = MaxScoreMaintenanceManifest
            .NormalizeIdentifier(
                Instrument,
                nameof(Instrument),
                64);
        if (!MaxScoreMaintenanceManifest.AllInstruments.Contains(
                instrument,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported maximum constraint instrument {instrument}.",
                nameof(Instrument));
        }
        if (ExpectedValue is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedValue),
                "A constrained maximum must be null or positive.");
        }

        return this with { Instrument = instrument };
    }
}

public sealed record MaxScoreMaintenanceStageRequestSong(
    string SongId,
    MaxScoreMaintenanceMaxima? ExpectedOldMaxima = null,
    MaxScoreMaintenanceMaxima? ExpectedNewMaxima = null,
    IReadOnlyList<MaxScoreMaintenanceMaximaConstraint>?
        ExpectedOldConstraints = null,
    IReadOnlyList<MaxScoreMaintenanceMaximaConstraint>?
        ExpectedNewConstraints = null)
{
    internal MaxScoreMaintenanceStageRequestSong ValidateAndNormalize(
        string purpose,
        IReadOnlyList<string> expectedChangedInstruments)
    {
        var songId = MaxScoreMaintenanceManifest.NormalizeIdentifier(
            SongId,
            nameof(SongId),
            256);
        var oldConstraints = NormalizeConstraints(
            ExpectedOldConstraints,
            nameof(ExpectedOldConstraints));
        var newConstraints = NormalizeConstraints(
            ExpectedNewConstraints,
            nameof(ExpectedNewConstraints));
        if (purpose == MaxScoreMaintenanceStagePurposes.Discovery)
        {
            if (ExpectedOldMaxima is not null
                || ExpectedNewMaxima is not null)
            {
                throw new ArgumentException(
                    "Discovery requests use explicit partial constraints and must leave complete old/new maxima null.");
            }
            if (oldConstraints.Length == 0
                && newConstraints.Length == 0)
            {
                throw new ArgumentException(
                    "Discovery requests require at least one old or new maximum constraint.");
            }
        }
        else
        {
            if (oldConstraints.Length > 0
                || newConstraints.Length > 0)
            {
                throw new ArgumentException(
                    "Promotion requests use complete old/new maxima and cannot include partial constraints.");
            }
            var oldMaxima = ExpectedOldMaxima
                ?? throw new ArgumentException(
                    "Promotion requests require complete expected old maxima.",
                    nameof(ExpectedOldMaxima));
            oldMaxima.Validate(nameof(ExpectedOldMaxima));
            var newMaxima = ExpectedNewMaxima
                ?? throw new ArgumentException(
                    "Promotion requests require complete expected new maxima.",
                    nameof(ExpectedNewMaxima));
            newMaxima.Validate(nameof(ExpectedNewMaxima));
            var actualChanged = MaxScoreMaintenanceManifest.AllInstruments
                .Where(instrument =>
                    oldMaxima.GetByInstrument(instrument)
                    != newMaxima.GetByInstrument(instrument))
                .ToArray();
            if (!actualChanged.SequenceEqual(
                    expectedChangedInstruments,
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Promotion request maxima must change exactly the approved instruments.",
                    nameof(ExpectedNewMaxima));
            }
        }

        return this with
        {
            SongId = songId,
            ExpectedOldConstraints = oldConstraints,
            ExpectedNewConstraints = newConstraints,
        };
    }

    internal void ValidateOldMaxima(
        MaxScoreMaintenanceMaxima actual)
    {
        if (ExpectedOldMaxima is not null
            && ExpectedOldMaxima != actual)
        {
            throw new InvalidOperationException(
                $"Expected old maxima do not match current state for {SongId}.");
        }
        ValidateConstraints(
            actual,
            ExpectedOldConstraints
            ?? Array.Empty<MaxScoreMaintenanceMaximaConstraint>(),
            "old");
    }

    internal void ValidateNewMaxima(
        MaxScoreMaintenanceMaxima actual)
    {
        if (ExpectedNewMaxima is not null
            && ExpectedNewMaxima != actual)
        {
            throw new InvalidOperationException(
                $"Expected new maxima do not match staged generation for {SongId}.");
        }
        ValidateConstraints(
            actual,
            ExpectedNewConstraints
            ?? Array.Empty<MaxScoreMaintenanceMaximaConstraint>(),
            "new");
    }

    private static MaxScoreMaintenanceMaximaConstraint[]
        NormalizeConstraints(
            IReadOnlyList<MaxScoreMaintenanceMaximaConstraint>? constraints,
            string parameterName)
    {
        if (constraints is null)
            return [];
        var normalized = constraints
            .Select(constraint =>
                constraint?.ValidateAndNormalize()
                ?? throw new ArgumentException(
                    "Maximum constraints cannot contain null.",
                    parameterName))
            .OrderBy(
                constraint => Array.IndexOf(
                    MaxScoreMaintenanceManifest.AllInstruments
                        .ToArray(),
                    constraint.Instrument))
            .ToArray();
        if (normalized.Length != constraints.Count
            || !normalized.SequenceEqual(constraints)
            || normalized
                .Select(constraint => constraint.Instrument)
                .Distinct(StringComparer.Ordinal)
                .Count() != normalized.Length)
        {
            throw new ArgumentException(
                "Maximum constraints must be unique and in canonical instrument order.",
                parameterName);
        }

        return normalized;
    }

    private void ValidateConstraints(
        MaxScoreMaintenanceMaxima actual,
        IReadOnlyList<MaxScoreMaintenanceMaximaConstraint> constraints,
        string label)
    {
        foreach (var constraint in constraints)
        {
            if (actual.GetByInstrument(constraint.Instrument)
                != constraint.ExpectedValue)
            {
                throw new InvalidOperationException(
                    $"Expected {label} maximum constraint does not match {SongId}/{constraint.Instrument}.");
            }
        }
    }
}

public sealed record MaxScoreMaintenanceStageRequest(
    int RequestVersion,
    string Purpose,
    long ExpectedPublishedScrapeId,
    IReadOnlyList<string> ExpectedPathInstruments,
    IReadOnlyList<string> ExpectedChangedInstruments,
    IReadOnlyList<MaxScoreMaintenanceStageRequestSong> Songs,
    string ExpectedChoptVersion,
    string ExpectedChoptBinarySha256,
    string ExpectedGenerationProfile)
{
    public const int CurrentRequestVersion = 2;

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
        var purpose = MaxScoreMaintenanceStagePurposes.Normalize(
            Purpose,
            nameof(Purpose));
        var expectedPathInstruments =
            MaxScoreMaintenanceManifest.NormalizeInstrumentScope(
                ExpectedPathInstruments,
                nameof(ExpectedPathInstruments),
                requireNonEmpty: true);
        var expectedChangedInstruments =
            MaxScoreMaintenanceManifest.NormalizeInstrumentScope(
                ExpectedChangedInstruments,
                nameof(ExpectedChangedInstruments),
                requireNonEmpty: true);
        if (expectedChangedInstruments.Any(instrument =>
                !expectedPathInstruments.Contains(
                    instrument,
                    StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Every changed instrument must be in the exact staged path scope.",
                nameof(ExpectedChangedInstruments));
        }
        var expectedChoptVersion =
            MaxScoreMaintenanceManifest.NormalizeIdentifier(
                ExpectedChoptVersion,
                nameof(ExpectedChoptVersion),
                256);
        var expectedChoptBinarySha256 =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                ExpectedChoptBinarySha256,
                nameof(ExpectedChoptBinarySha256));
        var expectedGenerationProfile =
            MaxScoreMaintenanceManifest.NormalizeIdentifier(
                ExpectedGenerationProfile,
                nameof(ExpectedGenerationProfile),
                256);
        MaxScoreMaintenanceManifest.ValidatePlasticDrumsRuntimeScope(
            expectedChangedInstruments,
            expectedPathInstruments,
            new PathGenerationRuntimeIdentity(
                expectedChoptVersion,
                expectedChoptBinarySha256,
                expectedGenerationProfile));
        if (Songs is null
            || Songs.Count is < 1 or > MaxScoreMaintenanceManifest.MaximumSongs)
        {
            throw new ArgumentException(
                $"Stage request must contain between 1 and {MaxScoreMaintenanceManifest.MaximumSongs} songs.",
                nameof(Songs));
        }

        var songs = Songs
            .Select(song => song?.ValidateAndNormalize(
                    purpose,
                    expectedChangedInstruments)
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
            Purpose = purpose,
            ExpectedPathInstruments = expectedPathInstruments,
            ExpectedChangedInstruments = expectedChangedInstruments,
            Songs = songs,
            ExpectedChoptVersion = expectedChoptVersion,
            ExpectedChoptBinarySha256 = expectedChoptBinarySha256,
            ExpectedGenerationProfile = expectedGenerationProfile,
        };
    }

    public byte[] SerializeCanonical()
        => JsonSerializer.SerializeToUtf8Bytes(
            ValidateAndNormalize(),
            MaxScoreMaintenanceJson.Canonical);

    public string ComputeDigest()
        => Convert.ToHexStringLower(
            SHA256.HashData(SerializeCanonical()));
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
    bool PathGenerationPending,
    string? ArtifactTreeSha256 = null,
    int? ArtifactFileCount = null)
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

    internal static MaxScoreMaintenancePathIdentity From(
        PathGenerationState state,
        ValidatedPathGeneration validated)
    {
        ArgumentNullException.ThrowIfNull(validated);
        return From(state) with
        {
            ArtifactTreeSha256 = validated.ArtifactTreeSha256,
            ArtifactFileCount = validated.ArtifactFileCount,
        };
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
        if (PathGenerationPending)
        {
            throw new ArgumentException(
                "A complete immutable generation cannot be pending.",
                parameterName);
        }
        foreach (var instrument in instruments)
        {
            if (Maxima.GetByInstrument(instrument) is not > 0)
            {
                throw new ArgumentException(
                    $"{parameterName}.maxima must be positive for expected instrument {instrument}.",
                    parameterName);
            }
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
            ArtifactTreeSha256 =
                MaxScoreMaintenanceManifest.NormalizeSha256(
                    ArtifactTreeSha256
                    ?? throw new ArgumentException(
                        $"{parameterName}.artifactTreeSha256 is required.",
                        parameterName),
                    $"{parameterName}.artifactTreeSha256"),
            ArtifactFileCount = ArtifactFileCount
                == 1
                + instruments.Length
                * PathGenerationInstruments.Difficulties.Count
                * 2
                ? ArtifactFileCount
                : throw new ArgumentException(
                    $"{parameterName}.artifactFileCount does not match the exact immutable artifact tree.",
                    parameterName),
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

public sealed record MaxScoreMaintenancePlasticDrumsEvidence(
    int ProCymbalsAuthoredActivationWindowCount,
    int ProDrumsAuthoredActivationWindowCount,
    string SoloDrumsNoteInventorySha256,
    string ProCymbalsNoteInventorySha256,
    string ProDrumsNoteInventorySha256)
{
    internal MaxScoreMaintenancePlasticDrumsEvidence ValidateAndNormalize()
    {
        if (ProCymbalsAuthoredActivationWindowCount <= 0
            || ProDrumsAuthoredActivationWindowCount <= 0)
        {
            throw new ArgumentException(
                "Plastic-drums artifacts require non-empty authored activation windows.");
        }
        var soloDrums = MaxScoreMaintenanceManifest.NormalizeSha256(
            SoloDrumsNoteInventorySha256,
            nameof(SoloDrumsNoteInventorySha256));
        var proCymbals = MaxScoreMaintenanceManifest.NormalizeSha256(
            ProCymbalsNoteInventorySha256,
            nameof(ProCymbalsNoteInventorySha256));
        var proDrums = MaxScoreMaintenanceManifest.NormalizeSha256(
            ProDrumsNoteInventorySha256,
            nameof(ProDrumsNoteInventorySha256));
        if (string.Equals(
                soloDrums,
                proCymbals,
                StringComparison.Ordinal)
            || string.Equals(
                soloDrums,
                proDrums,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Plastic-drums note inventories must be distinct from Solo_Drums.");
        }

        return this with
        {
            SoloDrumsNoteInventorySha256 = soloDrums,
            ProCymbalsNoteInventorySha256 = proCymbals,
            ProDrumsNoteInventorySha256 = proDrums,
        };
    }
}

public sealed record MaxScoreMaintenanceManifestSong(
    string SongId,
    string ExpectedCatalogLastModified,
    MaxScoreMaintenancePathIdentity CurrentPath,
    MaxScoreMaintenancePathIdentity StagedPath,
    IReadOnlyList<string> ChangedInstruments,
    MaxScoreMaintenancePlasticDrumsEvidence? PlasticDrumsEvidence = null)
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
            .ValidateAndNormalize(nameof(CurrentPath), true);
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
        if (!ProviderTimestampIdentity.Equivalent(
                current.SongLastModified,
                catalogLastModified))
        {
            throw new ArgumentException(
                "Current rollback path timestamp must match the exact catalog timestamp.",
                nameof(CurrentPath));
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
            || normalizedChanged.Length != ChangedInstruments.Count
            || !normalizedChanged.SequenceEqual(
                ChangedInstruments,
                StringComparer.Ordinal)
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
        var changesPlasticDrums = normalizedChanged.Any(
            PathGenerationInstruments.IsPlasticDrumsInstrument);
        MaxScoreMaintenancePlasticDrumsEvidence? plasticEvidence = null;
        if (changesPlasticDrums)
        {
            if (PathGenerationProfiles.HasInvalidPlasticDrumsScores(
                    current.GenerationProfile))
            {
                throw new ArgumentException(
                    "Known-invalid plastic-drums v3 cannot be rollback/current maintenance state.",
                    nameof(CurrentPath));
            }
            if (!string.Equals(
                    staged.GenerationProfile,
                    PathGenerationProfiles.PlasticDrumsV4,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Plastic-drums maximum changes require a v4 staged generation.",
                    nameof(StagedPath));
            }
            if (staged.Maxima.ProCymbals is not > 0
                || staged.Maxima.ProDrums is not > 0
                || staged.Maxima.ProCymbals
                    < staged.Maxima.ProDrums)
            {
                throw new ArgumentException(
                    "Plastic-drums maxima must be positive and cymbal mode must be greater than or equal to no-cymbal mode.",
                    nameof(StagedPath));
            }
            plasticEvidence = (PlasticDrumsEvidence
                ?? throw new ArgumentException(
                    "Plastic-drums maximum changes require staged artifact evidence.",
                    nameof(PlasticDrumsEvidence)))
                .ValidateAndNormalize();
        }
        else if (PlasticDrumsEvidence is not null)
        {
            throw new ArgumentException(
                "Plastic-drums evidence is valid only when plastic-drums maxima change.",
                nameof(PlasticDrumsEvidence));
        }

        return this with
        {
            SongId = songId,
            ExpectedCatalogLastModified = catalogLastModified,
            CurrentPath = current,
            StagedPath = staged,
            ChangedInstruments = normalizedChanged,
            PlasticDrumsEvidence = plasticEvidence,
        };
    }
}

public sealed record MaxScoreMaintenanceScope(
    string Purpose,
    string StageRequestSha256,
    IReadOnlyList<string> ExpectedPathInstruments,
    IReadOnlyList<string> ExpectedChangedInstruments)
{
    internal MaxScoreMaintenanceScope ValidateAndNormalize()
    {
        var purpose = MaxScoreMaintenanceStagePurposes.Normalize(
            Purpose,
            nameof(Purpose));
        var pathInstruments =
            MaxScoreMaintenanceManifest.NormalizeInstrumentScope(
                ExpectedPathInstruments,
                nameof(ExpectedPathInstruments),
                requireNonEmpty: true);
        var changedInstruments =
            MaxScoreMaintenanceManifest.NormalizeInstrumentScope(
                ExpectedChangedInstruments,
                nameof(ExpectedChangedInstruments),
                requireNonEmpty: true);
        if (changedInstruments.Any(instrument =>
                !pathInstruments.Contains(
                    instrument,
                    StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Every changed instrument must be in the exact staged path scope.",
                nameof(ExpectedChangedInstruments));
        }

        return this with
        {
            Purpose = purpose,
            StageRequestSha256 =
                MaxScoreMaintenanceManifest.NormalizeSha256(
                    StageRequestSha256,
                    nameof(StageRequestSha256)),
            ExpectedPathInstruments = pathInstruments,
            ExpectedChangedInstruments = changedInstruments,
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
    MaxScoreMaintenanceScope Scope,
    PathGenerationRuntimeIdentity Runtime,
    IReadOnlyList<MaxScoreMaintenanceManifestSong> Songs)
{
    public const int CurrentManifestVersion = 2;
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
        var scope = (Scope
            ?? throw new ArgumentNullException(nameof(Scope)))
            .ValidateAndNormalize();
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
        ValidatePlasticDrumsRuntimeScope(
            scope.ExpectedChangedInstruments,
            scope.ExpectedPathInstruments,
            runtime);
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
            if (!song.ChangedInstruments.SequenceEqual(
                    scope.ExpectedChangedInstruments,
                    StringComparer.Ordinal)
                || !song.StagedPath.ExpectedInstruments.SequenceEqual(
                    scope.ExpectedPathInstruments,
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Manifest scope differs for {song.SongId}.",
                    nameof(Songs));
            }
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
            Scope = scope,
            Runtime = runtime,
            Songs = songs,
        };
    }

    public MaxScoreMaintenanceManifest RequirePromotionReady()
    {
        var normalized = ValidateAndNormalize();
        if (!string.Equals(
                normalized.Scope.Purpose,
                MaxScoreMaintenanceStagePurposes.Promotion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Discovery manifests cannot be planned, applied, resumed, or promoted.");
        }

        return normalized;
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

    internal static string[] NormalizeInstrumentScope(
        IReadOnlyList<string>? instruments,
        string parameterName,
        bool requireNonEmpty)
    {
        if (instruments is null)
            throw new ArgumentNullException(parameterName);
        var normalized = PathGenerationInstruments.NormalizeExpected(
            instruments);
        if (requireNonEmpty && normalized.Length == 0
            || normalized.Length != instruments.Count
            || !normalized.SequenceEqual(
                instruments,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must contain supported instruments exactly once in canonical order.",
                parameterName);
        }

        return normalized;
    }

    internal static void ValidatePlasticDrumsRuntimeScope(
        IReadOnlyList<string> changedInstruments,
        IReadOnlyList<string> expectedPathInstruments,
        PathGenerationRuntimeIdentity runtime)
    {
        if (!changedInstruments.Any(
                PathGenerationInstruments.IsPlasticDrumsInstrument))
        {
            return;
        }
        foreach (var required in new[]
                 {
                     "Solo_Drums",
                     "Solo_PeripheralCymbals",
                     "Solo_PeripheralDrums",
                 })
        {
            if (!expectedPathInstruments.Contains(
                    required,
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Plastic-drums maintenance requires Solo_Drums and both plastic-drums modes in the exact staged path scope.",
                    nameof(expectedPathInstruments));
            }
        }
        if (!PathGenerationProfiles.IsApprovedPlasticDrumsV4(runtime))
        {
            throw new ArgumentException(
                "Plastic-drums maintenance requires the approved CHOpt 1.16.4 v4 runtime identity.",
                nameof(runtime));
        }
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
    long CatalogVersion,
    int CatalogSchemaVersion,
    string CatalogContentHash,
    int CatalogSongCount,
    DateTime CatalogSourceCapturedAtUtc,
    IReadOnlyList<MaxScoreMaintenanceRollbackSong> Songs)
{
    public const int CurrentSnapshotVersion = 3;

    public MaxScoreMaintenanceRollbackSnapshot ValidateAndNormalize()
    {
        if (SnapshotVersion != CurrentSnapshotVersion
            || CreatedAtUtc.Kind != DateTimeKind.Utc
            || ExpectedPublishedScrapeId <= 0
            || ExpectedPublicationId <= 0
            || CatalogVersion <= 0
            || CatalogSchemaVersion <= 0
            || CatalogSongCount <= 0
            || CatalogSourceCapturedAtUtc.Kind
                != DateTimeKind.Utc)
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

    internal byte[] SerializeCanonical()
        => JsonSerializer.SerializeToUtf8Bytes(
            ValidateAndNormalize(),
            MaxScoreMaintenanceJson.Canonical);
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
                .ValidateAndNormalize(nameof(Path), true),
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
    string Purpose,
    bool Promotable,
    string StageRequestSha256,
    long ExpectedPublishedScrapeId,
    long ExpectedPublicationId,
    string ManifestPath,
    string? ManifestSha256,
    IReadOnlyList<MaxScoreMaintenanceStageSongReport> Songs)
{
    public const int CurrentReportVersion = 2;
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
    bool BlocksMaintenance,
    string? RoutineEventGroupKey = null);

public sealed record MaxScoreMaintenanceArtifactEvidence(
    string SongId,
    string CurrentGenerationId,
    string CurrentArtifactTreeSha256,
    int CurrentArtifactFileCount,
    string StagedGenerationId,
    string StagedArtifactTreeSha256,
    int StagedArtifactFileCount,
    MaxScoreMaintenancePlasticDrumsEvidence? PlasticDrumsEvidence);

public sealed record MaxScoreMaintenanceObservedScoreCheck(
    string SongId,
    string Instrument,
    int NewMaximum,
    bool SourceMapped,
    int? HighestObservedScore,
    bool Passed);

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
    string ScoreHistoryFingerprint,
    IReadOnlyList<string> AffectedInstruments,
    long RoutineCandidateCount,
    IReadOnlyList<MaxScoreMaintenancePlanCheck> Checks,
    IReadOnlyList<MaxScoreMaintenanceCandidate> RoutineCandidates,
    IReadOnlyList<MaxScoreMaintenanceArtifactEvidence> ArtifactEvidence,
    IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck> ObservedScoreChecks)
{
    public const int CurrentReportVersion = 3;
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
