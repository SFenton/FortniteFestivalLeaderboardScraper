using System.Collections.Concurrent;
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

    /// <summary>
    /// Progressive thresholds for single-instrument rivals. We prefer repeated overlap,
    /// but degrade gracefully until sparse users still get a roster.
    /// </summary>
    internal static readonly int[] PerInstrumentFallbackThresholds =
        [MinSharedSongsPerInstrument, 4, 3, 2, 1];

    /// <summary>Maximum song samples to store per rival per instrument.</summary>
    internal const int MaxSamplesPerRivalPerInstrument = 200;

    private static readonly TimeSpan SongGapsCacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (SongGapsResult Result, DateTime CachedAt)> _songGapsCache = new();

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<RivalsCalculator> _log;

    public RivalsCalculator(
        GlobalLeaderboardPersistence persistence,
        ILogger<RivalsCalculator> log)
    {
        _persistence = persistence;
        _log = log;
    }

    /// <summary>Invalidate all cached song gap results (call after scrape completion).</summary>
    public void InvalidateSongGapsCache() => _songGapsCache.Clear();

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
    public RivalsResult ComputeRivals(string userId, IReadOnlySet<string>? dirtyInstruments = null, Action<int>? onProgress = null)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        var perInstrument = new Dictionary<string, InstrumentRivalsData>(StringComparer.OrdinalIgnoreCase);
        var allCandidates = new Dictionary<string, Dictionary<string, RivalCandidate>>(StringComparer.OrdinalIgnoreCase);
        int progressCount = 0;

        // Step 1+3: Gather user entries and scan neighborhoods per instrument
        foreach (var instrument in instrumentKeys)
        {
            if (dirtyInstruments is not null && !dirtyInstruments.Contains(instrument))
                continue;

            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var userScores = db.GetPlayerScores(userId);
            var scan = ScanInstrumentCandidates(db, userId, userScores);
            if (scan.UserSongCount < MinUserSongsPerInstrument)
            {
                _log.LogDebug("Rivals [{User}] {Instrument}: skipped — only {Count} songs (min {Min}).",
                    userId, instrument, scan.UserSongCount, MinUserSongsPerInstrument);
                continue;
            }

            int qualifiedCandidates = scan.Candidates.Values.Count(c => c.Appearances >= MinSharedSongsPerInstrument);
            _log.LogInformation(
                "Rivals [{User}] {Instrument}: {Total} songs, {Scanned} scanned, " +
                "{Unranked} skipped (unranked), {Single} skipped (single-entry), " +
                "{Neighbors} neighbors found, {Candidates} unique candidates, {Qualified} qualified (>= {Min} appearances).",
                userId, instrument, scan.UserSongCount, scan.SongsScanned,
                scan.SongsSkippedUnranked, scan.SongsSkippedSingle,
                scan.TotalNeighborsFound, scan.Candidates.Count, qualifiedCandidates, MinSharedSongsPerInstrument);

            perInstrument[instrument] = new InstrumentRivalsData
            {
                Instrument = instrument,
                UserSongCount = scan.UserSongCount,
                Candidates = scan.Candidates,
            };
            allCandidates[instrument] = scan.Candidates;
            progressCount++;
            onProgress?.Invoke(progressCount);
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
            var selection = SelectRivalsWithFallback(data.Candidates.Values, RivalsPerDirection);
            AppendSelectedRivals(userId, combo, "above", selection.Above.Selected, now, rivalRows);
            AppendSelectedRivals(userId, combo, "below", selection.Below.Selected, now, rivalRows);
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
            progressCount++;
            onProgress?.Invoke(progressCount);
        }

        // Step 6: Sample selection — for each selected rival per instrument,
        // fetch song-level comparison data on-demand from the DB.  This avoids
        // retaining full SongDetail lists for every candidate during scanning.
        var selectedRivalIds = new HashSet<string>(
            rivalRows.Select(r => r.RivalAccountId), StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in validInstruments)
        {
            var data = perInstrument[instrument];
            var db = _persistence.GetOrCreateInstrumentDb(instrument);

            // Load user scores once for this instrument
            var userScores = db.GetPlayerScores(userId);
            var userScoreMap = userScores.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);

            foreach (var rivalId in selectedRivalIds)
            {
                if (!data.Candidates.TryGetValue(rivalId, out var candidate))
                    continue;

                // Only query songs where this rival was actually a neighbor
                var sharedSongIds = candidate.SongIds.Where(userScoreMap.ContainsKey).ToList();
                if (sharedSongIds.Count == 0) continue;

                var rivalScores = db.GetPlayerScoresForSongs(rivalId, sharedSongIds);
                var rivalScoreMap = rivalScores.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);

                var details = new List<(int AbsDelta, RivalSongSampleRow Row)>();
                foreach (var songId in sharedSongIds)
                {
                    if (!userScoreMap.TryGetValue(songId, out var userScore)) continue;
                    if (!rivalScoreMap.TryGetValue(songId, out var rivalScore)) continue;

                    var userRank = userScore.Rank > 0 ? userScore.Rank : userScore.ApiRank;
                    var rivalRank = rivalScore.Rank > 0 ? rivalScore.Rank : rivalScore.ApiRank;
                    if (userRank <= 0 || rivalRank <= 0) continue;

                    var rankDelta = rivalRank - userRank;

                    details.Add((Math.Abs(rankDelta), new RivalSongSampleRow
                    {
                        UserId = userId,
                        RivalAccountId = rivalId,
                        Instrument = instrument,
                        SongId = songId,
                        UserRank = userRank,
                        RivalRank = rivalRank,
                        RankDelta = rankDelta,
                        UserScore = userScore.Score,
                        RivalScore = rivalScore.Score,
                    }));
                }

                // Keep top-N closest samples (smallest |rankDelta|)
                foreach (var (_, row) in details.OrderBy(d => d.AbsDelta).Take(MaxSamplesPerRivalPerInstrument))
                    sampleRows.Add(row);
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

    private static InstrumentScanResult ScanInstrumentCandidates(
        IInstrumentDatabase db,
        string userId,
        IReadOnlyList<PlayerScoreDto> userScores)
    {
        var candidates = new Dictionary<string, RivalCandidate>(StringComparer.OrdinalIgnoreCase);
        int songsSkippedUnranked = 0;
        int songsSkippedSingle = 0;
        int totalNeighborsFound = 0;
        int songsScanned = 0;

        if (userScores.Count < MinUserSongsPerInstrument)
        {
            return new InstrumentScanResult
            {
                UserSongCount = userScores.Count,
                Candidates = candidates,
            };
        }

        var songCounts = db.GetAllSongCounts();
        foreach (var entry in userScores)
        {
            // Prefer dense Rank (set by RecomputeAllRanks for scrape entries) because
            // GetNeighborhood queries the Rank column. ApiRank is the global Epic rank
            // which can be 100K+ and won't match any dense-ranked entries in the DB.
            var effectiveRank = entry.Rank > 0 ? entry.Rank : entry.ApiRank;
            if (effectiveRank <= 0)
            {
                songsSkippedUnranked++;
                continue;
            }

            if (!songCounts.TryGetValue(entry.SongId, out var entryCount) || entryCount <= 1)
            {
                songsSkippedSingle++;
                continue;
            }

            songsScanned++;
            var neighbors = db.GetNeighborhood(entry.SongId, effectiveRank, NeighborhoodRadius, userId);
            totalNeighborsFound += neighbors.Count;
            var logWeight = Math.Log2(entryCount);

            foreach (var (neighborId, neighborRank, _) in neighbors)
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
                candidate.SongIds.Add(entry.SongId);
            }
        }

        return new InstrumentScanResult
        {
            UserSongCount = userScores.Count,
            SongsSkippedUnranked = songsSkippedUnranked,
            SongsSkippedSingle = songsSkippedSingle,
            TotalNeighborsFound = totalNeighborsFound,
            SongsScanned = songsScanned,
            Candidates = candidates,
        };
    }

    internal static RivalFallbackSelection SelectRivalsWithFallback(
        IEnumerable<RivalCandidate> candidates,
        int rivalsPerDirection)
    {
        var candidateList = candidates.ToList();
        return new RivalFallbackSelection
        {
            Above = SelectDirectionWithFallback(
                candidateList.Where(c => c.AvgSignedDelta < 0).ToList(),
                rivalsPerDirection),
            Below = SelectDirectionWithFallback(
                candidateList.Where(c => c.AvgSignedDelta >= 0).ToList(),
                rivalsPerDirection),
        };
    }

    private static DirectionSelectionResult SelectDirectionWithFallback(
        List<RivalCandidate> candidates,
        int rivalsPerDirection)
    {
        var selected = new List<SelectedRival>();
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var threshold in PerInstrumentFallbackThresholds)
        {
            if (selected.Count >= rivalsPerDirection)
                break;

            var stageCandidates = OrderCandidatesForThreshold(
                candidates.Where(c => c.Appearances >= threshold && !selectedIds.Contains(c.AccountId)),
                threshold);

            foreach (var candidate in stageCandidates)
            {
                if (!selectedIds.Add(candidate.AccountId))
                    continue;

                selected.Add(new SelectedRival
                {
                    Candidate = candidate,
                    ThresholdUsed = threshold,
                });

                if (selected.Count >= rivalsPerDirection)
                    break;
            }
        }

        return new DirectionSelectionResult
        {
            Selected = selected,
        };
    }

    private static IOrderedEnumerable<RivalCandidate> OrderCandidatesForThreshold(
        IEnumerable<RivalCandidate> candidates,
        int threshold)
    {
        if (threshold == 1)
        {
            return candidates
                .OrderBy(c => Math.Abs(c.AvgSignedDelta))
                .ThenByDescending(c => c.Appearances)
                .ThenByDescending(c => c.WeightedScore)
                .ThenBy(c => c.AccountId, StringComparer.OrdinalIgnoreCase);
        }

        return candidates
            .OrderByDescending(c => c.WeightedScore)
            .ThenByDescending(c => c.Appearances)
            .ThenBy(c => Math.Abs(c.AvgSignedDelta))
            .ThenBy(c => c.AccountId, StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendSelectedRivals(
        string userId,
        string comboKey,
        string direction,
        IEnumerable<SelectedRival> selected,
        string computedAt,
        List<UserRivalRow> output)
    {
        foreach (var selectedRival in selected)
        {
            var candidate = selectedRival.Candidate;
            output.Add(new UserRivalRow
            {
                UserId = userId,
                RivalAccountId = candidate.AccountId,
                InstrumentCombo = comboKey,
                Direction = direction,
                RivalScore = candidate.WeightedScore,
                AvgSignedDelta = candidate.AvgSignedDelta,
                SharedSongCount = candidate.Appearances,
                AheadCount = candidate.AheadCount,
                BehindCount = candidate.BehindCount,
                ComputedAt = computedAt,
            });
        }
    }

    private static RivalThresholdCounts BuildThresholdCounts(IEnumerable<RivalCandidate> candidates)
    {
        var candidateList = candidates.ToList();
        return new RivalThresholdCounts
        {
            AtLeastFive = candidateList.Count(c => c.Appearances >= 5),
            AtLeastFour = candidateList.Count(c => c.Appearances >= 4),
            AtLeastThree = candidateList.Count(c => c.Appearances >= 3),
            AtLeastTwo = candidateList.Count(c => c.Appearances >= 2),
            AtLeastOne = candidateList.Count(c => c.Appearances >= 1),
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
        var sortedInsts = instruments.OrderBy(i => i, StringComparer.Ordinal).ToList();
        var cacheKey = $"{userId}:{rivalId}:{string.Join('+', sortedInsts)}";

        if (_songGapsCache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < SongGapsCacheTtl)
        {
            // Cap may differ per call but the underlying data is the same — re-cap from cached full result
            return new SongGapsResult
            {
                SongsToCompete = cached.Result.SongsToCompete.Take(cap).ToList(),
                YourExclusives = cached.Result.YourExclusives.Take(cap).ToList(),
            };
        }

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
        var fullResult = new SongGapsResult
        {
            SongsToCompete = songsToCompete
                .OrderBy(e => e.Rank <= 0 ? int.MaxValue : e.Rank)
                .ToList(),
            YourExclusives = yourExclusives
                .OrderBy(e => e.Rank <= 0 ? int.MaxValue : e.Rank)
                .ToList(),
        };

        _songGapsCache[cacheKey] = (fullResult, DateTime.UtcNow);

        return new SongGapsResult
        {
            SongsToCompete = fullResult.SongsToCompete.Take(cap).ToList(),
            YourExclusives = fullResult.YourExclusives.Take(cap).ToList(),
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

        /// <summary>Song IDs where this candidate appeared as a neighbor (lightweight tracking).</summary>
        public HashSet<string> SongIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public double AvgSignedDelta =>
            OverrideAvgSignedDelta ?? (Appearances > 0 ? (double)SignedDeltaSum / Appearances : 0);
    }

    internal sealed class InstrumentRivalsData
    {
        public string Instrument { get; init; } = "";
        public int UserSongCount { get; init; }
        public Dictionary<string, RivalCandidate> Candidates { get; init; } = new();
    }

    // ─── Diagnostics ─────────────────────────────────────────────

    /// <summary>
    /// Produce per-instrument diagnostic information for a user, revealing
    /// the funnel from songs → ranked songs → neighborhoods → qualified rivals.
    /// Used by the diagnostics endpoint to identify why rivals may be empty.
    /// </summary>
    public RivalsDiagnostics GetDiagnostics(string userId)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        var instruments = new List<InstrumentDiagnostics>();

        foreach (var instrument in instrumentKeys)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var userScores = db.GetPlayerScores(userId);

            if (userScores.Count == 0)
                continue;

            // Classify entries by rank state
            int bothZero = 0, rankOnly = 0, apiRankOnly = 0, bothSet = 0, mismatchCount = 0;
            var rankedEntries = new List<(PlayerScoreDto Entry, int EffectiveRank)>();

            foreach (var entry in userScores)
            {
                bool hasRank = entry.Rank > 0;
                bool hasApiRank = entry.ApiRank > 0;
                if (!hasRank && !hasApiRank) bothZero++;
                else if (hasRank && !hasApiRank) rankOnly++;
                else if (!hasRank && hasApiRank) apiRankOnly++;
                else
                {
                    bothSet++;
                    if (entry.Rank != entry.ApiRank) mismatchCount++;
                }

                // Match ComputeRivals: prefer dense Rank over global ApiRank
                var effectiveRank = entry.Rank > 0 ? entry.Rank : entry.ApiRank;
                if (effectiveRank > 0)
                    rankedEntries.Add((entry, effectiveRank));
            }

            var scan = ScanInstrumentCandidates(db, userId, userScores);
            var aboveThresholdCounts = BuildThresholdCounts(
                scan.Candidates.Values.Where(c => c.AvgSignedDelta < 0));
            var belowThresholdCounts = BuildThresholdCounts(
                scan.Candidates.Values.Where(c => c.AvgSignedDelta >= 0));
            RivalSelectionPreview? selectionPreview = null;
            if (scan.UserSongCount >= MinUserSongsPerInstrument)
            {
                var selection = SelectRivalsWithFallback(scan.Candidates.Values, RivalsPerDirection);
                selectionPreview = new RivalSelectionPreview
                {
                    AboveSelected = selection.Above.Selected.Count,
                    BelowSelected = selection.Below.Selected.Count,
                    LoosestThresholdUsedAbove = selection.Above.LoosestThresholdUsed,
                    LoosestThresholdUsedBelow = selection.Below.LoosestThresholdUsed,
                };
            }

            // Pick median entry by effectiveRank for a probe
            NeighborhoodProbe? probe = null;
            if (rankedEntries.Count > 0)
            {
                rankedEntries.Sort((a, b) => a.EffectiveRank.CompareTo(b.EffectiveRank));
                var median = rankedEntries[rankedEntries.Count / 2];
                var neighbors = db.GetNeighborhood(
                    median.Entry.SongId, median.EffectiveRank, NeighborhoodRadius, userId);
                probe = new NeighborhoodProbe
                {
                    SongId = median.Entry.SongId,
                    EffectiveRank = median.EffectiveRank,
                    Rank = median.Entry.Rank,
                    ApiRank = median.Entry.ApiRank,
                    RangeLo = Math.Max(1, median.EffectiveRank - NeighborhoodRadius),
                    RangeHi = median.EffectiveRank + NeighborhoodRadius,
                    NeighborsFound = neighbors.Count,
                };
            }

            // Sample up to 5 entries showing Rank/ApiRank/Source
            var sampleEntries = userScores
                .Take(5)
                .Select(e => new EntrySample
                {
                    SongId = e.SongId,
                    Score = e.Score,
                    Rank = e.Rank,
                    ApiRank = e.ApiRank,
                })
                .ToList();

            instruments.Add(new InstrumentDiagnostics
            {
                Instrument = instrument,
                TotalSongs = userScores.Count,
                MeetsMinimum = userScores.Count >= MinUserSongsPerInstrument,
                RankedSongs = rankedEntries.Count,
                CandidateCount = scan.Candidates.Count,
                BothZero = bothZero,
                RankOnly = rankOnly,
                ApiRankOnly = apiRankOnly,
                BothSet = bothSet,
                Mismatch = mismatchCount,
                AboveThresholdCounts = aboveThresholdCounts,
                BelowThresholdCounts = belowThresholdCounts,
                SelectionPreview = selectionPreview,
                Probe = probe,
                SampleEntries = sampleEntries,
            });
        }

        return new RivalsDiagnostics { Instruments = instruments };
    }

    internal sealed class InstrumentScanResult
    {
        public int UserSongCount { get; init; }
        public int SongsSkippedUnranked { get; init; }
        public int SongsSkippedSingle { get; init; }
        public int TotalNeighborsFound { get; init; }
        public int SongsScanned { get; init; }
        public Dictionary<string, RivalCandidate> Candidates { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class SelectedRival
    {
        public RivalCandidate Candidate { get; init; } = null!;
        public int ThresholdUsed { get; init; }
    }

    internal sealed class DirectionSelectionResult
    {
        public IReadOnlyList<SelectedRival> Selected { get; init; } = Array.Empty<SelectedRival>();

        public int? LoosestThresholdUsed =>
            Selected.Count == 0 ? null : Selected.Min(s => s.ThresholdUsed);
    }

    internal sealed class RivalFallbackSelection
    {
        public DirectionSelectionResult Above { get; init; } = new();
        public DirectionSelectionResult Below { get; init; } = new();
    }
}

// ─── Diagnostic types ────────────────────────────────────────────

public sealed class RivalsDiagnostics
{
    public IReadOnlyList<InstrumentDiagnostics> Instruments { get; init; } = Array.Empty<InstrumentDiagnostics>();
}

public sealed class InstrumentDiagnostics
{
    public string Instrument { get; init; } = "";
    public int TotalSongs { get; init; }
    public bool MeetsMinimum { get; init; }
    public int RankedSongs { get; init; }
    public int CandidateCount { get; init; }
    /// <summary>Entries with both Rank and ApiRank = 0.</summary>
    public int BothZero { get; init; }
    /// <summary>Entries with Rank > 0 but ApiRank = 0 (scrape-originated).</summary>
    public int RankOnly { get; init; }
    /// <summary>Entries with ApiRank > 0 but Rank = 0 (backfill-originated, not in scrape).</summary>
    public int ApiRankOnly { get; init; }
    /// <summary>Entries with both Rank > 0 and ApiRank > 0.</summary>
    public int BothSet { get; init; }
    /// <summary>Of BothSet entries, how many have Rank != ApiRank.</summary>
    public int Mismatch { get; init; }
    public RivalThresholdCounts AboveThresholdCounts { get; init; } = new();
    public RivalThresholdCounts BelowThresholdCounts { get; init; } = new();
    public RivalSelectionPreview? SelectionPreview { get; init; }
    public NeighborhoodProbe? Probe { get; init; }
    public IReadOnlyList<EntrySample> SampleEntries { get; init; } = Array.Empty<EntrySample>();
}

public sealed class RivalThresholdCounts
{
    public int AtLeastFive { get; init; }
    public int AtLeastFour { get; init; }
    public int AtLeastThree { get; init; }
    public int AtLeastTwo { get; init; }
    public int AtLeastOne { get; init; }
}

public sealed class RivalSelectionPreview
{
    public int AboveSelected { get; init; }
    public int BelowSelected { get; init; }
    public int? LoosestThresholdUsedAbove { get; init; }
    public int? LoosestThresholdUsedBelow { get; init; }
}

public sealed class NeighborhoodProbe
{
    public string SongId { get; init; } = "";
    public int EffectiveRank { get; init; }
    public int Rank { get; init; }
    public int ApiRank { get; init; }
    public int RangeLo { get; init; }
    public int RangeHi { get; init; }
    public int NeighborsFound { get; init; }
}

public sealed class EntrySample
{
    public string SongId { get; init; } = "";
    public int Score { get; init; }
    public int Rank { get; init; }
    public int ApiRank { get; init; }
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
