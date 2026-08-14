using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FSTService.Scraping.Replay;

public sealed class TierZeroPackageWriter
{
    private const int BufferSize = 64 * 1024;
    private const long MaximumMetadataBytes = 4L * 1024 * 1024;
    private const long MaximumChecksumBytes = 16L * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private TierZeroPackageState _state;
    private bool _sealed;

    private TierZeroPackageWriter(
        string root,
        TierZeroPackageState state)
    {
        _root = root;
        _state = state;
    }

    public string RootPath => _root;
    public bool IsSealed => _sealed;
    public IReadOnlyList<TierZeroArtifactDescriptor> Artifacts =>
        _state.Artifacts;

    public static async Task<TierZeroPackageWriter> CreateAsync(
        string rootPath,
        TierZeroPackageDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(draft);

        var root = Path.GetFullPath(rootPath);
        TierZeroPackagePath.EnsureNoSymbolicLinkAncestors(root);
        if (Directory.Exists(root) &&
            Directory.EnumerateFileSystemEntries(root).Any(
                entry => !string.Equals(
                    Path.GetFileName(entry),
                    TierZeroEvidenceFormat.LockFileName,
                    StringComparison.Ordinal)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadyExists,
                $"Tier-0 package path already contains data: {root}");
        }

        Directory.CreateDirectory(root);
        TierZeroPackagePath.EnsureNoSymbolicLinks(
            root,
            root,
            includeCandidate: true);
        await using var packageLock = await AcquirePackageLockAsync(
            root,
            createIfMissing: true,
            cancellationToken: cancellationToken);
        if (Directory.EnumerateFileSystemEntries(root).Any(
                entry => !string.Equals(
                    Path.GetFileName(entry),
                    TierZeroEvidenceFormat.LockFileName,
                    StringComparison.Ordinal)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadyExists,
                $"Tier-0 package path already contains data: {root}");
        }

        var normalizedDraft = TierZeroPackageModel.NormalizeDraft(draft);
        var state = new TierZeroPackageState(
            normalizedDraft,
            TierZeroPhasePlan.FromCurrentCatalog(),
            [],
            null,
            TierZeroPackageStatus.Draft,
            null,
            null);
        await WriteStateAsync(
            root,
            state,
            overwrite: false,
            cancellationToken);
        return new TierZeroPackageWriter(root, state);
    }

