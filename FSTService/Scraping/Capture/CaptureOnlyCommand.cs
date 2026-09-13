using System.Globalization;
using System.Reflection;
using FSTService.Scraping.Replay;

namespace FSTService.Scraping.Capture;

public enum CaptureOnlyExitCode
{
    Success = 0,
    UnexpectedFailure = 1,
    Usage = 2,
    RootRejected = 3,
    AdmissionRejected = 4,
    AuthenticationFailed = 5,
    CatalogRejected = 6,
    CaptureFailed = 7,
    SealFailed = 8,
    Cancelled = 130,
}

public enum CaptureOnlyFailureKind
{
    Usage,
    RootRejected,
    AdmissionRejected,
    AuthenticationFailed,
    CatalogRejected,
    CaptureFailed,
    SealFailed,
    Cancelled,
    UnexpectedFailure,
}

public sealed class CaptureOnlyException : InvalidOperationException
{
    public CaptureOnlyException(
        CaptureOnlyFailureKind kind,
        CaptureOnlyExitCode exitCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ExitCode = exitCode;
    }

    public CaptureOnlyFailureKind Kind { get; }
    public CaptureOnlyExitCode ExitCode { get; }
}

public sealed record CaptureOnlyCommand(
    string OutputPath,
    string CaptureId)
{
    public const string CaptureOnlyFlag = "--capture-only";
    public const string CaptureOutputFlag = "--capture-output";
    public const string CaptureIdFlag = "--capture-id";

    private static readonly HashSet<string> ValueFlags =
    [
        CaptureOutputFlag,
        CaptureIdFlag,
    ];

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(static argument =>
        {
            var separator = argument.IndexOf('=');
            var flag = separator >= 0
                ? argument[..separator]
                : argument;
            return flag.StartsWith(
                "--capture-",
                StringComparison.OrdinalIgnoreCase);
        });
    }

    public static CaptureOnlyCommand Parse(
        IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var captureOnly = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.IsNullOrWhiteSpace(argument) ||
                !argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw Usage(
                    "Capture mode accepts only explicit long options.");
            }

            var separator = argument.IndexOf('=');
            var flag = separator >= 0
                ? argument[..separator]
                : argument;
            if (flag.Equals(
                    CaptureOnlyFlag,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (separator >= 0 || captureOnly)
                {
                    throw Usage(
                        $"{CaptureOnlyFlag} must be specified exactly once without a value.");
                }
                captureOnly = true;
                continue;
            }

            if (!ValueFlags.Contains(flag))
            {
                throw Usage(
                    "Capture mode received an unknown or conflicting option.");
            }
            if (values.ContainsKey(flag))
            {
                throw Usage(
                    $"Capture option '{flag}' was specified more than once.");
            }

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
                    throw Usage(
                        $"Capture option '{flag}' requires a value.");
                }
                value = args[index];
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Usage(
                    $"Capture option '{flag}' requires a non-empty value.");
            }
            values.Add(flag, value);
        }

        if (!captureOnly)
        {
            throw Usage(
                $"Capture mode requires {CaptureOnlyFlag}.");
        }

        var captureId = Required(values, CaptureIdFlag);
        if (!IsSafeCaptureId(captureId))
        {
            throw Usage(
                $"{CaptureIdFlag} must be 1-128 ASCII letters, digits, dots, underscores, or hyphens and must start with a letter or digit.");
        }

        return new CaptureOnlyCommand(
            Required(values, CaptureOutputFlag),
            captureId);
    }

    private static bool IsSafeCaptureId(string value) =>
        value.Length is >= 1 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-') &&
        !TierZeroConfigurationFingerprinter
            .IsSecretLikeValue(value);

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string flag) =>
        values.TryGetValue(flag, out var value)
            ? value
            : throw Usage($"Capture mode requires {flag}.");

    private static CaptureOnlyException Usage(string message) =>
        new(
            CaptureOnlyFailureKind.Usage,
            CaptureOnlyExitCode.Usage,
            message);
}

public sealed record CaptureRootPolicyOptions(
    string ApprovedRoot,
    string? ExpectedFileSystemDevice,
    bool TestOnly = false);

