using System.Collections.Concurrent;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService;
using FSTService.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates per-instrument and cross-instrument ranking computation.
/// Runs post-scrape after rank recomputation and before rivals.
///
/// Ranking metrics:
///   - Adjusted Skill:  AVG(rank/entries) per song, with Bayesian credibility adjustment.
///   - Weighted:  Log₂-weighted AVG(rank/entries) — songs with more leaderboard entries
///                count more — with Bayesian credibility adjustment.
///   - FC Rate:   Full Combos divided by the total charted songs for the instrument.
///   - Total Score: Sum of all scores across played songs (no credibility adjustment).
///   - Max Score %: Average of (score / CHOpt max score) where a max is available,
///                  with Bayesian credibility based on all otherwise-valid scored songs.
///
/// Adjusted Skill, Weighted, and Max Score % apply Bayesian credibility:
///   adjusted = (songs × raw + m × C) / (songs + m)
/// where m = 50 (CredibilityThreshold) and C = 0.5 (PopulationMedian).
/// This pulls accounts with few songs toward the median, preventing
/// 1-song players from dominating the rankings.
/// </summary>
public sealed class RankingsCalculator
{
    internal const int CredibilityThreshold = 50;
    private const string RankHistorySnapshotsBranch = "rank_history_snapshots";
    private const string BandRankingsBranch = "band_rankings";

    /// <summary>The assumed population median percentile (0.5 = 50th percentile).</summary>
    internal const double PopulationMedian = 0.5;

    /// <summary>Base threshold multiplier for CHOpt max score filtering (+5.0% leeway).</summary>
    private const double BaseThresholdMultiplier = 1.05;

    internal static int ComputeMaxScoreThreshold(int maxScore)
        => (int)(maxScore * BaseThresholdMultiplier);

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly IPathDataStore _pathStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly ScraperOptions _scraperOptions;
    private readonly BandRankHistoryOptions _bandRankHistoryOptions;
    private readonly BandTeamRankingRebuildOptions _bandTeamRankingOptions;
    private readonly WorkerStatusPublisher? _workerStatus;
    private readonly ILogger<RankingsCalculator> _log;
    private long _activeScrapeId;

    public RankingsCalculator(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        IPathDataStore pathStore,
        ScrapeProgressTracker progress,
        ILogger<RankingsCalculator> log,
        IOptions<BandRankHistoryOptions>? bandRankHistoryOptions = null,
        IOptions<BandTeamRankingRebuildOptions>? bandTeamRankingOptions = null,
        IOptions<ScraperOptions>? scraperOptions = null,
        WorkerStatusPublisher? workerStatus = null)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _pathStore = pathStore;
        _progress = progress;
        _scraperOptions = scraperOptions?.Value ?? new ScraperOptions();
        _bandRankHistoryOptions = bandRankHistoryOptions?.Value ?? new BandRankHistoryOptions();
        _bandTeamRankingOptions = bandTeamRankingOptions?.Value ?? BandTeamRankingRebuildOptions.Default;
        _workerStatus = workerStatus;
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

        try
        {
            if (_activeScrapeId > 0)
            {
                var completedAt = DateTime.UtcNow;
                _metaDb.RecordScrapePhaseTiming(new ScrapePhaseTimingRecord(
                    _activeScrapeId,
                    "Rankings",
                    phase,
                    instrument,
                    completedAt - duration,
                    completedAt,
                    (long)duration.TotalMilliseconds,
                    RowsWritten: rowCount));
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to persist ranking phase timing for {Phase}.", phase);
        }
    }

    private static string FriendlyInstrumentName(string instrument)
        => instrument switch
        {
            "Solo_Guitar" => "Lead",
            "Solo_Bass" => "Bass",
            "Solo_Drums" => "Drums",
            "Solo_Vocals" => "Vocals",
            "Solo_PeripheralGuitar" => "Pro Lead",
            "Solo_PeripheralBass" => "Pro Bass",
            "Solo_PeripheralVocals" => "Karaoke",
            "Solo_PeripheralCymbals" => "Pro Drums + Cymbals",
            "Solo_PeripheralDrums" => "Pro Drums",
            _ => instrument.Replace('_', ' '),
        };

    private static string FriendlyBandTypeName(string bandType)
        => bandType switch
        {
            "Band_Duets" => "Band Duos",
            "Band_Trios" => "Band Trios",
            "Band_Quad" => "Band Quads",
            _ => bandType.Replace('_', ' '),
        };

    /// <summary>
    /// Compute all rankings: per-instrument (parallel) → composite → combo → history snapshots → band.
    /// </summary>
    public Task ComputeAllAsync(
        FestivalService festivalService,
        CancellationToken ct = default,
        long scrapeId = 0)
        => ComputeAllCoreAsync(
            festivalService,
            ct,
            scrapeId,
            instrumentsToRebuild: null,
            includeRankHistory: true,
            rebuildBandRankings: true,
            maintenanceLease: null);