    public static async Task<TierZeroPackageWriter> ResumeAsync(
        string rootPath,
        TierZeroResumeExpectations expectations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(expectations);

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotFound,
                $"Tier-0 package directory does not exist: {root}");
        }

        TierZeroPackagePath.EnsureNoSymbolicLinks(
            root,
            root,
            includeCandidate: true);
        if (File.Exists(Path.Combine(
                root,
                TierZeroEvidenceFormat.ManifestFileName)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadySealed,
                $"Tier-0 package is already sealed: {root}");
        }
        await using var packageLock = await AcquirePackageLockAsync(
            root,
            createIfMissing: false,
            cancellationToken: cancellationToken);
        if (File.Exists(Path.Combine(
                root,
                TierZeroEvidenceFormat.ManifestFileName)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadySealed,
                $"Tier-0 package is already sealed: {root}");
        }

        var statePath = Path.Combine(
            root,
            TierZeroEvidenceFormat.StateFileName);
        if (!File.Exists(statePath))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package has no resumable state: {root}");
        }

        var state = await ReadMetadataAsync<TierZeroPackageState>(
            root,
            statePath,
            cancellationToken);
        state = TierZeroPackageModel.NormalizeState(state);
        if (state.Status is not
            (TierZeroPackageStatus.Draft or TierZeroPackageStatus.Interrupted))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package state '{state.Status}' is not resumable.");
        }

        if (!TierZeroPackageModel.ResumeIdentityMatches(
                state,
                expectations))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.ResumeIdentityMismatch,
                "Tier-0 package immutable parent, producer, configuration, schema, or phase identity does not match.");
        }

        state = await RecoverPendingArtifactAsync(
            root,
            state,
            cancellationToken);
        await ValidateStateArtifactsAsync(
            root,
            state.Artifacts,
            cancellationToken);
        CleanupInterruptedTemporaryFiles(root, state.Artifacts);
        CleanupEmptyDirectories(root);
        ValidateResumeFileSet(root, state.Artifacts);

        var resumedState = state with
        {
            Status = TierZeroPackageStatus.Draft,
            Error = null,
            InterruptedAtUtc = null,
        };
        await WriteStateAsync(
            root,
            resumedState,
            overwrite: true,
            cancellationToken);
        return new TierZeroPackageWriter(root, resumedState);
    }

    public async Task<TierZeroArtifactDescriptor> AddArtifactAsync(
        TierZeroArtifactRegistration registration,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(
            content.ToArray(),
            writable: false);
        return await AddArtifactAsync(
            registration,
            stream,
            cancellationToken);
    }

    public async Task<TierZeroArtifactDescriptor> AddArtifactAsync(
        TierZeroArtifactRegistration registration,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw new ArgumentException("Artifact stream must be readable.", nameof(content));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureMutable();
            await using var packageLock = await AcquirePackageLockAsync(
                _root,
                createIfMissing: false,
                cancellationToken: cancellationToken);
            await RefreshStateAsync(cancellationToken);
            EnsureMutable();
            var normalized = TierZeroPackageModel.NormalizeRegistration(
                registration);
            if (_state.Artifacts.Any(
                    artifact => string.Equals(
                        artifact.Path,
                        normalized.Path,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.DuplicateArtifactPath,
                    $"Tier-0 artifact path '{normalized.Path}' is already registered.",
                    normalized.Path);
            }
            TierZeroPackagePath.ValidatePortableNamespace(
                _state.Artifacts
                    .Select(static artifact => artifact.Path)
                    .Append(normalized.Path));
            if (TierZeroPackagePath.IsReserved(normalized.Path))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.ReservedPath,
                    $"Tier-0 artifact path '{normalized.Path}' is reserved.",
                    normalized.Path);
            }

            var target = TierZeroPackagePath.ResolveUnderRoot(
                _root,
                normalized.Path);
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                _root,
                target,
                includeCandidate: true);
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.ArtifactAlreadyExists,
                    $"Tier-0 artifact path already exists: {normalized.Path}",
                    normalized.Path);
            }

            var parent = Path.GetDirectoryName(target)
                ?? throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidPath,
                    $"Tier-0 artifact has no parent path: {normalized.Path}",
                    normalized.Path);
            TierZeroRegularFile.CreateDirectoryUnderRoot(
                _root,
                GetLogicalParent(normalized.Path));
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                _root,
                parent,
                includeCandidate: true);

            ArtifactWriteResult written;
            try
            {
                written = await WriteArtifactTemporaryAsync(
                    target,
                    content,
                    cancellationToken);
            }
            catch
            {
                DeleteEmptyAncestors(_root, parent);
                throw;
            }
            var descriptor = new TierZeroArtifactDescriptor(
                normalized.LogicalOwner,
                normalized.Path,
                normalized.MediaType,
                normalized.SchemaVersion,
                normalized.RowCount,
                normalized.Ranges ?? [],
                written.Bytes,
                normalized.UncompressedBytes,
                written.Sha256);
            var pendingState = _state with
            {
                PendingArtifact = new TierZeroPendingArtifact(
                    descriptor,
                    TierZeroPackagePath.NormalizePhysicalRelativePath(
                        Path.GetRelativePath(
                            _root,
                            written.TemporaryPath))),
            };
            try
            {
                await WriteStateAsync(
                    _root,
                    pendingState,
                    overwrite: true,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                TierZeroPackageException or
                OperationCanceledException)
            {
                TierZeroRegularFile.DeleteFile(
                    written.TemporaryPath);
                DeleteEmptyAncestors(_root, parent);
                throw;
            }

            _state = pendingState;
            TierZeroRegularFile.Move(
                written.TemporaryPath,
                target,
                overwrite: false);
            var nextState = pendingState with
            {
                Artifacts = pendingState.Artifacts
                    .Append(descriptor)
                    .OrderBy(
                        static artifact => artifact.Path,
                        StringComparer.Ordinal)
                    .ToArray(),
                PendingArtifact = null,
            };
            await WriteStateAsync(
                _root,
                nextState,
                overwrite: true,
                cancellationToken);
            _state = nextState;
            return descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkInterruptedAsync(
        string error,
        DateTimeOffset? interruptedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        EnsureSafeError(error);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureMutable();
            await using var packageLock = await AcquirePackageLockAsync(
                _root,
                createIfMissing: false,
                cancellationToken: cancellationToken);
            await RefreshStateAsync(cancellationToken);
            EnsureMutable();
            var nextState = _state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = error,
                InterruptedAtUtc =
                    (interruptedAtUtc ?? DateTimeOffset.UtcNow)
                    .ToUniversalTime(),
            };
            await WriteStateAsync(
                _root,
                nextState,
                overwrite: true,
                cancellationToken);
            _state = nextState;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TierZeroEvidenceManifest> SealAsync(
        DateTimeOffset sealedAtUtc,
        TierZeroPackageStatus status = TierZeroPackageStatus.Sealed,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureMutable();
            await using var packageLock = await AcquirePackageLockAsync(
                _root,
                createIfMissing: false,
                cancellationToken: cancellationToken);
            await RefreshStateAsync(cancellationToken);
            EnsureMutable();
            if (status is not
                (TierZeroPackageStatus.Sealed or TierZeroPackageStatus.Failed))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidManifest,
                    "A sealed Tier-0 manifest must have sealed or failed status.");
            }
            if (status == TierZeroPackageStatus.Failed &&
                string.IsNullOrWhiteSpace(error))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidManifest,
                    "A failed Tier-0 manifest requires a visible error.");
            }
            if (status == TierZeroPackageStatus.Sealed &&
                !string.IsNullOrWhiteSpace(error))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidManifest,
                    "A successful sealed Tier-0 manifest cannot carry an error.");
            }
            if (!string.IsNullOrWhiteSpace(error))
                EnsureSafeError(error);

            await ValidateStateArtifactsAsync(
                _root,
                _state.Artifacts,
                cancellationToken);
            ValidateResumeFileSet(_root, _state.Artifacts);
            ValidateSummaryReferences(
                _state.Draft.SummaryReferences,
                _state.Artifacts);

            var checksumBytes = CreateChecksumManifest(_state.Artifacts);
            var checksum = new TierZeroChecksumManifest(
                TierZeroEvidenceFormat.ChecksumFileName,
                "sha256",
                _state.Artifacts.Count,
                TierZeroCanonicalJson.Sha256Hex(checksumBytes));
            var statePath = Path.Combine(
                _root,
                TierZeroEvidenceFormat.StateFileName);
            await WriteStateAsync(
                _root,
                _state,
                overwrite: true,
                cancellationToken);
            var stateSnapshot =
                TierZeroRegularFile.Inspect(statePath);
            var stateBytes = await TierZeroRegularFile.ReadAllBytesAsync(
                statePath,
                stateSnapshot,
                MaximumMetadataBytes,
                cancellationToken);
            var stateSha256 =
                TierZeroCanonicalJson.Sha256Hex(stateBytes);
            var manifest = TierZeroPackageModel.NormalizeManifest(
                new TierZeroEvidenceManifest(
                    TierZeroEvidenceFormat.FormatId,
                    TierZeroEvidenceFormat.ManifestVersion,
                    _state.Draft.PackageId,
                    _state.Draft.Source,
                    _state.Draft.Build,
                    _state.Draft.Database,
                    _state.Draft.Configuration,
                    _state.PhasePlan,
                    _state.Draft.SummaryReferences,
                    _state.Artifacts,
                    _state.Draft.ParentRootHashes,
                    _state.Draft.Attempt,
                    _state.Draft.ProducerIdentity,
                    _state.Draft.CreatedAtUtc,
                    sealedAtUtc.ToUniversalTime(),
                    status,
                    string.IsNullOrWhiteSpace(error) ? null : error,
                    stateSha256,
                    checksum,
                    null));
            var manifestBytes =
                TierZeroCanonicalJson.SerializeSealedManifest(manifest);
            if (manifestBytes.LongLength > MaximumMetadataBytes)
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidManifest,
                    "Tier-0 manifest exceeds the maximum supported metadata size.");
            }
            if (checksumBytes.LongLength > MaximumChecksumBytes)
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidManifest,
                    "Tier-0 checksum manifest exceeds the maximum supported size.");
            }
            var sealedManifest =
                TierZeroCanonicalJson.Deserialize<TierZeroEvidenceManifest>(
                    manifestBytes);

            await AtomicWriteAsync(
                Path.Combine(
                    _root,
                    TierZeroEvidenceFormat.ChecksumFileName),
                checksumBytes,
                overwrite: true,
                cancellationToken);
            var manifestPath = Path.Combine(
                _root,
                TierZeroEvidenceFormat.ManifestFileName);
            try
            {
                await AtomicWriteAsync(
                    manifestPath,
                    manifestBytes,
                    overwrite: false,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw ClassifyManifestWriteFailure(
                    _root,
                    manifestPath,
                    exception);
            }

            _sealed = true;
            return sealedManifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureMutable()
    {
        if (_sealed ||
            File.Exists(Path.Combine(
                _root,
                TierZeroEvidenceFormat.ManifestFileName)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadySealed,
                $"Tier-0 package is sealed and immutable: {_root}");
        }
    }

    internal static TierZeroPackageException ClassifyManifestWriteFailure(
        string root,
        string manifestPath,
        Exception exception) =>
        File.Exists(manifestPath)
            ? new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadySealed,
                $"Tier-0 package was sealed concurrently: {root}",
                innerException: exception)
            : new TierZeroPackageException(
                TierZeroPackageError.PackageWriteFailed,
                $"Tier-0 manifest could not be written: {root}",
                innerException: exception);

    private async Task RefreshStateAsync(
        CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(
                _root,
                TierZeroEvidenceFormat.ManifestFileName)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageAlreadySealed,
                $"Tier-0 package is sealed and immutable: {_root}");
        }

        var statePath = Path.Combine(
            _root,
            TierZeroEvidenceFormat.StateFileName);
        if (!File.Exists(statePath))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package state is missing: {_root}");
        }

        var current = TierZeroPackageModel.NormalizeState(
            await ReadMetadataAsync<TierZeroPackageState>(
                _root,
                statePath,
                cancellationToken));
        if (current.Status != TierZeroPackageStatus.Draft ||
            !TierZeroCanonicalJson.Serialize(current.Draft).AsSpan()
                .SequenceEqual(
                    TierZeroCanonicalJson.Serialize(_state.Draft)) ||
            !TierZeroCanonicalJson.Serialize(current.PhasePlan).AsSpan()
                .SequenceEqual(
                    TierZeroCanonicalJson.Serialize(_state.PhasePlan)))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.ResumeIdentityMismatch,
                "Tier-0 package state changed to an incompatible identity.");
        }

        _state = await RecoverPendingArtifactAsync(
            _root,
            current,
            cancellationToken);
    }

    private static async Task<TierZeroPackageState> RecoverPendingArtifactAsync(
        string root,
        TierZeroPackageState state,
        CancellationToken cancellationToken)
    {
        if (state.PendingArtifact is null)
            return state;

        var pending = state.PendingArtifact;
        var target = TierZeroPackagePath.ResolveUnderRoot(
            root,
            pending.Descriptor.Path);
        var temporary = TierZeroPackagePath.ResolveUnderRoot(
            root,
            pending.TemporaryPath);
        TierZeroPackagePath.EnsureNoSymbolicLinks(
            root,
            target,
            includeCandidate: true);
        TierZeroPackagePath.EnsureNoSymbolicLinks(
            root,
            temporary,
            includeCandidate: true);

        if (File.Exists(target))
        {
            await ValidatePendingFileAsync(
                target,
                pending.Descriptor,
                cancellationToken);
            if (File.Exists(temporary))
            {
                await ValidatePendingFileAsync(
                    temporary,
                    pending.Descriptor,
                    cancellationToken);
                TierZeroRegularFile.DeleteFile(temporary);
            }
        }
        else if (File.Exists(temporary))
        {
            await ValidatePendingFileAsync(
                temporary,
                pending.Descriptor,
                cancellationToken);
            var parent = Path.GetDirectoryName(target)
                ?? throw new TierZeroPackageException(
                    TierZeroPackageError.InvalidPath,
                    $"Pending Tier-0 artifact has no parent: {pending.Descriptor.Path}",
                    pending.Descriptor.Path);
            TierZeroRegularFile.CreateDirectoryUnderRoot(
                root,
                GetLogicalParent(pending.Descriptor.Path));
            TierZeroRegularFile.Move(
                temporary,
                target,
                overwrite: false);
        }
        else
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.ResumeArtifactMismatch,
                $"Pending Tier-0 artifact has neither temporary nor final content: {pending.Descriptor.Path}",
                pending.Descriptor.Path);
        }

        var recovered = state with
        {
            Artifacts = state.Artifacts
                .Append(pending.Descriptor)
                .OrderBy(
                    static artifact => artifact.Path,
                    StringComparer.Ordinal)
                .ToArray(),
            PendingArtifact = null,
        };
        await WriteStateAsync(
            root,
            recovered,
            overwrite: true,
            cancellationToken);
        return recovered;
    }

    private static async Task ValidatePendingFileAsync(
        string path,
        TierZeroArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var snapshot = TierZeroRegularFile.Inspect(path);
        if (snapshot.Length !=
                descriptor.CompressedBytes ||
            !string.Equals(
                await HashFileAsync(
                    path,
                    cancellationToken,
                    snapshot),
                descriptor.Sha256,
                StringComparison.Ordinal))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.ResumeArtifactMismatch,
                $"Pending Tier-0 artifact content does not match its journal: {descriptor.Path}",
                descriptor.Path);
        }
    }

    private static async Task<FileStream> AcquirePackageLockAsync(
        string root,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(
            root,
            TierZeroEvidenceFormat.LockFileName);
        if (!createIfMissing && !File.Exists(lockPath))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package lock is missing: {lockPath}");
        }
        IOException? lastException = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TierZeroPackagePath.EnsureNoSymbolicLinks(
                    root,
                    lockPath,
                    includeCandidate: true);
                var stream =
                    TierZeroRegularFile.OpenExclusiveLock(
                        lockPath,
                        createIfMissing);
                if (createIfMissing)
                {
                    stream.SetLength(0);
                }
                else if (stream.Length != 0)
                {
                    await stream.DisposeAsync();
                    throw new TierZeroPackageException(
                        TierZeroPackageError.ResumeArtifactMismatch,
                        $"Tier-0 package lock must remain empty: {lockPath}");
                }
                await stream.FlushAsync(cancellationToken);
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

        throw new TierZeroPackageException(
            TierZeroPackageError.PackageLockUnavailable,
            $"Tier-0 package lock could not be acquired: {lockPath}",
            innerException: lastException);
    }

    private static void EnsureSafeError(string error)
    {
        if (TierZeroConfigurationFingerprinter.IsSecretLikeValue(error))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.InvalidMetadata,
                "Tier-0 package errors cannot contain credentials, endpoints, or authorization material.");
        }
    }

    private static async Task<ArtifactWriteResult> WriteArtifactTemporaryAsync(
        string path,
        Stream content,
        CancellationToken cancellationToken)
    {
        var temporaryPath =
            $"{path}.partial-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var retained = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            long bytes = 0;
            await using (var output =
                         TierZeroRegularFile.CreateNewWrite(
                             temporaryPath,
                             BufferSize))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await content.ReadAsync(
                        buffer,
                        cancellationToken);
                    if (read == 0)
                        break;
                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    bytes += read;
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            retained = true;
            return new ArtifactWriteResult(
                temporaryPath,
                bytes,
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant());
        }
        finally
        {
            if (!retained && File.Exists(temporaryPath))
                TierZeroRegularFile.DeleteFile(temporaryPath);
        }
    }

    private static async Task AtomicWriteAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                $"Atomic output path has no parent: {path}");
        Directory.CreateDirectory(parent);
        var temporaryPath =
            $"{path}.partial-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (var stream =
                         TierZeroRegularFile.CreateNewWrite(
                             temporaryPath,
                             BufferSize))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            TierZeroRegularFile.Move(
                temporaryPath,
                path,
                overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                TierZeroRegularFile.DeleteFile(temporaryPath);
        }
    }

    private static Task WriteStateAsync(
        string root,
        TierZeroPackageState state,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var bytes = TierZeroCanonicalJson.Serialize(state);
        if (bytes.LongLength > MaximumMetadataBytes)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.InvalidMetadata,
                "Tier-0 package state exceeds the maximum supported metadata size.");
        }
        return AtomicWriteAsync(
            Path.Combine(root, TierZeroEvidenceFormat.StateFileName),
            bytes,
            overwrite,
            cancellationToken);
    }

    internal static byte[] CreateChecksumManifest(
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        var builder = new StringBuilder();
        foreach (var artifact in artifacts
                     .OrderBy(
                         static artifact => artifact.Path,
                         StringComparer.Ordinal))
        {
            builder
                .Append(artifact.Sha256)
                .Append("  ")
                .Append(artifact.Path)
                .Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void ValidateSummaryReferences(
        TierZeroSummaryReferences references,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        var byPath = artifacts.ToDictionary(
            static artifact => artifact.Path,
            StringComparer.Ordinal);
        foreach (var reference in TierZeroPackageModel.AllReferences(references))
        {
            if (!byPath.TryGetValue(reference.Path, out var artifact) ||
                !string.Equals(
                    reference.LogicalOwner,
                    artifact.LogicalOwner,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reference.Sha256,
                    artifact.Sha256,
                    StringComparison.Ordinal) ||
                (reference.RecordCount.HasValue &&
                 reference.RecordCount.Value != artifact.RowCount))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.SummaryReferenceMismatch,
                    $"Summary reference '{reference.Path}' does not match its registered artifact.",
                    reference.Path);
            }
        }
    }

    private static async Task ValidateStateArtifactsAsync(
        string root,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = TierZeroPackagePath.ResolveUnderRoot(
                root,
                artifact.Path);
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                path,
                includeCandidate: true);
            if (!File.Exists(path))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.ResumeArtifactMismatch,
                    $"Registered Tier-0 artifact is missing: {artifact.Path}",
                    artifact.Path);
            }

            var snapshot = TierZeroRegularFile.Inspect(path);
            if (snapshot.Length != artifact.CompressedBytes ||
                !string.Equals(
                    await HashFileAsync(
                        path,
                        cancellationToken,
                        snapshot),
                    artifact.Sha256,
                    StringComparison.Ordinal))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.ResumeArtifactMismatch,
                    $"Registered Tier-0 artifact changed: {artifact.Path}",
                    artifact.Path);
            }
        }
    }

    private static void ValidateResumeFileSet(
        string root,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        var allowed = artifacts
            .Select(static artifact => artifact.Path)
            .Append(TierZeroEvidenceFormat.StateFileName)
            .Append(TierZeroEvidenceFormat.ChecksumFileName)
            .Append(TierZeroEvidenceFormat.LockFileName)
            .ToHashSet(StringComparer.Ordinal);
        var inventory = TierZeroPackageFileEnumerator.Enumerate(root);
        foreach (var file in inventory.Files)
        {
            if (!allowed.Contains(file.RelativePath))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.ResumeArtifactMismatch,
                    $"Unsealed Tier-0 package contains untracked file '{file.RelativePath}'.",
                    file.RelativePath);
            }
        }
        var allowedDirectories = GetAncestorDirectories(allowed);
        foreach (var directory in inventory.Directories)
        {
            if (!allowedDirectories.Contains(directory.RelativePath))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.ResumeArtifactMismatch,
                    $"Tier-0 package contains untracked directory '{directory.RelativePath}'.",
                    directory.RelativePath);
            }
        }
    }

    private static void CleanupInterruptedTemporaryFiles(
        string root,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        var cleanupDirectories = new List<string>();
        var registered = artifacts
            .Select(static artifact => artifact.Path)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in
                 TierZeroPackageFileEnumerator.Enumerate(root).Files)
        {
            if (registered.Contains(file.RelativePath))
                continue;
            if (TierZeroPackagePath.IsTemporaryPath(
                    file.RelativePath))
            {
                TierZeroRegularFile.DeleteFile(file.FullPath);
                var parent = Path.GetDirectoryName(file.FullPath);
                if (parent is not null)
                    cleanupDirectories.Add(parent);
            }
        }
        foreach (var directory in cleanupDirectories
                     .OrderByDescending(static path => path.Length))
        {
            DeleteEmptyAncestors(root, directory);
        }
    }

    private static void DeleteEmptyAncestors(
        string root,
        string directory)
    {
        var canonicalRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(directory);
        while (!string.Equals(
                   current,
                   canonicalRoot,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal) &&
               Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any())
        {
            TierZeroRegularFile.DeleteDirectory(current);
            current = Path.GetDirectoryName(current)
                ?? canonicalRoot;
        }
    }

    private static void CleanupEmptyDirectories(string root)
    {
        foreach (var directory in
                 TierZeroPackageFileEnumerator.Enumerate(root)
                     .Directories
                     .OrderByDescending(static entry =>
                         entry.RelativePath.Count(
                             static character => character == '/'))
                     .ThenByDescending(static entry =>
                         entry.RelativePath.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(
                    directory.FullPath).Any())
            {
                TierZeroRegularFile.DeleteDirectory(
                    directory.FullPath);
            }
        }
    }

    internal static IReadOnlySet<string> GetAncestorDirectories(
        IEnumerable<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var segments = path.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                directories.Add(string.Join(
                    '/',
                    segments.Take(index)));
            }
        }
        return directories;
    }

    private static string GetLogicalParent(string normalizedPath)
    {
        var separator = normalizedPath.LastIndexOf('/');
        return separator < 0
            ? ""
            : normalizedPath[..separator];
    }

    internal static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken,
        TierZeroFileSnapshot? expected = null) =>
        await TierZeroRegularFile.HashAsync(
            path,
            expected,
            cancellationToken);

    private static async Task<T> ReadMetadataAsync<T>(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        TierZeroPackagePath.EnsureNoSymbolicLinks(
            root,
            path,
            includeCandidate: true);
        if (!File.Exists(path))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package metadata is missing: {path}");
        }
        var snapshot = TierZeroRegularFile.Inspect(path);
        if (snapshot.Length <= 0 ||
            snapshot.Length > MaximumMetadataBytes)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package metadata size is invalid: {path}");
        }

        try
        {
            return TierZeroCanonicalJson.Deserialize<T>(
                await TierZeroRegularFile.ReadAllBytesAsync(
                    path,
                    snapshot,
                    MaximumMetadataBytes,
                    cancellationToken));
        }
        catch (JsonException exception)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PackageNotResumable,
                $"Tier-0 package metadata is invalid JSON: {path}",
                innerException: exception);
        }
    }

    private sealed record ArtifactWriteResult(
        string TemporaryPath,
        long Bytes,
        string Sha256);
}

