using System.Collections.ObjectModel;
using System.Windows.Input;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class SongsViewModel : BaseViewModel
{
    private readonly IFestivalService _service;
    private readonly AppState _state;
    public ObservableCollection<Song> Songs => _state.Songs;
    private string _filter;
    public string Filter
    {
        get => _filter;
        set
        {
            Set(ref _filter, value);
            ApplyFilter();
        }
    }
    public ObservableCollection<Song> VisibleSongs { get; } = new ObservableCollection<Song>();
    public ICommand SelectAllCommand { get; }
    public ICommand ClearAllCommand { get; }

    public SongsViewModel(IFestivalService service, AppState state)
    {
        _service = service;
        _state = state;
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
    }

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
        var target = q.ToList(); // diff
        // remove items not in target
        for (int i = VisibleSongs.Count - 1; i >= 0; i--)
        {
            var item = VisibleSongs[i];
            if (!target.Contains(item))
                VisibleSongs.RemoveAt(i);
        }
        // ensure order & insertion
        for (int i = 0; i < target.Count; i++)
        {
            var item = target[i];
            if (i < VisibleSongs.Count)
            {
                if (!ReferenceEquals(VisibleSongs[i], item))
                {
                    if (VisibleSongs.Contains(item))
                        VisibleSongs.Remove(item);
                    VisibleSongs.Insert(i, item);
                }
            }
            else
                VisibleSongs.Add(item);
        }
    }

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
}
