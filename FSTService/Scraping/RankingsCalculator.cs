using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates per-instrument and cross-instrument ranking computation.
/// Runs post-scrape after rank recomputation and before rivals.
///
/// Ranking metrics:
///   - Adjusted Skill:  AVG(rank/entries) per song, with Bayesian credibility adjustment.
///   - Weighted:  Log₂-weighted AVG(rank/entries) — songs with more leaderboard entries
///                count more — with Bayesian credibility adjustment.
///   - FC Rate:   Percentage of played songs with a full combo, with Bayesian credibility adjustment.
///   - Total Score: Sum of all scores across played songs (no credibility adjustment).
///   - Max Score %: Average of (score / CHOpt max score) per song, with Bayesian credibility adjustment.
///
/// Adjusted Skill, Weighted, FC Rate, and Max Score % apply Bayesian credibility:
///   adjusted = (songs × raw + m × C) / (songs + m)
/// where m = 50 (CredibilityThreshold) and C = 0.5 (PopulationMedian).
/// This pulls accounts with few songs toward the median, preventing
/// 1-song players from dominating the rankings.
/// </summary>
public sealed class RankingsCalculator
{
    private const int HistoryTopN = 10_000;

    /// <summary>Number of songs at which the Bayesian adjustment reaches 50% weight.</summary>
    private const int CredibilityThreshold = 50;

    /// <summary>The assumed population median percentile (0.5 = 50th percentile).</summary>
    private const double PopulationMedian = 0.5;

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly MetaDatabase _metaDb;
    private readonly PathDataStore _pathStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<RankingsCalculator> _log;

    public RankingsCalculator(
        GlobalLeaderboardPersistence persistence,
        MetaDatabase metaDb,
        PathDataStore pathStore,
        ScrapeProgressTracker progress,
        ILogger<RankingsCalculator> log)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _pathStore = pathStore;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Compute all rankings: per-instrument (parallel) → composite → history snapshots → combo rankings.
    /// </summary>
    public async Task ComputeAllAsync(FestivalService festivalService, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allMaxScores = _pathStore.GetAllMaxScores();
        var instruments = _persistence.GetInstrumentKeys();
        var registeredIds = _metaDb.GetRegisteredAccountIds();
        var allPopulation = _metaDb.GetAllLeaderboardPopulation();

        // ── Phase 1+2: SongStats + AccountRankings per instrument (parallel) ──
        // Total steps: instruments(6) + composite(1) + snapshots(instruments+1) + combos(1) = 15
        _progress.BeginPhaseProgress(instruments.Count + 1 + instruments.Count + 1 + 1);
        var tasks = instruments.Select(instrument => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var db = _persistence.GetOrCreateInstrumentDb(instrument);

            // Build per-song max scores for this instrument
            var maxScoresForInstrument = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (songId, songMax) in allMaxScores)
            {
                var max = songMax.GetByInstrument(instrument);
                if (max.HasValue)
                    maxScoresForInstrument[songId] = max;
            }

            // Build per-song real population for this instrument
            var populationForInstrument = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var ((songId, inst), totalEntries) in allPopulation)
            {
                if (inst.Equals(instrument, StringComparison.OrdinalIgnoreCase) && totalEntries > 0)
                    populationForInstrument[songId] = totalEntries;
            }

            // Phase 1: SongStats (uses MAX of local count, previous, real population)
            db.ComputeSongStats(maxScoresForInstrument, populationForInstrument);

            // Phase 2: AccountRankings
            var totalCharted = CountChartedSongs(festivalService, instrument);
            if (totalCharted == 0)
            {
                _log.LogWarning("No charted songs for {Instrument}, skipping rankings.", instrument);
                return;
            }

