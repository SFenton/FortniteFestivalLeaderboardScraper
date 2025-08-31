using System.Collections.ObjectModel;
using System.Windows.Input;
using FortniteFestival.Core.Services;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using System.Timers;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class ProcessViewModel : BaseViewModel
{
    private readonly IFestivalService _service;
    private readonly AppState _state;
    private readonly Settings _settings;
    private readonly ISettingsPersistence _settingsPersistence;

    public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();
    // Exposed only for pages that still bind; avoid recomputing giant string every append; update on throttled timer
    private string _logJoined; public string LogJoined { get=>_logJoined; private set=> Set(ref _logJoined,value); }

    private readonly Queue<string> _pendingLogs = new Queue<string>();
    private readonly object _logLock = new object();
    private readonly System.Timers.Timer _logFlushTimer;

    private string _exchangeCode;
    public string ExchangeCode { get => _exchangeCode; set { Set(ref _exchangeCode,value); _state.ExchangeCode = value; UpdateFetchEnabled(); } }

    private bool _fetchEnabled; public bool FetchEnabled { get=>_fetchEnabled; private set=>Set(ref _fetchEnabled,value); }
    private double _progressPct; public double ProgressPct { get=>_progressPct; private set=>Set(ref _progressPct,value); }
    private string _progressLabel="0%"; public string ProgressLabel { get=>_progressLabel; private set=>Set(ref _progressLabel,value); }

    private string _metrics; public string Metrics { get=>_metrics; private set=>Set(ref _metrics,value); }

    public ICommand FetchCommand { get; }
    public ICommand GenerateCodeCommand { get; }

    private bool _initialized;

    public ProcessViewModel(IFestivalService service, AppState state, Settings settings, ISettingsPersistence settingsPersistence)
    {
        _service = service; _state = state; _settings = settings; _settingsPersistence = settingsPersistence;
        _exchangeCode = _state.ExchangeCode;
        FetchCommand = new Command(async ()=> await FetchAsync(), ()=> FetchEnabled);
        GenerateCodeCommand = new Command(()=> Launcher.OpenAsync(new Uri("https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code")));
        _logFlushTimer = new System.Timers.Timer(250); // 4x per second
        _logFlushTimer.Elapsed += (s,e)=> FlushPendingLogs();
        _logFlushTimer.AutoReset = true;
        _logFlushTimer.Start();
        WireServiceEvents();
    }

    private void EnqueueLog(string line)
    {
        lock(_logLock){ _pendingLogs.Enqueue(line); if(_pendingLogs.Count>2000){ // prevent unbounded growth in memory
                while(_pendingLogs.Count>1500) _pendingLogs.Dequeue();
            } }
    }
    private void FlushPendingLogs()
    {
        string[] batch=null;
        lock(_logLock){ if(_pendingLogs.Count==0) return; batch=_pendingLogs.ToArray(); _pendingLogs.Clear(); }
        MainThread.BeginInvokeOnMainThread(()=>
        {
            foreach(var l in batch) LogLines.Add(l);
            // Keep only last N lines to avoid Editor rendering cost
            const int MaxLines = 4000;
            while(LogLines.Count>MaxLines) LogLines.RemoveAt(0);
            // Only rebuild large string occasionally (every flush already throttled)
            LogJoined = string.Join(Environment.NewLine, LogLines);
            UpdateMetrics();
        });
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await InitializeAsync();
    }

    private void WireServiceEvents()
    {
        _service.Log += l => EnqueueLog(l);
        _service.SongProgress += (cur,total,title,started)=> MainThread.BeginInvokeOnMainThread(()=> OnSongProgress(cur,total,title,started));
        _service.ScoreUpdated += ld => { UpdateMetrics(); };
    }

    private async Task InitializeAsync()
    {
        if (_state.IsInitializing) return; _state.IsInitializing = true; UpdateFetchEnabled(); EnqueueLog("Initializing service (syncing songs)...");
        // Load settings
        try { var loaded = await _settingsPersistence.LoadSettingsAsync(); CopyInstrumentSettings(loaded,_settings); } catch { }
        await _service.InitializeAsync();
        _state.Songs.Clear(); foreach(var s in _service.Songs) _state.Songs.Add(s);
        EnqueueLog($"Song sync complete. {_service.ScoresIndex.Count} cached scores; {_service.Songs.Count} songs loaded.");
        _state.IsInitializing = false; UpdateFetchEnabled(); UpdateMetrics();
    }

    private void CopyInstrumentSettings(Settings src, Settings dest)
    {
        if (src==null || dest==null) return;
        dest.QueryLead = src.QueryLead; dest.QueryDrums = src.QueryDrums; dest.QueryVocals = src.QueryVocals; dest.QueryBass = src.QueryBass; dest.QueryProLead = src.QueryProLead; dest.QueryProBass = src.QueryProBass; dest.DegreeOfParallelism = src.DegreeOfParallelism;
    }

    private async Task FetchAsync()
    {
        if (_service.IsFetching || string.IsNullOrWhiteSpace(ExchangeCode)) return;
        _state.IsFetching = true; UpdateFetchEnabled(); ProgressPct = 0; ProgressLabel = "0%"; EnqueueLog("Starting score fetch...");
        var ids = _state.SelectedSongIds.Any() ? _state.SelectedSongIds : null;
        bool ok = await _service.FetchScoresAsync(ExchangeCode, _settings.DegreeOfParallelism, ids, _settings);
        EnqueueLog(ok?"Score fetch complete.":"Score fetch failed.");
        UpdateMetrics();
        _state.IsFetching = false; UpdateFetchEnabled();
    }

    private void OnSongProgress(int current,int total,string title,bool started)
    {
        if(total>0) _state.ProgressTotal = total; _state.ProgressCurrent = current;
        if(_state.ProgressTotal>0)
        {
            double pct = (double)_state.ProgressCurrent / _state.ProgressTotal * 100.0; ProgressPct = pct; ProgressLabel = $"{_state.ProgressCurrent}/{_state.ProgressTotal} ({pct:0.0}%)";
        }
        if(started) EnqueueLog($"Started: {title}"); else EnqueueLog($"Finished: {title}");
        UpdateMetrics();
    }

    private void UpdateFetchEnabled() => FetchEnabled = !string.IsNullOrWhiteSpace(ExchangeCode) && !_state.IsInitializing && !_state.IsFetching;

    private void UpdateMetrics()
    {
        var (improved,empty,errors,requests,bytes,elapsed) = _service.GetInstrumentation();
        Metrics = $"Req:{requests} Improved:{improved} Empty:{empty} Errors:{errors} Bytes:{bytes} Elapsed:{elapsed:0.0}s";
    }
}
