using System.Text;
using FSTService.Scraping.Replay;

namespace FSTService.Scraping.Capture;

internal interface ICaptureStorageProbe
{
    long GetAvailableFreeSpace(string approvedRoot);

    int CountRetainedSealedPackages(
        string approvedRoot,
        int stopAfter,
        CancellationToken cancellationToken);

    long GetPackageBytes(
        string packageRoot,
        CancellationToken cancellationToken);
}

internal sealed class CaptureFileSystemStorageProbe
    : ICaptureStorageProbe
{
    public long GetAvailableFreeSpace(string approvedRoot) =>
        ReplayRootAdmission.GetAvailableFreeSpace(
            approvedRoot);

    public int CountRetainedSealedPackages(
        string approvedRoot,
        int stopAfter,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            stopAfter,
            1);
        var count = 0;
        foreach (var directory in
                 Directory.EnumerateDirectories(
                     approvedRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = Path.Combine(
                directory,
                TierZeroEvidenceFormat.ManifestFileName);
            var captureManifest = Path.Combine(
                directory,
                CapturePackageFormat.ManifestPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            if (!File.Exists(manifest) ||
                !File.Exists(captureManifest))
            {
                continue;
            }

            TierZeroPackagePath.EnsureNoSymbolicLinks(
                approvedRoot,
                manifest,
                includeCandidate: false);
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                approvedRoot,
                captureManifest,
                includeCandidate: false);
            count++;
            if (count >= stopAfter)
                return count;
        }
        return count;
    }

    public long GetPackageBytes(
        string packageRoot,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var inventory = TierZeroPackageFileEnumerator.Enumerate(
            packageRoot,
            CapturePackageFormat.MaximumPackageFileSystemEntries,
            cancellationToken);
        foreach (var file in inventory.Files)
            total = checked(total + file.Length);
        return total;
    }
}

internal sealed record AdmittedCapturePath(
    string ApprovedRoot,
    string OutputPackage);

internal sealed class CaptureRootAdmission
{
    internal const long SealWorkingSpaceAllowanceBytes =
        4L * 1024 * 1024;
    internal const long SealFinalPackageMetadataAllowanceBytes =
        20L * 1024 * 1024;
    internal const long PreMetadataFinalPackageAllowanceBytes =
        24L * 1024 * 1024;
    internal const string AdmissionLockFileName =
        ".capture-admission.lock";

    private static readonly string[] ProductionPrefixes =
    [
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/capture",
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/capture",
    ];

    private readonly CaptureRootPolicyOptions _options;
    private readonly ReplayRootAdmission _pathAdmission;
    private readonly ICaptureStorageProbe _storage;

    internal CaptureRootAdmission(
        CaptureRootPolicyOptions options,
        ICaptureStorageProbe storage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        _options = options;
        _storage = storage;

        try
        {
            _pathAdmission = new ReplayRootAdmission(
                new ReplayRootPolicyOptions(
                    options.ApprovedRoot,
                    TestOnly: true,
                    RollbackReserveBytes: 0));
        }
        catch (ReplayException exception)
        {
            throw RootRejected(exception);
        }

        if (!options.TestOnly &&
            !ProductionPrefixes.Any(prefix =>
                IsWithin(
                    _pathAdmission.ApprovedRoot,
                    Path.GetFullPath(prefix))))
        {
            throw RootRejected();
        }

        if (!options.TestOnly &&
            string.IsNullOrWhiteSpace(
                options.ExpectedFileSystemDevice))
        {
            throw RootRejected();
        }

        if (!string.IsNullOrWhiteSpace(
                options.ExpectedFileSystemDevice) &&
            !string.Equals(
                ReplayRootAdmission
                    .GetFileSystemDeviceIdentity(
                        _pathAdmission.ApprovedRoot),
                options.ExpectedFileSystemDevice,
                StringComparison.Ordinal))
        {
            throw RootRejected();
        }

        EnsureNotInsideTierZeroPackage(
            _pathAdmission.ApprovedRoot);
    }

