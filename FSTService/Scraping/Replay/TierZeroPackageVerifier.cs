using System.Text;
using System.Text.Json;

namespace FSTService.Scraping.Replay;

public enum TierZeroVerificationFailureKind
{
    PackageNotFound,
    UnsealedPackage,
    ManifestMissing,
    ManifestUnreadable,
    ManifestNotCanonical,
    UnsupportedFormat,
    InvalidManifest,
    RootHashMismatch,
    ChecksumMissing,
    ChecksumUnreadable,
    ChecksumHashMismatch,
    ChecksumContentMismatch,
    InvalidPath,
    DuplicatePath,
    SymbolicLinkDetected,
    MissingFile,
    ExtraFile,
    ExtraDirectory,
    ArtifactSizeMismatch,
    ArtifactHashMismatch,
    SummaryReferenceMismatch,
    StateMissing,
    StateMismatch,
    LockMissing,
    LockMismatch,
    PackageChangedDuringVerification,
    ParentMismatch,
    ConfigurationMismatch,
    SchemaMismatch,
    PhasePlanMismatch,
}

public sealed record TierZeroVerificationFailure(
    TierZeroVerificationFailureKind Kind,
    string Message,
    string? Path = null);

public sealed record TierZeroVerificationResult(
    TierZeroEvidenceManifest? Manifest,
    IReadOnlyList<TierZeroVerificationFailure> Failures)
{
    public bool IsValid => Failures.Count == 0;
}

