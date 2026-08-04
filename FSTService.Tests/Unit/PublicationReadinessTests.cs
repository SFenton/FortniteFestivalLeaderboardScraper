using System.Text.Json;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PublicationReadinessTests
{
    private const long PublicationId = 42;
    private const long ScrapeId = 1277;
    private const long BandGeneration = 9;

    [Fact]
    public void ReadyExactBindingsPassAllContractRules()
    {
        var bindings = CreateReadyBindings();
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        Assert.True(result.ReadyForPinning);
        Assert.Empty(result.UnreadySurfaces);
        Assert.Equal(
            PublicationRouteSurfaceContractCatalog.ContractVersion,
            result.ContractVersion);
    }

    [Fact]
    public void MissingBindingFailsClosed()
    {
        var bindings = CreateReadyBindings()
            .Where(static binding =>
                binding.SurfaceName != PublicationSurfaceNames.AccountNames)
            .ToArray();
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.AccountNames,
            "missing_binding");
    }

    [Theory]
    [InlineData(PublicationGenerationStatus.Building, "status_building")]
    [InlineData(PublicationGenerationStatus.Failed, "status_failed")]
    public void NonReadyBindingStatusFailsClosed(
        string status,
        string expectedReason)
    {
        var bindings = ReplaceBinding(
            CreateReadyBindings(),
            PublicationSurfaceNames.History,
            binding => binding with { Status = status });
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.History,
            expectedReason);
    }

    [Theory]
    [InlineData(PublicationSurfaceNames.ItemShop)]
    [InlineData(PublicationSurfaceNames.PathArtifacts)]
    public void LegacyLiveUnversionedBindingsNeverBecomeReady(
        string surfaceName)
    {
        var bindings = ReplaceBinding(
            CreateReadyBindings(),
            surfaceName,
            binding => binding with
            {
                BindingKind = "legacy_live_unversioned",
                BindingJson = JsonSerializer.Serialize(new
                {
                    table = "legacy",
                }),
                ContentHash = null,
                Status = PublicationGenerationStatus.Building,
            });
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(result, surfaceName, "status_building");
        AssertReason(
            result,
            surfaceName,
            "binding_kind_not_allowed:legacy_live_unversioned");
    }

    [Fact]
    public void MissingCountAndHashFailWhenBindingTypePromisesThem()
    {
        var bindings = ReplaceBinding(
            CreateReadyBindings(),
            PublicationSurfaceNames.AccountNames,
            binding => binding with
            {
                RowCount = null,
                ContentHash = null,
            });
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.AccountNames,
            "row_count_missing");
        AssertReason(
            result,
            PublicationSurfaceNames.AccountNames,
            "content_hash_missing");
    }

    [Fact]
    public void InvalidHashFailsClosed()
    {
        var bindings = ReplaceBinding(
            CreateReadyBindings(),
            PublicationSurfaceNames.PathArtifacts,
            binding => binding with { ContentHash = "not-a-hash" });
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.PathArtifacts,
            "content_hash_invalid");
    }

    [Fact]
    public void SourceIdentityAndContractVersionMustMatch()
    {
        var descriptor = PublicationSurfaceContractCatalog.Get(
            PublicationSurfaceNames.AccountOverlays);
        var invalid = CreateReadyBinding(
            descriptor,
            sourcePublicationId: PublicationId + 1,
            sourceScrapeId: ScrapeId + 1,
            contractVersion:
                PublicationRouteSurfaceContractCatalog.ContractVersion + 1);
        var bindings = ReplaceBinding(
            CreateReadyBindings(),
            PublicationSurfaceNames.AccountOverlays,
            _ => invalid);
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.AccountOverlays,
            "contract_version_mismatch:2");
        AssertReason(
            result,
            PublicationSurfaceNames.AccountOverlays,
            "source_publication_id_mismatch");
        AssertReason(
            result,
            PublicationSurfaceNames.AccountOverlays,
            "source_scrape_id_mismatch");
    }

    [Fact]
    public void MissingRetainedSourceFailsClosed()
    {
        var bindings = CreateReadyBindings();
        var evaluator = CreateEvaluator(
            bindings,
            evidenceOverrides: new Dictionary<
                string,
                PublicationSurfaceSourceEvidence?>
            {
                [PublicationSurfaceNames.SongCatalog] = null,
            });

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.SongCatalog,
            "source_missing");
    }

    [Fact]
    public void MissingSourceIsReportedAlongsideBindingMetadataFailures()
    {
        var bindings = ReplaceBinding(
            CreateReadyBindings(),
            PublicationSurfaceNames.SongCatalog,
            binding => binding with { ContentHash = null });
        var evaluator = CreateEvaluator(
            bindings,
            evidenceOverrides: new Dictionary<
                string,
                PublicationSurfaceSourceEvidence?>
            {
                [PublicationSurfaceNames.SongCatalog] = null,
            });

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.SongCatalog,
            "content_hash_missing");
        AssertReason(
            result,
            PublicationSurfaceNames.SongCatalog,
            "source_missing");
    }

    [Fact]
    public void SourceCountAndHashMustMatchBinding()
    {
        var bindings = CreateReadyBindings();
        var cache = bindings.Single(static binding =>
            binding.SurfaceName ==
            PublicationSurfaceNames.ApiResponseCache);
        var evaluator = CreateEvaluator(
            bindings,
            evidenceOverrides: new Dictionary<
                string,
                PublicationSurfaceSourceEvidence?>
            {
                [PublicationSurfaceNames.ApiResponseCache] =
                    new PublicationSurfaceSourceEvidence(
                        PublicationSurfaceNames.ApiResponseCache,
                        Exists: true,
                        PublicationId,
                        ScrapeId,
                        cache.RowCount + 1,
                        new string('b', 32)),
            });

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationSurfaceNames.ApiResponseCache,
            "source_row_count_mismatch");
        AssertReason(
            result,
            PublicationSurfaceNames.ApiResponseCache,
            "source_content_hash_mismatch");
    }

    [Fact]
    public void DuplicateBindingFailsClosed()
    {
        var bindings = CreateReadyBindings().ToList();
        bindings.Add(bindings[0]);
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            bindings[0].SurfaceName,
            "duplicate_bindings:2");
    }

    [Fact]
    public void UnreadySurfacesAndReasonsAreDeterministic()
    {
        var bindings = CreateReadyBindings()
            .Where(static binding =>
                binding.SurfaceName is not (
                    PublicationSurfaceNames.AccountNames
                    or PublicationSurfaceNames.History))
            .Reverse()
            .ToArray();
        var evaluator = CreateEvaluator(bindings);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        Assert.Equal(
            result.UnreadySurfaces
                .OrderBy(static surface => surface.Surface, StringComparer.Ordinal)
                .Select(static surface => surface.Surface),
            result.UnreadySurfaces.Select(static surface => surface.Surface));
        Assert.All(
            result.UnreadySurfaces,
            static surface => Assert.Equal(
                surface.Reasons.Order(StringComparer.Ordinal),
                surface.Reasons));
    }

    [Fact]
    public void GenerationMustExistAndMatchCurrentPublishedScrape()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationGeneration(PublicationId)
            .Returns(new PublicationGenerationInfo(
                PublicationId,
                ScrapeId + 1,
                PublicationGenerationStatus.Failed,
                PreviousPublicationId: null,
                CreatedAtUtc: DateTime.UtcNow.AddMinutes(-5),
                SourceCutAtUtc: null,
                ReadyAtUtc: null,
                PublishedAtUtc: null,
                FailedAtUtc: DateTime.UtcNow,
                FailurePhase: "publication",
                FailureMessage: "test"));
        metaDb.GetPublicationSurfaceBindings(PublicationId)
            .Returns(CreateReadyBindings());
        var evaluator = new PublicationReadinessEvaluator(metaDb);

        var result = evaluator.Evaluate(PublicationId, ScrapeId);

        AssertReason(
            result,
            PublicationReadinessEvaluator.GenerationSurface,
            "generation_scrape_id_mismatch");
        AssertReason(
            result,
            PublicationReadinessEvaluator.GenerationSurface,
            "generation_status_failed");
        AssertReason(
            result,
            PublicationReadinessEvaluator.GenerationSurface,
            "source_cut_at_missing");
    }

    [Fact]
    public void EffectivePinningRequiresConfigurationAndReadiness()
    {
        using var fixture = new InMemoryMetaDatabase();
        var bindings = CreateReadyBindings();
        var metaDb = CreateMetaDatabase(bindings);
        var configured = new PublicationReadContextService(
            metaDb,
            fixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnablePublicationReadContext = true,
            }));
        var disabled = new PublicationReadContextService(
            metaDb,
            fixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnablePublicationReadContext = false,
            }));

        Assert.True(configured.PinningConfigured);
        Assert.True(configured.PinningEnabled);
        Assert.False(disabled.PinningConfigured);
        Assert.False(disabled.PinningEnabled);

        var unreadyBindings = ReplaceBinding(
            bindings,
            PublicationSurfaceNames.ItemShop,
            binding => binding with
            {
                BindingKind = "legacy_live_unversioned",
                Status = PublicationGenerationStatus.Building,
            });
        var unready = new PublicationReadContextService(
            CreateMetaDatabase(unreadyBindings),
            fixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnablePublicationReadContext = true,
            }));

        Assert.True(unready.PinningConfigured);
        Assert.False(unready.PinningEnabled);
    }

    [Fact]
    public void PinningEnabledReturnsFalseWhenConfiguredPointersAreMissing()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState()
            .Returns(new PublicationPointerState(
                CurrentPublicationId: null,
                PreviousPublicationId: null,
                WorkingPublicationId: null,
                PublishedScrapeId: null,
                PublishedAtUtc: null));
        var service = new PublicationReadContextService(
            metaDb,
            fixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnablePublicationReadContext = true,
            }));

        Assert.False(service.PinningEnabled);
        metaDb.DidNotReceive()
            .GetPublicationGeneration(Arg.Any<long>());
    }

    [Fact]
    public void PinningEnabledPropagatesPublicationPointerReadFailures()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState()
            .Returns(_ => throw new InvalidOperationException(
                "pointer read failed"));
        var service = new PublicationReadContextService(
            metaDb,
            fixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnablePublicationReadContext = true,
            }));

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = service.PinningEnabled;
        });

        Assert.Equal("pointer read failed", error.Message);
    }

    private static PublicationReadinessEvaluator CreateEvaluator(
        IReadOnlyList<PublicationSurfaceBinding> bindings,
        IReadOnlyDictionary<
            string,
            PublicationSurfaceSourceEvidence?>? evidenceOverrides = null)
        => new(CreateMetaDatabase(bindings, evidenceOverrides));

    private static IMetaDatabase CreateMetaDatabase(
        IReadOnlyList<PublicationSurfaceBinding> bindings,
        IReadOnlyDictionary<
            string,
            PublicationSurfaceSourceEvidence?>? evidenceOverrides = null)
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationGeneration(PublicationId)
            .Returns(CreateGeneration());
        metaDb.GetPublicationPointerState()
            .Returns(new PublicationPointerState(
                PublicationId,
                PublicationId - 1,
                WorkingPublicationId: null,
                ScrapeId,
                CreateGeneration().PublishedAtUtc));
        metaDb.GetPublicationSurfaceBindings(PublicationId)
            .Returns(bindings);

        var bindingBySurface = bindings
            .GroupBy(static binding => binding.SurfaceName)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        metaDb.GetPublicationSurfaceSourceEvidence(
                PublicationId,
                Arg.Any<string>())
            .Returns(call =>
            {
                var surfaceName = call.ArgAt<string>(1);
                if (evidenceOverrides is not null
                    && evidenceOverrides.TryGetValue(
                        surfaceName,
                        out var overridden))
                {
                    return overridden;
                }

                if (!bindingBySurface.TryGetValue(
                        surfaceName,
                        out var binding))
                {
                    return null;
                }

                return new PublicationSurfaceSourceEvidence(
                    surfaceName,
                    Exists: true,
                    PublicationId,
                    ScrapeId,
                    binding.RowCount,
                    binding.ContentHash,
                    surfaceName == PublicationSurfaceNames.BandRankings
                        ? BandGeneration
                        : null);
            });
        return metaDb;
    }

    private static PublicationGenerationInfo CreateGeneration()
    {
        var now = DateTime.UtcNow;
        return new PublicationGenerationInfo(
            PublicationId,
            ScrapeId,
            PublicationGenerationStatus.Current,
            PreviousPublicationId: PublicationId - 1,
            CreatedAtUtc: now.AddMinutes(-5),
            SourceCutAtUtc: now.AddMinutes(-4),
            ReadyAtUtc: now.AddMinutes(-2),
            PublishedAtUtc: now.AddMinutes(-1),
            FailedAtUtc: null,
            FailurePhase: null,
            FailureMessage: null);
    }

    private static PublicationSurfaceBinding[] CreateReadyBindings()
        => PublicationSurfaceContractCatalog.Surfaces
            .Select(static descriptor => CreateReadyBinding(descriptor))
            .OrderBy(static binding => binding.SurfaceName, StringComparer.Ordinal)
            .ToArray();

    private static PublicationSurfaceBinding CreateReadyBinding(
        PublicationSurfaceContractDescriptor descriptor,
        long sourcePublicationId = PublicationId,
        long sourceScrapeId = ScrapeId,
        int contractVersion =
            PublicationRouteSurfaceContractCatalog.ContractVersion)
    {
        var rowCount = descriptor.RequiresRowCount
            ? Math.Max(1, descriptor.MinimumRowCount)
            : (long?)null;
        var bindingJson = new Dictionary<string, object?>
        {
            ["contractVersion"] = contractVersion,
        };
        if (descriptor.PublicationIdProperty is not null)
        {
            bindingJson[descriptor.PublicationIdProperty] =
                sourcePublicationId;
        }
        if (descriptor.ScrapeIdProperty is not null)
            bindingJson[descriptor.ScrapeIdProperty] = sourceScrapeId;
        if (descriptor.SourceGenerationProperty is not null)
            bindingJson[descriptor.SourceGenerationProperty] = BandGeneration;
        if (descriptor.RequiredSourceKind is not null)
            bindingJson["sourceKind"] = descriptor.RequiredSourceKind;
        if (descriptor.RequiresExactSource)
            bindingJson["isExact"] = true;
        if (descriptor.JsonRowCountProperty is not null)
            bindingJson[descriptor.JsonRowCountProperty] = rowCount;

        return new PublicationSurfaceBinding(
            PublicationId,
            descriptor.SurfaceName,
            descriptor.AllowedBindingKinds[0],
            JsonSerializer.Serialize(bindingJson),
            rowCount,
            descriptor.ContentHashRequirement switch
            {
                PublicationContentHashRequirement.None => null,
                PublicationContentHashRequirement.Md5OrSha256 =>
                    new string('a', 32),
                PublicationContentHashRequirement.Sha256 =>
                    new string('a', 64),
                _ => throw new ArgumentOutOfRangeException(),
            },
            PublicationGenerationStatus.Ready,
            DateTime.UtcNow);
    }

    private static PublicationSurfaceBinding[] ReplaceBinding(
        IReadOnlyList<PublicationSurfaceBinding> bindings,
        string surfaceName,
        Func<
            PublicationSurfaceBinding,
            PublicationSurfaceBinding> replace)
        => bindings
            .Select(binding => binding.SurfaceName == surfaceName
                ? replace(binding)
                : binding)
            .ToArray();

    private static void AssertReason(
        PublicationReadinessResult result,
        string surfaceName,
        string reason)
    {
        var surface = Assert.Single(
            result.UnreadySurfaces,
            item => item.Surface == surfaceName);
        Assert.Contains(reason, surface.Reasons);
    }
}
