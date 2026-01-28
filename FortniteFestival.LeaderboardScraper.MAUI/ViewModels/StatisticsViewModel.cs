using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FortniteFestival.Core.Suggestions;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

/// <summary>
/// ViewModel for statistics section. Computes aggregated stats from ScoresIndex.
/// </summary>
public class StatisticsViewModel : INotifyPropertyChanged
{
    private static readonly string[] InstrumentKeys = { "guitar", "drums", "vocals", "bass", "pro_guitar", "pro_bass" };

    private readonly IFestivalService _service;
    private bool _isLoading;
    private bool _hasData;

    public StatisticsViewModel(IFestivalService service)
    {
        _service = service;
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); } }
    }

    public bool HasData
    {
        get => _hasData;
        private set { if (_hasData != value) { _hasData = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Detailed stats per instrument.
    /// </summary>
    public ObservableCollection<InstrumentDetailedStats> InstrumentStats { get; } = new();

    /// <summary>
    /// Top 5 song categories per instrument (weighted and unweighted).
    /// </summary>
    public ObservableCollection<SuggestionCategory> TopSongCategories { get; } = new();

    /// <summary>
    /// Rebuilds all statistics from ScoresIndex. Call from UI thread.
    /// </summary>
    public void Refresh()
    {
        IsLoading = true;
        try
        {
            Clear();

            if (_service.ScoresIndex == null || _service.ScoresIndex.Count == 0)
            {
                HasData = false;
                return;
            }

            HasData = true;
            var boards = _service.ScoresIndex.Values.ToList();
            int totalSongsInLibrary = _service.Songs?.Count ?? boards.Count;

            BuildInstrumentDetailedStats(boards, totalSongsInLibrary);
            BuildTopSongCategories(boards);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatisticsViewModel] Error building statistics: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Clears all computed statistics.
    /// </summary>
    public void Clear()
    {
        InstrumentStats.Clear();
        TopSongCategories.Clear();
    }

    private void BuildInstrumentDetailedStats(List<LeaderboardData> boards, int totalSongsInLibrary)
    {
        foreach (var k in InstrumentKeys)
        {
            var stats = new InstrumentDetailedStats
            {
                InstrumentKey = k,
                InstrumentLabel = KeyToLabel(k),
                TotalSongsInLibrary = totalSongsInLibrary
            };

            var trackers = boards
                .Select(ld => GetTracker(ld, k))
                .Where(t => t != null)
                .ToList();

            var playedTrackers = trackers.Where(t => t!.percentHit > 0).ToList();

            // Basic counts
            stats.SongsPlayed = playedTrackers.Count;
            stats.FcCount = playedTrackers.Count(t => t!.isFullCombo);

            // Star distribution
            stats.GoldStarCount = playedTrackers.Count(t => t!.numStars == 6);
            stats.FiveStarCount = playedTrackers.Count(t => t!.numStars == 5);
            stats.FourStarCount = playedTrackers.Count(t => t!.numStars == 4);
            stats.ThreeOrLessStarCount = playedTrackers.Count(t => t!.numStars > 0 && t!.numStars <= 3);
            stats.AverageStars = playedTrackers.Count > 0
                ? playedTrackers.Where(t => t!.numStars > 0).Average(t => t!.numStars)
                : 0;

            // Accuracy (percentHit is stored as int * 10000, e.g., 9850.00% = 985000)
            var accuracies = playedTrackers.Select(t => t!.percentHit / 10000.0).ToList();
            stats.AverageAccuracy = accuracies.Count > 0 ? accuracies.Average() : 0;
            stats.BestAccuracy = accuracies.Count > 0 ? accuracies.Max() : 0;
            stats.PerfectScoreCount = playedTrackers.Count(t => t!.percentHit >= 1000000); // 100.0000%

            // Scores
            var scores = playedTrackers.Where(t => t!.maxScore > 0).Select(t => t!.maxScore).ToList();
            stats.TotalScore = scores.Sum(s => (long)s);
            stats.HighestScore = scores.Count > 0 ? scores.Max() : 0;
            stats.AverageScore = scores.Count > 0 ? scores.Average() : 0;

            // Leaderboard / Percentile
            var rankedTrackers = playedTrackers.Where(t => t!.rank > 0).ToList();
            stats.BestRank = rankedTrackers.Count > 0 ? rankedTrackers.Min(t => t!.rank) : 0;

            var percentileTrackers = playedTrackers.Where(t => t!.rawPercentile > 0 && t!.rawPercentile <= 1).ToList();
            stats.AveragePercentile = percentileTrackers.Count > 0
                ? percentileTrackers.Average(t => t!.rawPercentile)
                : double.NaN;

            // Weighted percentile
            var weightedComponents = percentileTrackers
                .Select(t =>
                {
                    int weight = t!.totalEntries > 0 ? t.totalEntries : (t.calculatedNumEntries > 0 ? t.calculatedNumEntries : 1);
                    return (pct: t.rawPercentile, weight);
                })
                .ToList();

            if (weightedComponents.Count > 0)
            {
                double num = weightedComponents.Sum(v => v.pct * v.weight);
                double den = weightedComponents.Sum(v => v.weight);
                stats.WeightedPercentile = den > 0 ? num / den : double.NaN;
            }
            else
            {
                stats.WeightedPercentile = double.NaN;
            }

            // Percentile distribution
            foreach (var t in percentileTrackers)
            {
                double pct = t!.rawPercentile * 100.0; // convert to percentage
                if (pct <= 1) stats.Top1PercentCount++;
                else if (pct <= 5) stats.Top5PercentCount++;
                else if (pct <= 10) stats.Top10PercentCount++;
                else if (pct <= 25) stats.Top25PercentCount++;
                else if (pct <= 50) stats.Top50PercentCount++;
                else stats.Below50PercentCount++;
            }

            InstrumentStats.Add(stats);
        }
    }

    private void BuildTopSongCategories(IEnumerable<LeaderboardData> boards)
    {
        var boardList = boards.ToList();

        foreach (var k in InstrumentKeys)
        {
            var instrumentBoards = boardList
                .Where(ld => GetTracker(ld, k) != null && GetTracker(ld, k)!.rawPercentile > 0)
                .ToList();

            if (instrumentBoards.Count == 0) continue;

            // Calculate baseline for weighting
            var weightsList = instrumentBoards
                .Select(ld =>
                {
                    var t = GetTracker(ld, k)!;
                    return t.totalEntries > 0 ? t.totalEntries : (t.calculatedNumEntries > 0 ? t.calculatedNumEntries : 1);
                })
                .OrderBy(x => x)
                .ToList();

            int baseline = weightsList[weightsList.Count / 2];
            if (baseline <= 0) baseline = 1;

            // Weighted top 5
            var weightedTopFive = instrumentBoards
                .OrderBy(ld => WeightScore(ld, k, baseline))
                .ThenBy(ld => GetTracker(ld, k)!.rawPercentile)
                .Take(5)
                .ToList();

            if (weightedTopFive.Count > 0)
            {
                var wCat = new SuggestionCategory
                {
                    Key = $"stats_top_five_weighted_{k}",
                    Title = $"Top five songs by weighted percentile for {KeyToLabel(k)}",
                    Description = $"Your top five competitive songs (weighted by participants) for {KeyToLabel(k)}."
                };
                foreach (var ld in weightedTopFive)
                    wCat.Songs.Add(new SuggestionSongItem { SongId = ld.songId, Title = ld.title, Artist = ld.artist });
                TopSongCategories.Add(wCat);
            }

            // Unweighted top 5
            var topFive = instrumentBoards
                .OrderBy(ld => GetTracker(ld, k)!.rawPercentile)
                .Take(5)
                .ToList();

            if (topFive.Count > 0)
            {
                var sc = new SuggestionCategory
                {
                    Key = $"stats_top_five_{k}",
                    Title = $"Top five songs by percentile for {KeyToLabel(k)}",
                    Description = $"Your top five competitive songs for {KeyToLabel(k)}."
                };
                foreach (var ld in topFive)
                    sc.Songs.Add(new SuggestionSongItem { SongId = ld.songId, Title = ld.title, Artist = ld.artist });
                TopSongCategories.Add(sc);
            }
        }
    }

    private double WeightScore(LeaderboardData ld, string key, int baseline)
    {
        var t = GetTracker(ld, key);
        if (t == null || t.rawPercentile <= 0) return double.MaxValue;
        int entries = t.totalEntries > 0 ? t.totalEntries : (t.calculatedNumEntries > 0 ? t.calculatedNumEntries : 1);
        return t.rawPercentile * (baseline / (double)entries);
    }

    private static ScoreTracker? GetTracker(LeaderboardData board, string instrument)
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

    private static string KeyToLabel(string key)
    {
        switch (key)
        {
            case "guitar": return "Lead";
            case "bass": return "Bass";
            case "drums": return "Drums";
            case "vocals": return "Vocals";
            case "pro_guitar": return "Pro Guitar";
            case "pro_bass": return "Pro Bass";
            default: return key;
        }
    }

    private static double Average(IEnumerable<double> vals)
    {
        var list = vals.ToList();
        return list.Count > 0 ? list.Average() : double.NaN;
    }

    private static double WeightedAverage(IEnumerable<(double p, int w)> vals)
    {
        var list = vals.ToList();
        if (list.Count == 0) return double.NaN;
        double num = list.Sum(v => v.p * v.w);
        double den = list.Sum(v => v.w);
        return den > 0 ? num / den : double.NaN;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
