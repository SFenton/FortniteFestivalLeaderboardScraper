using System.Text.Json;
using FSTService.Scraping;
using FSTService.Scraping.Replay;

namespace FSTService.Tests.Unit;

public sealed class TierZeroEvidenceContractTests
{
    [Fact]
    public void PhasePlanUsesTheStableProgressCatalog()
    {
        var plan = TierZeroPhasePlan.FromCurrentCatalog();

        Assert.Equal(PhaseProgressCatalog.OperationId, plan.Id);
        Assert.Equal(PhaseProgressCatalog.PlanVersion, plan.Version);
        Assert.Equal(PhaseProgressCatalog.All.Count, plan.Phases.Count);
        Assert.Equal(
            PhaseProgressCatalog.All.Select(static phase => phase.Id),
            plan.Phases.Select(static phase => phase.Id));
        Assert.Equal(
            PhaseProgressCatalog.All.Select(static phase => phase.Ordinal),
            plan.Phases.Select(static phase => phase.Ordinal));
        Assert.Equal(
            PhaseProgressCatalog.All.Select(static phase => phase.DefaultUnitsKind),
            plan.Phases.Select(static phase => phase.DefaultUnitsKind));
    }

    [Fact]
    public void ConfigurationFingerprintIsOrderedAndDoesNotRetainValues()
    {
        var first = TierZeroConfigurationFingerprinter.Create(
            new Dictionary<string, string?>
            {
                ["Scraper:PageConcurrency"] = "32",
                ["Scraper:SequentialScrape"] = "false",
            },
            ["Scraper:SequentialScrape", "Scraper:PageConcurrency"]);
        var second = TierZeroConfigurationFingerprinter.Create(
            new Dictionary<string, string?>
            {
                ["Scraper:SequentialScrape"] = "false",
                ["Scraper:PageConcurrency"] = "32",
            },
            ["Scraper:PageConcurrency", "Scraper:SequentialScrape"]);

        Assert.Equal(first.Algorithm, second.Algorithm);
        Assert.Equal(first.ValuesSha256, second.ValuesSha256);
        Assert.Equal(first.Keys, second.Keys);
        Assert.Equal(
            ["Scraper:PageConcurrency", "Scraper:SequentialScrape"],
            first.Keys);
        var serialized = TierZeroCanonicalJson.SerializeToString(first);
        Assert.DoesNotContain("\"32\"", serialized);
        Assert.DoesNotContain("\"false\"", serialized);
    }

