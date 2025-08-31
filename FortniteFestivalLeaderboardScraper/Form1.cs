using FortniteFestivalLeaderboardScraper.Helpers;
using FortniteFestivalLeaderboardScraper.Helpers.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FortniteFestivalLeaderboardScraper.UI.Views;

namespace FortniteFestivalLeaderboardScraper
{
    public enum Instruments { Lead, Drums, Vocals, Bass, ProLead, ProBass }
    public enum SortOrder { None, Title, Artist, Availability, AvailableLocally, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, Difficulty, Score, PercentageHit, FullCombo, Stars }

    public partial class Form1 : Form
    {
        private List<Song> _sparkTracks = new List<Song>();
        private readonly Dictionary<string, Song> _songCache = new Dictionary<string, Song>(); // songId -> Song (reused instances)
        private List<LeaderboardData> _allScores = new List<LeaderboardData>();
        private readonly Dictionary<string, LeaderboardData> _scoreIndex = new Dictionary<string, LeaderboardData>(); // reuse objects
        private List<Song> _visibleSongs = new List<Song>();
        private List<LeaderboardData> _visibleScores = new List<LeaderboardData>();
        private readonly List<string> _selectedSongIds = new List<string>();

        private SortOrder _songsSortOrder = SortOrder.None;
        private SortOrder _scoresSortOrder = SortOrder.None;
        private Instruments _scoreViewerInstrument = Instruments.Lead;
        private bool _songsReversed; private bool _scoresReversed; private int _songsSortColumn = -1; private int _scoresSortColumn = -1;
        private bool _fetchInProgress;
        private bool _initialSongSyncComplete;

        // Queues
        private readonly ConcurrentQueue<LeaderboardData> _pendingScoreUpdates = new ConcurrentQueue<LeaderboardData>();
        private readonly ConcurrentQueue<string> _pendingAvailabilityUpdates = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<LeaderboardData> _dbWriteQueue = new ConcurrentQueue<LeaderboardData>();

        // Timers
        private System.Windows.Forms.Timer _uiTimer; private System.Windows.Forms.Timer _logTimer; private System.Windows.Forms.Timer _dbFlushTimer;
        private readonly object _uiLock = new object();

        // Logging ring buffer
        private readonly List<string> _logLines = new List<string>(4096);
        private const int MaxLogLines = 4000; // adjust as needed
        private readonly StringBuilder _logBuffer = new StringBuilder(4096);

        private readonly string[] _starStrings = { "N/A", "⍟", "⍟⍟", "⍟⍟⍟", "⍟⍟⍟⍟", "⍟⍟⍟⍟⍟", "⍟⍟⍟⍟⍟" };
        private Font _starFont;
        private string SettingsPath => Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "FNFLS_settings.json");

        private Settings _settings = new Settings();

        public Form1()
        {
            InitializeComponent();
            SqlRepository.Initialize();
            WireViewEvents();
            InitRuntimeGridSettings();
            InitTimers();
        }

        private void WireViewEvents()
        {
            processView.GenerateCodeClicked += (s, e) => OpenExchangeCodeUrl();
            processView.FetchScoresClicked += async (s, e) => await FetchScoresAsync();
            songSelectView.SearchChanged += (s, e) => { RebuildVisibleSongs(); BuildSongsGrid(); };
            songSelectView.SelectAllClicked += (s, e) => SelectAllSongs();
            songSelectView.DeselectAllClicked += (s, e) => DeselectAllSongs();
            songSelectView.ToggleQuerySong += SongsGrid_CellContentClick;
            songSelectView.HeaderClicked += onSongColumnHeaderMouseClick;
            scoreViewerView.SearchChanged += (s, e) => RebuildScoreViewer();
            scoreViewerView.HeaderClicked += onScoreViewerColumnHeaderMouseClick;
            scoreViewerView.InstrumentChanged += (s, e) => { UpdateInstrumentSelection(); RebuildScoreViewer(); };
            mainTabControl.SelectedIndexChanged += (s, e) => { if (mainTabControl.SelectedTab == songsTab) { RebuildVisibleSongs(); BuildSongsGrid(); } else if (mainTabControl.SelectedTab == scoresTab) { RebuildScoreViewer(); } };
            dopNumeric.ValueChanged += (s, e) => OnDopChanged();
        }

