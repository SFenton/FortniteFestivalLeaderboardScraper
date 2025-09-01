using System.Collections.ObjectModel;
using System.Windows.Input;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class ScoresViewModel : BaseViewModel
{
    private readonly IFestivalService _service;
    private readonly AppState _state;
    public ObservableCollection<LeaderboardData> Scores => _state.Scores;
    private string _filter = string.Empty;
    public string Filter
    {
        get => _filter;
        set
        {
            Set(ref _filter, value);
            RebuildRows();
        }
    }

    private string _instrument = "Lead"; // Lead, Drums, Vocals, Bass, ProLead, ProBass
    public string Instrument
    {
        get => _instrument;
        set
        {
            if (_instrument != value)
            {
                Set(ref _instrument, value);
                RebuildRows();
            }
        }
    }

    // Sorting state
    private string _sortColumn = "Title";
    private bool _sortDesc;
    public ICommand SortCommand => _sortCommand ??= new Command<string>(DoSort);
    private ICommand? _sortCommand;

    private void DoSort(string col)
    {
        if (string.IsNullOrEmpty(col))
            return;
        if (_sortColumn == col)
            _sortDesc = !_sortDesc;
        else
        {
            _sortColumn = col;
            _sortDesc = false;
        }
        RebuildRows();
    }

    public ObservableCollection<ScoreRowModel> VisibleRows { get; } = new();

    public ScoresViewModel(IFestivalService service, AppState state)
    {
        _service = service;
        _state = state;
    }

    public void RefreshFromService()
    {
        Scores.Clear();
        foreach (var ld in _service.ScoresIndex.Values)
            Scores.Add(ld);
        RebuildRows();
    }

    private void RebuildRows()
    {
        // Base set
        IEnumerable<LeaderboardData> baseSet = Scores;
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            var low = Filter.ToLowerInvariant();
            baseSet = baseSet.Where(x =>
                (x.title ?? "").ToLowerInvariant().Contains(low)
                || (x.artist ?? "").ToLowerInvariant().Contains(low)
            );
        }
        // Sorting (use underlying tracker values)
        baseSet = ApplySort(baseSet);

        VisibleRows.Clear();
        foreach (var ld in baseSet)
        {
            var t = GetTracker(ld);
            if (t == null || !t.initialized)
                continue;
            var pct = (t.percentHit / 10000.0).ToString("0.00") + "%";
            var starsCount = t.numStars;
            var starText = starsCount > 0 ? new string('?', System.Math.Min(starsCount, 6)) : "";
            var fullCombo = t.isFullCombo ? "?" : "?";
            var season = t.seasonAchieved > 0 ? t.seasonAchieved.ToString() : "All-Time";
            VisibleRows.Add(new ScoreRowModel
            {
                Title = ld.title ?? string.Empty,
                Artist = ld.artist ?? string.Empty,
                Score = t.maxScore,
                Percent = pct,
                StarText = starText,
                MaxStars = starsCount >= 6,
                FullComboSymbol = fullCombo,
                IsFullCombo = t.isFullCombo,
                Season = season,
                SongId = ld.songId ?? string.Empty,
            });
        }
    }

    private IEnumerable<LeaderboardData> ApplySort(IEnumerable<LeaderboardData> list)
    {
        Func<LeaderboardData, ScoreTracker> sel = GetTracker;
        switch (_sortColumn)
        {
            case "Artist":
                list = list.OrderBy(x => x.artist).ThenBy(x => x.title);
                break;
            case "Score":
                list = list.OrderBy(x => sel(x).maxScore);
                break;
            case "Percent":
                list = list.OrderBy(x => sel(x).percentHit);
                break;
            case "Stars":
                list = list.OrderBy(x => sel(x).numStars);
                break;
            case "FC":
                list = list.OrderBy(x => sel(x).isFullCombo);
                break;
            case "Season":
                list = list.OrderBy(x => sel(x).seasonAchieved);
                break;
            default:
                list = list.OrderBy(x => x.title);
                break; // Title
        }
        if (_sortDesc)
            list = list.Reverse();
        return list;
    }

    private ScoreTracker GetTracker(LeaderboardData ld) =>
        Instrument switch
        {
            "Drums" => ld.drums ?? new ScoreTracker(),
            "Vocals" => ld.vocals ?? new ScoreTracker(),
            "Bass" => ld.bass ?? new ScoreTracker(),
            "ProLead" => ld.pro_guitar ?? new ScoreTracker(),
            "ProBass" => ld.pro_bass ?? new ScoreTracker(),
            _ => ld.guitar ?? new ScoreTracker(),
        };
}

internal static class ObservableCollectionExtensions
{
    public static int FindIndex<T>(this ObservableCollection<T> col, System.Predicate<T> pred)
    {
        for (int i = 0; i < col.Count; i++)
            if (pred(col[i]))
                return i;
        return -1;
    }
}
