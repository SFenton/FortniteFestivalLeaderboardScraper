using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FstSnapshotGenerationEvidence;

namespace FstSnapshotGenerationRetirement;

public static class SnapshotGenerationRetirementContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-retirement-plan.v1";
    public const string StagePlan = "plan";
    public const string ConnectionEnvironment =
        "FST_SNAPSHOT_RETIREMENT_CONNECTION_STRING";
    public const string BinaryHashEnvironment =
        "FST_SNAPSHOT_RETIREMENT_BINARY_SHA256";
    public const string BinaryPathEnvironment =
        "FST_SNAPSHOT_RETIREMENT_BINARY_PATH";
    public const string WrapperRelativePath =
        "tools/postgres-snapshot-generation-retirement.sh";
    public const string HonestClaim =
        "largest-first planning only; no archive, detach, quarantine, drop, delete, truncate, restore, worker lifecycle, or steady-state storage claim";
    public const long PublicationAdvisoryLockKey =
        5067481511116519500L;
    public const long PlannerAdvisoryLockKey =
        2026082301L;
    public const long SchemaAdvisoryLockKey =
        2026090402L;

    public static readonly IReadOnlyList<
        SnapshotGenerationRetirementInstrument> Instruments =
    [
        new("Solo_Guitar", 0),
        new("Solo_Bass", 1),
        new("Solo_Vocals", 2),
        new("Solo_Drums", 3),
        new("Solo_PeripheralGuitar", 4),
        new("Solo_PeripheralBass", 5),
        new("Solo_PeripheralVocals", 6),
        new("Solo_PeripheralCymbals", 7),
        new("Solo_PeripheralDrums", 8),
    ];

    public static SnapshotGenerationRetirementInstrument
        GetInstrument(string instrument) =>
        Instruments.SingleOrDefault(item =>
            string.Equals(
                item.Instrument,
                instrument,
                StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"Unsupported snapshot-generation instrument: {instrument}");
}

public sealed record SnapshotGenerationRetirementInstrument(
    string Instrument,
    short CanonicalOrder);

public static class RetirementJson
{
    public static readonly JsonSerializerOptions Output =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
        };

    public static byte[] Canonical<T>(T value) =>
        SnapshotGenerationCanonicalJson.Serialize(value);

    public static string Sha256<T>(T value) =>
        Convert.ToHexString(
                SHA256.HashData(Canonical(value)))
            .ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    public static bool IsGitObjectId(string? value) =>
        value is { Length: 40 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
}

public sealed record RetirementCodeIdentity(
    string RepositoryCommit,
    string RepositoryTree,
    string SupervisorBinarySha256,
    string SupervisorSourceSha256,
    string WrapperSha256)
{
    public void Validate()
    {
        if (!RetirementJson.IsGitObjectId(
                RepositoryCommit)
            || !RetirementJson.IsGitObjectId(
                RepositoryTree)
            || !RetirementJson.IsSha256(
                SupervisorBinarySha256)
            || !RetirementJson.IsSha256(
                SupervisorSourceSha256)
            || !RetirementJson.IsSha256(
                WrapperSha256))
        {
            throw new InvalidDataException(
                "Snapshot-generation retirement code identity is invalid.");
        }
    }
}

public sealed record RetirementDatabaseIdentity(
    string DatabaseName,
    long DatabaseOid,
    string SystemIdentifier,
    int ServerVersionNum,
    string DataDirectory,
    DateTimeOffset PostmasterStartedAtUtc)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabaseName)
            || DatabaseOid <= 0
            || string.IsNullOrWhiteSpace(
                SystemIdentifier)
            || SystemIdentifier.Any(character =>
                character is < '0' or > '9')
            || ServerVersionNum is < 170000 or >= 180000
            || string.IsNullOrWhiteSpace(DataDirectory)
            || !DataDirectory.StartsWith(
                "/",
                StringComparison.Ordinal)
            || PostmasterStartedAtUtc == default)
        {
            throw new InvalidDataException(
                "Snapshot-generation retirement database identity is invalid.");
        }
    }

    public string ComputeDigest()
    {
        Validate();
        return RetirementJson.Sha256(this);
    }
}

public sealed record RetirementRuntimeIdentity(
    RetirementCodeIdentity Code,
    RetirementDatabaseIdentity Database,
    string ControlSchemaSha256,
    string SourceIdentitySha256)
{
    public void Validate()
    {
        Code.Validate();
        Database.Validate();
        if (!RetirementJson.IsSha256(
                ControlSchemaSha256)
            || !RetirementJson.IsSha256(
                SourceIdentitySha256)
            || SourceIdentitySha256 !=
                Database.ComputeDigest())
        {
            throw new InvalidDataException(
                "Snapshot-generation retirement source identity is invalid.");
        }
    }
}

