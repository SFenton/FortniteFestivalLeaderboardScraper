using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Computes rival players for a registered user by scanning ±50 rank neighborhoods
/// across all songs on each instrument, then aggregating frequency and proximity
/// into a weighted score. Produces per-instrument and per-combo rival lists.
/// </summary>
public sealed class RivalsCalculator
{
    /// <summary>Minimum songs a user must have on an instrument to include it.</summary>
    internal const int MinUserSongsPerInstrument = 10;

    /// <summary>Minimum shared songs for a rival to qualify on a single instrument.</summary>
    internal const int MinSharedSongsPerInstrument = 5;

    /// <summary>Minimum shared songs per instrument for combo qualification.</summary>
    internal const int MinSharedSongsPerInstrumentInCombo = 3;

    /// <summary>Number of ranks above and below the user to scan.</summary>
    internal const int NeighborhoodRadius = 50;

    /// <summary>Number of rivals to store per direction (above/below) per combo.</summary>
    internal const int RivalsPerDirection = 10;

    /// <summary>Maximum song samples to store per rival per instrument.</summary>
    internal const int MaxSamplesPerRivalPerInstrument = 200;

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<RivalsCalculator> _log;

    public RivalsCalculator(
        GlobalLeaderboardPersistence persistence,
        ILogger<RivalsCalculator> log)
    {
        _persistence = persistence;
        _log = log;
    }

    /// <summary>
    /// Quickly count how many valid instrument combos a user will have.
    /// Only queries song counts per instrument — no neighborhood scans.
    /// </summary>
    public int CountValidCombos(string userId, IReadOnlySet<string>? dirtyInstruments = null)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        int validCount = 0;

        foreach (var instrument in instrumentKeys)
        {
            if (dirtyInstruments is not null && !dirtyInstruments.Contains(instrument))
                continue;

            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var scores = db.GetPlayerScores(userId);
            if (scores.Count >= MinUserSongsPerInstrument)
                validCount++;
        }