public sealed record CaptureOnlyExecutionEnvironment(
    CaptureRootPolicyOptions RootPolicy,
    CapturePackageStoragePolicy StoragePolicy,
    TierZeroBuildIdentity Implementation,
    int ResponseShardMaximumBytes,
    string ProducerIdentity,
    string? PaginationMaximumScoresPath = null)
{
    public const string ApprovedRootEnvironment =
        "FST_CAPTURE_APPROVED_ROOT";
    public const string ApprovedDeviceEnvironment =
        "FST_CAPTURE_APPROVED_DEVICE";
    public const string MaximumPackageBytesEnvironment =
        "FST_CAPTURE_MAX_PACKAGE_BYTES";
    public const string MinimumReserveBytesEnvironment =
        "FST_CAPTURE_MIN_FREE_SPACE_RESERVE_BYTES";
    public const string MaximumRetainedPackagesEnvironment =
        "FST_CAPTURE_MAX_RETAINED_SEALED_PACKAGES";
    public const string GitCommitEnvironment =
        "FST_CAPTURE_GIT_COMMIT";
    public const string ImageDigestEnvironment =
        "FST_CAPTURE_IMAGE_DIGEST";
    public const string ImageRevisionEnvironment =
        "FST_CAPTURE_IMAGE_REVISION";
    public const string ResponseShardBytesEnvironment =
        "FST_CAPTURE_RESPONSE_SHARD_BYTES";
    public const string PaginationMaximumScoresPathEnvironment =
        "FST_CAPTURE_PAGINATION_MAX_SCORES_PATH";
    public const int DefaultResponseShardMaximumBytes =
        64 * 1024 * 1024;
    public const string DefaultProducerIdentity =
        "fstservice-capture-only";

    public static CaptureOnlyExecutionEnvironment
        FromProcessEnvironment()
    {
        var maximumPackageBytes = RequiredPositiveInt64(
            MaximumPackageBytesEnvironment);
        if (maximumPackageBytes <=
            CaptureRootAdmission
                .PreMetadataFinalPackageAllowanceBytes)
        {
            throw Usage(
                $"{MaximumPackageBytesEnvironment} must exceed the bounded sealing-workspace allowance.");
        }
        var minimumReserveBytes = RequiredNonNegativeInt64(
            MinimumReserveBytesEnvironment);
        var maximumRetainedPackages = RequiredPositiveInt32(
            MaximumRetainedPackagesEnvironment);
        var responseShardBytes =
            OptionalPositiveInt32(
                ResponseShardBytesEnvironment,
                DefaultResponseShardMaximumBytes);
        if (responseShardBytes <=
                CapturePackageFormat
                    .MaximumResponseRecordBytes ||
            responseShardBytes >
            CapturePackageFormat.MaximumResponseShardBytes)
        {
            throw Usage(
                $"{ResponseShardBytesEnvironment} must exceed the response-record limit and cannot exceed the capture-contract shard limit.");
        }

        var gitCommit = Required(GitCommitEnvironment);
        var imageDigest = Required(ImageDigestEnvironment);
        var imageRevision = Required(ImageRevisionEnvironment);
        if (!IsCommit(gitCommit) ||
            !IsCommit(imageRevision) ||
            !TierZeroCanonicalJson.IsOciSha256(
                imageDigest.ToLowerInvariant()))
        {
            throw Usage(
                "Capture build identity environment variables are invalid.");
        }

        var version = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        return new CaptureOnlyExecutionEnvironment(
            new CaptureRootPolicyOptions(
                Required(ApprovedRootEnvironment),
                Required(ApprovedDeviceEnvironment)),
            new CapturePackageStoragePolicy(
                maximumPackageBytes,
                minimumReserveBytes,
                maximumRetainedPackages),
            new TierZeroBuildIdentity(
                gitCommit,
                imageDigest,
                imageRevision,
                version),
            responseShardBytes,
            DefaultProducerIdentity,
            Optional(
                PaginationMaximumScoresPathEnvironment));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw Usage(
                $"Capture mode requires environment variable {name}.");

    private static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name) is
        { } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static long RequiredPositiveInt64(string name)
    {
        var text = Required(name);
        if (!long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0)
        {
            throw Usage(
                $"{name} must be a positive integer.");
        }
        return value;
    }

    private static long RequiredNonNegativeInt64(string name)
    {
        var text = Required(name);
        if (!long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value < 0)
        {
            throw Usage(
                $"{name} must be a non-negative integer.");
        }
        return value;
    }

    private static int RequiredPositiveInt32(string name)
    {
        var text = Required(name);
        if (!int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0)
        {
            throw Usage(
                $"{name} must be a positive integer.");
        }
        return value;
    }

    private static int OptionalPositiveInt32(
        string name,
        int defaultValue)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text))
            return defaultValue;
        if (!int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0)
        {
            throw Usage(
                $"{name} must be a positive integer.");
        }
        return value;
    }

    private static bool IsCommit(string value) =>
        value.Length is 40 or 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    private static CaptureOnlyException Usage(string message) =>
        new(
            CaptureOnlyFailureKind.Usage,
            CaptureOnlyExitCode.Usage,
            message);
}

internal static class CaptureEnvironmentFile
{
    internal static void LoadCurrentDirectory()
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".env");
        if (!File.Exists(path))
            return;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..]
                .Trim()
                .Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