internal static class TierZeroPackageModel
{
    internal static TierZeroPackageDraft NormalizeDraft(
        TierZeroPackageDraft draft)
    {
        if (draft.Source is null ||
            draft.Build is null ||
            draft.Database is null ||
            draft.Configuration is null ||
            draft.SummaryReferences is null ||
            draft.ParentRootHashes is null)
        {
            Invalid("Tier-0 draft is missing required identity metadata.");
        }
        RequireSafeText(draft.PackageId, "package ID");
        RequireSafeText(draft.ProducerIdentity, "producer identity");
        if (draft.Attempt <= 0)
            Invalid("Tier-0 attempt must be positive.");

        return draft with
        {
            Source = NormalizeSource(draft.Source),
            Build = NormalizeBuild(draft.Build),
            Database = NormalizeDatabase(draft.Database),
            Configuration = NormalizeConfiguration(draft.Configuration),
            SummaryReferences = NormalizeReferences(draft.SummaryReferences),
            ParentRootHashes = NormalizeParents(draft.ParentRootHashes),
            CreatedAtUtc = RequireUtc(draft.CreatedAtUtc, "created timestamp"),
        };
    }

    internal static TierZeroPackageState NormalizeState(
        TierZeroPackageState state)
    {
        if (state.Draft is null ||
            state.PhasePlan is null ||
            state.Artifacts is null)
        {
            Invalid("Tier-0 package state is missing required metadata.");
        }
        if ((state.Status is TierZeroPackageStatus.Interrupted or
                TierZeroPackageStatus.Failed) &&
            string.IsNullOrWhiteSpace(state.Error))
        {
            Invalid("Interrupted or failed Tier-0 package state requires an error.");
        }
        if ((state.Status is TierZeroPackageStatus.Draft or
                TierZeroPackageStatus.Sealed) &&
            !string.IsNullOrWhiteSpace(state.Error))
        {
            Invalid("Draft or sealed Tier-0 package state cannot carry an error.");
        }
        if (!string.IsNullOrWhiteSpace(state.Error) &&
            TierZeroConfigurationFingerprinter.IsSecretLikeValue(
                state.Error))
        {
            Invalid("Tier-0 package state errors cannot contain credentials, endpoints, or authorization material.");
        }
        if (state.Status == TierZeroPackageStatus.Interrupted &&
            state.InterruptedAtUtc is null)
        {
            Invalid("Interrupted Tier-0 package state requires an interruption timestamp.");
        }
        if (state.InterruptedAtUtc is { } interruptedAt &&
            interruptedAt == default)
        {
            Invalid("Tier-0 interruption timestamp cannot be the default value.");
        }
        if (state.Status != TierZeroPackageStatus.Interrupted &&
            state.InterruptedAtUtc is not null)
        {
            Invalid("Only interrupted Tier-0 package state can carry an interruption timestamp.");
        }
        var artifacts = NormalizeArtifacts(state.Artifacts);
        TierZeroPendingArtifact? pending = null;
        if (state.PendingArtifact is not null)
        {
            if (state.Status != TierZeroPackageStatus.Draft)
                Invalid("Only draft Tier-0 package state can carry a pending artifact.");
            var descriptor =
                NormalizeArtifacts([state.PendingArtifact.Descriptor])[0];
            var temporaryPath = TierZeroPackagePath.Normalize(
                state.PendingArtifact.TemporaryPath);
            if (!TierZeroPackagePath.IsTemporaryPath(temporaryPath) ||
                !temporaryPath.StartsWith(
                    descriptor.Path + ".partial-",
                    StringComparison.Ordinal) ||
                artifacts.Any(artifact => string.Equals(
                    artifact.Path,
                    descriptor.Path,
                    StringComparison.OrdinalIgnoreCase)))
            {
                Invalid("Tier-0 pending artifact metadata is invalid.");
            }
            TierZeroPackagePath.ValidatePortableNamespace(
                artifacts
                    .Select(static artifact => artifact.Path)
                    .Append(descriptor.Path));
            pending = new TierZeroPendingArtifact(
                descriptor,
                temporaryPath);
        }
        return state with
        {
            Draft = NormalizeDraft(state.Draft),
            PhasePlan = NormalizePhasePlan(state.PhasePlan),
            Artifacts = artifacts,
            PendingArtifact = pending,
            InterruptedAtUtc =
                state.InterruptedAtUtc?.ToUniversalTime(),
        };
    }