    internal Task ComputeForMaxScoreMaintenanceAsync(
        FestivalService festivalService,
        IReadOnlyCollection<string> affectedInstruments,
        IMaxScoreMaintenanceLease maintenanceLease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(affectedInstruments);
        var instruments = PathGenerationInstruments.NormalizeExpected(
            affectedInstruments);
        if (instruments.Length == 0
            || instruments.Length != affectedInstruments.Count)
        {
            throw new ArgumentException(
                "Max-score maintenance requires a nonempty unique supported instrument set.",
                nameof(affectedInstruments));
        }

        return ComputeAllCoreAsync(
            festivalService,
            ct,
            scrapeId: 0,
            instrumentsToRebuild: instruments,
            includeRankHistory: false,
            rebuildBandRankings: true,
            maintenanceLease: maintenanceLease);
    }

    private async Task ComputeAllCoreAsync(
        FestivalService festivalService,
        CancellationToken ct,
        long scrapeId,
        IReadOnlyList<string>? instrumentsToRebuild,
        bool includeRankHistory,
        bool rebuildBandRankings,
        IMaxScoreMaintenanceLease? maintenanceLease)
    {
        _activeScrapeId = includeRankHistory ? scrapeId : 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allMaxScores = _pathStore.GetAllMaxScores();
        var pathGenerationStates =
            _pathStore.GetPathGenerationStates();
        var instruments = GlobalLeaderboardScraper.AllInstruments;
        instrumentsToRebuild ??= instruments;
        var bandTypes = BandInstrumentMapping.AllBandTypes;
        var allPopulation = _metaDb.GetAllLeaderboardPopulation();
        var totalChartedByInstrument = instruments.ToDictionary(
            instrument => instrument,
            instrument => CountChartedSongs(
                festivalService.Songs,
                instrument,
                pathGenerationStates),
            StringComparer.OrdinalIgnoreCase);

        // ── Phase 1+2: SongStats + AccountRankings per instrument (parallel) ──
        _progress.BeginPhaseProgress(
            instrumentsToRebuild.Count +
            1 +
            1 +
            (includeRankHistory ? instruments.Count + 1 : 0) +
            1 +
            (rebuildBandRankings ? bandTypes.Count : 0));
        _progress.SetSubOperation("per_instrument_rankings");
        _workerStatus?.BeginOperation("rankings.per_instrument", "Computing solo instrument rankings", phase: "ComputingRankings", subOperation: "per_instrument_rankings");

        // Cap at 2 concurrent instruments to avoid OOM-killing PostgreSQL.
        // Each instrument's ranking pipeline boosts work_mem to 256MB per-session
        // (temp table + indexes + 5 ROW_NUMBER window functions). 6 concurrent
        // pipelines × ~1GB peak would exceed the container memory limit.
        await Parallel.ForEachAsync(instrumentsToRebuild,
            new ParallelOptions
            {
                MaxDegreeOfParallelism =
                    maintenanceLease is null ? 2 : 1,
                CancellationToken = ct,
            },
            async (instrument, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var instrumentSw = System.Diagnostics.Stopwatch.StartNew();
            var operationKey = $"rankings.instrument.{instrument}";
            _workerStatus?.BeginOperation(operationKey, $"Computing {FriendlyInstrumentName(instrument)} Rankings",
                phase: "ComputingRankings", subOperation: "per_instrument_rankings", detail: instrument);

            try
            {

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
            if (maintenanceLease is null)
            {
                db.ComputeCurrentStateSongStats(
                    maxScoresForInstrument,
                    populationForInstrument);
            }
            else
            {
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-song-stats:{instrument}",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        db.ComputeCurrentStateSongStats(
                            maxScoresForInstrument,
                            populationForInstrument,
                            connection,
                            transaction);
                        return Task.CompletedTask;
                    },
                    ct: innerCt);
            }
            songStatsSw.Stop();
            LogPhase("per_instrument.song_stats", instrument, songStatsSw.Elapsed, maxScoresForInstrument.Count);

            // Phase 1.5: Populate valid score overrides for over-threshold entries
            // Finds entries whose current score exceeds 1.05× CHOpt max, then looks up
            // the best valid historical score from ScoreHistory to use in rankings.
            var overridesSw = System.Diagnostics.Stopwatch.StartNew();
            var overThreshold = db.GetCurrentStateOverThresholdEntries();
            long overrideRows = 0;
            IReadOnlyList<(
                string SongId,
                string AccountId,
                int Score,
                int? Accuracy,
                bool? IsFullCombo,
                int? Stars)> overridesToPersist = [];
            if (overThreshold.Count > 0)
            {
                var thresholds = new Dictionary<(string AccountId, string SongId), int>();
                foreach (var (accountId, songId) in overThreshold)
                {
                    if (maxScoresForInstrument.TryGetValue(songId, out var raw) && raw.HasValue)
                        thresholds[(accountId, songId)] =
                            ComputeMaxScoreThreshold(raw.Value);
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
                    overridesToPersist = overrides;
                    overrideRows = overrides.Count;
                    if (overrides.Count > 0)
                        _log.LogInformation("{Instrument}: {OverCount} over-threshold entries, {FallbackCount} valid fallbacks found.",
                            instrument, overThreshold.Count, overrides.Count);
                }
            }
            if (maintenanceLease is null)
            {
                db.PopulateValidScoreOverrides(
                    overridesToPersist);
            }
            else
            {
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-score-overrides:{instrument}",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        db.PopulateValidScoreOverrides(
                            overridesToPersist,
                            connection,
                            transaction);
                        return Task.CompletedTask;
                    },
                    ct: innerCt);
            }
            overridesSw.Stop();
            LogPhase("per_instrument.populate_valid_overrides", instrument, overridesSw.Elapsed, overrideRows);

            // Phase 2: canonical account rankings.
            // Materialize the current leaderboard join once for base rankings.
            var totalCharted = totalChartedByInstrument[instrument];
            if (totalCharted == 0)
            {
                _log.LogWarning("No charted songs for {Instrument}, skipping rankings.", instrument);
                _workerStatus?.CompleteOperation(operationKey, "skipped", "No charted songs");
                return;
            }

            var matSw = System.Diagnostics.Stopwatch.StartNew();
            var arSw = new System.Diagnostics.Stopwatch();
            if (maintenanceLease is null)
            {
                using var conn = db.OpenConnection();
                using (var wmCmd = conn.CreateCommand())
                {
                    wmCmd.CommandText =
                        "SET work_mem = '256MB'; SET maintenance_work_mem = '256MB'";
                    wmCmd.ExecuteNonQuery();
                }

                db.MaterializeCurrentStateValidEntries(
                    conn,
                    BaseThresholdMultiplier);
                matSw.Stop();
                arSw.Start();
                db.ComputeAccountRankingsFromMaterialized(
                    conn,
                    totalCharted,
                    CredibilityThreshold,
                    PopulationMedian,
                    BaseThresholdMultiplier);
                arSw.Stop();
            }
            else
            {
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-account-rankings:{instrument}",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        using (var wmCmd = connection.CreateCommand())
                        {
                            wmCmd.Transaction = transaction;
                            wmCmd.CommandText =
                                "SET LOCAL work_mem = '256MB'; SET LOCAL maintenance_work_mem = '256MB'";
                            wmCmd.ExecuteNonQuery();
                        }

                        db.MaterializeCurrentStateValidEntries(
                            connection,
                            transaction,
                            BaseThresholdMultiplier);
                        matSw.Stop();
                        arSw.Start();
                        db.ComputeAccountRankingsFromMaterialized(
                            connection,
                            transaction,
                            totalCharted,
                            CredibilityThreshold,
                            PopulationMedian,
                            BaseThresholdMultiplier);
                        arSw.Stop();
                        return Task.CompletedTask;
                    },
                    ct: innerCt);
            }
            _log.LogDebug("{Instrument}: materialized valid entries in {Elapsed}.", instrument, matSw.Elapsed);
            LogPhase("per_instrument.materialize_valid_entries", instrument, matSw.Elapsed);

            LogPhase("per_instrument.compute_account_rankings", instrument, arSw.Elapsed);
            _progress.ReportPhaseItemComplete();

            instrumentSw.Stop();
            LogPhase("per_instrument.total", instrument, instrumentSw.Elapsed);
            _workerStatus?.CompleteOperation(operationKey);

            return;
            }
            catch (OperationCanceledException)
            {
                _workerStatus?.CompleteOperation(operationKey, "cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _workerStatus?.FailOperation(operationKey, ex);
                throw;
            }
        });

        _workerStatus?.CompleteOperation("rankings.per_instrument");
        _log.LogInformation("Per-instrument rankings complete in {Elapsed}.", sw.Elapsed);
        LogPhase("per_instrument.all", instrument: null, sw.Elapsed);

        // ── Load per-instrument ranking data ONCE (shared across phases 3–5) ──
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var rankingDataFull = new Dictionary<string, Dictionary<string, AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        var rankingDataRanks = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        long totalLoadedRows = 0;
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);

            var full = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var summary in db.GetAllRankingSummariesDetailed())
                full[summary.AccountId] = new AccountMetrics(
                    summary.AdjustedSkillRating,
                    summary.WeightedRating,
                    summary.FcRate,
                    summary.TotalScore,
                    summary.MaxScorePercent,
                    summary.SongsPlayed,
                    summary.FullComboCount,
                    summary.TotalChartedSongs,
                    summary.RawSkillRating,
                    summary.RawWeightedRating,
                    summary.RawMaxScorePercent);
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
        _workerStatus?.BeginOperation("rankings.composite", "Computing composite rankings", phase: "ComputingRankings", subOperation: "composite_rankings");
        var compositeSw = System.Diagnostics.Stopwatch.StartNew();
        var compositeRankings = ComputeCompositeRankings(
            instruments,
            rankingDataFull,
            rankingDataRanks,
            persist: maintenanceLease is null);
        if (maintenanceLease is not null)
        {
            await maintenanceLease.ExecuteTransactionAsync(
                "derived-composite-rankings",
                requireSourceLocks: true,
                (connection, transaction, _) =>
                {
                    _metaDb.ReplaceCompositeRankings(
                        compositeRankings,
                        connection,
                        transaction);
                    return Task.CompletedTask;
                },
                ct: ct);
        }
        compositeSw.Stop();
        _progress.ReportPhaseItemComplete();
        _workerStatus?.CompleteOperation("rankings.composite");
        _log.LogInformation("Composite rankings complete in {Elapsed}.", compositeSw.Elapsed);
        LogPhase("composite_rankings", instrument: null, compositeSw.Elapsed);

        // ── Phase 3.5: Fixed solo family rankings for Statistics global cards ──
        _progress.SetSubOperation("solo_family_rankings");
        _workerStatus?.BeginOperation("rankings.solo_family", "Computing solo family rankings", phase: "ComputingRankings", subOperation: "solo_family_rankings");
        var familySw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var familyRankings = ComputeSoloFamilyRankings(
                rankingDataFull,
                totalChartedByInstrument,
                persist: maintenanceLease is null);
            if (maintenanceLease is not null)
            {
                await maintenanceLease.ExecuteTransactionAsync(
                    "derived-solo-family-rankings",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        _metaDb.ReplaceSoloFamilyRankings(
                            familyRankings.Rankings,
                            connection,
                            transaction);
                        return Task.CompletedTask;
                    },
                    ct: ct);
            }
            familySw.Stop();
            _progress.ReportPhaseItemComplete();
            _workerStatus?.CompleteOperation("rankings.solo_family");
            _log.LogInformation(
                "Solo family rankings complete in {Elapsed}.",
                familySw.Elapsed);
            LogPhase(
                "solo_family_rankings",
                instrument: null,
                familySw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            familySw.Stop();
            _workerStatus?.CompleteOperation(
                "rankings.solo_family",
                "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            familySw.Stop();
            _workerStatus?.FailOperation(
                "rankings.solo_family",
                ex);
            throw;
        }

        // ── Phase 4: All-combo rankings ──
        _progress.SetSubOperation("combo_rankings");
        _workerStatus?.BeginOperation("rankings.combo", "Computing combo rankings", phase: "ComputingRankings", subOperation: "combo_rankings");
        var comboSw = System.Diagnostics.Stopwatch.StartNew();
        if (maintenanceLease is null)
            ComputeAllCombos(instruments, rankingDataFull);
        else
            await ComputeAllCombosForMaintenanceAsync(
                instruments,
                rankingDataFull,
                maintenanceLease,
                ct);
        comboSw.Stop();
        _progress.ReportPhaseItemComplete();
        _workerStatus?.CompleteOperation("rankings.combo");
        _log.LogInformation("All-combo rankings complete in {Elapsed}.", comboSw.Elapsed);
        LogPhase("all_combo_rankings", instrument: null, comboSw.Elapsed);

        // ── Phase 5+6: History snapshots and band team rankings ──
        if (!includeRankHistory && rebuildBandRankings)
        {
            if (maintenanceLease is null)
            {
                RunBandRankingsWithTiming(
                    bandTypes,
                    festivalService.Songs.Count,
                    scrapeId: 0,
                    ct: ct,
                    recordBandRankHistory: false);
            }
            else
            {
                await RunBandRankingsForMaintenanceAsync(
                    bandTypes,
                    festivalService.Songs.Count,
                    maintenanceLease,
                    ct);
            }
        }
        else if (!includeRankHistory)
        {
            _log.LogInformation(
                "Max-score maintenance ranking rebuild skipped rank history and band rankings.");
        }
        else if (_bandTeamRankingOptions.OverlapRankHistorySnapshotsWithBandRankings)
        {
            await RunRankHistorySnapshotsAndBandRankingsOverlappedAsync(
                instruments,
                bandTypes,
                festivalService.Songs.Count,
                scrapeId,
                ct);
        }
        else
        {
            await SnapshotRankHistoryBestEffortAsync(instruments, ct);
            RunBandRankingsWithTiming(bandTypes, festivalService.Songs.Count, scrapeId, ct);
        }

        _log.LogInformation("Full rankings computation complete in {Total}.", sw.Elapsed);
        LogPhase("total", instrument: null, sw.Elapsed);
    }

    /// <summary>
    /// Compute composite rankings by merging per-instrument AccountRankings.
    /// Uses pre-loaded ranking data to avoid redundant DB reads.
    /// </summary>
    internal IReadOnlyList<CompositeRankingDto> ComputeCompositeRankings(
        IReadOnlyList<string> instruments,
        Dictionary<string, Dictionary<string, AccountMetrics>>? rankingDataFull = null,
        Dictionary<string, Dictionary<string, int>>? rankingDataRanks = null,
        bool persist = true)
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

        if (persist)
            _metaDb.ReplaceCompositeRankings(rankings);
        _log.LogInformation("Computed composite rankings for {Count:N0} accounts.", rankings.Count);
        return rankings;
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

        var comboIds = ComboIds.WithinGroupComboMasks.Select(ComboIds.FromMask).ToList();
        foreach (var leaderboard in ComboLeaderboardBuilder.BuildLeaderboards(comboIds, perInstrument))
        {
            _metaDb.ReplaceComboLeaderboard(leaderboard.ComboId, leaderboard.Entries, leaderboard.Entries.Count);
            combosComputed++;
            totalRows += leaderboard.Entries.Count;
        }

        _log.LogInformation("Computed {Combos} combo leaderboards with {TotalRows:N0} total ranked entries.", combosComputed, totalRows);
    }

    private async Task ComputeAllCombosForMaintenanceAsync(
        IReadOnlyList<string> instruments,
        Dictionary<string, Dictionary<string, AccountMetrics>>
            rankingDataFull,
        IMaxScoreMaintenanceLease maintenanceLease,
        CancellationToken ct)
    {
        if (instruments.Count < 2)
        {
            _log.LogDebug(
                "Fewer than 2 instruments, skipping combo rankings.");
            return;
        }

        var comboIds = ComboIds.WithinGroupComboMasks
            .Select(ComboIds.FromMask)
            .ToList();
        var combosComputed = 0;
        var totalRows = 0;
        foreach (var leaderboard in
                 ComboLeaderboardBuilder.BuildLeaderboards(
                     comboIds,
                     rankingDataFull))
        {
            ct.ThrowIfCancellationRequested();
            await maintenanceLease.ExecuteTransactionAsync(
                $"derived-combo-ranking:{leaderboard.ComboId}",
                requireSourceLocks: true,
                (connection, transaction, _) =>
                {
                    _metaDb.ReplaceComboLeaderboard(
                        leaderboard.ComboId,
                        leaderboard.Entries,
                        leaderboard.Entries.Count,
                        connection,
                        transaction);
                    return Task.CompletedTask;
                },
                ct: ct);
            combosComputed++;
            totalRows += leaderboard.Entries.Count;
        }

        _log.LogInformation(
            "Computed {Combos} fenced combo leaderboards with {TotalRows:N0} total ranked entries.",
            combosComputed,
            totalRows);
    }

    internal SoloFamilyRankingBuildResult ComputeSoloFamilyRankings(
        Dictionary<string, Dictionary<string, AccountMetrics>> rankingDataFull,
        IReadOnlyDictionary<string, int> totalChartedByInstrument,
        bool persist = true)
    {
        var result = SoloFamilyRankingBuilder.BuildRankings(
            SoloFamilyRankingScopes.All,
            rankingDataFull,
            totalChartedByInstrument,
            CredibilityThreshold,
            PopulationMedian);

        foreach (var denominator in result.InstrumentDenominators
                     .Where(static row => row.IsOverride))
        {
            _log.LogWarning(
                "Solo family ranking denominator override for {Instrument}: catalog={CatalogDenominator:N0}, canonical={CanonicalDenominator:N0}, effective={EffectiveDenominator:N0}.",
                denominator.Instrument,
                denominator.CatalogDenominator,
                denominator.CanonicalDenominator,
                denominator.EffectiveDenominator);
        }

        result.ThrowIfInvalid();
        if (persist)
            _metaDb.ReplaceSoloFamilyRankings(result.Rankings);
        _log.LogInformation(
            "Computed solo family rankings for {RowCount:N0} account-scope rows with {OverrideCount:N0} denominator override(s).",
            result.Rankings.Count,
            result.InstrumentDenominators.Count(static row => row.IsOverride));
        return result;
    }

    private async Task RunBandRankingsForMaintenanceAsync(
        IReadOnlyList<string> bandTypes,
        int totalChartedSongs,
        IMaxScoreMaintenanceLease maintenanceLease,
        CancellationToken ct)
    {
        _progress.SetSubOperation("band_rankings");
        var bandSw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var bandType in bandTypes)
        {
            ct.ThrowIfCancellationRequested();
            var operationKey = $"rankings.band.{bandType}";
            _workerStatus?.BeginOperation(
                operationKey,
                $"Computing {FriendlyBandTypeName(bandType)} Rankings",
                phase: "ComputingRankings",
                subOperation: "band_rankings",
                detail: bandType);
            var perBandSw =
                System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-band-ranking:{bandType}",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        _metaDb.RebuildBandTeamRankingsMeasured(
                            bandType,
                            totalChartedSongs,
                            CredibilityThreshold,
                            PopulationMedian,
                            _bandTeamRankingOptions,
                            connection,
                            transaction);
                        return Task.CompletedTask;
                    },
                    ct: ct);
                perBandSw.Stop();
                LogPhase(
                    "band_rankings.per_type",
                    bandType,
                    perBandSw.Elapsed);
                _workerStatus?.CompleteOperation(operationKey);
                _progress.ReportPhaseItemComplete();
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                perBandSw.Stop();
                _workerStatus?.FailOperation(operationKey, ex);
                LogPhase(
                    "band_rankings.per_type.failed",
                    bandType,
                    perBandSw.Elapsed);
                throw;
            }
        }

        bandSw.Stop();
        LogPhase(
            "band_rankings.total",
            instrument: null,
            bandSw.Elapsed);
    }

    private async Task RunRankHistorySnapshotsAndBandRankingsOverlappedAsync(
        IReadOnlyList<string> instruments,
        IReadOnlyList<string> bandTypes,
        int totalChartedSongs,
        long scrapeId,
        CancellationToken ct)
    {
        _progress.SetSubOperation("rank_history_and_band_rankings");
        _progress.RegisterBranches([RankHistorySnapshotsBranch, BandRankingsBranch]);
        _progress.SetBranchTotal(RankHistorySnapshotsBranch, instruments.Count + 1);
        _progress.SetBranchTotal(BandRankingsBranch, bandTypes.Count);

        _log.LogInformation(
            "Running rank-history snapshots and band rankings concurrently; rankings pass completion still waits for both branches.");

        _progress.StartBranch(RankHistorySnapshotsBranch);
        var snapshotsTask = SnapshotRankHistoryBestEffortAsync(instruments, ct, RankHistorySnapshotsBranch)
            .ContinueWith(task =>
            {
                if (task.IsCanceled)
                    _progress.CompleteBranch(RankHistorySnapshotsBranch, "failed", "cancelled");
                else if (task.IsFaulted)
                    _progress.CompleteBranch(RankHistorySnapshotsBranch, "failed", task.Exception?.GetBaseException().Message);
                else
                    _progress.CompleteBranch(RankHistorySnapshotsBranch);

                return task;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            .Unwrap();

        _progress.StartBranch(BandRankingsBranch);
        var bandTask = Task.Run(() =>
        {
            try
            {
                RunBandRankingsWithTiming(bandTypes, totalChartedSongs, scrapeId, ct, BandRankingsBranch);
                _progress.CompleteBranch(BandRankingsBranch);
            }
            catch (OperationCanceledException)
            {
                _progress.CompleteBranch(BandRankingsBranch, "failed", "cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _progress.CompleteBranch(BandRankingsBranch, "failed", ex.Message);
                throw;
            }
        });

        await Task.WhenAll(snapshotsTask, bandTask);
    }

    private void RunBandRankingsWithTiming(
        IReadOnlyList<string> bandTypes,
        int totalChartedSongs,
        long scrapeId,
        CancellationToken ct,
        string? progressBranchId = null,
        bool recordBandRankHistory = true)
    {
        _progress.SetSubOperation("band_rankings");
        var bandSw = System.Diagnostics.Stopwatch.StartNew();
        ComputeBandRankings(
            bandTypes,
            totalChartedSongs,
            scrapeId,
            ct,
            _progress.ReportPhaseItemComplete,
            progressBranchId,
            recordBandRankHistory);
        bandSw.Stop();
        LogPhase("band_rankings.total", instrument: null, bandSw.Elapsed);
    }

    internal void ComputeBandRankings(
        IReadOnlyList<string> bandTypes,
        int totalChartedSongs,
        long scrapeId = 0,
        CancellationToken ct = default,
        Action? onBandComplete = null,
        string? progressBranchId = null,
        bool recordBandRankHistory = true)
    {
        if (totalChartedSongs <= 0)
        {
            _log.LogWarning("No charted songs available, skipping band rankings.");
            foreach (var bandType in bandTypes)
            {
                var operationKey = $"rankings.band.{bandType}";
                _workerStatus?.BeginOperation(operationKey, $"Computing {FriendlyBandTypeName(bandType)} Rankings",
                    phase: "ComputingRankings", subOperation: "band_rankings", detail: bandType);
                _workerStatus?.CompleteOperation(operationKey, "skipped", "No charted songs");
                if (progressBranchId is not null)
                    _progress.IncrementBranchProgress(progressBranchId);
                onBandComplete?.Invoke();
            }
            return;
        }

        var successfulBandTypes = 0;
        var failures = new ConcurrentQueue<(string BandType, Exception Error)>();
        var maxParallelBandTypes = Math.Clamp(_bandTeamRankingOptions.MaxParallelBandTypes, 1, Math.Max(1, bandTypes.Count));
        Parallel.ForEach(
            bandTypes,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelBandTypes, CancellationToken = ct },
            bandType =>
        {
            ct.ThrowIfCancellationRequested();
            var perBandSw = System.Diagnostics.Stopwatch.StartNew();
            var operationKey = $"rankings.band.{bandType}";
            _workerStatus?.BeginOperation(operationKey, $"Computing {FriendlyBandTypeName(bandType)} Rankings",
                phase: "ComputingRankings", subOperation: "band_rankings", detail: bandType);
            try
            {
                RebuildBandTeamRankingsWithDeadlockRetry(
                    bandType,
                    totalChartedSongs,
                    _bandTeamRankingOptions);

                perBandSw.Stop();
                LogPhase("band_rankings.per_type", bandType, perBandSw.Elapsed);
                Interlocked.Increment(ref successfulBandTypes);

                if (recordBandRankHistory)
                HandleBandRankHistoryAfterPublish(bandType, scrapeId, ct);
                _workerStatus?.CompleteOperation(operationKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                perBandSw.Stop();
                _workerStatus?.FailOperation(operationKey, ex);
                _log.LogWarning(ex,
                    "Band team ranking rebuild failed for {BandType}. Continuing with remaining band types.",
                    bandType);
                LogPhase("band_rankings.per_type.failed", bandType, perBandSw.Elapsed);
                failures.Enqueue((bandType, ex));
            }
            finally
            {
                if (progressBranchId is not null)
                    _progress.IncrementBranchProgress(progressBranchId);
                onBandComplete?.Invoke();
            }
        });

        _log.LogInformation("Computed band rankings for {SuccessfulBandTypeCount}/{BandTypeCount} band types.",
            successfulBandTypes, bandTypes.Count);

        if (!failures.IsEmpty)
        {
            var orderedFailures = failures
                .OrderBy(static failure => failure.BandType, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            throw new AggregateException(
                $"Band team ranking rebuild failed for {orderedFailures.Length}/{bandTypes.Count} band type(s): " +
                string.Join(", ", orderedFailures.Select(static failure => failure.BandType)),
                orderedFailures.Select(static failure =>
                    new InvalidOperationException(
                        $"{failure.BandType}: {failure.Error.Message}",
                        failure.Error)));
        }
    }

    private void RebuildBandTeamRankingsWithDeadlockRetry(
        string bandType,
        int totalChartedSongs,
        BandTeamRankingRebuildOptions options)
    {
        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _metaDb.RebuildBandTeamRankings(
                    bandType,
                    totalChartedSongs,
                    CredibilityThreshold,
                    PopulationMedian,
                    options);
                return;
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.DeadlockDetected &&
                attempt < maxAttempts)
            {
                _log.LogWarning(
                    ex,
                    "Band team ranking rebuild deadlocked for {BandType}; retrying attempt {Attempt}/{MaxAttempts}.",
                    bandType,
                    attempt + 1,
                    maxAttempts);
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    private void HandleBandRankHistoryAfterPublish(string bandType, long scrapeId, CancellationToken ct)
    {
        var mode = _bandRankHistoryOptions.Mode;
        var historySw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            switch (mode)
            {
                case BandRankHistoryMode.Disabled:
                    _log.LogInformation("Band ranking history snapshot skipped for {BandType}: mode=Disabled. Current rankings remain published.", bandType);
                    _progress.ReportBandRankHistoryProgress(
                        mode.ToString(),
                        "disabled",
                        bandType,
                        rankingScope: null,
                        comboId: null,
                        chunksCompleted: 0,
                        chunksTotal: 0,
                        rowsScanned: 0,
                        rowsInserted: 0,
                        rowsSkipped: 0,
                        message: "history disabled",
                        updatedAtUtc: DateTime.UtcNow);
                    break;

                case BandRankHistoryMode.Background:
                    var job = _metaDb.EnqueueBandRankHistoryJob(
                        scrapeId,
                        bandType,
                        DateOnly.FromDateTime(DateTime.UtcNow),
                        mode.ToString(),
                        _bandRankHistoryOptions.CoalesceSameDaySnapshots);
                    _log.LogInformation(
                        "Band ranking history job {JobId} queued for {BandType} scrape {ScrapeId}; current rankings remain published.",
                        job.JobId,
                        bandType,
                        scrapeId);
                    _progress.ReportBandRankHistoryProgress(
                        mode.ToString(),
                        "queued",
                        bandType,
                        rankingScope: null,
                        comboId: null,
                        chunksCompleted: job.ChunksCompleted,
                        chunksTotal: job.ChunksTotal,
                        rowsScanned: job.RowsScanned,
                        rowsInserted: job.RowsInserted,
                        rowsSkipped: job.RowsSkipped,
                        message: $"history job {job.JobId} queued",
                        updatedAtUtc: DateTime.UtcNow);
                    break;

                case BandRankHistoryMode.Inline:
                default:
                    var options = new BandRankHistorySnapshotOptions
                    {
                        UseLatestState = _bandRankHistoryOptions.UseLatestState,
                        WriteMode = _bandRankHistoryOptions.WriteMode,
                        UseNarrowHistory = _bandRankHistoryOptions.UseNarrowHistory,
                        UseWideHistoryCompatibilityWrite = _bandRankHistoryOptions.UseWideHistoryCompatibilityWrite,
                        RangeChunkingEnabled = _bandRankHistoryOptions.RangeChunkingEnabled,
                        ChunkSize = _bandRankHistoryOptions.ChunkSize,
                        SynchronousCommitOff = _bandRankHistoryOptions.SynchronousCommitOff,
                        CommandTimeoutSeconds = _bandRankHistoryOptions.CommandTimeoutSeconds,
                        RetentionDays = _bandRankHistoryOptions.RetentionDays,
                        CleanupRetention = false,
                    };
                    var result = _metaDb.SnapshotBandRankHistoryChunked(bandType, options, jobId: null, ct);
                    _progress.ReportBandRankHistoryProgress(
                        mode.ToString(),
                        "complete",
                        bandType,
                        rankingScope: null,
                        comboId: null,
                        chunksCompleted: result.ChunksCompleted,
                        chunksTotal: result.ChunksTotal,
                        rowsScanned: result.RowsScanned,
                        rowsInserted: result.RowsInserted,
                        rowsSkipped: result.RowsSkipped,
                        message: "inline history snapshot complete",
                        updatedAtUtc: DateTime.UtcNow);
                    break;
            }

            historySw.Stop();
            LogPhase(mode == BandRankHistoryMode.Background ? "band_rankings.history_queued" : "band_rankings.history_snapshot", bandType, historySw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            historySw.Stop();
            _log.LogWarning(ex,
                "Band ranking history maintenance failed for {BandType}. Current rankings remain published.",
                bandType);
            LogPhase("band_rankings.history_snapshot.failed", bandType, historySw.Elapsed);
        }
    }

    private async Task SnapshotRankHistoryBestEffortAsync(IReadOnlyList<string> instruments, CancellationToken ct, string? progressBranchId = null)
    {
        _progress.SetSubOperation("rank_history_snapshots");
        var snapshotsSw = System.Diagnostics.Stopwatch.StartNew();
        var maxDegreeOfParallelism = Math.Clamp(
            _scraperOptions.RankHistorySnapshotMaxDegreeOfParallelism,
            1,
            instruments.Count + 1);

        var snapshotItems = instruments.Select(instrument => (Instrument: (string?)instrument, IsComposite: false))
            .Append((Instrument: (string?)null, IsComposite: true));

        await Parallel.ForEachAsync(snapshotItems,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = ct },
            (snapshotItem, token) =>
        {
            token.ThrowIfCancellationRequested();
            var snapshotItemSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (snapshotItem.IsComposite)
                {
                    _metaDb.SnapshotCompositeRankHistory(cleanupRetention: false);
                    snapshotItemSw.Stop();
                    LogPhase("snapshots.composite", instrument: null, snapshotItemSw.Elapsed);
                }
                else
                {
                    var db = _persistence.GetOrCreateInstrumentDb(snapshotItem.Instrument!);
                    db.SnapshotRankHistory(cleanupRetention: false);
                    snapshotItemSw.Stop();
                    LogPhase("snapshots.per_instrument", snapshotItem.Instrument, snapshotItemSw.Elapsed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snapshotItemSw.Stop();
                if (snapshotItem.IsComposite)
                {
                    _log.LogWarning(ex,
                        "Composite rank history snapshot maintenance failed. Continuing without blocking core rankings.");
                    LogPhase("snapshots.composite.failed", instrument: null, snapshotItemSw.Elapsed);
                }
                else
                {
                    _log.LogWarning(ex,
                        "Rank history snapshot maintenance failed for {Instrument}. Continuing without blocking core rankings.",
                        snapshotItem.Instrument);
                    LogPhase("snapshots.per_instrument.failed", snapshotItem.Instrument, snapshotItemSw.Elapsed);
                }
            }
            finally
            {
                if (progressBranchId is not null)
                    _progress.IncrementBranchProgress(progressBranchId);
                _progress.ReportPhaseItemComplete();
            }

            return ValueTask.CompletedTask;
        });

        snapshotsSw.Stop();
        LogPhase("snapshots.total", instrument: null, snapshotsSw.Elapsed);
    }

    /// <summary>Per-instrument metric values for a single account.</summary>
    internal readonly record struct AccountMetrics(
        double AdjustedRating, double WeightedRating, double FcRate,
        long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount,
        int TotalChartedSongs, double RawSkillRating, double? RawWeightedRating, double? RawMaxScorePercent)
    {
        public AccountMetrics(
            double adjustedRating,
            double weightedRating,
            double fcRate,
            long totalScore,
            double maxScorePercent,
            int songsPlayed,
            int fullComboCount)
            : this(
                adjustedRating,
                weightedRating,
                fcRate,
                totalScore,
                maxScorePercent,
                songsPlayed,
                fullComboCount,
                songsPlayed,
                adjustedRating,
                weightedRating,
                maxScorePercent)
        {
        }
    }

    private static int BitCount(int value)
    {
        int count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }

    /// <summary>
    /// Count how many songs are charted for a given instrument.
    /// Negative legacy sentinels and Spark's 99 Karaoke sentinel indicate that
    /// the song has no chart for that instrument and are excluded.
    /// </summary>
    internal static int CountChartedSongs(FestivalService festivalService, string instrument)
        => CountChartedSongs(festivalService.Songs, instrument);

    internal static int CountChartedSongs(
        IEnumerable<Song> songs,
        string instrument)
        => CountChartedSongs(
            songs,
            instrument,
            new Dictionary<string, PathGenerationState>(
                StringComparer.OrdinalIgnoreCase));

    internal static int CountChartedSongs(
        IEnumerable<Song> songs,
        string instrument,
        IReadOnlyDictionary<string, PathGenerationState>
            pathGenerationStates)
    {
        ArgumentNullException.ThrowIfNull(pathGenerationStates);
        if (!GlobalLeaderboardScraper.AllInstruments.Contains(
                instrument,
                StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }

        return songs.Count(song =>
        {
            if (GlobalLeaderboardScraper.TrackSupportsInstrument(
                    song.track,
                    instrument))
            {
                return true;
            }

            var songId = song.track?.su;
            return songId is not null
                && pathGenerationStates.TryGetValue(
                    songId,
                    out var pathState)
                && GlobalLeaderboardScraper
                    .PathStateSupportsInstrument(
                        song,
                        pathState,
                        instrument);
        });
    }

    private static double? GetInstrumentSkill(Dictionary<string, AccountMetrics> data, string instrument)
        => data.TryGetValue(instrument, out var v) ? v.AdjustedRating : null;

    /// <summary>
    /// Fallback: load per-instrument metrics from DB when pre-loaded data is not available.
    /// </summary>
    private Dictionary<string, Dictionary<string, AccountMetrics>> LoadPerInstrumentMetrics(
        IReadOnlyList<string> instruments)
    {
        var result = new Dictionary<string, Dictionary<string, AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var dict = new Dictionary<string, AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var summary in db.GetAllRankingSummariesDetailed())
                dict[summary.AccountId] = new AccountMetrics(
                    summary.AdjustedSkillRating,
                    summary.WeightedRating,
                    summary.FcRate,
                    summary.TotalScore,
                    summary.MaxScorePercent,
                    summary.SongsPlayed,
                    summary.FullComboCount,
                    summary.TotalChartedSongs,
                    summary.RawSkillRating,
                    summary.RawWeightedRating,
                    summary.RawMaxScorePercent);
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
