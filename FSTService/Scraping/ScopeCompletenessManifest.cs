using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FSTService.Scraping;

public enum ScopeTerminalBoundaryKind
{
    None,
    EpicEmpty,
    EpicForbidden,
    Unsupported,
}

public sealed record ScopeCompletenessRecord(
    string SongId,
    string Instrument,
    ScopeCompletenessManifest Manifest);

public sealed class ScopeCompletenessManifest
{
    public int ExpectedFirstPage { get; init; }
    public int ExpectedLastPage { get; init; }
    public IReadOnlyList<int> ReceivedPages { get; init; } = [];
    public IReadOnlyDictionary<int, string> PageStatuses { get; init; } =
        new Dictionary<int, string>();
    public ScopeTerminalBoundaryKind TerminalBoundary { get; init; }
    public int? TerminalBoundaryPage { get; init; }
    public string ParseStatus { get; init; } = "complete";
    public bool RetryExhausted { get; init; }
    public long ReportedTotalEntries { get; init; }
    public int ReportedTotalPages { get; init; }
    public int? DeepStartPage { get; init; }
    public int? DeepEndPage { get; init; }
    public string ContentFingerprint { get; init; } = "";
    public string CoverageFingerprint { get; init; } = "";
    public bool IsComplete { get; init; }
    public string? FailureReason { get; init; }

    public static ScopeCompletenessManifest Unsupported(
        IReadOnlyList<LeaderboardEntry> entries)
    {
        var statuses = new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>();
        return Create(
            expectedFirstPage: 0,
            expectedLastPage: -1,
            statuses,
            entries,
            reportedTotalPages: 0,
            terminalBoundary: ScopeTerminalBoundaryKind.Unsupported);
    }

    public static ScopeCompletenessManifest Create(
        int expectedFirstPage,
        int expectedLastPage,
        IReadOnlyDictionary<int, GlobalLeaderboardScraper.FetchStatus> statuses,
        IReadOnlyList<LeaderboardEntry> entries,
        int reportedTotalPages,
        ScopeTerminalBoundaryKind terminalBoundary = ScopeTerminalBoundaryKind.None,
        int? terminalBoundaryPage = null,
        int? deepStartPage = null,
        int? deepEndPage = null,
        string? contentFingerprintOverride = null,
        long? reportedTotalEntriesOverride = null)
    {
        var normalizedStatuses = statuses
            .OrderBy(static pair => pair.Key)
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToString().ToLowerInvariant());
        var receivedPages = statuses
            .Where(static pair => pair.Value == GlobalLeaderboardScraper.FetchStatus.Success)
            .Select(static pair => pair.Key)
            .Order()
            .ToArray();
        var parseFailed = statuses.Values.Any(
            static status => status == GlobalLeaderboardScraper.FetchStatus.ParseFailure);
        var retryExhausted = statuses.Values.Any(
            static status => status == GlobalLeaderboardScraper.FetchStatus.RetryExhausted);

        var failures = new List<string>();
        var complete = terminalBoundary == ScopeTerminalBoundaryKind.Unsupported;

        if (!complete)
        {
            if (!statuses.TryGetValue(expectedFirstPage, out var firstStatus)
                || firstStatus != GlobalLeaderboardScraper.FetchStatus.Success)
            {
                failures.Add($"page {expectedFirstPage} was not received successfully");
            }

            var requiredLastPage = expectedLastPage;
            if (terminalBoundary == ScopeTerminalBoundaryKind.EpicForbidden
                && terminalBoundaryPage.HasValue)
            {
                requiredLastPage = Math.Min(requiredLastPage, terminalBoundaryPage.Value - 1);
            }

            for (var page = expectedFirstPage; page <= requiredLastPage; page++)
            {
                if (!statuses.TryGetValue(page, out var status)
                    || status != GlobalLeaderboardScraper.FetchStatus.Success)
                {
                    failures.Add($"expected page {page} was not received");
                }
            }

            foreach (var (page, status) in statuses)
            {
                if (page < expectedFirstPage || page > expectedLastPage)
                    continue;

                if (status == GlobalLeaderboardScraper.FetchStatus.Success)
                    continue;

                if (terminalBoundary == ScopeTerminalBoundaryKind.EpicForbidden
                    && terminalBoundaryPage.HasValue
                    && page >= terminalBoundaryPage.Value
                    && status == GlobalLeaderboardScraper.FetchStatus.Forbidden)
                {
                    continue;
                }

                failures.Add($"page {page} ended with {status}");
            }

            if (terminalBoundary == ScopeTerminalBoundaryKind.EpicForbidden
                && (!terminalBoundaryPage.HasValue
                    || terminalBoundaryPage.Value <= expectedFirstPage))
            {
                failures.Add("Epic forbidden boundary was not preceded by a complete page range");
            }

            complete = failures.Count == 0;
        }

        var reportedEntries = reportedTotalEntriesOverride ?? reportedTotalPages switch
        {
            <= 0 => entries.Count,
            <= 100 => entries.Count,
            _ => (long)reportedTotalPages * 100,
        };
        var contentFingerprint = contentFingerprintOverride
            ?? ComputeContentFingerprint(entries);
        var coverageFingerprint = ComputeCoverageFingerprint(
            expectedFirstPage,
            expectedLastPage,
            receivedPages,
            normalizedStatuses,
            terminalBoundary,
            terminalBoundaryPage,
            parseFailed ? "failed" : terminalBoundary == ScopeTerminalBoundaryKind.Unsupported
                ? "not_applicable"
                : "complete",
            retryExhausted,
            reportedEntries,
            reportedTotalPages,
            deepStartPage,
            deepEndPage,
            complete);