public static class TierZeroPackageVerifier
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private const long MaximumChecksumBytes = 16L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<TierZeroVerificationResult> VerifyAsync(
        string rootPath,
        TierZeroVerificationExpectations? expectations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var failures = new List<TierZeroVerificationFailure>();
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.PackageNotFound,
                $"Tier-0 package directory does not exist: {root}"));
            return new TierZeroVerificationResult(null, failures);
        }

        TierZeroPackageInventory inventory;
        try
        {
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                root,
                includeCandidate: true);
            inventory = TierZeroPackageFileEnumerator.Enumerate(root);
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                exception.Error switch
                {
                    TierZeroPackageError.SymbolicLinkDetected =>
                        TierZeroVerificationFailureKind.SymbolicLinkDetected,
                    TierZeroPackageError.DuplicateArtifactPath =>
                        TierZeroVerificationFailureKind.DuplicatePath,
                    _ => TierZeroVerificationFailureKind.InvalidPath,
                },
                exception.Message,
                exception.LogicalPath));
            return new TierZeroVerificationResult(null, failures);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ManifestUnreadable,
                $"Tier-0 package cannot be enumerated: {exception.Message}"));
            return new TierZeroVerificationResult(null, failures);
        }
        var byPath = inventory.Files.ToDictionary(
            static file => file.RelativePath,
            StringComparer.Ordinal);
        if (!byPath.TryGetValue(
                TierZeroEvidenceFormat.ManifestFileName,
                out var manifestFile))
        {
            failures.Add(new TierZeroVerificationFailure(
                byPath.ContainsKey(TierZeroEvidenceFormat.StateFileName)
                    ? TierZeroVerificationFailureKind.UnsealedPackage
                    : TierZeroVerificationFailureKind.ManifestMissing,
                byPath.ContainsKey(TierZeroEvidenceFormat.StateFileName)
                    ? "Tier-0 package is unsealed or interrupted."
                    : "Tier-0 package manifest is missing.",
                TierZeroEvidenceFormat.ManifestFileName));
            return new TierZeroVerificationResult(null, failures);
        }
        if (manifestFile.Length <= 0 ||
            manifestFile.Length > MaximumManifestBytes)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ManifestUnreadable,
                "Tier-0 manifest size is invalid.",
                TierZeroEvidenceFormat.ManifestFileName));
            return new TierZeroVerificationResult(null, failures);
        }

        byte[] manifestBytes;
        TierZeroEvidenceManifest parsed;
        try
        {
            manifestBytes = await TierZeroRegularFile.ReadAllBytesAsync(
                manifestFile.FullPath,
                manifestFile.Snapshot,
                MaximumManifestBytes,
                cancellationToken);
            parsed = TierZeroCanonicalJson.Deserialize<TierZeroEvidenceManifest>(
                manifestBytes);
        }
        catch (JsonException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ManifestUnreadable,
                $"Tier-0 manifest JSON is invalid: {exception.Message}",
                TierZeroEvidenceFormat.ManifestFileName));
            return new TierZeroVerificationResult(null, failures);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ManifestUnreadable,
                $"Tier-0 manifest cannot be read: {exception.Message}",
                TierZeroEvidenceFormat.ManifestFileName));
            return new TierZeroVerificationResult(null, failures);
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                exception.Error == TierZeroPackageError.SymbolicLinkDetected
                    ? TierZeroVerificationFailureKind.SymbolicLinkDetected
                    : TierZeroVerificationFailureKind.ManifestUnreadable,
                exception.Message,
                TierZeroEvidenceFormat.ManifestFileName));
            return new TierZeroVerificationResult(null, failures);
        }

        FindDuplicateOrInvalidPaths(parsed, failures);
        TierZeroEvidenceManifest manifest;
        try
        {
            manifest = TierZeroPackageModel.NormalizeManifest(parsed);
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                string.Equals(
                    parsed.FormatId,
                    TierZeroEvidenceFormat.FormatId,
                    StringComparison.Ordinal) &&
                parsed.ManifestVersion == TierZeroEvidenceFormat.ManifestVersion
                    ? TierZeroVerificationFailureKind.InvalidManifest
                    : TierZeroVerificationFailureKind.UnsupportedFormat,
                exception.Message,
                exception.LogicalPath));
            return new TierZeroVerificationResult(parsed, failures);
        }

        var canonicalBytes = TierZeroCanonicalJson.Serialize(manifest);
        if (!manifestBytes.AsSpan().SequenceEqual(canonicalBytes))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ManifestNotCanonical,
                "Tier-0 manifest bytes are not the canonical representation.",
                TierZeroEvidenceFormat.ManifestFileName));
        }
        var expectedRoot =
            TierZeroCanonicalJson.ComputeManifestRootHash(manifest);
        if (!string.Equals(
                expectedRoot,
                manifest.PackageRootHash,
                StringComparison.Ordinal))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.RootHashMismatch,
                "Tier-0 package root hash does not match the canonical manifest.",
                TierZeroEvidenceFormat.ManifestFileName));
        }

        await VerifyChecksumAsync(
            manifest,
            byPath,
            failures,
            cancellationToken);
        await VerifyArtifactsAsync(
            manifest,
            byPath,
            failures,
            cancellationToken);
        await VerifyStateAsync(
            root,
            manifest,
            byPath,
            failures,
            cancellationToken);
        VerifyLockFile(byPath, failures);
        VerifySummaryReferences(manifest, failures);
        VerifyExpectations(manifest, expectations, failures);
        VerifyExtraFiles(manifest, inventory, failures);
        VerifyStableFinalInventory(root, inventory, failures);

        return new TierZeroVerificationResult(manifest, failures);
    }

    internal static void VerifyStableFinalInventory(
        string root,
        TierZeroPackageInventory initial,
        List<TierZeroVerificationFailure> failures)
    {
        try
        {
            var final = TierZeroPackageFileEnumerator.Enumerate(root);
            if (!initial.Files.SequenceEqual(final.Files) ||
                !initial.Directories.SequenceEqual(final.Directories))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.PackageChangedDuringVerification,
                    "Tier-0 package filesystem entries changed during verification."));
            }
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.PackageChangedDuringVerification,
                $"Tier-0 package changed during final inventory: {exception.Message}",
                exception.LogicalPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.PackageChangedDuringVerification,
                $"Tier-0 package final inventory failed: {exception.Message}"));
        }
    }

    private static void FindDuplicateOrInvalidPaths(
        TierZeroEvidenceManifest manifest,
        List<TierZeroVerificationFailure> failures)
    {
        var normalized = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = new List<string>();
        foreach (var artifact in manifest.Artifacts ?? [])
        {
            if (artifact is null)
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.InvalidManifest,
                    "Tier-0 manifest contains a null artifact descriptor."));
                continue;
            }
            try
            {
                var path = TierZeroPackagePath.Normalize(artifact.Path);
                if (!normalized.Add(path))
                {
                    failures.Add(new TierZeroVerificationFailure(
                        TierZeroVerificationFailureKind.DuplicatePath,
                        $"Tier-0 manifest contains duplicate normalized artifact path '{path}'.",
                        path));
                }
                else
                {
                    normalizedPaths.Add(path);
                }
            }
            catch (TierZeroPackageException exception)
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.InvalidPath,
                    exception.Message,
                    artifact.Path));
            }
        }
        try
        {
            TierZeroPackagePath.ValidatePortableNamespace(
                normalizedPaths);
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.DuplicatePath,
                exception.Message,
                exception.LogicalPath));
        }
    }

    private static async Task VerifyChecksumAsync(
        TierZeroEvidenceManifest manifest,
        IReadOnlyDictionary<string, TierZeroPackageFile> files,
        List<TierZeroVerificationFailure> failures,
        CancellationToken cancellationToken)
    {
        if (!files.TryGetValue(
                TierZeroEvidenceFormat.ChecksumFileName,
                out var checksumFile))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumMissing,
                "Tier-0 checksum manifest is missing.",
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }
        if (checksumFile.Length < 0 ||
            checksumFile.Length > MaximumChecksumBytes)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumUnreadable,
                "Tier-0 checksum manifest size is invalid.",
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await TierZeroRegularFile.ReadAllBytesAsync(
                checksumFile.FullPath,
                checksumFile.Snapshot,
                MaximumChecksumBytes,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumUnreadable,
                $"Tier-0 checksum manifest cannot be read: {exception.Message}",
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                exception.Error == TierZeroPackageError.SymbolicLinkDetected
                    ? TierZeroVerificationFailureKind.SymbolicLinkDetected
                    : TierZeroVerificationFailureKind.ChecksumUnreadable,
                exception.Message,
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }

        if (!string.Equals(
                TierZeroCanonicalJson.Sha256Hex(bytes),
                manifest.ChecksumManifest.Sha256,
                StringComparison.Ordinal))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumHashMismatch,
                "Tier-0 checksum manifest hash does not match the manifest.",
                TierZeroEvidenceFormat.ChecksumFileName));
        }

        IReadOnlyDictionary<string, string> parsed;
        try
        {
            parsed = ParseChecksums(bytes);
        }
        catch (FormatException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumContentMismatch,
                exception.Message,
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }
        catch (DecoderFallbackException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumUnreadable,
                $"Tier-0 checksum manifest is not UTF-8: {exception.Message}",
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumContentMismatch,
                exception.Message,
                exception.LogicalPath ??
                TierZeroEvidenceFormat.ChecksumFileName));
            return;
        }

        var canonicalChecksum =
            TierZeroPackageWriter.CreateChecksumManifest(
                manifest.Artifacts);
        if (!bytes.AsSpan().SequenceEqual(canonicalChecksum))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumContentMismatch,
                "Tier-0 checksum manifest bytes are not canonical.",
                TierZeroEvidenceFormat.ChecksumFileName));
        }
        if (parsed.Count != manifest.ChecksumManifest.EntryCount ||
            parsed.Count != manifest.Artifacts.Count)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ChecksumContentMismatch,
                "Tier-0 checksum entry count does not match the manifest.",
                TierZeroEvidenceFormat.ChecksumFileName));
        }
        foreach (var artifact in manifest.Artifacts)
        {
            if (!parsed.TryGetValue(artifact.Path, out var hash) ||
                !string.Equals(hash, artifact.Sha256, StringComparison.Ordinal))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.ChecksumContentMismatch,
                    $"Tier-0 checksum entry does not match artifact '{artifact.Path}'.",
                    artifact.Path));
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseChecksums(
        byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.Length > 0 && !text.EndsWith('\n'))
            throw new FormatException(
                "Tier-0 checksum manifest must end with a newline.");

        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var previousPath = "";
        foreach (var line in text.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 67 ||
                line[64] != ' ' ||
                line[65] != ' ')
            {
                throw new FormatException(
                    "Tier-0 checksum manifest contains a malformed line.");
            }

            var hash = line[..64];
            var path = TierZeroPackagePath.Normalize(line[66..]);
            if (!TierZeroCanonicalJson.IsSha256(hash) ||
                !result.TryAdd(path, hash))
            {
                throw new FormatException(
                    $"Tier-0 checksum manifest contains invalid or duplicate path '{path}'.");
            }
            if (string.CompareOrdinal(previousPath, path) >= 0 &&
                previousPath.Length > 0)
            {
                throw new FormatException(
                    "Tier-0 checksum entries are not in canonical path order.");
            }
            previousPath = path;
        }

        return result;
    }

    private static async Task VerifyArtifactsAsync(
        TierZeroEvidenceManifest manifest,
        IReadOnlyDictionary<string, TierZeroPackageFile> files,
        List<TierZeroVerificationFailure> failures,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in manifest.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(artifact.Path, out var file))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.MissingFile,
                    $"Tier-0 artifact is missing: {artifact.Path}",
                    artifact.Path));
                continue;
            }

            if (file.Length != artifact.CompressedBytes)
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.ArtifactSizeMismatch,
                    $"Tier-0 artifact size changed: {artifact.Path}",
                    artifact.Path));
            }
            try
            {
                var hash = await TierZeroPackageWriter.HashFileAsync(
                    file.FullPath,
                    cancellationToken,
                    file.Snapshot);
                if (!string.Equals(
                        hash,
                        artifact.Sha256,
                        StringComparison.Ordinal))
                {
                    failures.Add(new TierZeroVerificationFailure(
                        TierZeroVerificationFailureKind.ArtifactHashMismatch,
                        $"Tier-0 artifact hash changed: {artifact.Path}",
                        artifact.Path));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.ArtifactHashMismatch,
                    $"Tier-0 artifact cannot be hashed: {exception.Message}",
                    artifact.Path));
            }
            catch (TierZeroPackageException exception)
            {
                failures.Add(new TierZeroVerificationFailure(
                    exception.Error == TierZeroPackageError.SymbolicLinkDetected
                        ? TierZeroVerificationFailureKind.SymbolicLinkDetected
                        : TierZeroVerificationFailureKind.ArtifactHashMismatch,
                    exception.Message,
                    artifact.Path));
            }
        }
    }

    private static void VerifySummaryReferences(
        TierZeroEvidenceManifest manifest,
        List<TierZeroVerificationFailure> failures)
    {
        var artifacts = manifest.Artifacts.ToDictionary(
            static artifact => artifact.Path,
            StringComparer.Ordinal);
        foreach (var reference in TierZeroPackageModel.AllReferences(
                     manifest.SummaryReferences))
        {
            if (!artifacts.TryGetValue(reference.Path, out var artifact) ||
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
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.SummaryReferenceMismatch,
                    $"Tier-0 summary reference does not match artifact '{reference.Path}'.",
                    reference.Path));
            }
        }
    }

    private static async Task VerifyStateAsync(
        string root,
        TierZeroEvidenceManifest manifest,
        IReadOnlyDictionary<string, TierZeroPackageFile> files,
        List<TierZeroVerificationFailure> failures,
        CancellationToken cancellationToken)
    {
        if (!files.TryGetValue(
                TierZeroEvidenceFormat.StateFileName,
                out var stateFile))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.StateMissing,
                "Tier-0 package state journal is missing.",
                TierZeroEvidenceFormat.StateFileName));
            return;
        }
        if (stateFile.Length <= 0 ||
            stateFile.Length > MaximumManifestBytes)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.StateMismatch,
                "Tier-0 package state journal size is invalid.",
                TierZeroEvidenceFormat.StateFileName));
            return;
        }

        try
        {
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                stateFile.FullPath,
                includeCandidate: true);
            var stateBytes = await TierZeroRegularFile.ReadAllBytesAsync(
                stateFile.FullPath,
                stateFile.Snapshot,
                MaximumManifestBytes,
                cancellationToken);
            if (!string.Equals(
                    TierZeroCanonicalJson.Sha256Hex(stateBytes),
                    manifest.StateSha256,
                    StringComparison.Ordinal))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.StateMismatch,
                    "Tier-0 package state journal hash does not match the manifest.",
                    TierZeroEvidenceFormat.StateFileName));
            }
            var state = TierZeroPackageModel.NormalizeState(
                TierZeroCanonicalJson.Deserialize<TierZeroPackageState>(
                    stateBytes));
            if (state.Status != TierZeroPackageStatus.Draft ||
                state.Error is not null ||
                state.InterruptedAtUtc is not null ||
                state.PendingArtifact is not null)
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.StateMismatch,
                    "Sealed Tier-0 package state must be a clean pre-seal draft journal.",
                    TierZeroEvidenceFormat.StateFileName));
            }
            if (!stateBytes.AsSpan().SequenceEqual(
                    TierZeroCanonicalJson.Serialize(state)))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.StateMismatch,
                    "Tier-0 package state journal bytes are not canonical.",
                    TierZeroEvidenceFormat.StateFileName));
            }
            var manifestDraft = new TierZeroPackageDraft(
                manifest.PackageId,
                manifest.Source,
                manifest.Build,
                manifest.Database,
                manifest.Configuration,
                manifest.SummaryReferences,
                manifest.ParentRootHashes,
                manifest.Attempt,
                manifest.ProducerIdentity,
                manifest.CreatedAtUtc);
            if (!TierZeroCanonicalJson.Serialize(state.Draft).AsSpan()
                    .SequenceEqual(
                        TierZeroCanonicalJson.Serialize(manifestDraft)) ||
                !TierZeroCanonicalJson.Serialize(state.PhasePlan).AsSpan()
                    .SequenceEqual(
                        TierZeroCanonicalJson.Serialize(manifest.PhasePlan)) ||
                !TierZeroCanonicalJson.Serialize(state.Artifacts).AsSpan()
                    .SequenceEqual(
                        TierZeroCanonicalJson.Serialize(manifest.Artifacts)))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.StateMismatch,
                    "Tier-0 package state journal does not match the sealed manifest.",
                    TierZeroEvidenceFormat.StateFileName));
            }
        }
        catch (JsonException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.StateMismatch,
                $"Tier-0 package state journal is invalid JSON: {exception.Message}",
                TierZeroEvidenceFormat.StateFileName));
        }
        catch (TierZeroPackageException exception)
        {
            failures.Add(new TierZeroVerificationFailure(
                exception.Error == TierZeroPackageError.SymbolicLinkDetected
                    ? TierZeroVerificationFailureKind.SymbolicLinkDetected
                    : TierZeroVerificationFailureKind.StateMismatch,
                exception.Message,
                TierZeroEvidenceFormat.StateFileName));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.StateMismatch,
                $"Tier-0 package state journal cannot be read: {exception.Message}",
                TierZeroEvidenceFormat.StateFileName));
        }
    }

    private static void VerifyLockFile(
        IReadOnlyDictionary<string, TierZeroPackageFile> files,
        List<TierZeroVerificationFailure> failures)
    {
        if (!files.TryGetValue(
                TierZeroEvidenceFormat.LockFileName,
                out var lockFile))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.LockMissing,
                "Tier-0 package lock file is missing.",
                TierZeroEvidenceFormat.LockFileName));
            return;
        }
        if (lockFile.Length != 0)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.LockMismatch,
                "Tier-0 package lock file must remain empty.",
                TierZeroEvidenceFormat.LockFileName));
        }
    }

    private static void VerifyExpectations(
        TierZeroEvidenceManifest manifest,
        TierZeroVerificationExpectations? expectations,
        List<TierZeroVerificationFailure> failures)
    {
        if (expectations is null)
            return;

        if (expectations.ParentRootHashes is not null)
        {
            IReadOnlyList<TierZeroParentRootHash> expected;
            try
            {
                expected = TierZeroPackageModel.NormalizeParents(
                    expectations.ParentRootHashes);
            }
            catch (TierZeroPackageException)
            {
                expected = expectations.ParentRootHashes;
            }
            if (!TierZeroPackageModel.ParentsEqual(
                    manifest.ParentRootHashes,
                    expected))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.ParentMismatch,
                    "Tier-0 parent root hashes do not match expectations."));
            }
        }
        if (expectations.ConfigurationValuesSha256 is not null &&
            !string.Equals(
                manifest.Configuration.ValuesSha256,
                expectations.ConfigurationValuesSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.ConfigurationMismatch,
                "Tier-0 configuration fingerprint does not match expectations."));
        }
        if (expectations.DatabaseSchemaFingerprint is not null &&
            !string.Equals(
                manifest.Database.SchemaFingerprint,
                expectations.DatabaseSchemaFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.SchemaMismatch,
                "Tier-0 database schema fingerprint does not match expectations."));
        }
        var phaseMismatch =
            (expectations.PhasePlanId is not null &&
             !string.Equals(
                 manifest.PhasePlan.Id,
                 expectations.PhasePlanId,
                 StringComparison.Ordinal)) ||
            (expectations.PhasePlanVersion is not null &&
             !string.Equals(
                 manifest.PhasePlan.Version,
                 expectations.PhasePlanVersion,
                 StringComparison.Ordinal));
        if (!phaseMismatch &&
            string.Equals(
                expectations.PhasePlanId,
                PhaseProgressCatalog.OperationId,
                StringComparison.Ordinal) &&
            string.Equals(
                expectations.PhasePlanVersion,
                PhaseProgressCatalog.PlanVersion,
                StringComparison.Ordinal))
        {
            var current = TierZeroPhasePlan.FromCurrentCatalog();
            phaseMismatch = !manifest.PhasePlan.Phases.SequenceEqual(
                current.Phases);
        }
        if (phaseMismatch)
        {
            failures.Add(new TierZeroVerificationFailure(
                TierZeroVerificationFailureKind.PhasePlanMismatch,
                "Tier-0 phase plan identity does not match expectations."));
        }
    }

    private static void VerifyExtraFiles(
        TierZeroEvidenceManifest manifest,
        TierZeroPackageInventory inventory,
        List<TierZeroVerificationFailure> failures)
    {
        var allowed = manifest.Artifacts
            .Select(static artifact => artifact.Path)
            .Append(TierZeroEvidenceFormat.ManifestFileName)
            .Append(TierZeroEvidenceFormat.ChecksumFileName)
            .Append(TierZeroEvidenceFormat.StateFileName)
            .Append(TierZeroEvidenceFormat.LockFileName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in inventory.Files)
        {
            if (!allowed.Contains(file.RelativePath))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.ExtraFile,
                    $"Tier-0 package contains untracked file '{file.RelativePath}'.",
                    file.RelativePath));
            }
        }
        var allowedDirectories =
            TierZeroPackageWriter.GetAncestorDirectories(allowed);
        foreach (var directory in inventory.Directories)
        {
            if (!allowedDirectories.Contains(directory.RelativePath))
            {
                failures.Add(new TierZeroVerificationFailure(
                    TierZeroVerificationFailureKind.ExtraDirectory,
                    $"Tier-0 package contains untracked directory '{directory.RelativePath}'.",
                    directory.RelativePath));
            }
        }
    }
}
