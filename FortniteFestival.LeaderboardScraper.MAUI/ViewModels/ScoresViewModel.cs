using System.Collections.ObjectModel;
using FortniteFestival.Core.Services;
using FortniteFestival.Core;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class ScoresViewModel : BaseViewModel
{
    private readonly IFestivalService _service; private readonly AppState _state;
    public ObservableCollection<LeaderboardData> Scores => _state.Scores;
    private string _filter; public string Filter { get=>_filter; set { Set(ref _filter,value); RebuildRows(); } }

    // Instrument selection
    private string _instrument = "Lead"; // Lead, Drums, Vocals, Bass, ProLead, ProBass
    public string Instrument { get=>_instrument; set { Set(ref _instrument,value); RebuildRows(); } }

    // Projected rows for UI binding (instrument-specific)
    public class ScoreRow
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public int Score { get; set; }
        public int PercentHit { get; set; } // scaled (10000 == 100%)
        public int Stars { get; set; }
        public bool FullCombo { get; set; }
        public int Season { get; set; }
        public string SongId { get; set; }
    }
    public ObservableCollection<ScoreRow> VisibleRows { get; } = new();

    public ScoresViewModel(IFestivalService service, AppState state){ _service = service; _state = state; }

    public void RefreshFromService(){ Scores.Clear(); foreach(var ld in _service.ScoresIndex.Values) Scores.Add(ld); RebuildRows(); }

    private void RebuildRows()
    {
        VisibleRows.Clear();
        IEnumerable<LeaderboardData> q = Scores;
        if(!string.IsNullOrWhiteSpace(Filter))
        {
            var low = Filter.ToLowerInvariant();
            q = q.Where(x=> (x.title??"").ToLowerInvariant().Contains(low) || (x.artist??"").ToLowerInvariant().Contains(low));
        }
        foreach(var ld in q)
        {
            var tracker = GetTracker(ld);
            if(tracker==null) continue;
            VisibleRows.Add(new ScoreRow
            {
                Title = ld.title,
                Artist = ld.artist,
                Score = tracker.maxScore,
                PercentHit = tracker.percentHit,
                Stars = tracker.numStars,
                FullCombo = tracker.isFullCombo,
                Season = tracker.seasonAchieved,
                SongId = ld.songId
            });
        }
        Raise(nameof(VisibleRows));
    }

    private ScoreTracker GetTracker(LeaderboardData ld) => Instrument switch
    {
        "Drums" => ld.drums ?? new ScoreTracker(),
        "Vocals" => ld.vocals ?? new ScoreTracker(),
        "Bass" => ld.bass ?? new ScoreTracker(),
        "ProLead" => ld.pro_guitar ?? new ScoreTracker(),
        "ProBass" => ld.pro_bass ?? new ScoreTracker(),
        _ => ld.guitar ?? new ScoreTracker()
    };
}
