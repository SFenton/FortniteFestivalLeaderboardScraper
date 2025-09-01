using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Maui.ApplicationModel; // MainThread
using FortniteFestival.Core.Models; // adjust if needed
using FortniteFestival.Core; // for ScoreTracker
using FortniteFestival.Core.Services; // for IFestivalService
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class SongInfoInstrumentRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    private int _starsCount;
    public int StarsCount { get => _starsCount; set { if (_starsCount != value) { _starsCount = value; OnPropertyChanged(); } } }
    private bool _hasScore;
    public bool HasScore { get => _hasScore; set { if (_hasScore != value) { _hasScore = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowNA)); } } }
    private bool _isFullCombo;
    public bool IsFullCombo { get => _isFullCombo; set { if (_isFullCombo != value) { _isFullCombo = value; OnPropertyChanged(); } } }
    public ObservableCollection<StarVisual> Stars { get; } = new();
    private string _scoreDisplay = string.Empty;
    public string ScoreDisplay { get => _scoreDisplay; set { if (_scoreDisplay != value) { _scoreDisplay = value; OnPropertyChanged(); } } }
    private string _percentDisplay = string.Empty;
    public string PercentDisplay { get => _percentDisplay; set { if (_percentDisplay != value) { _percentDisplay = value; OnPropertyChanged(); } } }
    private string _seasonDisplay = string.Empty;
    public string SeasonDisplay { get => _seasonDisplay; set { if (_seasonDisplay != value) { _seasonDisplay = value; OnPropertyChanged(); } } }
    public bool ShowNA => !HasScore;
    private bool _showScore = true; public bool ShowScore { get => _showScore; set { if (_showScore != value) { _showScore = value; OnPropertyChanged(); } } }
    private bool _showPercent = true; public bool ShowPercent { get => _showPercent; set { if (_showPercent != value) { _showPercent = value; OnPropertyChanged(); } } }
    private bool _showSeason = true; public bool ShowSeason { get => _showSeason; set { if (_showSeason != value) { _showSeason = value; OnPropertyChanged(); } } }
    private bool _useCompactLayout; public bool UseCompactLayout { get => _useCompactLayout; set { if (_useCompactLayout != value) { _useCompactLayout = value; OnPropertyChanged(); } } }

    // Difficulty (raw 0-6 mapped to display 1-7). If raw < 0 treat as 0.
    private int _rawDifficulty;
    public int RawDifficulty { get => _rawDifficulty; set { if (_rawDifficulty != value) { _rawDifficulty = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayDifficulty)); } } }
    public int DisplayDifficulty => (_rawDifficulty < 0 ? 0 : _rawDifficulty) + 1; // 1-7
    public ObservableCollection<DifficultyBarVisual> DifficultyBars { get; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name ?? string.Empty));
}

public class StarVisual
{
    public string Image { get; set; } = string.Empty; // star-white or star-gold
    public string OutlineColor { get; set; } = "Transparent"; // gold, red, transparent
    public string BackgroundColor { get; set; } = "Transparent"; // for missing score red fill
}

public class DifficultyBarVisual
{
    public bool IsFilled { get; set; }
    public string FillColor => IsFilled ? "White" : "#666666";
}

public class SongInfoViewModel : BaseViewModel // if a BaseViewModel exists; otherwise implement INotifyPropertyChanged
{
    public string Title { get; }
    public string Artist { get; }
    // Release year pulled from underlying Track.ReleaseYear (ry). If 0/invalid, left blank.
    public string YearDisplay { get; } = string.Empty;
    public string ArtistYearDisplay { get; }
    public string AlbumArtPath { get; }

    public ObservableCollection<SongInfoInstrumentRow> InstrumentRows { get; } = new();

    private bool _showSeason = true;
    public bool ShowSeason { get => _showSeason; set => Set(ref _showSeason, value); }
    private bool _showPercent = true;
    public bool ShowPercent { get => _showPercent; set => Set(ref _showPercent, value); }
    private bool _showScore = true;
    public bool ShowScore { get => _showScore; set => Set(ref _showScore, value); }

    private bool _useCompactLayout; // triggers multi-line row layout in XAML
    public bool UseCompactLayout { get => _useCompactLayout; set => Set(ref _useCompactLayout, value); }

    private readonly IFestivalService _service;
    private readonly SongDisplayRow _rowRef;