    internal static TierZeroEvidenceManifest NormalizeManifest(
        TierZeroEvidenceManifest manifest)
    {
        if (manifest.Source is null ||
            manifest.Build is null ||
            manifest.Database is null ||
            manifest.Configuration is null ||
            manifest.PhasePlan is null ||
            manifest.SummaryReferences is null ||
            manifest.Artifacts is null ||
            manifest.ParentRootHashes is null ||
            manifest.ChecksumManifest is null)
        {
            Invalid("Tier-0 manifest is missing required metadata.");
        }
        if (!string.Equals(
                manifest.FormatId,
                TierZeroEvidenceFormat.FormatId,
                StringComparison.Ordinal) ||
            manifest.ManifestVersion != TierZeroEvidenceFormat.ManifestVersion)
        {
            Invalid("Tier-0 manifest format identity is unsupported.");
        }

        var draft = NormalizeDraft(new TierZeroPackageDraft(
            manifest.PackageId,
            manifest.Source,
            manifest.Build,
            manifest.Database,
            manifest.Configuration,
            manifest.SummaryReferences,
            manifest.ParentRootHashes,
            manifest.Attempt,
            manifest.ProducerIdentity,
            manifest.CreatedAtUtc));
        if (manifest.SealedAtUtc < draft.CreatedAtUtc)
            Invalid("Tier-0 sealed timestamp precedes created timestamp.");
        if (manifest.Status is not
            (TierZeroPackageStatus.Sealed or TierZeroPackageStatus.Failed))
        {
            Invalid("Tier-0 manifest status is not terminal.");
        }
        if (manifest.Status == TierZeroPackageStatus.Failed &&
            string.IsNullOrWhiteSpace(manifest.Error))
        {
            Invalid("Failed Tier-0 manifest requires an error.");
        }
        if (manifest.Status == TierZeroPackageStatus.Sealed &&
            !string.IsNullOrWhiteSpace(manifest.Error))
        {
            Invalid("Successful Tier-0 manifest cannot carry an error.");
        }
        if (!string.IsNullOrWhiteSpace(manifest.Error) &&
            TierZeroConfigurationFingerprinter.IsSecretLikeValue(
                manifest.Error))
        {
            Invalid("Tier-0 manifest errors cannot contain credentials, endpoints, or authorization material.");
        }
        if (!string.Equals(
                manifest.ChecksumManifest.Path,
                TierZeroEvidenceFormat.ChecksumFileName,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ChecksumManifest.Algorithm,
                "sha256",
                StringComparison.Ordinal) ||
            manifest.ChecksumManifest.EntryCount < 0 ||
            !TierZeroCanonicalJson.IsSha256(
                manifest.ChecksumManifest.Sha256))
        {
            Invalid("Tier-0 checksum manifest metadata is invalid.");
        }
        if (!TierZeroCanonicalJson.IsSha256(manifest.StateSha256))
            Invalid("Tier-0 package state hash is invalid.");
        if (manifest.PackageRootHash is not null &&
            !TierZeroCanonicalJson.IsSha256(manifest.PackageRootHash))
        {
            Invalid("Tier-0 package root hash is invalid.");
        }

        return manifest with
        {
            Source = draft.Source,
            Build = draft.Build,
            Database = draft.Database,
            Configuration = draft.Configuration,
            PhasePlan = NormalizePhasePlan(manifest.PhasePlan),
            SummaryReferences = draft.SummaryReferences,
            Artifacts = NormalizeArtifacts(manifest.Artifacts),
            ParentRootHashes = draft.ParentRootHashes,
            CreatedAtUtc = draft.CreatedAtUtc,
            SealedAtUtc = RequireUtc(
                manifest.SealedAtUtc,
                "sealed timestamp"),
            Error = string.IsNullOrWhiteSpace(manifest.Error)
                ? null
                : manifest.Error,
            StateSha256 = manifest.StateSha256.ToLowerInvariant(),
        };
    }

