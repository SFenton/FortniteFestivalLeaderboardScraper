using FortniteFestival.Core.Services;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

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
    private const int CredibilityThreshold = 50;

    /// <summary>The assumed population median percentile (0.5 = 50th percentile).</summary>
    private const double PopulationMedian = 0.5;

    /// <summary>Base threshold multiplier for CHOpt max score filtering (+5.0% leeway).</summary>
    private const double BaseThresholdMultiplier = 1.05;

    /// <summary>Maximum threshold multiplier (+5.0% leeway).</summary>
    private const double MaxThresholdMultiplier = 1.05;

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly IPathDataStore _pathStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly FeatureOptions _features;
    private readonly ILogger<RankingsCalculator> _log;

    public RankingsCalculator(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        IPathDataStore pathStore,
        ScrapeProgressTracker progress,
        IOptions<FeatureOptions> features,
        ILogger<RankingsCalculator> log)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _pathStore = pathStore;
        _progress = progress;
        _features = features.Value;
        _log = log;
    }

    /// <summary>
    /// Emit a structured phase-timing marker. Stable prefix <c>[Rankings.Phase]</c> plus
    /// named fields for greppable aggregation. <paramref name="phase"/> keys are stable
    /// (snake-case, dot-separated) and safe to parse offline.
    /// </summary>
    private void LogPhase(string phase, string? instrument, TimeSpan duration, long? rowCount = null)
    {
        _log.LogInformation(
            "[Rankings.Phase] phase={Phase} instrument={Instrument} duration_ms={DurationMs} row_count={RowCount}",
            phase,
            instrument ?? "-",
            (long)duration.TotalMilliseconds,
            rowCount?.ToString() ?? "-");
    }

    /// <summary>
    /// Compute all rankings: per-instrument (parallel) → composite → history snapshots → combo rankings.
    /// </summary>
    public async Task ComputeAllAsync(FestivalService festivalService, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allMaxScores = _pathStore.GetAllMaxScores();
        var instruments = GlobalLeaderboardScraper.AllInstruments;
        var bandTypes = BandInstrumentMapping.AllBandTypes;
        var allPopulation = _metaDb.GetAllLeaderboardPopulation();

        // ── Phase 1+2: SongStats + AccountRankings per instrument (parallel) ──
        // Total steps: instruments(9) + composite(1) + snapshots(instruments+1) + combos(1) + bandTypes(3) = 24
        _progress.BeginPhaseProgress(instruments.Count + 1 + instruments.Count + 1 + 1 + bandTypes.Count);
        _progress.SetSubOperation("per_instrument_rankings");

        // Cap at 2 concurrent instruments to avoid OOM-killing PostgreSQL.
        // Each instrument's ranking pipeline boosts work_mem to 128MB per-session
        // (temp table + indexes + 5 ROW_NUMBER window functions). 6 concurrent
        // pipelines × ~1GB peak would exceed the container memory limit.
        await Parallel.ForEachAsync(instruments,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            (instrument, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var instrumentSw = System.Diagnostics.Stopwatch.StartNew();

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
            var songStatsSw = System.Diagnostics.Stopwatch.StartNew();
            db.ComputeSongStats(maxScoresForInstrument, populationForInstrument);
            songStatsSw.Stop();
            LogPhase("per_instrument.song_stats", instrument, songStatsSw.Elapsed, maxScoresForInstrument.Count);

            // Phase 1.5: Populate valid score overrides for over-threshold entries
            // Finds entries whose current score exceeds 1.05× CHOpt max, then looks up
            // the best valid historical score from ScoreHistory to use in rankings.
            var overridesSw = System.Diagnostics.Stopwatch.StartNew();
            var overThreshold = db.GetOverThresholdEntries();
            long overrideRows = 0;
            if (overThreshold.Count > 0)
            {
                var thresholds = new Dictionary<(string AccountId, string SongId), int>();
                foreach (var (accountId, songId) in overThreshold)
                {
                    if (maxScoresForInstrument.TryGetValue(songId, out var raw) && raw.HasValue)
                        thresholds[(accountId, songId)] = (int)(raw.Value * MaxThresholdMultiplier);
                }

                if (thresholds.Count > 0)
                {
                    var fallbacks = _metaDb.GetBulkBestValidScores(instrument, thresholds);
                    var overrides = fallbacks.Select(kvp => (
                        SongId: kvp.Key.SongId,
                        AccountId: kvp.Key.AccountId,
                        Score: kvp.Value.Score,
                        Accuracy: kvp.Value.Accuracy,
                        IsFullCombo: kvp.Value.IsFullCombo,
                        Stars: kvp.Value.Stars
                    )).ToList();
                    db.PopulateValidScoreOverrides(overrides);
                    overrideRows = overrides.Count;
                    if (overrides.Count > 0)
                        _log.LogInformation("{Instrument}: {OverCount} over-threshold entries, {FallbackCount} valid fallbacks found.",
                            instrument, overThreshold.Count, overrides.Count);
                }
                else
                {
                    db.PopulateValidScoreOverrides([]);
                }
            }
            else
            {
                db.PopulateValidScoreOverrides([]);
            }
            overridesSw.Stop();
            LogPhase("per_instrument.populate_valid_overrides", instrument, overridesSw.Elapsed, overrideRows);

            // Phase 2: AccountRankings + Phase 2.5: Ranking deltas
            // Materialized pipeline: one join into a temp table, reused for
            // base rankings, band-entry discovery, and all bucket deltas.
            var totalCharted = CountChartedSongs(festivalService, instrument);
            if (totalCharted == 0)
            {
                _log.LogWarning("No charted songs for {Instrument}, skipping rankings.", instrument);
                return ValueTask.CompletedTask;
            }

            using var conn = db.OpenConnection();

            // Boost work_mem for this connection only — the global setting is
            // kept low (16 MB) to prevent idle backends from holding huge RSS.
            // The ranking pipeline runs 5 ROW_NUMBER() window functions that
            // benefit from extra sort memory, so we raise it per-session.
            using (var wmCmd = conn.CreateCommand())
            {
                wmCmd.CommandText = "SET work_mem = '128MB'";
                wmCmd.ExecuteNonQuery();
            }

            // Materialize leaderboard_entries × song_stats once
            var matSw = System.Diagnostics.Stopwatch.StartNew();
            db.MaterializeValidEntries(conn, BaseThresholdMultiplier);
            matSw.Stop();
            _log.LogDebug("{Instrument}: materialized valid entries in {Elapsed}.", instrument, matSw.Elapsed);
            LogPhase("per_instrument.materialize_valid_entries", instrument, matSw.Elapsed);

            // Compute base rankings from the materialized temp table
            var arSw = System.Diagnostics.Stopwatch.StartNew();
            db.ComputeAccountRankingsFromMaterialized(conn, totalCharted, CredibilityThreshold, PopulationMedian, BaseThresholdMultiplier);
            arSw.Stop();
            LogPhase("per_instrument.compute_account_rankings", instrument, arSw.Elapsed);
            _progress.ReportPhaseItemComplete();

            // Phase 2.5: Ranking deltas — uses the same materialized temp table
            if (_features.ComputeRankingDeltas)
            {
                var deltaSw = System.Diagnostics.Stopwatch.StartNew();
                var deltaCount = ComputeRankingDeltasFromMaterialized(instrument, conn, db, totalCharted);
                deltaSw.Stop();
                LogPhase("per_instrument.compute_ranking_deltas", instrument, deltaSw.Elapsed, deltaCount);
            }
            else
            {
                _log.LogDebug("{Instrument}: skipping ranking delta computation (ComputeRankingDeltas=false).", instrument);
            }

            instrumentSw.Stop();
            LogPhase("per_instrument.total", instrument, instrumentSw.Elapsed);

            return ValueTask.CompletedTask;
        });

        _log.LogInformation("Per-instrument rankings + deltas complete in {Elapsed}.", sw.Elapsed);
        LogPhase("per_instrument.all", instrument: null, sw.Elapsed);
        var perInstrumentEndMs = sw.ElapsedMilliseconds;

        // ── Load per-instrument ranking data ONCE (shared across phases 3–5) ──
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var rankingDataFull = new Dictionary<string, Dictionary<string, AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        var rankingDataRanks = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        long totalLoadedRows = 0;
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);

            var full = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, adj, wgt, fc, ts, ms, songs, fcc) in db.GetAllRankingSummariesFull())
                full[accountId] = new AccountMetrics(adj, wgt, fc, ts, ms, songs, fcc);
            rankingDataFull[instrument] = full;

            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, _, _, rank) in db.GetAllRankingSummaries())
                ranks[accountId] = rank;
            rankingDataRanks[instrument] = ranks;
            totalLoadedRows += full.Count;
        }
        loadSw.Stop();
        _log.LogInformation("Loaded ranking data: {InstrumentCount} instruments, {TotalAccounts:N0} account-instrument entries.",
            instruments.Count, rankingDataFull.Values.Sum(d => d.Count));
        LogPhase("load_ranking_data", instrument: null, loadSw.Elapsed, totalLoadedRows);

        // ── Phase 3: Composite rankings ──
        _progress.SetSubOperation("composite_rankings");
        var compositeSw = System.Diagnostics.Stopwatch.StartNew();
        ComputeCompositeRankings(instruments, rankingDataFull, rankingDataRanks);
        compositeSw.Stop();
        _progress.ReportPhaseItemComplete();
        _log.LogInformation("Composite rankings complete in {Elapsed}.", compositeSw.Elapsed);
        LogPhase("composite_rankings", instrument: null, compositeSw.Elapsed);

        // ── Phase 3.5: Composite + combo deltas ──
        _progress.SetSubOperation("composite_combo_deltas");
        var crossDeltaSw = System.Diagnostics.Stopwatch.StartNew();
        var heapBeforeDeltas = GC.GetTotalMemory(false);

        // Load per-instrument deltas ONCE, shared by composite and combo delta passes
        var deltaLoadSw = System.Diagnostics.Stopwatch.StartNew();
        var sharedDeltas = LoadPerInstrumentDeltas(instruments);
        deltaLoadSw.Stop();
        LogPhase("cross_deltas.load_deltas", instrument: null, deltaLoadSw.Elapsed,
            sharedDeltas.DeltasPerInstrument.Values.Sum(d => (long)d.Values.Sum(b => b.Count)));

        var compositeDeltaSw = System.Diagnostics.Stopwatch.StartNew();
        var compositeDeltaCount = ComputeCompositeDeltas(instruments, rankingDataFull, preloadedDeltas: sharedDeltas);
        compositeDeltaSw.Stop();
        LogPhase("cross_deltas.composite", instrument: null, compositeDeltaSw.Elapsed, compositeDeltaCount);

        var comboDeltaSw = System.Diagnostics.Stopwatch.StartNew();
        var comboDeltaCount = ComputeComboDeltas(instruments, rankingDataFull, preloadedDeltas: sharedDeltas);
        comboDeltaSw.Stop();
        LogPhase("cross_deltas.combo", instrument: null, comboDeltaSw.Elapsed, comboDeltaCount);

        crossDeltaSw.Stop();
        var heapAfterDeltas = GC.GetTotalMemory(false);
        _log.LogInformation("Composite + combo deltas complete in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
            crossDeltaSw.Elapsed, heapBeforeDeltas, heapAfterDeltas, heapAfterDeltas - heapBeforeDeltas);
        LogPhase("cross_deltas.total", instrument: null, crossDeltaSw.Elapsed);

        // ── Phase 4: History snapshots (parallel per instrument + composite) ──
        _progress.SetSubOperation("rank_history_snapshots");
        var snapshotsSw = System.Diagnostics.Stopwatch.StartNew();
        var snapshotTasks = instruments.Select(instrument => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var instSw = System.Diagnostics.Stopwatch.StartNew();
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            db.SnapshotRankHistory();
            db.SnapshotRankHistoryDeltas();
            instSw.Stop();
            LogPhase("snapshots.per_instrument", instrument, instSw.Elapsed);
            _progress.ReportPhaseItemComplete();
        }, ct)).ToList();

        await Task.WhenAll(snapshotTasks);

        var compositeSnapSw = System.Diagnostics.Stopwatch.StartNew();
        _metaDb.SnapshotCompositeRankHistory();
        compositeSnapSw.Stop();
        LogPhase("snapshots.composite", instrument: null, compositeSnapSw.Elapsed);
        _progress.ReportPhaseItemComplete();
        snapshotsSw.Stop();
        LogPhase("snapshots.total", instrument: null, snapshotsSw.Elapsed);

        // ── Phase 5: All-combo rankings ──
        _progress.SetSubOperation("combo_rankings");
        var comboSw = System.Diagnostics.Stopwatch.StartNew();
        ComputeAllCombos(instruments, rankingDataFull);
        comboSw.Stop();
        _progress.ReportPhaseItemComplete();
        _log.LogInformation("All-combo rankings complete in {Elapsed}.", comboSw.Elapsed);
        LogPhase("all_combo_rankings", instrument: null, comboSw.Elapsed);

        // ── Phase 6: Band team rankings ──
        _progress.SetSubOperation("band_rankings");
        var bandSw = System.Diagnostics.Stopwatch.StartNew();
        var totalBandSongs = festivalService.Songs.Count;
        if (totalBandSongs > 0)
        {
            foreach (var bandType in bandTypes)
            {
                ct.ThrowIfCancellationRequested();
                var perBandSw = System.Diagnostics.Stopwatch.StartNew();
                _metaDb.RebuildBandTeamRankings(bandType, totalBandSongs, CredibilityThreshold, PopulationMedian);
                perBandSw.Stop();
                LogPhase("band_rankings.per_type", bandType, perBandSw.Elapsed);
                _progress.ReportPhaseItemComplete();
            }
        }
        else
        {
            _log.LogWarning("No songs are loaded, skipping band rankings.");
            foreach (var _ in bandTypes)
                _progress.ReportPhaseItemComplete();
        }
        bandSw.Stop();
        LogPhase("band_rankings.total", instrument: null, bandSw.Elapsed);

        _log.LogInformation("Full rankings computation complete in {Total}.", sw.Elapsed);
        LogPhase("total", instrument: null, sw.Elapsed);
        _ = perInstrumentEndMs; // retained for future delta arithmetic if needed
    }

    /// <summary>
    /// Compute composite rankings by merging per-instrument AccountRankings.
    /// Uses pre-loaded ranking data to avoid redundant DB reads.
    /// </summary>
    internal void ComputeCompositeRankings(
        IReadOnlyList<string> instruments,
        Dictionary<string, Dictionary<string, AccountMetrics>>? rankingDataFull = null,
        Dictionary<string, Dictionary<string, int>>? rankingDataRanks = null)
    {
        // Use pre-loaded data or fall back to DB
        var fullData = rankingDataFull ?? LoadPerInstrumentMetrics(instruments);

        // Load per-instrument data from cache: AccountId → { instrument → AccountMetrics }
        // Pre-size using the largest instrument's account count to reduce rehashing
        int estimatedAccounts = 0;
        foreach (var instData in fullData.Values)
            if (instData.Count > estimatedAccounts) estimatedAccounts = instData.Count;
        var perAccount = new Dictionary<string, Dictionary<string, AccountMetrics>>(estimatedAccounts, StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in instruments)
        {
            if (!fullData.TryGetValue(instrument, out var instData)) continue;
            foreach (var (accountId, metrics) in instData)
            {
                if (!perAccount.TryGetValue(accountId, out var dict))
                {
                    dict = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
                    perAccount[accountId] = dict;
                }
                dict[instrument] = metrics;
            }
        }

        // Use pre-loaded adjusted ranks or load from DB
        var perAccountAdjustedRank = rankingDataRanks ?? LoadPerInstrumentRanks(instruments);

        // Build composite data per account
        var composites = new List<CompositeAccountData>(perAccount.Count);

        foreach (var (accountId, instrumentData) in perAccount)
        {
            int totalSongs = 0;
            double adjWeightedSum = 0;
            double wgtWeightedSum = 0;
            double fcWeightedSum = 0;
            double totalScore = 0;
            double msWeightedSum = 0;

            foreach (var (_, m) in instrumentData)
            {
                adjWeightedSum += m.AdjustedRating * m.SongsPlayed;
                wgtWeightedSum += m.WeightedRating * m.SongsPlayed;
                fcWeightedSum += m.FcRate * m.SongsPlayed;
                totalScore += m.TotalScore;
                msWeightedSum += m.MaxScorePercent * m.SongsPlayed;
                totalSongs += m.SongsPlayed;
            }

            if (totalSongs == 0) continue;

            composites.Add(new CompositeAccountData(
                accountId,
                adjWeightedSum / totalSongs,
                wgtWeightedSum / totalSongs,
                fcWeightedSum / totalSongs,
                totalScore,
                msWeightedSum / totalSongs,
                instrumentData.Count,
                totalSongs,
                instrumentData));
        }

        // Rank each metric independently
        // adjusted: ASC (lower = better), weighted: ASC, fcrate: DESC (higher = better),
        // totalscore: DESC, maxscore: DESC
        var adjustedRanks = RankBy(composites, c => c.AdjustedRating, ascending: true);
        var weightedRanks = RankBy(composites, c => c.WeightedRating, ascending: true);
        var fcRateRanks = RankBy(composites, c => c.FcRateRating, ascending: false);
        var totalScoreRanks = RankBy(composites, c => c.TotalScoreRating, ascending: false);
        var maxScoreRanks = RankBy(composites, c => c.MaxScoreRating, ascending: false);

        // Map to DTOs
        var rankings = new List<CompositeRankingDto>(composites.Count);
        for (int i = 0; i < composites.Count; i++)
        {
            var c = composites[i];
            var adjRankDict = perAccountAdjustedRank.GetValueOrDefault(c.AccountId);
            rankings.Add(new CompositeRankingDto
            {
                AccountId = c.AccountId,
                InstrumentsPlayed = c.InstrumentsPlayed,
                TotalSongsPlayed = c.TotalSongsPlayed,
                CompositeRating = c.AdjustedRating,
                CompositeRank = adjustedRanks[c.AccountId],
                GuitarAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Guitar"),
                GuitarSkillRank = adjRankDict?.GetValueOrDefault("Solo_Guitar"),
                BassAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Bass"),
                BassSkillRank = adjRankDict?.GetValueOrDefault("Solo_Bass"),
                DrumsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Drums"),
                DrumsSkillRank = adjRankDict?.GetValueOrDefault("Solo_Drums"),
                VocalsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_Vocals"),
                VocalsSkillRank = adjRankDict?.GetValueOrDefault("Solo_Vocals"),
                ProGuitarAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralGuitar"),
                ProGuitarSkillRank = adjRankDict?.GetValueOrDefault("Solo_PeripheralGuitar"),
                ProBassAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralBass"),
                ProBassSkillRank = adjRankDict?.GetValueOrDefault("Solo_PeripheralBass"),
                ProVocalsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralVocals"),
                ProVocalsSkillRank = adjRankDict?.GetValueOrDefault("Solo_PeripheralVocals"),
                ProCymbalsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralCymbals"),
                ProCymbalsSkillRank = adjRankDict?.GetValueOrDefault("Solo_PeripheralCymbals"),
                ProDrumsAdjustedSkill = GetInstrumentSkill(c.InstrumentData, "Solo_PeripheralDrums"),
                ProDrumsSkillRank = adjRankDict?.GetValueOrDefault("Solo_PeripheralDrums"),
                CompositeRatingWeighted = c.WeightedRating,
                CompositeRankWeighted = weightedRanks[c.AccountId],
                CompositeRatingFcRate = c.FcRateRating,
                CompositeRankFcRate = fcRateRanks[c.AccountId],
                CompositeRatingTotalScore = c.TotalScoreRating,
                CompositeRankTotalScore = totalScoreRanks[c.AccountId],
                CompositeRatingMaxScore = c.MaxScoreRating,
                CompositeRankMaxScore = maxScoreRanks[c.AccountId],
            });
        }

        _metaDb.ReplaceCompositeRankings(rankings);
        _log.LogInformation("Computed composite rankings for {Count:N0} accounts.", rankings.Count);
    }

    /// <summary>Rank a list of composites by a metric, returning AccountId → 1-based rank.</summary>
    private static Dictionary<string, int> RankBy(
        List<CompositeAccountData> composites,
        Func<CompositeAccountData, double> selector,
        bool ascending)
    {
        // Build an index array and sort it instead of copying the entire list 5 times
        var indices = new int[composites.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        Array.Sort(indices, (a, b) =>
        {
            int cmp = ascending
                ? selector(composites[a]).CompareTo(selector(composites[b]))
                : selector(composites[b]).CompareTo(selector(composites[a]));
            if (cmp != 0) return cmp;
            cmp = composites[b].TotalSongsPlayed.CompareTo(composites[a].TotalSongsPlayed);
            if (cmp != 0) return cmp;
            cmp = composites[b].InstrumentsPlayed.CompareTo(composites[a].InstrumentsPlayed);
            if (cmp != 0) return cmp;
            return string.Compare(composites[a].AccountId, composites[b].AccountId, StringComparison.OrdinalIgnoreCase);
        });

        var ranks = new Dictionary<string, int>(indices.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < indices.Length; i++)
            ranks[composites[indices[i]].AccountId] = i + 1;
        return ranks;
    }

    private sealed record CompositeAccountData(
        string AccountId,
        double AdjustedRating,
        double WeightedRating,
        double FcRateRating,
        double TotalScoreRating,
        double MaxScoreRating,
        int InstrumentsPlayed,
        int TotalSongsPlayed,
        Dictionary<string, AccountMetrics> InstrumentData);

    /// <summary>
    /// Compute rankings for every multi-instrument combo (2^N - 1 minus singles).
    /// Stores all players' ranks in ComboLeaderboard.
    /// </summary>
    internal void ComputeAllCombos(IReadOnlyList<string> instruments,
        Dictionary<string, Dictionary<string, AccountMetrics>>? rankingDataFull = null)
    {
        if (instruments.Count < 2)
        {
            _log.LogDebug("Fewer than 2 instruments, skipping combo rankings.");
            return;
        }

        // Use pre-loaded per-instrument ranking summaries or load from DB
        var perInstrument = rankingDataFull ?? LoadPerInstrumentMetrics(instruments);

        // Iterate only within-group combos (no cross-group)
        int combosComputed = 0;
        int totalRows = 0;

        foreach (int mask in ComboIds.WithinGroupComboMasks)
        {
            // Build instrument list for this mask from canonical order
            var comboInstruments = ComboIds.ToInstruments(ComboIds.FromMask(mask));

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

    internal void ComputeBandRankings(IReadOnlyList<string> bandTypes, int totalChartedSongs)
    {
        if (totalChartedSongs <= 0)
        {
            _log.LogWarning("No charted songs available, skipping band rankings.");
            return;
        }

        foreach (var bandType in bandTypes)
            _metaDb.RebuildBandTeamRankings(bandType, totalChartedSongs, CredibilityThreshold, PopulationMedian);

        _log.LogInformation("Computed band rankings for {BandTypeCount} band types.", bandTypes.Count);
    }

    /// <summary>Per-instrument metric values for a single account.</summary>
    internal readonly record struct AccountMetrics(
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
                "Solo_PeripheralVocals" => song.track.@in.bd == 0 ? -1 : song.track.@in.bd,
                "Solo_PeripheralCymbals" => song.track.@in.pd,
                "Solo_PeripheralDrums" => song.track.@in.pd,
                _ => -1,
            };
            if (diff >= 0 && diff <= 6) count++;
        }
        return count;
    }

    private static double? GetInstrumentSkill(Dictionary<string, AccountMetrics> data, string instrument)
        => data.TryGetValue(instrument, out var v) ? v.AdjustedRating : null;

    // ── Ranking delta computation ────────────────────────────────────

    /// <summary>
    /// Compute ranking deltas using the materialized <c>_valid_entries</c> temp table.
    /// Replaces the 101× <c>ComputeMetricsAtThreshold</c> C# loop with a single SQL pass.
    /// Must be called on the same connection that created the temp table.
    /// </summary>
    private int ComputeRankingDeltasFromMaterialized(
        string instrument, Npgsql.NpgsqlConnection conn,
        IInstrumentDatabase db, int totalCharted)
    {
        // Load base metrics from the just-computed account_rankings
        var baseMetrics = new Dictionary<string, InstrumentDatabase.AccountAggregateMetrics>(StringComparer.OrdinalIgnoreCase);
        foreach (var (accountId, adj, wgt, fc, ts, ms, songs, fcc) in db.GetAllRankingSummariesFull())
        {
            baseMetrics[accountId] = new InstrumentDatabase.AccountAggregateMetrics
            {
                SongsPlayed = songs,
                AdjustedSkill = adj,
                Weighted = wgt,
                FcRate = fc,
                TotalScore = ts,
                MaxScorePct = ms,
                FullComboCount = fcc,
            };
        }

        // Get band entries from materialized temp table
        var bandEntries = db.GetBandEntriesFromMaterialized(conn, BaseThresholdMultiplier, MaxThresholdMultiplier);
        if (bandEntries.Count == 0)
        {
            _log.LogDebug("{Instrument}: no band entries found, skipping delta computation.", instrument);
            db.TruncateRankingDeltas();
            return 0;
        }

        // Group affected accounts by their earliest activation bucket
        var affectedAccountsByBucket = new SortedDictionary<double, HashSet<string>>();
        var allAffectedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (accountId, activationLeeway) in bandEntries)
        {
            double bucket = Math.Ceiling(activationLeeway * 10) / 10.0;
            bucket = Math.Clamp(bucket, -4.9, 5.0);
            bucket = Math.Round(bucket, 1);

            if (!affectedAccountsByBucket.TryGetValue(bucket, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                affectedAccountsByBucket[bucket] = set;
            }
            set.Add(accountId);
            allAffectedAccounts.Add(accountId);
        }

        _log.LogInformation("{Instrument}: {AffectedCount} accounts have {BandCount} band entries across {BucketCount} buckets.",
            instrument, allAffectedAccounts.Count, bandEntries.Count, affectedAccountsByBucket.Count);

        db.TruncateRankingDeltas();

        // Single SQL pass: compute metrics for all buckets + unfiltered at once
        var bucketResults = db.ComputeAllBucketDeltas(
            conn, affectedAccountsByBucket, allAffectedAccounts,
            totalCharted, CredibilityThreshold, PopulationMedian);

        // Diff against base and collect deltas
        var allDeltas = new List<(string AccountId, double LeewayBucket,
            int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
            double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)>();

        foreach (var (accountId, bucket, m) in bucketResults)
        {
            if (!baseMetrics.TryGetValue(accountId, out var @base)) continue;
            if (MetricsEqual(@base, m)) continue;

            allDeltas.Add((accountId, bucket, m.SongsPlayed, m.AdjustedSkill,
                m.Weighted, m.FcRate, m.TotalScore, m.MaxScorePct,
                m.FullComboCount, m.AvgAccuracy, m.BestRank, m.Coverage));
        }

        // Write deltas using COPY binary (dense path — always written for fallback)
        db.WriteRankingDeltasBulk(allDeltas);

        // Compress dense deltas to interval tiers and dual-write
        var tiers = InstrumentDatabase.CompressDeltasToTiers(allDeltas);
        db.TruncateRankingDeltaTiers();
        db.WriteRankingDeltaTiersBulk(tiers);

        _log.LogInformation("{Instrument}: wrote {DeltaCount} ranking deltas ({TierCount} tiers, {Ratio:P0} compression) for {AccountCount} affected accounts.",
            instrument, allDeltas.Count, tiers.Count,
            allDeltas.Count > 0 ? 1.0 - (double)tiers.Count / allDeltas.Count : 0.0,
            allAffectedAccounts.Count);
        return allDeltas.Count;
    }

    /// <summary>
    /// Compute ranking metric deltas across all leeway buckets for a single instrument.
    /// For each bucket from -4.9% to +5.0% (101 buckets) plus unfiltered:
    /// 1. Identify accounts with entries in the score band
    /// 2. Compute their aggregate metrics at that threshold
    /// 3. Compare to base (account_rankings at -5.0%), store deltas
    /// </summary>
    internal int ComputeRankingDeltasForInstrument(
        string instrument, FestivalService festivalService,
        Dictionary<string, AccountMetrics>? preloadedMetrics = null)
    {
        var db = _persistence.GetOrCreateInstrumentDb(instrument);
        var totalCharted = CountChartedSongs(festivalService, instrument);
        if (totalCharted == 0) return 0;

        // Load base metrics from pre-loaded data or DB
        var baseMetrics = new Dictionary<string, InstrumentDatabase.AccountAggregateMetrics>(StringComparer.OrdinalIgnoreCase);
        if (preloadedMetrics is not null)
        {
            foreach (var (accountId, m) in preloadedMetrics)
            {
                baseMetrics[accountId] = new InstrumentDatabase.AccountAggregateMetrics
                {
                    SongsPlayed = m.SongsPlayed,
                    AdjustedSkill = m.AdjustedRating,
                    Weighted = m.WeightedRating,
                    FcRate = m.FcRate,
                    TotalScore = m.TotalScore,
                    MaxScorePct = m.MaxScorePercent,
                    FullComboCount = m.FullComboCount,
                };
            }
        }
        else
        {
            foreach (var (accountId, adj, wgt, fc, ts, ms, songs, fcc) in db.GetAllRankingSummariesFull())
            {
                baseMetrics[accountId] = new InstrumentDatabase.AccountAggregateMetrics
                {
                    SongsPlayed = songs,
                    AdjustedSkill = adj,
                    Weighted = wgt,
                    FcRate = fc,
                    TotalScore = ts,
                    MaxScorePct = ms,
                    FullComboCount = fcc,
                };
            }
        }

        // Get all band entries with activation leeway
        var bandEntries = db.GetBandEntries(BaseThresholdMultiplier, MaxThresholdMultiplier);
        if (bandEntries.Count == 0)
        {
            _log.LogDebug("{Instrument}: no band entries found, skipping delta computation.", instrument);
            db.TruncateRankingDeltas();
            return 0;
        }

        // Group affected accounts by their earliest activation bucket (quantized to 0.1)
        var affectedAccountsByBucket = new SortedDictionary<double, HashSet<string>>();
        var allAffectedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (accountId, activationLeeway) in bandEntries)
        {
            // Quantize to nearest 0.1 step, ceiling (entry becomes valid at this bucket)
            double bucket = Math.Ceiling(activationLeeway * 10) / 10.0;
            bucket = Math.Clamp(bucket, -4.9, 5.0);
            bucket = Math.Round(bucket, 1); // avoid floating-point drift

            if (!affectedAccountsByBucket.TryGetValue(bucket, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                affectedAccountsByBucket[bucket] = set;
            }
            set.Add(accountId);
            allAffectedAccounts.Add(accountId);
        }

        _log.LogInformation("{Instrument}: {AffectedCount} accounts have {BandCount} band entries across {BucketCount} buckets.",
            instrument, allAffectedAccounts.Count, bandEntries.Count, affectedAccountsByBucket.Count);

        // Truncate existing deltas
        db.TruncateRankingDeltas();

        // Sweep through buckets -4.9 to 5.0
        var cumulativeAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allDeltas = new List<(string AccountId, double LeewayBucket,
            int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
            double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)>();

        for (double bucket = -4.9; bucket <= 5.05; bucket = Math.Round(bucket + 0.1, 1))
        {
            double roundedBucket = Math.Round(bucket, 1);
            // Add accounts newly affected at this bucket
            if (affectedAccountsByBucket.TryGetValue(roundedBucket, out var newAccounts))
                foreach (var a in newAccounts)
                    cumulativeAccounts.Add(a);

            if (cumulativeAccounts.Count == 0) continue;

            // Compute metrics at this threshold for all cumulative affected accounts
            double threshold = 1.0 + roundedBucket / 100.0;
            var metrics = db.ComputeMetricsAtThreshold(
                threshold, cumulativeAccounts, totalCharted, CredibilityThreshold, PopulationMedian);

            // Diff against base
            foreach (var kvp in metrics)
            {
                var accountId = kvp.Key;
                var m = kvp.Value;
                if (!baseMetrics.TryGetValue(accountId, out var @base)) continue;
                if (MetricsEqual(@base, m)) continue;

                allDeltas.Add((accountId, roundedBucket, m.SongsPlayed, m.AdjustedSkill,
                    m.Weighted, m.FcRate, m.TotalScore, m.MaxScorePct,
                    m.FullComboCount, m.AvgAccuracy, m.BestRank, m.Coverage));
            }
        }

        // Unfiltered bucket: compute for all affected accounts with no threshold
        var unfilteredMetrics = db.ComputeMetricsUnfiltered(
            allAffectedAccounts, totalCharted, CredibilityThreshold, PopulationMedian);
        foreach (var kvp in unfilteredMetrics)
        {
            var accountId = kvp.Key;
            var m = kvp.Value;
            if (!baseMetrics.TryGetValue(accountId, out var @base)) continue;
            if (MetricsEqual(@base, m)) continue;

            // Use a sentinel value for unfiltered bucket (larger than any real bucket)
            allDeltas.Add((accountId, 99.0, m.SongsPlayed, m.AdjustedSkill,
                m.Weighted, m.FcRate, m.TotalScore, m.MaxScorePct,
                m.FullComboCount, m.AvgAccuracy, m.BestRank, m.Coverage));
        }

        // Write all deltas in batch
        db.WriteRankingDeltas(allDeltas);

        _log.LogInformation("{Instrument}: wrote {DeltaCount} ranking deltas for {AccountCount} affected accounts.",
            instrument, allDeltas.Count, allAffectedAccounts.Count);
        return allDeltas.Count;
    }

    private static bool MetricsEqual(InstrumentDatabase.AccountAggregateMetrics a, InstrumentDatabase.AccountAggregateMetrics b) =>
        a.SongsPlayed == b.SongsPlayed &&
        Math.Abs(a.AdjustedSkill - b.AdjustedSkill) < 1e-9 &&
        Math.Abs(a.Weighted - b.Weighted) < 1e-9 &&
        Math.Abs(a.FcRate - b.FcRate) < 1e-9 &&
        a.TotalScore == b.TotalScore &&
        Math.Abs(a.MaxScorePct - b.MaxScorePct) < 1e-9 &&
        a.FullComboCount == b.FullComboCount;

    // ── Composite + combo delta computation ──────────────────────────

    /// <summary>
    /// Compute composite ranking deltas at each leeway bucket.
    /// Loads per-instrument ranking_deltas, merges with base per-instrument metrics,
    /// aggregates into composite, compares to base composite, writes diffs.
    /// </summary>
    internal int ComputeCompositeDeltas(IReadOnlyList<string> instruments,
        Dictionary<string, Dictionary<string, AccountMetrics>>? preloadedPerInstrument = null,
        (Dictionary<string, Dictionary<double, Dictionary<string, AccountMetrics>>> DeltasPerInstrument, SortedSet<double> AllBuckets)? preloadedDeltas = null)
    {
        // Use pre-loaded data or fall back to DB
        var basePerInstrument = preloadedPerInstrument
            ?? LoadPerInstrumentMetrics(instruments);

        // Use pre-loaded deltas or load from DB
        var (deltasPerInstrument, allBuckets) = preloadedDeltas ?? LoadPerInstrumentDeltas(instruments);

        _metaDb.TruncateCompositeRankingDeltas();
        if (allBuckets.Count == 0) return 0;

        // Compute base composite for ALL accounts (not just affected)
        var baseComposite = new Dictionary<string, CompositeMetrics>(StringComparer.OrdinalIgnoreCase);
        var allAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instDict in basePerInstrument.Values)
            foreach (var aid in instDict.Keys)
                allAccounts.Add(aid);

        foreach (var accountId in allAccounts)
        {
            int totalSongs = 0, instCount = 0;
            double adjWS = 0, wgtWS = 0, fcWS = 0, ts = 0, msWS = 0;
            foreach (var instrument in instruments)
            {
                if (basePerInstrument[instrument].TryGetValue(accountId, out var m))
                {
                    adjWS += m.AdjustedRating * m.SongsPlayed;
                    wgtWS += m.WeightedRating * m.SongsPlayed;
                    fcWS += m.FcRate * m.SongsPlayed;
                    ts += m.TotalScore;
                    msWS += m.MaxScorePercent * m.SongsPlayed;
                    totalSongs += m.SongsPlayed;
                    instCount++;
                }
            }
            if (totalSongs > 0)
                baseComposite[accountId] = new CompositeMetrics(
                    adjWS / totalSongs, wgtWS / totalSongs, fcWS / totalSongs,
                    ts, msWS / totalSongs, instCount, totalSongs);
        }

        // Sweep buckets, compute effective composite, diff against base
        var compositeDeltas = new List<(string AccountId, double Bucket, double Adj, double Wgt,
            double Fc, double Ts, double Ms, int Inst, int Songs)>();

        foreach (var bucket in allBuckets)
        {
            // Find accounts affected at this bucket (any instrument has a delta)
            var accountsAtBucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var instrument in instruments)
                if (deltasPerInstrument[instrument].TryGetValue(bucket, out var d))
                    foreach (var aid in d.Keys)
                        accountsAtBucket.Add(aid);

            foreach (var accountId in accountsAtBucket)
            {
                int totalSongs = 0, instCount = 0;
                double adjWS = 0, wgtWS = 0, fcWS = 0, ts = 0, msWS = 0;

                foreach (var instrument in instruments)
                {
                    AccountMetrics m;
                    bool hasData = false;

                    if (deltasPerInstrument[instrument].TryGetValue(bucket, out var bucketDict) &&
                        bucketDict.TryGetValue(accountId, out m))
                        hasData = true;
                    else if (basePerInstrument[instrument].TryGetValue(accountId, out m))
                        hasData = true;

                    if (!hasData) continue;

                    adjWS += m.AdjustedRating * m.SongsPlayed;
                    wgtWS += m.WeightedRating * m.SongsPlayed;
                    fcWS += m.FcRate * m.SongsPlayed;
                    ts += m.TotalScore;
                    msWS += m.MaxScorePercent * m.SongsPlayed;
                    totalSongs += m.SongsPlayed;
                    instCount++;
                }

                if (totalSongs == 0) continue;

                double adj = adjWS / totalSongs;
                double wgt = wgtWS / totalSongs;
                double fc = fcWS / totalSongs;
                double ms = msWS / totalSongs;

                if (!baseComposite.TryGetValue(accountId, out var b)) continue;
                if (Math.Abs(adj - b.AdjustedRating) < 1e-7 &&
                    Math.Abs(wgt - b.WeightedRating) < 1e-7 &&
                    Math.Abs(fc - b.FcRateRating) < 1e-7 &&
                    Math.Abs(ts - b.TotalScore) < 1e-7 &&
                    Math.Abs(ms - b.MaxScoreRating) < 1e-7 &&
                    instCount == b.InstrumentsPlayed && totalSongs == b.TotalSongsPlayed)
                    continue;

                compositeDeltas.Add((accountId, bucket, adj, wgt, fc, ts, ms, instCount, totalSongs));
            }
        }

        _metaDb.WriteCompositeRankingDeltas(compositeDeltas);
        _log.LogInformation("Computed {DeltaCount} composite ranking deltas across {BucketCount} buckets.",
            compositeDeltas.Count, allBuckets.Count);
        return compositeDeltas.Count;
    }

    /// <summary>
    /// Compute combo ranking deltas at each leeway bucket for all within-group combos.
    /// For each combo, at each bucket where any member instrument has a per-instrument delta,
    /// recalculate the combo aggregation and diff against the base combo.
    /// </summary>
    internal int ComputeComboDeltas(IReadOnlyList<string> instruments,
        Dictionary<string, Dictionary<string, AccountMetrics>>? preloadedPerInstrument = null,
        (Dictionary<string, Dictionary<double, Dictionary<string, AccountMetrics>>> DeltasPerInstrument, SortedSet<double> AllBuckets)? preloadedDeltas = null)
    {
        if (instruments.Count < 2) return 0;

        // Use pre-loaded data or fall back to DB
        var basePerInstrument = preloadedPerInstrument
            ?? LoadPerInstrumentMetrics(instruments);

        // Use pre-loaded deltas or load from DB
        var (deltasPerInstrument, allBuckets) = preloadedDeltas ?? LoadPerInstrumentDeltas(instruments);

        _metaDb.TruncateComboRankingDeltas();
        if (allBuckets.Count == 0) return 0;

        // Compute base combo metrics per combo per account
        var baseComboMetrics = new Dictionary<string, Dictionary<string, ComboAccountMetrics>>(StringComparer.Ordinal);

        foreach (int mask in ComboIds.WithinGroupComboMasks)
        {
            var comboInstruments = ComboIds.ToInstruments(ComboIds.FromMask(mask));
            if (!comboInstruments.All(i => basePerInstrument.ContainsKey(i))) continue;
            var comboId = ComboIds.FromMask(mask);

            var perAccount = new Dictionary<string, ComboAccountMetrics>(StringComparer.OrdinalIgnoreCase);
            // Intersect accounts that have ALL instruments in this combo
            HashSet<string>? commonAccounts = null;
            foreach (var inst in comboInstruments)
            {
                var instAccounts = new HashSet<string>(basePerInstrument[inst].Keys, StringComparer.OrdinalIgnoreCase);
                commonAccounts = commonAccounts is null ? instAccounts : new HashSet<string>(commonAccounts.Intersect(instAccounts), StringComparer.OrdinalIgnoreCase);
            }

            if (commonAccounts is null) continue;

            foreach (var aid in commonAccounts)
            {
                double adjWS = 0, wgtWS = 0; int fcc = 0; long ts = 0; double msSum = 0; int totalSongs = 0;
                foreach (var inst in comboInstruments)
                {
                    var m = basePerInstrument[inst][aid];
                    adjWS += m.AdjustedRating * m.SongsPlayed;
                    wgtWS += m.WeightedRating * m.SongsPlayed;
                    fcc += m.FullComboCount;
                    ts += m.TotalScore;
                    msSum += m.MaxScorePercent;
                    totalSongs += m.SongsPlayed;
                }
                if (totalSongs == 0) continue;
                perAccount[aid] = new ComboAccountMetrics(
                    adjWS / totalSongs, wgtWS / totalSongs,
                    totalSongs > 0 ? (double)fcc / totalSongs : 0.0,
                    ts, msSum / comboInstruments.Count, totalSongs, fcc);
            }
            baseComboMetrics[comboId] = perAccount;
        }

        // Sweep buckets, compute effective combo, diff against base
        var comboDeltas = new List<(string ComboId, string AccountId, double Bucket,
            double Adj, double Wgt, double Fc, long Ts, double Ms, int Songs, int Fcc)>();

        foreach (var bucket in allBuckets)
        {
            foreach (int mask in ComboIds.WithinGroupComboMasks)
            {
                var comboInstruments = ComboIds.ToInstruments(ComboIds.FromMask(mask));
                if (!comboInstruments.All(i => basePerInstrument.ContainsKey(i))) continue;
                var comboId = ComboIds.FromMask(mask);
                if (!baseComboMetrics.TryGetValue(comboId, out var baseForCombo)) continue;

                // Check if any member instrument has deltas at this bucket
                bool hasAnyDelta = false;
                foreach (var inst in comboInstruments)
                {
                    if (deltasPerInstrument[inst].TryGetValue(bucket, out var d) && d.Count > 0)
                    { hasAnyDelta = true; break; }
                }
                if (!hasAnyDelta) continue;

                // Recompute for accounts that have all combo instruments AND at least one delta
                foreach (var aid in baseForCombo.Keys)
                {
                    bool anyDelta = false;
                    foreach (var inst in comboInstruments)
                    {
                        if (deltasPerInstrument[inst].TryGetValue(bucket, out var bd) && bd.ContainsKey(aid))
                        { anyDelta = true; break; }
                    }
                    if (!anyDelta) continue;

                    double adjWS = 0, wgtWS = 0; int fcc = 0; long ts = 0; double msSum = 0; int totalSongs = 0;
                    bool allPresent = true;

                    foreach (var inst in comboInstruments)
                    {
                        AccountMetrics m;
                        if (deltasPerInstrument[inst].TryGetValue(bucket, out var bd) && bd.TryGetValue(aid, out m))
                        { /* use delta */ }
                        else if (basePerInstrument[inst].TryGetValue(aid, out m))
                        { /* use base */ }
                        else { allPresent = false; break; }

                        adjWS += m.AdjustedRating * m.SongsPlayed;
                        wgtWS += m.WeightedRating * m.SongsPlayed;
                        fcc += m.FullComboCount;
                        ts += m.TotalScore;
                        msSum += m.MaxScorePercent;
                        totalSongs += m.SongsPlayed;
                    }

                    if (!allPresent || totalSongs == 0) continue;

                    double adj = adjWS / totalSongs;
                    double wgt = wgtWS / totalSongs;
                    double fc = totalSongs > 0 ? (double)fcc / totalSongs : 0.0;
                    double ms = msSum / comboInstruments.Count;

                    var b = baseForCombo[aid];
                    if (Math.Abs(adj - b.AdjustedRating) < 1e-7 &&
                        Math.Abs(wgt - b.WeightedRating) < 1e-7 &&
                        Math.Abs(fc - b.FcRate) < 1e-7 &&
                        ts == (long)b.TotalScore &&
                        Math.Abs(ms - b.MaxScorePct) < 1e-7 &&
                        totalSongs == b.SongsPlayed && fcc == b.FullComboCount)
                        continue;

                    comboDeltas.Add((comboId, aid, bucket, adj, wgt, fc, ts, ms, totalSongs, fcc));
                }
            }
        }

        _metaDb.WriteComboRankingDeltas(comboDeltas);
        _log.LogInformation("Computed {DeltaCount} combo ranking deltas across {BucketCount} buckets for {ComboCount} combos.",
            comboDeltas.Count, allBuckets.Count, ComboIds.WithinGroupComboMasks.Count);
        return comboDeltas.Count;
    }

    private readonly record struct CompositeMetrics(
        double AdjustedRating, double WeightedRating, double FcRateRating,
        double TotalScore, double MaxScoreRating, int InstrumentsPlayed, int TotalSongsPlayed);

    private readonly record struct ComboAccountMetrics(
        double AdjustedRating, double WeightedRating, double FcRate,
        double TotalScore, double MaxScorePct, int SongsPlayed, int FullComboCount);

    /// <summary>
    /// Load per-instrument ranking deltas once, grouped by instrument → bucket → accountId.
    /// Shared between ComputeCompositeDeltas and ComputeComboDeltas to avoid redundant DB reads.
    /// </summary>
    private (Dictionary<string, Dictionary<double, Dictionary<string, AccountMetrics>>> DeltasPerInstrument, SortedSet<double> AllBuckets)
        LoadPerInstrumentDeltas(IReadOnlyList<string> instruments)
    {
        var deltasPerInstrument = new Dictionary<string, Dictionary<double, Dictionary<string, AccountMetrics>>>(StringComparer.OrdinalIgnoreCase);
        var allBuckets = new SortedSet<double>();

        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var allDeltas = db.GetAllRankingDeltas();
            var byBucket = new Dictionary<double, Dictionary<string, AccountMetrics>>();
            foreach (var (accountId, bucket, songs, adj, wgt, fc, ts, ms, fcc) in allDeltas)
            {
                if (!byBucket.TryGetValue(bucket, out var dict))
                {
                    dict = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
                    byBucket[bucket] = dict;
                }
                dict[accountId] = new AccountMetrics(adj, wgt, fc, ts, ms, songs, fcc);
                allBuckets.Add(bucket);
            }
            deltasPerInstrument[instrument] = byBucket;
        }

        return (deltasPerInstrument, allBuckets);
    }

    /// <summary>
    /// Fallback: load per-instrument metrics from DB when pre-loaded data is not available.
    /// Used by internal test methods that call ComputeCompositeDeltas/ComputeComboDeltas directly.
    /// </summary>
    private Dictionary<string, Dictionary<string, AccountMetrics>> LoadPerInstrumentMetrics(
        IReadOnlyList<string> instruments)
    {
        var result = new Dictionary<string, Dictionary<string, AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var dict = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, adj, wgt, fc, ts, ms, songs, fcc) in db.GetAllRankingSummariesFull())
                dict[accountId] = new AccountMetrics(adj, wgt, fc, ts, ms, songs, fcc);
            result[instrument] = dict;
        }
        return result;
    }

    /// <summary>
    /// Fallback: load per-instrument adjusted ranks from DB.
    /// </summary>
    private Dictionary<string, Dictionary<string, int>> LoadPerInstrumentRanks(
        IReadOnlyList<string> instruments)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, _, _, rank) in db.GetAllRankingSummaries())
                ranks[accountId] = rank;
            result[instrument] = ranks;
        }
        return result;
    }
}