            db.ComputeAccountRankings(totalCharted, CredibilityThreshold, PopulationMedian);
            _progress.ReportPhaseItemComplete();
        }, ct)).ToList();

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        _log.LogInformation("Per-instrument rankings complete in {Elapsed}.", sw.Elapsed);

        // ── Phase 3: Composite rankings ──
        var compositeSw = System.Diagnostics.Stopwatch.StartNew();
        ComputeCompositeRankings(instruments);
        _progress.ReportPhaseItemComplete();
        _log.LogInformation("Composite rankings complete in {Elapsed}.", compositeSw.Elapsed);

        // ── Phase 4: History snapshots (parallel per instrument + composite) ──
        var snapshotTasks = instruments.Select(instrument => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            db.SnapshotRankHistory(HistoryTopN, registeredIds);
            _progress.ReportPhaseItemComplete();
        }, ct)).ToList();

        await Task.WhenAll(snapshotTasks);
        _metaDb.SnapshotCompositeRankHistory(HistoryTopN, registeredIds);
        _progress.ReportPhaseItemComplete();

        // ── Phase 5: All-combo rankings ──
        var comboSw = System.Diagnostics.Stopwatch.StartNew();
        ComputeAllCombos(instruments);
        _progress.ReportPhaseItemComplete();
        _log.LogInformation("All-combo rankings complete in {Elapsed}.", comboSw.Elapsed);

        _log.LogInformation("Full rankings computation complete in {Total}.", sw.Elapsed);
    }

    /// <summary>
    /// Compute composite rankings by merging per-instrument AccountRankings.
    /// Reads all instrument summaries into memory, aggregates, assigns CompositeRank.
    /// </summary>
    internal void ComputeCompositeRankings(IReadOnlyList<string> instruments)
    {
        // Load per-instrument data into memory: AccountId → { instrument → (rating, songsPlayed, rank) }
        var perAccount = new Dictionary<string, Dictionary<string, (double Rating, int SongsPlayed, int Rank)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var summaries = db.GetAllRankingSummaries();
            foreach (var (accountId, rating, songsPlayed, rank) in summaries)
            {
                if (!perAccount.TryGetValue(accountId, out var dict))
                {
                    dict = new Dictionary<string, (double, int, int)>(StringComparer.OrdinalIgnoreCase);
                    perAccount[accountId] = dict;
                }
                dict[instrument] = (rating, songsPlayed, rank);
            }
        }

        // Compute weighted composite: Σ(rating × songsPlayed) / Σ(songsPlayed)
        var composites = new List<(string AccountId, double CompositeRating, int InstrumentsPlayed, int TotalSongsPlayed,
            Dictionary<string, (double Rating, int SongsPlayed, int Rank)> InstrumentData)>();

        foreach (var (accountId, instrumentData) in perAccount)
        {
            double weightedSum = 0;
            int totalSongs = 0;
            foreach (var (_, (rating, songs, _)) in instrumentData)
            {
                weightedSum += rating * songs;
                totalSongs += songs;
            }

            if (totalSongs == 0) continue;

            composites.Add((accountId, weightedSum / totalSongs, instrumentData.Count, totalSongs, instrumentData));
        }

        // Sort by CompositeRating ASC, TotalSongsPlayed DESC, InstrumentsPlayed DESC, AccountId ASC
        composites.Sort((a, b) =>
        {
            int cmp = a.CompositeRating.CompareTo(b.CompositeRating);
            if (cmp != 0) return cmp;
            cmp = b.TotalSongsPlayed.CompareTo(a.TotalSongsPlayed);
            if (cmp != 0) return cmp;
            cmp = b.InstrumentsPlayed.CompareTo(a.InstrumentsPlayed);
            if (cmp != 0) return cmp;
            return string.Compare(a.AccountId, b.AccountId, StringComparison.OrdinalIgnoreCase);
        });

        // Map to DTOs with ordinal rank
        var rankings = new List<CompositeRankingDto>(composites.Count);
        for (int i = 0; i < composites.Count; i++)
        {
            var c = composites[i];
            rankings.Add(new CompositeRankingDto
            {
                AccountId = c.AccountId,
                InstrumentsPlayed = c.InstrumentsPlayed,
                TotalSongsPlayed = c.TotalSongsPlayed,
                CompositeRating = c.CompositeRating,
                CompositeRank = i + 1,
                GuitarAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Guitar"),
                GuitarSkillRank = GetInstrumentRank(c.InstrumentData, "Solo_Guitar"),
                BassAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Bass"),
                BassSkillRank = GetInstrumentRank(c.InstrumentData, "Solo_Bass"),
                DrumsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Drums"),
                DrumsSkillRank = GetInstrumentRank(c.InstrumentData, "Solo_Drums"),
                VocalsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Vocals"),
                VocalsSkillRank = GetInstrumentRank(c.InstrumentData, "Solo_Vocals"),
                ProGuitarAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralGuitar"),
                ProGuitarSkillRank = GetInstrumentRank(c.InstrumentData, "Solo_PeripheralGuitar"),
                ProBassAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralBass"),
                ProBassSkillRank = GetInstrumentRank(c.InstrumentData, "Solo_PeripheralBass"),
            });
        }

        _metaDb.ReplaceCompositeRankings(rankings);
        _log.LogInformation("Computed composite rankings for {Count:N0} accounts.", rankings.Count);
    }

    /// <summary>
    /// Compute rankings for every multi-instrument combo (2^N - 1 minus singles).
    /// Stores all players' ranks in ComboLeaderboard.
    /// </summary>
    internal void ComputeAllCombos(IReadOnlyList<string> instruments)
    {
        if (instruments.Count < 2)
        {
            _log.LogDebug("Fewer than 2 instruments, skipping combo rankings.");
            return;
        }

        // Load all per-instrument ranking summaries (all 5 metrics) into memory
        var perInstrument = new Dictionary<string, Dictionary<string, AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var dict = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, adj, wgt, fc, ts, ms, songs, fcc) in db.GetAllRankingSummariesFull())
                dict[accountId] = new AccountMetrics(adj, wgt, fc, ts, ms, songs, fcc);
            perInstrument[instrument] = dict;
        }

        // Use ComboIds canonical order — iterate all bitmasks over the full 6-instrument set
        int n = ComboIds.CanonicalOrder.Count;
        int combosComputed = 0;
        int totalRows = 0;

        for (int mask = 3; mask < (1 << n); mask++)
        {
            if (BitCount(mask) < 2) continue;

            // Build instrument list for this mask from canonical order
            var comboInstruments = new List<string>();
            for (int bit = 0; bit < n; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    comboInstruments.Add(ComboIds.CanonicalOrder[bit]);
            }

            // Skip if any instrument in this combo is not in the active set
            if (!comboInstruments.All(i => perInstrument.ContainsKey(i))) continue;

            var comboId = ComboIds.FromMask(mask);
            int comboSize = comboInstruments.Count;

            // Intersect accounts across all instruments in this combo + aggregate metrics
            Dictionary<string, AggregatedMetrics>? accountMetrics = null;

            foreach (var instrument in comboInstruments)
            {
                var instData = perInstrument[instrument];

                if (accountMetrics is null)
                {
                    accountMetrics = new Dictionary<string, AggregatedMetrics>(instData.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var (aid, m) in instData)
                        accountMetrics[aid] = new AggregatedMetrics(
                            m.AdjustedRating * m.SongsPlayed, m.WeightedRating * m.SongsPlayed,
                            m.FullComboCount, m.TotalScore, m.MaxScorePercent, m.SongsPlayed, 1);
                }
                else
                {
                    var toRemove = new List<string>();
                    foreach (var aid in accountMetrics.Keys)
                    {
                        if (!instData.ContainsKey(aid))
                            toRemove.Add(aid);
                    }
                    foreach (var aid in toRemove)
                        accountMetrics.Remove(aid);

                    foreach (var (aid, m) in instData)
                    {
                        if (accountMetrics.TryGetValue(aid, out var existing))
                            accountMetrics[aid] = new AggregatedMetrics(
                                existing.AdjWeightedSum + m.AdjustedRating * m.SongsPlayed,
                                existing.WgtWeightedSum + m.WeightedRating * m.SongsPlayed,
                                existing.TotalFcCount + m.FullComboCount,
                                existing.TotalScore + m.TotalScore,
                                existing.MaxScoreSum + m.MaxScorePercent,
                                existing.TotalSongs + m.SongsPlayed,
                                existing.InstrumentCount + 1);
                    }
                }
            }

            if (accountMetrics is null || accountMetrics.Count == 0) continue;

            // Build final entries with all 5 computed metrics
            var entries = accountMetrics
                .Select(kvp =>
                {
                    var a = kvp.Value;
                    return (
                        AccountId: kvp.Key,
                        AdjustedRating: a.AdjWeightedSum / a.TotalSongs,
                        WeightedRating: a.WgtWeightedSum / a.TotalSongs,
                        FcRate: a.TotalSongs > 0 ? (double)a.TotalFcCount / a.TotalSongs : 0.0,
                        TotalScore: a.TotalScore,
                        MaxScorePercent: a.InstrumentCount > 0 ? a.MaxScoreSum / a.InstrumentCount : 0.0,
                        SongsPlayed: a.TotalSongs,
                        FullComboCount: a.TotalFcCount);
                })
                .ToList();

            _metaDb.ReplaceComboLeaderboard(comboId, entries, entries.Count);
            combosComputed++;
            totalRows += entries.Count;
        }

        _log.LogInformation("Computed {Combos} combo leaderboards with {TotalRows:N0} total ranked entries.", combosComputed, totalRows);
    }

    /// <summary>Per-instrument metric values for a single account.</summary>
    private readonly record struct AccountMetrics(
        double AdjustedRating, double WeightedRating, double FcRate,
        long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount);

    /// <summary>Accumulated metrics across instruments for a single account within a combo.</summary>
    private readonly record struct AggregatedMetrics(
        double AdjWeightedSum, double WgtWeightedSum, int TotalFcCount,
        long TotalScore, double MaxScoreSum, int TotalSongs, int InstrumentCount);

    private static int BitCount(int value)
    {
        int count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }

    /// <summary>
    /// Count how many songs are charted for a given instrument.
    /// Valid difficulty values are 0–6. Values outside this range (e.g. 99 = sentinel/N/A)
    /// indicate the song has no chart for that instrument and are excluded.
    /// </summary>
    internal static int CountChartedSongs(FestivalService festivalService, string instrument)
    {
        int count = 0;
        foreach (var song in festivalService.Songs)
        {
            if (song.track?.@in is null) continue;
            var diff = instrument switch
            {
                "Solo_Guitar" => song.track.@in.gr,
                "Solo_Bass" => song.track.@in.ba,
                "Solo_Vocals" => song.track.@in.vl,
                "Solo_Drums" => song.track.@in.ds,
                "Solo_PeripheralGuitar" => song.track.@in.pg,
                "Solo_PeripheralBass" => song.track.@in.pb,
                _ => -1,
            };
            if (diff >= 0 && diff <= 6) count++;
        }
        return count;
    }

    private static double? GetInstrumentSkill(Dictionary<string, (double Rating, int SongsPlayed, int Rank)> data, string instrument)
        => data.TryGetValue(instrument, out var v) ? v.Rating : null;

    private static int? GetInstrumentRank(Dictionary<string, (double Rating, int SongsPlayed, int Rank)> data, string instrument)
        => data.TryGetValue(instrument, out var v) ? v.Rank : null;
}