    internal static TierZeroArtifactRegistration NormalizeRegistration(
        TierZeroArtifactRegistration registration)
    {
        RequireSafeText(registration.LogicalOwner, "artifact logical owner");
        RequireText(registration.MediaType, "artifact media type");
        if (registration.SchemaVersion <= 0)
            Invalid("Artifact schema version must be positive.");
        if (registration.RowCount < 0)
            Invalid("Artifact row count cannot be negative.");
        if (registration.UncompressedBytes < 0)
            Invalid("Artifact uncompressed byte count cannot be negative.");

        var ranges = (registration.Ranges ?? [])
            .Select(static range =>
            {
                if (range is null)
                    Invalid("Artifact range entries cannot be null.");
                RequireText(range.Field, "artifact range field");
                if (range.Minimum is null && range.Maximum is null)
                    Invalid("Artifact range requires a minimum or maximum value.");
                ValidateRangeValue(range.Minimum);
                ValidateRangeValue(range.Maximum);
                return range;
            })
            .OrderBy(static range => range.Field, StringComparer.Ordinal)
            .ToArray();
        if (ranges.Select(static range => range.Field)
            .Distinct(StringComparer.Ordinal)
            .Count() != ranges.Length)
        {
            Invalid("Artifact range fields must be unique.");
        }

        return registration with
        {
            Path = TierZeroPackagePath.Normalize(registration.Path),
            Ranges = ranges,
        };
    }

