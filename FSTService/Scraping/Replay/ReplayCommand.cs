namespace FSTService.Scraping.Replay;

public enum ReplayExitCode
{
    Success = 0,
    UnexpectedFailure = 1,
    Usage = 2,
    RootRejected = 3,
    PackageRejected = 4,
    TargetRejected = 5,
    ImportRejected = 6,
    PhaseFailed = 7,
    OutputFailed = 8,
    ComparisonFailed = 9,
    Cancelled = 130,
}

public enum ReplayCommandKind
{
    Execute,
    Compare,
}

public sealed record ReplayCommand(
    ReplayCommandKind Kind,
    string? ParentPackagePath,
    string? InputPackagePath,
    string? PhaseId,
    string? SubphaseId,
    string? OutputPath,
    string? ReplayId,
    int Attempt,
    string? BaselinePath,
    string? CandidatePath,
    string? ComparisonOutputPath,
    ReplayComparisonExpectations? ComparisonExpectations = null,
    string ExecutionProfile =
        ReplayExecutionProfileCatalog.DeterministicProfileId)
{
    public const string ReplayPackageFlag = "--replay-package";
    public const string ReplayParentPackageFlag = "--replay-parent-package";
    public const string ReplayPhaseFlag = "--replay-phase";
    public const string ReplaySubphaseFlag = "--replay-subphase";
    public const string ReplayOutputFlag = "--replay-output";
    public const string ReplayIdFlag = "--replay-id";
    public const string ReplayAttemptFlag = "--replay-attempt";
    public const string ReplayProfileFlag = "--replay-profile";
    public const string NoPublicationFlag = "--no-publication";
    public const string CompareBaselineFlag = "--replay-compare-baseline";
    public const string CompareCandidateFlag = "--replay-compare-candidate";
    public const string ComparisonOutputFlag = "--replay-comparison-output";
    public const string BaselineDigestFlag =
        "--replay-baseline-image-digest";
    public const string CandidateDigestFlag =
        "--replay-candidate-image-digest";
    public const string BaselineRevisionFlag =
        "--replay-baseline-revision";
    public const string CandidateRevisionFlag =
        "--replay-candidate-revision";
    public const string BaselineGitCommitFlag =
        "--replay-baseline-git-commit";
    public const string CandidateGitCommitFlag =
        "--replay-candidate-git-commit";
    public const string BaselineAttemptFlag =
        "--replay-baseline-attempt";
    public const string CandidateAttemptFlag =
        "--replay-candidate-attempt";

    private static readonly HashSet<string> ValueFlags =
    [
        ReplayPackageFlag,
        ReplayParentPackageFlag,
        ReplayPhaseFlag,
        ReplaySubphaseFlag,
        ReplayOutputFlag,
        ReplayIdFlag,
        ReplayAttemptFlag,
        ReplayProfileFlag,
        CompareBaselineFlag,
        CompareCandidateFlag,
        ComparisonOutputFlag,
        BaselineDigestFlag,
        CandidateDigestFlag,
        BaselineRevisionFlag,
        CandidateRevisionFlag,
        BaselineGitCommitFlag,
        CandidateGitCommitFlag,
        BaselineAttemptFlag,
        CandidateAttemptFlag,
    ];

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(static argument =>
            CanonicalFlag(argument).StartsWith(
                "--replay-",
                StringComparison.OrdinalIgnoreCase));

    public static ReplayCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var noPublication = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=');
            var flag = separator >= 0
                ? argument[..separator]
                : argument;
            flag = CanonicalFlag(flag);

            if (flag.Equals(
                    NoPublicationFlag,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (separator >= 0 ||
                    noPublication)
                {
                    throw Usage(
                        $"{NoPublicationFlag} must be specified exactly once without a value.");
                }
                noPublication = true;
                continue;
            }

            if (!ValueFlags.Contains(flag))
            {
                throw Usage(
                    $"Replay mode does not accept option '{argument}'.");
            }
            if (values.ContainsKey(flag))
                throw Usage($"Replay option '{flag}' was specified more than once.");

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else
            {
                if (++index >= args.Count ||
                    args[index].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw Usage($"Replay option '{flag}' requires a value.");
                }
                value = args[index];
            }
            if (string.IsNullOrWhiteSpace(value))
                throw Usage($"Replay option '{flag}' requires a non-empty value.");
            values.Add(flag, value);
        }

        if (!noPublication)
        {
            throw Usage(
                $"{NoPublicationFlag} is mandatory for every replay command.");
        }

        var compareRequested =
            values.ContainsKey(CompareBaselineFlag) ||
            values.ContainsKey(CompareCandidateFlag) ||
            values.ContainsKey(ComparisonOutputFlag);
        if (compareRequested)
        {
            RequireOnly(
                values,
                CompareBaselineFlag,
                CompareCandidateFlag,
                ComparisonOutputFlag,
                BaselineDigestFlag,
                CandidateDigestFlag,
                BaselineRevisionFlag,
                CandidateRevisionFlag,
                BaselineGitCommitFlag,
                CandidateGitCommitFlag,
                BaselineAttemptFlag,
                CandidateAttemptFlag);
            var baselineDigest =
                Required(values, BaselineDigestFlag);
            var candidateDigest =
                Required(values, CandidateDigestFlag);
            var baselineRevision =
                Required(values, BaselineRevisionFlag);
            var candidateRevision =
                Required(values, CandidateRevisionFlag);
            var baselineGitCommit =
                Required(values, BaselineGitCommitFlag);
            var candidateGitCommit =
                Required(values, CandidateGitCommitFlag);
            if (!TierZeroCanonicalJson.IsOciSha256(
                    baselineDigest) ||
                !TierZeroCanonicalJson.IsOciSha256(
                    candidateDigest) ||
                !IsCommit(baselineGitCommit) ||
                !IsCommit(candidateGitCommit) ||
                !IsCommit(baselineRevision) ||
                !IsCommit(candidateRevision))
            {
                throw Usage(
                    "Replay comparison image digest or revision is invalid.");
            }
            return new ReplayCommand(
                ReplayCommandKind.Compare,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                Required(values, CompareBaselineFlag),
                Required(values, CompareCandidateFlag),
                Required(values, ComparisonOutputFlag),
                new ReplayComparisonExpectations(
                    baselineDigest,
                    baselineGitCommit.ToLowerInvariant(),
                    baselineRevision.ToLowerInvariant(),
                    PositiveInteger(
                        values,
                        BaselineAttemptFlag),
                    candidateDigest,
                    candidateGitCommit.ToLowerInvariant(),
                    candidateRevision.ToLowerInvariant(),
                    PositiveInteger(
                        values,
                        CandidateAttemptFlag)));
        }

        RequireOnly(
            values,
            ReplayPackageFlag,
            ReplayParentPackageFlag,
            ReplayPhaseFlag,
            ReplaySubphaseFlag,
            ReplayOutputFlag,
            ReplayIdFlag,
            ReplayAttemptFlag,
            ReplayProfileFlag);
        var attemptText = Required(values, ReplayAttemptFlag);
        if (!int.TryParse(attemptText, out var attempt) ||
            attempt <= 0)
        {
            throw Usage(
                $"{ReplayAttemptFlag} requires a positive integer.");
        }

        return new ReplayCommand(
            ReplayCommandKind.Execute,
            Required(values, ReplayParentPackageFlag),
            Required(values, ReplayPackageFlag),
            Required(values, ReplayPhaseFlag),
            Required(values, ReplaySubphaseFlag),
            Required(values, ReplayOutputFlag),
            Required(values, ReplayIdFlag),
            attempt,
            null,
            null,
            null,
            null,
            ReplayExecutionProfileCatalog.Resolve(
                values.GetValueOrDefault(
                    ReplayProfileFlag)).Id);
    }

    private static string CanonicalFlag(string argument) =>
        argument.StartsWith(
            "--",
            StringComparison.Ordinal)
            ? argument
            : $"--{argument.TrimStart('-')}";

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string flag) =>
        values.TryGetValue(flag, out var value)
            ? value
            : throw Usage($"Replay mode requires {flag}.");

    private static void RequireOnly(
        IReadOnlyDictionary<string, string> values,
        params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var unexpected = values.Keys.FirstOrDefault(
            key => !allowedSet.Contains(key));
        if (unexpected is not null)
            throw Usage($"Replay option '{unexpected}' is not valid for this command.");
    }

    private static int PositiveInteger(
        IReadOnlyDictionary<string, string> values,
        string flag)
    {
        if (!int.TryParse(
                Required(values, flag),
                out var value) ||
            value <= 0)
        {
            throw Usage($"{flag} requires a positive integer.");
        }
        return value;
    }

    private static bool IsCommit(string value) =>
        value.Length is 40 or 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    private static ReplayException Usage(string message) =>
        new(ReplayFailureKind.Usage, ReplayExitCode.Usage, message);
}

public enum ReplayFailureKind
{
    Usage,
    RootRejected,
    PackageRejected,
    TargetRejected,
    ImportRejected,
    PhaseFailed,
    OutputFailed,
    ComparisonFailed,
}

public sealed class ReplayException : InvalidOperationException
{
    public ReplayException(
        ReplayFailureKind kind,
        ReplayExitCode exitCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ExitCode = exitCode;
    }

    public ReplayFailureKind Kind { get; }
    public ReplayExitCode ExitCode { get; }
}
