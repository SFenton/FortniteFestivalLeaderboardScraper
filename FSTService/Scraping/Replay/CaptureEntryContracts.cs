using System.Text.Json;

namespace FSTService.Scraping.Replay;

public sealed record CaptureSoloLeaderboardEntry(
    string AccountId,
    int Rank,
    double Percentile,
    int Score,
    int Accuracy,
    bool IsFullCombo,
    int Stars,
    int Season,
    int Difficulty,
    string? EndTime);

public sealed record CaptureBandMemberEntry(
    int MemberIndex,
    string AccountId,
    int InstrumentId,
    int Score,
    int Accuracy,
    bool IsFullCombo,
    int Stars,
    int Difficulty);

public sealed record CaptureBandLeaderboardEntry(
    string TeamKey,
    IReadOnlyList<string> TeamMembers,
    int Score,
    int? BaseScore,
    int? InstrumentBonus,
    int? OverdriveBonus,
    int Accuracy,
    bool IsFullCombo,
    int Stars,
    int Difficulty,
    int Season,
    int Rank,
    double Percentile,
    string? EndTime,
    string InstrumentCombo,
    IReadOnlyList<CaptureBandMemberEntry> MemberStats);

internal readonly record struct CaptureEntryIdentity(
    string Primary,
    string Secondary,
    int Rank);

internal static class CaptureEntryContracts
{
    internal static JsonElement Project(
        LeaderboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonSerializer.SerializeToElement(
            new CaptureSoloLeaderboardEntry(
                entry.AccountId,
                entry.Rank,
                entry.Percentile,
                entry.Score,
                entry.Accuracy,
                entry.IsFullCombo,
                entry.Stars,
                entry.Season,
                entry.Difficulty,
                entry.EndTime),
            TierZeroCanonicalJson.SerializerOptions);
    }

    internal static JsonElement Project(
        BandLeaderboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonSerializer.SerializeToElement(
            new CaptureBandLeaderboardEntry(
                entry.TeamKey,
                entry.TeamMembers,
                entry.Score,
                entry.BaseScore,
                entry.InstrumentBonus,
                entry.OverdriveBonus,
                entry.Accuracy,
                entry.IsFullCombo,
                entry.Stars,
                entry.Difficulty,
                entry.Season,
                entry.Rank,
                entry.Percentile,
                entry.EndTime,
                entry.InstrumentCombo,
                entry.MemberStats
                    .Select(static member =>
                        new CaptureBandMemberEntry(
                            member.MemberIndex,
                            member.AccountId,
                            member.InstrumentId,
                            member.Score,
                            member.Accuracy,
                            member.IsFullCombo,
                            member.Stars,
                            member.Difficulty))
                    .ToArray()),
            TierZeroCanonicalJson.SerializerOptions);
    }

    internal static CaptureEntryIdentity ValidateAndIdentify(
        CaptureResponseKind responseKind,
        string leaderboardType,
        JsonElement element,
        Action<string?, string> requireSafeText,
        Action<CapturePackageFailureKind, string> invalid)
    {
        ArgumentNullException.ThrowIfNull(requireSafeText);
        ArgumentNullException.ThrowIfNull(invalid);
        if (element.ValueKind != JsonValueKind.Object)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response entries must be JSON objects.");
        }

