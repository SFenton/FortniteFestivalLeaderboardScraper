using System.Text.Json;
using FSTService.Persistence;

namespace FSTService.Api;

public enum PublicationContentHashRequirement
{
    None,
    Md5OrSha256,
    Sha256,
}

public sealed record PublicationSurfaceContractDescriptor(
    string SurfaceName,
    IReadOnlyList<string> AllowedBindingKinds,
    string? PublicationIdProperty,
    string? ScrapeIdProperty,
    string? SourceGenerationProperty,
    bool RequiresRowCount,
    long MinimumRowCount,
    PublicationContentHashRequirement ContentHashRequirement,
    bool RequiresSourceEvidence,
    string? RequiredSourceKind = null,
    bool RequiresExactSource = false,
    string? JsonRowCountProperty = null);

public static class PublicationSurfaceContractCatalog
{
    private static readonly PublicationSurfaceContractDescriptor[] Definitions =
    [
        Surface(
            PublicationSurfaceNames.AccountNames,
            ["generation_account_name_snapshot"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            contentHashRequirement:
                PublicationContentHashRequirement.Sha256),
        Surface(
            PublicationSurfaceNames.AccountOverlays,
            ["generation_account_overlay_snapshot"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            contentHashRequirement:
                PublicationContentHashRequirement.Sha256),
        Surface(
            PublicationSurfaceNames.ApiResponseCache,
            ["generation_cache_table"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            contentHashRequirement:
                PublicationContentHashRequirement.Md5OrSha256,
            requiresSourceEvidence: true),
        Surface(
            PublicationSurfaceNames.BandRankings,
            ["published_tables"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            sourceGenerationProperty: "generation",
            requiresSourceEvidence: true),
        Surface(
            PublicationSurfaceNames.History,
            ["generation_history_snapshot"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            contentHashRequirement:
                PublicationContentHashRequirement.Sha256),
        Surface(
            PublicationSurfaceNames.ImprovementNotifications,
            ["publication_outbox"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            jsonRowCountProperty: "scopeCount"),
        Surface(
            PublicationSurfaceNames.ItemShop,
            ["generation_item_shop_snapshot"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            contentHashRequirement:
                PublicationContentHashRequirement.Sha256),
        Surface(
            PublicationSurfaceNames.PathArtifacts,
            ["generation_path_artifact_manifest"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "scrapeId",
            requiresRowCount: true,
            contentHashRequirement:
                PublicationContentHashRequirement.Sha256),
        Surface(
            PublicationSurfaceNames.SoloScopeSources,
            ["scrape_id"],
            publicationIdProperty: "publicationId",
            scrapeIdProperty: "publishedScrapeId",
            requiresRowCount: true,
            minimumRowCount: 1,
            requiresSourceEvidence: true),
        Surface(
            PublicationSurfaceNames.SongCatalog,
            ["generation_catalog_snapshot"],
            publicationIdProperty: "publicationId",
            requiresRowCount: true,
            minimumRowCount: 1,
            contentHashRequirement:
                PublicationContentHashRequirement.Sha256,
            requiresSourceEvidence: true,
            requiredSourceKind: "provider_exact",
            requiresExactSource: true),
    ];

    private static readonly IReadOnlyDictionary<string, PublicationSurfaceContractDescriptor>
        ByName = BuildIndex(Definitions);

    public static IReadOnlyList<PublicationSurfaceContractDescriptor> Surfaces { get; } =
        Array.AsReadOnly(Definitions);

    public static IReadOnlySet<string> KnownSurfaceNames { get; } =
        new HashSet<string>(
            Definitions.Select(static descriptor => descriptor.SurfaceName),
            StringComparer.Ordinal);

    public static IReadOnlyList<string> PinningInfrastructureSurfaceNames { get; } =
        [PublicationSurfaceNames.ApiResponseCache];

    internal static PublicationSurfaceContractDescriptor Get(string surfaceName)
        => ByName.TryGetValue(surfaceName, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException(
                $"Unknown publication surface contract {surfaceName}.");

    private static PublicationSurfaceContractDescriptor Surface(
        string surfaceName,
        string[] allowedBindingKinds,
        string? publicationIdProperty = null,
        string? scrapeIdProperty = null,
        string? sourceGenerationProperty = null,
        bool requiresRowCount = false,
        long minimumRowCount = 0,
        PublicationContentHashRequirement contentHashRequirement =
            PublicationContentHashRequirement.None,
        bool requiresSourceEvidence = false,
        string? requiredSourceKind = null,
        bool requiresExactSource = false,
        string? jsonRowCountProperty = null)
        => new(
            surfaceName,
            Array.AsReadOnly(allowedBindingKinds),
            publicationIdProperty,
            scrapeIdProperty,
            sourceGenerationProperty,
            requiresRowCount,
            minimumRowCount,
            contentHashRequirement,
            requiresSourceEvidence,
            requiredSourceKind,
            requiresExactSource,
            jsonRowCountProperty);

    private static IReadOnlyDictionary<string, PublicationSurfaceContractDescriptor>
        BuildIndex(IEnumerable<PublicationSurfaceContractDescriptor> descriptors)
    {
        var result =
            new Dictionary<string, PublicationSurfaceContractDescriptor>(
                StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!result.TryAdd(descriptor.SurfaceName, descriptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate publication surface contract {descriptor.SurfaceName}.");
            }
        }

        return result;
    }
}

public sealed record PublicationUnreadySurface(
    string Surface,
    IReadOnlyList<string> Reasons);

public sealed record PublicationReadinessResult(
    int ContractVersion,
    long PublicationId,
    long PublishedScrapeId,
    IReadOnlyList<PublicationUnreadySurface> UnreadySurfaces)
{
    public bool ReadyForPinning => UnreadySurfaces.Count == 0;
}

public sealed record PublicationBootstrapResponse(
    int ContractVersion,
    long PublicationId,
    long? PreviousPublicationId,
    long PublishedScrapeId,
    DateTime? PublishedAt,
    bool ReadyForPinning,
    bool PinningEnabled,
    IReadOnlyList<PublicationUnreadySurface> UnreadySurfaces);

public sealed class PublicationReadinessEvaluator
{
    public const string GenerationSurface = "publication_generation";
    public const string EvaluatorSurface = "publication_readiness";

    private readonly IMetaDatabase _metaDb;

    public PublicationReadinessEvaluator(IMetaDatabase metaDb)
    {
        _metaDb = metaDb;
    }

    public PublicationReadinessResult Evaluate(
        long publicationId,
        long publishedScrapeId)
    {
        var failures =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        PublicationGenerationInfo? generation;
        IReadOnlyList<PublicationSurfaceBinding> bindings;

        try
        {
            generation = _metaDb.GetPublicationGeneration(publicationId);
            bindings = _metaDb.GetPublicationSurfaceBindings(publicationId);
        }
        catch (Exception ex)
        {
            AddFailure(
                failures,
                EvaluatorSurface,
                $"evaluation_failed:{ex.GetType().Name}");
            return BuildResult(
                publicationId,
                publishedScrapeId,
                failures);
        }

        ValidateGeneration(
            publicationId,
            publishedScrapeId,
            generation,
            failures);

        var requiredSurfaceNames =
            PublicationRouteSurfaceContractCatalog.RequiredSurfaceNames
                .Concat(
                    PublicationSurfaceContractCatalog
                        .PinningInfrastructureSurfaceNames)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

        foreach (var surfaceName in requiredSurfaceNames)
        {
            var matchingBindings = bindings
                .Where(binding => string.Equals(
                    binding.SurfaceName,
                    surfaceName,
                    StringComparison.Ordinal))
                .ToArray();
            if (matchingBindings.Length == 0)
            {
                AddFailure(failures, surfaceName, "missing_binding");
                continue;
            }

            if (matchingBindings.Length != 1)
            {
                AddFailure(
                    failures,
                    surfaceName,
                    $"duplicate_bindings:{matchingBindings.Length}");
            }

            ValidateBinding(
                publicationId,
                publishedScrapeId,
                PublicationSurfaceContractCatalog.Get(surfaceName),
                matchingBindings[0],
                failures);
        }

        return BuildResult(
            publicationId,
            publishedScrapeId,
            failures);
    }

    private void ValidateBinding(
        long publicationId,
        long publishedScrapeId,
        PublicationSurfaceContractDescriptor descriptor,
        PublicationSurfaceBinding binding,
        Dictionary<string, List<string>> failures)
    {
        var reasons = GetReasons(failures, descriptor.SurfaceName);
        if (binding.PublicationId != publicationId)
            reasons.Add("binding_publication_id_mismatch");
        if (!string.Equals(
                binding.Status,
                PublicationGenerationStatus.Ready,
                StringComparison.Ordinal))
        {
            reasons.Add($"status_{NormalizeReason(binding.Status)}");
        }
        if (!descriptor.AllowedBindingKinds.Contains(
                binding.BindingKind,
                StringComparer.Ordinal))
        {
            reasons.Add(
                $"binding_kind_not_allowed:{NormalizeReason(binding.BindingKind)}");
        }
        if (binding.BuiltAtUtc == default)
            reasons.Add("built_at_missing");

        JsonDocument? document = null;
        var bindingJsonValid = false;
        try
        {
            document = JsonDocument.Parse(binding.BindingJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reasons.Add("binding_json_not_object");
            }
            else
            {
                bindingJsonValid = true;
                ValidateBindingJson(
                    publicationId,
                    publishedScrapeId,
                    descriptor,
                    binding,
                    document.RootElement,
                    reasons);
            }
        }
        catch (JsonException)
        {
            reasons.Add("binding_json_invalid");
        }
        finally
        {
            document?.Dispose();
        }

        ValidateContentMetadata(descriptor, binding, reasons);

        if (!descriptor.RequiresSourceEvidence || !bindingJsonValid)
            return;

        try
        {
            var evidence = _metaDb.GetPublicationSurfaceSourceEvidence(
                publicationId,
                descriptor.SurfaceName);
            ValidateSourceEvidence(
                publicationId,
                publishedScrapeId,
                descriptor,
                binding,
                evidence,
                reasons);
        }
        catch (Exception ex)
        {
            reasons.Add(
                $"source_validation_failed:{ex.GetType().Name}");
        }
    }

    private static void ValidateBindingJson(
        long publicationId,
        long publishedScrapeId,
        PublicationSurfaceContractDescriptor descriptor,
        PublicationSurfaceBinding binding,
        JsonElement bindingJson,
        List<string> reasons)
    {
        if (!TryGetInt64(
                bindingJson,
                "contractVersion",
                out var contractVersion))
        {
            reasons.Add("contract_version_missing");
        }
        else if (contractVersion !=
                 PublicationRouteSurfaceContractCatalog.ContractVersion)
        {
            reasons.Add(
                $"contract_version_mismatch:{contractVersion}");
        }

        ValidateIdentityProperty(
            bindingJson,
            descriptor.PublicationIdProperty,
            publicationId,
            "source_publication_id",
            reasons);
        ValidateIdentityProperty(
            bindingJson,
            descriptor.ScrapeIdProperty,
            publishedScrapeId,
            "source_scrape_id",
            reasons);

        if (descriptor.SourceGenerationProperty is not null
            && (!TryGetInt64(
                    bindingJson,
                    descriptor.SourceGenerationProperty,
                    out var sourceGeneration)
                || sourceGeneration <= 0))
        {
            reasons.Add("source_generation_missing");
        }

        if (descriptor.RequiredSourceKind is not null)
        {
            if (!bindingJson.TryGetProperty(
                    "sourceKind",
                    out var sourceKind)
                || sourceKind.ValueKind != JsonValueKind.String)
            {
                reasons.Add("source_kind_missing");
            }
            else if (!string.Equals(
                         sourceKind.GetString(),
                         descriptor.RequiredSourceKind,
                         StringComparison.Ordinal))
            {
                reasons.Add(
                    $"source_kind_not_allowed:{NormalizeReason(sourceKind.GetString())}");
            }
        }

        if (descriptor.RequiresExactSource
            && (!bindingJson.TryGetProperty("isExact", out var isExact)
                || isExact.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False)
                || !isExact.GetBoolean()))
        {
            reasons.Add("source_not_exact");
        }

        if (descriptor.JsonRowCountProperty is not null)
        {
            if (!TryGetInt64(
                    bindingJson,
                    descriptor.JsonRowCountProperty,
                    out var jsonRowCount))
            {
                reasons.Add("binding_json_row_count_missing");
            }
            else if (!binding.RowCount.HasValue
                     || binding.RowCount.Value != jsonRowCount)
            {
                reasons.Add("binding_json_row_count_mismatch");
            }
        }
    }

    private static void ValidateContentMetadata(
        PublicationSurfaceContractDescriptor descriptor,
        PublicationSurfaceBinding binding,
        List<string> reasons)
    {
        if (descriptor.RequiresRowCount)
        {
            if (!binding.RowCount.HasValue)
            {
                reasons.Add("row_count_missing");
            }
            else if (binding.RowCount.Value < descriptor.MinimumRowCount)
            {
                reasons.Add(
                    descriptor.MinimumRowCount > 0
                        ? "row_count_below_minimum"
                        : "row_count_negative");
            }
        }

        if (descriptor.ContentHashRequirement ==
            PublicationContentHashRequirement.None)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(binding.ContentHash))
        {
            reasons.Add("content_hash_missing");
            return;
        }

        var requiredLengths = descriptor.ContentHashRequirement switch
        {
            PublicationContentHashRequirement.Md5OrSha256 => new[] { 32, 64 },
            PublicationContentHashRequirement.Sha256 => new[] { 64 },
            _ => [],
        };
        if (!requiredLengths.Contains(binding.ContentHash.Length)
            || binding.ContentHash.Any(static character =>
                !Uri.IsHexDigit(character)))
        {
            reasons.Add("content_hash_invalid");
        }
    }

    private static void ValidateSourceEvidence(
        long publicationId,
        long publishedScrapeId,
        PublicationSurfaceContractDescriptor descriptor,
        PublicationSurfaceBinding binding,
        PublicationSurfaceSourceEvidence? evidence,
        List<string> reasons)
    {
        if (evidence is null || !evidence.Exists)
        {
            reasons.Add("source_missing");
            return;
        }

        if (evidence.PublicationId != publicationId)
            reasons.Add("source_evidence_publication_id_mismatch");
        if (evidence.ScrapeId != publishedScrapeId)
            reasons.Add("source_evidence_scrape_id_mismatch");
        if (binding.RowCount.HasValue
            && evidence.RowCount.HasValue
            && binding.RowCount.Value != evidence.RowCount.Value)
        {
            reasons.Add("source_row_count_mismatch");
        }
        if (!string.IsNullOrWhiteSpace(binding.ContentHash)
            && !string.IsNullOrWhiteSpace(evidence.ContentHash)
            && !string.Equals(
                binding.ContentHash,
                evidence.ContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("source_content_hash_mismatch");
        }

        if (descriptor.SourceGenerationProperty is null)
            return;

        using var document = JsonDocument.Parse(binding.BindingJson);
        if (!TryGetInt64(
                document.RootElement,
                descriptor.SourceGenerationProperty,
                out var bindingGeneration))
        {
            return;
        }
        if (evidence.SourceGeneration != bindingGeneration)
            reasons.Add("source_generation_mismatch");
    }

    private static void ValidateGeneration(
        long publicationId,
        long publishedScrapeId,
        PublicationGenerationInfo? generation,
        Dictionary<string, List<string>> failures)
    {
        if (generation is null)
        {
            AddFailure(failures, GenerationSurface, "generation_missing");
            return;
        }

        if (generation.PublicationId != publicationId)
        {
            AddFailure(
                failures,
                GenerationSurface,
                "generation_publication_id_mismatch");
        }
        if (generation.ScrapeId != publishedScrapeId)
        {
            AddFailure(
                failures,
                GenerationSurface,
                "generation_scrape_id_mismatch");
        }
        if (!string.Equals(
                generation.Status,
                PublicationGenerationStatus.Current,
                StringComparison.Ordinal))
        {
            AddFailure(
                failures,
                GenerationSurface,
                $"generation_status_{NormalizeReason(generation.Status)}");
        }
        if (!generation.SourceCutAtUtc.HasValue)
            AddFailure(failures, GenerationSurface, "source_cut_at_missing");
        if (!generation.ReadyAtUtc.HasValue)
            AddFailure(failures, GenerationSurface, "ready_at_missing");
        if (!generation.PublishedAtUtc.HasValue)
            AddFailure(failures, GenerationSurface, "published_at_missing");
    }

    private static void ValidateIdentityProperty(
        JsonElement bindingJson,
        string? propertyName,
        long expectedValue,
        string reasonPrefix,
        List<string> reasons)
    {
        if (propertyName is null)
            return;

        if (!TryGetInt64(bindingJson, propertyName, out var value))
        {
            reasons.Add($"{reasonPrefix}_missing");
        }
        else if (value != expectedValue)
        {
            reasons.Add($"{reasonPrefix}_mismatch");
        }
    }

    private static bool TryGetInt64(
        JsonElement json,
        string propertyName,
        out long value)
    {
        value = 0;
        return json.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    private static PublicationReadinessResult BuildResult(
        long publicationId,
        long publishedScrapeId,
        Dictionary<string, List<string>> failures)
        => new(
            PublicationRouteSurfaceContractCatalog.ContractVersion,
            publicationId,
            publishedScrapeId,
            failures
                .Where(static failure => failure.Value.Count > 0)
                .OrderBy(static failure => failure.Key, StringComparer.Ordinal)
                .Select(static failure => new PublicationUnreadySurface(
                    failure.Key,
                    failure.Value
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray()))
                .ToArray());

    private static List<string> GetReasons(
        Dictionary<string, List<string>> failures,
        string surfaceName)
    {
        if (!failures.TryGetValue(surfaceName, out var reasons))
        {
            reasons = [];
            failures[surfaceName] = reasons;
        }

        return reasons;
    }

    private static void AddFailure(
        Dictionary<string, List<string>> failures,
        string surfaceName,
        string reason)
        => GetReasons(failures, surfaceName).Add(reason);

    private static string NormalizeReason(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "missing"
            : value.Trim().ToLowerInvariant().Replace(' ', '_');
}

internal static class PublicationReadinessHttpResults
{
    public static IResult Unavailable(PublicationReadinessResult readiness)
        => Results.Problem(
            title: "Published data unavailable",
            detail:
                "The current publication generation is not ready for pinned reads.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                ["contractVersion"] = readiness.ContractVersion,
                ["publicationId"] = readiness.PublicationId,
                ["unreadySurfaces"] = readiness.UnreadySurfaces,
            });
}
