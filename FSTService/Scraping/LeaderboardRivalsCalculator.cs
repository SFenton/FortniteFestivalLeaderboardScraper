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
            var db = _persistence.GetOrCreateInstrumentDb(instrument);

            // Load user scores once per instrument — reused across all rank methods
            var userScores = db.GetPlayerScores(userId);
            if (userScores.Count == 0) continue;

            var userScoreMap = userScores.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);

            // Per-instrument lists — persisted and released each iteration
            var instrumentRivals = new List<LeaderboardRivalRow>();
            var instrumentSamples = new List<LeaderboardRivalSongSampleRow>();

            // Cache neighbor scores to avoid re-fetching across rank methods
            var neighborScoreCache = new Dictionary<string, Dictionary<string, PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
            var selfFoundForInstrument = false;

            foreach (var rankMethod in RankMethods)
            {
                var (above, self, below) = db.GetAccountRankingNeighborhood(userId, _radius, rankMethod);
                if (self is null) continue;
                selfFoundForInstrument = true;

                var userRank = InstrumentDatabase.GetRankValue(self, rankMethod);
                var neighbors = new List<(AccountRankingDto Dto, string Direction)>();
                foreach (var a in above) neighbors.Add((a, "above"));
                foreach (var b in below) neighbors.Add((b, "below"));

                foreach (var (neighbor, direction) in neighbors)
                {
                    var neighborId = neighbor.AccountId;
                    var neighborRank = InstrumentDatabase.GetRankValue(neighbor, rankMethod);

                    // Get or cache neighbor scores
                    if (!neighborScoreCache.TryGetValue(neighborId, out var cachedScores))
                    {
                        var neighborScoreList = db.GetPlayerScoresForSongs(neighborId, userScoreMap.Keys.ToList());
                        cachedScores = neighborScoreList.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);
                        neighborScoreCache[neighborId] = cachedScores;
                    }

                    // Compute per-song comparison
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

                        var rankDelta = rivalSongRank - userSongRank; // positive = user ahead
                        signedDeltaSum += rankDelta;

                        // Convention: fields are rival-perspective (matching RivalsCalculator)
                        if (rankDelta > 0) behindCount++;      // rival is behind (user has better rank)
                        else if (rankDelta < 0) aheadCount++;  // rival is ahead (rival has better rank)

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

                    // Keep top-N closest song samples (smallest |rankDelta|)
                    var topSamples = songSamples
                        .OrderBy(s => Math.Abs(s.RankDelta))
                        .Take(MaxSamplesPerRival);
                    instrumentSamples.AddRange(topSamples);
                }
            }

            // Persist all data for this instrument at once — but only if the user
            // was found in AccountRankings for at least one rank method.
            if (selfFoundForInstrument)
            {
                _meta.ReplaceLeaderboardRivalsData(userId, instrument, instrumentRivals, instrumentSamples);
                totalRivals += instrumentRivals.Count;
                totalSamples += instrumentSamples.Count;
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
}

/// <summary>Result summary from leaderboard rivals computation.</summary>
public sealed class LeaderboardRivalsResult
{
    public int RivalCount { get; init; }
    public int SampleCount { get; init; }
}
