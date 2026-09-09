using System.Security.Cryptography;
using System.Text.Json;
using FSTService.Scraping.Replay;
using FSTService.Persistence.Maintenance;

namespace FstSnapshotGenerationRetentionReport;

public static class OfflineReportContract
{
    public const string ConnectionEnvironment = "FST_SNAPSHOT_RETENTION_REPORT_CONNECTION_STRING";
    public const string BinaryHashEnvironment = "FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256";
    public const string BinaryPathEnvironment = "FST_SNAPSHOT_RETENTION_REPORT_BINARY_PATH";
    public const string WrapperRelativePath = "tools/postgres-snapshot-generation-retention-report.sh";
    public const string Claim =
        "current offline report-only observation; no scrape, freeze, notification, schema initialization, worker lifecycle, archive, or source mutation";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool IsHash(string? value, int length = 64) =>
        value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Digest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(TierZeroCanonicalJson.Serialize(value))).ToLowerInvariant();

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record OfflineReportCodeIdentity(
    string RepositoryCommit,
    string RepositoryTree,
    string SourceSha256,
    string BinarySha256,
    string WrapperSha256,
    bool WorkingTreeClean);

public sealed record OfflineReportDatabaseIdentity(
    string DatabaseName,
    long DatabaseOid,
    string SystemIdentifier,
    int ServerVersionNum,
    string DataDirectory,
    DateTime PostmasterStartedAtUtc,
    string SessionUser,
    string CurrentUser,
    bool BypassesRowSecurity);

public sealed record OfflineReportRuntimeIdentity(
    OfflineReportCodeIdentity Code,
    OfflineReportDatabaseIdentity Database,
    string DatabaseIdentitySha256,
    string SchemaSha256,
    bool RequiredSchemaAccepted,
    SnapshotGenerationRetentionWorkerConfiguration? WorkerConfiguration = null);

public sealed record OfflineReportAssertions(
    string RepositoryCommit,
    string RepositoryTree,
    string SourceSha256,
    string WrapperSha256,
    string SchemaSha256,
    string DatabaseIdentitySha256,
    string WorkerConfigurationSha256)
{
    public void Require(OfflineReportRuntimeIdentity observed)
    {
        if (RepositoryCommit != observed.Code.RepositoryCommit
            || RepositoryTree != observed.Code.RepositoryTree
            || SourceSha256 != observed.Code.SourceSha256
            || WrapperSha256 != observed.Code.WrapperSha256)
            throw new OfflineReportRefusal("code_identity_mismatch");
        if (DatabaseIdentitySha256 != observed.DatabaseIdentitySha256)
            throw new OfflineReportRefusal("database_identity_mismatch");
        if (!observed.RequiredSchemaAccepted || SchemaSha256 != observed.SchemaSha256)
            throw new OfflineReportRefusal("required_schema_mismatch");
        var configuration = observed.WorkerConfiguration
            ?? throw new OfflineReportRefusal("report_only_configuration_missing");
        if (!configuration.IsAuthentic
            || configuration.CanonicalCycleIdentityVersion != SnapshotGenerationRetentionContract.CanonicalCycleIdentityVersion
            || configuration.PlannerVersion != SnapshotGenerationRetentionContract.PlannerVersion
            || configuration.ConfigVersion != SnapshotGenerationRetentionContract.ConfigVersion)
            throw new OfflineReportRefusal("report_only_configuration_incompatible");
        if (!configuration.ReportOnlyEnabled)
            throw new OfflineReportRefusal("report_only_configuration_disabled");
        if (WorkerConfigurationSha256 != configuration.ConfigurationSha256)
            throw new OfflineReportRefusal("worker_configuration_identity_mismatch");
    }
}

public sealed class OfflineReportRefusal(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed record OfflineReportCommand(string Name, OfflineReportAssertions? Assertions)
{
    private static readonly string[] Options =
    [
        "expected-repository-commit", "expected-repository-tree",
        "expected-source-sha256", "expected-wrapper-sha256",
        "expected-schema-sha256", "expected-database-identity-sha256",
        "expected-worker-configuration-sha256",
    ];

    public static OfflineReportCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] == "inspect")
            return new("inspect", null);
        if (args.Count != 15 || args[0] != "observe-current")
            throw new OfflineReportRefusal("invalid_command_or_arguments");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal)
                || !Options.Contains(option[2..], StringComparer.Ordinal)
                || !values.TryAdd(option[2..], args[index + 1]))
                throw new OfflineReportRefusal("invalid_assertion_option");
        }
        if (Options.Any(option => !values.ContainsKey(option)))
            throw new OfflineReportRefusal("missing_identity_assertion");
        for (var index = 0; index < Options.Length; index++)
        {
            if (!OfflineReportContract.IsHash(values[Options[index]], index < 2 ? 40 : 64))
                throw new OfflineReportRefusal("invalid_identity_assertion");
        }
        return new("observe-current", new(
            values[Options[0]], values[Options[1]], values[Options[2]],
            values[Options[3]], values[Options[4]], values[Options[5]], values[Options[6]]));
    }
}
