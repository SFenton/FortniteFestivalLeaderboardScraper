using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class SongsViewModel : BaseViewModel
{
    private readonly IFestivalService _service;
    private readonly AppState _state;
    public ObservableCollection<Song> Songs => _state.Songs;

    // UI projection rows
    public ObservableCollection<SongDisplayRow> VisibleRows { get; } = new();
    // Sorting
    public bool IsSortByTitle { get => _isSortByTitle; set { Set(ref _isSortByTitle, value); if (value) SetSortMode("title"); } }
    public bool IsSortByArtist { get => _isSortByArtist; set { Set(ref _isSortByArtist, value); if (value) SetSortMode("artist"); } }
    public bool IsSortByHasFC { get => _isSortByHasFC; set { Set(ref _isSortByHasFC, value); if (value) SetSortMode("hasfc"); } }
    private bool _isSortByTitle = true; // default
    private bool _isSortByArtist;
    private bool _isSortByHasFC;
    private string _sortMode = "title"; // title|artist|hasfc
    private bool _sortAscending = true;
    public bool SortAscending { get => _sortAscending; set { Set(ref _sortAscending, value); } }
    public string SortDirectionCaret => _sortAscending ? "▲" : "▼";
    public ObservableCollection<InstrumentOrderItem> PrimaryInstrumentOrder { get; } = new(new [] {
        new InstrumentOrderItem("guitar","Lead"),
        new InstrumentOrderItem("drums","Drums"),
        new InstrumentOrderItem("vocals","Vocals"),
        new InstrumentOrderItem("bass","Bass"),
        new InstrumentOrderItem("pro_guitar","Pro Guitar"),
        new InstrumentOrderItem("pro_bass","Pro Bass")
    });

    private string _filter = string.Empty;
    public string Filter
    {
        get => _filter;
        set
        {
            Set(ref _filter, value);
            ApplyFilter();
        }
    }

    // Advanced filter flags (persist in-memory for session)
    public bool MissingPadFCs { get => _missingPadFCs; set => Set(ref _missingPadFCs, value); }
    public bool MissingProFCs { get => _missingProFCs; set => Set(ref _missingProFCs, value); }
    public bool MissingPadScores { get => _missingPadScores; set => Set(ref _missingPadScores, value); }
    public bool MissingProScores { get => _missingProScores; set => Set(ref _missingProScores, value); }

    public bool IncludeLead { get => _includeLead; set => Set(ref _includeLead, value); }
    public bool IncludeBass { get => _includeBass; set => Set(ref _includeBass, value); }
    public bool IncludeDrums { get => _includeDrums; set => Set(ref _includeDrums, value); }
    public bool IncludeVocals { get => _includeVocals; set => Set(ref _includeVocals, value); }
    public bool IncludeProGuitar { get => _includeProGuitar; set => Set(ref _includeProGuitar, value); }
    public bool IncludeProBass { get => _includeProBass; set => Set(ref _includeProBass, value); }

    private bool _missingPadFCs;
    private bool _missingProFCs;
    private bool _missingPadScores;
    private bool _missingProScores;
    private bool _includeLead = true;
    private bool _includeBass = true;
    private bool _includeDrums = true;
    private bool _includeVocals = true;
    private bool _includeProGuitar = true;
    private bool _includeProBass = true;

    public ICommand SelectAllCommand { get; }
    public ICommand ClearAllCommand { get; }

    public SongsViewModel(IFestivalService service, AppState state)
    {
        _service = service;
        _state = state;
    Service = service; // expose for detail pages

        SelectAllCommand = new Command(() =>
        {
            foreach (var s in Songs)
            {
                s.isSelected = true;
                if (!_state.SelectedSongIds.Contains(s.track.su))
                    _state.SelectedSongIds.Add(s.track.su);
            }
            ApplyFilter();
        });
        ClearAllCommand = new Command(() =>
        {
            foreach (var s in Songs)
                s.isSelected = false;
            _state.SelectedSongIds.Clear();
            ApplyFilter();
        });

        // Update rows when scores update
        _service.ScoreUpdated += ld =>
        {
            // find row by song id
            var row = VisibleRows.FirstOrDefault(r => r.Song.track.su == ld.songId);
            row?.RefreshScore(_service);
        };
    }

    // Expose service (read-only) for other viewmodels needing raw score data.
    public IFestivalService Service { get; }

    private void ApplyFilter()
    {
        IEnumerable<Song> q = Songs;
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            var low = Filter.ToLowerInvariant();
            q = q.Where(x =>
                (x.track.tt ?? "").ToLowerInvariant().Contains(low)
                || (x.track.an ?? "").ToLowerInvariant().Contains(low)
            );
        }

        // Advanced missing filters
        bool anyMissing = MissingPadFCs || MissingProFCs || MissingPadScores || MissingProScores;
        if (anyMissing)
        {
            q = q.Where(SongMatchesAdvancedMissing);
        }
        // Apply sorting
        q = _sortMode switch
        {
            "artist" => q.OrderBy(s => s.track.an).ThenBy(s => s.track.tt),
            "hasfc" => q.OrderByDescending(SongHasAllFCsPriority).ThenByDescending(SongHasSequentialTopFCsScore).ThenBy(s => s.track.tt),
            _ => q.OrderBy(s => s.track.tt).ThenBy(s => s.track.an)
        };
        if (!_sortAscending)
            q = q.Reverse();
        var target = q.ToList();

        // Remove rows not needed
        for (int i = VisibleRows.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(VisibleRows[i].Song))
                VisibleRows.RemoveAt(i);
        }

        // Insert / ensure order
        for (int i = 0; i < target.Count; i++)
        {
            var song = target[i];
            if (i < VisibleRows.Count)
            {
                if (!ReferenceEquals(VisibleRows[i].Song, song))
                {
                    // Existing row someplace else?
                    var existing = VisibleRows.FirstOrDefault(r => ReferenceEquals(r.Song, song));
                    if (existing != null)
                        VisibleRows.Remove(existing);
                    var newRow = existing ?? new SongDisplayRow(song, _service);
                    VisibleRows.Insert(i, newRow);
                }
                else
                {
                    // refresh score just in case
                    VisibleRows[i].RefreshScore(_service);
                }
            }
            else
            {
                VisibleRows.Add(new SongDisplayRow(song, _service));
            }
        }
    }

    private bool SongMatchesAdvancedMissing(Song song)
    {
        if (!_service.ScoresIndex.TryGetValue(song.track.su, out var entry))
        {
            // If seeking missing scores, a completely absent entry counts as missing for any selected instrument set where score missing is selected
            if (MissingPadScores && (IncludeLead || IncludeBass || IncludeDrums || IncludeVocals)) return true;
            if (MissingProScores && (IncludeProGuitar || IncludeProBass)) return true;
            return false;
        }

        bool match = false;

        // Helper local functions
        bool MissingScore(ScoreTracker? t) => t == null || !t.initialized;
        bool MissingFC(ScoreTracker? t) => !(t?.initialized ?? false) || !(t?.isFullCombo ?? false); // treat no score as missing FC too

        // Pad instruments
        if (IncludeLead)
        {
            if (MissingPadScores && MissingScore(entry.guitar)) match = true;
            if (MissingPadFCs && MissingFC(entry.guitar)) match = true;
        }
        if (IncludeDrums)
        {
            if (MissingPadScores && MissingScore(entry.drums)) match = true;
            if (MissingPadFCs && MissingFC(entry.drums)) match = true;
        }
        if (IncludeVocals)
        {
            if (MissingPadScores && MissingScore(entry.vocals)) match = true;
            if (MissingPadFCs && MissingFC(entry.vocals)) match = true;
        }
        if (IncludeBass)
        {
            if (MissingPadScores && MissingScore(entry.bass)) match = true;
            if (MissingPadFCs && MissingFC(entry.bass)) match = true;
        }

        // Pro instruments
        if (IncludeProGuitar)
        {
            if (MissingProScores && MissingScore(entry.pro_guitar)) match = true;
            if (MissingProFCs && MissingFC(entry.pro_guitar)) match = true;
        }
        if (IncludeProBass)
        {
            if (MissingProScores && MissingScore(entry.pro_bass)) match = true;
            if (MissingProFCs && MissingFC(entry.pro_bass)) match = true;
        }

        return match;
    }

    public void ApplyAdvancedFilters() => ApplyFilter();

    public void ToggleSelection(Song s)
    {
        if (s == null)
            return;
        s.isSelected = !s.isSelected;
        if (s.isSelected)
        {
            if (!_state.SelectedSongIds.Contains(s.track.su))
                _state.SelectedSongIds.Add(s.track.su);
        }
        else
            _state.SelectedSongIds.Remove(s.track.su);
    }

    public void Refresh()
    {
        ApplyFilter();
    }

    public void ResetFiltersToDefaults()
    {
        MissingPadFCs = false;
        MissingProFCs = false;
        MissingPadScores = false;
        MissingProScores = false;
        IncludeLead = true;
        IncludeBass = true;
        IncludeDrums = true;
        IncludeVocals = true;
        IncludeProGuitar = true;
        IncludeProBass = true;
    }

    private void SetSortMode(string mode)
    {
        _sortMode = mode;
        // ensure only one flag true
        _isSortByTitle = mode == "title";
        _isSortByArtist = mode == "artist";
        _isSortByHasFC = mode == "hasfc";
    Raise(nameof(IsSortByTitle));
    Raise(nameof(IsSortByArtist));
    Raise(nameof(IsSortByHasFC));
    // Defer actual re-sort until user clicks Apply in Sort modal
    }

    public void ToggleSortDirection()
    {
        SortAscending = !SortAscending;
        Raise(nameof(SortDirectionCaret));
    }

    private int SongHasAllFCsPriority(Song s)
    {
        if (!_service.ScoresIndex.TryGetValue(s.track.su, out var entry)) return 0;
        // all instruments FC?
        var orderKeys = PrimaryInstrumentOrder.Select(i => i.Key).ToList();
        foreach (var key in orderKeys)
        {
            if (!InstrumentHasFC(entry, key)) return 0;
        }
        return 1;
    }

    private int SongHasSequentialTopFCsScore(Song s)
    {
        if (!_service.ScoresIndex.TryGetValue(s.track.su, out var entry)) return 0;
        var orderKeys = PrimaryInstrumentOrder.Select(i => i.Key).ToList();
        int count = 0;
        foreach (var key in orderKeys)
        {
            if (InstrumentHasFC(entry, key)) count++; else break;
        }
        return count; // 0..6
    }

    private static bool InstrumentHasFC(LeaderboardData ld, string key)
    {
        ScoreTracker? tr = key switch
        {
            "guitar" => ld.guitar,
            "drums" => ld.drums,
            "vocals" => ld.vocals,
            "bass" => ld.bass,
            "pro_guitar" => ld.pro_guitar,
            "pro_bass" => ld.pro_bass,
            _ => null
        };
        return tr != null && tr.initialized && tr.isFullCombo;
    }
}

