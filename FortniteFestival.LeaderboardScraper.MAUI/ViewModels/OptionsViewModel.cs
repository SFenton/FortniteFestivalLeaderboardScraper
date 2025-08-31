using System.Collections.ObjectModel;
using System.Windows.Input;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class OptionsViewModel : BaseViewModel
{
    private readonly Settings _settings; private readonly ISettingsPersistence _persist;

    public class InstrumentOption : BaseViewModel
    {
        private bool _isSelected; public string Name { get; set; } public Func<bool> Getter { get; set; } public Action<bool> Setter { get; set; }
        public bool IsSelected { get => Getter(); set { Setter(value); Set(ref _isSelected,value); Raise(); } }
    }

    public ObservableCollection<InstrumentOption> InstrumentOptions { get; } = new();

    public int DegreeOfParallelism { get=>_settings.DegreeOfParallelism; set { _settings.DegreeOfParallelism = value; Raise(); } }

    public ICommand SaveCommand { get; }

    public OptionsViewModel(Settings settings, ISettingsPersistence persist)
    {
        _settings = settings; _persist = persist; if(_settings.DegreeOfParallelism<=0) _settings.DegreeOfParallelism = 16;
        BuildInstrumentOptions();
        SaveCommand = new Command(async ()=> await _persist.SaveSettingsAsync(_settings));
    }

    private void BuildInstrumentOptions()
    {
        InstrumentOptions.Clear();
        InstrumentOptions.Add(new InstrumentOption{ Name="Lead", Getter=()=> _settings.QueryLead, Setter=v=> _settings.QueryLead=v });
        InstrumentOptions.Add(new InstrumentOption{ Name="Drums", Getter=()=> _settings.QueryDrums, Setter=v=> _settings.QueryDrums=v });
        InstrumentOptions.Add(new InstrumentOption{ Name="Vocals", Getter=()=> _settings.QueryVocals, Setter=v=> _settings.QueryVocals=v });
        InstrumentOptions.Add(new InstrumentOption{ Name="Bass", Getter=()=> _settings.QueryBass, Setter=v=> _settings.QueryBass=v });
        InstrumentOptions.Add(new InstrumentOption{ Name="Pro Lead", Getter=()=> _settings.QueryProLead, Setter=v=> _settings.QueryProLead=v });
        InstrumentOptions.Add(new InstrumentOption{ Name="Pro Bass", Getter=()=> _settings.QueryProBass, Setter=v=> _settings.QueryProBass=v });
    }
}
