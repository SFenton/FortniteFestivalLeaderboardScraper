using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates per-instrument and cross-instrument ranking computation.
/// Runs post-scrape after rank recomputation and before rivals.
/// </summary>
public sealed class RankingsCalculator
{
    private const int HistoryTopN = 10_000;
    private const int CredibilityThreshold = 50;
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

        // Load all per-instrument ranking summaries into memory
        var perInstrument = new Dictionary<string, Dictionary<string, (double Rating, int SongsPlayed)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var dict = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, rating, songsPlayed, _) in db.GetAllRankingSummaries())
                dict[accountId] = (rating, songsPlayed);
            perInstrument[instrument] = dict;
        }

        // Generate all combos of size >= 2
        var instrumentList = instruments.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        int n = instrumentList.Count;
        int combosComputed = 0;
        int totalRows = 0;

        for (int mask = 3; mask < (1 << n); mask++) // Start at 3 (binary 11) to skip single-instrument
        {
            if (BitCount(mask) < 2) continue;

            // Build combo key and instrument list for this mask
            var comboInstruments = new List<string>();
            for (int bit = 0; bit < n; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    comboInstruments.Add(instrumentList[bit]);
            }
            var comboKey = string.Join("+", comboInstruments);

            // Intersect accounts across all instruments in this combo + compute weighted rating
            Dictionary<string, (double WeightedSum, int TotalSongs)>? accountRatings = null;

            foreach (var instrument in comboInstruments)
            {
                if (!perInstrument.TryGetValue(instrument, out var instData)) { accountRatings = null; break; }

                if (accountRatings is null)
                {
                    // Seed from first instrument
                    accountRatings = new Dictionary<string, (double, int)>(instData.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var (aid, (rating, songs)) in instData)
                        accountRatings[aid] = (rating * songs, songs);
                }
                else
                {
                    // Intersect: remove accounts not in this instrument, accumulate for those that are
                    var toRemove = new List<string>();
                    foreach (var aid in accountRatings.Keys)
                    {
                        if (!instData.ContainsKey(aid))
                            toRemove.Add(aid);
                    }
                    foreach (var aid in toRemove)
                        accountRatings.Remove(aid);

                    foreach (var (aid, (rating, songs)) in instData)
                    {
                        if (accountRatings.TryGetValue(aid, out var existing))
                            accountRatings[aid] = (existing.WeightedSum + rating * songs, existing.TotalSongs + songs);
                    }
                }
            }

            if (accountRatings is null || accountRatings.Count == 0) continue;

            // Sort by combo rating ASC, then AccountId ASC for deterministic tiebreak
            var sorted = accountRatings
                .Select(kvp => (AccountId: kvp.Key, ComboRating: kvp.Value.WeightedSum / kvp.Value.TotalSongs, SongsPlayed: kvp.Value.TotalSongs))
                .OrderBy(x => x.ComboRating)
                .ThenBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _metaDb.ReplaceComboLeaderboard(comboKey, sorted, sorted.Count);
            combosComputed++;
            totalRows += sorted.Count;
        }

        _log.LogInformation("Computed {Combos} combo leaderboards with {TotalRows:N0} total ranked entries.", combosComputed, totalRows);
    }

    private static int BitCount(int value)
    {
        int count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }

    /// <summary>
    /// Count how many songs are charted for a given instrument (difficulty &gt; 0).
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
                _ => 0,
            };
            if (diff > 0) count++;
        }
        return count;
    }

    private static double? GetInstrumentSkill(Dictionary<string, (double Rating, int SongsPlayed, int Rank)> data, string instrument)
        => data.TryGetValue(instrument, out var v) ? v.Rating : null;

    private static int? GetInstrumentRank(Dictionary<string, (double Rating, int SongsPlayed, int Rank)> data, string instrument)
        => data.TryGetValue(instrument, out var v) ? v.Rank : null;
}