    internal AdmittedCapturePath AdmitOutput(
        string outputPath)
    {
        string output;
        try
        {
            output = _pathAdmission
                .AdmitNewOutputPackage(outputPath);
        }
        catch (ReplayException exception)
        {
            throw RootRejected(exception);
        }

        var parent = Path.TrimEndingDirectorySeparator(
            Path.GetDirectoryName(output)
            ?? throw RootRejected());
        if (!string.Equals(
                parent,
                _pathAdmission.ApprovedRoot,
                PathComparison))
        {
            throw RootRejected();
        }

        return new AdmittedCapturePath(
            _pathAdmission.ApprovedRoot,
            output);
    }

    internal void Preflight(
        AdmittedCapturePath path,
        CapturePackageStoragePolicy policy,
        CancellationToken cancellationToken,
        long transientScratchBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        long remainingBytes;
        try
        {
            remainingBytes = checked(
                policy.MaximumPackageBytes +
                SealWorkingSpaceAllowanceBytes +
                transientScratchBytes);
        }
        catch (OverflowException exception)
        {
            throw AdmissionRejected(exception);
        }
        Evaluate(
            path,
            policy,
            currentPackageBytes: 0,
            finalPackageBytes:
                policy.MaximumPackageBytes,
            remainingBytesToWrite:
                remainingBytes,
            cancellationToken);
    }

    internal async Task<CaptureSealAdmission>
        AcquireCaptureAdmissionAsync(
        AdmittedCapturePath path,
        CapturePackageStoragePolicy policy,
        CancellationToken cancellationToken,
        long transientScratchBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(path);

        FileStream? rootLock = null;
        try
        {
            rootLock = await AcquireAdmissionLockAsync(
                path.ApprovedRoot,
                cancellationToken);
            EnsureNotInsideTierZeroPackage(
                path.ApprovedRoot);
            var admission = new CaptureSealAdmission(
                path,
                policy,
                _storage,
                rootLock);
            Preflight(
                path,
                policy,
                cancellationToken,
                transientScratchBytes);
            return admission;
        }
        catch (OperationCanceledException)
        {
            if (rootLock is not null)
                await rootLock.DisposeAsync();
            throw;
        }
        catch (CaptureOnlyException)
        {
            if (rootLock is not null)
                await rootLock.DisposeAsync();
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException or
            TierZeroPackageException)
        {
            if (rootLock is not null)
                await rootLock.DisposeAsync();
            throw AdmissionRejected(exception);
        }
    }

    private static async Task<FileStream>
        AcquireAdmissionLockAsync(
            string approvedRoot,
            CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(
            approvedRoot,
            AdmissionLockFileName);
        IOException? lastException = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TierZeroPackagePath.EnsureNoSymbolicLinks(
                    approvedRoot,
                    lockPath,
                    includeCandidate: true);
                var stream =
                    TierZeroRegularFile.OpenExclusiveLock(
                        lockPath,
                        createIfMissing: true);
                if (stream.Length != 0)
                {
                    await stream.DisposeAsync();
                    throw new TierZeroPackageException(
                        TierZeroPackageError.InvalidMetadata,
                        "Capture admission lock must remain empty.");
                }
                await stream.FlushAsync(
                    cancellationToken);
                return stream;
            }
            catch (IOException exception)
            {
                lastException = exception;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    cancellationToken);
            }
        }

        throw new IOException(
            "Capture root admission lock is unavailable.",
            lastException);
    }

    internal static void EnsureNotInsideTierZeroPackage(
        string approvedRoot)
    {
        for (var directory =
                 new DirectoryInfo(approvedRoot);
             directory is not null;
             directory = directory.Parent)
        {
            foreach (var marker in new[]
                     {
                         TierZeroEvidenceFormat
                             .ManifestFileName,
                         TierZeroEvidenceFormat
                             .ChecksumFileName,
                         TierZeroEvidenceFormat
                             .StateFileName,
                         TierZeroEvidenceFormat
                             .LockFileName,
                     })
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        marker)))
                {
                    throw RootRejected();
                }
            }
        }
    }

    internal void Evaluate(
        AdmittedCapturePath path,
        CapturePackageStoragePolicy policy,
        long currentPackageBytes,
        long finalPackageBytes,
        long remainingBytesToWrite,
        CancellationToken cancellationToken)
    {
        try
        {
            var retained = _storage
                .CountRetainedSealedPackages(
                    path.ApprovedRoot,
                    policy.MaximumRetainedSealedPackages,
                    cancellationToken);
            var decision = CapturePackageStorageAdmission.Evaluate(
                policy,
                new CapturePackageStorageState(
                    currentPackageBytes,
                    finalPackageBytes,
                    remainingBytesToWrite,
                    _storage.GetAvailableFreeSpace(
                        path.ApprovedRoot),
                    retained));
            if (!decision.IsAdmitted)
                throw AdmissionRejected();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException or
            TierZeroPackageException or
            ArgumentOutOfRangeException)
        {
            throw AdmissionRejected(exception);
        }
    }

    private static bool IsWithin(
        string path,
        string root)
    {
        var canonicalPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
                .Normalize(NormalizationForm.FormC));
        var canonicalRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root)
                .Normalize(NormalizationForm.FormC));
        return canonicalPath.Equals(
                   canonicalRoot,
                   PathComparison) ||
               canonicalPath.StartsWith(
                   canonicalRoot +
                   Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static CaptureOnlyException RootRejected(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.RootRejected,
            CaptureOnlyExitCode.RootRejected,
            "Capture root or output path was rejected.",
            innerException);

    private static CaptureOnlyException AdmissionRejected(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.AdmissionRejected,
            CaptureOnlyExitCode.AdmissionRejected,
            "Capture storage admission was rejected.",
            innerException);
}

