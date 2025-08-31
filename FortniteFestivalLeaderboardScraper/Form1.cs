using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using FortniteFestival.Core; // core domain models
using FortniteFestival.Core.Services; // service abstraction
using FortniteFestival.Core.Config; // use core settings
using FortniteFestival.Core.Persistence; // persistence
using FortniteFestivalLeaderboardScraper.UI.Views;

namespace FortniteFestivalLeaderboardScraper
{
    public enum Instruments { Lead, Drums, Vocals, Bass, ProLead, ProBass }
    public enum SortOrder { None, Title, Artist, Availability, AvailableLocally, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, Difficulty, Score, PercentageHit, FullCombo, Stars }

    public partial class Form1 : Form
    {
        private IFestivalService _service;
        private readonly List<Song> _visibleSongs = new List<Song>();
        private readonly List<LeaderboardData> _visibleScores = new List<LeaderboardData>();
        private readonly List<string> _selectedSongIds = new List<string>();

        // Sorting / view state
        private SortOrder _songsSortOrder = SortOrder.None;
        private SortOrder _scoresSortOrder = SortOrder.None;
        private bool _songsReversed;
        private bool _scoresReversed;
        private int _songsSortColumn = -1;
        private int _scoresSortColumn = -1;
        private Instruments _scoreViewerInstrument = Instruments.Lead;

        // Settings
        private Settings _settings = new Settings();
        private ISettingsPersistence _settingsPersistence;

        // UI helpers
        private readonly string[] _starStrings = { "N/A", "⍟", "⍟⍟", "⍟⍟⍟", "⍟⍟⍟⍟", "⍟⍟⍟⍟⍟", "⍟⍟⍟⍟⍟" };
        private Font _starFont = new Font("Verdana", 16, FontStyle.Bold);
        private CheckBox chkLead; private CheckBox chkDrums; private CheckBox chkVocals; private CheckBox chkBass; private CheckBox chkProLead; private CheckBox chkProBass;

        public Form1()
        {
            InitializeComponent();
            var dataDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var dbPath = Path.Combine(dataDir, "scores.db");
            _service = new FestivalService(new SqlitePersistence(dbPath));
            var settingsPath = Path.Combine(dataDir, "FNFLS_settings.json");
            _settingsPersistence = new JsonSettingsPersistence(settingsPath);
            WireEvents();
            InitGrids();
        }

        private void WireEvents()
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
            mainTabControl.SelectedIndexChanged += (s, e) =>
            {
                if (mainTabControl.SelectedTab == songsTab) { RebuildVisibleSongs(); BuildSongsGrid(); }
                else if (mainTabControl.SelectedTab == scoresTab) { RebuildScoreViewer(); }
            };
            dopNumeric.ValueChanged += (s, e) => OnDopChanged();

            // Service event subscriptions
            _service.Log += line => SafeAppendLog(line);
            _service.ScoreUpdated += ld => BeginInvoke(new Action(() => { UpdateOrInsertScoreViewerRow(ld); UpdateSongRowAvailability(ld.songId); }));
            _service.SongProgress += (cur,total,title,started)=> BeginInvoke(new Action(()=> OnSongProgress(cur,total,title,started)));
        }

