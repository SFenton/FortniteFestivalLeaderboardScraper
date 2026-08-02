using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSTService.Persistence;

public sealed record ImprovementNotificationMaintenanceManifest(
    int ManifestVersion,
    IReadOnlyList<ImprovementNotificationMaintenanceSong> Songs)
{
    public const int CurrentManifestVersion = 1;
    public const int RequiredSongCount = 4;
    public static readonly IReadOnlyList<string> RequiredSongIds =
    [
        "02c93f60-3184-4088-8ff2-bd716d18f432",
        "b79112db-b6e3-492c-a732-9fabbc2f1788",
        "be543f7f-c528-4b82-8047-2de07773519c",
        "c4817c49-9cec-48ac-a7e0-6d83f56f5df1",
    ];

    private const long MaximumManifestBytes = 256 * 1024;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public ImprovementNotificationMaintenanceManifest ValidateAndNormalize()
    {
        if (ManifestVersion != CurrentManifestVersion)
        {
            throw new ArgumentException(
                $"Notification maintenance manifestVersion must be " +
                $"{CurrentManifestVersion}.",
                nameof(ManifestVersion));
        }

        if (Songs is null || Songs.Count != RequiredSongCount)
        {
            throw new ArgumentException(
                $"Notification maintenance manifest must contain exactly " +
                $"{RequiredSongCount} songs.",
                nameof(Songs));
        }

        var normalizedSongs = Songs
            .Select(static song => song?.ValidateAndNormalize()
                ?? throw new ArgumentException(
                    "Notification maintenance manifest cannot contain a null song.",
                    nameof(Songs)))
            .ToArray();

        for (var index = 1; index < normalizedSongs.Length; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(
                normalizedSongs[index - 1].SongId,
                normalizedSongs[index].SongId);
            if (comparison >= 0)
            {
                throw new ArgumentException(
                    "Notification maintenance song IDs must be unique and " +
                    "strictly sorted with ordinal comparison.",
                    nameof(Songs));
            }
        }

        if (!normalizedSongs
                .Select(static song => song.SongId)
                .SequenceEqual(RequiredSongIds, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Notification maintenance manifest must contain the exact " +
                "approved four-song allowlist.",
                nameof(Songs));
        }

        var generationIds = normalizedSongs
            .Select(static song => song.StagedArtifactGenerationId)
            .ToHashSet(StringComparer.Ordinal);
        if (generationIds.Count != RequiredSongCount)
        {
            throw new ArgumentException(
                "Each notification maintenance song must bind a unique staged " +
                "artifact generation ID.",
                nameof(Songs));
        }

        return this with { Songs = normalizedSongs };
    }

    public static async Task<ImprovementNotificationMaintenanceManifest> LoadAsync(
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Notification maintenance manifest path cannot be blank.",
                nameof(path));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            throw new ArgumentException(
                "Notification maintenance manifest path is invalid.",
                nameof(path),
                ex);
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0)
        {
            throw new ArgumentException(
                "Notification maintenance manifest path must identify an existing file.",
                nameof(path));
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "Notification maintenance manifest path cannot be a symbolic link.",
                nameof(path));
        }

        if (!file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Notification maintenance manifest file must use the .json extension.",
                nameof(path));
        }

        if (file.Length <= 0 || file.Length > MaximumManifestBytes)
        {
            throw new ArgumentException(
                $"Notification maintenance manifest file must be between 1 and " +
                $"{MaximumManifestBytes:N0} bytes.",
                nameof(path));
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumManifestBytes)
        {
            throw new ArgumentException(
                "Notification maintenance manifest file size changed during validation.",
                nameof(path));
        }

        ImprovementNotificationMaintenanceManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<
                ImprovementNotificationMaintenanceManifest>(
                stream,
                ManifestJsonOptions,
                ct);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Notification maintenance manifest is not valid strict JSON.",
                nameof(path),
                ex);
        }

        return (manifest
                ?? throw new ArgumentException(
                    "Notification maintenance manifest cannot be JSON null.",
                    nameof(path)))
            .ValidateAndNormalize();
    }

    internal string SerializeCanonicalJson()
        => JsonSerializer.Serialize(this, ManifestJsonOptions);
}

public sealed record ImprovementNotificationMaintenanceSong(
    string SongId,
    long ExpectedCurrentPathRevision,
    string ExpectedCatalogLastModified,
    int? CurrentOldProLeadMaxScore,
    int ProposedProLeadMaxScore,
    string StagedArtifactGenerationId,
    string StagedDatFileHash,
    string? StagedChoptVersion,
    string? StagedChoptBinarySha256,
    string? StagedGenerationProfile)
{
    internal ImprovementNotificationMaintenanceSong ValidateAndNormalize()
    {
        ValidateIdentifier(SongId, nameof(SongId), 256);
        ValidateIdentifier(
            StagedArtifactGenerationId,
            nameof(StagedArtifactGenerationId),
            256);
        ValidateIdentifier(
            ExpectedCatalogLastModified,
            nameof(ExpectedCatalogLastModified),
            128);

        if (!DateTimeOffset.TryParse(
                ExpectedCatalogLastModified,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new ArgumentException(
                "Expected catalog last-modified values must be ISO-8601 timestamps.",
                nameof(ExpectedCatalogLastModified));
        }

        if (ExpectedCurrentPathRevision < 0
            || ExpectedCurrentPathRevision == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedCurrentPathRevision),
                "Expected current path revision must allow exactly one promotion.");
        }

        if (CurrentOldProLeadMaxScore is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CurrentOldProLeadMaxScore),
                "Current old Pro Lead maximum must be null or positive.");
        }

        if (ProposedProLeadMaxScore <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProposedProLeadMaxScore),
                "Proposed Pro Lead maximum must be positive.");
        }

        var normalizedDatHash = NormalizeSha256(
            StagedDatFileHash,
            nameof(StagedDatFileHash));

        var runtimeValues =
            new[]
            {
                StagedChoptVersion,
                StagedChoptBinarySha256,
                StagedGenerationProfile,
            };
        var suppliedRuntimeValues = runtimeValues.Count(
            static value => !string.IsNullOrWhiteSpace(value));
        if (suppliedRuntimeValues != 3)
        {
            throw new ArgumentException(
                "Staged runtime identity must provide CHOpt version, binary " +
                "SHA-256, and generation profile.");
        }

        ValidateIdentifier(StagedChoptVersion!, nameof(StagedChoptVersion), 256);
        ValidateIdentifier(
            StagedGenerationProfile!,
            nameof(StagedGenerationProfile),
            256);

        return this with
        {
            StagedDatFileHash = normalizedDatHash,
            StagedChoptBinarySha256 = NormalizeSha256(
                StagedChoptBinarySha256!,
                nameof(StagedChoptBinarySha256)),
        };
    }

    private static void ValidateIdentifier(
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
                $"{parameterName} must be a nonblank, trimmed value no longer " +
                $"than {maximumLength} characters and cannot contain controls.",
                parameterName);
        }
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64
            || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                $"{parameterName} must be exactly 64 hexadecimal characters.",
                parameterName);
        }

        return normalized;
    }
}