    internal static bool ResumeIdentityMatches(
        TierZeroPackageState state,
        TierZeroResumeExpectations expectations) =>
        string.Equals(
            state.Draft.PackageId,
            expectations.PackageId,
            StringComparison.Ordinal) &&
        state.Draft.Attempt == expectations.Attempt &&
        string.Equals(
            state.Draft.ProducerIdentity,
            expectations.ProducerIdentity,
            StringComparison.Ordinal) &&
        ParentsEqual(
            state.Draft.ParentRootHashes,
            NormalizeParents(expectations.ParentRootHashes)) &&
        string.Equals(
            state.Draft.Configuration.ValuesSha256,
            expectations.ConfigurationValuesSha256,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            state.Draft.Database.SchemaFingerprint,
            expectations.DatabaseSchemaFingerprint,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            state.PhasePlan.Id,
            expectations.PhasePlanId,
            StringComparison.Ordinal) &&
        string.Equals(
            state.PhasePlan.Version,
            expectations.PhasePlanVersion,
            StringComparison.Ordinal) &&
        (!string.Equals(
             expectations.PhasePlanId,
             PhaseProgressCatalog.OperationId,
             StringComparison.Ordinal) ||
         !string.Equals(
             expectations.PhasePlanVersion,
             PhaseProgressCatalog.PlanVersion,
             StringComparison.Ordinal) ||
         state.PhasePlan.Phases.SequenceEqual(
             TierZeroPhasePlan.FromCurrentCatalog().Phases));

    internal static IEnumerable<TierZeroSummaryReference> AllReferences(
        TierZeroSummaryReferences references) =>
        references.ScopeManifests
            .Concat(references.ScopeFingerprints)
            .Concat(references.PhaseOutcomes)
            .Concat(references.PhaseTimings);

    internal static bool ParentsEqual(
        IReadOnlyList<TierZeroParentRootHash> first,
        IReadOnlyList<TierZeroParentRootHash> second) =>
        first.SequenceEqual(second);