internal static class CaptureScratchPath
{
    internal const string DirectoryName =
        ".capture-curl-scratch";

    internal static string Validate(
        string configuredPath,
        string approvedRoot,
        string? outputPackage = null)
    {
        if (string.IsNullOrWhiteSpace(
                configuredPath) ||
            !Path.IsPathFullyQualified(
                configuredPath))
        {
            throw Usage();
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(approvedRoot));
        var scratch = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configuredPath));
        var expected = Path.Combine(
            root,
            DirectoryName);
        if (!string.Equals(
                scratch,
                expected,
                PathComparison) ||
            File.Exists(scratch))
        {
            throw Usage();
        }

        var existing = Directory.Exists(scratch)
            ? scratch
            : Path.GetDirectoryName(scratch);
        if (string.IsNullOrWhiteSpace(existing) ||
            !Directory.Exists(existing))
        {
            throw Usage();
        }
        try
        {
            TierZeroPackagePath
                .EnsureNoSymbolicLinkAncestors(existing);
            CaptureRootAdmission
                .EnsureNotInsideTierZeroPackage(scratch);
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            TierZeroPackageException)
        {
            throw Usage(exception);
        }

        if (outputPackage is not null)
        {
            var output = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(outputPackage));
            if (string.Equals(
                    output,
                    scratch,
                    PathComparison) ||
                output.StartsWith(
                    scratch +
                    Path.DirectorySeparatorChar,
                    PathComparison) ||
                scratch.StartsWith(
                    output +
                    Path.DirectorySeparatorChar,
                    PathComparison))
            {
                throw Usage();
            }
        }
        return scratch;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static CaptureOnlyException Usage(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.Usage,
            CaptureOnlyExitCode.Usage,
            "Capture scratch configuration is invalid.",
            innerException);
}

