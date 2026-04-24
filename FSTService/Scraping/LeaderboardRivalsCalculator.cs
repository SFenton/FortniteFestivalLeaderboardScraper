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
    public LeaderboardRivalsResult ComputeForUser(string userId)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        int totalRivals = 0;
        int totalSamples = 0;
        var now = DateTime.UtcNow.ToString("o");

        foreach (var instrument in instrumentKeys)
        {
            var instrumentResult = ComputeInstrument(userId, instrument, RankMethods);

            // Persist all data for this instrument at once — but only if the user
            // was found in AccountRankings for at least one rank method.
            if (instrumentResult.UserFound)
            {
                _meta.ReplaceLeaderboardRivalsData(userId, instrument, instrumentResult.Rivals, instrumentResult.Samples);
                totalRivals += instrumentResult.Rivals.Count;
                totalSamples += instrumentResult.Samples.Count;
            }
            else
            {
                _log.LogDebug(
                    "Skipping leaderboard rivals replace for {User}/{Instrument}: user not found in AccountRankings.",
                    userId, instrument);
            }
        }

        return new LeaderboardRivalsResult
        {
            RivalCount = totalRivals,
            SampleCount = totalSamples,
        };
    }

    private LeaderboardInstrumentRivalsResult ComputeInstrument(
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
            };
        }

        var userScoreMap = userScores.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);
        var instrumentRivals = new List<LeaderboardRivalRow>();
        var instrumentSamples = new List<LeaderboardRivalSongSampleRow>();
        var neighborScoreCache = new Dictionary<string, Dictionary<string, PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
        var userRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("o");

        foreach (var rankMethod in rankMethods)
        {
            var (above, self, below) = db.GetAccountRankingNeighborhood(userId, _radius, rankMethod);
            if (self is null) continue;

            var userRank = InstrumentDatabase.GetRankValue(self, rankMethod);
            userRanks[rankMethod] = userRank;

            var neighbors = new List<(AccountRankingDto Dto, string Direction)>();
            foreach (var a in above) neighbors.Add((a, "above"));
            foreach (var b in below) neighbors.Add((b, "below"));

            foreach (var (neighbor, direction) in neighbors)
            {
                var neighborId = neighbor.AccountId;
                var neighborRank = InstrumentDatabase.GetRankValue(neighbor, rankMethod);

                if (!neighborScoreCache.TryGetValue(neighborId, out var cachedScores))
                {
                    var neighborScoreList = db.GetPlayerScoresForSongs(neighborId, userScoreMap.Keys.ToList());
                    cachedScores = neighborScoreList.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);
                    neighborScoreCache[neighborId] = cachedScores;
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
            UserRanks = userRanks,
            Rivals = instrumentRivals,
            Samples = instrumentSamples,
        };
    }
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
    public IReadOnlyDictionary<string, int> UserRanks { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<LeaderboardRivalRow> Rivals { get; init; } = [];
    public IReadOnlyList<LeaderboardRivalSongSampleRow> Samples { get; init; } = [];

    public bool UserFound => UserRanks.Count > 0;

    public int? GetUserRank(string rankMethod)
    {
        return UserRanks.TryGetValue(rankMethod, out var rank) ? rank : null;
    }
}