public class InstrumentOrderItem : INotifyPropertyChanged
{
    public string Key { get; }
    public string DisplayName { get; }
    private bool _isDragging;
    public bool IsDragging
    {
        get => _isDragging;
        set { if (_isDragging != value) { _isDragging = value; OnPropertyChanged(); } }
    }

    public InstrumentOrderItem(string key, string displayName) { Key = key; DisplayName = displayName; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n ?? string.Empty));
}

public class SongDisplayRow : INotifyPropertyChanged
{
    public Song Song { get; }
    private int _score;
    private string _stars = string.Empty;
    private bool _isFullCombo;
    private string _season = string.Empty;
    private int _percentHit; // raw value from tracker (e.g., 999000 for 99.90%)
    public ObservableCollection<InstrumentStatus> InstrumentStatuses { get; } = new(
        new[]
        {
            new InstrumentStatus("guitar"),
            new InstrumentStatus("drums"),
            new InstrumentStatus("vocals"),
            new InstrumentStatus("bass"),
            new InstrumentStatus("pro_guitar"),
            new InstrumentStatus("pro_bass")
        });

    public int Score { get => _score; private set { if (_score != value) { _score = value; OnPropertyChanged(); } } }
    public string Stars { get => _stars; private set { if (_stars != value) { _stars = value; OnPropertyChanged(); } } }
    public bool IsFullCombo { get => _isFullCombo; private set { if (_isFullCombo != value) { _isFullCombo = value; OnPropertyChanged(); OnPropertyChanged(nameof(FullComboSymbol)); } } }
    public string Season { get => _season; private set { if (_season != value) { _season = value; OnPropertyChanged(); OnPropertyChanged(nameof(SeasonDisplay)); } } }
    public string SeasonDisplay => int.TryParse(Season, out var n) ? (n < 0 ? "N/A" : $"S{n}") : (Season == "All-Time" ? "N/A" : Season);
    public int PercentHitRaw { get => _percentHit; private set { if (_percentHit != value) { _percentHit = value; OnPropertyChanged(); OnPropertyChanged(nameof(PercentHitDisplay)); } } }
    public string PercentHitDisplay => _percentHit <= 0 ? "--" : $"{Math.Max(0, (_percentHit / 10000))}%"; // clamp down; raw stored *100 (assuming percentHit scaled by 100?)

