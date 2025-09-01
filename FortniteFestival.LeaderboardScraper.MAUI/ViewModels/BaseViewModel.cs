using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            if (prop != null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
    }

    protected void Raise([CallerMemberName] string? prop = null)
    {
        if (prop != null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