        return validCount > 0 ? (1 << validCount) - 1 : 0; // 2^N - 1
    }

    /// <summary>
    /// Compute rivals for a single user across all valid instruments and combos.
    /// Returns the total number of rival rows produced.
    /// </summary>
    public RivalsResult ComputeRivals(string userId, IReadOnlySet<string>? dirtyInstruments = null)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        var perInstrument = new Dictionary<string, InstrumentRivalsData>(StringComparer.OrdinalIgnoreCase);
        var allCandidates = new Dictionary<string, Dictionary<string, RivalCandidate>>(StringComparer.OrdinalIgnoreCase);

        // Step 1+3: Gather user entries and scan neighborhoods per instrument
        foreach (var instrument in instrumentKeys)
        {
            if (dirtyInstruments is not null && !dirtyInstruments.Contains(instrument))
                continue;

            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var userScores = db.GetPlayerScores(userId);
            if (userScores.Count < MinUserSongsPerInstrument)
                continue;

            var songCounts = db.GetAllSongCounts();
            var candidates = new Dictionary<string, RivalCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in userScores)
            {
                var effectiveRank = entry.ApiRank > 0 ? entry.ApiRank : entry.Rank;
                if (effectiveRank <= 0) continue; // skip unranked
                if (!songCounts.TryGetValue(entry.SongId, out var entryCount) || entryCount <= 1)
                    continue;

                var neighbors = db.GetNeighborhood(entry.SongId, effectiveRank, NeighborhoodRadius, userId);
                var logWeight = Math.Log2(entryCount);

                foreach (var (neighborId, neighborRank, neighborScore) in neighbors)
                {
                    if (!candidates.TryGetValue(neighborId, out var candidate))
                    {
                        candidate = new RivalCandidate { AccountId = neighborId };
                        candidates[neighborId] = candidate;
                    }

                    var rankDelta = neighborRank - effectiveRank; // positive = behind user, negative = ahead
                    var absDelta = Math.Abs(rankDelta);

                    candidate.Appearances++;
                    candidate.WeightedScore += logWeight / (1.0 + absDelta);
                    candidate.SignedDeltaSum += rankDelta;
                    if (rankDelta < 0) candidate.AheadCount++;
                    else if (rankDelta > 0) candidate.BehindCount++;

                    candidate.SongDetails.Add(new SongDetail
                    {
                        SongId = entry.SongId,
                        Instrument = instrument,
                        UserRank = effectiveRank,
                        RivalRank = neighborRank,
                        RankDelta = rankDelta,
                        UserScore = entry.Score,
                        RivalScore = neighborScore,
                    });
                }
            }

            perInstrument[instrument] = new InstrumentRivalsData
            {
                Instrument = instrument,
                UserSongCount = userScores.Count,
                Candidates = candidates,
            };
            allCandidates[instrument] = candidates;
        }

        if (perInstrument.Count == 0)
            return RivalsResult.Empty;

        var now = DateTime.UtcNow.ToString("o");
        var rivalRows = new List<UserRivalRow>();
        var sampleRows = new List<RivalSongSampleRow>();

        // Step 2: Determine valid instruments
        var validInstruments = perInstrument.Keys.ToList();
        validInstruments.Sort(StringComparer.Ordinal);

        // Step 4: Per-instrument rival selection
        foreach (var instrument in validInstruments)
        {
            var data = perInstrument[instrument];
            var combo = ComboIds.FromInstruments([instrument]); // single-instrument combo ID
            SelectRivals(userId, combo, data.Candidates.Values, MinSharedSongsPerInstrument,
                RivalsPerDirection, now, rivalRows);
        }

        // Step 5: Combination rival computation
        var combos = GenerateCombos(validInstruments);
        foreach (var instruments in combos)
        {
            if (instruments.Count < 2) continue; // singles already handled

            var comboId = ComboIds.FromInstruments(instruments);
            var combinedCandidates = IntersectCandidates(instruments, allCandidates);
            SelectRivals(userId, comboId, combinedCandidates.Values, MinSharedSongsPerInstrumentInCombo,
                RivalsPerDirection, now, rivalRows);
        }

        // Step 6: Sample selection — for each selected rival per instrument
        var selectedRivalIds = new HashSet<string>(
            rivalRows.Select(r => r.RivalAccountId), StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in validInstruments)
        {
            var data = perInstrument[instrument];
            foreach (var rivalId in selectedRivalIds)
            {
                if (!data.Candidates.TryGetValue(rivalId, out var candidate))
                    continue;

                var instrumentDetails = candidate.SongDetails
                    .Where(d => d.Instrument.Equals(instrument, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => Math.Abs(d.RankDelta))
                    .Take(MaxSamplesPerRivalPerInstrument)
                    .ToList();

                foreach (var detail in instrumentDetails)
                {
                    sampleRows.Add(new RivalSongSampleRow
                    {
                        UserId = userId,
                        RivalAccountId = rivalId,
                        Instrument = instrument,
                        SongId = detail.SongId,
                        UserRank = detail.UserRank,
                        RivalRank = detail.RivalRank,
                        RankDelta = detail.RankDelta,
                        UserScore = detail.UserScore,
                        RivalScore = detail.RivalScore,
                    });
                }
            }
        }

        var comboCount = rivalRows.Select(r => r.InstrumentCombo).Distinct().Count();

        return new RivalsResult
        {
            Rivals = rivalRows,
            Samples = sampleRows,
            CombosComputed = comboCount,
        };
    }

    /// <summary>
    /// Select top-N above and top-N below rivals from a candidate pool and append to <paramref name="output"/>.
    /// </summary>
    internal static void SelectRivals(
        string userId,
        string comboKey,
        IEnumerable<RivalCandidate> candidates,
        int minSharedSongs,
        int rivalsPerDirection,
        string computedAt,
        List<UserRivalRow> output)
    {
        var qualified = candidates.Where(c => c.Appearances >= minSharedSongs).ToList();

        var above = qualified
            .Where(c => c.AvgSignedDelta < 0) // negative = they're ahead
            .OrderByDescending(c => c.WeightedScore)
            .Take(rivalsPerDirection);

        var below = qualified
            .Where(c => c.AvgSignedDelta >= 0) // positive/zero = they're behind or dead even
            .OrderByDescending(c => c.WeightedScore)
            .Take(rivalsPerDirection);

        foreach (var c in above)
        {
            output.Add(new UserRivalRow
            {
                UserId = userId,
                RivalAccountId = c.AccountId,
                InstrumentCombo = comboKey,
                Direction = "above",
                RivalScore = c.WeightedScore,
                AvgSignedDelta = c.AvgSignedDelta,
                SharedSongCount = c.Appearances,
                AheadCount = c.AheadCount,
                BehindCount = c.BehindCount,
                ComputedAt = computedAt,
            });
        }

        foreach (var c in below)
        {
            output.Add(new UserRivalRow
            {
                UserId = userId,
                RivalAccountId = c.AccountId,
                InstrumentCombo = comboKey,
                Direction = "below",
                RivalScore = c.WeightedScore,
                AvgSignedDelta = c.AvgSignedDelta,
                SharedSongCount = c.Appearances,
                AheadCount = c.AheadCount,
                BehindCount = c.BehindCount,
                ComputedAt = computedAt,
            });
        }
    }

    /// <summary>Generate all non-empty subsets of the sorted instrument list.</summary>
    internal static List<List<string>> GenerateCombos(List<string> sortedInstruments)
    {
        var combos = new List<List<string>>();
        int n = sortedInstruments.Count;
        int total = 1 << n; // 2^N

        for (int mask = 1; mask < total; mask++)
        {
            var combo = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0)
                    combo.Add(sortedInstruments[i]);
            }
            combos.Add(combo);
        }
        return combos;
    }

    /// <summary>
    /// Intersect rival candidates across multiple instruments for combo computation.
    /// A candidate must appear on ALL instruments in the combo.
    /// Combined score = SUM of per-instrument scores.
    /// </summary>
    internal static Dictionary<string, RivalCandidate> IntersectCandidates(
        List<string> comboInstruments,
        Dictionary<string, Dictionary<string, RivalCandidate>> allCandidates)
    {
        // Start with the smallest candidate pool for efficiency
        var instrumentPools = comboInstruments
            .Where(i => allCandidates.ContainsKey(i))
            .Select(i => allCandidates[i])
            .OrderBy(p => p.Count)
            .ToList();

        if (instrumentPools.Count < comboInstruments.Count)
            return new Dictionary<string, RivalCandidate>(); // missing instrument data

        var smallest = instrumentPools[0];
        var result = new Dictionary<string, RivalCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var (accountId, _) in smallest)
        {
            bool presentInAll = true;
            for (int i = 1; i < instrumentPools.Count; i++)
            {
                if (!instrumentPools[i].ContainsKey(accountId))
                {
                    presentInAll = false;
                    break;
                }
            }
            if (!presentInAll) continue;

            // Build combined candidate
            var combined = new RivalCandidate { AccountId = accountId };
            double totalWeight = 0;
            foreach (var pool in instrumentPools)
            {
                var c = pool[accountId];
                combined.Appearances += c.Appearances;
                combined.WeightedScore += c.WeightedScore;
                combined.AheadCount += c.AheadCount;
                combined.BehindCount += c.BehindCount;
                combined.SignedDeltaSum += c.SignedDeltaSum;
                totalWeight += c.WeightedScore;
            }
            // Weighted average signed delta
            if (totalWeight > 0)
            {
                double weightedDelta = 0;
                foreach (var pool in instrumentPools)
                {
                    var c = pool[accountId];
                    weightedDelta += c.AvgSignedDelta * c.WeightedScore;
                }
                combined.SignedDeltaSum = 0; // clear so AvgSignedDelta uses override
                combined.OverrideAvgSignedDelta = weightedDelta / totalWeight;
            }

            result[accountId] = combined;
        }

        return result;
    }

    /// <summary>Maximum song gap entries to return per direction (songs to compete / exclusive songs).</summary>
    internal const int MaxSongGapsPerDirection = 100;

    /// <summary>
    /// Compute song gaps between a user and a rival across the specified instruments.
    /// Returns songs the rival has that the user doesn't ("songs to compete on")
    /// and songs the user has that the rival doesn't ("your exclusive songs").
    /// Computed on-the-fly from local instrument DBs — no storage or API calls.
    /// </summary>
    public SongGapsResult ComputeSongGaps(
        string userId,
        string rivalId,
        IReadOnlyList<string> instruments,
        int cap = MaxSongGapsPerDirection)
    {
        var songsToCompete = new List<SongGapEntry>();
        var yourExclusives = new List<SongGapEntry>();

        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);

            var userSongIds = db.GetSongIdsForAccount(userId);
            var rivalSongIds = db.GetSongIdsForAccount(rivalId);

            // Songs the rival has that the user hasn't played
            var rivalOnly = new List<string>();
            foreach (var sid in rivalSongIds)
            {
                if (!userSongIds.Contains(sid))
                    rivalOnly.Add(sid);
            }

            if (rivalOnly.Count > 0)
            {
                var rivalScores = db.GetPlayerScoresForSongs(rivalId, rivalOnly);
                foreach (var s in rivalScores)
                {
                    songsToCompete.Add(new SongGapEntry
                    {
                        SongId = s.SongId,
                        Instrument = instrument,
                        Score = s.Score,
                        Rank = s.Rank,
                    });
                }
            }

            // Songs the user has that the rival hasn't played
            var userOnly = new List<string>();
            foreach (var sid in userSongIds)
            {
                if (!rivalSongIds.Contains(sid))
                    userOnly.Add(sid);
            }

            if (userOnly.Count > 0)
            {
                var userScores = db.GetPlayerScoresForSongs(userId, userOnly);
                foreach (var s in userScores)
                {
                    yourExclusives.Add(new SongGapEntry
                    {
                        SongId = s.SongId,
                        Instrument = instrument,
                        Score = s.Score,
                        Rank = s.Rank,
                    });
                }
            }
        }

        // Sort by rank ascending (best songs first), cap at limit
        return new SongGapsResult
        {
            SongsToCompete = songsToCompete
                .OrderBy(e => e.Rank <= 0 ? int.MaxValue : e.Rank)
                .Take(cap)
                .ToList(),
            YourExclusives = yourExclusives
                .OrderBy(e => e.Rank <= 0 ? int.MaxValue : e.Rank)
                .Take(cap)
                .ToList(),
        };
    }

    // ─── Internal types ──────────────────────────────────────────

    internal sealed class RivalCandidate
    {
        public string AccountId { get; init; } = "";
        public int Appearances { get; set; }
        public double WeightedScore { get; set; }
        public long SignedDeltaSum { get; set; }
        public int AheadCount { get; set; }
        public int BehindCount { get; set; }
        public double? OverrideAvgSignedDelta { get; set; }
        public List<SongDetail> SongDetails { get; } = new();

        public double AvgSignedDelta =>
            OverrideAvgSignedDelta ?? (Appearances > 0 ? (double)SignedDeltaSum / Appearances : 0);
    }

    internal sealed class SongDetail
    {
        public string SongId { get; init; } = "";
        public string Instrument { get; init; } = "";
        public int UserRank { get; init; }
        public int RivalRank { get; init; }
        public int RankDelta { get; init; }
        public int UserScore { get; init; }
        public int RivalScore { get; init; }
    }

    internal sealed class InstrumentRivalsData
    {
        public string Instrument { get; init; } = "";
        public int UserSongCount { get; init; }
        public Dictionary<string, RivalCandidate> Candidates { get; init; } = new();
    }
}

/// <summary>
/// Result of a rivals computation for a single user.
/// </summary>
public sealed class RivalsResult
{
    public IReadOnlyList<UserRivalRow> Rivals { get; init; } = Array.Empty<UserRivalRow>();
    public IReadOnlyList<RivalSongSampleRow> Samples { get; init; } = Array.Empty<RivalSongSampleRow>();
    public int CombosComputed { get; init; }

    public static RivalsResult Empty { get; } = new();
}

/// <summary>
/// Result of song gap computation between a user and a rival.
/// </summary>
public sealed class SongGapsResult
{
    /// <summary>Songs the rival has scored on that the user hasn't — opportunities to compete.</summary>
    public IReadOnlyList<SongGapEntry> SongsToCompete { get; init; } = Array.Empty<SongGapEntry>();
    /// <summary>Songs the user has scored on that the rival hasn't — exclusive advantage.</summary>
    public IReadOnlyList<SongGapEntry> YourExclusives { get; init; } = Array.Empty<SongGapEntry>();
}
