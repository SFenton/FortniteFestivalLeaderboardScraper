using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence.Maintenance;

namespace FSTService.Tests.Unit;

public sealed class OfflineReportCommandTests
{
    [Fact]
    public void InspectHasNoSelectorsOrAssertions()
    {
        var command = OfflineReportCommand.Parse(["inspect"]);
        Assert.Equal("inspect", command.Name);
        Assert.Null(command.Assertions);
        Assert.Throws<OfflineReportRefusal>(() => OfflineReportCommand.Parse(["inspect", "2000"]));
    }

    [Fact]
    public void ObserveRequiresEveryExactIdentityAssertion()
    {
        var parsed = OfflineReportCommand.Parse(ValidArguments());
        Assert.Equal("observe-current", parsed.Name);
        Assert.NotNull(parsed.Assertions);
        Assert.Throws<OfflineReportRefusal>(() => OfflineReportCommand.Parse(["observe-current"]));
        var duplicated = ValidArguments();
        duplicated[3] = duplicated[1];
        Assert.Throws<OfflineReportRefusal>(() => OfflineReportCommand.Parse(duplicated));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("prove")]
    [InlineData("detach")]
    [InlineData("quarantine")]
    [InlineData("drop")]
    [InlineData("delete")]
    [InlineData("truncate")]
    [InlineData("restore")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("initialize-schema")]
    [InlineData("resume")]
    [InlineData("authorize-policy-epoch")]
    public void ExecutionOrMutationCommandsDoNotExist(string name) =>
        Assert.Throws<OfflineReportRefusal>(() => OfflineReportCommand.Parse([name]));

    [Theory]
    [InlineData("--scrape-id")]
    [InlineData("--cycle-id")]
    [InlineData("--target")]
    [InlineData("--relation")]
    [InlineData("--sql")]
    [InlineData("--path")]
    [InlineData("--connection-string")]
    [InlineData("--force")]
    [InlineData("--background-quiesced")]
    [InlineData("--broadcast-completed-scrape-id")]
    [InlineData("--resume-scrape-id")]
    [InlineData("--resume")]
    [InlineData("--report-only-enabled")]
    public void ObservationHasNoMutableOrCallerAssertedSafePointSelector(string selector)
    {
        var args = ValidArguments();
        args[1] = selector;
        var error = Assert.Throws<OfflineReportRefusal>(() => OfflineReportCommand.Parse(args));
        Assert.Equal("invalid_assertion_option", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("not-a-hash")]
    public void InvalidPinsAreRejectedWithoutEchoingValues(string value)
    {
        var args = ValidArguments();
        args[6] = value;
        var error = Assert.Throws<OfflineReportRefusal>(() => OfflineReportCommand.Parse(args));
        Assert.Equal("invalid_identity_assertion", error.Message);
    }

    [Theory]
    [InlineData("commit", "code_identity_mismatch")]
    [InlineData("tree", "code_identity_mismatch")]
    [InlineData("source", "code_identity_mismatch")]
    [InlineData("wrapper", "code_identity_mismatch")]
    [InlineData("schema", "required_schema_mismatch")]
    [InlineData("shape", "required_schema_mismatch")]
    [InlineData("database", "database_identity_mismatch")]
    public void EveryRuntimeIdentityAssertionFailsClosed(string change, string expected)
    {
        var identity = Identity();
        var assertions = Assertions(identity);
        var changed = change switch
        {
            "commit" => identity with { Code = identity.Code with { RepositoryCommit = new('b', 40) } },
            "tree" => identity with { Code = identity.Code with { RepositoryTree = new('b', 40) } },
            "source" => identity with { Code = identity.Code with { SourceSha256 = new('b', 64) } },
            "wrapper" => identity with { Code = identity.Code with { WrapperSha256 = new('b', 64) } },
            "schema" => identity with { SchemaSha256 = new('b', 64) },
            "shape" => identity with { RequiredSchemaAccepted = false },
            _ => identity with { DatabaseIdentitySha256 = new('b', 64) },
        };
        var error = Assert.Throws<OfflineReportRefusal>(() => assertions.Require(changed));
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void ReviewedUncommittedSourceIsExplicitlyHashPinnedNotMislabelledClean()
    {
        var identity = Identity() with { Code = Identity().Code with { WorkingTreeClean = false } };
        Assertions(identity).Require(identity);
        Assert.False(identity.Code.WorkingTreeClean);
    }

    [Fact]
    public void HostEntryPointDoesNotInitializeAServiceOrControlAnyWorker()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FortniteFestivalLeaderboardScraper.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName,
            "tools/FstSnapshotGenerationRetentionReport/Program.cs"));
        foreach (var forbidden in new[]
                 {
                     "WebApplication", "Host.Create", "AddHostedService", "StartupInitializer",
                     "EnsureSchema", "StartScrapeRun", "ScrapeStarting", "SetPublicReadFreeze",
                     "NotifyScoresChanged", "DockerClient", "Process.Start",
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.Contains("ObserveCurrentOfflineAsync", source, StringComparison.Ordinal);
    }

    private static string[] ValidArguments() =>
    [
        "observe-current",
        "--expected-repository-commit", new('a', 40),
        "--expected-repository-tree", new('a', 40),
        "--expected-source-sha256", new('a', 64),
        "--expected-wrapper-sha256", new('a', 64),
        "--expected-schema-sha256", new('a', 64),
        "--expected-database-identity-sha256", new('a', 64),
        "--expected-worker-configuration-sha256", new('a', 64),
    ];

    private static OfflineReportRuntimeIdentity Identity() => new(
        new(new('a', 40), new('a', 40), new('a', 64), new('a', 64), new('a', 64), true),
        new("fixture", 1, "123", 170000, "/owned-data", DateTime.UnixEpoch, "fixture", "fixture", true),
        new('a', 64), new('a', 64), true, TestConfiguration());

    private static SnapshotGenerationRetentionWorkerConfiguration TestConfiguration()
    {
        var digest = SnapshotGenerationRetentionWorkerConfiguration.ComputeDigest(
            "fixture", true, 3, 1, 2, new('a', 64));
        return new("fixture", true, 3, 1, 2, new('a', 64), digest, DateTime.UnixEpoch);
    }

    internal static OfflineReportAssertions Assertions(OfflineReportRuntimeIdentity identity) =>
        new(identity.Code.RepositoryCommit, identity.Code.RepositoryTree,
            identity.Code.SourceSha256, identity.Code.WrapperSha256,
            identity.SchemaSha256, identity.DatabaseIdentitySha256,
            identity.WorkerConfiguration?.ConfigurationSha256 ?? new('f', 64));
}