    [Theory]
    [InlineData("ConnectionStrings:PostgreSQL", "Host=postgres", TierZeroConfigurationFailureKind.SecretLikeKey)]
    [InlineData("Api:Token", "not-a-token", TierZeroConfigurationFailureKind.SecretLikeKey)]
    [InlineData("Scraper:ProxyEndpoint", "proxy-1", TierZeroConfigurationFailureKind.SecretLikeKey)]
    [InlineData("Epic:AccountId", "account-1", TierZeroConfigurationFailureKind.SecretLikeKey)]
    [InlineData("Scraper:Mode", "https://private.example", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "Bearer credential", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "Cookie: session=value", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "abcdefgh.ijklmnop.qrstuvwx", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "prefix abcdefgh.ijklmnop.qrstuvwx suffix", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "safe; access_token=opaque-value", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", """{"token":"opaque-value"}""", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "api-key = opaque-value", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "db.internal.example:5432", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "10.0.0.5:5432", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "private.internal.example", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "request to db.internal.example failed", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "request to localhost failed", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "request to [2001:db8::1] failed", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "Server=postgres;Port=5432;Database=fst", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "Host = postgres;Database = fst;User ID = admin", TierZeroConfigurationFailureKind.SecretLikeValue)]
    [InlineData("Scraper:Mode", "safe", TierZeroConfigurationFailureKind.KeyNotAllowlisted)]
    public void ConfigurationFingerprintRejectsUnsafeInput(
        string key,
        string value,
        TierZeroConfigurationFailureKind expected)
    {
        var exception = Assert.Throws<TierZeroConfigurationException>(() =>
            TierZeroConfigurationFingerprinter.Create(
                new Dictionary<string, string?> { [key] = value },
                ["Scraper:PageConcurrency"]));

        Assert.Equal(expected, exception.Kind);
    }

    [Fact]
    public void ConfigurationFingerprintRejectsInvalidAllowlistShapes()
    {
        AssertConfigurationFailure(
            new Dictionary<string, string?>(),
            [],
            TierZeroConfigurationFailureKind.EmptyAllowlist);
        AssertConfigurationFailure(
            new Dictionary<string, string?>
            {
                ["Scraper:PageConcurrency"] = "32",
            },
            ["Scraper:PageConcurrency", "Scraper:PageConcurrency"],
            TierZeroConfigurationFailureKind.DuplicateAllowlistKey);
        AssertConfigurationFailure(
            new Dictionary<string, string?>(),
            ["Scraper:PageConcurrency"],
            TierZeroConfigurationFailureKind.MissingAllowlistedKey);
        AssertConfigurationFailure(
            new Dictionary<string, string?> { [" "] = "32" },
            [" "],
            TierZeroConfigurationFailureKind.InvalidKey);
        AssertConfigurationFailure(
            new Dictionary<string, string?>
            {
                ["Scraper:PageConcurrency"] = "32\n64",
            },
            ["Scraper:PageConcurrency"],
            TierZeroConfigurationFailureKind.InvalidValue);

        var nullable = TierZeroConfigurationFingerprinter.Create(
            new Dictionary<string, string?>
            {
                ["Scraper:PageConcurrency"] = null,
            },
            ["Scraper:PageConcurrency"]);
        Assert.Single(nullable.Keys);

        var clockValue = TierZeroConfigurationFingerprinter.Create(
            new Dictionary<string, string?>
            {
                ["Scraper:Window"] = "12:34",
            },
            ["Scraper:Window"]);
        Assert.Single(clockValue.Keys);
    }

    [Fact]
    public void CanonicalJsonOrdersPropertiesAndHandlesPrimitiveKinds()
    {
        using var document = JsonDocument.Parse(
            """{"z":null,"truth":true,"number":12,"falsehood":false,"array":[true,null,2]}""");

        var json = TierZeroCanonicalJson.SerializeToString(
            document.RootElement);

        Assert.Equal(
            """{"array":[true,null,2],"falsehood":false,"number":12,"truth":true,"z":null}""",
            json);
    }

    [Theory]
    [InlineData("data\\scores.json", "data/scores.json")]
    [InlineData("data/./scores.json", "data/scores.json")]
    [InlineData("scope//summary.json", "scope/summary.json")]
    public void PackagePathsNormalizeAcrossSeparators(
        string input,
        string expected)
    {
        Assert.Equal(expected, TierZeroPackagePath.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.json")]
    [InlineData("data/../../escape.json")]
    [InlineData("/absolute.json")]
    [InlineData("\\\\server\\share\\artifact.json")]
    [InlineData("C:\\artifact.json")]
    [InlineData("data/a:b.json")]
    [InlineData("data/file.")]
    [InlineData("data/file ")]
    [InlineData("CON")]
    [InlineData("data/LPT1.txt")]
    [InlineData("COM¹")]
    [InlineData("data/LPT².log")]
    [InlineData("CONIN$")]
    [InlineData("data/CONOUT$.txt")]
    public void PackagePathsRejectAbsoluteAndTraversalInputs(string input)
    {
        var exception = Assert.Throws<TierZeroPackageException>(
            () => TierZeroPackagePath.Normalize(input));

        Assert.Equal(TierZeroPackageError.InvalidPath, exception.Error);
    }

    [Fact]
    public void PackagePathsNormalizeUnicodeToCanonicalComposition()
    {
        Assert.Equal(
            "data/\u00e9vidence.json",
            TierZeroPackagePath.Normalize(
                "data/e\u0301vidence.json"));
    }

    [Fact]
    public void PackagePathResolutionRejectsEscapeAndReservedFiles()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "tier0-path-resolution");

        var exception = Assert.Throws<TierZeroPackageException>(() =>
            TierZeroPackagePath.ResolveUnderRoot(root, "../escape.json"));

        Assert.Equal(TierZeroPackageError.PathEscapesPackage, exception.Error);
        Assert.True(TierZeroPackagePath.IsReserved(
            TierZeroEvidenceFormat.ManifestFileName));
        Assert.True(TierZeroPackagePath.IsReserved(
            TierZeroEvidenceFormat.ChecksumFileName));
        Assert.True(TierZeroPackagePath.IsReserved(
            TierZeroEvidenceFormat.StateFileName));
        Assert.True(TierZeroPackagePath.IsReserved("CHECKSUMS.SHA256"));
        Assert.True(TierZeroPackagePath.IsReserved("PACKAGE.LOCK"));
        Assert.False(TierZeroPackagePath.IsReserved("data/artifact.json"));
    }

    [Fact]
    public void SymbolicLinkGuardRejectsCandidateOutsidePackage()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "tier0-path-root");
        var outside = Path.Combine(
            AppContext.BaseDirectory,
            "tier0-outside",
            "artifact.json");

        var exception = Assert.Throws<TierZeroPackageException>(() =>
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                outside,
                includeCandidate: true));

        Assert.Equal(TierZeroPackageError.PathEscapesPackage, exception.Error);
    }

    private static void AssertConfigurationFailure(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string> allowlist,
        TierZeroConfigurationFailureKind expected)
    {
        var exception = Assert.Throws<TierZeroConfigurationException>(() =>
            TierZeroConfigurationFingerprinter.Create(values, allowlist));
        Assert.Equal(expected, exception.Kind);
    }
}
