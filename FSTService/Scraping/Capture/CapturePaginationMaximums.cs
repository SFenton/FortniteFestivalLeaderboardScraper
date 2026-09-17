using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping.Replay;

namespace FSTService.Scraping.Capture;

internal sealed record CapturePaginationMaximum(
    string LeaderboardType,
    int? MaximumScore);

internal sealed record CapturePaginationSongMaximums(
    string SongId,
    IReadOnlyList<CapturePaginationMaximum> Maximums);

internal sealed record CapturePaginationMaximumsDocument(
    string FormatId,
    int Version,
    string ProviderContentSha256,
    IReadOnlyList<CapturePaginationSongMaximums> Songs);

internal sealed record CapturePaginationMaximumsSnapshot(
    string ContentSha256,
    IReadOnlyDictionary<string, SongMaxScores> Scores);

internal static class CapturePaginationMaximums
{
    internal const string FormatId =
        "fst.capture-pagination-max-scores.v1";
    internal const int Version = 1;
    private const long MaximumBytes =
        16L * 1024 * 1024;

    internal static async Task<
        CapturePaginationMaximumsSnapshot?> LoadAsync(
        string? configuredPath,
        string approvedRoot,
        CaptureCatalogArtifact catalog,
        string providerContentSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var path = Path.GetFullPath(
            configuredPath);
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(approvedRoot));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(
                root + Path.DirectorySeparatorChar,
                comparison))
        {
            throw Rejected();
        }

        try
        {
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                path,
                includeCandidate: true);
            var snapshot =
                TierZeroRegularFile.Inspect(path);
            RequireApprovedDevice(
                snapshot.DeviceIdentity,
                TierZeroRegularFile
                    .GetFileSystemDeviceIdentity(
                        root));
            if (snapshot.Length <= 0 ||
                snapshot.Length > MaximumBytes)
            {
                throw Rejected();
            }
            var bytes =
                await TierZeroRegularFile
                    .ReadAllBytesAsync(
                        path,
                        snapshot,
                        MaximumBytes,
                        cancellationToken);
            var document =
                TierZeroCanonicalJson.Deserialize<
                    CapturePaginationMaximumsDocument>(
                    bytes);
            if (!bytes.SequenceEqual(
                    TierZeroCanonicalJson.Serialize(
                        document)))
            {
                throw Rejected();
            }
            return Validate(
                document,
                catalog,
                providerContentSha256,
                bytes);
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
            ArgumentException or
            OverflowException or
            JsonException or
            TierZeroPackageException)
        {
            throw Rejected(exception);
        }
    }

    internal static void RequireApprovedDevice(
        string? inputDeviceIdentity,
        string approvedDeviceIdentity)
    {
        if (string.IsNullOrWhiteSpace(
                inputDeviceIdentity) ||
            !string.Equals(
                inputDeviceIdentity,
                approvedDeviceIdentity,
                StringComparison.Ordinal))
        {
            throw Rejected();
        }
    }

    private static CapturePaginationMaximumsSnapshot
        Validate(
        CapturePaginationMaximumsDocument document,
        CaptureCatalogArtifact catalog,
        string providerContentSha256,
        ReadOnlySpan<byte> bytes)
    {
        if (!string.Equals(
                document.FormatId,
                FormatId,
                StringComparison.Ordinal) ||
            document.Version != Version ||
            !TierZeroCanonicalJson.IsSha256(
                document.ProviderContentSha256) ||
            !string.Equals(
                document.ProviderContentSha256,
                providerContentSha256,
                StringComparison.Ordinal) ||
            document.Songs is null ||
            document.Songs.Count !=
                catalog.SongCount)
        {
            throw Rejected();
        }

        var scores =
            new Dictionary<string, SongMaxScores>(
                document.Songs.Count,
                StringComparer.Ordinal);
        for (var songIndex = 0;
             songIndex < document.Songs.Count;
             songIndex++)
        {
            var source = document.Songs[songIndex];
            if (source is null ||
                source.Maximums is null ||
                !string.Equals(
                    source.SongId,
                    catalog.Songs[songIndex].SongId,
                    StringComparison.Ordinal) ||
                source.Maximums.Count !=
                    CapturePackageFormat
                        .SoloInstrumentOrder.Count)
            {
                throw Rejected();
            }

            var values = new SongMaxScores();
            for (var instrumentIndex = 0;
                 instrumentIndex <
                     source.Maximums.Count;
                 instrumentIndex++)
            {
                var maximum =
                    source.Maximums[instrumentIndex];
                var expectedInstrument =
                    CapturePackageFormat
                        .SoloInstrumentOrder[
                            instrumentIndex];
                if (maximum is null ||
                    !string.Equals(
                        maximum.LeaderboardType,
                        expectedInstrument,
                        StringComparison.Ordinal) ||
                    maximum.MaximumScore is <= 0)
                {
                    if (maximum?.MaximumScore is null &&
                        string.Equals(
                            maximum?.LeaderboardType,
                            expectedInstrument,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    throw Rejected();
                }
                values.SetByInstrument(
                    expectedInstrument,
                    maximum.MaximumScore);
                if (values.GetByInstrument(
                        expectedInstrument) !=
                    maximum.MaximumScore)
                {
                    throw Rejected();
                }
            }
            scores.Add(source.SongId, values);
        }

        return new CapturePaginationMaximumsSnapshot(
            TierZeroCanonicalJson.Sha256Hex(bytes),
            scores);
    }

    private static CaptureOnlyException Rejected(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.CatalogRejected,
            CaptureOnlyExitCode.CatalogRejected,
            "Capture pagination maximum-score input was rejected.",
            innerException);
}