        private void InitGrids()
        {
            EnsureSongColumns();
            EnsureScoreColumns();
            try
            {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pi != null)
                {
                    pi.SetValue(songSelectView.SongsGrid, true, null);
                    pi.SetValue(scoreViewerView.ScoresGrid, true, null);
                }
            }
            catch { }
            songSelectView.SongsGrid.CellFormatting += SongsGrid_CellFormatting;
            scoreViewerView.ScoresGrid.CellFormatting += ScoresGrid_CellFormatting;
        }

        protected void OnMainWindowLoad(object sender, EventArgs e)
        {
            _settings = LoadSettings();
            if(chkLead == null) CreateSettingsControls();
            if (dopNumeric != null)
            {
                int v = _settings.DegreeOfParallelism; if (v < 1) v = 16; if (v > 48) v = 48; dopNumeric.Value = v;
            }
            // sync checkbox states
            if(chkLead!=null){ chkLead.Checked = _settings.QueryLead; chkDrums.Checked=_settings.QueryDrums; chkVocals.Checked=_settings.QueryVocals; chkBass.Checked=_settings.QueryBass; chkProLead.Checked=_settings.QueryProLead; chkProBass.Checked=_settings.QueryProBass; }
            processView.FetchScoresButton.Enabled = false;
            SafeAppendLog("Initializing service (syncing songs)...");
            Task.Run(async () =>
            {
                await _service.InitializeAsync();
                BeginInvoke(new Action(() =>
                {
                    SafeAppendLog($"Song sync complete. {_service.ScoresIndex.Count} cached scores; {_service.Songs.Count} songs loaded.");
                    processView.FetchScoresButton.Enabled = true;
                    RebuildVisibleSongs();
                    BuildSongsGrid();
                }));
            });
        }

        private async Task FetchScoresAsync()
        {
            if (_service.IsFetching) return;
            processView.FetchScoresButton.Enabled = false;
            processView.GenerateCodeButton.Enabled = false;
            var original = processView.FetchScoresButton.Text;
            processView.FetchScoresButton.Text = "Retrieving...";
            SafeAppendLog("Starting score fetch...");
            processView.ProgressBar.Value = 0; processView.ProgressLabel.Text = "0%";
            bool ok = await _service.FetchScoresAsync(processView.ExchangeCodeTextBox.Text, _settings.DegreeOfParallelism, _selectedSongIds, _settings);
            BeginInvoke(new Action(() =>
            {
                processView.FetchScoresButton.Text = original;
                processView.FetchScoresButton.Enabled = true;
                processView.GenerateCodeButton.Enabled = true;
                if (ok) RebuildScoreViewer();
            }));
        }

        #region Logging
        private void SafeAppendLog(string line)
        {
            try
            {
                if (processView.LogTextBox.InvokeRequired)
                    processView.LogTextBox.BeginInvoke(new Action(() => AppendLine(line)));
                else AppendLine(line);
            }
            catch { }
        }
        private void AppendLine(string line)
        {
            processView.LogTextBox.AppendText(line + Environment.NewLine);
        }
        #endregion

        #region Songs Grid
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

        private void RebuildVisibleSongs()
        {
            _visibleSongs.Clear();
            IEnumerable<Song> q = _service.Songs;
            var filter = songSelectView.SearchTextBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                var low = filter.ToLowerInvariant();
                q = q.Where(x => (x.track.tt ?? "").ToLowerInvariant().Contains(low) || (x.track.an ?? "").ToLowerInvariant().Contains(low));
            }
            q = ApplySongSort(q);
            _visibleSongs.AddRange(q);
        }

        private IEnumerable<Song> ApplySongSort(IEnumerable<Song> list)
        {
            switch (_songsSortOrder)
            {
                case SortOrder.AvailableLocally: list = list.OrderBy(x => _service.ScoresIndex.ContainsKey(x.track.su)).ThenBy(x => x.track.an).ThenBy(x => x.track.tt); break;
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
            if (_songsReversed) list = list.Reverse();
            return list;
        }

        private void BuildSongsGrid()
        {
            if (mainTabControl.SelectedTab != songsTab) return;
            var grid = songSelectView.SongsGrid;
            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var s in _visibleSongs)
            {
                int idx = grid.Rows.Add(s.isSelected, _service.ScoresIndex.ContainsKey(s.track.su) ? "✔" : "❌", s.track.tt, s.track.an, s._activeDate,
                    s.track.@in.gr, s.track.@in.ba, s.track.@in.vl, s.track.@in.ds, s.track.@in.pg, s.track.@in.pb, s.track.su);
                StyleSongRow(grid.Rows[idx], s);
            }
            grid.Visible = true;
            grid.ResumeLayout();
        }

        private void StyleSongRow(DataGridViewRow row, Song s)
        {
            row.Cells[1].Style.ForeColor = _service.ScoresIndex.ContainsKey(s.track.su) ? Color.Green : Color.Red;
            row.Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }

        private void UpdateSongRowAvailability(string songId)
        {
            var grid = songSelectView.SongsGrid;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.Cells[11].Value?.ToString() == songId)
                {
                    bool has = _service.ScoresIndex.ContainsKey(songId);
                    r.Cells[1].Value = has ? "✔" : "❌";
                    r.Cells[1].Style.ForeColor = has ? Color.Green : Color.Red;
                    break;
                }
            }
        }

        private void onSongColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex == _songsSortColumn) _songsReversed = !_songsReversed; else { _songsReversed = false; _songsSortColumn = e.ColumnIndex; }
            switch (e.ColumnIndex)
            {
                case 1: _songsSortOrder = SortOrder.AvailableLocally; break;
                case 2: _songsSortOrder = SortOrder.Title; break;
                case 3: _songsSortOrder = SortOrder.Artist; break;
                case 4: _songsSortOrder = SortOrder.Availability; break;
                case 5: _songsSortOrder = SortOrder.LeadDiff; break;
                case 6: _songsSortOrder = SortOrder.BassDiff; break;
                case 7: _songsSortOrder = SortOrder.VocalsDiff; break;
                case 8: _songsSortOrder = SortOrder.DrumsDiff; break;
                case 9: _songsSortOrder = SortOrder.ProLeadDiff; break;
                case 10: _songsSortOrder = SortOrder.ProBassDiff; break;
                default: _songsSortOrder = SortOrder.None; break;
            }
            RebuildVisibleSongs();
            BuildSongsGrid();
        }
        #endregion

        #region Scores Grid
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

        private void RebuildScoreViewer()
        {
            if (mainTabControl.SelectedTab != scoresTab) return;
            var grid = scoreViewerView.ScoresGrid;
            grid.SuspendLayout();
            grid.Rows.Clear();
            IEnumerable<LeaderboardData> list = _service.ScoresIndex.Values;
            var filter = scoreViewerView.SearchTextBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                var low = filter.ToLowerInvariant();
                list = list.Where(x => (x.title ?? "").ToLowerInvariant().Contains(low) || (x.artist ?? "").ToLowerInvariant().Contains(low));
            }
            list = ApplyScoreSort(list);
            foreach (var ld in list) AddScoreViewerRow(ld);
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
            if (_scoresReversed) list = list.Reverse();
            return list;
        }

        private void AddScoreViewerRow(LeaderboardData ld)
        {
            var grid = scoreViewerView.ScoresGrid; var t = GetCurrentTracker(ld);
            var stars = t.numStars == 0 ? "N/A" : _starStrings[Math.Min(t.numStars, 6)];
            var fullCombo = t.isFullCombo ? "✔" : "❌";
            var percent = (t.percentHit / 10000) + "%";
            var season = t.seasonAchieved > 0 ? t.seasonAchieved.ToString() : "All-Time";
            int idx = grid.Rows.Add(ld.title, ld.artist, fullCombo, stars, t.maxScore, percent, season, t.difficulty, ld.songId);
            StyleScoreViewerRow(grid.Rows[idx], t);
        }

        private void UpdateOrInsertScoreViewerRow(LeaderboardData ld)
        {
            var grid = scoreViewerView.ScoresGrid;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.Cells[8].Value?.ToString() == ld.songId)
                {
                    var tExisting = GetCurrentTracker(ld);
                    var starsExisting = tExisting.numStars == 0 ? "N/A" : _starStrings[Math.Min(tExisting.numStars, 6)];
                    r.SetValues(ld.title, ld.artist, tExisting.isFullCombo ? "✔" : "❌", starsExisting, tExisting.maxScore,
                        (tExisting.percentHit / 10000) + "%", tExisting.seasonAchieved > 0 ? tExisting.seasonAchieved.ToString() : "All-Time", tExisting.difficulty, ld.songId);
                    StyleScoreViewerRow(r, tExisting);
                    return;
                }
            }
            AddScoreViewerRow(ld);
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
                case 0: _scoresSortOrder = SortOrder.Title; break;
                case 1: _scoresSortOrder = SortOrder.Artist; break;
                case 2: _scoresSortOrder = SortOrder.FullCombo; break;
                case 3: _scoresSortOrder = SortOrder.Stars; break;
                case 4: _scoresSortOrder = SortOrder.Score; break;
                case 5: _scoresSortOrder = SortOrder.PercentageHit; break;
                case 7: _scoresSortOrder = SortOrder.Difficulty; break;
                default: _scoresSortOrder = SortOrder.None; break;
            }
            RebuildScoreViewer();
        }
        #endregion

        #region Shared Handlers / Helpers
        private void SongsGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        { if (e.ColumnIndex == 1 && e.Value is string v) { e.CellStyle.ForeColor = v == "✔" ? Color.Green : Color.Red; e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; } }
        private void ScoresGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        { if (e.ColumnIndex == 3 && e.Value is string stars && stars == _starStrings[5]) e.CellStyle.ForeColor = Color.Gold; if (e.ColumnIndex == 2 && e.Value is string fc) { e.CellStyle.ForeColor = fc == "✔" ? Color.Green : Color.Red; e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; } }

        private void SongsGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var grid = songSelectView.SongsGrid;
            var id = grid.Rows[e.RowIndex].Cells[11].Value?.ToString();
            var song = _service.Songs.FirstOrDefault(x => x.track.su == id);
            if (song == null) return;
            song.isSelected = !song.isSelected;
            grid.Rows[e.RowIndex].Cells[0].Value = song.isSelected;
            if (song.isSelected && !_selectedSongIds.Contains(id)) _selectedSongIds.Add(id); else if (!song.isSelected) _selectedSongIds.Remove(id);
        }
        private void SelectAllSongs() { foreach (var s in _service.Songs) { s.isSelected = true; if (!_selectedSongIds.Contains(s.track.su)) _selectedSongIds.Add(s.track.su); } BuildSongsGrid(); }
        private void DeselectAllSongs() { foreach (var s in _service.Songs) s.isSelected = false; _selectedSongIds.Clear(); BuildSongsGrid(); }

        private void UpdateInstrumentSelection()
        {
            if (scoreViewerView.LeadRadio.Checked) _scoreViewerInstrument = Instruments.Lead;
            else if (scoreViewerView.VocalsRadio.Checked) _scoreViewerInstrument = Instruments.Vocals;
            else if (scoreViewerView.BassRadio.Checked) _scoreViewerInstrument = Instruments.Bass;
            else if (scoreViewerView.DrumsRadio.Checked) _scoreViewerInstrument = Instruments.Drums;
            else if (scoreViewerView.ProLeadRadio.Checked) _scoreViewerInstrument = Instruments.ProLead;
            else if (scoreViewerView.ProBassRadio.Checked) _scoreViewerInstrument = Instruments.ProBass;
        }

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

        private void OpenExchangeCodeUrl()
        { try { Process.Start(new ProcessStartInfo("https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code")); } catch { } }
        #endregion

        #region Settings
        private void OnDopChanged()
        { if (_settings == null) _settings = new Settings(); _settings.DegreeOfParallelism = (int)dopNumeric.Value; SaveSettings(); }
        private void SaveSettings()
        { try { _settingsPersistence.SaveSettingsAsync(_settings).Wait(); } catch { } }
        private Settings LoadSettings()
        { try { return _settingsPersistence.LoadSettingsAsync().Result; } catch { return new Settings(); } }
        protected void OnMainWindowClosing(object sender, EventArgs e) { SaveSettings(); }

        private void CreateSettingsControls()
        {
            var targetTab = optionsTab; // generated in designer
            if (targetTab == null) return;
            chkLead = new CheckBox { Text = "Lead", Left = 24, Top = 80, Checked = _settings.QueryLead, AutoSize = true };
            chkDrums = new CheckBox { Text = "Drums", Left = 100, Top = 80, Checked = _settings.QueryDrums, AutoSize = true };
            chkVocals = new CheckBox { Text = "Vocals", Left = 180, Top = 80, Checked = _settings.QueryVocals, AutoSize = true };
            chkBass = new CheckBox { Text = "Bass", Left = 260, Top = 80, Checked = _settings.QueryBass, AutoSize = true };
            chkProLead = new CheckBox { Text = "Pro Lead", Left = 320, Top = 80, Checked = _settings.QueryProLead, AutoSize = true };
            chkProBass = new CheckBox { Text = "Pro Bass", Left = 410, Top = 80, Checked = _settings.QueryProBass, AutoSize = true };
            EventHandler changed = (s,e)=> { _settings.QueryLead = chkLead.Checked; _settings.QueryDrums = chkDrums.Checked; _settings.QueryVocals = chkVocals.Checked; _settings.QueryBass = chkBass.Checked; _settings.QueryProLead = chkProLead.Checked; _settings.QueryProBass = chkProBass.Checked; SaveSettings(); };
            chkLead.CheckedChanged += changed; chkDrums.CheckedChanged += changed; chkVocals.CheckedChanged += changed; chkBass.CheckedChanged += changed; chkProLead.CheckedChanged += changed; chkProBass.CheckedChanged += changed;
            targetTab.Controls.Add(chkLead); targetTab.Controls.Add(chkDrums); targetTab.Controls.Add(chkVocals); targetTab.Controls.Add(chkBass); targetTab.Controls.Add(chkProLead); targetTab.Controls.Add(chkProBass);
        }
        #endregion

        private int _progressTotal;
        private void OnSongProgress(int current, int total, string title, bool started)
        {
            if (started) return; // update only on completion
            if (total > 0) _progressTotal = total;
            if (current > 0 && _progressTotal > 0)
            {
                processView.ProgressBar.Maximum = _progressTotal;
                processView.ProgressBar.Value = Math.Min(current, _progressTotal);
                double pct = (double)processView.ProgressBar.Value / _progressTotal * 100.0;
                processView.ProgressLabel.Text = $"{processView.ProgressBar.Value}/{_progressTotal} ({pct:0.0}%)";
            }
            if (_progressTotal > 0 && processView.ProgressBar.Value == _progressTotal)
            {
                processView.ProgressLabel.Text = $"{_progressTotal}/{_progressTotal} (100%)";
            }
        }
    }
}