        return new ScopeCompletenessManifest
        {
            ExpectedFirstPage = expectedFirstPage,
            ExpectedLastPage = expectedLastPage,
            ReceivedPages = receivedPages,
            PageStatuses = normalizedStatuses,
            TerminalBoundary = terminalBoundary,
            TerminalBoundaryPage = terminalBoundaryPage,
            ParseStatus = parseFailed
                ? "failed"
                : terminalBoundary == ScopeTerminalBoundaryKind.Unsupported
                    ? "not_applicable"
                    : "complete",
            RetryExhausted = retryExhausted,
            ReportedTotalEntries = reportedEntries,
            ReportedTotalPages = reportedTotalPages,
            DeepStartPage = deepStartPage,
            DeepEndPage = deepEndPage,
            ContentFingerprint = contentFingerprint,
            CoverageFingerprint = coverageFingerprint,
            IsComplete = complete,
            FailureReason = failures.Count == 0
                ? null
                : string.Join("; ", failures.Distinct(StringComparer.Ordinal)),
        };
    }

    public static ScopeCompletenessManifest Merge(
        ScopeCompletenessManifest wave1,
        ScopeCompletenessManifest deep,
        IReadOnlyList<LeaderboardEntry> mergedEntries)
    {
        var statuses = new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>();
        AddStatuses(wave1.PageStatuses, statuses);
        AddStatuses(deep.PageStatuses, statuses);

        var terminalBoundary = deep.TerminalBoundary != ScopeTerminalBoundaryKind.None
            ? deep.TerminalBoundary
            : wave1.TerminalBoundary;
        var terminalBoundaryPage = deep.TerminalBoundaryPage ?? wave1.TerminalBoundaryPage;

        return Create(
            Math.Min(wave1.ExpectedFirstPage, deep.ExpectedFirstPage),
            Math.Max(wave1.ExpectedLastPage, deep.ExpectedLastPage),
            statuses,
            mergedEntries,
            Math.Max(wave1.ReportedTotalPages, deep.ReportedTotalPages),
            terminalBoundary,
            terminalBoundaryPage,
            deep.DeepStartPage ?? wave1.DeepStartPage,
            deep.DeepEndPage ?? wave1.DeepEndPage);
    }

    private static void AddStatuses(
        IReadOnlyDictionary<int, string> source,
        Dictionary<int, GlobalLeaderboardScraper.FetchStatus> destination)
    {
        foreach (var (page, status) in source)
        {
            if (Enum.TryParse<GlobalLeaderboardScraper.FetchStatus>(
                    status,
                    ignoreCase: true,
                    out var parsed))
            {
                destination[page] = parsed;
            }
        }
    }

    private static string ComputeContentFingerprint(
        IReadOnlyList<LeaderboardEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries
                     .OrderBy(static entry => entry.AccountId, StringComparer.Ordinal)
                     .ThenByDescending(static entry => entry.Score)
                     .ThenBy(static entry => entry.Rank))
        {
            Append(hash, entry.AccountId);
            Append(hash, entry.Score);
            Append(hash, entry.Accuracy);
            Append(hash, entry.IsFullCombo);
            Append(hash, entry.Stars);
            Append(hash, entry.Season);
            Append(hash, entry.Difficulty);
            Append(hash, entry.Percentile);
            Append(hash, entry.Rank);
            Append(hash, entry.ApiRank);
            Append(hash, entry.EndTime);
            Append(hash, entry.Source);
            Append(hash, entry.BandScore);
            Append(hash, entry.BaseScore);
            Append(hash, entry.InstrumentBonus);
            Append(hash, entry.OverdriveBonus);
            Append(hash, entry.InstrumentCombo);
            if (entry.BandMembers is not null)
            {
                foreach (var member in entry.BandMembers
                             .OrderBy(static member => member.MemberIndex)
                             .ThenBy(static member => member.AccountId, StringComparer.Ordinal))
                {
                    Append(hash, member.MemberIndex);
                    Append(hash, member.AccountId);
                    Append(hash, member.InstrumentId);
                    Append(hash, member.Score);
                    Append(hash, member.Accuracy);
                    Append(hash, member.IsFullCombo);
                    Append(hash, member.Stars);
                    Append(hash, member.Difficulty);
                }
            }
            Append(hash, null);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeCoverageFingerprint(
        int expectedFirstPage,
        int expectedLastPage,
        IReadOnlyList<int> receivedPages,
        IReadOnlyDictionary<int, string> statuses,
        ScopeTerminalBoundaryKind terminalBoundary,
        int? terminalBoundaryPage,
        string parseStatus,
        bool retryExhausted,
        long reportedTotalEntries,
        int reportedTotalPages,
        int? deepStartPage,
        int? deepEndPage,
        bool isComplete)
    {
        var value = string.Join(
            '\u001f',
            expectedFirstPage,
            expectedLastPage,
            string.Join(',', receivedPages),
            string.Join(',', statuses.Select(static pair => $"{pair.Key}:{pair.Value}")),
            terminalBoundary,
            terminalBoundaryPage?.ToString(CultureInfo.InvariantCulture) ?? "",
            parseStatus,
            retryExhausted,
            reportedTotalEntries,
            reportedTotalPages,
            deepStartPage?.ToString(CultureInfo.InvariantCulture) ?? "",
            deepEndPage?.ToString(CultureInfo.InvariantCulture) ?? "",
            isComplete);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, object? value)
    {
        var text = value switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData([0x1f]);
    }
}
