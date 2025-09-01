using System.Collections.ObjectModel;
using System.Windows.Input;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class OptionsViewModel : BaseViewModel
{
    private readonly Settings _settings;
    private readonly ISettingsPersistence _persist;

    public ObservableCollection<InstrumentOptionModel> InstrumentOptions { get; } = new();

    public int DegreeOfParallelism
    {
        get => _settings.DegreeOfParallelism;
        set
        {
            _settings.DegreeOfParallelism = value;
            Raise();
        }
    }

    public ICommand SaveCommand { get; }

    public OptionsViewModel(Settings settings, ISettingsPersistence persist)
    {
        _settings = settings;
        _persist = persist;
        if (_settings.DegreeOfParallelism <= 0)
            _settings.DegreeOfParallelism = 16;
        BuildInstrumentOptions();
        SaveCommand = new Command(async () => await _persist.SaveSettingsAsync(_settings));
    }

    private void BuildInstrumentOptions()
    {
        InstrumentOptions.Clear();
        InstrumentOptions.Add(
            new InstrumentOptionModel
            {
                Name = "Lead",
                Getter = () => _settings.QueryLead,
                Setter = v => _settings.QueryLead = v,
            }
        );
        InstrumentOptions.Add(
            new InstrumentOptionModel
            {
                Name = "Drums",
                Getter = () => _settings.QueryDrums,
                Setter = v => _settings.QueryDrums = v,
            }
        );
        InstrumentOptions.Add(
            new InstrumentOptionModel
            {
                Name = "Vocals",
                Getter = () => _settings.QueryVocals,
                Setter = v => _settings.QueryVocals = v,
            }
        );
        InstrumentOptions.Add(
            new InstrumentOptionModel
            {
                Name = "Bass",
                Getter = () => _settings.QueryBass,
                Setter = v => _settings.QueryBass = v,
            }
        );
        InstrumentOptions.Add(
            new InstrumentOptionModel
            {
                Name = "Pro Lead",
                Getter = () => _settings.QueryProLead,
                Setter = v => _settings.QueryProLead = v,
            }
        );
        InstrumentOptions.Add(
            new InstrumentOptionModel
            {
                Name = "Pro Bass",
                Getter = () => _settings.QueryProBass,
                Setter = v => _settings.QueryProBass = v,
            }
        );
    }
}

public class InstrumentOptionModel : BaseViewModel
{
    private bool _isSelected;
    public string Name { get; set; } = string.Empty;
    public Func<bool> Getter { get; set; } = () => false;
    public Action<bool> Setter { get; set; } = _ => { };
    public bool IsSelected
    {
        get => Getter();
        set
        {
            Setter(value);
            Set(ref _isSelected, value);
            Raise();
        }
    }
}