    public SongInfoViewModel(SongDisplayRow row, IList<string> instrumentOrder, IFestivalService service)
    {
    Title = row.Title;
    Artist = row.Artist;
    var yr = row.Song.track.ReleaseYear;
    if (yr > 0 && yr <= 3000) YearDisplay = yr.ToString();
    ArtistYearDisplay = string.IsNullOrEmpty(YearDisplay) ? Artist : $"{Artist} · {YearDisplay}";
        AlbumArtPath = row.AlbumArtPath;
        _service = service;
        _rowRef = row;

    foreach (var key in instrumentOrder)
        {
            var status = row.InstrumentStatuses.FirstOrDefault(s => s.InstrumentKey == key);
            if (status == null) continue;
            int? score = null;
            int percentRaw = 0;
            int starsCount = 0;
            bool hasScore = false;
            bool isFC = status.IsFullCombo;
            int difficultyRaw = 0;
            if (row.Song.track?.su != null && service.ScoresIndex.TryGetValue(row.Song.track.su, out var ld))
            {
                var tr = key switch
                {
                    "guitar" => ld.guitar,
                    "drums" => ld.drums,
                    "vocals" => ld.vocals,
                    "bass" => ld.bass,
                    "pro_guitar" => ld.pro_guitar,
                    "pro_bass" => ld.pro_bass,
                    _ => null
                };
                if (tr is { initialized: true })
                {
                    score = tr.maxScore;
                    starsCount = tr.numStars;
                    percentRaw = tr.percentHit;
                    hasScore = true;
                    if (tr.isFullCombo) isFC = true;
                    difficultyRaw = tr.difficulty; // expected 0-6 (may be -1 for some not shown instruments)
                }
            }
            // Fallback if tracker didn't provide difficulty yet: try from song track intensities
            if (difficultyRaw == 0 && row.Song.track?.@in != null)
            {
                difficultyRaw = key switch
                {
                    "guitar" => row.Song.track.PlasticGuitarDifficulty,
                    "bass" => row.Song.track.PlasticBassDifficulty,
                    "drums" => row.Song.track.PlasticDrumsDifficulty,
                    "vocals" => row.Song.track.@in.vl,
                    "pro_guitar" => row.Song.track.@in.gr,
                    "pro_bass" => row.Song.track.@in.ba,
                    _ => difficultyRaw
                };
            }
            var instRow = new SongInfoInstrumentRow
            {
                Key = key,
                Name = KeyToDisplayName(key),
                Icon = status.Icon,
                StarsCount = starsCount,
                HasScore = hasScore,
                IsFullCombo = isFC,
                ScoreDisplay = hasScore && score.HasValue ? score.Value.ToString("N0") : "0",
                PercentDisplay = hasScore ? (isFC ? "100%" : FormatPercent(percentRaw)) : "0%",
                SeasonDisplay = hasScore ? row.SeasonDisplay : "N/A",
                ShowScore = ShowScore,
                ShowPercent = ShowPercent,
                ShowSeason = ShowSeason,
                UseCompactLayout = UseCompactLayout,
                RawDifficulty = difficultyRaw
            };
            // Build difficulty bars (always render 7 bars). Raw difficulty 0-6 => display bars 1-7
            int displayDiff = instRow.DisplayDifficulty; // 1-7
            for (int i = 1; i <= 7; i++)
            {
                instRow.DifficultyBars.Add(new DifficultyBarVisual { IsFilled = i <= displayDiff });
            }

            // Build star visuals per rules; if no score we show N/A text instead of a placeholder star
            if (hasScore)
            {
                int displayCount;
                bool allGold;
                if (starsCount >= 6)
                {
                    // Six stars: show 5 gold stars
                    displayCount = 5;
                    allGold = true;
                }
                else
                {
                    displayCount = Math.Max(1, starsCount); // ensure at least 1 if has score
                    allGold = false;
                }
                for (int i = 0; i < displayCount; i++)
                {
                    var star = new StarVisual
                    {
                        Image = allGold ? "star_gold.png" : "star_white.png",
                        OutlineColor = isFC ? "#FFD700" : "Transparent",
                        BackgroundColor = "Transparent" // remove glow
                    };
                    instRow.Stars.Add(star);
                }
            }

            InstrumentRows.Add(instRow);
        }

        // Subscribe to score updates for this song
        _service.ScoreUpdated += OnScoreUpdated;
        // Track visibility flag changes and propagate to rows
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShowScore) || e.PropertyName == nameof(ShowPercent) || e.PropertyName == nameof(ShowSeason))
            {
                foreach (var r in InstrumentRows)
                {
                    if (e.PropertyName == nameof(ShowScore)) r.ShowScore = ShowScore;
                    if (e.PropertyName == nameof(ShowPercent)) r.ShowPercent = ShowPercent;
                    if (e.PropertyName == nameof(ShowSeason)) r.ShowSeason = ShowSeason;
                    if (e.PropertyName == nameof(UseCompactLayout)) r.UseCompactLayout = UseCompactLayout;
                }
            }
        };
    }

    private void OnScoreUpdated(LeaderboardData ld)
    {
        if (ld.songId != _rowRef.Song.track.su) return;
        MainThread.BeginInvokeOnMainThread(() => RefreshFromService(ld));
    }

    private void RefreshFromService(LeaderboardData ld)
    {
        // Map by key for quick lookup
        foreach (var instRow in InstrumentRows)
        {
            ScoreTracker? tr = instRow.Key switch
            {
                "guitar" => ld.guitar,
                "drums" => ld.drums,
                "vocals" => ld.vocals,
                "bass" => ld.bass,
                "pro_guitar" => ld.pro_guitar,
                "pro_bass" => ld.pro_bass,
                _ => null
            };
            bool hadScore = instRow.HasScore;
            bool newHasScore = tr is { initialized: true };
            instRow.HasScore = newHasScore;
            instRow.IsFullCombo = tr?.isFullCombo ?? false;
            instRow.ScoreDisplay = newHasScore ? tr!.maxScore.ToString("N0") : "0";
            if (instRow.HasScore)
            {
                instRow.PercentDisplay = instRow.IsFullCombo ? "100%" : FormatPercent(tr!.percentHit);
                instRow.SeasonDisplay = _rowRef.SeasonDisplay;
                // Rebuild stars if count changed or was previously none
                int starsCount = tr!.numStars;
                int displayCount = starsCount >= 6 ? 5 : Math.Max(1, starsCount);
                bool allGold = starsCount >= 6;
                if (instRow.Stars.Count != displayCount || (allGold && instRow.Stars.FirstOrDefault()?.Image != "star_gold.png"))
                {
                    instRow.Stars.Clear();
                    for (int i = 0; i < displayCount; i++)
                    {
                        instRow.Stars.Add(new StarVisual
                        {
                            Image = allGold ? "star_gold.png" : "star_white.png",
                            OutlineColor = instRow.IsFullCombo ? "#FFD700" : "Transparent",
                            BackgroundColor = "Transparent"
                        });
                    }
                }
                else
                {
                    // Update outline colors if FC status changed
                    foreach (var st in instRow.Stars)
                        st.OutlineColor = instRow.IsFullCombo ? "#FFD700" : "Transparent";
                }
            }
            else if (hadScore)
            {
                // Lost score? Clear visuals
                instRow.PercentDisplay = "0%";
                instRow.SeasonDisplay = "N/A";
                instRow.Stars.Clear();
            }
            // Difficulty doesn't change dynamically right now; if future tracker supplies updates add refresh here.
        }
        // Notify collection refreshed (raise property changed for each row properties bound via manual refresh)
        Raise(nameof(InstrumentRows));
    }

    ~SongInfoViewModel()
    {
        try { _service.ScoreUpdated -= OnScoreUpdated; } catch { }
    }

    // Called by page when size changes to adapt column visibility.
    public void AdaptForWidth(double width)
    {
    // Revised breakpoints:
    // Engage compact (stacked) layout earlier so narrow desktop / mobile widths show all metrics clearly.
    // Compact keeps all metrics visible; wide layout progressively hides them as space shrinks.
    bool compact = width < 900; // previously 700 (too low; rarely triggered before metrics hid)
    bool showScore = compact || width >= 780;   // always show in compact; otherwise require wider width
    bool showPercent = compact || width >= 900; // percent hidden only in narrow wide layout just before compact kicks in
    bool showSeason = compact || width >= 980;  // season hidden first in wide mode
        if (ShowSeason != showSeason) ShowSeason = showSeason;
        if (ShowPercent != showPercent) ShowPercent = showPercent;
        if (ShowScore != showScore) ShowScore = showScore;
    if (UseCompactLayout != compact) UseCompactLayout = compact;
    // propagate compact to rows
    foreach (var r in InstrumentRows) r.UseCompactLayout = UseCompactLayout;
    }

    private static string FormatPercent(int raw)
    {
        Console.WriteLine("Raw value: " + raw);
        if (raw <= 0) return "0%";
    // Observed raw percentHit appears to be scaled by 100 (e.g., 9975 => 99.75%).
    // Some trackers may scale by 100 or 10000; heuristically detect large values.
    double value = raw >= 10000 ? raw / 100.0 : raw / 100.0; // fallback single scaling
    // Normalize to max 100
    if (value > 100) value = 100;
        if (value % 1 == 0) return ((int)value).ToString() + "%";
        if (value * 10 % 1 == 0) return value.ToString("0.0") + "%";
        return value.ToString("0.00") + "%";
    }

    private static string KeyToDisplayName(string key) => key switch
    {
        "guitar" => "Lead",
        "drums" => "Drums",
        "vocals" => "Vocals",
        "bass" => "Bass",
        "pro_guitar" => "Pro Guitar",
        "pro_bass" => "Pro Bass",
        _ => key
    };
}
