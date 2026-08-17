using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Computes leaderboard rivals for registered users. For each instrument and rank method,
/// finds the ±N ranked neighbors, then compares shared songs to produce rivalry data
/// identical in shape to per-song rivals.
/// </summary>
public sealed class LeaderboardRivalsCalculator
{
    /// <summary>Supported rank methods for neighborhood queries.</summary>
    internal static readonly string[] RankMethods = ["totalscore", "adjusted", "weighted", "fcrate", "maxscore"];

    /// <summary>Maximum song samples to store per rival per instrument/method.</summary>
    internal const int MaxSamplesPerRival = 200;

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _meta;
    private readonly int _radius;
    private readonly ILogger<LeaderboardRivalsCalculator> _log;

    internal Action<string, int>?
        MaintenanceProfileBatchReadTestHook { get; set; }

    public LeaderboardRivalsCalculator(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase meta,
        IOptions<ScraperOptions> options,
        ILogger<LeaderboardRivalsCalculator> log)
    {
        _persistence = persistence;
        _meta = meta;
        _radius = options.Value.LeaderboardRivalRadius;
        _log = log;
    }

    /// <summary>
    /// Compute leaderboard rivals for a single instrument and rank method without persisting.
    /// Used by the stateless read path and scrape-time precompute.
    /// </summary>
    public LeaderboardInstrumentRivalsResult ComputeInstrument(string userId, string instrument, string rankMethod)
    {
        return ComputeInstrument(userId, instrument, new[] { rankMethod });
    }

    /// <summary>
    /// Compute leaderboard rivals for a single user across all instruments and rank methods.
    /// </summary>
    public LeaderboardRivalsResult ComputeForUser(
        string userId,
        bool rankingsAuthoritative = false)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        int totalRivals = 0;
        int totalSamples = 0;

        foreach (var instrument in instrumentKeys)
        {
            var instrumentResult = ComputeInstrument(userId, instrument, RankMethods);

            if (instrumentResult.UserFound
                || !instrumentResult.HasUserScores
                || rankingsAuthoritative)
            {
                _meta.ReplaceLeaderboardRivalsData(
                    userId,
                    instrument,
                    instrumentResult.Rivals,
                    instrumentResult.Samples,
                    instrumentResult.CompletedRankMethods,
                    instrumentResult.UserRanks);
                totalRivals += instrumentResult.Rivals.Count;
                totalSamples += instrumentResult.Samples.Count;
            }
            else
            {
                _log.LogWarning(
                    "Preserving leaderboard rivals for {User}/{Instrument}: scores exist but AccountRankings has no user row.",
                    userId,
                    instrument);
            }
        }