    private static TierZeroSourceIdentity NormalizeSource(
        TierZeroSourceIdentity source)
    {
        if (source is null || source.Catalog is null)
            Invalid("Tier-0 source identity and catalog are required.");
        RequireSafeText(source.Catalog.Identity, "catalog identity");
        RequireSha(source.Catalog.ContentSha256, "catalog content hash");
        if (source.ScrapeId is < 0 || source.PublicationId is < 0)
            Invalid("Source scrape and publication IDs cannot be negative.");
        if (source.SourceCutUtc is { } sourceCut &&
            sourceCut == default)
        {
            Invalid("Source cut timestamp cannot be the default value.");
        }
        return source with
        {
            SourceCutUtc = source.SourceCutUtc?.ToUniversalTime(),
            Catalog = source.Catalog with
            {
                ContentSha256 = source.Catalog.ContentSha256.ToLowerInvariant(),
            },
        };
    }

    private static TierZeroBuildIdentity NormalizeBuild(
        TierZeroBuildIdentity build)
    {
        if (build is null)
            Invalid("Tier-0 build identity is required.");
        var imageDigest = build.OciImageDigest?.ToLowerInvariant();
        if (!IsGitCommit(build.GitCommit) ||
            !IsGitCommit(build.OciImageRevision))
        {
            Invalid("Build git commit and OCI revision must be 40- or 64-character hexadecimal identifiers.");
        }
        if (!TierZeroCanonicalJson.IsOciSha256(imageDigest))
        {
            Invalid("OCI image digest must be a sha256 digest.");
        }
        RequireText(build.ServiceVersion, "service version");
        return build with
        {
            GitCommit = build.GitCommit.ToLowerInvariant(),
            OciImageDigest = imageDigest!,
            OciImageRevision = build.OciImageRevision.ToLowerInvariant(),
        };
    }