    public string Title => Song.track.tt;
    public string Artist => Song.track.an;
    public string AlbumArtPath => Song.imagePath;
    public bool IsSelected { get => Song.isSelected; set { if (Song.isSelected != value) { Song.isSelected = value; OnPropertyChanged(); } } }
    public string FullComboSymbol => IsFullCombo ? "FC" : string.Empty;

    public SongDisplayRow(Song song, IFestivalService service)
    {
        Song = song;
        RefreshScore(service);
    }

    public void RefreshScore(IFestivalService service)
    {
        ScoreTracker? t = null;
        if (service.ScoresIndex.TryGetValue(Song.track.su, out var ld))
            t = ld.guitar ?? ld.drums ?? ld.vocals ?? ld.bass ?? ld.pro_guitar ?? ld.pro_bass; // prefer guitar
        if (t == null || !t.initialized)
        {
            Score = 0;
            Stars = "";
            IsFullCombo = false;
            PercentHitRaw = 0;
            Season = "";
        }
        else
        {
            Score = t.maxScore;
            Stars = t.numStars > 0 ? new string('\u2605', Math.Min(t.numStars, 6)) : ""; // ★ stars
            IsFullCombo = t.isFullCombo;
            PercentHitRaw = t.percentHit;
            Season = t.seasonAchieved != 0 ? t.seasonAchieved.ToString() : "-1"; // -1 sentinel for N/A
        }

        // Update per-instrument statuses
        UpdateInstrumentStatuses(service);
    }

