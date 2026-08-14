using FSTService.Scraping.Replay;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class TierOneReplayFixtureTests
{
    [Fact]
    public async Task SyntheticTierOneFixtureIsSealedAndBounded()
    {
        var preserved = Environment.GetEnvironmentVariable(
            "FST_TIER1_FIXTURE_OUTPUT");
        var root = string.IsNullOrWhiteSpace(preserved)
            ? Path.Combine(
                AppContext.BaseDirectory,
                ".test-temp",
                $"tier1-fixture-{Guid.NewGuid():N}")
            : Path.GetFullPath(preserved);
        var shouldDelete = string.IsNullOrWhiteSpace(preserved);
        try
        {
            if (!shouldDelete)
            {
                var repository = FindRepositoryRoot();
                Assert.StartsWith(
                    Path.TrimEndingDirectorySeparator(repository) +
                    Path.DirectorySeparatorChar,
                    root,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }
            var fixture =
                await TierOneReplayFixture.CreateAsync(root);
            var parent = await TierZeroPackageVerifier.VerifyAsync(
                fixture.ParentPackage);
            var input = await TierZeroPackageVerifier.VerifyAsync(
                fixture.InputPackage);

            Assert.True(parent.IsValid);
            Assert.True(input.IsValid);
            Assert.Equal(
                fixture.ParentManifest.PackageRootHash,
                fixture.InputManifest.ParentRootHashes.Single(
                    static item =>
                        item.LogicalParent == "tier0-parent")
                    .Sha256);
            Assert.True(
                Directory.EnumerateFiles(
                        root,
                        "*",
                        SearchOption.AllDirectories)
                    .Sum(static file => new FileInfo(file).Length) <
                2 * 1024 * 1024);
        }
        finally
        {
            if (shouldDelete &&
                Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Repository root was not found.");
    }
}