    private static TierZeroDatabaseIdentity NormalizeDatabase(
        TierZeroDatabaseIdentity database)
    {
        if (database is null || database.Extensions is null)
            Invalid("Tier-0 database identity and extensions are required.");
        if (database.MajorVersion <= 0)
            Invalid("Database major version must be positive.");
        RequireSha(database.SchemaFingerprint, "database schema fingerprint");
        var extensions = database.Extensions
            .Select(extension =>
            {
                RequireText(extension, "database extension");
                return extension;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static extension => extension, StringComparer.Ordinal)
            .ToArray();
        if (extensions.Length != database.Extensions.Count)
            Invalid("Database extensions must be unique.");
        return database with
        {
            Extensions = extensions,
            SchemaFingerprint =
                database.SchemaFingerprint.ToLowerInvariant(),
        };
    }

    private static TierZeroConfigurationFingerprint NormalizeConfiguration(
        TierZeroConfigurationFingerprint configuration)
    {
        if (configuration is null || configuration.Keys is null)
            Invalid("Tier-0 configuration fingerprint and keys are required.");
        if (configuration.Keys.Count == 0)
            Invalid("Tier-0 configuration fingerprint requires a named allowlist.");
        if (!string.Equals(
                configuration.Algorithm,
                TierZeroConfigurationFingerprinter.Algorithm,
                StringComparison.Ordinal))
        {
            Invalid("Tier-0 configuration fingerprint algorithm is unsupported.");
        }
        RequireSha(
            configuration.ValuesSha256,
            "configuration values hash");
        var keys = configuration.Keys
            .Select(key =>
            {
                RequireText(key, "configuration key");
                if (TierZeroConfigurationFingerprinter.IsSecretLikeKey(key))
                    Invalid("Configuration fingerprint keys cannot be secret-like.");
                return key;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        if (keys.Length != configuration.Keys.Count)
            Invalid("Configuration fingerprint keys must be unique.");
        return configuration with
        {
            Keys = keys,
            ValuesSha256 =
                configuration.ValuesSha256.ToLowerInvariant(),
        };
    }

    private static TierZeroSummaryReferences NormalizeReferences(
        TierZeroSummaryReferences references)
    {
        if (references is null ||
            references.ScopeManifests is null ||
            references.ScopeFingerprints is null ||
            references.PhaseOutcomes is null ||
            references.PhaseTimings is null)
        {
            Invalid("Tier-0 summary reference collections are required.");
        }
        return new TierZeroSummaryReferences(
            NormalizeReferenceList(references.ScopeManifests),
            NormalizeReferenceList(references.ScopeFingerprints),
            NormalizeReferenceList(references.PhaseOutcomes),
            NormalizeReferenceList(references.PhaseTimings));
    }

    private static IReadOnlyList<TierZeroSummaryReference> NormalizeReferenceList(
        IReadOnlyList<TierZeroSummaryReference> references)
    {
        var normalized = references
            .Select(reference =>
            {
                if (reference is null)
                    Invalid("Summary reference entries cannot be null.");
                RequireSafeText(reference.LogicalOwner, "summary logical owner");
                RequireSha(reference.Sha256, "summary hash");
                if (reference.RecordCount is < 0)
                    Invalid("Summary record count cannot be negative.");
                return reference with
                {
                    Path = TierZeroPackagePath.Normalize(reference.Path),
                    Sha256 = reference.Sha256.ToLowerInvariant(),
                };
            })
            .OrderBy(static reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(static reference => reference.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != normalized.Length)
        {
            Invalid("Summary reference paths must be unique within each category.");
        }
        return normalized;
    }

    internal static IReadOnlyList<TierZeroParentRootHash> NormalizeParents(
        IReadOnlyList<TierZeroParentRootHash> parents)
    {
        if (parents is null)
            Invalid("Tier-0 parent root hash collection is required.");
        if (parents.Count == 0)
            Invalid("Tier-0 package requires at least one immutable parent root hash.");
        var normalized = parents
            .Select(parent =>
            {
                if (parent is null)
                    Invalid("Parent root hash entries cannot be null.");
                RequireSafeText(parent.LogicalParent, "parent logical identity");
                RequireSha(parent.Sha256, "parent root hash");
                return parent with
                {
                    Sha256 = parent.Sha256.ToLowerInvariant(),
                };
            })
            .OrderBy(
                static parent => parent.LogicalParent,
                StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(static parent => parent.LogicalParent)
            .Distinct(StringComparer.Ordinal)
            .Count() != normalized.Length)
        {
            Invalid("Parent logical identities must be unique.");
        }
        return normalized;
    }

    private static TierZeroPhasePlan NormalizePhasePlan(
        TierZeroPhasePlan plan)
    {
        if (plan is null || plan.Phases is null)
            Invalid("Tier-0 phase plan and descriptors are required.");
        RequireText(plan.Id, "phase plan ID");
        RequireText(plan.Version, "phase plan version");
        var phases = plan.Phases
            .Select(phase =>
            {
                if (phase is null)
                    Invalid("Phase descriptor entries cannot be null.");
                RequireText(phase.Id, "phase ID");
                RequireText(phase.Label, "phase label");
                RequireText(phase.LegacyPhase, "legacy phase");
                RequireOptionalText(
                    phase.TrackerOperation,
                    "phase tracker operation");
                RequireOptionalText(
                    phase.BranchId,
                    "phase branch ID");
                RequireOptionalText(
                    phase.OperationKey,
                    "phase operation key");
                RequireOptionalText(
                    phase.DefaultUnitsKind,
                    "phase default units kind");
                if (phase.Ordinal <= 0)
                    Invalid("Phase ordinals must be positive.");
                return phase;
            })
            .OrderBy(static phase => phase.Ordinal)
            .ThenBy(static phase => phase.Id, StringComparer.Ordinal)
            .ToArray();
        if (phases.Select(static phase => phase.Id)
            .Distinct(StringComparer.Ordinal)
            .Count() != phases.Length)
        {
            Invalid("Phase IDs must be unique.");
        }
        if (phases.Select(static phase => phase.Ordinal)
            .Distinct()
            .Count() != phases.Length)
        {
            Invalid("Phase ordinals must be unique.");
        }
        return plan with { Phases = phases };
    }

    private static IReadOnlyList<TierZeroArtifactDescriptor> NormalizeArtifacts(
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        if (artifacts is null)
            Invalid("Tier-0 artifact collection is required.");
        var normalized = artifacts
            .Select(artifact =>
            {
                if (artifact is null)
                    Invalid("Artifact descriptor entries cannot be null.");
                var registration = NormalizeRegistration(
                    new TierZeroArtifactRegistration(
                        artifact.LogicalOwner,
                        artifact.Path,
                        artifact.MediaType,
                        artifact.SchemaVersion,
                        artifact.RowCount,
                        artifact.UncompressedBytes,
                        artifact.Ranges));
                if (TierZeroPackagePath.IsReserved(registration.Path))
                    Invalid("Artifact paths cannot use a reserved package namespace.");
                if (artifact.CompressedBytes < 0)
                    Invalid("Artifact compressed byte count cannot be negative.");
                RequireSha(artifact.Sha256, "artifact hash");
                return artifact with
                {
                    LogicalOwner = registration.LogicalOwner,
                    Path = registration.Path,
                    MediaType = registration.MediaType,
                    SchemaVersion = registration.SchemaVersion,
                    RowCount = registration.RowCount,
                    Ranges = registration.Ranges ?? [],
                    UncompressedBytes = registration.UncompressedBytes,
                    Sha256 = artifact.Sha256.ToLowerInvariant(),
                };
            })
            .OrderBy(static artifact => artifact.Path, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(static artifact => artifact.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != normalized.Length)
        {
            Invalid("Artifact paths must be unique after normalization.");
        }
        TierZeroPackagePath.ValidatePortableNamespace(
            normalized.Select(static artifact => artifact.Path));
        return normalized;
    }

    private static DateTimeOffset RequireUtc(
        DateTimeOffset value,
        string description)
    {
        if (value == default)
            Invalid($"Tier-0 {description} is required.");
        return value.ToUniversalTime();
    }

    private static void RequireText(
        string? value,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            Invalid($"Tier-0 {description} must be a non-empty printable string.");
        }
    }

    private static void RequireSafeText(
        string? value,
        string description)
    {
        RequireText(value, description);
        if (TierZeroConfigurationFingerprinter.IsSecretLikeValue(value))
        {
            Invalid($"Tier-0 {description} cannot contain credentials, endpoints, or authorization material.");
        }
    }

    private static void RequireOptionalText(
        string? value,
        string description)
    {
        if (value is not null)
            RequireText(value, description);
    }

    private static void ValidateRangeValue(string? value)
    {
        if (value is null)
            return;
        if (value.Any(char.IsControl) ||
            TierZeroConfigurationFingerprinter.IsSecretLikeValue(value))
        {
            Invalid("Tier-0 artifact range values must be safe printable metadata.");
        }
    }

    private static void RequireSha(
        string? value,
        string description)
    {
        if (!TierZeroCanonicalJson.IsSha256(value?.ToLowerInvariant()))
            Invalid($"Tier-0 {description} must be a SHA-256 hash.");
    }

    private static bool IsGitCommit(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new TierZeroPackageException(
            TierZeroPackageError.InvalidMetadata,
            message);
}

internal sealed record TierZeroPackageFile(
    string RelativePath,
    string FullPath,
    TierZeroFileSnapshot Snapshot)
{
    internal long Length => Snapshot.Length;
}

internal sealed record TierZeroPackageDirectory(
    string RelativePath,
    string FullPath);

internal sealed record TierZeroPackageInventory(
    IReadOnlyList<TierZeroPackageFile> Files,
    IReadOnlyList<TierZeroPackageDirectory> Directories);

internal static class TierZeroPackageFileEnumerator
{
    internal static TierZeroPackageInventory Enumerate(
        string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var files = new List<TierZeroPackageFile>();
        var packageDirectories = new List<TierZeroPackageDirectory>();
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.SymbolicLinkDetected,
                    $"Tier-0 package contains symbolic-link directory '{directory.FullName}'.");
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new TierZeroPackageException(
                        TierZeroPackageError.SymbolicLinkDetected,
                        $"Tier-0 package contains symbolic link '{entry.FullName}'.");
                }

                if (entry is DirectoryInfo child)
                {
                    packageDirectories.Add(new TierZeroPackageDirectory(
                        TierZeroPackagePath.NormalizePhysicalRelativePath(
                            Path.GetRelativePath(root, child.FullName)),
                        child.FullName));
                    directories.Push(child);
                    continue;
                }

                var file = (FileInfo)entry;
                var relative = Path.GetRelativePath(root, file.FullName);
                files.Add(new TierZeroPackageFile(
                    TierZeroPackagePath.NormalizePhysicalRelativePath(
                        relative),
                    file.FullName,
                    TierZeroRegularFile.Inspect(file.FullName)));
            }
        }

        var orderedFiles = files
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var orderedDirectories = packageDirectories
            .OrderBy(
                static directory => directory.RelativePath,
                StringComparer.Ordinal)
            .ToArray();
        TierZeroPackagePath.ValidatePortableNamespace(
            orderedFiles
                .Select(static file => file.RelativePath)
                .Concat(orderedDirectories.Select(
                    static directory =>
                        directory.RelativePath +
                        "/<fst-directory-sentinel>")));
        return new TierZeroPackageInventory(
            orderedFiles,
            orderedDirectories);
    }
}
