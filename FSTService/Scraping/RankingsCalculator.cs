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
    private readonly ILogger<RankingsCalculator> _log;

    public RankingsCalculator(
        GlobalLeaderboardPersistence persistence,
        MetaDatabase metaDb,
        PathDataStore pathStore,
        ILogger<RankingsCalculator> log)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _pathStore = pathStore;
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
        }, ct)).ToList();

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        _log.LogInformation("Per-instrument rankings complete in {Elapsed}.", sw.Elapsed);

        // ── Phase 3: Composite rankings ──
        var compositeSw = System.Diagnostics.Stopwatch.StartNew();
        ComputeCompositeRankings(instruments);
        _log.LogInformation("Composite rankings complete in {Elapsed}.", compositeSw.Elapsed);

        // ── Phase 4: History snapshots (parallel per instrument + composite) ──
        var snapshotTasks = instruments.Select(instrument => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            db.SnapshotRankHistory(HistoryTopN, registeredIds);
        }, ct)).ToList();

        await Task.WhenAll(snapshotTasks);
        _metaDb.SnapshotCompositeRankHistory(HistoryTopN, registeredIds);

        // ── Phase 5: Combo rankings for registered users ──
        ComputeUserComboRankings(instruments, registeredIds);

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
    /// Compute combo rankings for registered users based on their instrument preferences.
    /// Piggybacks on the in-memory per-instrument data (already read for composite).
    /// </summary>
    internal void ComputeUserComboRankings(IReadOnlyList<string> instruments, IReadOnlySet<string> registeredIds)
    {
        var allPrefs = _metaDb.GetAllUserInstrumentPrefs();
        if (allPrefs.Count == 0)
        {
            _log.LogDebug("No user instrument preferences found, skipping combo rankings.");
            return;
        }

        // Load all per-instrument ranking summaries into memory
        var perInstrument = new Dictionary<string, List<(string AccountId, double Rating, int SongsPlayed)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            perInstrument[instrument] = db.GetAllRankingSummaries()
                .Select(s => (s.AccountId, s.AdjustedSkillRating, s.SongsPlayed))
                .ToList();
        }

        foreach (var (accountId, userInstruments) in allPrefs)
        {
            if (!registeredIds.Contains(accountId)) continue;

            var comboKey = string.Join("+", userInstruments.OrderBy(i => i, StringComparer.OrdinalIgnoreCase));

            // Build combined ratings for all accounts that have entries on ALL instruments in the combo
            var accountRatings = new Dictionary<string, (double WeightedSum, int TotalSongs)>(StringComparer.OrdinalIgnoreCase);

            // Start with the first instrument — seed the dictionary
            bool first = true;
            foreach (var instrument in userInstruments)
            {
                if (!perInstrument.TryGetValue(instrument, out var summaries)) continue;

                if (first)
                {
                    foreach (var (aid, rating, songs) in summaries)
                        accountRatings[aid] = (rating * songs, songs);
                    first = true;
                }
                else
                {
                    // Only keep accounts that also have this instrument
                    var currentAccountIds = new HashSet<string>(summaries.Select(s => s.AccountId), StringComparer.OrdinalIgnoreCase);
                    var toRemove = accountRatings.Keys.Where(k => !currentAccountIds.Contains(k)).ToList();
                    foreach (var k in toRemove) accountRatings.Remove(k);

                    foreach (var (aid, rating, songs) in summaries)
                    {
                        if (accountRatings.TryGetValue(aid, out var existing))
                            accountRatings[aid] = (existing.WeightedSum + rating * songs, existing.TotalSongs + songs);
                    }
                }
                first = false;
            }

            if (!accountRatings.ContainsKey(accountId))
            {
                _log.LogDebug("User {AccountId} has no entries for combo {Combo}, skipping.", accountId, comboKey);
                continue;
            }

            // Sort all accounts by composite rating, find user's rank
            var sorted = accountRatings
                .Select(kvp => (AccountId: kvp.Key, ComboRating: kvp.Value.WeightedSum / kvp.Value.TotalSongs))
                .OrderBy(x => x.ComboRating)
                .ThenBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var userIndex = sorted.FindIndex(x => x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
            if (userIndex < 0) continue;

            var combos = new List<UserComboRankingDto>
            {
                new()
                {
                    InstrumentCombo = comboKey,
                    ComboRating = sorted[userIndex].ComboRating,
                    ComboRank = userIndex + 1,
                    TotalAccountsInCombo = sorted.Count,
                }
            };

            _metaDb.ReplaceUserComboRankings(accountId, combos);
            _log.LogDebug("Computed combo ranking for {AccountId} on {Combo}: rank {Rank}/{Total}.",
                accountId, comboKey, userIndex + 1, sorted.Count);
        }
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