        private void UpdateInstrumentSelection()
        {
            if (scoreViewerView.LeadRadio.Checked) _scoreViewerInstrument = Instruments.Lead;
            else if (scoreViewerView.VocalsRadio.Checked) _scoreViewerInstrument = Instruments.Vocals;
            else if (scoreViewerView.BassRadio.Checked) _scoreViewerInstrument = Instruments.Bass;
            else if (scoreViewerView.DrumsRadio.Checked) _scoreViewerInstrument = Instruments.Drums;
            else if (scoreViewerView.ProLeadRadio.Checked) _scoreViewerInstrument = Instruments.ProLead;
            else if (scoreViewerView.ProBassRadio.Checked) _scoreViewerInstrument = Instruments.ProBass;
        }

        private void InitRuntimeGridSettings()
        {
            EnsureSongColumns();
            EnsureScoreColumns();
            try
            {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null)
                {
                    pi.SetValue(songSelectView.SongsGrid, true, null);
                    pi.SetValue(scoreViewerView.ScoresGrid, true, null);
                }
            }
            catch { }
            _starFont = new Font("Verdana", 16, FontStyle.Bold);
            songSelectView.SongsGrid.CellFormatting += SongsGrid_CellFormatting;
            scoreViewerView.ScoresGrid.CellFormatting += ScoresGrid_CellFormatting;
        }

        private void EnsureSongColumns()
        {
            var grid = songSelectView.SongsGrid;
            if (grid.Columns.Count > 0) return;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "colQuery", HeaderText = "Query Scores", Width = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLocal", HeaderText = "Available Locally", Width = 130 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTitle", HeaderText = "Title", Width = 160 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colArtist", HeaderText = "Artist", Width = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colActiveDate", HeaderText = "Date Active", Width = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLeadDiff", HeaderText = "Lead Difficulty", Width = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colBassDiff", HeaderText = "Bass Difficulty", Width = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colVocalsDiff", HeaderText = "Vocals Difficulty", Width = 130 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDrumsDiff", HeaderText = "Drums Difficulty", Width = 130 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProLeadDiff", HeaderText = "Pro Lead Difficulty", Width = 150 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProBassDiff", HeaderText = "Pro Bass Difficulty", Width = 150 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colSongId", HeaderText = "Song ID", Width = 120 });
        }

        private void EnsureScoreColumns()
        {
            var grid = scoreViewerView.ScoresGrid;
            if (grid.Columns.Count > 0) return;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreTitle", HeaderText = "Title", Width = 160 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreArtist", HeaderText = "Artist", Width = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreFullCombo", HeaderText = "Full Combo", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreStars", HeaderText = "Stars", Width = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreValue", HeaderText = "Score", Width = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scorePercentHit", HeaderText = "Percentage Hit", Width = 130 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreSeason", HeaderText = "Season Achieved", Width = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreDifficulty", HeaderText = "Difficulty", Width = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scoreSongId", HeaderText = "Song Id", Visible = false });
        }

        private void InitTimers()
        {
            _uiTimer = new System.Windows.Forms.Timer { Interval = 120 }; _uiTimer.Tick += (s, e) => DrainUiQueues(); _uiTimer.Start();
            _logTimer = new System.Windows.Forms.Timer { Interval = 250 }; _logTimer.Tick += (s, e) => FlushLog(); _logTimer.Start();
            _dbFlushTimer = new System.Windows.Forms.Timer { Interval = 800 }; _dbFlushTimer.Tick += (s, e) => FlushDbWrites(); _dbFlushTimer.Start();
        }

        private void Log(string msg) => _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {msg}");

        private void FlushLog()
        {
            if (_logQueue.IsEmpty) return;
            int count = 0;
            while (_logQueue.TryDequeue(out var line) && count < 500)
            {
                _logLines.Add(line);
                count++;
            }
            if (_logLines.Count > MaxLogLines)
            {
                int remove = _logLines.Count - MaxLogLines;
                _logLines.RemoveRange(0, remove);
                // Rebuild full text only when trimming to limit allocations
                processView.LogTextBox.SuspendLayout();
                processView.LogTextBox.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
                processView.LogTextBox.SelectionStart = processView.LogTextBox.TextLength;
                processView.LogTextBox.ScrollToCaret();
                processView.LogTextBox.ResumeLayout();
            }
            else if (count > 0)
            {
                // Append incremental
                _logBuffer.Clear();
                for (int i = _logLines.Count - count; i < _logLines.Count; i++)
                {
                    if (i < 0) continue;
                    _logBuffer.AppendLine(_logLines[i]);
                }
                processView.LogTextBox.AppendText(_logBuffer.ToString());
            }
        }

        private async Task FetchScoresAsync()
        {
            if (_fetchInProgress) return;
            if (!_initialSongSyncComplete) { Log("Song catalog still loading; please wait..."); return; }
            _fetchInProgress = true;
            processView.GenerateCodeButton.Enabled = processView.FetchScoresButton.Enabled = false;
            var original = processView.FetchScoresButton.Text;
            processView.FetchScoresButton.Text = "Retrieving...";
            Log("Generating Fortnite bearer token...");
            var tokenGenerator = new EpicGamesExchangeTokenGenerator();
            var token = await tokenGenerator.GetTokenWithPermissions(processView.ExchangeCodeTextBox.Text);
            if (!token.Item1 || token.Item2.access_token == null)
            {
                Log("Authentication failed. Provide new exchange code.");
                FinishFetch(original);
                return;
            }

            // Only load scores from DB first run; reuse objects for subsequent fetches
            if (_allScores.Count == 0)
            {
                _allScores = LoadAllScores();
                foreach (var sc in _allScores) _scoreIndex[sc.songId] = sc; // reuse objects
                MarkSongAvailability();
                RebuildVisibleSongs();
                BuildSongsGrid();
            }

            var prioritizedTracks = _sparkTracks
                .Select((s, i) => new { s, i })
                .OrderBy(x => _scoreIndex.ContainsKey(x.s.track.su) ? 1 : 0)
                .ThenBy(x => x.i)
                .Select(x => x.s)
                .ToList();

            Log("Fetching leaderboards in parallel (prioritizing missing local scores)...");
            var scoreRetriever = new LeaderboardAPI();

            Action<LeaderboardData> perSong = ld =>
            {
                try
                {
                    // Reuse existing LeaderboardData object if present
                    if (_scoreIndex.TryGetValue(ld.songId, out var existing))
                    {
                        CopyScoreData(ld, existing);
                        ld = existing; // ensure reference enqueued is reused
                    }
                    else
                    {
                        _scoreIndex[ld.songId] = ld;
                        _allScores.Add(ld);
                    }
                    _pendingScoreUpdates.Enqueue(ld);
                    _pendingAvailabilityUpdates.Enqueue(ld.songId);
                    _dbWriteQueue.Enqueue(ld);
                }
                catch { }
            };

            var result = await scoreRetriever.GetLeaderboardsParallel(prioritizedTracks, token.Item2.access_token, token.Item2.account_id, 0, _allScores, processView.LogTextBox, _selectedSongIds, _settings.DegreeOfParallelism, perSong);
            if (!result.Item1)
            {
                Log("Fetch failed (unauthorized?).");
                FinishFetch(original);
                return;
            }
            FlushDbWrites();
            PersistScores(result.Item2); // result list contains reused objects where possible
            Log("Fetch complete.");
            FinishFetch(original);
        }

        private static void CopyScoreData(LeaderboardData source, LeaderboardData target)
        {
            target.title = source.title;
            target.artist = source.artist;
            // Copy trackers (reuse existing tracker objects if allocated)
            target.drums = CopyTracker(source.drums, target.drums);
            target.guitar = CopyTracker(source.guitar, target.guitar);
            target.bass = CopyTracker(source.bass, target.bass);
            target.vocals = CopyTracker(source.vocals, target.vocals);
            target.pro_guitar = CopyTracker(source.pro_guitar, target.pro_guitar);
            target.pro_bass = CopyTracker(source.pro_bass, target.pro_bass);
        }

        private static ScoreTracker CopyTracker(ScoreTracker src, ScoreTracker dst)
        {
            if (src == null) return dst ?? new ScoreTracker();
            if (dst == null) dst = new ScoreTracker();
            dst.initialized = src.initialized;
            dst.maxScore = src.maxScore;
            dst.difficulty = src.difficulty;
            dst.numStars = src.numStars;
            dst.isFullCombo = src.isFullCombo;
            dst.percentHit = src.percentHit;
            dst.seasonAchieved = src.seasonAchieved;
            return dst;
        }

        private void FinishFetch(string originalBtnText)
        {
            processView.FetchScoresButton.Text = originalBtnText;
            processView.GenerateCodeButton.Enabled = processView.FetchScoresButton.Enabled = true;
            _fetchInProgress = false;
            SafeOneTimeAutosize(songSelectView.SongsGrid);
            SafeOneTimeAutosize(scoreViewerView.ScoresGrid);
        }

        private void FlushDbWrites()
        {
            if (_dbWriteQueue.IsEmpty) return;
            var batch = new List<LeaderboardData>(256);
            while (_dbWriteQueue.TryDequeue(out var ld)) batch.Add(ld);
            if (batch.Count > 0) { try { SqlRepository.BulkUpsert(batch); } catch { } }
        }

        private void DrainUiQueues()
        {
            if (!Monitor.TryEnter(_uiLock)) return;
            try
            {
                bool anyAvailability = false;
                var uniqueScores = new Dictionary<string, LeaderboardData>();
                while (_pendingScoreUpdates.TryDequeue(out var ld)) uniqueScores[ld.songId] = ld;
                while (_pendingAvailabilityUpdates.TryDequeue(out var sid)) anyAvailability = true;
                if (anyAvailability)
                {
                    MarkSongAvailability();
                    foreach (var kv in uniqueScores) UpdateSongRowAvailability(kv.Key);
                }
                if (uniqueScores.Count > 0 && mainTabControl.SelectedTab == scoresTab)
                    foreach (var ld in uniqueScores.Values) UpdateOrInsertScoreViewerRow(ld);
            }
            finally { Monitor.Exit(_uiLock); }
        }

        private void MarkSongAvailability() { foreach (var s in _sparkTracks) s.isInLocalData = _scoreIndex.ContainsKey(s.track.su) ? "✔" : "❌"; }
        private void RebuildVisibleSongs()
        {
            IEnumerable<Song> query = _sparkTracks; var filter = songSelectView.SearchTextBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(filter)) { var low = filter.ToLowerInvariant(); query = query.Where(x => (x.track.tt ?? "").ToLowerInvariant().Contains(low) || (x.track.an ?? "").ToLowerInvariant().Contains(low)); }
            query = ApplySongSort(query); _visibleSongs = query.ToList();
        }
        private IEnumerable<Song> ApplySongSort(IEnumerable<Song> list)
        {
            switch (_songsSortOrder)
            {
                case SortOrder.AvailableLocally: list = list.OrderBy(x => _scoreIndex.ContainsKey(x.track.su)).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.Title: list = list.OrderBy(x => x.track.tt); break;
                case SortOrder.Artist: list = list.OrderBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.Availability: list = list.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.LeadDiff: list = list.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.BassDiff: list = list.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.VocalsDiff: list = list.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.DrumsDiff: list = list.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.ProLeadDiff: list = list.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
                case SortOrder.ProBassDiff: list = list.OrderBy(x => x.track.@in.pb).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
            }
            if (_songsReversed) list = list.Reverse(); return list;
        }
        private void BuildSongsGrid()
        {
            if (mainTabControl.SelectedTab != songsTab) return;
            var grid = songSelectView.SongsGrid;
            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var song in _visibleSongs)
            {
                int idx = grid.Rows.Add(song.isSelected, song.isInLocalData, song.track.tt, song.track.an, song._activeDate,
                    song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
                StyleSongRow(grid.Rows[idx], song);
            }
            grid.Visible = true;
            grid.ResumeLayout();
        }
        private void UpdateSongRowAvailability(string songId)
        {
            var grid = songSelectView.SongsGrid;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.Cells[11].Value?.ToString() == songId)
                {
                    var val = _scoreIndex.ContainsKey(songId) ? "✔" : "❌";
                    r.Cells[1].Value = val;
                    r.Cells[1].Style.ForeColor = val == "✔" ? Color.Green : Color.Red;
                    break;
                }
            }
        }
        private void StyleSongRow(DataGridViewRow row, Song song) { row.Cells[1].Style.ForeColor = song.isInLocalData == "✔" ? Color.Green : Color.Red; row.Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleCenter; }
        private void onSongColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex == _songsSortColumn) _songsReversed = !_songsReversed; else { _songsReversed = false; _songsSortColumn = e.ColumnIndex; }
            switch (e.ColumnIndex)
            {
                case 1: _songsSortOrder = SortOrder.AvailableLocally; break; case 2: _songsSortOrder = SortOrder.Title; break; case 3: _songsSortOrder = SortOrder.Artist; break; case 4: _songsSortOrder = SortOrder.Availability; break; case 5: _songsSortOrder = SortOrder.LeadDiff; break; case 6: _songsSortOrder = SortOrder.BassDiff; break; case 7: _songsSortOrder = SortOrder.VocalsDiff; break; case 8: _songsSortOrder = SortOrder.DrumsDiff; break; case 9: _songsSortOrder = SortOrder.ProLeadDiff; break; case 10: _songsSortOrder = SortOrder.ProBassDiff; break; default: _songsSortOrder = SortOrder.None; break;
            }
            RebuildVisibleSongs(); BuildSongsGrid();
        }

        private void RebuildScoreViewer()
        {
            if (mainTabControl.SelectedTab != scoresTab) return;
            var grid = scoreViewerView.ScoresGrid;
            grid.SuspendLayout();
            grid.Rows.Clear();
            IEnumerable<LeaderboardData> list = _allScores; var filter = scoreViewerView.SearchTextBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(filter)) { var low = filter.ToLowerInvariant(); list = list.Where(x => (x.title ?? "").ToLowerInvariant().Contains(low) || (x.artist ?? "").ToLowerInvariant().Contains(low)); }
            list = ApplyScoreSort(list); _visibleScores = list.ToList(); foreach (var ld in _visibleScores) AddScoreViewerRow(ld);
            grid.Visible = true;
            grid.ResumeLayout();
        }
        private IEnumerable<LeaderboardData> ApplyScoreSort(IEnumerable<LeaderboardData> list)
        {
            Func<LeaderboardData, ScoreTracker> sel = GetCurrentTracker;
            switch (_scoresSortOrder)
            {
                case SortOrder.Title: list = list.OrderBy(x => x.title); break;
                case SortOrder.Artist: list = list.OrderBy(x => x.artist).ThenBy(x => x.title); break;
                case SortOrder.Difficulty: list = list.OrderBy(x => sel(x).difficulty); break;
                case SortOrder.FullCombo: list = list.OrderBy(x => sel(x).isFullCombo); break;
                case SortOrder.Stars: list = list.OrderBy(x => sel(x).numStars); break;
                case SortOrder.Score: list = list.OrderBy(x => sel(x).maxScore); break;
                case SortOrder.PercentageHit: list = list.OrderBy(x => sel(x).percentHit); break;
            }
            if (_scoresReversed) list = list.Reverse(); return list;
        }
        private void AddScoreViewerRow(LeaderboardData ld)
        {
            var grid = scoreViewerView.ScoresGrid; var t = GetCurrentTracker(ld);
            var stars = t.numStars == 0 ? "N/A" : _starStrings[Math.Min(t.numStars, 6)];
            var fullCombo = t.isFullCombo ? "✔" : "❌"; var percent = (t.percentHit / 10000) + "%"; var season = t.seasonAchieved > 0 ? t.seasonAchieved.ToString() : "All-Time";
            int idx = grid.Rows.Add(ld.title, ld.artist, fullCombo, stars, t.maxScore, percent, season, t.difficulty, ld.songId);
            StyleScoreViewerRow(grid.Rows[idx], t);
        }
        private void UpdateOrInsertScoreViewerRow(LeaderboardData ld)
        {
            var grid = scoreViewerView.ScoresGrid; var filter = scoreViewerView.SearchTextBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(filter)) { var low = filter.ToLowerInvariant(); bool match = (ld.title?.ToLowerInvariant().Contains(low) ?? false) || (ld.artist?.ToLowerInvariant().Contains(low) ?? false); if (!match) { RemoveScoreViewerRow(ld.songId); return; } }
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.Cells[8].Value?.ToString() == ld.songId)
                {
                    var tExisting = GetCurrentTracker(ld); var starsExisting = tExisting.numStars == 0 ? "N/A" : _starStrings[Math.Min(tExisting.numStars, 6)];
                    r.SetValues(ld.title, ld.artist, tExisting.isFullCombo ? "✔" : "❌", starsExisting, tExisting.maxScore, (tExisting.percentHit / 10000) + "%", tExisting.seasonAchieved > 0 ? tExisting.seasonAchieved.ToString() : "All-Time", tExisting.difficulty, ld.songId);
                    StyleScoreViewerRow(r, tExisting); return;
                }
            }
            AddScoreViewerRow(ld);
        }
        private void RemoveScoreViewerRow(string songId)
        {
            var grid = scoreViewerView.ScoresGrid;
            for (int i = 0; i < grid.Rows.Count; i++) if (grid.Rows[i].Cells[8].Value?.ToString() == songId) { grid.Rows.RemoveAt(i); break; }
        }
        private void StyleScoreViewerRow(DataGridViewRow row, ScoreTracker t)
        {
            if (t.numStars == 6) row.Cells[3].Style.ForeColor = Color.Gold; else row.Cells[3].Style.ForeColor = Color.Black;
            row.Cells[3].Style.Font = t.numStars > 0 ? _starFont : scoreViewerView.ScoresGrid.DefaultCellStyle.Font;
            row.Cells[2].Style.ForeColor = t.isFullCombo ? Color.Green : Color.Red;
            row.Cells[2].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        private void onScoreViewerColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex == _scoresSortColumn) _scoresReversed = !_scoresReversed; else { _scoresReversed = false; _scoresSortColumn = e.ColumnIndex; }
            switch (e.ColumnIndex)
            {
                case 0: _scoresSortOrder = SortOrder.Title; break; case 1: _scoresSortOrder = SortOrder.Artist; break; case 2: _scoresSortOrder = SortOrder.FullCombo; break; case 3: _scoresSortOrder = SortOrder.Stars; break; case 4: _scoresSortOrder = SortOrder.Score; break; case 5: _scoresSortOrder = SortOrder.PercentageHit; break; case 7: _scoresSortOrder = SortOrder.Difficulty; break; default: _scoresSortOrder = SortOrder.None; break;
            }
            RebuildScoreViewer();
        }

        private void SongsGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e) { if (e.ColumnIndex == 1 && e.Value is string v) { e.CellStyle.ForeColor = v == "✔" ? Color.Green : Color.Red; e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; } }
        private void ScoresGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e) { if (e.ColumnIndex == 3 && e.Value is string stars && stars == _starStrings[5]) e.CellStyle.ForeColor = Color.Gold; if (e.ColumnIndex == 2 && e.Value is string fc) { e.CellStyle.ForeColor = fc == "✔" ? Color.Green : Color.Red; e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; } }

        private void SongsGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var grid = songSelectView.SongsGrid;
            var id = grid.Rows[e.RowIndex].Cells[11].Value?.ToString();
            var song = _sparkTracks.FirstOrDefault(x => x.track.su == id);
            if (song == null) return;
            song.isSelected = !song.isSelected;
            grid.Rows[e.RowIndex].Cells[0].Value = song.isSelected;
            if (song.isSelected && !_selectedSongIds.Contains(id)) _selectedSongIds.Add(id); else if (!song.isSelected) _selectedSongIds.Remove(id);
        }
        private void SelectAllSongs() { foreach (var s in _sparkTracks) { s.isSelected = true; if (!_selectedSongIds.Contains(s.track.su)) _selectedSongIds.Add(s.track.su); } BuildSongsGrid(); }
        private void DeselectAllSongs() { foreach (var s in _sparkTracks) s.isSelected = false; _selectedSongIds.Clear(); BuildSongsGrid(); }

        private void OpenExchangeCodeUrl() { try { Process.Start(new ProcessStartInfo("https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code")); } catch { } }

        private ScoreTracker GetCurrentTracker(LeaderboardData ld)
        {
            switch (_scoreViewerInstrument)
            {
                case Instruments.Vocals: return ld.vocals ?? new ScoreTracker();
                case Instruments.Bass: return ld.bass ?? new ScoreTracker();
                case Instruments.Drums: return ld.drums ?? new ScoreTracker();
                case Instruments.ProLead: return ld.pro_guitar ?? new ScoreTracker();
                case Instruments.ProBass: return ld.pro_bass ?? new ScoreTracker();
                default: return ld.guitar ?? new ScoreTracker();
            }
        }

        private void SafeOneTimeAutosize(DataGridView dgv)
        {
            try
            {
                dgv.SuspendLayout();
                dgv.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
                dgv.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
                dgv.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                dgv.ResumeLayout();
            }
            catch { }
        }

        protected void OnMainWindowLoad(object sender, EventArgs e)
        {
            _settings = LoadSettings();
            if (dopNumeric != null)
            {
                var v = _settings.DegreeOfParallelism;
                if (v < 1) v = 16; if (v > 48) v = 48; dopNumeric.Value = v;
            }
            _allScores = LoadAllScores();
            foreach (var sc in _allScores) _scoreIndex[sc.songId] = sc; // index existing objects
            processView.FetchScoresButton.Enabled = false;
            Task.Run(async () => await InitialSongSyncAsync());
        }
        private void OnDopChanged()
        {
            if (_settings == null) _settings = new Settings();
            _settings.DegreeOfParallelism = (int)dopNumeric.Value;
            SaveSettings();
        }
        private void SaveSettings() { try { File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(_settings, Formatting.Indented)); } catch { } }
        private Settings LoadSettings() { try { if (!File.Exists(SettingsPath)) return new Settings(); return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(); } catch { return new Settings(); } }

        private List<LeaderboardData> LoadAllScores() => SqlRepository.LoadAll();
        private void PersistScores(List<LeaderboardData> list) => SqlRepository.BulkUpsert(list);

        private async Task InitialSongSyncAsync()
        {
            Log("Retrieving latest song catalog (unauthenticated)...");
            try
            {
                var retriever = new SparkTrackRetriever();
                var songs = await retriever.GetSparkTracks(_allScores);
                // Update cache (reuse Song instances)
                foreach (var s in songs)
                {
                    if (_songCache.TryGetValue(s.track.su, out var existing))
                    {
                        existing.track = s.track; // replace nested track ref (simpler)
                        existing._activeDate = s._activeDate;
                        existing.lastModified = s.lastModified;
                    }
                    else
                    {
                        _songCache[s.track.su] = s;
                    }
                }
                // Remove songs no longer present
                var toRemove = _songCache.Keys.Except(songs.Select(x => x.track.su)).ToList();
                foreach (var id in toRemove) _songCache.Remove(id);
                _sparkTracks = _songCache.Values.ToList();
                SqlRepository.UpsertSongs(_sparkTracks);
                SqlRepository.DeleteSongsNotIn(_sparkTracks.Select(s => s.track.su));
                MarkSongAvailability();
                RebuildVisibleSongs();
                this.Invoke(new Action(() => { BuildSongsGrid(); }));
                Log($"Song catalog loaded. {_sparkTracks.Count} songs available.");
            }
            catch (Exception ex)
            {
                Log("Song catalog load failed: " + ex.Message);
            }
            finally
            {
                _initialSongSyncComplete = true;
                this.Invoke(new Action(() => processView.FetchScoresButton.Enabled = true));
            }
        }

        protected void OnMainWindowClosing(object sender, EventArgs e) { SaveSettings(); FlushDbWrites(); }
    }
}