        return new LeaderboardRivalsResult
        {
            RivalCount = totalRivals,
            SampleCount = totalSamples,
        };
    }

    internal async Task<LeaderboardRivalsResult>
        ComputeForUsersForMaxScoreMaintenanceAsync(
            IReadOnlyCollection<string> userIds,
            IReadOnlyCollection<string> instruments,
            IMaxScoreMaintenanceLease maintenanceLease,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(maintenanceLease);
        var normalizedUserIds =
            MaxScoreMaintenanceAccountIdPolicy
                .NormalizeSet(userIds);
        var instrumentKeys = instruments
            .Where(static instrument =>
                !string.IsNullOrWhiteSpace(instrument))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var totalRivals = 0;
        var totalSamples = 0;
        var totalPairs =
            normalizedUserIds.Length * instrumentKeys.Length;
        var completedPairs = 0;

        foreach (var instrument in instrumentKeys)
        {
            ct.ThrowIfCancellationRequested();
            var stopwatch =
                System.Diagnostics.Stopwatch.StartNew();
            _log.LogInformation(
                "Max-score leaderboard-rivals batch starting for {Instrument}: registeredUsers={UserCount:N0}.",
                instrument,
                normalizedUserIds.Length);
            var batch = ComputeInstrumentBatch(
                normalizedUserIds,
                instrument);
            _log.LogInformation(
                "Max-score leaderboard-rivals batch loaded {RankingCount:N0} rankings and {ProfileAccountCount:N0} user/neighbor profiles ({ScoreCount:N0} scores) for {Instrument}.",
                batch.RankingCount,
                batch.ProfileAccountCount,
                batch.ScoreCount,
                instrument);

            foreach (var userId in normalizedUserIds)
            {
                ct.ThrowIfCancellationRequested();
                var instrumentResult = batch.Results[userId];
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-leaderboard-rivals:{userId}:{instrument}",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        _meta.ReplaceLeaderboardRivalsData(
                            userId,
                            instrument,
                            instrumentResult.Rivals,
                            instrumentResult.Samples,
                            instrumentResult.CompletedRankMethods,
                            instrumentResult.UserRanks,
                            connection,
                            transaction);
                        return Task.CompletedTask;
                    },
                    ct: ct);
                totalRivals += instrumentResult.Rivals.Count;
                totalSamples += instrumentResult.Samples.Count;
                completedPairs++;
                if (completedPairs == totalPairs
                    || completedPairs % 10 == 0)
                {
                    _log.LogInformation(
                        "Max-score leaderboard-rivals progress: {CompletedPairs:N0}/{TotalPairs:N0} user/instrument pairs persisted.",
                        completedPairs,
                        totalPairs);
                }
            }

            stopwatch.Stop();
            _log.LogInformation(
                "Max-score leaderboard-rivals batch completed for {Instrument}: users={UserCount:N0}, rivals={RivalCount:N0}, samples={SampleCount:N0}, elapsedSeconds={ElapsedSeconds:F1}.",
                instrument,
                normalizedUserIds.Length,
                batch.Results.Values.Sum(
                    result => result.Rivals.Count),
                batch.Results.Values.Sum(
                    result => result.Samples.Count),
                stopwatch.Elapsed.TotalSeconds);
        }

        return new LeaderboardRivalsResult
        {
            RivalCount = totalRivals,
            SampleCount = totalSamples,
        };
    }

    internal LeaderboardRivalsInstrumentBatch
        ComputeInstrumentBatch(
            IReadOnlyCollection<string> userIds,
            string instrument)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        var normalizedUserIds =
            MaxScoreMaintenanceAccountIdPolicy
                .NormalizeSet(userIds);
        var db = _persistence.GetOrCreateInstrumentDb(
            instrument);
        var rankings = db.GetAllAccountRankings();
        var rankingsByAccount =
            new Dictionary<string, AccountRankingDto>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var ranking in rankings)
        {
            if (!string.IsNullOrWhiteSpace(
                    ranking.AccountId))
            {
                rankingsByAccount.TryAdd(
                    ranking.AccountId,
                    ranking);
            }
        }

        var rankOrders = RankMethods.ToDictionary(
            rankMethod => rankMethod,
            rankMethod =>
            {
                var rows = rankings
                    .OrderBy(ranking =>
                        InstrumentDatabase.GetRankValue(
                            ranking,
                            rankMethod))
                    .ToArray();
                var indexByRank = rows
                    .Select((ranking, index) =>
                        new
                        {
                            Rank = InstrumentDatabase
                                .GetRankValue(
                                    ranking,
                                    rankMethod),
                            Index = index,
                        })
                    .ToDictionary(
                        item => item.Rank,
                        item => item.Index);
                return new LeaderboardRankingOrder(
                    rows,
                    indexByRank);
            },
            StringComparer.OrdinalIgnoreCase);
        var neighborhoods =
            new Dictionary<
                string,
                IReadOnlyDictionary<
                    string,
                    LeaderboardRankingNeighborhood>>(
                StringComparer.OrdinalIgnoreCase);
        var profileAccountIds =
            new HashSet<string>(
                normalizedUserIds,
                StringComparer.OrdinalIgnoreCase);
        var radius = Math.Max(0, _radius);

        foreach (var userId in normalizedUserIds)
        {
            var byMethod =
                new Dictionary<
                    string,
                    LeaderboardRankingNeighborhood>(
                    StringComparer.OrdinalIgnoreCase);
            if (rankingsByAccount.TryGetValue(
                    userId,
                    out var self))
            {
                foreach (var rankMethod in RankMethods)
                {
                    var order = rankOrders[rankMethod];
                    var selfRank =
                        InstrumentDatabase.GetRankValue(
                            self,
                            rankMethod);
                    if (!order.IndexByRank.TryGetValue(
                            selfRank,
                            out var selfIndex))
                    {
                        continue;
                    }

                    var aboveStart =
                        Math.Max(0, selfIndex - radius);
                    var above = order.Rows[
                            aboveStart..selfIndex]
                        .ToArray();
                    var belowEnd =
                        Math.Min(
                            order.Rows.Length,
                            selfIndex + radius + 1);
                    var below = order.Rows[
                            (selfIndex + 1)..belowEnd]
                        .ToArray();
                    byMethod[rankMethod] =
                        new LeaderboardRankingNeighborhood(
                            above,
                            self,
                            below);
                    foreach (var neighbor in
                             above.Concat(below))
                    {
                        profileAccountIds.Add(
                            neighbor.AccountId);
                    }
                }
            }
            neighborhoods[userId] = byMethod;
        }

        var profiles =
            db.GetCurrentStatePlayerScoresForAccounts(
                profileAccountIds,
                includeBlankAccountIds: true);
        MaintenanceProfileBatchReadTestHook?.Invoke(
            instrument,
            profileAccountIds.Count);
        var results =
            new Dictionary<
                string,
                LeaderboardInstrumentRivalsResult>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var userId in normalizedUserIds)
        {
            profiles.TryGetValue(
                userId,
                out var userScores);
            results[userId] = BuildInstrumentResult(
                userId,
                instrument,
                RankMethods,
                userScores ?? [],
                neighborhoods[userId],
                profiles);
        }

        return new LeaderboardRivalsInstrumentBatch(
            instrument,
            rankings.Count,
            profiles.Count,
            profiles.Values.Sum(scores => scores.Count),
            results);
    }

    internal LeaderboardInstrumentRivalsResult ComputeInstrument(
        string userId,
        string instrument,
        IReadOnlyCollection<string> rankMethods)
    {
        var db = _persistence.GetOrCreateInstrumentDb(instrument);

        var userScores = db.GetCurrentStatePlayerScores(userId);
        if (userScores.Count == 0)
        {
            return new LeaderboardInstrumentRivalsResult
            {
                Instrument = instrument,
                CompletedRankMethods = rankMethods.ToArray(),
            };
        }

        var neighborhoods =
            new Dictionary<
                string,
                LeaderboardRankingNeighborhood>(
                StringComparer.OrdinalIgnoreCase);
        var neighborIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var rankMethod in rankMethods)
        {
            var (above, self, below) =
                db.GetAccountRankingNeighborhood(
                    userId,
                    _radius,
                    rankMethod);
            neighborhoods[rankMethod] =
                new LeaderboardRankingNeighborhood(
                    above,
                    self,
                    below);
            foreach (var neighbor in above.Concat(below))
                neighborIds.Add(neighbor.AccountId);
        }
        var profiles =
            new Dictionary<
                string,
                List<PlayerScoreDto>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [userId] = userScores,
            };
        var userSongIds =
            userScores.Select(score => score.SongId)
                .ToArray();
        foreach (var neighborId in neighborIds)
        {
            profiles[neighborId] =
                db.GetCurrentStatePlayerScoresForSongs(
                    neighborId,
                    userSongIds);
        }
        return BuildInstrumentResult(
            userId,
            instrument,
            rankMethods,
            userScores,
            neighborhoods,
            profiles);
    }

    private static LeaderboardInstrumentRivalsResult
        BuildInstrumentResult(
            string userId,
            string instrument,
            IReadOnlyCollection<string> rankMethods,
            IReadOnlyList<PlayerScoreDto> userScores,
            IReadOnlyDictionary<
                string,
                LeaderboardRankingNeighborhood> neighborhoods,
            IReadOnlyDictionary<
                string,
                List<PlayerScoreDto>> profiles)
    {
        if (userScores.Count == 0)
        {
            return new LeaderboardInstrumentRivalsResult
            {
                Instrument = instrument,
                CompletedRankMethods =
                    rankMethods.ToArray(),
            };
        }

        var userScoreMap = userScores.ToDictionary(
            score => score.SongId,
            StringComparer.OrdinalIgnoreCase);
        var instrumentRivals = new List<LeaderboardRivalRow>();
        var instrumentSamples = new List<LeaderboardRivalSongSampleRow>();
        var neighborScoreMaps =
            new Dictionary<
                string,
                Dictionary<string, PlayerScoreDto>>(
                StringComparer.OrdinalIgnoreCase);
        var userRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("o");

        foreach (var rankMethod in rankMethods)
        {
            if (!neighborhoods.TryGetValue(
                    rankMethod,
                    out var neighborhood)
                || neighborhood.Self is null)
            {
                continue;
            }

            var userRank = InstrumentDatabase.GetRankValue(
                neighborhood.Self,
                rankMethod);
            userRanks[rankMethod] = userRank;

            var neighbors = new List<(AccountRankingDto Dto, string Direction)>();
            foreach (var above in neighborhood.Above)
                neighbors.Add((above, "above"));
            foreach (var below in neighborhood.Below)
                neighbors.Add((below, "below"));

            foreach (var (neighbor, direction) in neighbors)
            {
                var neighborId = neighbor.AccountId;
                var neighborRank = InstrumentDatabase.GetRankValue(neighbor, rankMethod);

                if (!neighborScoreMaps.TryGetValue(
                        neighborId,
                        out var cachedScores))
                {
                    if (!profiles.TryGetValue(
                            neighborId,
                            out var neighborScores))
                        continue;
                    cachedScores =
                        neighborScores.ToDictionary(
                            score => score.SongId,
                            StringComparer.OrdinalIgnoreCase);
                    neighborScoreMaps[neighborId] =
                        cachedScores;
                }

                int sharedSongCount = 0;
                int aheadCount = 0;
                int behindCount = 0;
                double signedDeltaSum = 0;
                var songSamples = new List<LeaderboardRivalSongSampleRow>();

                foreach (var (songId, userScore) in userScoreMap)
                {
                    if (!cachedScores.TryGetValue(songId, out var rivalScore)) continue;

                    sharedSongCount++;
                    var userSongRank = userScore.Rank > 0 ? userScore.Rank : userScore.ApiRank;
                    var rivalSongRank = rivalScore.Rank > 0 ? rivalScore.Rank : rivalScore.ApiRank;
                    if (userSongRank == 0 || rivalSongRank == 0) continue;

                    var rankDelta = rivalSongRank - userSongRank;
                    signedDeltaSum += rankDelta;

                    if (rankDelta > 0) behindCount++;
                    else if (rankDelta < 0) aheadCount++;

                    songSamples.Add(new LeaderboardRivalSongSampleRow
                    {
                        UserId = userId,
                        RivalAccountId = neighborId,
                        Instrument = instrument,
                        RankMethod = rankMethod,
                        SongId = songId,
                        UserRank = userSongRank,
                        RivalRank = rivalSongRank,
                        RankDelta = rankDelta,
                        UserScore = userScore.Score,
                        RivalScore = rivalScore.Score,
                    });
                }

                if (sharedSongCount == 0) continue;

                instrumentRivals.Add(new LeaderboardRivalRow
                {
                    UserId = userId,
                    RivalAccountId = neighborId,
                    Instrument = instrument,
                    RankMethod = rankMethod,
                    Direction = direction,
                    UserRank = userRank,
                    RivalRank = neighborRank,
                    SharedSongCount = sharedSongCount,
                    AheadCount = aheadCount,
                    BehindCount = behindCount,
                    AvgSignedDelta = signedDeltaSum / sharedSongCount,
                    ComputedAt = now,
                });

                var topSamples = songSamples
                    .OrderBy(s => Math.Abs(s.RankDelta))
                    .Take(MaxSamplesPerRival);
                instrumentSamples.AddRange(topSamples);
            }
        }

        return new LeaderboardInstrumentRivalsResult
        {
            Instrument = instrument,
            HasUserScores = true,
            CompletedRankMethods = rankMethods.ToArray(),
            UserRanks = userRanks,
            Rivals = instrumentRivals,
            Samples = instrumentSamples,
        };
    }

    private sealed record LeaderboardRankingOrder(
        AccountRankingDto[] Rows,
        IReadOnlyDictionary<int, int> IndexByRank);

    internal sealed record LeaderboardRankingNeighborhood(
        IReadOnlyList<AccountRankingDto> Above,
        AccountRankingDto? Self,
        IReadOnlyList<AccountRankingDto> Below);
}

/// <summary>Result summary from leaderboard rivals computation.</summary>
public sealed class LeaderboardRivalsResult
{
    public int RivalCount { get; init; }
    public int SampleCount { get; init; }
}

public sealed class LeaderboardInstrumentRivalsResult
{
    public required string Instrument { get; init; }
    public bool HasUserScores { get; init; }
    public IReadOnlyCollection<string> CompletedRankMethods { get; init; } = [];
    public IReadOnlyDictionary<string, int> UserRanks { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<LeaderboardRivalRow> Rivals { get; init; } = [];
    public IReadOnlyList<LeaderboardRivalSongSampleRow> Samples { get; init; } = [];

    public bool UserFound => UserRanks.Count > 0;

    public int? GetUserRank(string rankMethod)
    {
        return UserRanks.TryGetValue(rankMethod, out var rank) ? rank : null;
    }
}

internal sealed record LeaderboardRivalsInstrumentBatch(
    string Instrument,
    int RankingCount,
    int ProfileAccountCount,
    int ScoreCount,
    IReadOnlyDictionary<
        string,
        LeaderboardInstrumentRivalsResult> Results);
