using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using FortniteFestival.Core.Services;

namespace FortniteFestival.Core.Suggestions
{
public class SuggestionGenerator
{
    private readonly IFestivalService _service;
    private readonly HashSet<string> _emitted = new HashSet<string>();
    private readonly Queue<Func<IEnumerable<SuggestionCategory>>> _pipelines = new Queue<Func<IEnumerable<SuggestionCategory>>>();
    private bool _initialized;
    private readonly Random _rand = new Random();
    private RivalDataIndex _rivalData;
    
    // Standard instrument list used throughout the generator
    private static readonly string[] Instruments = { "guitar", "bass", "drums", "vocals", "pro_guitar", "pro_bass" };
    
    // Session-level exclusion: songs shown in ANY category this session are deprioritized
    private readonly HashSet<string> _sessionShownSongs = new HashSet<string>();
    
    // Reuse / rotation state
    private readonly Queue<string> _recentSongIds = new Queue<string>();
    private readonly Queue<string> _recentArtists = new Queue<string>(); // stored as normalized (lower invariant)
    private static string Canon(string s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToLowerInvariant();
    private const int SongReuseCooldown = 40; // songs in this queue avoided when possible
    private const int ArtistReuseCooldown = 12; // artists in this queue avoided for artist-focused sets
    // Per-category history of songIds already surfaced (for novelty preference)
    private readonly Dictionary<string, HashSet<string>> _categorySongHistory = new Dictionary<string, HashSet<string>>();
    // Track skip streaks for deprioritizing low-diversity categories
    private readonly Dictionary<string,int> _categorySkipStreak = new Dictionary<string,int>();
    
    // Simple conditional debug logger (avoids scattering #if DEBUG everywhere)
    [Conditional("DEBUG")]
    private void DLog(string msg)
    {
        Debug.WriteLine("[SuggestGen] " + msg);
    }

    public SuggestionGenerator(IFestivalService service)
    {
        _service = service;
    }

    /// <summary>Inject rival data for rivalry-aware suggestion strategies. Pass null to disable.</summary>
    public void SetRivalData(RivalDataIndex data)
    {
        _rivalData = data;
    }

    private LeaderboardData TryGetBoard(string songId)
    {
        LeaderboardData ld;
        if (_service.ScoresIndex != null && _service.ScoresIndex.TryGetValue(songId, out ld)) return ld;
        return null;
    }

    private static double? Pct(ScoreTracker t)
    {
        if (t == null || t.percentHit <= 0) return null;
        return t.percentHit / 10000.0;
    }
    private static int? Stars(ScoreTracker t)
    {
        if (t == null || t.numStars <= 0) return null;
        return t.numStars;
    }

    private void EnsurePipelines()
    {
        if (_initialized) return;
        _initialized = true;
        // Build list of strategy delegates then shuffle for randomized ordering each cycle.
        var list = new List<Func<IEnumerable<SuggestionCategory>>>
        {
            FcTheseNext,
            FcTheseNextDecade,
            NearFcRelaxed,
            NearFcRelaxedDecade,
            AlmostSixStars,
            AlmostSixStarsDecade,
            StarGains,
            StarGainsDecade,
            UnFcInstrumentGuitar,
            UnFcInstrumentGuitarDecade,
            UnFcInstrumentBass,
            UnFcInstrumentBassDecade,
            UnFcInstrumentDrums,
            UnFcInstrumentDrumsDecade,
            UnFcInstrumentVocals,
            UnFcInstrumentVocalsDecade,
            UnFcInstrumentProGuitar,
            UnFcInstrumentProGuitarDecade,
            UnFcInstrumentProBass,
            UnFcInstrumentProBassDecade,
            FirstPlaysMixed,
            FirstPlaysMixedDecade,
            UnplayedAll,
            UnplayedAllDecade,
            VarietyPack,
            ArtistSamplerRotating,
            GetMoreStars,
            GetMoreStarsDecade,
            UnplayedPerInstrumentGuitar,
            UnplayedPerInstrumentGuitarDecade,
            UnplayedPerInstrumentBass,
            UnplayedPerInstrumentBassDecade,
            UnplayedPerInstrumentDrums,
            UnplayedPerInstrumentDrumsDecade,
            UnplayedPerInstrumentVocals,
            UnplayedPerInstrumentVocalsDecade,
            UnplayedPerInstrumentProGuitar,
            UnplayedPerInstrumentProGuitarDecade,
            UnplayedPerInstrumentProBass,
            UnplayedPerInstrumentProBassDecade,
            ArtistFocusUnplayed,
            SameNameSets,
            SameNameNearFc
        };
        // Add rival strategies (no-op when _rivalData is null)
        list.Add(SongRivalBattleground);
        list.Add(SongRivalNearFc);
        list.Add(SongRivalStale);
        list.Add(SongRivalStarGains);
        list.Add(SongRivalPctPush);
        if (_rivalData?.SongRivals != null)
        {
            foreach (var r in _rivalData.SongRivals)
            {
                var id = r.AccountId;
                list.Add(() => SongRivalGap(id));
                list.Add(() => SongRivalProtect(id));
                list.Add(() => SongRivalSpotlight(id));
                list.Add(() => SongRivalSlipping(id));
                list.Add(() => SongRivalDominate(id));
            }
        }
        // Fisher-Yates shuffle
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rand.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        foreach (var f in list) _pipelines.Enqueue(f);
    }

    // Fisher-Yates shuffle helper
    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rand.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
    private List<T> ShuffleAndTake<T>(IEnumerable<T> source, int max)
    {
        var list = source.ToList();
        Shuffle(list);
        if (list.Count > max) list = list.Take(max).ToList();
        return list;
    }

    private List<(Song song, ScoreTracker tracker)> SelectNewFirst(string categoryKey, IEnumerable<(Song song, ScoreTracker tracker)> pool, int take)
    {
        var list = pool.ToList();
        if (list.Count == 0 || take <= 0) return new List<(Song, ScoreTracker)>();
        if (!_categorySongHistory.TryGetValue(categoryKey, out var used))
        {
            used = new HashSet<string>();
            _categorySongHistory[categoryKey] = used;
        }
        // If all candidates already used in this category, reset for a fresh cycle
        if (list.All(x => used.Contains(x.song.track.su)))
            used.Clear();
        
        // Priority 1: Songs not shown anywhere this session AND not used in this category
        var freshNew = list.Where(x => !_sessionShownSongs.Contains(x.song.track.su) && !used.Contains(x.song.track.su)).ToList();
        Shuffle(freshNew);
        
        // Priority 2: Songs not used in this category (but may have appeared elsewhere)
        var categoryNew = list.Where(x => !used.Contains(x.song.track.su) && !freshNew.Any(f => f.song.track.su == x.song.track.su)).ToList();
        Shuffle(categoryNew);
        
        // Priority 3: Previously used songs as fallback
        var oldOnes = list.Where(x => used.Contains(x.song.track.su)).ToList();
        Shuffle(oldOnes);
        
        var result = new List<(Song, ScoreTracker)>();
        
        // Take from fresh first
        foreach (var n in freshNew)
        {
            result.Add(n);
            if (result.Count == take) break;
        }
        
        // Then from category-new
        if (result.Count < take)
        {
            foreach (var n in categoryNew)
            {
                result.Add(n);
                if (result.Count == take) break;
            }
        }
        
        // Finally from old as last resort
        if (result.Count < take)
        {
            foreach (var o in oldOnes)
            {
                result.Add(o);
                if (result.Count == take) break;
            }
        }
        
        // Mark songs as used in category history and session
        foreach (var r in result)
        {
            used.Add(r.Item1.track.su);
            _sessionShownSongs.Add(r.Item1.track.su);
        }
        return result;
    }

    // Random display count (2-5) for UX variety
    private int GetDisplayCount() => _rand.Next(2, 6); // upper bound exclusive so 6 => 5 max

    // Filter pool to exclude songs already shown in this session (for counting fresh candidates)
    private List<(Song song, ScoreTracker tracker)> FilterFreshCandidates(IEnumerable<(Song song, ScoreTracker tracker)> pool)
    {
        return pool.Where(x => !_sessionShownSongs.Contains(x.song.track.su)).ToList();
    }
    
    // Get count of fresh (not yet shown this session) candidates for diversity assessment
    private int GetFreshCount(IEnumerable<(Song song, ScoreTracker tracker)> pool)
    {
        return pool.Count(x => !_sessionShownSongs.Contains(x.song.track.su));
    }
    
    // Overload for Song-only lists (used by Unplayed methods)
    private int GetFreshSongCount(IEnumerable<Song> songs)
    {
        return songs.Count(s => !_sessionShownSongs.Contains(s.track.su));
    }

    // Decide whether to emit a category this cycle based on potential candidate diversity.
    // Small candidate pools get probabilistically skipped but never starved (forced after 2 skips).
    private bool ShouldEmit(string key, int candidateCount)
    {
        // Tuned curve: more granularity & slightly harsher on tiny pools to increase freshness.
        double prob = candidateCount >= 80 ? 1.0 :
                       candidateCount >= 50 ? 0.98 :
                       candidateCount >= 35 ? 0.95 :
                       candidateCount >= 25 ? 0.9 :
                       candidateCount >= 18 ? 0.85 :
                       candidateCount >= 12 ? 0.75 :
                       candidateCount >= 8  ? 0.62 :
                       candidateCount >= 5  ? 0.5  : 0.38; // 1-4 very small, still occasionally present
        if (!_categorySkipStreak.TryGetValue(key, out var skipped)) skipped = 0;
        if (skipped >= 2) { _categorySkipStreak[key] = 0; return true; } // force emit after two skips
        bool emit = _rand.NextDouble() < prob;
        _categorySkipStreak[key] = emit ? 0 : skipped + 1;
        return emit;
    }

    // Decade helpers (1970+)
    private static int? GetDecadeStart(int year)
    {
        if (year < 1970 || year > 2099) return null;
        return (year / 10) * 10; // 1987 -> 1980
    }
    private static string DecadeLabel(int decadeStart)
    {
        int two = decadeStart % 100; // 1970 -> 70, 2000 -> 0, 2020 -> 20
        if (two == 0) return "00's"; // 2000s
        return two.ToString("00") + "'s"; // ensures 20 -> 20's
    }
    private IEnumerable<SuggestionCategory> BuildDecadeVariant(
        string baseKey,
        string baseTitle,
        string baseDescription,
        IEnumerable<(Song song, ScoreTracker tracker)> pool)
    {
        // Filter to songs with valid year and decade grouping
        var songs = pool.Where(p => p.song?.track != null && p.song.track.ry > 0)
            .Where(p => GetDecadeStart(p.song.track.ry) != null)
            .ToList();
        if (songs.Count < 2) yield break;
        var decadeGroups = songs.GroupBy(s => GetDecadeStart(s.song.track.ry).Value)
            .Where(g => g.Count() >= 2)
            .ToList();
        if (decadeGroups.Count == 0) yield break;
        Shuffle(decadeGroups);
        var chosen = decadeGroups.First();
        var decadeStart = chosen.Key;
        var decadeLabel = DecadeLabel(decadeStart);
        var take = GetDisplayCount();
        var variantKey = baseKey + "_decade_" + (decadeStart % 100).ToString("00");
        var selection = SelectNewFirst(variantKey, chosen.Select(x => (x.song, x.tracker)), take);
        if (selection.Count < 2) yield break; // ensure meaningful set
        string title;
        if (baseKey == "more_stars") title = $"Push These {decadeLabel} Songs to Gold Stars";
        else if (baseKey.StartsWith("unfc_"))
        {
            var instr = baseKey.Substring(5);
            title = $"Close {InstrumentLabel(instr)} FCs on Songs From the {decadeLabel}";
        }
        else if (baseKey.StartsWith("unplayed_"))
        {
            var instr = baseKey.Substring(9);
            title = instr == "any" ? $"First Plays from the {decadeLabel}" : $"First {InstrumentLabel(instr)} Plays ({decadeLabel})";
        }
        else if (baseKey == "first_plays_mixed") title = $"First Plays (Mixed {decadeLabel})";
        else if (baseKey == "near_fc_relaxed") title = $"Close to FC (92%+) - {decadeLabel}";
        else if (baseKey == "near_fc_any") title = $"FC These Next! ({decadeLabel})";
        else if (baseKey == "almost_six_star") title = $"Push {decadeLabel} Songs to Gold Stars";
        else if (baseKey == "star_gains") title = $"Easy Star Gains ({decadeLabel})";
        else title = baseTitle + " (" + decadeLabel + ")";
        var desc = baseDescription + $" Limited to {decadeLabel} songs.";
        if (baseKey == "unplayed_any") desc = $"Unplayed songs from the {decadeLabel}.";
        if (baseKey.StartsWith("unplayed_") && baseKey != "unplayed_any")
        {
            var instr = baseKey.Substring(9);
            desc = $"Unplayed {InstrumentLabel(instr)} songs from the {decadeLabel}.";
        }
        yield return new SuggestionCategory
        {
            Key = variantKey,
            Title = title,
            Description = desc,
            Songs = selection.Select(MapUniqueSong).ToList()
        };
    }

    public IEnumerable<SuggestionCategory> GetNext(int count)
    {
        EnsurePipelines();
        var produced = new List<SuggestionCategory>();
        int safety = 0; // guard against infinite loop in case of logic issues
        while (produced.Count < count && _pipelines.Count > 0 && safety < 500)
        {
            safety++;
            var pipe = _pipelines.Dequeue();
            foreach (var cat in pipe())
            {
                // Skip duplicate keys or empty song lists.
                if (cat.Songs == null || cat.Songs.Count == 0) continue;
                if (!_emitted.Add(cat.Key)) continue;
                produced.Add(cat);
                if (produced.Count >= count) break;
            }
            // If a non-fallback pipeline only yielded <=1 and we still need more, continue loop to pull next pipeline immediately.
        }
        return produced;
    }

    // Reset for an endless feed (allows previously emitted categories to appear again in a new cycle)
    public void ResetForEndless()
    {
        _initialized = false;
        _pipelines.Clear();
        _emitted.Clear();
        _sessionShownSongs.Clear(); // Allow songs to reappear in new cycle
        EnsurePipelines();
    }

    // (Fallback removed for deterministic single-pass generation.)

    #region Strategy Helpers
    private IEnumerable<SuggestionCategory> FcTheseNext()
    {
        // songs over 95% with 6 stars but not FC (any instrument)
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(tuple => EachTracker(tuple.song, tuple.board, (t,instr) => t != null && t.numStars == 6 && !t.isFullCombo && t.percentHit >= 950000))
            .OrderBy(_ => _rand.Next())
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("near_fc_any", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst("near_fc_any", list, take);
        yield return new SuggestionCategory
        {
            Key = "near_fc_any",
            Title = "FC These Next!",
            Description = "High accuracy Gold Star runs that just need the full combo.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }
    private IEnumerable<SuggestionCategory> FcTheseNextDecade()
    {
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(tuple => EachTracker(tuple.song, tuple.board, (t,instr) => t != null && t.numStars == 6 && !t.isFullCombo && t.percentHit >= 950000))
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("near_fc_any_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("near_fc_any", "FC These Next!", "High accuracy Gold Star runs that just need the full combo.", list))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> NearFcRelaxed()
    {
        // 5★ or 6★, >=92% accuracy, not FC
        var pool = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (sc,i) => sc != null && sc.numStars >=5 && sc.percentHit >= 920000 && !sc.isFullCombo))
            .OrderBy(_ => _rand.Next()).ToList();
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit("near_fc_relaxed", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst("near_fc_relaxed", pool, take);
        yield return new SuggestionCategory
        {
            Key = "near_fc_relaxed",
            Title = "Close to FC (92%+)",
            Description = "High accuracy 5★/Gold Star runs to polish.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }
    private IEnumerable<SuggestionCategory> NearFcRelaxedDecade()
    {
        var pool = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (sc,i) => sc != null && sc.numStars >=5 && sc.percentHit >= 920000 && !sc.isFullCombo))
            .ToList();
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit("near_fc_relaxed_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("near_fc_relaxed", "Close to FC (92%+)", "High accuracy 5★/Gold Star runs to polish.", pool))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> AlmostSixStars()
    {
        // 5★, >=90% accuracy, not 6★ yet (any instrument); encourage pushing to 6★
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (sc,i) => sc != null && sc.numStars == 5 && sc.percentHit >= 900000))
            .OrderBy(_ => _rand.Next()).ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("almost_six_star", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst("almost_six_star", list, take);
        yield return new SuggestionCategory
        {
            Key = "almost_six_star",
            Title = "Push to Gold Stars",
            Description = "High 5★ runs close to Gold Stars.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }
    private IEnumerable<SuggestionCategory> AlmostSixStarsDecade()
    {
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (sc,i) => sc != null && sc.numStars == 5 && sc.percentHit >= 900000))
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("almost_six_star_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("almost_six_star", "Push to Gold Stars", "High 5★ runs close to Gold Stars.", list))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> StarGains()
    {
        // 3–5★ songs with higher percent; sort by stars desc then percent desc
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (sc,i) => sc != null && sc.numStars >=3 && sc.numStars < 6))
            .OrderBy(_ => _rand.Next()).ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("star_gains", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst("star_gains", list, take);
        yield return new SuggestionCategory
        {
            Key = "star_gains",
            Title = "Easy Star Gains",
            Description = "Mid-star songs ripe for improvement.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }
    private IEnumerable<SuggestionCategory> StarGainsDecade()
    {
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (sc,i) => sc != null && sc.numStars >=3 && sc.numStars < 6))
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("star_gains_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("star_gains", "Easy Star Gains", "Mid-star songs ripe for improvement.", list))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> UnFcInstrumentGuitar() => UnFcInstrument("guitar");
    private IEnumerable<SuggestionCategory> UnFcInstrumentBass() => UnFcInstrument("bass");
    private IEnumerable<SuggestionCategory> UnFcInstrumentDrums() => UnFcInstrument("drums");
    private IEnumerable<SuggestionCategory> UnFcInstrumentVocals() => UnFcInstrument("vocals");
    private IEnumerable<SuggestionCategory> UnFcInstrumentProGuitar() => UnFcInstrument("pro_guitar");
    private IEnumerable<SuggestionCategory> UnFcInstrumentProBass() => UnFcInstrument("pro_bass");
    private IEnumerable<SuggestionCategory> UnFcInstrument(string instrument)
    {
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .Select(tuple => (tuple.song, tracker: GetTracker(tuple.board, instrument)))
            .Where(x => x.tracker != null && x.tracker.numStars == 6 && !x.tracker.isFullCombo)
            .OrderBy(_ => _rand.Next())
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit($"unfc_{instrument}", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst($"unfc_{instrument}", list, take);
        yield return new SuggestionCategory
        {
            Key = $"unfc_{instrument}",
            Title = $"Finish the {InstrumentLabel(instrument)} FCs",
            Description = $"Clean up these almost full combos on {InstrumentLabel(instrument)}.",
            Songs = final.Select(x => MapUniqueSong((x.song, x.tracker))).ToList()
        };
    }
    private IEnumerable<SuggestionCategory> UnFcInstrumentGuitarDecade() => UnFcInstrumentDecade("guitar");
    private IEnumerable<SuggestionCategory> UnFcInstrumentBassDecade() => UnFcInstrumentDecade("bass");
    private IEnumerable<SuggestionCategory> UnFcInstrumentDrumsDecade() => UnFcInstrumentDecade("drums");
    private IEnumerable<SuggestionCategory> UnFcInstrumentVocalsDecade() => UnFcInstrumentDecade("vocals");
    private IEnumerable<SuggestionCategory> UnFcInstrumentProGuitarDecade() => UnFcInstrumentDecade("pro_guitar");
    private IEnumerable<SuggestionCategory> UnFcInstrumentProBassDecade() => UnFcInstrumentDecade("pro_bass");
    private IEnumerable<SuggestionCategory> UnFcInstrumentDecade(string instrument)
    {
        var list = _service.Songs
            .Select(s => (song:s, board:TryGetBoard(s.track.su)))
            .Select(tuple => (tuple.song, tracker: GetTracker(tuple.board, instrument)))
            .Where(x => x.tracker != null && x.tracker.numStars == 6 && !x.tracker.isFullCombo)
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit($"unfc_{instrument}_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant($"unfc_{instrument}", $"Finish the {InstrumentLabel(instrument)} FCs", $"Clean up these almost full combos on {InstrumentLabel(instrument)}.", list))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> GetMoreStars()
    {
        // songs with 1-5 stars highest potential (any instrument)
        var list = _service.ScoresIndex.Values
            .SelectMany(ld => EachTracker(null, ld, (t,i) => t != null && t.numStars >=1 && t.numStars < 6))
            .OrderBy(_ => _rand.Next())
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("more_stars", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst("more_stars", list, take);
        yield return new SuggestionCategory
        {
            Key = "more_stars",
            Title = "Push These to Gold Stars",
            Description = "Improve star ratings toward Gold Stars across any instrument.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }
    private IEnumerable<SuggestionCategory> GetMoreStarsDecade()
    {
        var list = _service.ScoresIndex.Values
            .SelectMany(ld => EachTracker(null, ld, (t,i) => t != null && t.numStars >=1 && t.numStars < 6))
            .ToList();
        var freshCount = GetFreshCount(list);
        if (!ShouldEmit("more_stars_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("more_stars", "Push These to Gold Stars", "Improve star ratings toward Gold Stars across any instrument.", list))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> UnplayedAll()
    {
        var list = _service.Songs.Where(s => TryGetBoard(s.track.su) == null)
            .OrderBy(_ => _rand.Next()).ToList();
        var freshCount = GetFreshSongCount(list);
        if (!ShouldEmit("unplayed_any", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst("unplayed_any", list.Select(s => (s,(ScoreTracker)null)), take);
        if (final.Count > 0)
        {
            yield return new SuggestionCategory
            {
                Key = "unplayed_any",
                Title = "Try Something New",
                Description = "Songs you haven't played on any instrument yet.",
                Songs = final.Select(MapUniqueSong).ToList()
            };
        }
    }
    private IEnumerable<SuggestionCategory> UnplayedAllDecade()
    {
        var list = _service.Songs.Where(s => TryGetBoard(s.track.su) == null).ToList();
        var freshCount = GetFreshSongCount(list);
        if (!ShouldEmit("unplayed_any_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("unplayed_any", "Try Something New", "Songs you haven't played on any instrument yet.", list.Select(s => (s,(ScoreTracker)null))))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentGuitar() => UnplayedInstrument("guitar");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentBass() => UnplayedInstrument("bass");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentDrums() => UnplayedInstrument("drums");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentVocals() => UnplayedInstrument("vocals");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentProGuitar() => UnplayedInstrument("pro_guitar");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentProBass() => UnplayedInstrument("pro_bass");
    private IEnumerable<SuggestionCategory> UnplayedInstrument(string instrument)
    {
        var list = _service.Songs
            .Where(s => { var b = TryGetBoard(s.track.su); return b == null || GetTracker(b, instrument) == null || GetTracker(b,instrument).numStars==0; })
            .OrderBy(_ => _rand.Next())
            .ToList();
        var freshCount = GetFreshSongCount(list);
        if (!ShouldEmit($"unplayed_{instrument}", freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst($"unplayed_{instrument}", list.Select(s => (s,(ScoreTracker)null)), take);
        if (final.Count > 0)
        {
            yield return new SuggestionCategory
            {
                Key = $"unplayed_{instrument}",
                Title = $"New on {InstrumentLabel(instrument)}",
                Description = $"Never attempted on {InstrumentLabel(instrument)} yet.",
                Songs = final.Select(MapUniqueSong).ToList()
            };
        }
    }
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentGuitarDecade() => UnplayedInstrumentDecade("guitar");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentBassDecade() => UnplayedInstrumentDecade("bass");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentDrumsDecade() => UnplayedInstrumentDecade("drums");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentVocalsDecade() => UnplayedInstrumentDecade("vocals");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentProGuitarDecade() => UnplayedInstrumentDecade("pro_guitar");
    private IEnumerable<SuggestionCategory> UnplayedPerInstrumentProBassDecade() => UnplayedInstrumentDecade("pro_bass");
    private IEnumerable<SuggestionCategory> UnplayedInstrumentDecade(string instrument)
    {
        var list = _service.Songs
            .Where(s => { var b = TryGetBoard(s.track.su); return b == null || GetTracker(b, instrument) == null || GetTracker(b,instrument).numStars==0; })
            .ToList();
        var freshCount = GetFreshSongCount(list);
        if (!ShouldEmit($"unplayed_{instrument}_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant($"unplayed_{instrument}", $"New on {InstrumentLabel(instrument)}", $"Never attempted on {InstrumentLabel(instrument)} yet.", list.Select(s => (s,(ScoreTracker)null))))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> ArtistSamplerRotating()
    {
        // Rotating prolific artist not used recently
        var artistGroups = _service.Songs.GroupBy(s => Canon(s.track.an))
            .Where(g => g.Count() >= 3)
            .OrderBy(_ => _rand.Next())
            .ToList();
        var chosen = artistGroups.FirstOrDefault();
        if (chosen != null) EnqueueRecentArtist(chosen.Key);
        var picked = chosen == null ? new List<Song>() : chosen.OrderBy(s => s.track.su).Take(10).ToList();
        if (picked.Count > 0) Shuffle(picked);
        if (picked.Count > 5) picked = picked.Take(GetDisplayCount()).ToList();
        // Recover a display name using first song's original artist casing if available
        var artistName = (picked.FirstOrDefault()?.track.ar ?? chosen?.Key) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artistName) || artistName.Trim().Length <= 1) artistName = "Featured Artist";
        // Guard against extremely short/placeholder artist strings (e.g. single letter)
        if (artistName.Length <= 1) artistName = "Featured Artist";
        if (picked.Count > 0 && artistName != "Featured Artist") // suppress placeholder artist
        {
            yield return new SuggestionCategory
            {
                Key = $"artist_sampler_{artistName}",
                Title = $"{artistName} Essentials",
                Description = $"Rotating focus: songs by {artistName} (avoids recently featured artists).",
                Songs = picked.Select(s => MapUniqueSong((s, TryGetBoard(s.track.su)?.guitar ?? TryGetBoard(s.track.su)?.drums))).ToList()
            };
        }
    }
    private IEnumerable<SuggestionCategory> FirstPlaysMixedDecade()
    {
        var result = new List<Song>();
        foreach (var ins in Instruments)
        {
            var subset = _service.Songs.Where(s => { var b = TryGetBoard(s.track.su); var tr = b==null?null:GetTracker(b,ins); return tr==null || tr.numStars==0; })
                .Where(s => !result.Any(r=>r.track.su==s.track.su))
                .Take(2).ToList();
            result.AddRange(subset);
        }
        var freshCount = GetFreshSongCount(result);
        if (!ShouldEmit("first_plays_mixed_decade_wrap", freshCount)) yield break;
        foreach (var dec in BuildDecadeVariant("first_plays_mixed", "First Plays (Mixed)", "Unplayed picks across instruments.", result.Select(s => (s,(ScoreTracker)null))))
            yield return dec;
    }

    private IEnumerable<SuggestionCategory> ArtistFocusUnplayed()
    {
        var artist = _service.Songs
            .Where(s => TryGetBoard(s.track.su) == null)
            .GroupBy(s => Canon(s.track.an))
            .OrderBy(_ => _rand.Next())
            .FirstOrDefault();
    if (artist != null)
        {
            yield return new SuggestionCategory
            {
                Key = $"artist_unplayed_{artist.Key}",
                Title = $"Discover {artist.First().track.ar}",
                Description = $"Unplayed songs from {artist.First().track.ar}.",
                Songs = SelectNewFirst($"artist_unplayed_{artist.Key}", artist.Select(s => (s,(ScoreTracker)null)), GetDisplayCount()).Select(s => MapUniqueSong(s)).ToList()
            };
        }
    }

    private IEnumerable<SuggestionCategory> SameNameSets()
    {
        DLog("--- Duplicate Title Scan (All Songs) using track.tt ---");
        foreach (var s in _service.Songs)
        {
            var raw = s.track?.tt ?? s._title;
            DLog($"TitleTT='{s.track?.tt}' FallbackTitle='{s._title}' Canon='{Canon(raw)}' SU={s.track?.su}");
        }
        var dupGroups = _service.Songs
            .GroupBy(s => Canon(s.track?.tt ?? s._title))
            .Where(gr => gr.Count() >= 2)
            .OrderBy(_ => _rand.Next())
            .ToList();
        if (dupGroups.Count == 0)
        {
            DLog("No duplicate title groups found.");
            yield break;
        }
        DLog($"Found {dupGroups.Count} duplicate title groups.");
        foreach (var grp in dupGroups)
        {
            DLog($"Group Key='{grp.Key}' Count={grp.Count()} -> [" + string.Join(", ", grp.Select(x => x._title + "(" + x.track?.su + ")")) + "]");
        }
    var g = dupGroups.First();
    var songs = SelectNewFirst("samename", g.Select(s => (s,(ScoreTracker)null)), GetDisplayCount()).Select(x => x.song).ToList();
    var displayTitle = (songs.First().track?.tt ?? songs.First()._title).Trim();
        yield return new SuggestionCategory
        {
            Key = $"samename_{displayTitle}",
            Title = $"Songs Named '{displayTitle}'",
            Description = "Different tracks sharing the same title.",
            Songs = songs.Select(s => MapUniqueSong((s,null))).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SameNameNearFc()
    {
        DLog("--- Duplicate Title Near-FC Scan ---");
        var allGroups = _service.Songs
            .Select(s => (song: s, board: TryGetBoard(s.track.su)))
            .GroupBy(x => Canon(x.song.track?.tt ?? x.song._title))
            .Where(gr => gr.Count() >= 2)
            .OrderBy(_ => _rand.Next())
            .ToList();
        if (allGroups.Count == 0)
        {
            DLog("No duplicate groups exist (so none near FC).");
            yield break;
        }
        foreach (var grp in allGroups)
        {
            DLog($"Group '{grp.Key}' candidates: [" + string.Join(", ", grp.Select(x => (x.song.track?.tt ?? x.song._title) + "(stars=" + (x.board?.guitar?.numStars??0) + ")")) + "]");
        }
        var g2 = allGroups
            .Select(gr => new { gr.Key, songs = gr.Where(x => x.board != null).ToList() })
            .Where(x => x.songs.Count >= 2)
            .OrderBy(_ => _rand.Next())
            .FirstOrDefault();
        if (g2 == null)
        {
            DLog("Duplicate title groups exist but none have >=2 scored entries.");
            yield break;
        }
        var poolAll = g2.songs
            .SelectMany(x => EachTracker(x.song, x.board, (t,i)=> t!=null && t.numStars==6 && !t.isFullCombo && t.percentHit>=900000))
            .OrderBy(_ => _rand.Next())
            .Take(30)
            .ToList();
    var pool = SelectNewFirst("samename_nearfc", poolAll, GetDisplayCount());
        var disp = (g2.songs.First().song.track?.tt ?? g2.songs.First().song._title).Trim();
        DLog($"Near-FC duplicate title selected group '{disp}' poolSize={pool.Count}");
        foreach (var p in pool)
        {
            DLog($"  Candidate '{(p.song.track?.tt ?? p.song._title)}' stars={p.tracker.numStars} pct={p.tracker.percentHit} fc={p.tracker.isFullCombo}");
        }
        yield return new SuggestionCategory
        {
            Key = $"samename_nearfc_{disp}",
            Title = $"Close to FC: '{disp}' Variants",
            Description = "Same-name tracks nearly full combo'd.",
            Songs = pool.Select(MapUniqueSong).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> VarietyPack()
    {
        // 5 songs from 5 different artists; prefer songs with some score data if available
        var shuffled = _service.Songs.OrderBy(s => Canon(s.track.an)).ThenBy(s => s.track.su).OrderBy(_ => _rand.Next());
        var usedArtists = new HashSet<string>();
        var picks = new List<Song>();
        foreach (var s in shuffled)
        {
            var aKey = Canon(s.track.an);
            if (usedArtists.Contains(aKey)) continue;
            if (IsSongRecentlyUsed(s.track.su)) continue;
            // Also skip songs already shown this session for variety
            if (_sessionShownSongs.Contains(s.track.su)) continue;
            usedArtists.Add(aKey);
            picks.Add(s);
            if (picks.Count == 5) break;
        }
        var freshCount = GetFreshSongCount(picks);
        if (!ShouldEmit("variety_pack", freshCount)) yield break;
        Shuffle(picks);
        // Select final display set (2-5 random) from unique-artist picks
        var selectedPairs = SelectNewFirst("variety_pack", picks.Select(s => (s, TryGetBoard(s.track.su)?.guitar ?? TryGetBoard(s.track.su)?.drums)), GetDisplayCount());
        var display = selectedPairs.Select(MapUniqueSong).ToList();
        string varietyDesc;
        if (display.Count >= 2)
        {
            if (display.Count == 2) varietyDesc = "Two different artists for variety.";
            else if (display.Count == 3) varietyDesc = "Three different artists for variety.";
            else if (display.Count == 4) varietyDesc = "Four different artists for variety.";
            else varietyDesc = "Five different artists for variety."; // max
        }
        else if (display.Count == 1) varietyDesc = "Only one artist met variety criteria.";
        else varietyDesc = "No variety available.";
        if (display.Count >= 2)
        {
            yield return new SuggestionCategory
            {
                Key = "variety_pack",
                Title = "Variety Pack",
                Description = varietyDesc,
                Songs = display
            };
        }
    }

    private IEnumerable<SuggestionCategory> FirstPlaysMixed()
    {
        // Collect up to 2 unplayed per key instrument without duplicates
        var result = new List<Song>();
        foreach (var ins in Instruments)
        {
            var subset = _service.Songs.Where(s =>
            {
                var b = TryGetBoard(s.track.su);
                var tr = b == null ? null : GetTracker(b, ins);
                return tr == null || tr.numStars == 0;
            })
            .Where(s => !result.Any(r=>r.track.su==s.track.su))
            .OrderBy(_ => _rand.Next()).Take(2).ToList();
            result.AddRange(subset);
        }
        // Need at least 4 to feel meaningful
        Shuffle(result);
        var finalPairs = SelectNewFirst("first_plays_mixed", result.Select(s => (s,(ScoreTracker)null)), GetDisplayCount());
        var final = finalPairs.Select(p => p.song).ToList();
        if (final.Count > 0)
        {
            yield return new SuggestionCategory
            {
                Key = "first_plays_mixed",
                Title = "First Plays (Mixed)",
                Description = "Unplayed picks across instruments.",
                Songs = final.Select(s => MapUniqueSong((s,null))).ToList()
            };
        }
    }

    private IEnumerable<(Song song, ScoreTracker tracker)> EachTracker(Song song, LeaderboardData board, Func<ScoreTracker,string,bool> predicate)
    {
        if (board == null) yield break;
        var resolvedSong = song ?? FindSong(board.songId);
        if (resolvedSong == null) yield break; // Guard against null song
        
        if (board.guitar != null && predicate(board.guitar, "guitar")) yield return (resolvedSong, board.guitar);
        if (board.bass != null && predicate(board.bass, "bass")) yield return (resolvedSong, board.bass);
        if (board.drums != null && predicate(board.drums, "drums")) yield return (resolvedSong, board.drums);
        if (board.vocals != null && predicate(board.vocals, "vocals")) yield return (resolvedSong, board.vocals);
        if (board.pro_guitar != null && predicate(board.pro_guitar, "pro_guitar")) yield return (resolvedSong, board.pro_guitar);
        if (board.pro_bass != null && predicate(board.pro_bass, "pro_bass")) yield return (resolvedSong, board.pro_bass);
    }

    private Song FindSong(string id) => _service.Songs.FirstOrDefault(s => s.track.su == id);

    private static ScoreTracker GetTracker(LeaderboardData board, string instrument)
    {
        if (board == null) return null;
        switch (instrument)
        {
            case "guitar": return board.guitar;
            case "bass": return board.bass;
            case "drums": return board.drums;
            case "vocals": return board.vocals;
            case "pro_guitar": return board.pro_guitar;
            case "pro_bass": return board.pro_bass;
            default: return null;
        }
    }
    private static string InstrumentLabel(string instrument)
    {
        switch (instrument)
        {
            case "guitar": return "Guitar";
            case "bass": return "Bass";
            case "drums": return "Drums";
            case "vocals": return "Vocals";
            case "pro_guitar": return "Pro Guitar";
            case "pro_bass": return "Pro Bass";
            default: return instrument;
        }
    }

    private SuggestionSongItem MapSong((Song song, ScoreTracker tracker) x) => new SuggestionSongItem
    {
        SongId = x.song.track.su,
        Title = x.song._title,
        Artist = x.song.track.ar,
        Stars = Stars(x.tracker),
        Percent = Pct(x.tracker),
        FullCombo = x.tracker != null ? (bool?)x.tracker.isFullCombo : null
    };

    private SuggestionSongItem MapUniqueSong((Song song, ScoreTracker tracker) x)
    {
        AddRecentSong(x.song.track.su, x.song.track.ar);
        return MapSong(x);
    }

    private void AddRecentSong(string songId, string artist)
    {
        _recentSongIds.Enqueue(songId);
        while (_recentSongIds.Count > SongReuseCooldown) _recentSongIds.Dequeue();
        EnqueueRecentArtist(artist);
    }
    private void EnqueueRecentArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        _recentArtists.Enqueue(Canon(artist));
        while (_recentArtists.Count > ArtistReuseCooldown) _recentArtists.Dequeue();
    }
    private bool IsSongRecentlyUsed(string songId) => _recentSongIds.Contains(songId);
    #endregion

    #region Rival Strategies
    private SuggestionSongItem AnnotateWithRival(Song song, string instrument)
    {
        if (_rivalData == null) return null;
        var key = $"{song.track.su}:{instrument}";
        if (!_rivalData.ClosestRivalBySong.TryGetValue(key, out var match)) return null;
        return new SuggestionSongItem
        {
            RivalName = match.Rival.DisplayName,
            RivalAccountId = match.Rival.AccountId,
            RivalRankDelta = match.RankDelta,
            RivalSource = match.Rival.Source == RivalSourceKind.Song ? "song" : "leaderboard",
        };
    }

    private SuggestionSongItem MapRivalSong(Song song, ScoreTracker tracker, RivalInfo rival, int rankDelta)
    {
        var item = MapUniqueSong((song, tracker));
        item.RivalName = rival.DisplayName;
        item.RivalAccountId = rival.AccountId;
        item.RivalRankDelta = rankDelta;
        item.RivalSource = rival.Source == RivalSourceKind.Song ? "song" : "leaderboard";
        return item;
    }

    private IEnumerable<SuggestionCategory> SongRivalGap(string rivalId)
    {
        if (_rivalData == null || !_rivalData.ByRival.TryGetValue(rivalId, out var matches) || matches.Count == 0) yield break;
        var rival = matches[0].Rival;
        var pool = matches.Where(m => m.RankDelta < 0)
            .OrderBy(m => Math.Abs(m.RankDelta))
            .Select(m => { var s = _service.Songs.FirstOrDefault(x => x.track?.su == m.SongId); return (song: s, m); })
            .Where(x => x.song != null)
            .Select(x => (song: x.song, tracker: GetTracker(TryGetBoard(x.song.track.su), x.m.Instrument)))
            .ToList();
        if (pool.Count == 0) yield break;
        var key = $"song_rival_gap_{rivalId}";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = $"Close the Gap vs {rival.DisplayName}",
            Description = $"Songs where {rival.DisplayName} barely leads you. One good run could overtake them.",
            Songs = final.Select(p =>
            {
                var m = matches.FirstOrDefault(x => x.SongId == p.Item1.track.su);
                return MapRivalSong(p.Item1, p.Item2, rival, m?.RankDelta ?? 0);
            }).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalProtect(string rivalId)
    {
        if (_rivalData == null || !_rivalData.ByRival.TryGetValue(rivalId, out var matches) || matches.Count == 0) yield break;
        var rival = matches[0].Rival;
        var pool = matches.Where(m => m.RankDelta > 0)
            .OrderBy(m => m.RankDelta)
            .Select(m => { var s = _service.Songs.FirstOrDefault(x => x.track?.su == m.SongId); return (song: s, m); })
            .Where(x => x.song != null)
            .Select(x => (song: x.song, tracker: GetTracker(TryGetBoard(x.song.track.su), x.m.Instrument)))
            .ToList();
        if (pool.Count == 0) yield break;
        var key = $"song_rival_protect_{rivalId}";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = $"Protect Your Lead vs {rival.DisplayName}",
            Description = $"You're barely ahead of {rival.DisplayName} on these. Don't let them pass you.",
            Songs = final.Select(p =>
            {
                var m = matches.FirstOrDefault(x => x.SongId == p.Item1.track.su);
                return MapRivalSong(p.Item1, p.Item2, rival, m?.RankDelta ?? 0);
            }).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalBattleground()
    {
        if (_rivalData == null) yield break;
        var rivalCountBySong = new Dictionary<string, int>();
        foreach (var kv in _rivalData.ByRival)
            foreach (var m in kv.Value)
                if (Math.Abs(m.RankDelta) <= 10)
                {
                    var k = $"{m.SongId}:{m.Instrument}";
                    rivalCountBySong[k] = rivalCountBySong.GetValueOrDefault(k) + 1;
                }
        var pool = rivalCountBySong.Where(kv => kv.Value >= 2)
            .Select(kv => { var parts = kv.Key.Split(':'); var s = _service.Songs.FirstOrDefault(x => x.track?.su == parts[0]); return (song: s, instrument: parts.Length > 1 ? parts[1] : null); })
            .Where(x => x.song != null)
            .Select(x => (song: x.song, tracker: GetTracker(TryGetBoard(x.song.track.su), x.instrument)))
            .ToList();
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var key = "song_rival_battleground";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = "Battleground Songs",
            Description = "Multiple rivals are clustered around your rank on these songs.",
            Songs = final.Select(p => MapUniqueSong(p)).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalSpotlight(string rivalId)
    {
        if (_rivalData == null || !_rivalData.ByRival.TryGetValue(rivalId, out var matches) || matches.Count < 3) yield break;
        var rival = matches[0].Rival;
        var behind = matches.Where(m => m.RankDelta < 0).OrderBy(m => Math.Abs(m.RankDelta)).ToList();
        var ahead = matches.Where(m => m.RankDelta > 0).OrderBy(m => m.RankDelta).ToList();
        var picks = new List<RivalSongMatch>();
        if (behind.Count > 0) picks.Add(behind[0]);
        if (behind.Count > 1) picks.Add(behind[1]);
        if (ahead.Count > 0) picks.Add(ahead[0]);
        if (ahead.Count > 1) picks.Add(ahead[1]);
        var closest = matches.OrderBy(m => Math.Abs(m.RankDelta)).FirstOrDefault(m => !picks.Any(p => p.SongId == m.SongId));
        if (closest != null) picks.Add(closest);

        var pool = picks
            .Select(m => { var s = _service.Songs.FirstOrDefault(x => x.track?.su == m.SongId); return (song: s, m); })
            .Where(x => x.song != null)
            .ToList();
        if (pool.Count < 3) yield break;
        var key = $"song_rival_spotlight_{rivalId}";
        if (_emitted.Contains(key)) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = $"Rival Spotlight: {rival.DisplayName}",
            Description = $"A curated mix of your rivalry with {rival.DisplayName}.",
            Songs = pool.Select(p => MapRivalSong(p.song, GetTracker(TryGetBoard(p.song.track.su), p.m.Instrument), rival, p.m.RankDelta)).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalSlipping(string rivalId)
    {
        if (_rivalData == null || !_rivalData.ByRival.TryGetValue(rivalId, out var matches)) yield break;
        var rival = matches[0].Rival;
        var pool = matches.Where(m => m.RankDelta < -20)
            .Select(m => { var s = _service.Songs.FirstOrDefault(x => x.track?.su == m.SongId); return (song: s, m); })
            .Where(x => x.song != null)
            .Select(x => (song: x.song, tracker: GetTracker(TryGetBoard(x.song.track.su), x.m.Instrument)))
            .ToList();
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var key = $"song_rival_slipping_{rivalId}";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = $"{rival.DisplayName} is Pulling Ahead",
            Description = $"{rival.DisplayName} has a big lead on these songs.",
            Songs = final.Select(p =>
            {
                var m = matches.FirstOrDefault(x => x.SongId == p.Item1.track.su);
                return MapRivalSong(p.Item1, p.Item2, rival, m?.RankDelta ?? 0);
            }).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalDominate(string rivalId)
    {
        if (_rivalData == null || !_rivalData.ByRival.TryGetValue(rivalId, out var matches)) yield break;
        var rival = matches[0].Rival;
        var pool = matches.Where(m => m.RankDelta > 30)
            .Select(m => { var s = _service.Songs.FirstOrDefault(x => x.track?.su == m.SongId); return (song: s, m); })
            .Where(x => x.song != null)
            .Select(x => (song: x.song, tracker: GetTracker(TryGetBoard(x.song.track.su), x.m.Instrument)))
            .ToList();
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var key = $"song_rival_dominate_{rivalId}";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = $"Dominate {rival.DisplayName}",
            Description = $"You're crushing {rival.DisplayName} on these. Keep it up.",
            Songs = final.Select(p =>
            {
                var m = matches.FirstOrDefault(x => x.SongId == p.Item1.track.su);
                return MapRivalSong(p.Item1, p.Item2, rival, m?.RankDelta ?? 0);
            }).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalNearFc()
    {
        if (_rivalData == null) yield break;
        var pool = _service.Songs
            .Select(s => (song: s, board: TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (tr, ins) => tr != null && tr.numStars >= 5 && tr.percentHit >= 920000 && !tr.isFullCombo))
            .Where(p => _rivalData.ClosestRivalBySong.ContainsKey($"{p.song.track.su}:{InstrumentFromTracker(p)}"))
            .ToList();
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var key = "song_rival_near_fc";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = "FC These to Beat a Rival!",
            Description = "Almost FC songs where a rival also competes.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalStale()
    {
        if (_rivalData == null) yield break;
        var pool = new List<(Song song, ScoreTracker tracker)>();
        foreach (var s in _service.Songs)
        {
            var board = TryGetBoard(s.track.su);
            if (board == null) continue;
            foreach (var ins in Instruments)
            {
                var tr = GetTracker(board, ins);
                if (tr == null || tr.seasonAchieved == 0) continue;
                var key = $"{s.track.su}:{ins}";
                if (_rivalData.ClosestRivalBySong.TryGetValue(key, out var match) && match.RankDelta < 0)
                    pool.Add((s, tr));
            }
        }
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var catKey = "song_rival_stale";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(catKey, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(catKey, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = catKey,
            Title = "Stale Songs Your Rivals Are Beating You On",
            Description = "Songs you haven't touched in a while where rivals have pulled ahead.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalStarGains()
    {
        if (_rivalData == null) yield break;
        var pool = _service.Songs
            .Select(s => (song: s, board: TryGetBoard(s.track.su)))
            .SelectMany(t => EachTracker(t.song, t.board, (tr, ins) => tr != null && tr.numStars >= 3 && tr.numStars <= 5))
            .Where(p => {
                var k = $"{p.song.track.su}:{InstrumentFromTracker(p)}";
                return _rivalData.ClosestRivalBySong.TryGetValue(k, out var m) && m.RankDelta < 0;
            })
            .ToList();
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var key = "song_rival_star_gains";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = "Gain Stars & Beat a Rival",
            Description = "Improving your star count on these would also overtake a rival.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }

    private IEnumerable<SuggestionCategory> SongRivalPctPush()
    {
        if (_rivalData == null) yield break;
        var pool = new List<(Song song, ScoreTracker tracker)>();
        foreach (var s in _service.Songs)
        {
            var board = TryGetBoard(s.track.su);
            if (board == null) continue;
            foreach (var ins in Instruments)
            {
                var tr = GetTracker(board, ins);
                if (tr == null || tr.rawPercentile <= 0) continue;
                var k = $"{s.track.su}:{ins}";
                if (_rivalData.ClosestRivalBySong.TryGetValue(k, out var match) && match.RankDelta < 0)
                    pool.Add((s, tr));
            }
        }
        if (pool.Count == 0) yield break;
        Shuffle(pool);
        var key = "song_rival_pct_push";
        var freshCount = GetFreshCount(pool);
        if (!ShouldEmit(key, freshCount)) yield break;
        var take = GetDisplayCount();
        var final = SelectNewFirst(key, pool, take);
        if (final.Count == 0) yield break;
        yield return new SuggestionCategory
        {
            Key = key,
            Title = "Climb Past a Rival",
            Description = "A percentile push on these would also move you past a rival.",
            Songs = final.Select(MapUniqueSong).ToList()
        };
    }

    /// <summary>Helper to extract instrument string from a tracker pair.</summary>
    private static string InstrumentFromTracker((Song song, ScoreTracker tracker) pair) => pair.tracker?.ToString() ?? "";
    #endregion
}
}