public sealed record RetirementAuthorizationRequest(
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    int MaxJobs,
    long MaxTotalBytes,
    string ApprovedBy,
    string ReviewedBy,
    string ApprovalReference,
    string ExpectedRepositoryCommit,
    string ExpectedRepositoryTree,
    string ExpectedSupervisorBinarySha256,
    string ExpectedSupervisorSourceSha256,
    string ExpectedWrapperSha256,
    string ExpectedControlSchemaSha256,
    string ExpectedSourceIdentitySha256)
{
    public RetirementAuthorizationRequest Normalize() =>
        this with
        {
            ApprovedBy = ApprovedBy.Trim(),
            ReviewedBy = ReviewedBy.Trim(),
            ApprovalReference =
                ApprovalReference.Trim(),
        };

    public void Validate()
    {
        if (NotBefore.Offset != TimeSpan.Zero
            || ExpiresAt.Offset != TimeSpan.Zero
            || NotBefore.Ticks % 10 != 0
            || ExpiresAt.Ticks % 10 != 0
            || ExpiresAt <= NotBefore
            || ExpiresAt > NotBefore.AddDays(7)
            || MaxJobs is < 1 or > 32
            || MaxTotalBytes is < 1
                or > 17592186044416L
            || string.IsNullOrWhiteSpace(ApprovedBy)
            || string.IsNullOrWhiteSpace(ReviewedBy)
            || string.Equals(
                ApprovedBy.Trim(),
                ReviewedBy.Trim(),
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(
                ApprovalReference)
            || !RetirementJson.IsGitObjectId(
                ExpectedRepositoryCommit)
            || !RetirementJson.IsGitObjectId(
                ExpectedRepositoryTree)
            || !RetirementJson.IsSha256(
                ExpectedSupervisorBinarySha256)
            || !RetirementJson.IsSha256(
                ExpectedSupervisorSourceSha256)
            || !RetirementJson.IsSha256(
                ExpectedWrapperSha256)
            || !RetirementJson.IsSha256(
                ExpectedControlSchemaSha256)
            || !RetirementJson.IsSha256(
                ExpectedSourceIdentitySha256))
        {
            throw new ArgumentException(
                "Snapshot-generation retirement authorization is invalid.");
        }
    }

    public void RequireExactIdentity(
        RetirementRuntimeIdentity identity)
    {
        Validate();
        identity.Validate();
        if (ExpectedRepositoryCommit !=
                identity.Code.RepositoryCommit
            || ExpectedRepositoryTree !=
                identity.Code.RepositoryTree
            || ExpectedSupervisorBinarySha256 !=
                identity.Code.SupervisorBinarySha256
            || ExpectedSupervisorSourceSha256 !=
                identity.Code.SupervisorSourceSha256
            || ExpectedWrapperSha256 !=
                identity.Code.WrapperSha256
            || ExpectedControlSchemaSha256 !=
                identity.ControlSchemaSha256
            || ExpectedSourceIdentitySha256 !=
                identity.SourceIdentitySha256)
        {
            throw new InvalidOperationException(
                "The observed retirement runtime differs from the reviewed authorization identity.");
        }
    }
}

public sealed record SnapshotGenerationRetirementPolicy(
    Guid PolicyEpochId,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    int MaxJobs,
    long MaxTotalBytes,
    RetirementRuntimeIdentity RuntimeIdentity,
    string ApprovedBy,
    string ReviewedBy,
    string ApprovalReference,
    DateTimeOffset AuthorizedAt,
    string PolicyDigest);

public sealed record SnapshotGenerationRetirementJob(
    Guid JobId,
    Guid PolicyEpochId,
    long CycleId,
    long ObservationId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string Instrument,
    short InstrumentOrder,
    long SnapshotId,
    string RootSchema,
    string RootRelation,
    long RootOid,
    string ChildSchema,
    string ChildRelation,
    long ChildOid,
    long ChildRelfilenode,
    string StableChildIdentityHash,
    string StableConfigSchemaHash,
    long TargetBytes,
    string SourceIdentitySha256,
    string PlanDigest,
    string State,
    string? StateReason,
    DateTimeOffset PlannedAt,
    DateTimeOffset? TerminalAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RetirementControlState(
    bool Enabled,
    Guid? ActivePolicyEpochId,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record RetirementStatus(
    bool SchemaInitialized,
    string Claim,
    RetirementRuntimeIdentity ObservedIdentity,
    RetirementControlState? Control,
    SnapshotGenerationRetirementPolicy? ActivePolicy,
    SnapshotGenerationRetirementPolicy? LatestPolicy,
    SnapshotGenerationRetirementJob? ActiveJob,
    SnapshotGenerationRetirementJob? LatestJob);

public sealed record RetirementReconcileResult(
    string Outcome,
    string Claim,
    SnapshotGenerationRetirementPolicy? Policy,
    SnapshotGenerationRetirementJob? Job);

public sealed record RetirementTarget(
    long CycleId,
    long ObservationId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string Instrument,
    short InstrumentOrder,
    long SnapshotId,
    string RootSchema,
    string RootRelation,
    long RootOid,
    string ChildSchema,
    string ChildRelation,
    long ChildOid,
    long ChildRelfilenode,
    string StableChildIdentityHash,
    string StableConfigSchemaHash,
    long TargetBytes);

internal sealed record RetirementPolicyDigestInput(
    int SchemaVersion,
    string ToolId,
    string StageCeiling,
    RetirementAuthorizationRequest Authorization,
    RetirementRuntimeIdentity RuntimeIdentity,
    DateTimeOffset AuthorizedAt);

internal sealed record RetirementPlanDigestInput(
    int SchemaVersion,
    string ToolId,
    Guid PolicyEpochId,
    RetirementTarget Target,
    string SourceIdentitySha256,
    DateTimeOffset PlannedAt);

internal sealed record RetirementEventHashInput(
    Guid PolicyEpochId,
    Guid? JobId,
    int Sequence,
    string EventType,
    JsonElement Payload,
    string? PreviousHash);

public sealed class RetirementCommandLine
{
    private static readonly HashSet<string>
        CommandsWithoutArguments =
    [
        "status",
        "reconcile",
        "plan-cycle",
        "deactivate-policy-epoch",
    ];

    private static readonly string[] AuthorizationOptions =
    [
        "not-before",
        "expires-at",
        "max-jobs",
        "max-total-bytes",
        "approved-by",
        "reviewed-by",
        "approval-reference",
        "expected-repository-commit",
        "expected-repository-tree",
        "expected-supervisor-binary-sha256",
        "expected-supervisor-source-sha256",
        "expected-wrapper-sha256",
        "expected-control-schema-sha256",
        "expected-source-identity-sha256",
    ];

    private readonly IReadOnlyDictionary<string, string>
        _options;

    private RetirementCommandLine(
        string command,
        IReadOnlyDictionary<string, string> options)
    {
        Command = command;
        _options = options;
    }

    public string Command { get; }

    public static RetirementCommandLine Parse(
        IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException(
                "A retirement command is required.");
        }
        var command = args[0];
        if (CommandsWithoutArguments.Contains(command))
        {
            if (args.Count != 1)
            {
                throw new ArgumentException(
                    $"{command} accepts no arguments.");
            }
            return new(
                command,
                new Dictionary<string, string>());
        }
        if (command != "authorize-policy-epoch")
        {
            throw new ArgumentException(
                $"Unknown retirement command: {command}");
        }
        if ((args.Count - 1) % 2 != 0)
        {
            throw new ArgumentException(
                "Authorization options require a value.");
        }
        var allowed = AuthorizationOptions.ToHashSet(
            StringComparer.Ordinal);
        var options = new Dictionary<string, string>(
            StringComparer.Ordinal);
        for (var index = 1;
             index < args.Count;
             index += 2)
        {
            var option = args[index];
            if (!option.StartsWith(
                    "--",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Invalid retirement option: {option}");
            }
            var name = option[2..];
            if (!allowed.Contains(name)
                || !options.TryAdd(
                    name,
                    args[index + 1]))
            {
                throw new ArgumentException(
                    $"Unknown or duplicate retirement option: {option}");
            }
        }
        var missing = AuthorizationOptions
            .Where(option =>
                !options.ContainsKey(option))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                $"Missing retirement options: {string.Join(", ", missing)}");
        }
        return new(command, options);
    }

    public RetirementAuthorizationRequest
        GetAuthorizationRequest()
    {
        if (Command != "authorize-policy-epoch")
        {
            throw new InvalidOperationException(
                "This command has no authorization request.");
        }
        var request =
            new RetirementAuthorizationRequest(
                ParseUtc("not-before"),
                ParseUtc("expires-at"),
                ParseInt32("max-jobs"),
                ParseInt64("max-total-bytes"),
                Get("approved-by"),
                Get("reviewed-by"),
                Get("approval-reference"),
                Get("expected-repository-commit"),
                Get("expected-repository-tree"),
                Get(
                    "expected-supervisor-binary-sha256"),
                Get(
                    "expected-supervisor-source-sha256"),
                Get("expected-wrapper-sha256"),
                Get("expected-control-schema-sha256"),
                Get(
                    "expected-source-identity-sha256"));
        request.Validate();
        return request;
    }

    private string Get(string name) =>
        _options[name];

    private DateTimeOffset ParseUtc(string name)
    {
        if (!DateTimeOffset.TryParseExact(
                Get(name),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value)
            || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"--{name} must be a UTC round-trip timestamp.");
        }
        return value;
    }

    private int ParseInt32(string name) =>
        int.TryParse(
            Get(name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new ArgumentException(
                $"--{name} must be an integer.");

    private long ParseInt64(string name) =>
        long.TryParse(
            Get(name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new ArgumentException(
                $"--{name} must be an integer.");
}