internal sealed class CaptureSealAdmission
    : IAsyncDisposable
{
    private readonly AdmittedCapturePath _path;
    private readonly CapturePackageStoragePolicy _policy;
    private readonly ICaptureStorageProbe _storage;
    private FileStream? _rootLock;

    internal CaptureSealAdmission(
        AdmittedCapturePath path,
        CapturePackageStoragePolicy policy,
        ICaptureStorageProbe storage,
        FileStream rootLock)
    {
        _path = path;
        _policy = policy;
        _storage = storage;
        _rootLock = rootLock;
    }

    internal void RecheckBeforeMetadata(
        long metadataBytes,
        CancellationToken cancellationToken)
    {
        if (metadataBytes < 0)
            throw AdmissionRejected();
        var current = GetPackageBytes(
            cancellationToken);
        long finalPackageBytes;
        long remainingBytes;
        try
        {
            finalPackageBytes = checked(
                current +
                metadataBytes +
                CaptureRootAdmission
                    .PreMetadataFinalPackageAllowanceBytes);
            remainingBytes = checked(
                metadataBytes +
                CaptureRootAdmission
                    .PreMetadataFinalPackageAllowanceBytes +
                CaptureRootAdmission
                    .SealWorkingSpaceAllowanceBytes);
        }
        catch (OverflowException exception)
        {
            throw AdmissionRejected(exception);
        }
        Evaluate(
            current,
            finalPackageBytes,
            remainingBytes,
            cancellationToken);
    }

    internal void RecheckBeforeSeal(
        CancellationToken cancellationToken)
    {
        var current = GetPackageBytes(
            cancellationToken);
        long finalPackageBytes;
        try
        {
            finalPackageBytes = checked(
                current +
                CaptureRootAdmission
                    .SealFinalPackageMetadataAllowanceBytes);
        }
        catch (OverflowException exception)
        {
            throw AdmissionRejected(exception);
        }
        Evaluate(
            current,
            finalPackageBytes,
            checked(
                CaptureRootAdmission
                    .SealFinalPackageMetadataAllowanceBytes +
                CaptureRootAdmission
                    .SealWorkingSpaceAllowanceBytes),
            cancellationToken);
    }

    private long GetPackageBytes(
        CancellationToken cancellationToken)
    {
        try
        {
            return _storage.GetPackageBytes(
                _path.OutputPackage,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException or
            TierZeroPackageException)
        {
            throw AdmissionRejected(exception);
        }
    }

    private void Evaluate(
        long currentPackageBytes,
        long finalPackageBytes,
        long remainingBytesToWrite,
        CancellationToken cancellationToken)
    {
        try
        {
            var retained =
                _storage.CountRetainedSealedPackages(
                    _path.ApprovedRoot,
                    _policy
                        .MaximumRetainedSealedPackages,
                    cancellationToken);
            var decision =
                CapturePackageStorageAdmission.Evaluate(
                    _policy,
                    new CapturePackageStorageState(
                        currentPackageBytes,
                        finalPackageBytes,
                        remainingBytesToWrite,
                        _storage.GetAvailableFreeSpace(
                            _path.ApprovedRoot),
                        retained));
            if (!decision.IsAdmitted)
                throw AdmissionRejected();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException or
            TierZeroPackageException or
            ArgumentOutOfRangeException)
        {
            throw AdmissionRejected(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var rootLock = Interlocked.Exchange(
            ref _rootLock,
            null);
        if (rootLock is not null)
            await rootLock.DisposeAsync();
    }

    private static CaptureOnlyException AdmissionRejected(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.AdmissionRejected,
            CaptureOnlyExitCode.AdmissionRejected,
            "Capture storage admission was rejected.",
            innerException);
}

internal static class CaptureResponseShardCapacity
{
    internal static void EnsureAdmittedBudgetFits(
        int shardBytes,
        long maximumPackageBytes)
    {
        long shardCapacity;
        long responseBudget;
        try
        {
            shardCapacity = checked(
                (long)shardBytes *
                CapturePackageFormat
                    .MaximumResponseShards);
            responseBudget = checked(
                maximumPackageBytes -
                CaptureRootAdmission
                    .PreMetadataFinalPackageAllowanceBytes);
        }
        catch (OverflowException exception)
        {
            throw AdmissionRejected(exception);
        }

        if (shardBytes <=
                CapturePackageFormat
                    .MaximumResponseRecordBytes ||
            shardBytes >
                CapturePackageFormat
                    .MaximumResponseShardBytes ||
            responseBudget <= 0 ||
            shardCapacity < responseBudget)
        {
            throw AdmissionRejected();
        }
    }

    private static CaptureOnlyException AdmissionRejected(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.AdmissionRejected,
            CaptureOnlyExitCode.AdmissionRejected,
            "Capture response shard geometry cannot contain the admitted package budget.",
            innerException);
}