        try
        {
            return responseKind switch
            {
                CaptureResponseKind.SoloLeaderboardPage =>
                    ValidateSolo(
                        element.Deserialize<
                            CaptureSoloLeaderboardEntry>(
                            TierZeroCanonicalJson
                                .SerializerOptions)!,
                        element,
                        requireSafeText,
                        invalid),
                CaptureResponseKind.BandLeaderboardPage =>
                    ValidateBand(
                        element.Deserialize<
                            CaptureBandLeaderboardEntry>(
                            TierZeroCanonicalJson
                                .SerializerOptions)!,
                        leaderboardType,
                        element,
                        requireSafeText,
                        invalid),
                _ => throw new JsonException(
                    "Capture response kind is unsupported."),
            };
        }
        catch (JsonException)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response entry does not match its strict safe DTO.");
            return default;
        }
    }

    private static CaptureEntryIdentity ValidateSolo(
        CaptureSoloLeaderboardEntry entry,
        JsonElement source,
        Action<string?, string> requireSafeText,
        Action<CapturePackageFailureKind, string> invalid)
    {
        if (entry is null)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture solo response entry is null.");
            return default;
        }
        RequireExactProjection(source, entry, invalid);
        requireSafeText(
            entry.AccountId,
            "solo entry account ID");
        ValidateRank(entry.Rank, invalid);
        ValidateOptionalSafeText(
            entry.EndTime,
            "solo entry end time",
            requireSafeText);
        return new CaptureEntryIdentity(
            entry.AccountId,
            "",
            entry.Rank);
    }

    private static CaptureEntryIdentity ValidateBand(
        CaptureBandLeaderboardEntry entry,
        string leaderboardType,
        JsonElement source,
        Action<string?, string> requireSafeText,
        Action<CapturePackageFailureKind, string> invalid)
    {
        if (entry is null ||
            entry.TeamMembers is null ||
            entry.MemberStats is null)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response entry is incomplete.");
            return default;
        }
        RequireExactProjection(source, entry, invalid);
        requireSafeText(entry.TeamKey, "band entry team key");
        requireSafeText(
            entry.InstrumentCombo,
            "band entry instrument combo");
        var expectedMemberCount =
            BandInstrumentMapping.ExpectedMemberCount(
                leaderboardType);
        if (expectedMemberCount <= 0 ||
            entry.TeamMembers.Count != expectedMemberCount)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response entry does not match its leaderboard team size.");
        }
        foreach (var member in entry.TeamMembers)
        {
            requireSafeText(
                member,
                "band entry team member account ID");
        }
        if (entry.TeamMembers
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != entry.TeamMembers.Count)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response entry has duplicate team members.");
        }
        var expectedTeamKey = string.Join(
            ':',
            entry.TeamMembers.OrderBy(
                static member => member,
                StringComparer.OrdinalIgnoreCase));
        if (!string.Equals(
                entry.TeamKey,
                expectedTeamKey,
                StringComparison.OrdinalIgnoreCase))
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response entry team identity is inconsistent.");
        }

        var seenMemberIndexes = new HashSet<int>();
        var memberAccountIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var member in entry.MemberStats)
        {
            if (member.MemberIndex < 0 ||
                !seenMemberIndexes.Add(member.MemberIndex))
            {
                invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture band response entry member indexes must be unique and non-negative.");
            }
            requireSafeText(
                member.AccountId,
                "band member account ID");
            if (!memberAccountIds.Add(
                    member.AccountId))
            {
                invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture band response member identities must be unique.");
            }
            if (BandInstrumentMapping.ToLeaderboardType(
                    member.InstrumentId) is null)
            {
                invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture band response member instrument is unsupported.");
            }
        }
        if (entry.MemberStats.Count !=
                entry.TeamMembers.Count ||
            !entry.MemberStats
                .Select(static member => member.MemberIndex)
                .SequenceEqual(
                    Enumerable.Range(
                        0,
                        entry.MemberStats.Count)) ||
            !memberAccountIds.SetEquals(
                entry.TeamMembers))
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response member identities are inconsistent.");
        }
        if (entry.MemberStats.Count == 0)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response member stats are required.");
        }
        var expectedCombo = string.Join(
            ':',
            entry.MemberStats
                .Select(static member =>
                    member.InstrumentId)
                .Order());
        if (!string.Equals(
                entry.InstrumentCombo,
                expectedCombo,
                StringComparison.Ordinal))
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture band response entry instrument identity is inconsistent.");
        }

        ValidateRank(entry.Rank, invalid);
        ValidateOptionalSafeText(
            entry.EndTime,
            "band entry end time",
            requireSafeText);
        return new CaptureEntryIdentity(
            entry.TeamKey,
            entry.InstrumentCombo,
            entry.Rank);
    }

    private static void RequireExactProjection<T>(
        JsonElement source,
        T entry,
        Action<CapturePackageFailureKind, string> invalid)
    {
        var sourceBytes =
            TierZeroCanonicalJson.Serialize(source);
        var projectedBytes =
            TierZeroCanonicalJson.Serialize(entry);
        if (!sourceBytes.SequenceEqual(projectedBytes))
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response entry contains missing, unknown, or noncanonical fields.");
        }
    }

    private static void ValidateRank(
        int rank,
        Action<CapturePackageFailureKind, string> invalid)
    {
        if (rank <= 0)
        {
            invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response entry rank must be positive.");
        }
    }

    private static void ValidateOptionalSafeText(
        string? value,
        string description,
        Action<string?, string> requireSafeText)
    {
        if (value is not null)
            requireSafeText(value, description);
    }
}