    private void UpdateInstrumentStatuses(IFestivalService service)
    {
        if (!service.ScoresIndex.TryGetValue(Song.track.su, out var ld))
        {
            foreach (var s in InstrumentStatuses)
            {
                s.HasScore = false;
                s.IsFullCombo = false;
            }
            return;
        }
        foreach (var s in InstrumentStatuses)
        {
            ScoreTracker? tr = s.InstrumentKey switch
            {
                "guitar" => ld.guitar,
                "drums" => ld.drums,
                "vocals" => ld.vocals,
                "bass" => ld.bass,
                "pro_guitar" => ld.pro_guitar,
                "pro_bass" => ld.pro_bass,
                _ => null
            };
            if (tr == null || !tr.initialized)
            {
                s.HasScore = false;
                s.IsFullCombo = false;
            }
            else
            {
                s.HasScore = true;
                s.IsFullCombo = tr.isFullCombo;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

public class InstrumentStatus : INotifyPropertyChanged
{
    public string InstrumentKey { get; }
    // MAUI image resolution ignores subfolders at runtime; reference by filename only.
    public string Icon => $"{InstrumentKey}.png";
    private bool _hasScore;
    private bool _isFullCombo;
    public bool HasScore { get => _hasScore; set { if (_hasScore != value) { _hasScore = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowCircle)); OnPropertyChanged(nameof(CircleFillColor)); OnPropertyChanged(nameof(CircleStrokeColor)); } } }
    public bool IsFullCombo { get => _isFullCombo; set { if (_isFullCombo != value) { _isFullCombo = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowCircle)); OnPropertyChanged(nameof(CircleFillColor)); OnPropertyChanged(nameof(CircleStrokeColor)); } } }
    // Always show a status circle: gold = FC, green = has score (not FC), red = no score
    public bool ShowCircle => true;
    public string CircleFillColor => IsFullCombo ? "#FFD700" : (HasScore ? "#2ECC71" : "#C62828");
    public string CircleStrokeColor => IsFullCombo ? "#CFA500" : (HasScore ? "#1E7F46" : "#8B0000");
    public InstrumentStatus(string key) => InstrumentKey = key;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n ?? string.Empty));
}
